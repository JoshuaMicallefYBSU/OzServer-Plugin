using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Resolves which one of a set of candidate sectors an FDR is physically located in right now, plus
// (DistanceToBoundaryNm) how close it is to one particular sector's own boundary. Shared by callers
// that want the same geometry tests against different candidate sets/sectors:
//   - TagOwnershipSync restricts Resolve's candidates to MMI.SectorsControlled (the sectors this
//     controller has actually selected right now) to decide who should hold the tag, and calls
//     DistanceToBoundaryNm against those same sectors to pre-activate a flight before it arrives.
//   - FdrSync passes the full SectorsVolumes.Sectors list to Resolve, to report the aircraft's true
//     geographic sector to OzServer regardless of whether anyone has actually claimed it there -
//     "which sector is this aircraft in" and "who owns the tag" are different questions, and the DB
//     only ever had an answer for the second one.
public static class SectorLocator
{
    // Sentinel "unknown" (-1) either way - RadarTrack.CorrectedAltitude when unset, and
    // Sector.IsInSector's own "skip the altitude band check" level - so an aircraft with no live
    // track yet is still resolved purely on lateral position.
    public static SectorsVolumes.Sector? Resolve(FDP2.FDR fdr, IEnumerable<SectorsVolumes.Sector> candidates)
    {
        var location = fdr.GetLocation();
        if (location == null)
            return null;

        var candidateList = candidates.ToList();
        if (candidateList.Count == 0)
            return null;

        var level = fdr.CoupledTrack?.CorrectedAltitude ?? -1;
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

        return mostSpecific.FirstOrDefault() ?? matches[0];
    }

    // Lateral distance (nm) from point to the nearest edge of sector's own boundary polygon(s) -
    // altitude bands aren't considered, since "how close is this aircraft to the boundary" is a 2D
    // question about the outline drawn on the radar screen, same as IsInSector's own polygon test
    // is a purely lateral one before its altitude-band check. Null only if sector has no volumes at
    // all (shouldn't happen for a real sector, but a Dummy has none).
    //
    // Built on the same great-circle primitives vatSys itself already uses for route/track-proximity
    // math (Conversions.CalculateCrossTrackDistance/CalculateAlongTrackDistance) rather than a flat
    // planar approximation, which would drift noticeably wrong for boundary edges hundreds of nm
    // long at the kind of latitudes OzServer's own sectors sit at.
    public static double? DistanceToBoundaryNm(SectorsVolumes.Sector sector, Coordinate point)
    {
        double? closest = null;

        foreach (var volume in sector.Volumes)
        {
            for (var i = 1; i < volume.Boundary.Count; i++)
            {
                var distance = DistanceToSegmentNm(volume.Boundary[i - 1], volume.Boundary[i], point);
                if (closest == null || distance < closest)
                    closest = distance;
            }
        }

        return closest;
    }

    // Great-circle point-to-segment distance (nm): projects point onto the great circle through
    // segStart/segEnd (CalculateCrossTrackDistance) and checks whether that projection actually
    // falls within the segment (CalculateAlongTrackDistance, bounded by the segment's own length) -
    // if it doesn't, the closest point on the *segment* (as opposed to the infinite circle) is
    // whichever endpoint is nearer, not the out-of-bounds projection.
    static double DistanceToSegmentNm(Coordinate segStart, Coordinate segEnd, Coordinate point)
    {
        var segmentLength = Conversions.CalculateDistance(segStart, segEnd);
        // Degenerate (near-duplicate) vertex pair - nothing to project onto.
        if (segmentLength < 1e-6)
            return Conversions.CalculateDistance(segStart, point);

        var alongTrack = Conversions.CalculateAlongTrackDistance(segStart, segEnd, point);
        // NaN falls out of CalculateAlongTrackDistance's own sqrt for a point essentially on top of
        // segStart - endpoint distance is the right answer there too.
        if (double.IsNaN(alongTrack) || alongTrack < 0 || alongTrack > segmentLength)
            return Math.Min(Conversions.CalculateDistance(segStart, point), Conversions.CalculateDistance(segEnd, point));

        return Math.Abs(Conversions.CalculateCrossTrackDistance(segStart, segEnd, point));
    }
}
