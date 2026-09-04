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
//   2. from the very controller who had that sector a moment ago (fdr.HandoffController),
//   3. within a short window of gaining it.
//
// Anything else is somebody's deliberate handoff and is left to flash. The window matters because
// the two clients act on their own poll ticks, so the handoff can arrive either side of this
// client noticing the ownership change - see PendingTransfer.
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
    // rather than on every FDR update for the whole time it flashes.
    readonly HashSet<string> _reportedUnmatched = new(StringComparer.OrdinalIgnoreCase);

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
        Network.Disconnected += (_, _) => { lock (_lock) _incoming.Clear(); _reportedUnmatched.Clear(); };
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
        if (string.IsNullOrEmpty(fdr.Callsign) || fdr.State != FDP2.FDR.FDRStates.STATE_HANDOVER_FIRST)
            return;

        // Deliberately not an early return when this is missing. A handoff with no HandoffController
        // and a handoff from the wrong controller both end up flashing, and telling them apart
        // afterwards is the whole point of the diagnostic below.
        var from = fdr.HandoffController?.Callsign;

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
            if (_reportedUnmatched.Add(fdr.Callsign))
            {
                string expecting;
                lock (_lock)
                    expecting = _incoming.Count == 0
                        ? "none"
                        : string.Join(", ", _incoming.Select(p => $"{p.Sector.Name} from {p.FromCallsign}"));

                var inside = SectorLocator.Resolve(fdr, MMI.SectorsControlled.Where(s => !s.IsDummy))?.Name ?? "nowhere of ours";
                ActionLog.Log("Tag",
                    $"{fdr.Callsign} flashing from {from ?? "(no HandoffController)"}, left to accept by hand "
                    + $"(resolves to {inside}; expecting transfers: {expecting})");
            }

            return;
        }

        _reportedUnmatched.Remove(fdr.Callsign);

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
