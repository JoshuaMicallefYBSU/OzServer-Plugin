using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Hands a position's own sectors back when its primary logs on.
//
// A sector belongs to whoever is actually logged in on it. Until then anyone may extend into it -
// AfvSectorClaimer.Init() grants the primary plus its uncovered sub-sectors on connect, and a VSCS
// transmit press extends further - which leaves the ordinary case of a controller holding sectors
// purely because nobody was on them yet. When that position's controller finally connects, those
// sectors have to go back, and OzServer's ownership record has to say so, or the new arrival's own
// claim simply collides with the previous holder's (see OzServerOwnershipTracker.HandleConflictAsync).
//
// This watches Network.OnlineATCChanged for a *newly* arrived real ATC, works out which sectors
// that position takes by default, intersects that with what this controller currently owns, and
// releases the overlap - telling them what happened rather than asking, because the handover is not
// theirs to refuse.
//
// Constructed unconditionally by Plugin, like AfvSectorClaimer and OzServerOwnershipTracker: a
// controller who never opens the OzServer Sectors window still has to give a position back.
public class PrimaryPositionWatcher
{
    readonly OzServerOwnershipTracker _tracker;

    // Callsigns seen online on the previous OnlineATCChanged, to pick out genuinely new arrivals.
    // Compared case-insensitively - a callsign is an identity here, not a string to round-trip.
    HashSet<string> _onlineCallsigns = new(StringComparer.OrdinalIgnoreCase);
    // Whoever is already online at the moment this session joins is not a "logon" - they were there
    // first, and anything of theirs this controller somehow holds is a pre-existing situation
    // rather than something this event just caused. The first update after connecting is therefore
    // recorded as the baseline and acted on no further.
    bool _hasBaseline;
    // Releases are awaited one after another and can outlast the event that started them, so a
    // second burst of arrivals must not start a parallel pass over the same Owned list.
    bool _handling;
    readonly Queue<NetworkATC> _pending = new();
    // Guards the two above. They are written from the UI thread (OnOnlineAtcChanged) and read and
    // written again from wherever RunHandoverLoopAsync's continuations resume - which is not the UI
    // thread: OzServerApiClient awaits with ConfigureAwait(false), so everything after the first
    // await inside ReleaseAsync comes back on a pool thread. Without this, an arrival enqueued on
    // the UI thread could race the loop's own Dequeue, and testing _handling then setting it had
    // the same split-step race OzServerOwnershipTracker documents on _claimGate: a burst arriving
    // between the loop's last Dequeue and its clearing of _handling would start no new loop and be
    // silently dropped - the position never handed back at all.
    readonly object _pendingGate = new();

    public PrimaryPositionWatcher(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;

        Network.OnlineATCChanged += (_, _) => RunOnUiThread(OnOnlineAtcChanged);
        // A new session gets a new baseline: the online list this controller comes back to has
        // nothing to do with the one they left, and every position on it was there before them.
        Network.Disconnected += (_, _) => RunOnUiThread(ResetBaseline);
    }

    void ResetBaseline()
    {
        _onlineCallsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _hasBaseline = false;

        lock (_pendingGate)
            _pending.Clear();
    }

    void OnOnlineAtcChanged()
    {
        if (!Network.IsConnected)
        {
            ResetBaseline();
            return;
        }

        var online = PrimaryPosition.OnlineRealAtcs();
        var current = new HashSet<string>(online.Select(a => a.Callsign), StringComparer.OrdinalIgnoreCase);

        if (!_hasBaseline)
        {
            _onlineCallsigns = current;
            _hasBaseline = true;
            return;
        }

        var arrived = online.Where(a => !_onlineCallsigns.Contains(a.Callsign)).ToList();
        _onlineCallsigns = current;

        var start = false;
        lock (_pendingGate)
        {
            foreach (var atc in arrived)
            {
                // Not this session's own logon: Network.Me appearing in the online list is this
                // controller arriving, and "relinquishing" a position to yourself would release
                // everything AfvSectorClaimer.Init() had just granted.
                if (string.Equals(atc.Callsign, Network.Me?.Callsign, StringComparison.OrdinalIgnoreCase))
                    continue;

                _pending.Enqueue(atc);
            }

            // Claiming the loop and enqueueing under the same lock: otherwise a loop that is about
            // to stop can be observed as still running, and the arrival just queued is never acted on.
            if (_pending.Count > 0 && !_handling)
            {
                _handling = true;
                start = true;
            }
        }

        if (start)
            _ = RunHandoverLoopAsync();
    }

    async Task RunHandoverLoopAsync()
    {
        try
        {
            while (true)
            {
                NetworkATC next;
                // Test-and-clear as one atomic step - see _pendingGate for the race that splitting
                // "is the queue empty" from "stop running" would lose an arrival to.
                lock (_pendingGate)
                {
                    if (_pending.Count == 0)
                    {
                        _handling = false;
                        return;
                    }

                    next = _pending.Dequeue();
                }

                await HandleArrivalAsync(next);
            }
        }
        catch (Exception ex)
        {
            // Deliberately fire-and-forget from the event handler, so nothing may escape as an
            // unobserved task exception or leave _handling stuck true and every later logon ignored.
            Errors.Add(new Exception($"Couldn't relinquish sectors to a primary position: {ex.Message}", ex), "OzServer");

            lock (_pendingGate)
                _handling = false;
        }
    }

    async Task HandleArrivalAsync(NetworkATC atc)
    {
        if (!Network.IsConnected)
            return;

        // Owned, not MMI.SectorsControlled: OzServer's record is what another controller's claim
        // actually collides with, and it is the thing that has to stop saying "mine". Releasing it
        // pulls the sector out of MMI and drops its VSCS line back to Idle on its own, through
        // OzServerOwnershipTracker.ReconcileMmiWithOwned.
        var owned = _tracker.Owned;
        var relinquishing = PrimaryPosition.DefaultSectorsFor(atc.Callsign)
            .Where(s => owned.Any(o => !o.IsDummy && o.Name == s.Name))
            .ToList();

        if (relinquishing.Count == 0)
            return;

        // Shown before the releases rather than after: each one is a round trip, and the controller
        // should see why their sectors are about to disappear as it happens rather than several
        // seconds later. A release that then fails is reported by ReleaseAsync into the same error
        // log everything else here uses.
        ShowNotice(atc, relinquishing);

        foreach (var sector in relinquishing)
            await _tracker.ReleaseAsync(sector);
    }

    static void ShowNotice(NetworkATC atc, List<SectorsVolumes.Sector> relinquishing)
    {
        var who = string.IsNullOrEmpty(atc.RealName) ? atc.Callsign : $"{atc.Callsign} ({atc.RealName})";
        var list = string.Join(Environment.NewLine, relinquishing.Select(s => $"    {s.Name} - {s.FullName}"));
        var lead = relinquishing.Count == 1
            ? "This sector belongs to that position and is being relinquished to them:"
            : "These sectors belong to that position and are being relinquished to them:";

        var message = $"{who} has logged on."
                      + Environment.NewLine + Environment.NewLine
                      + lead
                      + Environment.NewLine + Environment.NewLine
                      + list;

        // Non-modal: this is a notification about something already decided, and ShowDialog would
        // freeze the whole vatSys UI thread until the controller happened to notice and dismiss it.
        var notice = new SectorRelinquishNoticeWindow(message, "Position relinquished");
        if (Application.OpenForms["MainForm"] is Form mainForm)
            notice.Show(mainForm);
        else
            notice.Show();

        notice.BringToFront();
    }

    // Same fire-and-forget marshaling OzServerOwnershipTracker uses: Network's events can arrive on
    // a background thread, and everything here ends up touching MMI, VSCS or a window.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
