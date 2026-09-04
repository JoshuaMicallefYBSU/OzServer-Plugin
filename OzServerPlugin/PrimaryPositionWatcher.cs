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

    // Every callsign online at all - observers included, and regardless of IsRealATC. Presence, not
    // eligibility: this is what decides whether somebody is *new*.
    HashSet<string> _onlineCallsigns = new(StringComparer.OrdinalIgnoreCase);
    // Controllers already treated as arrived, so one logon produces one handover however many times
    // the online list is republished. Cleared per callsign when they actually leave, so a genuine
    // reconnect is a fresh arrival.
    readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    // Everyone visible during this window counts as already-online rather than newly arrived.
    //
    // One snapshot at connect is not enough on its own: the online list arrives progressively, and
    // IsRealATC settles later still, so a controller who was on the network long before this session
    // can first become visible several seconds in. That is exactly what announced ML_APP as having
    // "logged on" to a controller who connected after them.
    DateTime _settleUntil = DateTime.MinValue;
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

    // Long enough for the network's ATC list and its IsRealATC flags to settle after connecting,
    // short enough that a genuine logon moments later is still caught.
    static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(20);

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
        _handled.Clear();
        _hasBaseline = false;
        _settleUntil = DateTime.MinValue;

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

        // Presence is read from the raw list, eligibility from IsRealATC - and the two must not be
        // conflated, which is the bug this replaced.
        //
        // IsRealATC reads false for a genuine controller for some seconds after they connect, and
        // can drop out again later. Deriving "who is online" from the filtered list therefore had
        // controllers repeatedly vanishing and reappearing, and every reappearance looked like a
        // fresh logon: SY_TWR was announced twice ten minutes apart, and ML_APP was announced as
        // having "logged on" to a controller who connected *after* them. Each false arrival tried to
        // relinquish sectors that were never theirs to give, which is where the "Only the current
        // owner may release this sector" errors came from.
        var raw = Network.GetOnlineATCs ?? new List<NetworkATC>();
        var present = new HashSet<string>(
            raw.Where(a => !string.IsNullOrEmpty(a.Callsign)).Select(a => a.Callsign),
            StringComparer.OrdinalIgnoreCase);

        // Gone entirely - not merely filtered out - so a later reconnect counts as a new arrival.
        _handled.RemoveWhere(callsign => !present.Contains(callsign));

        if (!_hasBaseline)
        {
            var alreadyOnline = UnhandledOnlineRealAtcs();
            MarkHandled(alreadyOnline);
            EnforceAlreadyOnline(alreadyOnline);

            _onlineCallsigns = present;
            _hasBaseline = true;
            _settleUntil = DateTime.UtcNow + SettleWindow;
            return;
        }

        // Still settling: record who is there as already-online, but still enforce the sector
        // boundary locally. Otherwise an enroute controller who connects after ML_APP can keep MAE
        // in vatSys's own MMI window until some later ownership event happens to touch it.
        if (DateTime.UtcNow < _settleUntil)
        {
            var alreadyOnline = UnhandledOnlineRealAtcs();
            MarkHandled(alreadyOnline);
            EnforceAlreadyOnline(alreadyOnline);

            _onlineCallsigns = present;
            return;
        }

        // Gone since we last looked, read from raw presence for the same reason arrivals are: an
        // IsRealATC flicker must not read as somebody leaving, or this would take back airspace
        // from a controller who never went anywhere.
        //
        // Handled before arrivals so that one controller replacing another on the same group
        // settles in the right order.
        var departed = _onlineCallsigns.Where(callsign => !present.Contains(callsign)).ToList();

        if (departed.Count > 0)
            TakeBackCoverFrom(departed);

        // Arrived means: a real controller, not already handled, and not present under any guise
        // when we last looked. The last clause is what a flag flicker cannot fake - they were
        // already there.
        var arrived = PrimaryPosition.OnlineRealAtcs()
            .Where(a => !_handled.Contains(a.Callsign) && !_onlineCallsigns.Contains(a.Callsign))
            .ToList();

        MarkHandled(arrived);

        _onlineCallsigns = present;

        // Logged because this whole path used to be invisible: when a primary logged on and their
        // group was not handed back, nothing anywhere said whether this client had even noticed
        // them. Note the list this is derived from is filtered by IsRealATC, which lags a genuine
        // controller's connect by some seconds - so an arrival can legitimately be seen a poll or
        // two after the logon, and if it is never seen at all that is the thing to look at first.
        if (arrived.Count > 0)
            ActionLog.Log("Primary", $"ATC arrived: {string.Join(", ", arrived.Select(a => a.Callsign))}");

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

    // The mirror of a primary logging on. While an approach controller is online their sectors are
    // withheld from the enroute controller who covers them top-down (see
    // PrimaryPosition.StaffedCoveredSectors); when they log off, that cover is theirs again and they
    // should not have to ask for it or wait for a poll to notice.
    //
    // Deliberately not filtered to controllers this session knows anything about: whether a departed
    // callsign matters is decided by whether it names a sector inside one of this controller's own
    // groups, which the tracker works out.
    void TakeBackCoverFrom(List<string> departed)
    {
        var freed = _tracker.Owned
            .SelectMany(PrimaryPosition.CoveredBy)
            .Where(covered => departed.Any(callsign =>
                string.Equals(callsign, covered.Callsign, StringComparison.OrdinalIgnoreCase)))
            .Select(covered => covered.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (freed.Count == 0)
            return;

        ActionLog.Log("Primary",
            $"ATC left: {string.Join(", ", departed)} - taking back top-down cover of {string.Join(", ", freed)}");

        _ = _tracker.ReclaimTopDownCoverAsync(freed);
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

    async Task HandleArrivalAsync(NetworkATC atc, bool notify = true)
    {
        if (!Network.IsConnected)
            return;

        // Owned, not MMI.SectorsControlled: OzServer's record is what another controller's claim
        // actually collides with, and it is the thing that has to stop saying "mine". Releasing it
        // pulls the sector out of MMI and drops its VSCS line back to Idle on its own, through
        // OzServerOwnershipTracker.ReconcileMmiWithOwned.
        // This session's own active position is never given away, but top-down cover that somebody
        // else is logged on for is not active ownership. For BLA while ML_APP is online, MAE is in
        // BLA's normal dataset group but must still be removed from BLA's current session.
        var mine = PrimaryPosition.DefaultSectorsForCurrentSession(Network.Me?.Callsign);

        var owned = _tracker.Owned;
        var theirs = PrimaryPosition.DefaultSectorsFor(atc.Callsign);
        var locallyRemoved = RemoveLocalCover(theirs, mine, atc.Callsign);
        var relinquishing = theirs
            .Where(s => owned.Any(o => !o.IsDummy && o.Name == s.Name))
            .Where(s => !mine.Any(m => m.Name == s.Name))
            .ToList();

        // Both outcomes are logged. "Decided to relinquish nothing" and "never ran at all" look
        // identical from the outside, and they have completely different causes.
        if (relinquishing.Count == 0)
        {
            if (notify || locallyRemoved.Count > 0)
                ActionLog.Log("Primary",
                    $"{atc.Callsign} {(notify ? "arrived" : "already online")} - nothing of theirs is owned here "
                    + $"(their group: {(theirs.Count == 0 ? "none resolved" : string.Join(", ", theirs.Select(s => s.Name)))})");
            return;
        }

        ActionLog.Log("Primary",
            $"Relinquishing to {atc.Callsign}: {string.Join(", ", relinquishing.Select(s => s.Name))}");

        // Shown before the releases rather than after: each one is a round trip, and the controller
        // should see why their sectors are about to disappear as it happens rather than several
        // seconds later. A release that then fails is reported by ReleaseAsync into the same error
        // log everything else here uses.
        if (notify)
            ShowNotice(atc, relinquishing);

        // Re-checked before each call because the backend cascades: releaseGroup releases the named
        // sector *and* everything it covers, so releasing MAE also releases MDN, MDS, MAV and MAW.
        // Walking the list blindly then asked to release four sectors this controller had already
        // given up, and each answered "Only the current owner may release this sector" - four
        // alarming errors for an operation that had in fact completely succeeded.
        foreach (var sector in relinquishing)
        {
            if (!_tracker.Owned.Any(o => o.Name == sector.Name))
                continue;

            await _tracker.ReleaseAsync(sector);
        }
    }

    List<NetworkATC> UnhandledOnlineRealAtcs() =>
        PrimaryPosition.OnlineRealAtcs()
            .Where(atc => !string.Equals(atc.Callsign, Network.Me?.Callsign, StringComparison.OrdinalIgnoreCase))
            .Where(atc => !_handled.Contains(atc.Callsign))
            .ToList();

    void MarkHandled(IEnumerable<NetworkATC> atcs)
    {
        foreach (var atc in atcs)
            _handled.Add(atc.Callsign);
    }

    void EnforceAlreadyOnline(List<NetworkATC> atcs)
    {
        if (atcs.Count == 0)
            return;

        foreach (var atc in atcs)
            _ = HandleArrivalAsync(atc, notify: false);
    }

    static List<SectorsVolumes.Sector> RemoveLocalCover(
        List<SectorsVolumes.Sector> theirs,
        List<SectorsVolumes.Sector> mine,
        string callsign)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            return (List<SectorsVolumes.Sector>)mainForm.Invoke(
                new Func<List<SectorsVolumes.Sector>>(() => RemoveLocalCover(theirs, mine, callsign)));

        var removable = theirs
            .Where(s => !mine.Any(m => m.Name == s.Name))
            .ToList();
        if (removable.Count == 0)
            return new List<SectorsVolumes.Sector>();

        var current = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var removed = current
            .Where(s => removable.Any(r => r.Name == s.Name))
            .ToList();
        if (removed.Count == 0)
            return removed;

        var remaining = current
            .Where(s => !removed.Any(r => r.Name == s.Name))
            .ToList();

        MMI.SetControlledSectors(remaining);

        foreach (var removedSector in removed)
        {
            foreach (var frequency in Audio.VSCSFrequencies.Where(f =>
                string.Equals(f.Name, removedSector.Callsign, StringComparison.OrdinalIgnoreCase)))
            {
                frequency.Transmit = false;
            }
        }

        ActionLog.Log("Primary",
            $"Removed local top-down cover for {callsign}: {string.Join(", ", removed.Select(s => s.Name).OrderBy(s => s))}");

        return removed;
    }

    static void ShowNotice(NetworkATC atc, List<SectorsVolumes.Sector> relinquishing)
    {
        var who = string.IsNullOrEmpty(atc.RealName) ? atc.Callsign : $"{atc.Callsign} ({atc.RealName})";

        // Full description, callsign included - the same way every other list in the plugin writes a
        // sector (SectorDescription). "ARA" on its own does not tell a controller which airspace is
        // leaving them.
        //
        // No leading indent: the message pane centres its text (see SectorMessageWindow), so spaces
        // on the left do not indent the list, they shift it off centre.
        var list = string.Join(Environment.NewLine, relinquishing.Select(s => SectorDescription.Describe(s)));
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
        var notice = new SectorNoticeWindow(message, "Position relinquished");
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
