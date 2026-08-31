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
public class TagOwnershipSync
{
    static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    readonly OzServerOwnershipTracker _tracker;
    readonly System.Threading.Timer _sweepTimer;

    public TagOwnershipSync(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _tracker.OwnershipChanged += (_, diff) => RunOnUiThread(() => OnOwnershipChanged(diff));

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

    // Trigger (a) - see the class comment. Activates and/or assumes fdr if it's physically sitting,
    // right now, in a subsector this controller currently owns on OzServer. Covers both a flight
    // plan that hasn't been activated yet and a tag nobody currently holds - the same two cases the
    // native Activate/Accept context-menu items handle by hand.
    void EvaluatePickup(FDP2.FDR fdr)
    {
        if (!Network.IsConnected || string.IsNullOrEmpty(fdr.Callsign))
            return;

        var sector = ResolveSector(fdr);
        if (sector == null)
            return;

        var owner = _tracker.OwnerOf(sector);
        if (owner == null || !int.TryParse(Network.ControllerId, out var myCid) || owner.Cid != myCid)
            return;

        if (!fdr.ESTed)
            RunOnUiThread(() => MMI.EstFDR(fdr));

        // MMI.AcceptJurisdiction already no-ops below STATE_COORDINATED (and off invalid ATC, or no
        // sectors controlled at all) - checked here too so this reads as the same eligibility EstFDR
        // just established, not a call made on faith that it did.
        if (!fdr.IsTracked && fdr.State > FDP2.FDR.FDRStates.STATE_COORDINATED)
            RunOnUiThread(() => MMI.AcceptJurisdiction(fdr));
    }

    // Trigger (b) - see the class comment. Every flight this controller is tracking that was
    // physically sitting in a sector OzServer just took away is handed off to whoever it went to.
    // MMI.HandoffJurisdiction(fdr, to) itself decides whether that means a real network handoff (the
    // new owner is online under that exact subsector callsign) or just dropping the track (a
    // covering primary claimed it instead, or nobody is online under that callsign at all) - see its
    // own comment. Either way, the new owner's own Trigger (a) mops up the result.
    void OnOwnershipChanged(SectorOwnershipDiff diff)
    {
        if (!Network.IsConnected || diff.Lost.Count == 0)
            return;

        foreach (var lost in diff.Lost)
        {
            foreach (var fdr in FDP2.GetFDRs.Where(f => f.IsTrackedByMe))
            {
                if (ResolveSector(fdr)?.Equals(lost) == true)
                    MMI.HandoffJurisdiction(fdr, lost);
            }
        }
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
