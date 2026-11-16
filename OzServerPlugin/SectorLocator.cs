using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Resolves which one of a set of candidate sectors an FDR is physically inside. Each caller passes
// the candidate set that fits its question:
//   - FdrSync passes the full SectorsVolumes.Sectors list, to report the aircraft's geographic
//     sector to OzServer whether or not anyone has claimed it - "which sector is this aircraft in"
//     and "who owns the tag" are different questions.
//   - TagResumeRecovery passes MMI.SectorsControlled, to find which of this controller's own
//     sectors a recovered tag belongs to.
public static class SectorLocator
{
    // The aircraft's radar track, whether or not vatSys has coupled it to the flight plan. Coupling
    // and activation depend on one another, so a flight inside a sector can still have a null
    // CoupledTrack; the live radar picture is matched by callsign instead.
    //
    // Never falls back to fdr.GetLocation(): with no track that returns PredictedPosition.Location,
    // the flight plan's guess, which can resolve an aircraft into a sector it is laterally outside.
    // No radar return means no position.
    internal static RDP.RadarTrack? LiveTrack(FDP2.FDR fdr)
    {
        if (fdr.CoupledTrack is { } coupled)
            return coupled;

        if (string.IsNullOrEmpty(fdr.Callsign))
            return null;

        try
        {
            // RDP.RadarTracks is the live list and is read here from timer threads, so a concurrent
            // radar update can invalidate the enumeration. Missing a track this pass is harmless -
            // the next FDR or radar update asks again - whereas throwing would abort the caller's
            // whole loop.
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
        // Candidates first: LiveTrack can scan every radar track, which is wasted work with nothing
        // to match against.
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
        // own SubSectors), the more specific sub-sector wins.
        var mostSpecific = matches
            .Where(candidate => !matches.Any(other => !other.Equals(candidate) && candidate.SubSectors.Any(sub => sub.Equals(other))))
            .ToList();

        // Sector.IsInSector(point, -1) ignores every Volume's LowerLevel/UpperLevel - vatSys's own
        // "unknown altitude" behaviour - so with no Mode C, two sub-sectors stacked over the same
        // ground both match, and they are not in a SubSectors relationship for the filter above to
        // collapse. No sector is guessed, as no position is (see LiveTrack); a Mode C return
        // resolves it.
        if (level == -1 && mostSpecific.Count > 1)
            return null;

        return mostSpecific.FirstOrDefault() ?? matches[0];
    }

    // Where the aircraft is according to this client's own radar picture - null when this client
    // does not receive it (outside its visibility range, or not connected).
    public static Coordinate? LivePosition(FDP2.FDR fdr) => LiveTrack(fdr)?.LatLong;

    // The live track's Mode C altitude, or null with no track or no Mode C (vatSys's -1).
    public static int? LiveAltitude(FDP2.FDR fdr) => LiveTrack(fdr) is { CorrectedAltitude: var altitude } && altitude != -1 ? altitude : null;

    // Where the aircraft is, for measuring its distance from a sector: its radar return if there is
    // one, otherwise vatSys's prediction along the flight plan. Unlike Resolve, a guess is
    // acceptable here: nothing is put into a sector on the strength of it.
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
    // A flat-earth approximation with longitude scaled to the point's latitude, which is enough to
    // tell which sector is closest.
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
                // The point is the origin: ray cast along +x, and distance from it to the edge.
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
