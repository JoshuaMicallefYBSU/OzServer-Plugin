using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Keeps who holds each tag (FDR) in vatSys in step with live geographic subsector ownership on
// OzServer - the same OzServer-drives-vatSys direction OzServerOwnershipTracker already applies to
// MMI.SectorsControlled/VSCS (see its class comment), just aimed at individual aircraft instead of
// whole sectors.
//
// Exactly two things are allowed to move a tag between controllers - deliberately, so a boundary
// aircraft's position updates alone can never flap it back and forth while ownership of its
// subsector hasn't actually changed:
//   (a) the tag being uncontrolled the moment this evaluates it (OnFdrUpdate/OnRadarTrackUpdate,
//       fired from Plugin for every FDR update vatSys hands out - plus a periodic sweep timer that
//       re-runs the exact same check for every known flight, purely as a backstop against a flight
//       that never gets another FDR/radar event to re-evaluate it on; see the constructor) - picked
//       up if it's sitting, right now, in a subsector this controller owns on OzServer. Monotonic:
//       once picked up, fdr.IsTracked flips true and this branch simply stops firing for it.
//   (b) an OzServer subsector-ownership transfer (tracker.OwnershipChanged) - hands off whatever
//       this controller was tracking that was sitting in a sector it just lost. Fires only on the
//       discrete ownership-change event OzServerOwnershipTracker raises, never on a live position
//       update by itself.
// No explicit "gained" handling is needed for (b): the controller who just gained a sector picks up
// whatever aircraft that leaves untracked on their own very next (sub-second) OnRadarTrackUpdate,
// through (a) above.
//
// A third trigger, ActivateIfEligible, sits outside that "moves a tag between controllers"
// guarantee entirely - it only ever gets a flight out of STATE_PREACTIVE, pre-emptively, once it's
// known to OzServer, airborne (a live radar/ADS-B return, not just a filed flight plan's predicted
// position), and within NearSectorThresholdNm of the boundary of a sector this controller currently
// has selected - so its data is ready and EvaluatePickup's own ownership-gated flash can fire the
// moment it actually crosses in, rather than only starting the activation dance right as (or after)
// it already has. See its own comment for the full detail on each condition. Loosely tied to
// ownership - proximity, not containment - since the whole point is to run ahead of the containment
// test. Never touches jurisdiction/tracking/ControllingSector.
public class TagOwnershipSync
{
    static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    // How close (nm) an aircraft has to be to the boundary of a sector this controller currently has
    // selected before its FDR gets pre-activated ahead of arrival - see ActivateIfEligible.
    const double NearSectorThresholdNm = 50.0;

    readonly OzServerOwnershipTracker _tracker;
    readonly FdrSync _fdrSync;
    readonly FdrActivationSync _fdrActivationSync;
    readonly System.Threading.Timer _sweepTimer;

    // Guards _trackedByMe and _pickupSuppressed below - FDP2.FDRsChanged and
    // OzServerOwnershipTracker.OwnershipChanged can both arrive off the UI thread.
    readonly object _pickupStateLock = new();
    // Every callsign this session was tracking as of the last FDRsChanged we saw for it - the only
    // way OnFdrsChanged can tell "just handed to none by me" apart from "was never picked up in
    // the first place" (see its own comment).
    readonly HashSet<string> _trackedByMe = new(StringComparer.OrdinalIgnoreCase);
    // Callsigns dropped to none, by this controller, inside a sector this controller still owns -
    // EvaluatePickup skips these rather than immediately reclaiming them on its own very next
    // sweep/FDR tick. Cleared only by a real event (see OnFdrsChanged/OnOwnershipChanged), never by
    // the ordinary re-evaluation that would otherwise undo the drop within seconds.
    readonly HashSet<string> _pickupSuppressed = new(StringComparer.OrdinalIgnoreCase);
    // Tags the backend handed back on a reconnect inside the resume window. These come back to the
    // controller directly instead of flashing for acceptance: they were this controller's moments
    // ago, and the backend has already confirmed nobody else picked them up while they were gone.
    // Entries are consumed as each tag becomes eligible, and dropped wholesale on disconnect so a
    // later session can never inherit them.
    readonly HashSet<string> _resumeAutoAccept = new(StringComparer.OrdinalIgnoreCase);

    public TagOwnershipSync(OzServerOwnershipTracker tracker, FdrSync fdrSync, FdrActivationSync fdrActivationSync)
    {
        _tracker = tracker;
        _fdrSync = fdrSync;
        _fdrActivationSync = fdrActivationSync;
        _tracker.OwnershipChanged += (_, diff) => RunOnUiThread(() => OnOwnershipChanged(diff));
        _tracker.TagsResumed += (_, callsigns) => RunOnUiThread(() => OnTagsResumed(callsigns));
        // Public and static on FDP2 itself - fires for every jurisdiction/handoff state change vatSys
        // makes to any FDR, including MMI.HandoffToNone (see OnFdrsChanged for why that one matters).
        FDP2.FDRsChanged += OnFdrsChanged;
        // Nothing to take back once this session is over - same reasoning as
        // OzServerOwnershipTracker's own Network.Disconnected handler on _pendingPrimaryClaims.
        // _pickupSuppressed in particular has to be cleared here: dropping a tag to none does not
        // touch OzServer's *sector* ownership record (see OnFdrsChanged), so OnOwnershipChanged's own
        // diff-based clearing never sees anything to clear it on. Left alone, a tag dropped to none
        // right before disconnecting stayed suppressed forever after - including across a full
        // reconnect where the flight plan comes back fresh at STATE_PREACTIVE - silently blocking
        // EvaluatePickup from ever activating it again, with nothing else prompting a retry.
        Network.Disconnected += (_, _) =>
        {
            lock (_pickupStateLock)
            {
                _pickupSuppressed.Clear();
                _trackedByMe.Clear();
                // A queued reclaim belongs to the session that asked for it. Left behind, a tag
                // never re-evaluated before the disconnect would be taken without acceptance in
                // whatever session came next.
                _resumeAutoAccept.Clear();
            }
        };

        // Backstop for trigger (a) - see the class comment. OnFdrUpdate/OnRadarTrackUpdate only run
        // EvaluatePickup when vatSys itself decides to notify plugins about that flight, and there
        // is a real window where nothing does: right after a reconnect, AfvSectorClaimer.Init()
        // resets MMI.SectorsControlled to just this position's own defaults synchronously, while
        // OzServerOwnershipTracker's own re-claim of everything else this session owned
        // (RefreshFromServerIfIdleAsync -> ReconcileMmiWithOwned) is still an async round trip in
        // flight - if a flight's only FDR/radar event for a while lands in that gap, tracker.IsMine
        // isn't true yet, the pickup is skipped, and nothing else prompts a retry for a flight that
        // isn't otherwise generating fresh events (no live radar track coupled yet, or simply
        // nothing about the FDR's own data changing again soon). Sweeping every known flight
        // periodically, independent of vatSys's own event cadence, closes that gap - each pass calls
        // the exact same idempotent EvaluatePickup the event-driven path uses, so it's a no-op for
        // anything already picked up.
        _sweepTimer = new System.Threading.Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    void Sweep()
    {
        if (!Network.IsConnected)
            return;

        foreach (var fdr in FDP2.GetFDRs)
            EvaluatePickup(fdr);
    }

    public void OnFdrUpdate(FDP2.FDR fdr) => EvaluatePickup(fdr);

    public void OnRadarTrackUpdate(RDP.RadarTrack track)
    {
        if (track.CoupledFDR != null)
            EvaluatePickup(track.CoupledFDR);
    }

    // A third, independent trigger, alongside (a) and (b) in the class comment - deliberately not
    // scoped to sector ownership the way either of those is. Pre-activates a flight before it
    // actually arrives, so its data is ready and EvaluatePickup's own ownership-gated flash can fire
    // the moment it does cross in, rather than only starting the activation dance right as (or
    // after) it already has. All three of the following have to hold:
    //   - Known to OzServer (FdrActivationSync.IsKnownToServer) - a flight plan OzServer has no FDR
    //     row for at all isn't something this session should be reaching out and activating off its
    //     own back; only flights already part of the OzServer-coordinated picture qualify.
    //   - Airborne - a live radar/ADS-B return (fdr.CoupledTrack with OnGround false), not just a
    //     filed flight plan's predicted position. fdr.GetLocation() falls back to
    //     PredictedPosition.Location whenever CoupledTrack is null (see FDP2.cs), which is exactly
    //     what let a still-on-ground aircraft near a controlled sector's boundary get activated
    //     before this was tightened to require the live track's own position instead.
    //   - Within NearSectorThresholdNm of the boundary of a sector this controller currently has
    //     selected (MMI.SectorsControlled) - loosely tied to ownership, proximity rather than
    //     containment, since the whole point is to run ahead of the containment test.
    //
    // Activation only - never jurisdiction/tracking/ControllingSector. Getting the tag out of
    // STATE_PREACTIVE is this method's whole job; EvaluatePickup's own sector-ownership-gated path,
    // right below, remains solely responsible for who actually holds and flashes in for it.
    void ActivateIfEligible(FDP2.FDR fdr)
    {
        if (fdr.State != FDP2.FDR.FDRStates.STATE_PREACTIVE || !_fdrActivationSync.IsKnownToServer(fdr.Callsign))
            return;

        var reason = ActivationReason(fdr);
        if (reason == null)
            return;

        RunOnUiThread(() =>
        {
            // Re-checked here (already checked once above) - this callback can run some time after
            // it was posted, and something else (a controller's own manual Activate, the
            // ownership-gated path below, a second update that got there first) may have already
            // moved this flight on in the meantime. FDP2.EstFDR itself is idempotent regardless, but
            // there's no reason to log a no-op as if it were a real activation.
            if (fdr.State != FDP2.FDR.FDRStates.STATE_PREACTIVE)
                return;

            MMI.EstFDR(fdr);

            if (fdr.State != FDP2.FDR.FDRStates.STATE_PREACTIVE)
                ActionLog.Log("Tag", $"Activated {fdr.Callsign} ({reason})");
        });
    }

    static string? ActivationReason(FDP2.FDR fdr)
    {
        // Live track required - see the class comment on ActivateIfEligible for why this can't fall
        // back to fdr.GetLocation()'s predicted-position case the way SectorLocator.Resolve's own
        // candidates elsewhere in this plugin are allowed to.
        if (fdr.CoupledTrack is not { OnGround: false } track)
            return null;

        var nearest = MMI.SectorsControlled.Where(s => !s.IsDummy)
            .Select(s => SectorLocator.DistanceToBoundaryNm(s, track.LatLong))
            .Where(d => d != null)
            .Select(d => d!.Value)
            .DefaultIfEmpty(double.MaxValue)
            .Min();

        return nearest <= NearSectorThresholdNm
            ? $"airborne, within {NearSectorThresholdNm:0}nm of a controlled sector boundary"
            : null;
    }

    // Trigger (a) - see the class comment. Activates and/or assumes fdr if it's physically sitting,
    // right now, in a subsector this controller currently owns on OzServer. Covers both a flight
    // plan that hasn't been activated yet and a tag nobody currently holds - the same two cases the
    // native Activate/Accept context-menu items handle by hand.
    //
    // Does not accept jurisdiction outright any more - see OfferPickup for why - so this only ever
    // gets the tag as far as activated-and-flashing-in. The controller's own Accept (or the 120s
    // HandoverIn timeout, same as any other first-handoff flash) is what finishes the job from there.
    void EvaluatePickup(FDP2.FDR fdr)
    {
        if (!Network.IsConnected || string.IsNullOrEmpty(fdr.Callsign))
            return;

        // Unconditional on sector ownership, unlike everything below it - see ActivateIfEligible's
        // own comment for why. This only ever activates; it never touches jurisdiction/tracking, so
        // it can't interfere with the ownership-gated flash-to-handover path that follows.
        ActivateIfEligible(fdr);

        // Resolved directly against the sectors this controller has actually selected right now
        // (MMI.SectorsControlled - the same "not dummy" filter every other MMI.SectorsControlled
        // read in this plugin already applies), not indirectly through OzServer's own ownership
        // tracker (tracker.OwnerOf/ClaimedSectors). The two are supposed to stay in step (see
        // OzServerOwnershipTracker.ReconcileMmiWithOwned) but the tracker only refreshes on its own
        // ~10s poll, and a flash-to-handover has to reflect what the controller is actually working
        // right now - not what OzServer's last refresh happened to say, which could still show a
        // sector this controller just released, or not yet show one just claimed.
        var mmiSector = SectorLocator.Resolve(fdr, MMI.SectorsControlled.Where(s => !s.IsDummy));
        if (mmiSector == null)
            return;

        // Cheap filter before ever touching the UI thread - re-checked for real inside
        // TryActivateAndFlashIn, since fdr.State can move between this read and the posted
        // callback actually running.
        if (fdr.IsTracked
            || fdr.State == FDP2.FDR.FDRStates.STATE_HANDOVER_FIRST
            || fdr.State == FDP2.FDR.FDRStates.STATE_HANDOVER)
            return;

        lock (_pickupStateLock)
        {
            if (_pickupSuppressed.Contains(fdr.Callsign))
                return;
        }

        RunOnUiThread(() => TryActivateAndFlashIn(fdr, mmiSector));
    }

    // Activating (if needed) and deciding whether to flash in happen together, on the UI thread, in
    // one synchronous pass - splitting them across two independently-queued actions (as this used
    // to: EstFDR posted one BeginInvoke, the eligibility check ran immediately afterward against the
    // still-stale fdr.State) meant a flight plan nobody had ever activated only got as far as
    // flashing in on some *later* sweep pass, once the queued Est call had finally caught up - if it
    // ever did (see below).
    //
    // mmiSector is already the exact MMI.SectorsControlled entry the tag resolved under (see
    // EvaluatePickup) - no separate lookup needed here to translate it, unlike before this was
    // resolved directly against MMI.SectorsControlled instead of OzServer's own ownership record.
    void TryActivateAndFlashIn(FDP2.FDR fdr, SectorsVolumes.Sector mmiSector)
    {
        if (fdr.IsTracked)
            return;

        if (!fdr.ESTed)
        {
            // Exactly what a controller clicking "Activate" does by hand (see
            // AircraftContextMenu_ItemClicked's own "Activate" -> MMI.EstFDR(fdr)) - TagOwnershipSync's
            // whole point is picking a tag up without the controller having to notice and act on it
            // themselves first, and a flight plan nobody has even activated yet is the case that most
            // needs that: left alone, it just sits at STATE_PREACTIVE forever, since nothing else here
            // (or in vatSys itself, on a fresh logon over already-filed traffic) ever activates it.
            MMI.EstFDR(fdr);

            // MMI.EstFDR's own jurisdiction assignment (FDP2.AcceptJurisdiction/TrySetDefaultJurisdiction
            // - see MMI.EstFDR's own body) resolves against vatSys's native default-jurisdiction
            // geometry, which has no idea OzServer subsectors exist: it can leave ControllingSector on
            // whichever of this controller's sectors happens to be MMI.SectorsControlled.First(), or on
            // its own geometric guess, either of which can disagree with the specific sector the tag
            // actually resolved under. Reasserted only for the flight this call itself just activated
            // - the already-activated branch below never touches ControllingSector, exactly as before
            // this change.
            fdr.ControllingSector = mmiSector;

            ActionLog.Log("Tag", $"Activated {fdr.Callsign} into {mmiSector.Name} (was never activated)");
        }

        // Still not eligible even after activating - e.g. no route to establish against - or already
        // tracked by the time EstFDR's own side effects landed. !fdr.ESTed (state <= STATE_PREACTIVE)
        // rather than "<= STATE_COORDINATED": FDP2.EstFDR's own body only ever bumps a freshly
        // activated flight to exactly STATE_COORDINATED and no further (nothing else here bumps it
        // past that) - a "<=" check against that same value rejected the flight this call had just
        // successfully activated, every time, permanently. The already-activated branch above never
        // hit this, because whatever put it past STATE_PREACTIVE before this ever ran (FDRDeparted's
        // own jurisdiction assignment, etc.) had already carried it past STATE_COORDINATED too.
        if (fdr.IsTracked || !fdr.ESTed)
            return;

        // Already flashing in from a previous pass - nothing more to do until the controller (or the
        // flash's own timeout) resolves it.
        if (fdr.State == FDP2.FDR.FDRStates.STATE_HANDOVER_FIRST || fdr.State == FDP2.FDR.FDRStates.STATE_HANDOVER)
            return;

        // Dropped to none by this controller, inside a sector they still own - see OnFdrsChanged.
        // Left suppressed until a real event says otherwise, rather than flashed straight back in.
        // Re-checked here (already checked once in EvaluatePickup) since this callback can run some
        // time after it was posted.
        lock (_pickupStateLock)
        {
            if (_pickupSuppressed.Contains(fdr.Callsign))
                return;
        }

        // A tag restored by the backend on a reconnect comes straight back rather than flashing.
        // Everything above still applies - it is still activated if it needed activating, still
        // checked against suppression, and still skipped entirely if anyone is tracking it - so this
        // only changes whether the controller has to accept something that was already theirs.
        bool restoredOnReconnect;
        lock (_pickupStateLock)
            restoredOnReconnect = _resumeAutoAccept.Remove(fdr.Callsign);

        if (restoredOnReconnect)
            ReclaimAfterReconnect(fdr, mmiSector);
        else
            OfferPickup(fdr, mmiSector);
    }

    void OnTagsResumed(IReadOnlyList<string> callsigns)
    {
        lock (_pickupStateLock)
        {
            foreach (var callsign in callsigns)
            {
                // Suppression is cleared on disconnect (see the Network.Disconnected handler), so
                // this normally has nothing to skip. It matters only for a tag dropped to none in
                // the narrow window between reconnecting and the resume landing - that drop is a
                // decision this session made, and reclaiming over it would undo it.
                if (_pickupSuppressed.Contains(callsign))
                    continue;

                _resumeAutoAccept.Add(callsign);
            }
        }

        // Re-evaluated immediately rather than left to the next sweep, so the tags are back by the
        // time the controller has finished reconnecting. Each is still put through the full
        // eligibility path - this only queues them.
        foreach (var fdr in FDP2.GetFDRs)
            EvaluatePickup(fdr);
    }

    // Same as OfferPickup but accepted straight away, mirroring vatSys's own FDP2.FDRDeparted -
    // which calls HandoffFirst and then immediately accepts. The handoff is what actually assigns
    // jurisdiction, so it still has to happen; all that is skipped is the waiting.
    static void ReclaimAfterReconnect(FDP2.FDR fdr, SectorsVolumes.Sector mmiSector)
    {
        FDP2.HandoffFirst(fdr);
        fdr.HandoffSector = mmiSector;

        MMI.AcceptJurisdiction(fdr);

        // Reasserted after the accept for the same reason OfferPickup overwrites HandoffSector:
        // vatSys resolves jurisdiction against its own default geometry, which knows nothing about
        // OzServer subsectors and can land on a different sector of this controller's than the one
        // the tag actually resolved under.
        fdr.ControllingSector = mmiSector;

        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);

        ActionLog.Log("Tag", $"Reclaimed {fdr.Callsign} into {mmiSector.Name} after reconnect");
    }

    // Flashes fdr in as an incoming handover rather than silently assuming jurisdiction - the
    // controller has to notice and accept it themselves, the same as a tag actually handed to them
    // by another controller, instead of it just becoming theirs with nothing to see. Mirrors
    // vatSys's own FDP2.FDRDeparted, the one other place a flight plan becomes eligible in a
    // controller's own sector with nobody handing it over - it calls FDP2.HandoffFirst for exactly
    // this reason, just followed immediately by an accept there. HandoffFirst's own 120s timeout
    // (see FDP2.cs) reverts fdr to STATE_UNCONTROLLED if the controller never acts, and the next
    // sweep/FDR tick re-offers it from scratch.
    //
    // HandoffFirst sets HandoffSector to fdr.ControllingSector, which is not reliably the same
    // sector this controller owns on OzServer (see EvaluatePickup) - overwritten here with the one
    // already confirmed to resolve under MMI.SectorsControlled, then the track is repainted
    // directly since HandoffFirst itself does not (see its own body - FDRDeparted only gets away
    // with that because its own immediate accept repaints a moment later).
    static void OfferPickup(FDP2.FDR fdr, SectorsVolumes.Sector mmiSector)
    {
        FDP2.HandoffFirst(fdr);
        fdr.HandoffSector = mmiSector;

        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);

        ActionLog.Log("Tag", $"Flashed {fdr.Callsign} in for pickup into {mmiSector.Name}");
    }

    // Trigger (b) - see the class comment. Every flight this controller is tracking that was
    // physically sitting in a sector OzServer just took away is handed off to whoever it went to.
    // MMI.HandoffJurisdiction(fdr, to) itself decides whether that means a real network handoff (the
    // new owner is online under that exact subsector callsign) or just dropping the track (a
    // covering primary claimed it instead, or nobody is online under that callsign at all) - see its
    // own comment. Either way, the new owner's own Trigger (a) mops up the result.
    void OnOwnershipChanged(SectorOwnershipDiff diff)
    {
        if (!Network.IsConnected)
            return;

        foreach (var lost in diff.Lost)
        {
            foreach (var fdr in FDP2.GetFDRs.Where(f => f.IsTrackedByMe))
            {
                if (ResolveSector(fdr)?.Equals(lost) == true)
                {
                    MMI.HandoffJurisdiction(fdr, lost);
                    ActionLog.Log("Tag", $"Handed off {fdr.Callsign} - lost {lost.Name}");
                }
            }
        }

        // A real ownership move on a sector - gained or lost, by anyone - retires any pickup
        // suppression parked against it: whatever justified holding off no longer describes the
        // sector's current state, so the next tick is free to make a fresh decision. Suppression
        // only ever meant "nothing has actually changed since the drop"; this is a change.
        var changed = diff.Gained.Concat(diff.Lost).ToList();
        if (changed.Count == 0)
            return;

        lock (_pickupStateLock)
        {
            _pickupSuppressed.RemoveWhere(callsign =>
            {
                var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == callsign);
                var sector = fdr != null ? ResolveSector(fdr) : null;
                return sector != null && changed.Any(s => s.Equals(sector));
            });
        }
    }

    // Watches every FDR mutation for exactly one transition: this controller was tracking fdr, and
    // now isn't, with nobody else picking it up either (fdr.IsTracked false) - the shape a
    // MMI.HandoffToNone from the "handoff" window's None button leaves behind, and not anything
    // else that can drop IsTrackedByMe (a real handoff hands it to Network.Instance's own tracking
    // model, leaving IsTracked true; TagOwnershipSync's own hand-off-on-loss above only ever runs
    // against a sector already confirmed lost, not this one). Recorded per callsign rather than
    // read off fdr directly at the point of acting on it, because by then the "was mine a moment
    // ago" fact is already gone - ControllerTracking has already been cleared by the same call that
    // raised this event.
    //
    // A genuine drop-to-none does two separate things, not one: OzServer's own record of who holds
    // the tag is cleared unconditionally (see FdrSync.ClearControllingAuthority - that one matters
    // everywhere, not just the controller's own sector, since it is what stops OzServer showing a
    // controller who has actually let go as still holding it), while pickup suppression only ever
    // applies inside a sector this controller still owns (see EvaluatePickup's own comment) -
    // dropped somewhere this controller does not own, whoever does own it is free to pick it straight
    // back up.
    void OnFdrsChanged(object? sender, FDP2.FDRsChangedEventArgs e)
    {
        var fdr = e.Change;
        if (fdr == null || string.IsNullOrEmpty(fdr.Callsign))
            return;

        if (fdr.IsTrackedByMe)
        {
            lock (_pickupStateLock)
            {
                _trackedByMe.Add(fdr.Callsign);
                // Picked back up (by this controller) - a later drop deserves a fresh decision.
                _pickupSuppressed.Remove(fdr.Callsign);
            }

            return;
        }

        bool wasMine;
        lock (_pickupStateLock)
        {
            wasMine = _trackedByMe.Remove(fdr.Callsign);
        }

        if (fdr.IsTracked)
        {
            // Handed to a specific controller rather than to none - not this controller's decision
            // to suppress, and nothing to suppress regardless: it is tracked, so EvaluatePickup
            // already leaves it alone. Cleared anyway so a *later* drop-to-none starts fresh.
            lock (_pickupStateLock)
                _pickupSuppressed.Remove(fdr.Callsign);
            return;
        }

        // Untracked for some other reason entirely - a flight plan that was never picked up in the
        // first place, someone else's drop, a disconnect - is not this controller choosing to let
        // go of a tag, so there is nothing here to do.
        if (!wasMine)
            return;

        // Unconditional on sector ownership - see the class comment above. OzServer's record should
        // never go on claiming this controller still holds a tag they have just, deliberately, let
        // go of, wherever it happened to be.
        _fdrSync.ClearControllingAuthority(fdr.Callsign);

        var sector = ResolveSector(fdr);
        if (sector == null || !_tracker.IsMine(sector))
            return;

        lock (_pickupStateLock)
            _pickupSuppressed.Add(fdr.Callsign);
    }

    // The live subsector fdr is physically in, resolved only against sectors OzServer currently has
    // an active ownership record for (tracker.ClaimedSectors - Owned plus everyone else's Controlled)
    // rather than the whole SectorsVolumes.Sectors list, which would happily resolve plenty of
    // sectors nobody on OzServer has any opinion about at all - a sector with no active record
    // isn't a candidate to pick a tag up into. See SectorLocator for the geometry test itself.
    SectorsVolumes.Sector? ResolveSector(FDP2.FDR fdr) => SectorLocator.Resolve(fdr, _tracker.ClaimedSectors);

    // Same fire-and-forget marshaling OzServerOwnershipTracker/PrimaryPositionWatcher use: both
    // OwnershipChanged and the FDR/radar update events can arrive off the UI thread, and every call
    // here ends up touching live vatSys tracking/jurisdiction state.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
