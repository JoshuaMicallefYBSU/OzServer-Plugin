using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shows which aircraft a staged or requested sector would bring with it, drawn as Ghost
// Jurisdiction - the state vatSys uses for "in your airspace, not yours to work".
//
// Live while a sector is staged in the Sectors window and while a request for it is open. An
// aircraft is ghosted only if it would transfer: inside the sector and held by the controller who
// owns it. A tag a third controller is working would not move - SectorTagHandoff applies the same
// rule on the transferring side.
//
// The state is written directly. vatSys computes Ghost in MMI.SetTrackState only inside a branch
// guarded by MMI.SectorsControlled.Contains(fdr.ControllingSector), and a staged sector is not in
// SectorsControlled - putting it there would claim it. vatSys recomputes over the written state on
// its next update for that track, so it is reasserted on the FDR and radar callbacks and on a
// timer. Only Track.State is written, which is presentation; no FDR is changed.
public class PendingSectorGhosts
{
    // Short, because a slow correction after vatSys's recompute reads as a flicker.
    static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(1);

    readonly OzServerOwnershipTracker _tracker;
    readonly System.Threading.Timer _timer;
    readonly object _lock = new();

    // One Apply at a time. vatSys dispatches OnFDRUpdate and OnRadarTrackUpdate through Task.Run
    // with no serialisation, so Reassert is entered from many pool threads at once, alongside the
    // timer. Overlapping passes race on _painted: one enumerates it while another clears it -
    // which invalidates an enumerator even when the set is empty - and the exception aborts the
    // pass, leaving ghosts stuck on.
    //
    // TryEnter and skip rather than lock and queue: the timer repeats a skipped pass within
    // ReassertInterval, and queueing would pile pool threads up behind it.
    readonly object _applyGate = new();

    // Sectors staged in the Sectors window, which owns that state and pushes it in.
    List<SectorsVolumes.Sector> _staged = new();

    // Callsigns painted by this class, so they can be handed back when they stop qualifying.
    readonly HashSet<string> _painted = new(StringComparer.OrdinalIgnoreCase);

    public PendingSectorGhosts(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        Network.Disconnected += (_, _) => SetStaged(new List<SectorsVolumes.Sector>());

        _timer = new System.Threading.Timer(_ => RunOnUiThread(Apply), null,
            ReassertInterval, ReassertInterval);
    }

    // Called by the Sectors window whenever staging changes, including when Cancel or closing the
    // window clears it.
    public void SetStaged(IEnumerable<SectorsVolumes.Sector> staged)
    {
        lock (_lock)
            _staged = staged.ToList();

        RunOnUiThread(Apply);
    }

    // Called on every FDR and radar update, when vatSys has just recomputed the state. Marshalled
    // because Apply writes Track.State, which is vatSys UI state, and these callbacks arrive on
    // pool threads.
    //
    // Posted only while there is something to paint or hand back, and at most one pass waits on
    // the UI thread at a time. Posting one per update queued hundreds of passes a second onto
    // vatSys's UI thread in busy airspace - each forcing a full map redraw while a request was
    // pending - and froze the scope on slower PCs (ML-BLA_CTR and ML-GUN_CTR, 16 Nov).
    public void Reassert()
    {
        if (!_active)
            return;
        if (System.Threading.Interlocked.Exchange(ref _queued, 1) == 1)
            return;
        RunOnUiThread(() =>
        {
            System.Threading.Interlocked.Exchange(ref _queued, 0);
            Apply();
        });
    }

    // Whether the last pass had anything pending or painted. Read by Reassert from pool threads.
    volatile bool _active;
    int _queued;

    void Apply()
    {
        if (!Network.IsConnected)
            return;

        // See _applyGate. RunOnUiThread alone is not enough: it runs inline on the calling thread
        // when there is no MainForm to marshal through.
        if (!System.Threading.Monitor.TryEnter(_applyGate))
            return;

        try
        {
            var pending = PendingSectors();
            // Disabled: nothing is pending, so whatever is painted is handed back to vatSys.
            if (OzServerMode.Disabled)
                pending.Clear();

            // The common case - nothing pending and nothing painted - returns before walking every
            // flight, and turns the per-update reassert off until the timer finds work again.
            _active = pending.Count > 0 || _painted.Count > 0;
            if (!_active)
                return;

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // One snapshot for the whole pass. FDP2.GetFDRs is vatSys's live collection, mutated
            // from its own threads, so it is never enumerated directly.
            var fdrs = FDP2.GetFDRs.ToList();

            foreach (var fdr in fdrs)
            {
                if (string.IsNullOrEmpty(fdr.Callsign) || !WouldTransfer(fdr, pending))
                    continue;

                wanted.Add(fdr.Callsign);

                if (MMI.FindTrack(fdr) is { } track)
                    track.State = MMI.HMIStates.GhostJurisdiction;
            }

            // Anything painted that no longer qualifies is handed back to vatSys: the request
            // resolved, or the aircraft left the sector.
            var released = _painted.Where(callsign => !wanted.Contains(callsign)).ToList();
            foreach (var callsign in released)
            {
                var fdr = fdrs.FirstOrDefault(f =>
                    string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase));

                if (fdr != null && MMI.FindTrack(fdr) is { } track)
                    MMI.SetTrackState(track);
            }

            _painted.Clear();
            foreach (var callsign in wanted)
                _painted.Add(callsign);

            // No redraw request: tracks are drawn from Track.State on every frame of the scope's
            // render loop. A background redraw (RequestRedraw(true)) recomputes every map on every
            // scope, and was being asked for on every radar update.
        }
        catch (Exception ex)
        {
            // Presentation only - a failure here must never disturb the traffic picture.
            ActionLog.Log("Ghost", $"could not apply: {ex.Message}");
        }
        finally
        {
            System.Threading.Monitor.Exit(_applyGate);
        }
    }

    // Staged sectors plus requests still waiting on an answer. A rejected request is not pending.
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

        // Expanded to every sector each one covers, since a claim or transfer acts on the covered
        // sectors and not only the one named. WouldTransfer then narrows by who holds each tag.
        return staged.Concat(open)
            .SelectMany(PrimaryPosition.CoveredBy)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    // The transfer rule, matched to SectorTagHandoff: inside one of the pending sectors and held by
    // the controller who owns that sector.
    bool WouldTransfer(FDP2.FDR fdr, List<SectorsVolumes.Sector> pending)
    {
        // Already tracked by this controller, so it is not incoming.
        if (fdr.IsTrackedByMe || fdr.ControllingSector == null)
            return false;

        var inside = SectorLocator.Resolve(fdr, pending);
        if (inside == null)
            return false;

        // A third controller working an aircraft inside the sector keeps it, so it is not ghosted.
        var holder = _tracker.OwnerOf(fdr.ControllingSector)?.Callsign;
        var owner = _tracker.OwnerOf(inside)?.Callsign;

        return !string.IsNullOrEmpty(holder)
               && string.Equals(holder, owner, StringComparison.OrdinalIgnoreCase);
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
