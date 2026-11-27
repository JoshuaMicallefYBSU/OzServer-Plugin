using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Activates IFR flights inbound to controlled airspace from outside it, so the controller ahead sees
// them coming instead of a pre-active flight plan and a bare radar return.
//
// A flight is activated when all of these hold:
//
//  - its flight plan is pre-active here, nobody tracks it, and it is IFR (flight rules I). VFR
//    flight plans are never activated: they are activated when they call for a clearance;
//  - its pilot is online and its radar return is airborne (50 kt or more);
//  - it is not inside a sector an OzServer controller holds. A flight that departs inside
//    someone's airspace is activated by that controller when the pilot calls, as before;
//  - its route from where it is now enters a held sector, or passes within RouteMarginNm of one;
//  - it is within TriggerNm of that sector.
//
// The new flight plan gets a code from vatSys (as when a controller activates it), which the pilot
// is not told: the controller gives it on first contact. No CFL is set and nobody takes
// jurisdiction. CoupleAssist couples it to its radar return, and FdrSync reports it to OzServer, so
// every other OzServer controller's FdrResumeActivation brings it in with the same code.
//
// Only one client activates each flight: whoever holds the first such sector along the route.
// Another holder of a sector on the route takes over when the flight is still pre-active here
// TakeOverAfter later - the first holder may not see the aircraft, or may have lost OzServer. A
// flight OzServer already has activated is left to FdrResumeActivation, and one activated here is
// never activated again this session: a controller who puts it back to pre-active means it.
public class InboundActivation
{
    const double TriggerNm = 150;
    const double RouteMarginNm = 50;
    const double MinGroundSpeed = 50;
    // Spacing of the points the remaining route is tested at, and how far along it is followed:
    // enough to cover every sector that can be within TriggerNm of the aircraft.
    const double SampleNm = 5;
    const double MaxRouteNm = 600;

    static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan TakeOverAfter = TimeSpan.FromSeconds(60);
    // The server snapshot (refreshed every 20 s) has to be this recent before anything is
    // activated: without it, a flight OzServer already has activated would get a second code.
    static readonly TimeSpan SnapshotUsableFor = TimeSpan.FromSeconds(60);
    // A server record older than this is not a picture of now (FdrResumeActivation's MaxRecordAge).
    static readonly TimeSpan MaxRecordAge = TimeSpan.FromMinutes(15);

    readonly OzServerOwnershipTracker _tracker;
    readonly System.Threading.Timer _timer;
    readonly object _gate = new();
    // Held by reference, as FdrResumeActivation's: a new FDR for the same callsign is a new flight.
    // A weak set, as that one is: these are inbound flights, many of them long-haul, and a set
    // that kept each one after vatSys deleted it would hold a megabyte or more apiece until the
    // next disconnect - see WeakSet. _waitingSince needs none: it is pruned on every scan.
    readonly WeakSet<FDP2.FDR> _done = new();
    // When this client first found each flight due for activation by another holder.
    readonly Dictionary<FDP2.FDR, DateTime> _waitingSince = new();
    int _session;
    int _scanning;

    sealed class Held
    {
        public Held(SectorsVolumes.Sector sector, bool mine, string holder)
        {
            Sector = sector;
            Mine = mine;
            Holder = holder;
            var lats = sector.Volumes.Where(v => v.Boundary != null).SelectMany(v => v.Boundary).Select(c => c.Latitude).ToList();
            var lons = sector.Volumes.Where(v => v.Boundary != null).SelectMany(v => v.Boundary).Select(c => c.Longitude).ToList();
            if (lats.Count == 0)
                return;
            // A box around the sector, RouteMarginNm wider all round, to skip the exact distance for
            // route points nowhere near it.
            var marginLat = RouteMarginNm / 60.0;
            var marginLon = RouteMarginNm / (60.0 * Math.Max(0.2, Math.Cos(lats.Average() * Math.PI / 180)));
            MinLat = lats.Min() - marginLat;
            MaxLat = lats.Max() + marginLat;
            MinLon = lons.Min() - marginLon;
            MaxLon = lons.Max() + marginLon;
            HasBounds = true;
        }

        public SectorsVolumes.Sector Sector { get; }
        public bool Mine { get; }
        public string Holder { get; }
        public bool HasBounds { get; }
        public double MinLat { get; }
        public double MaxLat { get; }
        public double MinLon { get; }
        public double MaxLon { get; }

        public bool Near(Coordinate point) =>
            HasBounds && point.Latitude >= MinLat && point.Latitude <= MaxLat
            && point.Longitude >= MinLon && point.Longitude <= MaxLon;
    }

    public InboundActivation(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;

        Network.Connected += (_, _) => Reset();
        Network.Disconnected += (_, _) => Reset();

        _timer = new System.Threading.Timer(_ => Scan(), null, ScanInterval, ScanInterval);
    }

    void Reset()
    {
        lock (_gate)
        {
            _done.Clear();
            _waitingSince.Clear();
            _session++;
        }
    }

    void Scan()
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
            return;
        try
        {
            ScanOnce();
        }
        catch (Exception ex)
        {
            ErrorThrottle.Report("inbound-activation", $"Couldn't check for inbound flights to activate: {ex.Message}", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    void ScanOnce()
    {
        if (OzServerMode.Disabled || !Network.IsConnected || NetworkIdentity.IsObserver
            || NetworkServer.Current == null || !_tracker.HasBaseline
            || ServerFdrSnapshot.Age > SnapshotUsableFor)
            return;

        var held = HeldSectors();
        // Only a holder activates, so with nothing held here there is nothing to do.
        if (!held.Any(h => h.Mine))
            return;

        List<FDP2.FDR> fdrs;
        try
        {
            fdrs = FDP2.GetFDRs.Where(f => f != null && !string.IsNullOrEmpty(f.Callsign)).ToList();
        }
        catch (InvalidOperationException)
        {
            return; // modified mid-copy; the next scan
        }

        int session;
        lock (_gate)
            session = _session;

        var now = DateTime.UtcNow;
        var due = new List<(FDP2.FDR Fdr, string Sector, bool TakeOver)>();
        var stillWaiting = new HashSet<FDP2.FDR>();

        foreach (var fdr in fdrs)
        {
            if (!IsCandidate(fdr))
                continue;
            lock (_gate)
            {
                if (_done.Contains(fdr))
                    continue;
            }

            var track = SectorLocator.LiveTrack(fdr);
            if (track?.LatLong is not { } position || track.OnGround || track.GroundSpeed < MinGroundSpeed)
                continue;
            if (!FdrSync.IsPilotOnline(fdr.Callsign) || ActivatedOnServer(fdr.Callsign))
                continue;

            // Inside a held sector: that controller's to activate when the pilot calls. Laterally
            // inside with no Mode C counts as inside (IsInSector ignores levels at -1).
            var level = track.CorrectedAltitude;
            if (held.Any(h => h.Sector.IsInSector(position, level)))
                continue;

            var near = held.Where(h => SectorLocator.DistanceNm(h.Sector, position) <= TriggerNm).ToList();
            if (near.Count == 0)
                continue;

            var ahead = SectorsAhead(fdr, position, near);
            if (ahead.Count == 0)
                continue;

            var first = ahead[0];
            if (first.Mine)
            {
                due.Add((fdr, first.Sector.Name, false));
                continue;
            }

            // Another controller's to activate. A holder of a later sector on the route takes over
            // if it is still pre-active here after TakeOverAfter.
            var mineAhead = ahead.FirstOrDefault(h => h.Mine);
            if (mineAhead == null)
                continue;
            stillWaiting.Add(fdr);
            lock (_gate)
            {
                if (!_waitingSince.TryGetValue(fdr, out var since))
                    _waitingSince[fdr] = now;
                else if (now - since >= TakeOverAfter)
                    due.Add((fdr, mineAhead.Sector.Name, true));
            }
        }

        lock (_gate)
        {
            foreach (var fdr in _waitingSince.Keys.Where(f => !stillWaiting.Contains(f)).ToList())
                _waitingSince.Remove(fdr);
        }

        if (due.Count > 0)
            RunOnUiThread(() => Activate(due, session));
    }

    // Pre-active, untracked and IFR. Rechecked on the UI thread before activating.
    static bool IsCandidate(FDP2.FDR fdr) =>
        !fdr.ESTed
        && fdr.State == FDP2.FDR.FDRStates.STATE_PREACTIVE
        && !fdr.IsTracked
        && string.Equals(fdr.FlightRules?.Trim(), "I", StringComparison.OrdinalIgnoreCase)
        && fdr.ParsedRoute.Count > 0;

    // OzServer has it activated, recently: FdrResumeActivation brings it in with that record's code.
    static bool ActivatedOnServer(string callsign) =>
        ServerFdrSnapshot.TryGet(callsign, out var entry)
        && entry.LastSeenAt is { } seen && seen >= DateTimeOffset.UtcNow - MaxRecordAge
        && Enum.TryParse<FDP2.FDR.FDRStates>(entry.State, out var state)
        && state > FDP2.FDR.FDRStates.STATE_PREACTIVE;

    // Every sector an OzServer controller holds: this controller's, and those of others vatSys lists
    // online.
    List<Held> HeldSectors()
    {
        var myCid = NetworkIdentity.Current?.Cid;
        var myCallsign = Network.Me?.Callsign ?? "";
        var online = new HashSet<string>(
            (Network.GetOnlineATCs ?? new List<NetworkATC>()).Where(a => !string.IsNullOrEmpty(a.Callsign)).Select(a => a.Callsign),
            StringComparer.OrdinalIgnoreCase);
        var others = _tracker.ControlledByOthers;

        var held = _tracker.Owned.Where(s => !s.IsDummy).Select(s => new Held(s, true, myCallsign)).ToList();
        foreach (var sector in SectorsVolumes.Sectors)
        {
            if (sector.IsDummy || held.Any(h => h.Sector.Equals(sector)))
                continue;
            if (others.TryGetValue(sector.Name, out var owner) && owner.Cid != myCid
                && !string.IsNullOrEmpty(owner.Callsign) && online.Contains(owner.Callsign))
                held.Add(new Held(sector, false, owner.Callsign));
        }
        return held;
    }

    // The sectors among `near` that the route from the aircraft's position onwards enters or passes
    // within RouteMarginNm of, in the order the route reaches them (ties by name, so every client
    // orders them alike).
    static List<Held> SectorsAhead(FDP2.FDR fdr, Coordinate position, List<Held> near)
    {
        var path = RemainingRoute(fdr, position);
        if (path.Count < 2)
            return new List<Held>();

        var reachedAt = new Dictionary<Held, double>();
        var along = 0.0;
        for (var i = 1; i < path.Count && along <= MaxRouteNm && reachedAt.Count < near.Count; i++)
        {
            var from = path[i - 1];
            var to = path[i];
            var leg = Conversions.CalculateDistance(from, to);
            var steps = Math.Max(1, (int)Math.Ceiling(leg / SampleNm));
            for (var k = i == 1 ? 0 : 1; k <= steps; k++)
            {
                var t = (double)k / steps;
                var point = new Coordinate(from.Latitude + (to.Latitude - from.Latitude) * t,
                                           from.Longitude + (to.Longitude - from.Longitude) * t);
                var distanceAlong = along + leg * t;
                if (distanceAlong > MaxRouteNm)
                    break;
                foreach (var h in near)
                {
                    if (reachedAt.ContainsKey(h) || !h.Near(point))
                        continue;
                    if (SectorLocator.DistanceNm(h.Sector, point) <= RouteMarginNm)
                        reachedAt[h] = distanceAlong;
                }
            }
            along += leg;
        }

        return reachedAt.OrderBy(r => r.Value).ThenBy(r => r.Key.Sector.Name, StringComparer.Ordinal)
                        .Select(r => r.Key).ToList();
    }

    // The aircraft's position, then the route's waypoints from the end of the leg it is nearest to.
    static List<Coordinate> RemainingRoute(FDP2.FDR fdr, Coordinate position)
    {
        var points = fdr.ParsedRoute.Select(s => s.Intersection?.LatLong).Where(c => c != null).Cast<Coordinate>().ToList();
        if (points.Count == 0)
            return new List<Coordinate>();
        if (points.Count == 1)
            return new List<Coordinate> { position, points[0] };

        var bestEnd = 1;
        var best = double.MaxValue;
        for (var i = 1; i < points.Count; i++)
        {
            var d = DistanceToLegNm(position, points[i - 1], points[i]);
            if (d < best)
            {
                best = d;
                bestEnd = i;
            }
        }

        var remaining = new List<Coordinate> { position };
        remaining.AddRange(points.Skip(bestEnd));
        return remaining;
    }

    // Flat-earth distance from a point to a leg, as SectorLocator.DistanceNm measures.
    static double DistanceToLegNm(Coordinate p, Coordinate a, Coordinate b)
    {
        var k = Math.Cos(p.Latitude * Math.PI / 180);
        double ax = (a.Longitude - p.Longitude) * k * 60, ay = (a.Latitude - p.Latitude) * 60;
        double bx = (b.Longitude - p.Longitude) * k * 60, by = (b.Latitude - p.Latitude) * 60;
        double dx = bx - ax, dy = by - ay, length2 = dx * dx + dy * dy;
        var t = length2 == 0 ? 0 : Math.Max(0, Math.Min(1, -(ax * dx + ay * dy) / length2));
        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    // On the UI thread: activates each flight still due, as a controller's activation would, with a
    // code from vatSys and no CFL, and leaves coupling to CoupleAssist.
    void Activate(List<(FDP2.FDR Fdr, string Sector, bool TakeOver)> due, int session)
    {
        if (OzServerMode.Disabled || !Network.IsConnected)
            return;

        foreach (var (fdr, sector, takeOver) in due)
        {
            lock (_gate)
            {
                if (session != _session || _done.Contains(fdr))
                    continue;
            }
            // Anything may have changed since the scan: a controller activated it, or another
            // client did and FdrResumeActivation brought it in.
            if (!IsCandidate(fdr) || ActivatedOnServer(fdr.Callsign))
                continue;

            try
            {
                var etd = fdr.ETD != DateTime.MaxValue ? fdr.ETD : DateTime.UtcNow;
                FDP2.EstFDR(fdr, fdr.ParsedRoute[0], etd, networkEST: false);
                fdr.InhibitAutoCouple = false;
                lock (_gate)
                {
                    _done.Add(fdr);
                    _waitingSince.Remove(fdr);
                }
                var code = fdr.AssignedSSRCode == -1 ? null : Convert.ToString(fdr.AssignedSSRCode, 8).PadLeft(4, '0');
                ActionLog.Log("Inbound",
                    $"Activated {fdr.Callsign}, inbound to {sector}{(takeOver ? " (taken over: its first sector's controller had not)" : "")}, code {code ?? "none"}",
                    new { action = "inbound_activate", fdr_callsign = fdr.Callsign, sector, take_over = takeOver, ssr = code });
            }
            catch (Exception ex)
            {
                lock (_gate)
                    _done.Add(fdr); // not tried again every scan
                ErrorThrottle.Report("inbound-activate", $"Couldn't activate {fdr.Callsign}: {ex.Message}", ex);
            }
        }
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
