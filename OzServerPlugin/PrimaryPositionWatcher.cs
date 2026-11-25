using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Gives a position's sectors back to a controller who was already online on it.
//
// A sector belongs to whoever is logged on as it. Until then anyone may extend into it, so a
// controller can hold sectors only because nobody was on them. This watches
// Network.OnlineATCChanged for real ATC coming and going:
//
//   - A controller found already online when this session joins keeps their position: the overlap
//     with what this session owns is released.
//   - A controller who arrives during this session is handed nothing. They request their position
//     and this controller accepts it (see HandleArrivalAsync), so the airspace and the aircraft in
//     it move only when the controller working them agrees.
//   - A controller who leaves frees the top-down cover withheld for them, which is taken back.
//
// Constructed unconditionally by Plugin: a controller who never opens the Sectors window still has
// to give a position back.
public class PrimaryPositionWatcher
{
    readonly OzServerOwnershipTracker _tracker;

    // Every callsign online, observers included and regardless of IsRealATC. Presence, not
    // eligibility: this decides whether somebody is new.
    HashSet<string> _onlineCallsigns = new(StringComparer.OrdinalIgnoreCase);
    // Controllers already treated as arrived, so one logon is handled once however often the
    // online list is republished. Cleared per callsign when they leave, so a reconnect is a fresh
    // arrival.
    readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    // Everyone first seen before this time counts as already online. One snapshot at connect is not
    // enough: the online list arrives progressively and IsRealATC settles later still, so a
    // controller who was on long before this session can first become visible seconds in.
    DateTime _settleUntil = DateTime.MinValue;
    // False until the first update after connecting. Whoever is online then was there first: they
    // are not logons, and their positions are enforced as already-online (EnforceAlreadyOnline).
    bool _hasBaseline;
    // Arrivals are handled one at a time: a second burst must not start a parallel pass.
    bool _handling;
    readonly Queue<NetworkATC> _pending = new();
    // Guards the two above. Starting or stopping the loop has to be atomic with the test of the
    // queue, or an arrival enqueued just as the loop decides to stop starts no new loop and is
    // never handled.
    readonly object _pendingGate = new();

    // Long enough for the network's ATC list and its IsRealATC flags to settle after connecting,
    // short enough that a logon moments later is still caught.
    static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(20);

    // The longest an already-online check waits for the resume: its 5 s wait for Network.Me plus
    // the request timeout.
    static readonly TimeSpan ResumeWait = TimeSpan.FromSeconds(30);

    public PrimaryPositionWatcher(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;

        Network.OnlineATCChanged += (_, _) => RunOnUiThread(OnOnlineAtcChanged);
        // A new session gets a new baseline: everyone on the online list it joins was there first.
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
        if (OzServerMode.Disabled)
            return;

        if (!Network.IsConnected)
        {
            ResetBaseline();
            return;
        }

        // Presence is read from the raw list, eligibility from IsRealATC. IsRealATC reads false for
        // a real controller for some seconds after they connect and can drop out again later, so
        // deriving "who is online" from the filtered list would make every flicker look like a
        // fresh logon.
        var raw = Network.GetOnlineATCs ?? new List<NetworkATC>();
        var present = new HashSet<string>(
            raw.Where(a => !string.IsNullOrEmpty(a.Callsign)).Select(a => a.Callsign),
            StringComparer.OrdinalIgnoreCase);

        // Gone entirely, not merely filtered out, so a later reconnect counts as a new arrival.
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

        // Still settling: whoever appears is recorded as already online and their position is
        // enforced, so an enroute controller who connects after ML_APP does not keep MAE in MMI.
        if (DateTime.UtcNow < _settleUntil)
        {
            var alreadyOnline = UnhandledOnlineRealAtcs();
            MarkHandled(alreadyOnline);
            EnforceAlreadyOnline(alreadyOnline);

            _onlineCallsigns = present;
            return;
        }

        // Departures are read from raw presence too: an IsRealATC flicker must not read as somebody
        // leaving, or cover would be taken back from a controller who is still online. Handled
        // before arrivals so one controller replacing another on the same group settles in order.
        var departed = _onlineCallsigns.Where(callsign => !present.Contains(callsign)).ToList();

        if (departed.Count > 0)
            TakeBackCoverFrom(departed);

        // Arrived: a real controller, not already handled, and not present in any form at the last
        // update - the clause a flag flicker cannot satisfy.
        var arrived = PrimaryPosition.OnlineRealAtcs()
            .Where(a => !_handled.Contains(a.Callsign) && !_onlineCallsigns.Contains(a.Callsign))
            .ToList();

        MarkHandled(arrived);

        _onlineCallsigns = present;

        // Logged so that whether this client noticed a logon can be traced. The list is filtered by
        // IsRealATC, which lags a real connect by some seconds, so an arrival may be seen an update
        // or two after the logon.
        if (arrived.Count > 0)
            ActionLog.Log("Primary", $"ATC arrived: {string.Join(", ", arrived.Select(a => a.Callsign))}");

        var start = false;
        lock (_pendingGate)
        {
            foreach (var atc in arrived)
            {
                // Network.Me appearing in the online list is this session's own logon, not an
                // arrival.
                if (string.Equals(atc.Callsign, Network.Me?.Callsign, StringComparison.OrdinalIgnoreCase))
                    continue;

                _pending.Enqueue(atc);
            }

            // Enqueued and the loop started under one lock - see _pendingGate.
            if (_pending.Count > 0 && !_handling)
            {
                _handling = true;
                start = true;
            }
        }

        if (start)
            _ = RunHandoverLoopAsync();
    }

    // The mirror of a controller logging on. While an approach controller is online their sectors
    // are withheld from the enroute controller who covers them top-down (see
    // PrimaryPosition.StaffedCoveredSectors); when they log off, the cover is claimed back without
    // the enroute controller having to ask.
    //
    // Not filtered to known controllers: a departed callsign matters only if it names a sector
    // covered by one this session owns.
    void TakeBackCoverFrom(List<string> departed)
    {
        if (OzServerMode.Disabled)
            return;

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
            $"ATC left: {string.Join(", ", departed)} - taking back top-down cover of {string.Join(", ", freed)}",
            new
            {
                action = "take_back_top_down_cover",
                departed = departed.ToArray(),
                sectors = freed.ToArray()
            });

        _ = _tracker.ReclaimTopDownCoverAsync(freed);
    }

    async Task RunHandoverLoopAsync()
    {
        if (OzServerMode.Disabled)
            return;

        try
        {
            while (true)
            {
                NetworkATC next;
                // Test-and-clear as one atomic step - see _pendingGate.
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
            // Fire-and-forget from the event handler, so nothing may escape as an unobserved task
            // exception or leave _handling stuck true and every later logon ignored.
            OzErrors.Add(new Exception($"Couldn't relinquish sectors to a primary position: {ex.Message}", ex), "OzServer");

            lock (_pendingGate)
                _handling = false;
        }
    }

    async Task HandleArrivalAsync(NetworkATC atc, bool notify = true)
    {
        if (OzServerMode.Disabled)
            return;

        if (!Network.IsConnected)
            return;

        // This session's own active position is never given away, but top-down cover whose own
        // controller is logged on is not part of it: for BLA while ML_APP is online, MAE is in
        // BLA's dataset group but is not kept.
        var mine = PrimaryPosition.DefaultSectorsForCurrentSession(Network.Me?.Callsign);

        // Owned, not MMI.SectorsControlled: OzServer's record is what another controller's claim
        // collides with.
        var owned = _tracker.Owned;
        var theirs = PrimaryPosition.DefaultSectorsFor(atc.Callsign);

        // A controller arriving on a position this session holds is not handed it. Their client
        // requests it (OzServerOwnershipTracker.HandleConflictAsync) and it is given from the
        // request window here. Releasing on arrival would move the airspace with no say from the
        // controller working it, and removing the sector from MMI drops every aircraft tracked
        // inside it: vatSys stops tracking any tag whose controlling sector leaves the list
        // (MMI.SetControlledSectors).
        //
        // Someone found already online when this session joins (notify == false) is still
        // enforced below: those sectors were theirs before this session existed.
        if (notify)
        {
            var held = theirs.Where(s => owned.Any(o => !o.IsDummy && o.Name == s.Name)).Select(s => s.Name).ToList();
            ActionLog.Log("Primary",
                held.Count == 0
                    ? $"{atc.Callsign} arrived - nothing of theirs is owned here"
                    : $"{atc.Callsign} arrived - keeping {string.Join(", ", held)} until they request it",
                new
                {
                    action = held.Count == 0 ? "arrival_no_owned_sectors" : "arrival_keep_until_requested",
                    controller_callsign = atc.Callsign,
                    their_group = theirs.Select(s => s.Name).ToArray(),
                    held = held.ToArray()
                });
            return;
        }

        // Only once the resume has answered, so the previous session's sectors can be told from what
        // this session's own logon claim took (see OzServerOwnershipTracker._carriedOver). Bounded:
        // a resume that never answers must not leave the position unenforced for good.
        var settled = _tracker.WhenResumeSettled;
        if (!settled.IsCompleted)
            await Task.WhenAny(settled, Task.Delay(ResumeWait));
        if (!Network.IsConnected)
            return;

        // Re-read after the wait: the resume may have changed both.
        mine = PrimaryPosition.DefaultSectorsForCurrentSession(Network.Me?.Callsign);
        owned = _tracker.Owned;

        // A sector this controller already held before connecting stays theirs: the controller
        // online on it did not take it while they were away (OzServer would no longer list it), so
        // they had handed it over or left it. BN-TRT_CTR handed TRT to BN-TRS_CTR; when BN-TRS_CTR
        // reconnected, the resume gave TRT back and this then released it to BN-TRT_CTR.
        var carriedOver = theirs
            .Where(s => owned.Any(o => !o.IsDummy && o.Name == s.Name) && _tracker.IsCarriedOver(s))
            .Where(s => !mine.Any(m => m.Name == s.Name))
            .ToList();
        if (carriedOver.Count > 0)
        {
            ActionLog.Log("Primary",
                $"{atc.Callsign} already online - keeping {string.Join(", ", carriedOver.Select(s => s.Name))}, held before connecting",
                new
                {
                    action = "already_online_keep_carried_over",
                    controller_callsign = atc.Callsign,
                    sectors = carriedOver.Select(s => s.Name).ToArray(),
                    their_group = theirs.Select(s => s.Name).ToArray()
                });
        }

        var keep = mine.Concat(carriedOver).ToList();
        var locallyRemoved = RemoveLocalCover(theirs, keep, atc.Callsign);
        var relinquishing = theirs
            .Where(s => owned.Any(o => !o.IsDummy && o.Name == s.Name))
            .Where(s => !keep.Any(m => m.Name == s.Name))
            .ToList();

        // Both outcomes are logged: "decided to relinquish nothing" and "never ran" otherwise look
        // the same from outside.
        if (relinquishing.Count == 0)
        {
            if (notify || locallyRemoved.Count > 0)
                ActionLog.Log("Primary",
                    $"{atc.Callsign} {(notify ? "arrived" : "already online")} - nothing of theirs is owned here "
                    + $"(their group: {(theirs.Count == 0 ? "none resolved" : string.Join(", ", theirs.Select(s => s.Name)))})",
                    new
                    {
                        action = notify ? "arrival_no_owned_sectors" : "already_online_no_owned_sectors",
                        controller_callsign = atc.Callsign,
                        their_group = theirs.Select(s => s.Name).ToArray(),
                        locally_removed = locallyRemoved.Select(s => s.Name).ToArray()
                    });
            return;
        }

        ActionLog.Log("Primary",
            $"Relinquishing to {atc.Callsign}: {string.Join(", ", relinquishing.Select(s => s.Name))}",
            new
            {
                action = notify ? "relinquish_to_arrival" : "relinquish_to_already_online",
                controller_callsign = atc.Callsign,
                sectors = relinquishing.Select(s => s.Name).ToArray(),
                their_group = theirs.Select(s => s.Name).ToArray(),
                locally_removed = locallyRemoved.Select(s => s.Name).ToArray()
            });

        // All of them are excused from RestoreOwnedToMmi before the first release goes out.
        // RemoveLocalCover has already taken them off the scope, and the refresh after each release
        // would otherwise put back the ones still in Owned waiting their turn.
        _tracker.ExpectReleaseOf(relinquishing);

        foreach (var sector in relinquishing)
        {
            // Re-checked before each call: the backend's releaseGroup releases the named sector
            // and everything it covers (MAE takes MDN, MDS, MAV and MAW), and releasing a sector
            // that has already gone is refused as "not the current owner".
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
        if (OzServerMode.Disabled)
            return;

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
            $"Removed local top-down cover for {callsign}: {string.Join(", ", removed.Select(s => s.Name).OrderBy(s => s))}",
            new
            {
                action = "remove_local_top_down_cover",
                controller_callsign = callsign,
                sectors = removed.Select(s => s.Name).OrderBy(s => s).ToArray(),
                remaining = remaining.Select(s => s.Name).OrderBy(s => s).ToArray()
            });

        return removed;
    }

    // Network's events can arrive on a background thread, and everything here touches MMI, VSCS or
    // a window.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
