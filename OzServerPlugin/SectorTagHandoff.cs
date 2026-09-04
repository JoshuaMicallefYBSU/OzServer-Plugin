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
//                  own handoff, travelling over vatSys's own protocol; nothing is invented here.
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
public class SectorTagHandoff
{
    // How long after gaining a sector an incoming handoff for it still counts as part of that
    // transfer. Generous, because the losing client only acts when its own poll notices the change,
    // and the two clients are not synchronised. Too short and the tag flashes instead of landing;
    // too long and a genuine manual handoff from the same controller is swallowed - a minute is
    // well past the former and nowhere near the traffic pattern of the latter.
    static readonly TimeSpan TransferWindow = TimeSpan.FromSeconds(60);

    readonly OzServerOwnershipTracker _tracker;
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
        public DateTime Until { get; }
    }

    public SectorTagHandoff(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _tracker.OwnershipChanged += (_, diff) => RunOnUiThread(() => OnOwnershipChanged(diff));
        Network.Disconnected += (_, _) =>
        {
            lock (_lock)
            {
                _incoming.Clear();
                _reportedUnmatched.Clear();
                _accepting.Clear();
            }
        };
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
                _incoming.Add(new PendingTransfer(transfer.Sector, from));

            ActionLog.Log("Tag", $"Gained {transfer.Sector.Name} from {from} - expecting its tags");

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

        foreach (var fdr in FDP2.GetFDRs.ToList())
        {
            if (string.IsNullOrEmpty(fdr.Callsign) || !fdr.IsTrackedByMe)
                continue;

            // Geographically inside the sector that changed hands, by live position - the same test
            // FdrSync uses to report a flight's sector, restricted here to the one sector involved.
            if (SectorLocator.Resolve(fdr, new[] { transfer.Sector }) == null)
                continue;

            MMI.HandoffJurisdiction(fdr, toSector);
            moved.Add(fdr.Callsign);
        }

        ActionLog.Log("Tag", moved.Count > 0
            ? $"{transfer.Sector.Name} went to {toCallsign} - handed over {string.Join(", ", moved)}"
            : $"{transfer.Sector.Name} went to {toCallsign} - no tags of ours inside it");
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

        // The controller who handed it to us. Deliberately not an early return when this is missing:
        // a handoff with no ControllerTracking and a handoff from the wrong controller both end up
        // flashing, and telling them apart afterwards is the whole point of the diagnostic below.
        var from = fdr.ControllerTracking?.Callsign;

        PendingTransfer? match = null;

        lock (_lock)
        {
            _incoming.RemoveAll(pending => pending.Until < DateTime.UtcNow);

            match = string.IsNullOrEmpty(from)
                ? null
                : _incoming.FirstOrDefault(pending =>
                    string.Equals(pending.FromCallsign, from, StringComparison.OrdinalIgnoreCase)
                    && SectorLocator.Resolve(fdr, new[] { pending.Sector }) != null);
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
                    + $"(resolves to {inside}; expecting transfers: {expecting})");
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

    // The accept itself, split out only so the in-flight guard above has a body to wrap.
    void AcceptTransfer(FDP2.FDR fdr, PendingTransfer match, string? from)
    {
        // Resolved against what this session actually holds now, rather than assuming the
        // transferred sector: the aircraft may sit in a sub-sector of it.
        var mine = SectorLocator.Resolve(fdr, MMI.SectorsControlled.Where(s => !s.IsDummy))
                   ?? match.Sector;

        MMI.AcceptJurisdiction(fdr);

        if (fdr.State != FDP2.FDR.FDRStates.STATE_CONTROLLED)
        {
            ActionLog.Log("Tag", $"Could not accept transferred {fdr.Callsign} (state {fdr.State})");
            return;
        }

        // Reasserted after the accept: vatSys resolves jurisdiction against its own default
        // geometry, which knows nothing about OzServer subsectors.
        fdr.ControllingSector = mine;

        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);

        ActionLog.Log("Tag", $"Accepted {fdr.Callsign} with {match.Sector.Name} from {from}, no flash");
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
