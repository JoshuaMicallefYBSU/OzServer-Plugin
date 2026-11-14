using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Drives MMI.SectorsControlled from the VSCS/AFV panel's own Transmit state - ported from
// badvectors/VatpacPlugin's Sectors.cs (Init/CheckActive), the reference for how a working plugin
// wires VSCS transmit to vatSys's own sector control. This class never touches OzServerApiClient
// directly; it writes MMI.SectorsControlled and, for a release only, asks
// OzServerOwnershipTracker to give the sector back.
//
// Claiming is left entirely downstream: a sector appearing in MMI.SectorsControlled is picked up
// by OzServerOwnershipTracker's own MMI.SectorsControlledChanged subscription (including asking
// whether to request a sub-sector that turns out to already be owned by someone else - see
// OzServerOwnershipTracker.HandleConflictAsync). That's what makes a transmit press show up in
// both the built-in vatSys Sectors window (which just displays MMI.SectorsControlled - nothing
// else needed for that one) and the OzServer window, and (as a consequence of
// MMI.SectorsControlled being correct) is also what makes vatSys's own map display an extension
// normally - no separate API call needed for that.
//
// Releasing is not symmetrical, and this is the one place that asymmetry shows. The tracker's MMI
// handler is claim-only on purpose - inferring a release from a sector's absence in MMI is what
// used to fight a VSCS transmit claim in a claim/release loop - so a sector dropping out of MMI
// releases nothing by itself. CheckActive therefore calls the tracker's release path explicitly
// when a line stops transmitting (see the end of it): dropping a frequency's line takes the
// sector's infill off the map locally, and OzServer has to stop calling it this controller's at
// the same moment, or it stays in the Sectors window and stays unavailable to everyone else.
//
// Controller Info is deliberately not touched here. This used to publish an "Extending ..."
// line from the same Transmit state; that is another plugin's job now, and two plugins editing
// the same free-text field would fight over it.
public class AfvSectorClaimer
{
    // Used for two things only: RecentlyResumed (read-only - see Init()) and the release a line
    // going quiet has to make (ReleaseIdleVscsLineAsync - see CheckActive). Everything else here
    // still only ever touches MMI.SectorsControlled and lets the tracker follow it.
    readonly OzServerOwnershipTracker _tracker;
    readonly HashSet<VSCSFrequency> _subscribed = new();
    // What Network.Me.Callsign was the last time Init() actually ran - see the
    // VSCSFrequenciesChanged handler for why this matters: that event fires both for a genuine
    // position change (the panel reloading for a new primary) and for a station merely being
    // added/removed while working the *same* position (e.g. adding another AFV line mid-session),
    // and only the former should re-baseline.
    string? _lastPrimaryCallsign;

    // Network.Connected fires the moment the session comes up, which is NOT the same moment the
    // data Init() depends on is usable: Network.Me can still be null, and DefaultSectorsFor matches
    // Network.Me.Callsign against the profile's own SectorsVolumes.Sectors. A single attempt at
    // that instant silently granted nothing whenever either side wasn't ready - Init() returned on
    // the empty result, MMI.SectorsControlled was never written, and since nothing else touches it
    // on connect there was no second chance. The symptom was logging in on a position and being
    // handed none of its airspace, having to claim your own sector group by hand.
    static readonly TimeSpan InitRetryInterval = TimeSpan.FromSeconds(1);
    const int InitMaxAttempts = 20;
    readonly System.Threading.Timer _initRetryTimer;
    int _initAttemptsLeft;

    // Putting the controller back on the frequencies they were working before a disconnect. The
    // sectors come back on their own (POST /sectors/resume), the VSCS panel does not - vatSys
    // rebuilds it Idle every session - so without this a resumed position is held on OzServer, drawn
    // on the scope, and silently off the air.
    //
    // Retried rather than done once, and for longer than Init's own retries: every input arrives at
    // its own pace after a connect. The panel's lines are rebuilt by vatSys, the network has to
    // confirm the session as a real controller before vatSys will transmit at all (Network.ValidATC,
    // the same gate its own Transmit button uses), and the sectors themselves only become this
    // controller's again when the resume lands a round trip later. A single pass at connect would
    // run before all three on most sessions.
    static readonly TimeSpan TransmitRestoreInterval = TimeSpan.FromSeconds(1);
    const int TransmitRestoreMaxAttempts = 45;
    readonly System.Threading.Timer _transmitRestoreTimer;
    // Lines still to put back, by VSCS line name. Each one is dropped the moment it is dealt with -
    // restored, or found already transmitting - so no line is ever written twice. That is what keeps
    // this from fighting the controller: switching a line off during the restore window is their
    // decision, and a loop that kept re-reading the remembered list would turn it straight back on.
    readonly HashSet<string> _transmitToRestore = new(StringComparer.OrdinalIgnoreCase);
    int _transmitRestoreAttemptsLeft;

    public AfvSectorClaimer(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;

        // Network.Connected, Audio.VSCSFrequenciesChanged and the retry timer below all end up in
        // MMI.SetControlledSectors, which touches vatSys's own map/UI state - and none of the three
        // are guaranteed to fire on the UI thread (vatSys never installs a SynchronizationContext on
        // its FSDSession, and a System.Threading.Timer callback is never the UI thread either).
        // RunOnUiThread is what the sibling trackers (PrimaryPositionWatcher, ObserverPositionMirror)
        // already do for the same reason.
        Network.Connected += (_, _) => RunOnUiThread(() =>
        {
            BeginInit();
            BeginTransmitRestore();
        });
        // Nothing to put back once the session is over, and a list left over from it must not be
        // applied to whatever position this client connects as next - the same rule
        // TagResumeRecovery applies to its own outstanding tags.
        Network.Disconnected += (_, _) => RunOnUiThread(StopTransmitRestore);
        // VSCSFrequenciesChanged re-baselines via Init() (not CheckActive() - see Init()'s own
        // comment for why) only when the primary callsign has actually changed since Init() last
        // ran, i.e. only for a genuine position change. Init() unconditionally resets
        // MMI.SectorsControlled to primary + its own direct subsectors - running that on *every*
        // list change, including a mid-session station add that has nothing to do with a position
        // change, wiped out whatever had already been extended into (the map's infill for those
        // sectors disappearing, until a later TransmitChanged happened to sweep them back in via
        // CheckActive()'s own full read of current Transmit state - which is why pressing transmit
        // on the new station appeared to "bring everything back").
        //
        // "Changed" means changed to another position, never merely unreadable. Network.Me is null
        // for stretches of a perfectly ordinary session (it lags Connected, and it is momentarily
        // null again around a reconnect), and a bare inequality counted that as a position change:
        // BeginInit then started its one-second retry loop, which sat there until Me came back and
        // promptly reset MMI.SectorsControlled to the position's bare defaults - silently taking
        // every extension off the scope, while OzServer went on recording them as this controller's
        // because nothing here releases anything. The sector then could not be dropped from its own
        // VSCS line either, since by the time the line was pressed the sector was no longer in MMI
        // for CheckActive to find (it releases that case now too - see
        // ReleaseQuietLineMissingFromMmi - but the reset itself should not be happening).
        Audio.VSCSFrequenciesChanged += (_, _) => RunOnUiThread(() =>
        {
            ResubscribeTransmitChanged();

            var callsign = Network.Me?.Callsign;
            if (!string.IsNullOrEmpty(callsign) && callsign != _lastPrimaryCallsign)
                BeginInit();
        });
        _initRetryTimer = new System.Threading.Timer(
            _ => RunOnUiThread(RetryInit), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _transmitRestoreTimer = new System.Threading.Timer(
            _ => RunOnUiThread(RetryTransmitRestore), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        ResubscribeTransmitChanged();
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }

    // Always queued, even from the UI thread - runs after whatever event is being handled right now
    // has finished with every other subscriber.
    static void DeferOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control { IsHandleCreated: true } mainForm)
            mainForm.BeginInvoke(action);
        else
            action();
    }

    void BeginInit()
    {
        _initAttemptsLeft = InitMaxAttempts;
        RetryInit();
    }

    void StopInitRetries() =>
        _initRetryTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    void RetryInit()
    {
        // Session went away while still waiting for it to become ready - nothing left to grant.
        if (!Network.IsConnected)
        {
            StopInitRetries();
            return;
        }

        // An observer is never granted a position of their own - their airspace is mirrored from
        // whoever actually holds it (ObserverPositionMirror). Read from the connection's own
        // Position/Rating, which are correct immediately; the previous test on Network.Me.IsRealATC
        // abandoned a real controller's grant outright, taking their whole position off them.
        if (NetworkIdentity.IsObserver)
        {
            StopInitRetries();
            return;
        }

        if (Init() || --_initAttemptsLeft <= 0)
        {
            StopInitRetries();
            return;
        }

        // One-shot reschedule rather than a repeating period, so a slow Init() can never overlap
        // itself.
        _initRetryTimer.Change(InitRetryInterval, System.Threading.Timeout.InfiniteTimeSpan);
    }

    // Audio.VSCSFrequencies is a fresh array snapshot on every call, and the VSCSFrequency
    // instances it hands back aren't guaranteed to be the same ones across a
    // VSCSFrequenciesChanged event (a position change tears down and rebuilds the whole list) - so
    // this diffs by reference against what's already subscribed rather than assuming it's safe to
    // subscribe once and forget.
    void ResubscribeTransmitChanged()
    {
        var current = Audio.VSCSFrequencies;

        foreach (var freq in _subscribed.Except(current).ToList())
        {
            freq.TransmitChanged -= OnTransmitChanged;
            _subscribed.Remove(freq);
        }

        foreach (var freq in current.Except(_subscribed))
        {
            freq.TransmitChanged += OnTransmitChanged;
            _subscribed.Add(freq);
        }
    }

    void OnTransmitChanged(object? sender, EventArgs e)
    {
        if (sender is VSCSFrequency frequency)
            CheckActive(frequency);

        // After CheckActive, so what is written is the state the press has actually settled into.
        // Saved on every change rather than on disconnect - see VscsTransmitMemory for why a
        // disconnect is the wrong moment to be writing a file.
        VscsTransmitMemory.Remember();
    }

    // Starts putting back the frequencies this callsign was transmitting on before it dropped.
    void BeginTransmitRestore()
    {
        StopTransmitRestore();

        if (!Network.IsConnected || NetworkIdentity.IsObserver)
            return;

        // Read once, at connect. Re-reading it on every attempt would pick up the memory this
        // session is itself writing (OnTransmitChanged saves on every change), turning a restore
        // the controller then undid into a restore that keeps coming back.
        foreach (var line in VscsTransmitMemory.Recall(Network.Me?.Callsign))
            _transmitToRestore.Add(line);

        if (_transmitToRestore.Count == 0)
            return;

        _transmitRestoreAttemptsLeft = TransmitRestoreMaxAttempts;
        RetryTransmitRestore();
    }

    void StopTransmitRestore()
    {
        _transmitToRestore.Clear();
        _transmitRestoreTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    void RetryTransmitRestore()
    {
        if (!Network.IsConnected || NetworkIdentity.IsObserver)
        {
            StopTransmitRestore();
            return;
        }

        RestoreRememberedTransmits();

        if (_transmitToRestore.Count == 0 || --_transmitRestoreAttemptsLeft <= 0)
        {
            // Whatever is left was never this controller's to take back - a sector somebody else
            // picked up while they were away, or a line the panel no longer has. Logged rather
            // than dropped silently: "I came back and one of my frequencies is missing" is worth
            // being able to answer.
            if (_transmitToRestore.Count > 0)
                ActionLog.Log("Primary",
                    $"Did not put back transmit on {string.Join(", ", _transmitToRestore.OrderBy(n => n))} - not owned here after reconnecting",
                    new
                    {
                        action = "vscs_transmit_not_restored",
                        lines = _transmitToRestore.OrderBy(n => n).ToArray(),
                        own_callsign = Network.Me?.Callsign
                    });

            StopTransmitRestore();
            return;
        }

        // One-shot reschedule rather than a repeating period, the same shape as the init retry, so
        // a slow pass can never overlap itself.
        _transmitRestoreTimer.Change(TransmitRestoreInterval, System.Threading.Timeout.InfiniteTimeSpan);
    }

    // One pass over the lines still to put back. Each is dropped from the set as soon as it is
    // settled, so nothing is written twice.
    void RestoreRememberedTransmits()
    {
        // The same gate vatSys's own Transmit button uses (VSCSControlDX's Freq_Trans clickspot):
        // until the network has confirmed this session as a controller, setting Transmit puts the
        // panel in a state AFV will not actually carry.
        if (!Network.ValidATC)
            return;

        foreach (var name in _transmitToRestore.ToList())
        {
            var frequency = Audio.VSCSFrequencies.FirstOrDefault(f => f.Name == name);

            // The panel is still being rebuilt - vatSys populates it over the first seconds of a
            // session. Keep waiting rather than writing this line off.
            if (frequency == null)
                continue;

            if (frequency.Transmit)
            {
                _transmitToRestore.Remove(name);
                continue;
            }

            var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == name);

            // Only what OzServer agrees is this controller's. A frequency is a claim on airspace,
            // and the resume has already decided which of it came back: a sector somebody else
            // picked up in the meantime is theirs now, and putting the controller on its frequency
            // would both talk over them and have CheckActive claim it back out from under them.
            // Not yet owned is not the same as not theirs, though - the resume lands a round trip
            // after connect - so this is left in the set and asked again next second.
            if (sector == null || !_tracker.IsMine(sector))
                continue;

            frequency.Transmit = true;
            _transmitToRestore.Remove(name);

            ActionLog.Log("Primary", $"Put transmit back on {sector.Name} after reconnecting", new
            {
                action = "vscs_transmit_restored",
                sector = sector.Name,
                line = name,
                own_callsign = Network.Me?.Callsign
            });
        }
    }

    // Sets the initial controlled-sector set on connect (or on a genuine position change - see the
    // VSCSFrequenciesChanged handler): the primary sector matching this session's own login
    // callsign, plus the rest of its current-session group after removing top-down cover already
    // staffed by another real controller. Based on VatpacPlugin's Sectors.Init().
    // True once the position's default sectors have actually been granted, which is what tells
    // RetryInit to stop. False means "not ready yet", never "nothing to do" - see the retry fields.
    bool Init()
    {
        // Network.Me (and everything else here - VSCS lines, sector callsigns) is only meaningful
        // once actually connected to the network; running this beforehand (e.g. a stray
        // VSCSFrequenciesChanged before login) would touch MMI.SectorsControlled with garbage.
        if (!Network.IsConnected)
            return false;

        var callsign = Network.Me?.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return false;

        // PrimaryPosition, not an open-coded copy of the same rule: PrimaryPositionWatcher applies
        // it on the *other* controller's side to decide what to release to this session, and the
        // two have to agree exactly or a sector is either released to nobody or never handed over.
        var unfiltered = PrimaryPosition.DefaultSectorsFor(callsign);
        var sectors = PrimaryPosition.WithoutStaffedTopDownCover(unfiltered, callsign);
        if (sectors.Count == 0)
            return false;

        LogWithheldTopDownCover("Withheld top-down cover on login", unfiltered, sectors);

        // Anything a resume just handed back stays. Nothing orders this against the resume, and
        // replacing MMI with the bare defaults after it had already restored the extras took them
        // back out - see OzServerOwnershipTracker.RecentlyResumed.
        foreach (var resumed in _tracker.RecentlyResumed)
        {
            if (!sectors.Any(s => s.Equals(resumed)))
                sectors.Add(resumed);
        }

        // Only recorded once the grant actually happened. Setting it on the way in marked the
        // position as handled even when nothing was granted, so the VSCSFrequenciesChanged
        // re-baseline below then saw no callsign change and never gave it a second attempt.
        _lastPrimaryCallsign = callsign;

        MMI.SetControlledSectors(sectors);
        return true;
    }

    // Recomputes MMI.SectorsControlled from ONE VSCS line's current Transmit state - the specific
    // line that just raised TransmitChanged (see OnTransmitChanged, which passes it in via sender),
    // never the whole panel. Two cases, exactly:
    //   - A bare sub-sector's own line (no SubSectors of its own, e.g. SNO) activating adds only
    //     that sector - never its primary, never siblings. There is no legitimate reading of
    //     "extend into SNO" that should also hand over WOL and WOL's other sub-sectors.
    //   - A primary's own line (e.g. WOL, or WON) activating adds it plus its *whole* group -
    //     matching real "extend" semantics (you're covering the area on your own scope/tags).
    //     OzServer ownership conflicts are still resolved downstream by
    //     OzServerOwnershipTracker.HandleConflictAsync, but direct live-ATC presence is applied here
    //     too for dataset top-down cover: if ML_APP is already online, pressing BLA Transmit must
    //     not locally re-add MAE/MAV to vatSys's Sector Management Window before the backend gets a
    //     chance to reject them.
    //
    // A line going the other way - to Recv or Idle, both of which clear Transmit (see
    // VSCSControlDX's Freq_Recv/Freq_Idle clickspots) - removes that sector and its sub-sectors
    // from MMI *and* releases it on OzServer. Dropping a line is the controller saying they are no
    // longer working that airspace, and the two records have to say the same thing: before the
    // release call at the end of this method the infill came off the map while OzServer went on
    // listing the sector as owned, so nobody else could take it and it sat in the OzServer Sectors
    // window as if it were still being worked. The one exception is this session's own primary
    // sector, which is never dropped either locally or on OzServer - see the primarySector check
    // below.
    //
    // Deliberately scoped to just this one frequency rather than resweeping every VSCS line's
    // current state (an earlier version did that): every OTHER line's Transmit still reads exactly
    // what it read before this event, including any sub-sector Init() had already granted into
    // MMI.SectorsControlled but that's never individually been transmitted on - resweeping those
    // treated "controlled but not currently transmitting" as "just turned off" on every unrelated
    // transmit press, silently stripping them back out (and, worse, once a sector drops out of
    // MMI.SectorsControlled entirely vatSys backfills it with a dummy placeholder infill that VSCS
    // can no longer reclaim by transmitting again - the actual bug this was rewritten to fix:
    // pressing Transmit on a primary like BLA was wiping BLA's own already-granted infill back to a
    // dummy and leaving it unrecoverable). The primarySector self-protection below still matters for
    // its own sake (turning the primary's own line off should never drop the primary itself), but
    // scoping to one frequency is what actually stops every OTHER granted sector from being caught
    // in the blast radius of a change that has nothing to do with them.
    // Uses Sector.Equals throughout (Callsign-based), not == or ReferenceEquals - Sector overrides
    // Equals/GetHashCode but not the == operator, so two Sector instances for the same real sector
    // reached via different lookups compare unequal under == even though they're the same sector.
    void CheckActive(VSCSFrequency frequency)
    {
        // Same reasoning as Init() above - nothing here means anything before the network
        // connection is actually up.
        if (!Network.IsConnected)
            return;

        // Network.Me lags Connected (see Init), and a Transmit press in that window dereferenced it
        // here unguarded - a NullReferenceException thrown into whatever vatSys code set Transmit.
        var myCallsign = Network.Me?.Callsign;
        if (string.IsNullOrEmpty(myCallsign))
            return;

        var primarySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == myCallsign);
        if (primarySector == null)
            return;

        var frequencySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == frequency.Name);
        if (frequencySector == null)
            return;

        // Transmitting on a sector another controller holds asks whether to request it - see
        // PromptToRequestHeldSector. Checked before anything touches MMI, and whether or not
        // something else (VatpacPlugin, on this same press) has already put it there. This
        // session's own position is left to the logon path, which requests it on its own.
        if (frequency.Transmit
            && !primarySector.Equals(frequencySector)
            && !_tracker.IsMine(frequencySector)
            && _tracker.ControlledByOthers.ContainsKey(frequencySector.Name))
        {
            PromptToRequestHeldSector(frequency, frequencySector, myCallsign!);
            return;
        }

        var currentSectors = MMI.SectorsControlled.ToList();
        var currentlyControlled = currentSectors.FirstOrDefault(s => s.Callsign == frequency.Name);

        // Set only by the drop branch below, and given back to OzServer after the MMI write - see
        // the end of this method.
        SectorsVolumes.Sector? dropped = null;

        if (currentlyControlled == null)
        {
            if (!frequency.Transmit)
            {
                ReleaseQuietLineMissingFromMmi(frequencySector, primarySector);
                return;
            }

            // A flow position never takes enroute or TMA airspace nobody holds - see
            // PrimaryPosition.MayOwn. (Airspace someone does hold was handled above: it is asked
            // for, as relief cover.) Already given to this session, by an accepted request, it goes
            // back on the scope like any other sector of ours.
            if (!PrimaryPosition.MayOwn(frequencySector, myCallsign) && !_tracker.IsMine(frequencySector))
            {
                PrimaryPosition.ReportFlowCannotAssume(new[] { frequencySector }, myCallsign);
                return;
            }

            var withheld = new HashSet<string>(
                PrimaryPosition.StaffedCoveredSectors(frequencySector, myCallsign),
                StringComparer.OrdinalIgnoreCase);

            if (!withheld.Contains(frequencySector.Name))
                currentSectors.Add(frequencySector);

            foreach (var subsector in frequencySector.SubSectors.ToList())
            {
                if (withheld.Contains(subsector.Name))
                    continue;

                if (!currentSectors.Any(s => s.Equals(subsector)))
                    currentSectors.Add(subsector);
            }

            if (withheld.Count > 0)
                ActionLog.Log("Primary",
                    $"Withheld top-down cover under {frequencySector.Name}: {string.Join(", ", withheld.OrderBy(s => s))}",
                    new
                    {
                        action = "withhold_top_down_cover_transmit",
                        sector = frequencySector.Name,
                        sectors = withheld.OrderBy(s => s).ToArray(),
                        own_callsign = myCallsign
                    });
        }
        else
        {
            if (frequency.Transmit)
                return;

            if (primarySector.Equals(currentlyControlled))
            {
                ActionLog.Log("Primary",
                    $"VSCS line for {currentlyControlled.Name} stopped transmitting - kept, it is this session's own position",
                    new
                    {
                        action = "vscs_quiet_line_kept_own_position",
                        sector = currentlyControlled.Name,
                        own_callsign = myCallsign
                    });
                return;
            }

            currentSectors.Remove(currentlyControlled);

            // The whole covered group, not just the direct sub-sectors: the release below gives back
            // everything the dropped sector is responsible for all the way down (the backend's
            // releaseGroup walks the chain, and PrimaryPosition.CoveredBy mirrors it), and 19 sectors
            // in the dataset bundle sub-sectors that bundle more of their own. One level left the
            // deeper ones in MMI with nothing owning them on OzServer, which is exactly what the
            // tracker's RetryUnclaimedMmiSectors exists to claim - so a tick landing before the
            // release's refresh took them out could claim them straight back, on their own, after
            // the controller had dropped the line that covered them.
            foreach (var subsector in PrimaryPosition.CoveredBy(currentlyControlled).Where(s => !s.Equals(currentlyControlled)))
            {
                // Never the position this session is actually logged on as, even when the group
                // being dropped is responsible for it. A controller logged on as a bare sub-sector
                // (SNO) who extended into its parent group (WOL) and then drops WOL's line is
                // dropping a group whose SubSectors include SNO itself - taking their own airspace
                // off their own scope, and (because a sector that leaves MMI.SectorsControlled
                // entirely gets backfilled with a dummy infill VSCS can no longer reclaim - see
                // this method's own header) not giving it back on the next transmit either. The
                // release below sweeps the same group up server-side, where it cannot be skipped
                // sector by sector - see OzServerOwnershipTracker's
                // ReclaimOwnPositionAfterReleaseAsync, which takes the position straight back
                // instead.
                if (primarySector.Equals(subsector))
                    continue;

                currentSectors.Remove(subsector);
            }

            dropped = currentlyControlled;
        }

        MMI.SetControlledSectors(currentSectors);

        // The sector has just come off the scope locally; this is what stops OzServer from going on
        // saying it is still this controller's. It cannot be inferred downstream from the MMI write
        // above the way a claim is: the tracker's MMI.SectorsControlledChanged handler is
        // claim-only by design (see ClaimMmiControlledSectorsAsync - releasing on MMI absence is
        // what used to fight a VSCS transmit claim in a claim/release loop), so a release has to be
        // asked for explicitly. Without it, dropping a line took the infill off the map while the
        // sector stayed listed as owned in the OzServer Sectors window and stayed unavailable to
        // every other controller - which is the bug this exists to fix.
        //
        // Deliberately after MMI.SetControlledSectors, not before: that call is what hands the
        // airspace back locally, and it is synchronous, so by the time the release's own refresh
        // comes back and ReconcileMmiWithOwned diffs the result, MMI already agrees with it and
        // there is nothing left for it to push. Released first, the refresh could land while the
        // sector was still in MMI - and a sector in MMI that OzServer no longer records as ours is
        // exactly what the claim path exists to put back (RetryUnclaimedMmiSectors), so the release
        // would be undone by the next poll tick.
        //
        // Fire-and-forget: TransmitChanged is raised from inside vatSys's own VSCS click handling
        // (VSCSControlDX.Execute), which cannot be made to wait on a round trip to OzServer.
        // Nothing is lost by not awaiting - ReleaseIdleVscsLineAsync reports its own failures and
        // re-derives Owned itself when it succeeds.
        if (dropped != null)
            _ = _tracker.ReleaseIdleVscsLineAsync(dropped);
    }

    // Raised (on the UI thread) once a request has been sent from a transmit press, with the
    // requested sector's name - Plugin opens the Sectors window on it.
    public event Action<string>? SectorRequested;

    // Sectors with a transmit prompt currently open, so pressing again does not stack a second one.
    readonly HashSet<string> _promptOpen = new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan RequestPromptTimeout = TimeSpan.FromSeconds(60);

    const string FlowReliefNote =
        "You are requesting to take ownership of airspace as FMP. This is a temporary operation for "
        + "relieving ATC during bathroom breaks etc. FMP should not control ENR/TMA Airspace for "
        + "extended periods of time.";

    // Transmit was pressed on a sector someone else holds. Pressing transmit is how a controller
    // takes airspace, but this airspace is somebody's: so rather than claiming it (which the backend
    // refuses) or silently asking for it, the controller is asked whether to request it, shown
    // everything that would come with it.
    //
    //   Accept - the request goes to the holder, and the Sectors window opens on it.
    //   Reject - (also closing the prompt, or leaving it for a minute) the line goes back to
    //            Receive, since the controller has decided not to work this airspace.
    //
    // Nothing is put on the scope either way until the holder accepts; then the sector and its tags
    // arrive the ordinary way. A flow position sees an extra note - it may only ever take airspace
    // this way, as temporary relief.
    void PromptToRequestHeldSector(VSCSFrequency frequency, SectorsVolumes.Sector sector, string myCallsign)
    {
        if (!_tracker.ControlledByOthers.TryGetValue(sector.Name, out var owner))
            return;

        // Kept out of the claim path while this is being decided, so the tracker does not also try
        // to claim it and ask its own "already owned" question about the same press.
        _tracker.HoldForTransmitPrompt(PrimaryPosition.CoveredBy(sector).Select(s => s.Name));

        // After every TransmitChanged handler has run - VatpacPlugin's included, which can add the
        // sector's whole group to MMI on this same press. None of it is ours.
        DeferOnUiThread(() => RemoveHeldFromMmi(sector, owner.Cid));

        if (_tracker.HasPendingRequestFor(sector))
        {
            ActionLog.Log("Request", $"Transmit on {sector.Name} - already requested from {owner.Callsign}",
                new { action = "transmit_held_already_requested", sector = sector.Name, owner = owner.Callsign, own_callsign = myCallsign });
            RunOnUiThread(() => SectorRequested?.Invoke(sector.Name));
            return;
        }

        if (!_promptOpen.Add(sector.Name))
            return;

        // What the holder would actually hand over: the sector and everything under it that they
        // hold themselves (the backend's transferRequest gives exactly that).
        var group = PrimaryPosition.CoveredBy(sector)
            .Where(s => s.Equals(sector)
                        || (_tracker.ControlledByOthers.TryGetValue(s.Name, out var o) && o.Cid == owner.Cid))
            .ToList();

        var message = $"{owner.Callsign} currently owns:"
                      + Environment.NewLine + Environment.NewLine
                      + string.Join(Environment.NewLine, group.Select(s => SectorDescription.Describe(s)))
                      + Environment.NewLine + Environment.NewLine
                      + $"Request {(group.Count == 1 ? "this sector" : "these sectors")} from {owner.Callsign}?";
        if (PrimaryPosition.IsFlowCallsign(myCallsign))
            message += Environment.NewLine + Environment.NewLine + FlowReliefNote;

        ActionLog.Log("Request", $"Transmit on {sector.Name}, held by {owner.Callsign} - asking whether to request it",
            new { action = "transmit_held_prompt", sector = sector.Name, sectors = group.Select(s => s.Name).ToArray(), owner = owner.Callsign, own_callsign = myCallsign });

        DeferOnUiThread(() =>
        {
            var prompt = new SectorConflictPromptWindow(message, "Request sector", "Accept", "Reject");
            var timeout = new System.Windows.Forms.Timer { Interval = (int)RequestPromptTimeout.TotalMilliseconds };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                if (!prompt.IsDisposed)
                    prompt.Close();
            };

            prompt.FormClosed += (_, _) =>
            {
                timeout.Stop();
                timeout.Dispose();
                _promptOpen.Remove(sector.Name);

                if (prompt.Answer)
                    _ = RequestFromTransmitAsync(sector);
                else
                    BackToReceive(frequency, sector);
            };

            if (System.Windows.Forms.Application.OpenForms["MainForm"] is System.Windows.Forms.Form mainForm)
                prompt.Show(mainForm);
            else
                prompt.Show();
            prompt.BringToFront();
            timeout.Start();
        });
    }

    async System.Threading.Tasks.Task RequestFromTransmitAsync(SectorsVolumes.Sector sector)
    {
        if (await _tracker.RequestHeldSectorAsync(sector))
            RunOnUiThread(() => SectorRequested?.Invoke(sector.Name));
    }

    // Declined: off Transmit, onto Receive - what vatSys's own Receive button does. Taken out of MMI
    // already (RemoveHeldFromMmi), so the TransmitChanged this raises finds nothing to drop or
    // release, and nothing of anyone else's is touched.
    void BackToReceive(VSCSFrequency frequency, SectorsVolumes.Sector sector)
    {
        _tracker.ReleaseTransmitPromptHold(PrimaryPosition.CoveredBy(sector).Select(s => s.Name));
        RemoveHeldFromMmi(sector, _tracker.ControlledByOthers.TryGetValue(sector.Name, out var owner) ? owner.Cid : (int?)null);

        ActionLog.Log("Request", $"Did not request {sector.Name} - its VSCS line is back on Receive",
            new { action = "transmit_held_declined", sector = sector.Name, own_callsign = Network.Me?.Callsign });

        if (frequency.Transmit)
            frequency.Transmit = false;
        frequency.Receive = true;
    }

    // Takes the sector, and whatever under it is not ours, back out of MMI - never this session's own
    // position or anything it owns.
    void RemoveHeldFromMmi(SectorsVolumes.Sector sector, int? ownerCid)
    {
        var own = Network.Me?.Callsign;
        var current = MMI.SectorsControlled.ToList();
        var remove = PrimaryPosition.CoveredBy(sector)
            .Where(s => s.Callsign != own && !_tracker.IsMine(s))
            .Where(s => s.Equals(sector) || ownerCid == null
                        || (_tracker.ControlledByOthers.TryGetValue(s.Name, out var o) && o.Cid == ownerCid))
            .Where(s => current.Any(c => c.Equals(s)))
            .ToList();

        if (remove.Count == 0)
            return;

        MMI.SetControlledSectors(current.Where(c => !remove.Any(r => r.Equals(c))).ToList());
    }

    // A line stopped transmitting for a sector vatSys is not listing as controlled. There is
    // nothing to take off the scope - but OzServer can still be recording the sector as this
    // controller's, and then dropping the line does nothing at all: the sector stays in the
    // OzServer Sectors window and stays unavailable to everyone else, with the one control that
    // ought to give it back now doing nothing, because the sector it would have dropped is already
    // out of MMI.
    //
    // That is not a hypothetical. MMI.SectorsControlled and OzServer ownership are separate records
    // and several paths move one without the other: a sector carried out of MMI by a *different*
    // line's drop (the group sweep below removes the dropped sector's own sub-sectors, and a
    // release server-side reaches further still, down the whole responsible-sectors chain), cover
    // removed locally when a position's own controller logs on (PrimaryPositionWatcher.
    // RemoveLocalCover, which deliberately only touches MMI), or an Owned list the Sectors window
    // committed. Keying the release off "it was in MMI a moment ago" made the fix for dropping a
    // line work only while those two records happened to agree.
    //
    // So the gesture is what counts, not the bookkeeping: the controller has taken themselves off
    // this frequency, and whatever OzServer still has under that sector's name goes back. Gated on
    // Owned here (unlike the in-MMI path, which asks the server outright - see
    // OzServerOwnershipTracker.ReleaseIdleVscsLineAsync for why it cannot afford to trust a reading
    // that trails by a round trip): without that gate every press of Idle or Recv on any line that
    // isn't controlled - monitoring a neighbour's frequency, most of all - would fire a release the
    // backend can only refuse.
    void ReleaseQuietLineMissingFromMmi(SectorsVolumes.Sector frequencySector, SectorsVolumes.Sector primarySector)
    {
        // The position this session is logged on as is never given away by going quiet on it, the
        // same rule the in-MMI path applies.
        if (frequencySector.Equals(primarySector))
            return;

        if (!_tracker.IsMine(frequencySector))
            return;

        ActionLog.Log("Primary",
            $"VSCS line for {frequencySector.Name} stopped transmitting - not in controlled sectors, but still ours on OzServer",
            new
            {
                action = "vscs_quiet_line_owned_not_controlled",
                sector = frequencySector.Name,
                own_callsign = Network.Me?.Callsign
            });

        _ = _tracker.ReleaseIdleVscsLineAsync(frequencySector);
    }

    static void LogWithheldTopDownCover(
        string lead,
        List<SectorsVolumes.Sector> unfiltered,
        List<SectorsVolumes.Sector> granted)
    {
        var withheld = unfiltered
            .Where(sector => !granted.Any(g => g.Equals(sector)))
            .Select(sector => sector.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();

        if (withheld.Count > 0)
            ActionLog.Log("Primary", $"{lead}: {string.Join(", ", withheld)}", new
            {
                action = "withhold_top_down_cover_login",
                sectors = withheld.ToArray(),
                unfiltered = unfiltered.Select(s => s.Name).ToArray(),
                granted = granted.Select(s => s.Name).ToArray(),
                own_callsign = Network.Me?.Callsign
            });
    }
}
