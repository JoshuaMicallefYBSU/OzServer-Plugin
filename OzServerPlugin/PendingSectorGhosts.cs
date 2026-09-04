using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shows, before anything is committed, which aircraft a sector request would actually bring with
// it - drawn as Ghost Jurisdiction, the state vatSys already uses for "in your airspace, not yours
// to work".
//
// Live while a sector is staged in the Sectors window, and while a request for it is still open.
// Both are the same question - "what am I asking for?" - and it stops the moment the answer is
// known: accepted, and the aircraft arrive for real (SectorTagHandoff); rejected, cancelled or
// unstaged, and they were never coming.
//
// Ghosted aircraft are exactly the ones that would transfer, no more: inside the sector, and held
// by the controller who owns it. A tag a third controller is working would not move, so showing it
// as incoming would be a lie - see SectorTagHandoff for the same rule on the transferring side.
//
// ---------------------------------------------------------------------------------------------
// Why this paints the state itself
//
// vatSys computes Ghost in MMI.SetTrackState, but only inside a branch guarded by
// MMI.SectorsControlled.Contains(fdr.ControllingSector) - within it, IsTrackedByMe picks
// Jurisdiction over GhostJurisdiction. A staged sector is deliberately not in SectorsControlled
// (putting it there would claim it, which is the entire thing the controller has not decided to do
// yet), so that branch is unreachable and the state has to be written directly.
//
// Which means vatSys will recompute over it on its next update for that track, so it is reasserted
// - on the FDR and radar callbacks that follow such a recompute, and on a timer for the frames in
// between. Nothing here changes an FDR: only Track.State, which is presentation.
public class PendingSectorGhosts
{
    // Short, because this fights vatSys's own recompute and a slow correction reads as a flicker.
    static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(1);

    readonly OzServerOwnershipTracker _tracker;
    readonly System.Threading.Timer _timer;
    readonly object _lock = new();

    // Sectors staged in the window right now. Pushed in by the window, which owns that state.
    List<SectorsVolumes.Sector> _staged = new();

    // Callsigns currently painted by us, so they can be handed back when they stop qualifying
    // rather than left ghosted forever.
    readonly HashSet<string> _painted = new(StringComparer.OrdinalIgnoreCase);

    public PendingSectorGhosts(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        Network.Disconnected += (_, _) => SetStaged(new List<SectorsVolumes.Sector>());

        _timer = new System.Threading.Timer(_ => RunOnUiThread(Apply), null,
            ReassertInterval, ReassertInterval);
    }

    // Called by the Sectors window whenever staging changes, including being cleared by Cancel or
    // by closing the window.
    public void SetStaged(IEnumerable<SectorsVolumes.Sector> staged)
    {
        lock (_lock)
            _staged = staged.ToList();

        RunOnUiThread(Apply);
    }

    // Called on every FDR and radar update, which is where vatSys has just recomputed the state
    // this needs to overwrite.
    public void Reassert() => Apply();

    void Apply()
    {
        if (!Network.IsConnected)
            return;

        try
        {
            var pending = PendingSectors();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fdr in FDP2.GetFDRs.ToList())
            {
                if (string.IsNullOrEmpty(fdr.Callsign) || !WouldTransfer(fdr, pending))
                    continue;

                wanted.Add(fdr.Callsign);

                if (MMI.FindTrack(fdr) is { } track)
                    track.State = MMI.HMIStates.GhostJurisdiction;
            }

            // Anything we painted that no longer qualifies is handed back to vatSys rather than
            // left as we last set it - the request resolved, or the aircraft left the sector.
            var released = _painted.Where(callsign => !wanted.Contains(callsign)).ToList();
            foreach (var callsign in released)
            {
                var fdr = FDP2.GetFDRs.FirstOrDefault(f =>
                    string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase));

                if (fdr != null && MMI.FindTrack(fdr) is { } track)
                    MMI.SetTrackState(track);
            }

            _painted.Clear();
            foreach (var callsign in wanted)
                _painted.Add(callsign);

            if (wanted.Count > 0 || released.Count > 0)
                MMI.RequestRedraw(true, false, false);
        }
        catch (Exception ex)
        {
            // Presentation only - a failure here must never disturb the traffic picture.
            ActionLog.Log("Ghost", $"could not apply: {ex.Message}");
        }
    }

    // Staged, plus anything already asked for and still waiting on an answer. A request that has
    // been rejected is not pending - the answer is known and it is not coming.
    List<SectorsVolumes.Sector> PendingSectors()
    {
        List<SectorsVolumes.Sector> staged;
        lock (_lock)
            staged = _staged;

        var open = _tracker.MyRequests.ByMe
            .Where(request => request.RejectedAt == null && request.Sector != null)
            .Select(request => SectorsVolumes.Sectors.FirstOrDefault(s =>
                string.Equals(s.Name, request.Sector!.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s != null)
            .Select(s => s!);

        return staged.Concat(open).Distinct().ToList();
    }

    // The transfer rule, matched to SectorTagHandoff: inside one of the pending sectors, and held
    // by the controller who owns that sector.
    bool WouldTransfer(FDP2.FDR fdr, List<SectorsVolumes.Sector> pending)
    {
        // Already ours - nothing is coming to us that we have.
        if (fdr.IsTrackedByMe || fdr.ControllingSector == null)
            return false;

        var inside = SectorLocator.Resolve(fdr, pending);
        if (inside == null)
            return false;

        // Whoever holds the tag has to be the same controller who owns the sector being asked for.
        // A third controller working an aircraft inside it keeps that aircraft, so it is not
        // ghosted - the preview would otherwise promise traffic that will never arrive.
        var holder = _tracker.OwnerOf(fdr.ControllingSector)?.Callsign;
        var owner = _tracker.OwnerOf(inside)?.Callsign;

        return !string.IsNullOrEmpty(holder)
               && string.Equals(holder, owner, StringComparison.OrdinalIgnoreCase);
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
