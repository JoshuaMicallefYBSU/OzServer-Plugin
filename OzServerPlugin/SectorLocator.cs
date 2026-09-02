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
    public static RDP.RadarTrack? LiveTrack(FDP2.FDR fdr)
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

    // How close a flight has to be to a controlled sector to count as worth preparing for.
    // Defined once here because two callers depend on it agreeing - TagOwnershipSync
    // pre-activates inside it, FdrActivationSync restores inside it - and two separate constants
    // would eventually disagree about which flights are "ours".
    public const double NearBoundaryThresholdNm = 50.0;

    // Inside one of candidates, or close enough to one's boundary to be arriving shortly.
    //
    // The point of this is what it excludes: a flight that is neither. Those are left exactly as
    // vatSys leaves them - STATE_PREACTIVE, unactivated - rather than being pulled up to
    // STATE_COORDINATED and rendered blue on a scope where they mean nothing to the controller.
    public static bool IsWithinOrNear(FDP2.FDR fdr, IEnumerable<SectorsVolumes.Sector> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 0)
            return false;

        if (Resolve(fdr, list) != null)
            return true;

        // Proximity needs a real position for the same reason containment does - a predicted one
        // would put an aircraft "near" a boundary it is nowhere near.
        if (LiveTrack(fdr) is not { } track)
            return false;

        var nearest = list
            .Select(sector => DistanceToBoundaryNm(sector, track.LatLong))
            .Where(distance => distance != null)
            .Select(distance => distance!.Value)
            .DefaultIfEmpty(double.MaxValue)
            .Min();

        return nearest <= NearBoundaryThresholdNm;
    }

    // A live radar/ADS-B return, never a prediction. fdr.GetLocation() falls back to
    // PredictedPosition.Location whenever CoupledTrack is null (see FDP2.cs) - the flight plan's
    // guess at where the aircraft ought to be - and "where do we think it should be" is not a basis
    // for deciding which controller owns a tag. That fallback is what repeatedly resolved an
    // aircraft laterally outside a sector into it: the prediction sat inside the boundary while the
    // aircraft was nowhere near it, so the tag was activated and flashed in over and over.
    //
    // ActivateIfEligible was already tightened for exactly this reason (see its own comment); this
    // is the same trap in the containment test, which was left behind.
    //
    // OnGround is deliberately not required here, unlike ActivateIfEligible: an aircraft sitting on
    // the ground inside a sector is still inside it, which is precisely a tower sector's traffic.
    // The point is only that the position has to be real.
    public static SectorsVolumes.Sector? Resolve(FDP2.FDR fdr, IEnumerable<SectorsVolumes.Sector> candidates)
    {
        if (LiveTrack(fdr) is not { } track)
            return null;

        var location = track.LatLong;
        if (location == null)
            return null;

        var candidateList = candidates.ToList();
        if (candidateList.Count == 0)
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
