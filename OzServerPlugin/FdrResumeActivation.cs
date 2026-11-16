using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// On (re)connecting, brings back the traffic picture OzServer still holds for flights vatSys has not
// activated itself.
//
// After a reconnect, and always after a vatSys restart, every flight plan arrives from the network
// as pre-active: no flight plan track, no auto-couple, nothing on the scope, even for an aircraft
// that was being worked minutes ago. OzServer still has the record (FdrSync keeps it current), so
// for each recent, activated record that is not this controller's own tag:
//
//  - the FDR is activated (EstFDR) with the record's SSR code, ETD and ATD, and no new code;
//  - it is placed where the record last had it, as a floating flight plan track on its route - a
//    position report at that point, so vatSys carries it on along the route from there;
//  - auto-couple is left on, so the tag becomes available when the radar track turns up;
//  - if that position is inside a sector this controller holds, it is offered to them - the
//    flashing first handoff vatSys gives a flight activated into a controller's airspace;
//    otherwise it is activated with no handoff to anyone.
//
// A tag the server has as this controller's own is left to TagResumeRecovery. An FDR vatSys already
// has tracked by someone is left alone, as is the position of one with a coupled track.
//
// Like TagResumeRecovery this retries for a while: flight plans, routes and the sector grant arrive
// over the seconds after connecting, and an FDR with no parsed route has nothing to be placed on.
public class FdrResumeActivation
{
    static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(90);
    // A record older than this is not a picture of now. Matches the backend's FDR_RETAIN_MINUTES.
    static readonly TimeSpan MaxRecordAge = TimeSpan.FromMinutes(15);
    // How far off its route a last position may be and still be placed on it.
    const double RouteToleranceNm = 30;

    // After the connect pass, how often the server's flights are checked again for ones that have
    // since come into view here: aircraft keep entering this client's visibility range all session,
    // and the connect pass only covers the flight plans it has at that moment.
    static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(20);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _retryTimer;
    readonly System.Threading.Timer _scanTimer;
    readonly object _gate = new();
    readonly Dictionary<string, OzServerFdrRecordDto> _pending = new(StringComparer.OrdinalIgnoreCase);
    // Activated (or offered) here this session. Each flight is brought in once: if the controller
    // then puts it back to pre-active, a scan must not re-activate it. Held by reference, not
    // callsign: vatSys deletes a finished FDR after ten minutes and builds a new one when the pilot
    // is seen again, and that new one is a flight to bring in like any other.
    readonly HashSet<FDP2.FDR> _done = new();
    DateTime _deadline = DateTime.MinValue;
    DateTime _connectedAt = DateTime.MaxValue;
    int _session;
    int _scanning;

    public FdrResumeActivation()
    {
        Network.Connected += (_, _) =>
        {
            int session;
            lock (_gate)
            {
                _pending.Clear();
                _done.Clear();
                _connectedAt = DateTime.UtcNow;
                session = ++_session;
            }
            _ = BeginAsync(session);
        };

        Network.Disconnected += (_, _) =>
        {
            ServerFdrSnapshot.Clear();
            lock (_gate)
            {
                _pending.Clear();
                _connectedAt = DateTime.MaxValue;
                _session++;
            }
        };

        _retryTimer = new System.Threading.Timer(_ =>
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                    return;
            }
            RunOnUiThread(Attempt);
        }, null, RetryInterval, RetryInterval);

        _scanTimer = new System.Threading.Timer(_ => _ = ScanAsync(), null, ScanInterval, ScanInterval);
    }

    // The ongoing half: server flights that are activated, not this controller's own tag, and whose
    // flight plan here is still pre-active - typically one that has just come into view. They go
    // through the same pass as the connect-time ones (Attempt). Queues nothing during the connect
    // pass's own retry window, so the two never overlap.
    async Task ScanAsync()
    {
        if (OzServerMode.Disabled)
            return;

        int session;
        bool connectPassRunning;
        lock (_gate)
        {
            if (!Network.IsConnected)
                return;
            session = _session;
            connectPassRunning = DateTime.UtcNow - _connectedAt < RetryWindow;
        }
        if (NetworkServer.Current == null || Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
            return;

        try
        {
            var records = await _api.GetAllFlightsAsync();
            // FdrSync's watchdog reads this too, so it is refreshed even while the connect pass
            // still owns activation.
            ServerFdrSnapshot.Update(records);
            if (connectPassRunning)
                return;

            var myCid = NetworkIdentity.Current?.Cid;
            var cutoff = DateTimeOffset.UtcNow - MaxRecordAge;

            var local = new Dictionary<string, FDP2.FDR>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var fdr in FDP2.GetFDRs)
                    if (!string.IsNullOrEmpty(fdr.Callsign))
                        local[fdr.Callsign] = fdr;
            }
            catch (InvalidOperationException)
            {
                return; // modified mid-copy; the next scan
            }

            var added = new List<string>();
            lock (_gate)
            {
                if (session != _session)
                    return;
                foreach (var record in records)
                {
                    if (string.IsNullOrEmpty(record.Callsign) || record.LastSeenAt < cutoff || !IsActivatedState(record.State)
                        || (myCid != null && record.ControllingCid == myCid)
                        || _pending.ContainsKey(record.Callsign))
                        continue;
                    if (!local.TryGetValue(record.Callsign, out var fdr) || fdr.ESTed || fdr.IsTracked)
                        continue;
                    // Finished here only because it went out of view, and now back in view: brought
                    // back every time that happens. Any other finish (a controller's, a landing)
                    // stands.
                    var backInView = fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED && IsBackInView(fdr);
                    if (fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED && !backInView)
                        continue;
                    if (!backInView && _done.Contains(fdr))
                        continue;
                    _pending[record.Callsign] = record;
                    added.Add(record.Callsign);
                }
                if (added.Count > 0)
                    _deadline = DateTime.UtcNow + RetryWindow;
            }

            if (added.Count == 0)
                return;
            ActionLog.Log("FdrResume", $"Activating {added.Count} flight(s) OzServer has activated, now in view here: {string.Join(", ", added)}",
                new { action = "fdr_scan_begin", callsigns = added });
            RunOnUiThread(Attempt);
        }
        catch (Exception ex)
        {
            ErrorThrottle.Report("fdr-scan", $"Couldn't check OzServer for flights to activate: {ex.Message}", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    async Task BeginAsync(int session)
    {
        if (OzServerMode.Disabled)
            return;

        try
        {
            await Task.Delay(StartDelay);
            if (!Network.IsConnected || NetworkServer.Current == null)
                return;

            var records = await _api.GetAllFlightsAsync();
            var myCid = NetworkIdentity.Current?.Cid;
            var cutoff = DateTimeOffset.UtcNow - MaxRecordAge;

            var wanted = records.Where(r =>
                    !string.IsNullOrEmpty(r.Callsign)
                    && r.LastSeenAt >= cutoff
                    && IsActivatedState(r.State)
                    && (myCid == null || r.ControllingCid != myCid))
                .ToList();

            lock (_gate)
            {
                if (session != _session)
                    return; // reconnected or dropped while this was loading
                foreach (var record in wanted)
                    _pending[record.Callsign] = record;
                _deadline = DateTime.UtcNow + RetryWindow;
            }

            ActionLog.Log("FdrResume", wanted.Count == 0
                ? "No activated flights on OzServer to bring back"
                : $"Bringing back {wanted.Count} activated flight(s) from OzServer: {string.Join(", ", wanted.Select(r => r.Callsign))}",
                new { action = "fdr_resume_begin", callsigns = wanted.Select(r => r.Callsign).ToList() });

            if (wanted.Count > 0)
                RunOnUiThread(Attempt);
        }
        catch (Exception ex)
        {
            ErrorThrottle.Report("fdr-resume", $"Couldn't read flights from OzServer to bring back: {ex.Message}", ex);
        }
    }

    // A flight vatSys finished because it left this client's view (FdrSync.FinishedOutOfView) that
    // this client can see again.
    static bool IsBackInView(FDP2.FDR fdr) =>
        !string.IsNullOrEmpty(fdr.Callsign)
        && FdrSync.FinishedOutOfView.ContainsKey(fdr.Callsign)
        // The pilot, not just a track: the track of an aircraft that has left coasts on for a
        // while, and bringing its flight back then leaves a ghost nothing will finish.
        && FdrSync.IsPilotOnline(fdr.Callsign)
        && SectorLocator.LivePosition(fdr) != null;

    static bool IsActivatedState(string? state) =>
        Enum.TryParse<FDP2.FDR.FDRStates>(state, out var parsed)
        && parsed > FDP2.FDR.FDRStates.STATE_PREACTIVE;

    // One pass over what is still outstanding, on the UI thread (it changes FDR and track state).
    void Attempt()
    {
        if (OzServerMode.Disabled)
            return;

        List<OzServerFdrRecordDto> wanted;
        DateTime deadline;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;
            wanted = _pending.Values.ToList();
            deadline = _deadline;
        }

        if (!Network.IsConnected)
        {
            lock (_gate)
                _pending.Clear();
            return;
        }

        var expired = DateTime.UtcNow >= deadline;
        var mine = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var fdrs = FDP2.GetFDRs.Where(f => !string.IsNullOrEmpty(f.Callsign))
                               .GroupBy(f => f.Callsign, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var activated = new List<string>();
        var offered = new List<string>();
        var skipped = new List<string>();
        var settled = new List<string>();
        var brought = new List<FDP2.FDR>();

        foreach (var record in wanted)
        {
            if (!fdrs.TryGetValue(record.Callsign, out var fdr) || fdr.ParsedRoute.Count == 0)
            {
                if (expired)
                {
                    settled.Add(record.Callsign);
                    skipped.Add($"{record.Callsign} (no flight plan loaded)");
                }
                continue; // the flight plan has not arrived yet
            }

            try
            {
                if (fdr.IsTracked)
                {
                    settled.Add(record.Callsign);
                    skipped.Add($"{record.Callsign} (tracked by {fdr.ControllerTracking?.Callsign ?? "another controller"})");
                    continue;
                }

                if (fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED && !IsBackInView(fdr))
                {
                    settled.Add(record.Callsign);
                    skipped.Add($"{record.Callsign} (finished)");
                    continue;
                }
                if (fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED)
                    FdrSync.FinishedOutOfView.TryRemove(record.Callsign, out _);

                // The sector the record last had it in, if it is one of ours. With no sectors in MMI
                // yet, waits for the grant rather than deciding nothing is ours.
                SectorsVolumes.Sector? inMine = null;
                if (!string.IsNullOrEmpty(record.CurrentSector))
                    inMine = mine.FirstOrDefault(s => string.Equals(s.Name, record.CurrentSector, StringComparison.OrdinalIgnoreCase));

                if (inMine == null && mine.Count == 0 && !expired)
                    continue; // nothing held yet: cannot tell whether to offer it

                var wasActive = fdr.ESTed;
                if (!wasActive)
                    Activate(fdr, record);

                fdr.InhibitAutoCouple = false;

                if (fdr.CoupledTrack == null && record.Lat is double lat && record.Lon is double lon)
                    FloatAt(fdr, new Coordinate(lat, lon), record);

                // Resolved against the position just applied when the record named none of ours.
                inMine ??= SectorLocator.Resolve(fdr, mine);

                if (inMine != null && Network.ValidATC)
                {
                    OfferTo(fdr, inMine);
                    offered.Add($"{record.Callsign} ({inMine.Name})");
                    brought.Add(fdr);
                }
                else if (!wasActive)
                {
                    activated.Add(record.Callsign);
                    brought.Add(fdr);
                }

                settled.Add(record.Callsign);
            }
            catch (Exception ex)
            {
                settled.Add(record.Callsign);
                skipped.Add($"{record.Callsign} ({ex.Message})");
            }
        }

        lock (_gate)
        {
            foreach (var callsign in settled)
                _pending.Remove(callsign);
            foreach (var fdr in brought)
                _done.Add(fdr);
            if (expired)
                _pending.Clear();
        }

        if (activated.Count > 0 || offered.Count > 0 || skipped.Count > 0)
        {
            ActionLog.Log("FdrResume",
                $"Activated: {List(activated)}; offered to you: {List(offered)}; left alone: {List(skipped)}",
                new { action = "fdr_resume_pass", activated, offered, skipped });
        }
    }

    static string List(List<string> items) => items.Count == 0 ? "none" : string.Join(", ", items);

    // Activates the FDR as the record had it. networkEST keeps vatSys from assigning a new code: the
    // aircraft is already squawking the one on the record.
    static void Activate(FDP2.FDR fdr, OzServerFdrRecordDto record)
    {
        if (fdr.State == FDP2.FDR.FDRStates.STATE_INHIBITED)
            FDP2.UnInhibit(fdr);

        if (fdr.AssignedSSRCode == -1 && record.AssignedSsrCode is int code)
            fdr.AssignedSSRCode = code;

        var etd = record.Etd is DateTime e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : DateTime.UtcNow;
        FDP2.EstFDR(fdr, fdr.ParsedRoute[0], etd, networkEST: true);

        if (record.Atd is DateTime atd && fdr.ATD == DateTime.MaxValue)
            MarkDeparted(fdr, DateTime.SpecifyKind(atd, DateTimeKind.Utc));
        else if (fdr.ATD != DateTime.MaxValue && fdr.State == FDP2.FDR.FDRStates.STATE_COORDINATED)
        {
            // Already departed (an FDR vatSys finished out of view keeps its ATD): set to the state
            // of a departed flight nobody is working.
            fdr.State = FDP2.FDR.FDRStates.STATE_UNCONTROLLED;
            FDP2.Process(fdr);
        }
    }

    // Records the departure without FDP2.DepartFDR. That path (and PositionReport, when there is no
    // ATD yet) ends in vatSys's FDRDeparted, which accepts jurisdiction by itself for an FDR whose
    // controlling sector is one of this controller's - taking the tag without the controller being
    // asked. Who is offered the flight is decided by OfferTo alone.
    static void MarkDeparted(FDP2.FDR fdr, DateTime atd)
    {
        fdr.ATD = atd;
        var first = fdr.ParsedRoute.FirstOrDefault();
        if (first != null)
        {
            first.ATONoEvent = atd;
            first.ETONoEvent = atd;
        }
        // What FDRDeparted sets for a flight nobody is taking.
        if (fdr.State == FDP2.FDR.FDRStates.STATE_COORDINATED)
            fdr.State = FDP2.FDR.FDRStates.STATE_UNCONTROLLED;
        FDP2.Process(fdr);
    }

    // Places the flight plan track at the record's last position: a position report at the waypoint
    // it last passed, timed so that at its ground speed it is at that position when the record was
    // last updated. vatSys then carries it along the route as a floating track until a radar return
    // couples to it. Nothing is sent to the network.
    static void FloatAt(FDP2.FDR fdr, Coordinate position, OzServerFdrRecordDto record)
    {
        // Still on the ground: vatSys already draws an undeparted flight plan track at its
        // departure aerodrome.
        var airborne = record.Atd != null || record.OnGround == false || (record.GroundSpeed ?? 0) > 50;
        if (!airborne && fdr.ATD == DateTime.MaxValue)
            return;

        var route = fdr.ParsedRoute.ToList();
        if (route.Count < 2)
            return;

        var bestIndex = -1;
        var bestCross = double.MaxValue;
        var alongFromPrevious = 0.0;
        for (var i = 1; i < route.Count; i++)
        {
            var from = route[i - 1].Intersection?.LatLong;
            var to = route[i].Intersection?.LatLong;
            if (from == null || to == null)
                continue;

            var cross = Math.Abs(Conversions.CalculateCrossTrackDistance(from, to, position));
            if (cross > RouteToleranceNm || cross >= bestCross)
                continue;

            var along1 = Conversions.CalculateAlongTrackDistance(from, to, position);
            var along2 = Conversions.CalculateAlongTrackDistance(to, from, position);
            var leg = Conversions.CalculateDistance(from, to);
            if (Math.Abs(leg - (along1 + along2)) >= 0.1)
                continue; // beyond either end of this leg

            bestIndex = i - 1;
            bestCross = cross;
            alongFromPrevious = along1;
        }

        var seenAt = record.LastSeenAt?.UtcDateTime ?? DateTime.UtcNow;
        if (bestIndex < 0)
        {
            // Off the route: the nearest waypoint, reported at the time it was last seen.
            var nearest = route.Where(s => s.Intersection?.LatLong != null)
                               .OrderBy(s => Conversions.CalculateDistance(s.Intersection.LatLong, position))
                               .FirstOrDefault();
            if (nearest != null)
            {
                EnsureDeparted(fdr, route, route.IndexOf(nearest), seenAt);
                FDP2.PositionReport(fdr, nearest, seenAt, false);
            }
            return;
        }

        var groundSpeed = record.GroundSpeed is int gs && gs > 50 ? gs : (fdr.TAS > 0 ? fdr.TAS : 250);
        var passedAt = seenAt - TimeSpan.FromHours(alongFromPrevious / groundSpeed);
        EnsureDeparted(fdr, route, bestIndex, passedAt);
        FDP2.PositionReport(fdr, route[bestIndex], passedAt, false);
    }

    // An airborne flight with no ATD on record: estimated back from when it passed this waypoint,
    // the way PositionReport itself would - but set directly, see MarkDeparted.
    static void EnsureDeparted(FDP2.FDR fdr, List<FDP2.FDR.ExtractedRoute.Segment> route, int passedIndex, DateTime passedAt)
    {
        if (fdr.ATD != DateTime.MaxValue || passedIndex < 0)
            return;
        var elapsed = TimeSpan.Zero;
        for (var i = 0; i <= passedIndex && i < route.Count; i++)
            elapsed += route[i].EET;
        MarkDeparted(fdr, passedAt - elapsed);
    }

    // The flashing first handoff vatSys gives a flight activated into a controller's airspace:
    // offered to this sector, for the controller to accept or leave like any other.
    static void OfferTo(FDP2.FDR fdr, SectorsVolumes.Sector sector)
    {
        if (fdr.State == FDP2.FDR.FDRStates.STATE_HANDOVER_FIRST && fdr.HandoffSector == sector)
            return;

        fdr.ControllingSector = sector;
        FDP2.HandoffFirst(fdr);
        fdr.HandoffSector = sector;
        FDP2.Process(fdr, true);

        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
