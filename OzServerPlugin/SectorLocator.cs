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
}
