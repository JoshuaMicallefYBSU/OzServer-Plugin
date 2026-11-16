using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Moves aircraft with their sector. When a sector changes hands on OzServer, the tags inside it go
// to whoever now owns it, and arrive already under their jurisdiction rather than flashing for
// acceptance.
//
// Both halves live here because they are two ends of one exchange and have to agree:
//
//   Giving side  - lost a sector to someone. Each aircraft it tracks inside that sector is handed
//                  to the new owner's sector with MMI.HandoffJurisdiction, vatSys's own handoff -
//                  once the new owner is visible in this client's Network.OnlineATC (see
//                  "Deferred handoffs" below).
//
//   Taking side  - gained a sector from someone. Accepts the handoffs that produces silently.
//
// Only aircraft the losing controller tracks move. Uncontrolled traffic in the sector stays
// uncontrolled, and a tag a third controller is working is not touched.
//
// Telling a transfer from an ordinary handoff
//
// A handoff a controller makes by hand still flashes. An incoming handoff is accepted silently
// only when all three hold:
//
//   1. the aircraft is inside a sector this session has just gained on OzServer,
//   2. it comes from the controller who held that sector (see HandoffFrom),
//   3. within TransferWindow of gaining it.
//
// The window is needed because the two clients act on their own poll ticks, so the handoff can
// arrive either side of this client noticing the ownership change.
//
// What a received handoff looks like
//
// Network.VATSIM_HandoffReceived ends in FDP2.Handoff, which leaves the FDR like this:
//
//   State              STATE_HANDOVER (9), not STATE_HANDOVER_FIRST (7). Seven is written only
//                      by FDP2.HandoffFirst, which vatSys reaches from FDP2.FDRDeparted: a local
//                      departure being offered, not a handoff from a controller.
//   HandoffController  null. MMI.HandoffJurisdiction sets it on the sending client only.
//   ControllerTracking the sending controller, resolved from the PDU's From field.
//   ControllingSector  the sender's sector, also from the PDU's From field.
//   HandoffSector      this session's sector, from the PDU's To field.
//
// MMI.AcceptJurisdiction handles that state: on an FDR that is tracked by someone else and has a
// HandoffSector, it sends the PDUHandoffAccept the sender is waiting on and then sets
// ControllingSector. TagResumeRecovery's HandoffFirst/Process sequence must not be used here: it
// lifts an untracked FDR over AcceptJurisdiction's "State > 5" precondition, and on a received
// handoff it would overwrite state 9, repoint HandoffSector and skip the acceptance PDU.
//
// Deferred handoffs
//
// MMI.HandoffJurisdiction(FDR, Sector) sends a handoff PDU only when the target sector's Callsign
// is in Network.OnlineATC. Otherwise it calls Network.StopTracking(fdr) - broadcasting a drop -
// followed by a local-only FDP2.AcceptJurisdiction, which leaves the aircraft STATE_UNCONTROLLED:
// released to nobody rather than handed to the new owner. OnlineATC is filled from a periodic
// ATCPosition broadcast, so it lags a new connection by a few seconds, and the previous holder's
// client can see the ownership change before it sees the new controller.
//
// So GiveAway checks IsOnline before calling it. When the target is not visible yet, the aircraft
// stays tracked by this session, its ControllingSector is still moved clear of
// MMI.SetControlledSectors's cleanup (see GiveAway), and RetryDeferredHandoffs sends the handoff
// once the new controller appears.
public class SectorTagHandoff
{
    // How long after gaining a sector an incoming handoff for it counts as part of that transfer.
    // The losing client acts only when its own poll notices the change, and the two clients are
    // not synchronised. Too short and the tag flashes instead of landing; too long and a manual
    // handoff from the same controller is accepted silently.
    static readonly TimeSpan TransferWindow = TimeSpan.FromSeconds(60);

    readonly OzServerOwnershipTracker _tracker;
    readonly FdrSync _fdrSync;
    readonly OzServerApiClient _api = new();
    readonly object _lock = new();

    // Sectors just taken from someone, and who from.
    readonly List<PendingTransfer> _incoming = new();

    // Flashing tags already reported as unmatched, so the diagnostic is logged once per handoff
    // rather than on every FDR update while it flashes. Cleared when the tag stops flashing, so a
    // later handoff of the same aircraft is reported again.
    readonly HashSet<string> _reportedUnmatched = new(StringComparer.OrdinalIgnoreCase);

    // Callsigns with an accept in flight. vatSys dispatches OnFDRUpdate through Task.Run - one task
    // per update, no serialisation - so several pool threads can be inside TryAcceptTransfer for
    // the same aircraft at once, all matching the same PendingTransfer.
    readonly HashSet<string> _accepting = new(StringComparer.OrdinalIgnoreCase);

    // How long after a silent accept Jurisdiction is reasserted, and how often - see
    // ReassertConfirmations.
    static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ConfirmationInterval = TimeSpan.FromSeconds(1);

    // Callsigns AcceptTransfer has accepted, with the sector jurisdiction should stay on, for as
    // long as ReassertConfirmations keeps checking them.
    readonly Dictionary<string, PendingConfirmation> _confirming = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Threading.Timer _confirmTimer;

    // How long GiveAway waits for a new sector owner to appear in Network.OnlineATC before giving
    // up on the handoff - see "Deferred handoffs" in the class comment. The owner had to be
    // connected to claim the sector, so this only has to cover OnlineATC's lag behind a connection.
    static readonly TimeSpan DeferredHandoffWindow = TimeSpan.FromSeconds(60);
    static readonly TimeSpan DeferredHandoffCheckInterval = TimeSpan.FromSeconds(2);

    // Callsigns GiveAway reassigned locally but could not hand off yet, and the sector each is
    // waiting to be handed to. Checked by RetryDeferredHandoffs until the new owner appears in
    // Network.OnlineATC or the window expires.
    readonly Dictionary<string, DeferredHandoff> _deferred = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Threading.Timer _deferredHandoffTimer;

    sealed class PendingTransfer
    {
        public PendingTransfer(SectorsVolumes.Sector sector, string fromCallsign)
        {
            Sector = sector;
            FromCallsign = fromCallsign;
            Until = DateTime.UtcNow + TransferWindow;
        }

        public SectorsVolumes.Sector Sector { get; }
        public string FromCallsign { get; }
        public DateTime Until { get; private set; }

        public void RefreshUntil() => Until = DateTime.UtcNow + TransferWindow;
    }

    sealed class PendingConfirmation
    {
        public PendingConfirmation(SectorsVolumes.Sector sector)
        {
            Sector = sector;
            Until = DateTime.UtcNow + ConfirmationWindow;
        }

        public SectorsVolumes.Sector Sector { get; }
        public DateTime Until { get; }
    }

    sealed class DeferredHandoff
    {
        public DeferredHandoff(SectorsVolumes.Sector toSector)
        {
            ToSector = toSector;
            Until = DateTime.UtcNow + DeferredHandoffWindow;
        }

        public SectorsVolumes.Sector ToSector { get; }
        public DateTime Until { get; }
    }

    public SectorTagHandoff(OzServerOwnershipTracker tracker, FdrSync fdrSync)
    {
        _tracker = tracker;
        _fdrSync = fdrSync;
        _tracker.OwnershipChanged += (_, diff) => RunOnUiThread(() => OnOwnershipChanged(diff));
        _confirmTimer = new System.Threading.Timer(_ => RunOnUiThread(ReassertConfirmations), null, ConfirmationInterval, ConfirmationInterval);
        _deferredHandoffTimer = new System.Threading.Timer(
            _ => RunOnUiThread(() =>
            {
                ProtectTagsFromArrivals();
                RetryDeferredHandoffs();
            }), null, DeferredHandoffCheckInterval, DeferredHandoffCheckInterval);
        // Deliberately not marshalled onto the UI thread - see ProtectTagsFromArrivals for the race
        // this has to win.
        Network.OnlineATCChanged += (_, _) => ProtectTagsFromArrivals();
        Network.Disconnected += (_, _) =>
        {
            lock (_lock)
            {
                _incoming.Clear();
                _reportedUnmatched.Clear();
                _accepting.Clear();
                _confirming.Clear();
                _deferred.Clear();
            }
        };
    }

    // vatSys recomputes track.State on every later FDR and radar callback, from whatever
    // fdr.ControllingSector and MMI.SectorsControlled are at that instant. AcceptTransfer checks
    // that fdr.State reached STATE_CONTROLLED, but the track can still settle on Announced (or
    // another demoted state) moments later, inside vatSys. So one SetTrackState call at accept
    // time is not durable: for ConfirmationWindow afterwards, a track that has left Jurisdiction
    // is put back. PendingSectorGhosts reasserts its preview state for the same reason.
    void ReassertConfirmations()
    {
        if (OzServerMode.Disabled)
            return;

        List<KeyValuePair<string, PendingConfirmation>> toCheck;
        lock (_lock)
        {
            var expired = _confirming.Where(kv => kv.Value.Until < DateTime.UtcNow).Select(kv => kv.Key).ToList();
            foreach (var callsign in expired)
                _confirming.Remove(callsign);

            if (_confirming.Count == 0)
                return;

            toCheck = _confirming.ToList();
        }

        foreach (var entry in toCheck)
        {
            var callsign = entry.Key;
            var confirmation = entry.Value;
            var fdr = FDP2.GetFDRs.FirstOrDefault(f => string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase));

            // No longer tracked by us - handed off again, dropped, or gone - so there is nothing
            // left to assert.
            if (fdr == null || !fdr.IsTrackedByMe)
            {
                lock (_lock)
                    _confirming.Remove(callsign);
                continue;
            }

            var track = MMI.FindTrack(fdr);
            if (track == null || track.State == MMI.HMIStates.Jurisdiction)
                continue;

            var wasState = track.State;
            fdr.ControllingSector = confirmation.Sector;
            MMI.SetTrackState(track);

            ActionLog.Log("Tag", $"Reasserted jurisdiction on {callsign}, which had settled on {wasState} instead", new
            {
                action = "handoff_reassert",
                fdr_callsign = callsign,
                sector = confirmation.Sector.Name,
                was_state = wasState.ToString(),
                now_state = track.State.ToString()
            });
        }
    }

    // This session's own login sector, as it sits in MMI.SectorsControlled. It is the one controlled
    // sector nothing takes out of the list: MMI.SetControlledSectors re-inserts it whenever a
    // caller leaves it off, and vatSys's own arrival removal keeps it. A tag whose ControllingSector
    // is this one therefore cannot be dropped by a sector leaving the list.
    static SectorsVolumes.Sector? OwnAnchor()
    {
        var callsign = Network.Me?.Callsign;
        return string.IsNullOrEmpty(callsign)
            ? null
            : MMI.SectorsControlled.ToList().FirstOrDefault(s => string.Equals(s.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
    }

    // Keeps this controller's aircraft through another controller logging on to a sector it holds.
    //
    // When a controller is confirmed ValidATC (Network.VATSIM_ClientQueryResponseReceived) and
    // their callsign names a sector in this session's controlled list, vatSys takes that sector and
    // its sub-sectors out of the list - and MMI.SetControlledSectors, in doing so, calls
    // HandoffToNone (StopTracking) on every tag this session tracks whose ControllingSector has
    // left. The aircraft are dropped to nobody, with no handoff. OzServer still has this controller
    // owning the airspace and puts the sectors back, but the tracking is gone.
    //
    // vatSys cannot be stopped from removing the sectors, but the removal can be made to miss the
    // aircraft: a tag pointed at this session's own login sector (OwnAnchor) is not in the set that
    // leaves, so it stays tracked and under this controller's jurisdiction until it is handed over
    // properly.
    //
    // Runs synchronously on whatever thread raised OnlineATCChanged rather than on the UI thread,
    // because of the timing: AddOnlineATC sends the IsValidATC query for a new controller and then
    // raises OnlineATCChanged, on the network thread, and the answer that triggers the removal
    // needs a server round trip and arrives on that same thread. Handled inline, this always runs
    // first; queued with BeginInvoke, it could run after the tags were dropped.
    //
    // Writes only FDR.ControllingSector, which vatSys itself writes from network threads, and only
    // for tags at risk.
    void ProtectTagsFromArrivals()
    {
        if (OzServerMode.Disabled)
            return;

        try
        {
            if (!Network.IsConnected)
                return;

            var anchor = OwnAnchor();
            if (anchor == null)
                return;

            var others = new HashSet<string>(
                (Network.GetOnlineATCs ?? new List<NetworkATC>())
                    .Select(atc => atc.Callsign)
                    .Where(callsign => !string.IsNullOrEmpty(callsign)
                                       && !string.Equals(callsign, anchor.Callsign, StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            if (others.Count == 0)
                return;

            // The set vatSys's removal takes: the sector named by an online controller's callsign,
            // plus its direct sub-sectors.
            var atRisk = MMI.SectorsControlled.ToList()
                .Where(s => !s.IsDummy && others.Contains(s.Callsign))
                .SelectMany(s => new[] { s }.Concat(s.SubSectors))
                .Where(s => !s.Equals(anchor))
                .ToList();

            if (atRisk.Count == 0)
                return;

            var anchored = new List<string>();

            foreach (var fdr in FDP2.GetFDRs.ToList())
            {
                if (string.IsNullOrEmpty(fdr.Callsign) || !fdr.IsTrackedByMe || fdr.ControllingSector == null)
                    continue;

                if (!atRisk.Any(s => s.Equals(fdr.ControllingSector)))
                    continue;

                var from = fdr.ControllingSector.Name;
                fdr.ControllingSector = anchor;
                anchored.Add($"{fdr.Callsign} ({from})");

                // ReassertConfirmations would otherwise point it back at the sector that is about
                // to be removed.
                lock (_lock)
                    _confirming.Remove(fdr.Callsign);
            }

            if (anchored.Count > 0)
                ActionLog.Log("Tag",
                    $"Kept {string.Join(", ", anchored)} under {anchor.Name} - its sector belongs to a controller who is online, "
                    + "and vatSys drops tags when it takes those sectors off this scope",
                    new
                    {
                        action = "tag_anchored_own_sector",
                        fdr_callsigns = anchored.ToArray(),
                        anchor = anchor.Name,
                        at_risk = atRisk.Select(s => s.Name).Distinct().ToArray()
                    });
        }
        catch (Exception ex)
        {
            // Runs inside vatSys's own network event, so nothing may escape. The worst case of
            // skipping it is the drop it exists to prevent.
            ActionLog.Log("Tag", $"Couldn't protect tags from a controller logging on: {ex.Message}");
        }
    }

    // Called for every FDR update, so an incoming transfer is accepted as soon as it lands.
    public void OnFdrUpdate(FDP2.FDR fdr) => TryAcceptTransfer(fdr);

    void OnOwnershipChanged(SectorOwnershipDiff diff)
    {
        if (OzServerMode.Disabled)
            return;

        // One handoff per aircraft for the whole change, however many of the lost sectors it is in:
        // stacked sectors share their ground footprint, so an aircraft is inside every one of them.
        // The receiving client accepts the first handoff as the transfer; a further one reads as an
        // ordinary handoff and leaves the tag pending again, while this client already counts it
        // as accepted - and nobody is tracking the aircraft.
        var handedOver = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // TMA sectors first: an aircraft inside one is also inside the enroute sector stacked over
        // it, and must be kept for a manual handoff (KeepForManualHandoff) before that sector's
        // pass would hand it over.
        foreach (var transfer in diff.Lost.OrderBy(t => PrimaryPosition.IsTmaSector(t.Sector) ? 0 : 1))
            GiveAway(transfer, handedOver);

        foreach (var transfer in diff.Gained)
        {
            if (transfer.Counterparty?.Callsign is not { Length: > 0 } from)
                continue;

            // A TMA sector's aircraft are handed over by hand (see PrimaryPosition.IsTmaSector), so
            // nothing is expected with it: a handoff that arrives flashes, to be accepted like any
            // other.
            if (PrimaryPosition.IsTmaSector(transfer.Sector))
            {
                ActionLog.Log("Tag", $"Gained {transfer.Sector.Name} from {from} - TMA sector, its tags are handed over by hand", new
                {
                    action = "sector_gained_tma_manual",
                    sector = transfer.Sector.Name,
                    from_callsign = from
                });
                continue;
            }

            lock (_lock)
                AddOrRefreshIncoming(transfer.Sector, from);

            ActionLog.Log("Tag", $"Gained {transfer.Sector.Name} from {from} - expecting its tags", new
            {
                action = "sector_gained_expect_tags",
                sector = transfer.Sector.Name,
                from_callsign = from,
                pending_transfers = PendingTransferContext()
            });

            // Anything already flashing arrived before this client noticed the ownership change,
            // which is the common ordering - the giving client acts on its own poll tick.
            foreach (var fdr in FDP2.GetFDRs.ToList())
                TryAcceptTransfer(fdr);
        }
    }

    // A TMA sector given away: its aircraft are not handed over, but they must not be dropped either.
    // When the sector leaves MMI.SectorsControlled, vatSys stops tracking every tag whose
    // ControllingSector it was. Each tag this controller tracks inside the sector, or controlled
    // from it, is pointed at this session's own login sector (OwnAnchor), which nothing removes, so
    // it stays tracked here until the controller hands it over.
    void KeepForManualHandoff(SectorTransfer transfer, string toCallsign, HashSet<string> handedOver)
    {
        var anchor = OwnAnchor();
        var kept = new List<string>();

        foreach (var fdr in FDP2.GetFDRs.ToList())
        {
            if (string.IsNullOrEmpty(fdr.Callsign) || !fdr.IsTrackedByMe || handedOver.Contains(fdr.Callsign))
                continue;

            var controlledFromIt = fdr.ControllingSector != null && fdr.ControllingSector.Equals(transfer.Sector);
            if (!controlledFromIt && SectorLocator.Resolve(fdr, new[] { transfer.Sector }) == null)
                continue;

            if (anchor != null && !anchor.Equals(transfer.Sector))
            {
                fdr.ControllingSector = anchor;
                if (MMI.FindTrack(fdr) is { } track)
                    MMI.SetTrackState(track);
            }

            // ReassertConfirmations would point it back at the sector being given away.
            lock (_lock)
                _confirming.Remove(fdr.Callsign);

            // Not handed over by a later sector in the same change either.
            handedOver.Add(fdr.Callsign);
            kept.Add(fdr.Callsign);
        }

        ActionLog.Log("Tag", kept.Count > 0
            ? $"{transfer.Sector.Name} went to {toCallsign} - TMA sector, kept {string.Join(", ", kept)} for a manual handoff"
            : $"{transfer.Sector.Name} went to {toCallsign} - TMA sector, no tags of ours inside it",
            new
            {
                action = "sector_lost_tma_manual",
                sector = transfer.Sector.Name,
                to_callsign = toCallsign,
                fdr_callsigns = kept.ToArray(),
                anchored_to = anchor?.Name
            });
    }

    // The giving side. Hands each aircraft this controller tracks inside the lost sector to the
    // controller who now owns it. handedOver holds aircraft already handed off earlier in the same
    // ownership change (see OnOwnershipChanged); they are never handed a second time.
    void GiveAway(SectorTransfer transfer, HashSet<string> handedOver)
    {
        if (OzServerMode.Disabled)
            return;

        // Released to nobody: there is no one to hand the aircraft to, and they are left as they
        // are rather than dropped to uncontrolled.
        if (transfer.Counterparty?.Callsign is not { Length: > 0 } toCallsign)
            return;

        // A TMA sector: its aircraft stay with this controller until they hand each one over
        // themselves (see PrimaryPosition.IsTmaSector).
        if (PrimaryPosition.IsTmaSector(transfer.Sector))
        {
            KeepForManualHandoff(transfer, toCallsign, handedOver);
            return;
        }

        // The sector to hand to is the new owner's own, not the one that changed hands: vatSys
        // addresses a handoff to a sector, and it has to be one that controller is working.
        var toSector = SectorsVolumes.Sectors.FirstOrDefault(s =>
            string.Equals(s.Callsign, toCallsign, StringComparison.OrdinalIgnoreCase));

        if (toSector == null)
        {
            ActionLog.Log("Tag", $"Lost {transfer.Sector.Name} to {toCallsign}, whose sector could not be resolved - tags left alone");
            return;
        }

        var moved = new List<string>();
        var deferred = new List<string>();

        foreach (var fdr in FDP2.GetFDRs.ToList())
        {
            if (string.IsNullOrEmpty(fdr.Callsign) || !fdr.IsTrackedByMe)
                continue;

            // Inside the sector that changed hands, by live radar position.
            if (SectorLocator.Resolve(fdr, new[] { transfer.Sector }) == null)
                continue;

            // Handed over already - by another of the sectors lost in this change, or by an earlier
            // change whose handoff to this controller is still in flight. A second handoff PDU
            // re-opens the transfer on the receiving side after it has been accepted.
            if (handedOver.Contains(fdr.Callsign)
                || (fdr.HandoffSector != null && fdr.HandoffSector.Equals(toSector)))
            {
                handedOver.Add(fdr.Callsign);
                continue;
            }

            // Deferred while toSector's controller is not visible in OnlineATC:
            // MMI.HandoffJurisdiction would drop the aircraft from tracking instead of handing it
            // off (see "Deferred handoffs" in the class comment).
            if (IsOnline(toSector))
            {
                MMI.HandoffJurisdiction(fdr, toSector);
            }
            else
            {
                lock (_lock)
                    _deferred[fdr.Callsign] = new DeferredHandoff(toSector);

                deferred.Add(fdr.Callsign);
            }

            // Moved off the lost sector now, on both branches. FDP2.Handoff sets only State and
            // HandoffSector; ControllingSector normally stays where it is until
            // ProcessHandoffAccepted moves it once the accept arrives. But ReconcileMmiWithOwned
            // raises OwnershipChanged (which runs this) before its MMI.SetControlledSectors call
            // drops the sector, and that call's cleanup stops tracking (Network.StopTracking) any
            // FDR this session tracks whose ControllingSector has left the list - cancelling the
            // handoff just started, or about to be retried.
            //
            // Pointed at this session's own login sector (OwnAnchor), not the receiving
            // controller's: the receiver's sector is itself in this session's list whenever this
            // controller has been covering it, and is removed next. The login sector is the one
            // entry no removal takes out, whoever calls SetControlledSectors and in whatever order.
            // ProcessHandoffAccepted sets ControllingSector from the accepting controller's
            // callsign, so where it points meanwhile does not change where the aircraft ends up.
            fdr.ControllingSector = OwnAnchor() ?? toSector;

            // Recomputes the tag's displayed state against the ControllingSector just set. The
            // deferred branch has not touched the track at all.
            var track = MMI.FindTrack(fdr);
            if (track != null)
                MMI.SetTrackState(track);

            // Drops any reassert AcceptTransfer registered for this callsign. An aircraft given
            // away inside its confirmation window has its track on HandoverOut - correct, but not
            // Jurisdiction - and ReassertConfirmations would force ControllingSector back to the
            // sector being given away, undoing the reassignment above.
            lock (_lock)
                _confirming.Remove(fdr.Callsign);

            handedOver.Add(fdr.Callsign);
            moved.Add(fdr.Callsign);
        }

        ActionLog.Log("Tag", moved.Count > 0
            ? $"{transfer.Sector.Name} went to {toCallsign} - handed over {string.Join(", ", moved)}"
              + (deferred.Count > 0
                  ? $" ({string.Join(", ", deferred)} deferred - {toCallsign} not yet visible on the network)"
                  : "")
            : $"{transfer.Sector.Name} went to {toCallsign} - no tags of ours inside it",
            new
            {
                action = "sector_lost_handoff_tags",
                sector = transfer.Sector.Name,
                to_callsign = toCallsign,
                fdr_callsigns = moved.ToArray(),
                deferred_callsigns = deferred.ToArray()
            });
    }

    // True once toSector's controller is visible in this client's Network.OnlineATC - the only
    // condition under which MMI.HandoffJurisdiction sends a handoff PDU rather than dropping the
    // aircraft (see "Deferred handoffs" in the class comment).
    static bool IsOnline(SectorsVolumes.Sector toSector) =>
        (Network.GetOnlineATCs ?? new List<NetworkATC>())
            .Any(atc => string.Equals(atc.Callsign, toSector.Callsign, StringComparison.Ordinal));

    // Finishes a handoff GiveAway could not send. Runs on its own timer rather than from
    // OnFdrUpdate: the wait is for the new owner to appear online, which no FDR update signals.
    void RetryDeferredHandoffs()
    {
        if (OzServerMode.Disabled)
            return;

        List<KeyValuePair<string, DeferredHandoff>> toCheck;
        lock (_lock)
        {
            var expired = _deferred.Where(kv => kv.Value.Until < DateTime.UtcNow).ToList();
            foreach (var kv in expired)
            {
                _deferred.Remove(kv.Key);
                ActionLog.Log("Tag",
                    $"Gave up waiting for {kv.Value.ToSector.Name}'s controller to appear online - "
                    + $"{kv.Key} stays tracked by us under {kv.Value.ToSector.Name}",
                    new
                    {
                        action = "handoff_deferred_timeout",
                        fdr_callsign = kv.Key,
                        sector = kv.Value.ToSector.Name
                    });
            }

            if (_deferred.Count == 0)
                return;

            toCheck = _deferred.ToList();
        }

        foreach (var entry in toCheck)
        {
            var callsign = entry.Key;
            var toSector = entry.Value.ToSector;

            var fdr = FDP2.GetFDRs.FirstOrDefault(f => string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase));

            // No longer tracked by us, or pointed somewhere else - a later GiveAway, a manual
            // handoff, TagResumeRecovery - so the entry no longer describes anything. GiveAway
            // leaves the tag on this session's own sector (OwnAnchor), or on toSector when there
            // was no anchor; either means it is still waiting.
            //
            // The anchor is tested for null before it is compared. vatSys's Sector.Equals reads its
            // argument's Callsign without checking it, so Equals(null) throws rather than answering
            // false - and OwnAnchor is null whenever the login sector is not in
            // MMI.SectorsControlled (around a disconnect, for one). This runs from a timer on the
            // UI thread with nothing above it to catch that.
            var anchor = OwnAnchor();
            var waiting = fdr?.ControllingSector != null
                          && (fdr.ControllingSector.Equals(toSector)
                              || (anchor != null && fdr.ControllingSector.Equals(anchor)));

            if (fdr == null || !fdr.IsTrackedByMe || !waiting)
            {
                lock (_lock)
                    _deferred.Remove(callsign);
                continue;
            }

            if (!IsOnline(toSector))
                continue;

            MMI.HandoffJurisdiction(fdr, toSector);

            lock (_lock)
                _deferred.Remove(callsign);

            ActionLog.Log("Tag",
                $"{toSector.Name}'s controller is now online - completed the deferred handoff for {callsign}",
                new
                {
                    action = "handoff_deferred_completed",
                    fdr_callsign = callsign,
                    sector = toSector.Name
                });
        }
    }

    // The taking side. Accepts an incoming handoff silently when it belongs to a transfer.
    void TryAcceptTransfer(FDP2.FDR fdr)
    {
        if (OzServerMode.Disabled)
            return;

        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        // STATE_HANDOVER, not STATE_HANDOVER_FIRST - see the class comment. Anything else is not a
        // handoff being offered to us, and the suppression is cleared so the next flash of this
        // aircraft is reported.
        if (fdr.State != FDP2.FDR.FDRStates.STATE_HANDOVER)
        {
            lock (_lock)
                _reportedUnmatched.Remove(fdr.Callsign);

            return;
        }

        // Still ours, so this is a handoff this client sent, not one it was offered: FDP2.Handoff
        // puts the FDR in STATE_HANDOVER on the giving client too.
        if (fdr.IsTrackedByMe)
            return;

        // The controller who handed it to us. vatSys does not always populate ControllerTracking on
        // repeated or crossed handoffs, so HandoffFrom falls back to the callsign of the sector
        // still attached to the offered FDR.
        var from = HandoffFrom(fdr);

        PendingTransfer? match = null;

        lock (_lock)
        {
            _incoming.RemoveAll(pending => pending.Until < DateTime.UtcNow);

            var geometryMatches = _incoming
                .Where(pending => SectorLocator.Resolve(fdr, new[] { pending.Sector }) != null)
                .ToList();

            // Deliberately requires a positive identity match: the sender has to be the controller
            // OzServer says the sector was gained from. Geometry alone is not enough, even with one
            // transfer pending - it would silently accept a deliberate handoff from any controller
            // for an aircraft inside a recently gained sector. A handoff whose sender cannot be
            // identified flashes like any other.
            match = string.IsNullOrEmpty(from)
                ? null
                : geometryMatches.FirstOrDefault(pending =>
                    string.Equals(pending.FromCallsign, from, StringComparison.OrdinalIgnoreCase));
        }

        // Not part of a transfer - somebody handed this over deliberately, so it stays flashing for
        // the controller to accept themselves.
        //
        // Reported once, with both sides of the comparison, because a flash has three causes that
        // otherwise look alike: no transfer registered, the handing controller's callsign not
        // matching the previous owner OzServer named, or the aircraft not resolving inside the
        // sector that moved.
        if (match == null)
        {
            // The handoff comes over VATSIM and the ownership change over OzServer, and the handoff
            // is often first; until the sector lands there is nothing to match it against. An
            // unmatched handoff therefore pulls the ownership sync forward rather than waiting for
            // the push signal or the next poll. Rate-limited, because an ordinary manual handoff is
            // also unmatched for as long as it flashes.
            RequestOwnershipCatchUp();

            bool report;
            string expecting;

            lock (_lock)
            {
                report = _reportedUnmatched.Add(fdr.Callsign);
                expecting = _incoming.Count == 0
                    ? "none"
                    : string.Join(", ", _incoming.Select(p => $"{p.Sector.Name} from {p.FromCallsign}"));
            }

            if (report)
            {
                var inside = SectorLocator.Resolve(fdr, MMI.SectorsControlled.Where(s => !s.IsDummy))?.Name ?? "nowhere of ours";
                ActionLog.Log("Tag",
                    $"{fdr.Callsign} flashing from {from ?? "(nobody tracking it)"}, left to accept by hand "
                    + $"(resolves to {inside}; expecting transfers: {expecting})",
                    new
                    {
                        action = "handoff_left_to_manual",
                        fdr_callsign = fdr.Callsign,
                        from_callsign = from,
                        resolved_sector = inside,
                        state = fdr.State.ToString(),
                        is_tracked_by_me = fdr.IsTrackedByMe,
                        controller_tracking = fdr.ControllerTracking?.Callsign,
                        controlling_sector = fdr.ControllingSector?.Name,
                        pending_transfers = PendingTransferContext()
                    });
            }

            return;
        }

        // One accept per aircraft, however many pool threads reach this line together - see
        // _accepting. A thread that loses the race leaves the accept to the one that won.
        lock (_lock)
        {
            _reportedUnmatched.Remove(fdr.Callsign);

            if (!_accepting.Add(fdr.Callsign))
                return;
        }

        try
        {
            AcceptTransfer(fdr, match, from);
        }
        finally
        {
            lock (_lock)
                _accepting.Remove(fdr.Callsign);
        }
    }

    void AddOrRefreshIncoming(SectorsVolumes.Sector sector, string fromCallsign)
    {
        _incoming.RemoveAll(pending => pending.Until < DateTime.UtcNow);

        var existing = _incoming.FirstOrDefault(pending =>
            pending.Sector.Equals(sector)
            && string.Equals(pending.FromCallsign, fromCallsign, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.RefreshUntil();
            return;
        }

        _incoming.Add(new PendingTransfer(sector, fromCallsign));
    }

    static string? HandoffFrom(FDP2.FDR fdr)
    {
        var from = CallsignOrNull(fdr.ControllerTracking?.Callsign);
        if (from != null)
            return from;

        var sector = fdr.ControllingSector;
        return sector == null ? null : CallsignOrNull(sector.Callsign);
    }

    static string? CallsignOrNull(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            return null;

        return callsign!.Trim();
    }

    // At most one catch-up refresh per interval, however many unmatched handoffs are in view - see
    // TryAcceptTransfer.
    static readonly TimeSpan CatchUpInterval = TimeSpan.FromSeconds(1);
    DateTime _lastCatchUp = DateTime.MinValue;

    void RequestOwnershipCatchUp()
    {
        if (OzServerMode.Disabled)
            return;

        lock (_lock)
        {
            if (DateTime.UtcNow - _lastCatchUp < CatchUpInterval)
                return;

            _lastCatchUp = DateTime.UtcNow;
        }

        _ = _tracker.RefreshFromServerIfIdleAsync();
    }

    // The accept itself, separate so TryAcceptTransfer's in-flight guard can wrap it.
    void AcceptTransfer(FDP2.FDR fdr, PendingTransfer match, string? from)
    {
        if (OzServerMode.Disabled)
            return;

        // Resolved against what this session holds now rather than assuming the transferred
        // sector: the aircraft may be in a sub-sector of it.
        var mine = SectorLocator.Resolve(fdr, MMI.SectorsControlled.Where(s => !s.IsDummy))
                   ?? match.Sector;

        var beforeState = fdr.State.ToString();
        var beforeTrackedByMe = fdr.IsTrackedByMe;
        var beforeControllerTracking = fdr.ControllerTracking?.Callsign;
        var beforeControllingSector = fdr.ControllingSector?.Name;

        // Held before the accept: once the tag is ours, FdrSync would report this client's own
        // label - a CFL vatSys has just reset, nothing typed yet - over the previous controller's,
        // before ApplyClearanceDataAsync has read theirs back.
        FdrSync.HoldLabelState(fdr.Callsign);

        MMI.AcceptJurisdiction(fdr);

        if (fdr.State != FDP2.FDR.FDRStates.STATE_CONTROLLED)
        {
            ActionLog.Log("Tag", $"Could not accept transferred {fdr.Callsign} (state {fdr.State})", new
            {
                action = "handoff_accept_failed",
                fdr_callsign = fdr.Callsign,
                sector = match.Sector.Name,
                from_callsign = from,
                before = new
                {
                    state = beforeState,
                    is_tracked_by_me = beforeTrackedByMe,
                    controller_tracking = beforeControllerTracking,
                    controlling_sector = beforeControllingSector
                },
                after = new
                {
                    state = fdr.State.ToString(),
                    is_tracked_by_me = fdr.IsTrackedByMe,
                    controller_tracking = fdr.ControllerTracking?.Callsign,
                    controlling_sector = fdr.ControllingSector?.Name
                }
            });
            return;
        }

        // Reasserted after the accept: vatSys resolves jurisdiction against its own default
        // geometry, which knows nothing about OzServer subsectors.
        fdr.ControllingSector = mine;

        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);

        // Registered although the state has just been set: one SetTrackState call is not durable -
        // see ReassertConfirmations.
        lock (_lock)
            _confirming[fdr.Callsign] = new PendingConfirmation(mine);

        _fdrSync.PushNow(fdr);
        _ = ApplyClearanceDataAsync(fdr);

        ActionLog.Log("Tag", $"Accepted {fdr.Callsign} with {match.Sector.Name} from {from}, no flash", new
        {
            action = "handoff_accept",
            fdr_callsign = fdr.Callsign,
            sector = match.Sector.Name,
            resolved_sector = mine.Name,
            from_callsign = from,
            before = new
            {
                state = beforeState,
                is_tracked_by_me = beforeTrackedByMe,
                controller_tracking = beforeControllerTracking,
                controlling_sector = beforeControllingSector
            },
            after = new
            {
                state = fdr.State.ToString(),
                is_tracked_by_me = fdr.IsTrackedByMe,
                controller_tracking = fdr.ControllerTracking?.Callsign,
                controlling_sector = fdr.ControllingSector?.Name
            }
        });
    }

    // Applies the previous controller's label - the CFL, scratchpad, global ops and the C (see
    // LabelStateRestore) - to a tag just accepted. Accepting jurisdiction changes who tracks a tag,
    // not vatSys's local FDR fields, so without this the new controller sees no CFL although
    // OzServer still holds it.
    //
    // Fire-and-forget from AcceptTransfer rather than awaited: the accept is timing-sensitive (see
    // ReassertConfirmations) and must not wait on a network round trip. The CFL's colour is not
    // carried: vatSys marks the CFL new whenever a tag enters a controller's jurisdiction.
    async System.Threading.Tasks.Task ApplyClearanceDataAsync(FDP2.FDR fdr)
    {
        OzServerFdrUpdateDto? flight = null;

        try
        {
            flight = await _api.GetFlightAsync(fdr.Callsign);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't fetch clearance data for {fdr.Callsign}: {ex.Message}", ex), "OzServer");
        }

        RunOnUiThread(() =>
        {
            try
            {
                // Only while it is still ours: a tag handed off again or dropped during the round
                // trip does not get the previous controller's label.
                if (flight == null || !fdr.IsTrackedByMe)
                    return;

                var applied = LabelStateRestore.Apply(fdr, flight, replaceCfl: true);
                if (applied == null)
                    return;

                ActionLog.Log("Tag", $"Applied {fdr.Callsign}'s label from OzServer after the transfer: {applied}", new
                {
                    action = "handoff_label_state_applied",
                    fdr_callsign = fdr.Callsign,
                    cfl_lower = flight.CflLower,
                    cfl_upper = flight.CflUpper,
                    label_op_data = flight.LabelOpData,
                    global_op_data = flight.GlobalOpData,
                    c_prompt = flight.CPrompt
                });
            }
            finally
            {
                // Always released and followed by a push: whatever was applied - or nothing, if the
                // read failed - this controller now speaks for the label.
                FdrSync.ReleaseLabelState(fdr.Callsign);
                _fdrSync.PushNow(fdr);
            }
        });
    }

    object[] PendingTransferContext()
    {
        lock (_lock)
            return _incoming
                .Select(pending => new
                {
                    sector = pending.Sector.Name,
                    from_callsign = pending.FromCallsign,
                    until = pending.Until
                })
                .Cast<object>()
                .ToArray();
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
