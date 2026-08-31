using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Resolves which one of a set of candidate sectors an FDR is physically located in right now.
// Shared by two callers that want the same geometry test against two different candidate sets:
//   - TagOwnershipSync restricts candidates to tracker.ClaimedSectors (only what OzServer currently
//     has an active ownership record for, mine or otherwise) to decide who should hold the tag.
//   - FdrSync passes the full SectorsVolumes.Sectors list, to report the aircraft's true geographic
//     sector to OzServer regardless of whether anyone has actually claimed it there - "which sector
//     is this aircraft in" and "who owns the tag" are different questions, and the DB only ever had
//     an answer for the second one.
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
}
