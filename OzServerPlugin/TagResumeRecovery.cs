using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Puts a controller's tags back after a disconnect, and nothing else.
//
// This is all that remains of the plugin's tag handling. TagOwnershipSync and FdrActivationSync -
// which moved tags between controllers, activated flights from server state, and offered pickups as
// aircraft crossed boundaries - were removed to be rebuilt from scratch. Nothing here decides who
// should hold a tag; it only restores what the backend has already confirmed was this controller's
// moments ago.
//
// What still has to exist for that to work, and why it is not "tag handling":
//
//   FdrSync keeps flight_data_records current, including controlling_cid and current_sector. The
//   resume endpoint is built entirely on those two columns - it hands back only flights that are
//   still uncontrolled and still inside a sector this controller holds - so without that push there
//   is nothing for a resume to restore and disconnect recovery cannot work at all.
//
// The backend has already applied every ownership rule by the time this runs (see
// POST /sectors/resume): a flight another controller picked up while this one was away stays with
// them, and one that has flown out of the airspace they just took back is not returned either. So
// the list arriving here is not a proposal to be re-checked - it is the answer, and this simply
// makes vatSys agree with it.
public class TagResumeRecovery
{
    readonly OzServerOwnershipTracker _tracker;

    public TagResumeRecovery(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _tracker.TagsResumed += (_, callsigns) => RunOnUiThread(() => Restore(callsigns));
    }

    void Restore(IReadOnlyList<string> callsigns)
    {
        if (callsigns.Count == 0)
            return;

        var restored = new List<string>();
        var failed = new List<string>();

        foreach (var fdr in FDP2.GetFDRs.ToList())
        {
            if (string.IsNullOrEmpty(fdr.Callsign)
                || !callsigns.Contains(fdr.Callsign, StringComparer.OrdinalIgnoreCase))
                continue;

            // Resolved against what this controller actually holds right now - the resume above
            // restores their sectors before returning the flights, so by this point MMI has them.
            var sector = SectorLocator.Resolve(fdr, MMI.SectorsControlled.Where(s => !s.IsDummy));
            if (sector == null)
            {
                failed.Add($"{fdr.Callsign} (no sector)");
                continue;
            }

            if (TryTakeJurisdiction(fdr, sector))
                restored.Add(fdr.Callsign);
            else
                failed.Add($"{fdr.Callsign} ({fdr.State})");
        }

        // Both halves reported. A tag that did not come back is the thing worth knowing about, and
        // silence used to be the only signal that anything had gone wrong.
        ActionLog.Log("Tag", restored.Count > 0
            ? $"Restored after reconnect: {string.Join(", ", restored)}"
            : "Resume returned tags but none were restored");

        if (failed.Count > 0)
            ActionLog.Log("Tag", $"Could not restore: {string.Join(", ", failed)}");
    }

    // Takes jurisdiction without the controller ever seeing a handover, by completing the whole
    // transition inside one UI callback and only repainting at the end. These tags were this
    // controller's a moment ago and the backend has confirmed nobody else took them, so making them
    // flash for acceptance would be asking a question that has already been answered.
    //
    // The FDP2.Process call is the part that matters, and the part that took three attempts to get
    // right. HandoffFirst only queues the state change; accepting has nothing to resolve until that
    // has been through a process pass, which is why calling the two back to back left the flight
    // sitting in STATE_HANDOVER_FIRST, flashing.
    //
    // Returns whether it actually worked, checked against the resulting state rather than assumed -
    // two other orderings were tried and each failed in its own way.
    static bool TryTakeJurisdiction(FDP2.FDR fdr, SectorsVolumes.Sector sector)
    {
        FDP2.HandoffFirst(fdr);
        fdr.HandoffSector = sector;

        FDP2.Process(fdr, true);

        MMI.AcceptJurisdiction(fdr);

        if (fdr.State != FDP2.FDR.FDRStates.STATE_CONTROLLED)
            return false;

        // Reasserted after the accept: vatSys resolves jurisdiction against its own default
        // geometry, which knows nothing about OzServer subsectors.
        fdr.ControllingSector = sector;

        // Only now is anything painted, so the intermediate handover state never reaches the screen.
        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);

        return true;
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
