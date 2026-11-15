using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Resolves which one of a set of candidate sectors an FDR is physically located in right now. Two
// callers, each passing a different candidate set for a different question:
//   - FdrSync passes the full SectorsVolumes.Sectors list, to report the aircraft's true geographic
//     sector to OzServer regardless of whether anyone has claimed it there - "which sector is this
//     aircraft in" and "who owns the tag" are different questions, and the DB only ever had an
//     answer for the second one.
//   - TagResumeRecovery passes MMI.SectorsControlled, to find which of this controller's own sectors
//     a recovered tag belongs to.
//
// The boundary-proximity maths that used to live here (DistanceToBoundaryNm, IsWithinOrNear) went
// with the tag handling it existed for - it only ever served pre-activating a flight before it
// crossed a boundary.
public static class SectorLocator
{
    // The aircraft's real position, whether or not vatSys has coupled its radar track to the flight
    // plan yet.
    //
    // Coupling and activation depend on one another: a flight can be sitting plainly inside a sector
    // with CoupledTrack still null, and keying containment on CoupledTrack alone left exactly that
    // flight stuck at STATE_PREACTIVE forever - unable to activate because it had not coupled, and
    // unable to couple because it had not activated. Matching the live radar picture by callsign
    // breaks that loop.
    //
    // What this deliberately never does is fall back to fdr.GetLocation(): that returns
    // PredictedPosition.Location when there is no track at all, which is the flight plan's guess at
    // where the aircraft ought to be, and is what repeatedly resolved an aircraft laterally outside
    // a sector into it. No radar return means no position - not a guessed one.
    static RDP.RadarTrack? LiveTrack(FDP2.FDR fdr)
    {
        if (fdr.CoupledTrack is { } coupled)
            return coupled;

        if (string.IsNullOrEmpty(fdr.Callsign))
            return null;

        try
        {
            // RDP.RadarTracks is the live list and is read here from timer threads, so a concurrent
            // radar update can invalidate the enumeration mid-pass. Not finding a track this tick is
            // harmless - the next FDR or radar update re-asks - whereas throwing would take out the
            // whole sync loop.
            foreach (var track in RDP.RadarTracks)
            {
                if (string.Equals(track?.ActualAircraft?.Callsign, fdr.Callsign, StringComparison.OrdinalIgnoreCase))
                    return track;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    public static SectorsVolumes.Sector? Resolve(FDP2.FDR fdr, IEnumerable<SectorsVolumes.Sector> candidates)
    {
        // Candidates first: LiveTrack can scan every radar track, and with nothing to match against
        // that is wasted work.
        var candidateList = candidates.ToList();
        if (candidateList.Count == 0)
            return null;

        if (LiveTrack(fdr) is not { } track)
            return null;

        var location = track.LatLong;
        if (location == null)
            return null;

        var level = track.CorrectedAltitude;
        var matches = candidateList.Where(s => s.IsInSector(location, level)).ToList();
        if (matches.Count == 0)
            return null;

        // Among more than one match (a primary whose Volumes cover the same ground as one of its
        // own SubSectors), the more specific sub-sector wins - the same "bare sub-sector vs its
        // covering primary" precedence AfvSectorClaimer.CheckActive already applies to a VSCS
        // transmit press.
        var mostSpecific = matches
            .Where(candidate => !matches.Any(other => !other.Equals(candidate) && candidate.SubSectors.Any(sub => sub.Equals(other))))
            .ToList();

        // Sector.IsInSector(point, -1) ignores every Volume's LowerLevel/UpperLevel entirely - it's
        // vatSys's own "unknown altitude" behaviour, not something introduced here - so with no Mode
        // C yet, two sub-sectors stacked at different levels over the same ground both match with
        // nothing left to tell them apart (they aren't in a SubSectors relationship with each other,
        // so the filter above doesn't collapse them). Guessing one is exactly what LiveTrack's own
        // comment above already refuses to do for position; do the same for the sector it resolves
        // to. A real Mode C return re-asks and resolves it properly.
        if (level == -1 && mostSpecific.Count > 1)
            return null;

        return mostSpecific.FirstOrDefault() ?? matches[0];
    }

    // Where the aircraft is according to this client's own radar picture - null when this client does
    // not receive it at all (outside its visibility range, or not connected).
    public static Coordinate? LivePosition(FDP2.FDR fdr) => LiveTrack(fdr)?.LatLong;

    // The live track's Mode C altitude, or null with no track or no Mode C (vatSys's -1).
    public static int? LiveAltitude(FDP2.FDR fdr) => LiveTrack(fdr) is { CorrectedAltitude: var altitude } && altitude != -1 ? altitude : null;

    // Where the aircraft is, for measuring how far it is from a sector: its radar return if there is
    // one, otherwise vatSys's own prediction along the flight plan. Unlike Resolve, a guess is fine
    // here - "which held sector is nearest" needs an answer even for an aircraft out of radar range,
    // and nothing is put into a sector on the strength of it.
    public static Coordinate? ApproximatePosition(FDP2.FDR fdr)
    {
        if (LiveTrack(fdr)?.LatLong is { } live)
            return live;
        try
        {
            return fdr.GetLocation();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Straight-line distance in nautical miles from a point to a sector: 0 when the point is inside
    // any of its volumes laterally (whatever the level), otherwise the distance to the nearest edge.
    // Flat-earth over the few hundred miles that matter here, longitude scaled to the point's
    // latitude - plenty to tell which sector is closest.
    public static double DistanceNm(SectorsVolumes.Sector sector, Coordinate point)
    {
        var best = double.MaxValue;
        var k = Math.Cos(point.Latitude * Math.PI / 180);
        double X(Coordinate c) => (c.Longitude - point.Longitude) * k * 60;
        double Y(Coordinate c) => (c.Latitude - point.Latitude) * 60;

        foreach (var volume in sector.Volumes)
        {
            var ring = volume.Boundary;
            if (ring == null || ring.Count < 3)
                continue;

            var inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double xi = X(ring[i]), yi = Y(ring[i]), xj = X(ring[j]), yj = Y(ring[j]);
                // The point is the origin: ray cast along +x, and distance from the origin to the edge.
                if ((yi > 0) != (yj > 0) && 0 < (xj - xi) * (0 - yi) / (yj - yi) + xi)
                    inside = !inside;

                double dx = xj - xi, dy = yj - yi, length2 = dx * dx + dy * dy;
                var t = length2 == 0 ? 0 : Math.Max(0, Math.Min(1, -(xi * dx + yi * dy) / length2));
                var cx = xi + t * dx;
                var cy = yi + t * dy;
                best = Math.Min(best, Math.Sqrt(cx * cx + cy * cy));
            }

            if (inside)
                return 0;
        }

        return best;
    }
}
