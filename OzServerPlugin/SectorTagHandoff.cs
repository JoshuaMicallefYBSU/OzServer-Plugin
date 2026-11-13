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
//   Giving side  - lost a sector to someone. For each aircraft it holds inside that sector, calls
//                  MMI.HandoffJurisdiction to the sector the new owner now has. That is vatSys's
//                  own handoff, travelling over vatSys's own protocol - PROVIDED the new owner is
//                  already visible in this client's own Network.OnlineATC. If they aren't yet
//                  (see IsOnline/_deferred below), HandoffJurisdiction does something else entirely
//                  and this class defers the real handoff instead of calling it.
//
//   Taking side  - gained a sector from someone. Watches for the handoffs that produces and accepts
//                  them silently, the way TagResumeRecovery accepts a resumed tag.
//
// Only aircraft the losing controller actually held move. Uncontrolled traffic in the sector stays
// uncontrolled, and a tag a third controller happens to be working is not touched - a sector
// changing hands is not authority over somebody else's aircraft.
//
// ---------------------------------------------------------------------------------------------
// Why the taking side can tell this apart from an ordinary handoff
//
// Only an OzServer sector transfer is accepted silently; a handoff a controller makes by hand still
// flashes, because they are asking rather than telling. Distinguishing them needs no extra server
// state: an incoming handoff is a transfer when all three hold -
//
//   1. it is for a sector this session has just gained on OzServer,
//   2. from the very controller who had that sector a moment ago (fdr.ControllerTracking),
//   3. within a short window of gaining it.
//
// Anything else is somebody's deliberate handoff and is left to flash. The window matters because
// the two clients act on their own poll ticks, so the handoff can arrive either side of this
// client noticing the ownership change - see PendingTransfer.
//
// ---------------------------------------------------------------------------------------------
// What an inbound handoff actually looks like, which is not what this file first assumed
//
// Both halves of the receiving test were wrong, and each on its own was enough to stop every
// transfer this plugin has ever made. From the vatSys IL, Network::VATSIM_HandoffReceived is the
// whole of the receiving path and it ends in FDP2::Handoff, which leaves the FDR like this:
//
//   State              STATE_HANDOVER (9). NOT STATE_HANDOVER_FIRST (7) - that is written at
//                      exactly one instruction in the assembly, inside FDP2::HandoffFirst, which
//                      vatSys only reaches from FDP2::FDRDeparted. Seven is a local departure
//                      offering you an aircraft; nine is somebody handing you one.
//   HandoffController  null. It has two writers in the assembly and neither runs here:
//                      MMI::HandoffJurisdiction sets it on the *sending* client, to the recipient,
//                      and FDP2::CancelHandoff nulls it. The receiver never sets it at all.
//   ControllerTracking the sending controller, resolved from the PDU's From field. This is the
//                      "who handed this to me" the match below needs, and it is what vatSys's own
//                      accept path uses to address the acceptance back.
//   ControllingSector  the sender's sector, also from the PDU's From field.
//   HandoffSector      our sector, from the PDU's To field.
//
// So the old test - state 7, matched against HandoffController - could never fire on a received
// handoff. It returned before even reaching the diagnostic, which is why a receiving client logged
// nothing whatsoever about a tag it had just left flashing.
//
// MMI.AcceptJurisdiction is right for this and is left alone: on an FDR that is tracked, not
// tracked by us, and has a HandoffSector, it sends the PDUHandoffAccept the sender is waiting on
// and then sets ControllingSector. TagResumeRecovery's HandoffFirst/Process sequence is
// deliberately NOT reused here - it exists to lift an *untracked* resumed FDR over
// MMI.AcceptJurisdiction's "State > 5" precondition, and applying it to a real inbound handoff
// would overwrite state 9, repoint HandoffSector at the sender, and skip the acceptance PDU.
//
// ---------------------------------------------------------------------------------------------
// What MMI.HandoffJurisdiction does when the new owner isn't visible yet
//
// From the vatSys IL, MMI::HandoffJurisdiction(FDR, Sector) only sends a real handoff PDU when the
// target sector's own Callsign is found in Network.Instance.OnlineATC. If it isn't, it takes a
// completely different branch instead: Network.Instance.StopTracking(fdr) - which broadcasts a
// drop and sets ControllerTracking null - followed by a *local-only* FDP2.AcceptJurisdiction(fdr,
// to). Since the aircraft is untracked at that point, that resolves fdr.State to
// STATE_UNCONTROLLED - the aircraft is silently released to nobody, not handed to the new owner.
//
// Network.OnlineATC is populated from a periodic ATCPosition broadcast (Network.cs), not an
// instant announcement on connect - PrimaryPositionWatcher and OzServerOwnershipTracker already
// document this same lag ("VATSIM's own update cycle a few seconds") for the exact scenario most
// likely to trigger it here: a controller logs on under a primary's callsign and OzServer moves
// that primary's sectors to them immediately, but the *previous* holder's client can notice the
// loss (and run GiveAway) before its own OnlineATC snapshot has caught up to the new controller's
// connection.
//
// So GiveAway checks IsOnline itself before ever calling MMI.HandoffJurisdiction. When the target
// isn't online yet, it still reassigns fdr.ControllingSector immediately - the aircraft has to be
// protected from MMI.SetControlledSectors's own old/new-sector cleanup exactly as the comment on
// that reassignment below describes, and that cleanup runs whether or not the real handoff already
// went out - but leaves the actual network handoff to RetryDeferredHandoffs, which finishes it
// once the new controller becomes visible. The aircraft stays tracked by this session throughout
// (nothing here ever calls StopTracking), so nothing is dropped while it waits.
public class SectorTagHandoff
{
    // How long after gaining a sector an incoming handoff for it still counts as part of that
    // transfer. Generous, because the losing client only acts when its own poll notices the change,
    // and the two clients are not synchronised. Too short and the tag flashes instead of landing;
    // too long and a genuine manual handoff from the same controller is swallowed - a minute is
    // well past the former and nowhere near the traffic pattern of the latter.
    static readonly TimeSpan TransferWindow = TimeSpan.FromSeconds(60);

    readonly OzServerOwnershipTracker _tracker;
    readonly FdrSync _fdrSync;
    readonly OzServerApiClient _api = new();
    readonly object _lock = new();

    // Sectors just taken from someone, and who from.
    readonly List<PendingTransfer> _incoming = new();

    // Flashing tags already reported as unmatched, so the diagnostic below fires once per handoff
    // rather than on every FDR update for the whole time it flashes. Cleared again the moment a tag
    // stops flashing, so this suppresses one flash rather than every flash that callsign will ever
    // have this session - which is how the one diagnostic that would have explained JST971 came to
    // be suppressed by an earlier, unrelated flash of the same aircraft.
    readonly HashSet<string> _reportedUnmatched = new(StringComparer.OrdinalIgnoreCase);

    // Callsigns with an accept already in flight. vatSys dispatches OnFDRUpdate through Task.Run -
    // one task per update, no serialisation - so several pool threads can be inside TryAcceptTransfer
    // for the same aircraft at once, all matching the same PendingTransfer and all calling
    // MMI.AcceptJurisdiction on it.
    readonly HashSet<string> _accepting = new(StringComparer.OrdinalIgnoreCase);

    // How long after a silent accept to keep reasserting Jurisdiction against whatever vatSys's own
    // FDR/radar processing does to the tag afterwards, and how often. See ReassertConfirmations for
    // why one correct SetTrackState call at accept time is not enough to guarantee it stays correct -
    // this fights the identical battle PendingSectorGhosts already fights for its own preview state,
    // on the same cadence.
    static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ConfirmationInterval = TimeSpan.FromSeconds(1);

    // Callsigns AcceptTransfer just accepted, and the sector jurisdiction should stick to, until
    // ReassertConfirmations has either confirmed it stuck or given up.
    readonly Dictionary<string, PendingConfirmation> _confirming = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Threading.Timer _confirmTimer;

    // How long GiveAway waits for a new sector owner to appear in Network.OnlineATC before giving
    // up on ever completing the real handoff PDU for a given aircraft - see the class header for
    // why this wait exists at all. The owner was necessarily connected to claim the sector on
    // OzServer in the first place, so this is generous relative to how long OnlineATC realistically
    // lags a connection, not a guess at how long a connection itself might take.
    static readonly TimeSpan DeferredHandoffWindow = TimeSpan.FromSeconds(60);
    static readonly TimeSpan DeferredHandoffCheckInterval = TimeSpan.FromSeconds(2);

    // Callsigns GiveAway reassigned locally but could not yet hand off for real, and the sector
    // each is waiting to be handed to. Checked by RetryDeferredHandoffs until the new owner shows
    // up in Network.OnlineATC or the window above expires.
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
        // Deliberately NOT marshalled onto the UI thread - see ProtectTagsFromArrivals for the race
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

    // Fights the same battle PendingSectorGhosts fights for its own preview state, for the same
    // reason: vatSys recomputes track.State on every subsequent FDR/radar callback using whatever
    // fdr.ControllingSector and MMI.SectorsControlled happen to be at that instant, on its own
    // schedule, independent of anything this plugin does. AcceptTransfer's own success check only
    // ever verified fdr.State reached STATE_CONTROLLED, never the actual track.State SetTrackState
    // computed - so a transfer that logged "no flash" success could still visibly settle on
    // Announced (or another demoted state) moments later, with nothing in the log to show it, because
    // whatever caused that settling ran entirely inside vatSys, after this plugin had already moved
    // on. One correct SetTrackState call at accept time is not durable against that. Reasserting it
    // for a few seconds afterwards is: if vatSys knocks it back down, this puts it back, on the same
    // 1-second cadence PendingSectorGhosts already uses to win the identical fight.
    void ReassertConfirmations()
    {
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

            // No longer tracked by us at all - handed off again, dropped, or gone. Nothing left to
            // assert, and no reason to keep trying.
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

    // This session's own login sector, as it sits in MMI.SectorsControlled. The one controlled
    // sector nothing ever takes out of the list: MMI.SetControlledSectors re-inserts it whenever a
    // caller leaves it off, and vatSys's own arrival removal keeps it explicitly. A tag whose
    // ControllingSector is this one can therefore never be dropped by a sector leaving the list.
    static SectorsVolumes.Sector? OwnAnchor()
    {
        var callsign = Network.Me?.Callsign;
        return string.IsNullOrEmpty(callsign)
            ? null
            : MMI.SectorsControlled.ToList().FirstOrDefault(s => string.Equals(s.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
    }

    // Keeps this controller's aircraft through another controller logging on to a sector it holds.
    //
    // vatSys does two things the moment a controller is confirmed ValidATC (Network
    // VATSIM_ClientQueryResponseReceived): if their callsign names a sector in this session's
    // controlled list, it takes that sector *and its sub-sectors* out of the list; and
    // MMI.SetControlledSectors, as part of doing that, calls HandoffToNone - StopTracking - on every
    // tag this session tracks whose ControllingSector just left. So BLA logging on stripped BLA, ELW,
    // MAE and MAV off Huon's scope and dropped any aircraft Huon held in them to nobody: no handoff,
    // no data, just an uncoupled, deactivated tag. OzServer still had Huon owning the airspace and put
    // the sectors straight back, but the tracking was already gone.
    //
    // There is no stopping vatSys removing the sectors. What can be done is make the removal miss
    // the aircraft: a tag pointed at this session's own login sector (OwnAnchor) is not in the set
    // that leaves, so it stays tracked, stays under this controller's jurisdiction, and goes on
    // showing its data until it is handed over for real.
    //
    // The timing is why this runs synchronously on whatever thread raised OnlineATCChanged instead
    // of being marshalled to the UI thread like everything else here. AddOnlineATC sends the
    // IsValidATC query for a new controller and *then* raises OnlineATCChanged, on the network
    // thread; the answer that triggers the removal needs a server round trip and arrives on that
    // same thread. Handled inline, this always runs first. BeginInvoked, it would be queued behind
    // vatSys's own work and could easily land after the tags were already dropped.
    //
    // Touches nothing but FDR.ControllingSector, which vatSys itself writes from network threads,
    // and only for tags at risk - a controller logging on somewhere unrelated changes nothing.
    void ProtectTagsFromArrivals()
    {
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

            // Exactly the set vatSys's removal takes: the sector named by an online controller's
            // callsign, plus its direct sub-sectors.
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

                // ReassertConfirmations would otherwise point it straight back at the sector that
                // is about to be removed.
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
            // Runs inside vatSys's own network event. Nothing here is worth breaking that for; the
            // worst case of skipping it is the drop it exists to prevent.
            ActionLog.Log("Tag", $"Couldn't protect tags from a controller logging on: {ex.Message}");
        }
    }

    // Called for every FDR update, so an incoming transfer is accepted as soon as it lands rather
    // than on a timer of our own.
    public void OnFdrUpdate(FDP2.FDR fdr) => TryAcceptTransfer(fdr);

    void OnOwnershipChanged(SectorOwnershipDiff diff)
    {
        foreach (var transfer in diff.Lost)
            GiveAway(transfer);

        foreach (var transfer in diff.Gained)
        {
            if (transfer.Counterparty?.Callsign is not { Length: > 0 } from)
                continue;

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
            // which is the common ordering - the giving client acts on its own poll tick and there
            // is no reason that lands after ours.
            foreach (var fdr in FDP2.GetFDRs.ToList())
                TryAcceptTransfer(fdr);
        }
    }

    // The losing half. Hands each aircraft this controller holds inside the lost sector to the
    // controller who now owns it.
    void GiveAway(SectorTransfer transfer)
    {
        // Released to nobody - there is no one to hand the aircraft to, and they are left exactly as
        // they are rather than dropped to uncontrolled.
        if (transfer.Counterparty?.Callsign is not { Length: > 0 } toCallsign)
            return;

        // The sector to hand to is the new owner's own, not the one that just changed hands: vatSys
        // addresses a handoff to a sector, and it has to be one that controller is actually working.
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

            // Geographically inside the sector that changed hands, by live position - the same test
            // FdrSync uses to report a flight's sector, restricted here to the one sector involved.
            if (SectorLocator.Resolve(fdr, new[] { transfer.Sector }) == null)
                continue;

            // See the class header for why this check exists: MMI.HandoffJurisdiction silently does
            // something else entirely - dropping the aircraft from network tracking rather than
            // handing it off - when toSector's controller isn't visible in OnlineATC yet. Deferring
            // here, rather than letting that happen, is the whole reason IsOnline/_deferred exist.
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

            // Reassigned immediately either way, not left for the real PDUHandoffAccept round trip to
            // set it (which is what FDP2.Handoff itself does - it only touches State and
            // HandoffSector, never ControllingSector; that field is normally left exactly where it
            // was until ProcessHandoffAccepted moves it, seconds later, once the round trip
            // completes).
            //
            // ReconcileMmiWithOwned calls OwnershipChanged (which is what runs this) before its own,
            // later MMI.SetControlledSectors call actually drops this sector from SectorsControlled -
            // and THAT method's own internal cleanup hands off to nobody any FDR whose
            // ControllingSector was in the old list and isn't in the new one, checked purely against
            // ControllingSector/IsTrackedByMe, with no idea that fdr.State just became STATE_HANDOVER
            // (or, on the deferred path, hasn't changed at all yet) a moment ago. Left at the old
            // sector, it matches that cleanup and gets dropped from the network outright
            // (Network.StopTracking) - cancelling the very handoff this loop just initiated (or is
            // about to retry), before the aircraft had any real chance to reach the controller it was
            // being given to.
            //
            // Reassigned to this session's own login sector (OwnAnchor), not to the receiving
            // controller's. That used to be toSector, on the reasoning that the receiver's sector "was
            // never in this session's own list to begin with" - which is false exactly when it
            // matters: when BLA's sectors moved off Huon, BLA was in Huon's list, because Huon had
            // been working it. The very next removal of BLA (VatpacPlugin's resweep, ReconcileMmiWithOwned)
            // matched JST215 and stopped tracking it mid-handoff - uncoupled and deactivated on
            // Huon's scope. The own login sector is the one entry no removal ever takes out, so the
            // cleanup cannot match it, whoever calls SetControlledSectors and in whatever order.
            // ProcessHandoffAccepted sets ControllingSector from the accepting controller's callsign
            // when the accept arrives, so where it points in the meantime does not change where the
            // aircraft ends up - Huon goes on seeing it, tracked by BLA.
            fdr.ControllingSector = OwnAnchor() ?? toSector;

            // Recomputes the tag's displayed state against the ControllingSector just set above,
            // rather than trusting whichever of the two branches above happened to touch it (a real
            // handoff already does this internally via FDP2.Handoff, but calling it again here is
            // harmless and covers the deferred branch, which does nothing to the track on its own).
            var track = MMI.FindTrack(fdr);
            if (track != null)
                MMI.SetTrackState(track);

            // Cancels ReassertConfirmations' own claim on this callsign, if AcceptTransfer registered
            // one - see there for why it exists. Without this, giving an aircraft away again inside
            // its own 10-second confirmation window (the same rapid back-and-forth this whole class
            // exists for) raced the two fixes against each other: the reassert timer saw this FDR's
            // track sitting on HandoverOut - correct, mid-handoff, but not Jurisdiction - and forced
            // ControllingSector straight back to the sector this loop is actively giving away, which
            // undid the reassignment two lines up and reopened the exact drop this was written to
            // prevent. Once GiveAway has taken an aircraft, nothing it used to hold jurisdiction for
            // is this plugin's to keep asserting.
            lock (_lock)
                _confirming.Remove(fdr.Callsign);

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

    // True once toSector's controller is visible in this client's own Network.OnlineATC snapshot -
    // the only condition under which MMI.HandoffJurisdiction actually sends a real handoff PDU
    // rather than silently dropping the aircraft (see the class header).
    static bool IsOnline(SectorsVolumes.Sector toSector) =>
        (Network.GetOnlineATCs ?? new List<NetworkATC>())
            .Any(atc => string.Equals(atc.Callsign, toSector.Callsign, StringComparison.Ordinal));

    // Finishes a handoff GiveAway couldn't send yet. Runs on its own timer rather than off
    // OnFdrUpdate: an aircraft GiveAway has already reassigned locally keeps receiving ordinary FDR
    // updates like any other, so there is no update-driven event left to hang a retry off.
    void RetryDeferredHandoffs()
    {
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

            // No longer tracked by us, or already pointed somewhere else - a later GiveAway, a
            // manual handoff, TagResumeRecovery - so this deferred entry no longer describes
            // anything real and is not this loop's to act on.
            // GiveAway leaves it on this session's own sector (OwnAnchor), or on toSector when there
            // was no anchor to use - either still means "waiting on this deferred handoff".
            //
            // The anchor is tested for null before it is compared. vatSys's Sector.Equals reads its
            // argument's Callsign without checking it, so Equals(null) throws rather than answering
            // false - and OwnAnchor is null whenever the login sector is not in MMI.SectorsControlled
            // (around a disconnect, most of all). This runs from a timer on the UI thread with
            // nothing above it to catch that.
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

    // The taking half. Accepts an incoming handoff silently when it belongs to a transfer.
    void TryAcceptTransfer(FDP2.FDR fdr)
    {
        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        // STATE_HANDOVER, not STATE_HANDOVER_FIRST - see the class header. Anything else is not a
        // handoff being offered to us, and the suppression is dropped on the way past so the next
        // genuine flash of this aircraft is reportable again.
        if (fdr.State != FDP2.FDR.FDRStates.STATE_HANDOVER)
        {
            lock (_lock)
                _reportedUnmatched.Remove(fdr.Callsign);

            return;
        }

        // Still ours, so this is the handoff we just *sent*, not one we were offered - FDP2.Handoff
        // puts the FDR in STATE_HANDOVER on the giving client too. Nothing to accept, and without
        // this the giving side reported every aircraft it handed over as an unmatched flash.
        if (fdr.IsTrackedByMe)
            return;

        // The controller who handed it to us. vatSys does not always populate ControllerTracking on
        // repeated or crossed handoffs, so fall back to the sector still attached to the offered
        // FDR, then finally to a unique pending sector match below.
        var from = HandoffFrom(fdr);

        PendingTransfer? match = null;

        lock (_lock)
        {
            _incoming.RemoveAll(pending => pending.Until < DateTime.UtcNow);

            var geometryMatches = _incoming
                .Where(pending => SectorLocator.Resolve(fdr, new[] { pending.Sector }) != null)
                .ToList();

            // Deliberately requires a positive identity match - who actually sent this handoff -
            // against who OzServer says this sector was gained from. There used to be a fallback
            // here that trusted geometry alone whenever exactly one transfer was pending and the
            // sender couldn't be identified (HandoffFrom returning null), on the reasoning that no
            // other pending sector could explain it. In practice that let an ordinary, deliberate
            // handoff from any controller auto-accept the instant it landed inside a sector this
            // session had recently gained from *someone*, without ever checking it came from that
            // same someone - "tags being auto accepted should only happen during sector changes",
            // not merely because an aircraft happens to be inside one. An unmatched handoff still
            // flashes for the controller to accept by hand, same as an unrecognized sender always
            // has - see the "not part of a transfer" branch below.
            match = string.IsNullOrEmpty(from)
                ? null
                : geometryMatches.FirstOrDefault(pending =>
                    string.Equals(pending.FromCallsign, from, StringComparison.OrdinalIgnoreCase));
        }

        // Not part of a transfer - somebody handed this over deliberately, so it stays flashing for
        // the controller to accept themselves.
        //
        // Reported once, with both sides of the comparison, because "it flashed instead of being
        // accepted" is otherwise indistinguishable between the three things that cause it: no
        // transfer registered, the handing controller's callsign not matching the one OzServer named
        // as the previous owner, or the aircraft not resolving inside the sector that moved.
        if (match == null)
        {
            // Ask the backend now rather than waiting to be told.
            //
            // The two halves of a transfer arrive over different channels: the handoff comes over
            // VATSIM, the ownership change over OzServer, and the VATSIM one is routinely first.
            // Until the sector lands there is nothing to match the handoff against, so the tag sits
            // in handover - visibly blue for a moment on every transfer.
            //
            // An unmatched handoff is a good reason to suspect ownership has moved, so this pulls
            // the sync forward instead of waiting for the push signal or the next poll. Rate-limited
            // because an ordinary manual handoff is also unmatched and will stay that way for as
            // long as it flashes - that must not turn into a request per FDR update.
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
        // _accepting. Losing the race means another thread is already accepting this same handoff,
        // which is the outcome wanted either way.
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

    // At most one catch-up refresh per interval, however many unmatched handoffs are in view. See
    // the caller for why an unmatched handoff is worth asking about at all.
    static readonly TimeSpan CatchUpInterval = TimeSpan.FromSeconds(1);
    DateTime _lastCatchUp = DateTime.MinValue;

    void RequestOwnershipCatchUp()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastCatchUp < CatchUpInterval)
                return;

            _lastCatchUp = DateTime.UtcNow;
        }

        _ = _tracker.RefreshFromServerIfIdleAsync();
    }

    // The accept itself, split out only so the in-flight guard above has a body to wrap.
    void AcceptTransfer(FDP2.FDR fdr, PendingTransfer match, string? from)
    {
        // Resolved against what this session actually holds now, rather than assuming the
        // transferred sector: the aircraft may sit in a sub-sector of it.
        var mine = SectorLocator.Resolve(fdr, MMI.SectorsControlled.Where(s => !s.IsDummy))
                   ?? match.Sector;

        var beforeState = fdr.State.ToString();
        var beforeTrackedByMe = fdr.IsTrackedByMe;
        var beforeControllerTracking = fdr.ControllerTracking?.Callsign;
        var beforeControllingSector = fdr.ControllingSector?.Name;

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

        // Registered even though the state was just set correctly above - see
        // ReassertConfirmations for why that alone is not durable, and why this keeps checking
        // rather than trusting the one call that just ran.
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

    // Applies the previous controller's clearance data - CFL in particular - onto a tag just
    // accepted. Fire-and-forget from AcceptTransfer rather than awaited inline: the jurisdiction
    // accept there is timing-sensitive (see ReassertConfirmations, which starts fighting for it
    // immediately) and must not wait on a network round trip, whereas a CFL landing a few hundred
    // milliseconds after the tag itself is not something a controller would ever notice. Accepting
    // jurisdiction only ever moves who is tracking a tag - none of vatSys's own local FDR fields -
    // so without this the new controller's screen simply shows no CFL at all until they enter one
    // themselves, even though OzServer's own record of it survived the transfer intact.
    async System.Threading.Tasks.Task ApplyClearanceDataAsync(FDP2.FDR fdr)
    {
        OzServerFdrUpdateDto? flight;

        try
        {
            flight = await _api.GetFlightAsync(fdr.Callsign);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't fetch clearance data for {fdr.Callsign}: {ex.Message}", ex), "OzServer");
            return;
        }

        if (flight == null)
            return;

        // Still ours - a tag handed off again, or dropped, in the time this round trip took is not
        // something to write clearance data onto after the fact.
        if (!fdr.IsTrackedByMe)
            return;

        var changed = false;

        if (flight.CflLower is { } lower && fdr.CFLLower != lower)
        {
            fdr.CFLLower = lower;
            changed = true;
        }

        if (flight.CflUpper is { } upper && fdr.CFLUpper != upper)
        {
            fdr.CFLUpper = upper;
            changed = true;
        }

        if (!changed)
            return;

        ActionLog.Log("Tag", $"Applied clearance data to {fdr.Callsign} from OzServer (CFL {flight.CflLower?.ToString() ?? "-"}/{flight.CflUpper?.ToString() ?? "-"})", new
        {
            action = "handoff_cfl_applied",
            fdr_callsign = fdr.Callsign,
            cfl_lower = flight.CflLower,
            cfl_upper = flight.CflUpper
        });

        _fdrSync.PushNow(fdr);
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
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
