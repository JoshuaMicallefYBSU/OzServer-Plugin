using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// The one definition of "which sectors a position takes when its controller is logged on".
//
// Three places need to agree on this and each used to answer it separately, which is exactly the
// kind of rule that drifts:
//   - AfvSectorClaimer.Init(), granting them to the arriving controller on connect;
//   - PrimaryPositionWatcher, releasing them from whoever was holding them at that moment;
//   - OzServerOwnershipTracker.HandleConflictAsync, deciding that a contested sector is this
//     session's own position and so is not something to ask the controller about.
// If the releasing side and the claiming side disagree by even one sub-sector, that sector either
// falls to nobody (released but never claimed) or stays contested forever (held back while the
// primary keeps trying to take it).
public static class PrimaryPosition
{
    // The sector matching the callsign, plus its whole tree of sub-sectors - a sub-sector can
    // itself bundle further sub-sectors (the same nesting OzServerSectorsWindow.BuildOwnedSectorNode
    // recurses through for display), and all of it belongs to this primary, not just the direct
    // children - except any branch nobody is separately logged in on the way down: a controller
    // logged in directly on a sub-sector (at any depth) keeps it and everything below it, since
    // that is their position, not part of what the primary picks up.
    //
    // Used to be one level deep only. That silently dropped anything nested two levels down (e.g. a
    // primary's sub-sector that is itself a grouping sector) out of every caller that relies on this
    // agreeing exactly with what claiming the primary actually covers server-side
    // (Sector::coveredSectors(), which is fully recursive) - AfvSectorClaimer.Init() handed the
    // arriving controller a bare top-level set with none of that deeper airspace,
    // PrimaryPositionWatcher left it behind on the previous holder instead of releasing it, and
    // HandleConflictAsync's mineByRight check didn't recognise it as this controller's own, offering
    // to "request" positions that were rightfully theirs by simply logging on.
    public static List<SectorsVolumes.Sector> DefaultSectorsFor(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return new List<SectorsVolumes.Sector>();

        var primary = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == callsign);
        if (primary == null)
            return new List<SectorsVolumes.Sector>();

        var sectors = new List<SectorsVolumes.Sector>();
        // Already checked non-null/non-empty above - the .NET Framework reference assemblies this
        // targets don't carry the NotNullWhen annotation on string.IsNullOrEmpty that would let the
        // compiler work that out on its own.
        CollectDefaultSectors(primary, callsign!, OnlineRealAtcs(), sectors, depth: 0);
        return sectors;
    }

    // depth-guarded the same way BuildOwnedSectorNode is, against any cyclical grouping the dataset
    // might contain - real sector data shouldn't nest anywhere near this deep.
    static void CollectDefaultSectors(SectorsVolumes.Sector sector, string callsign,
        List<NetworkATC> online, List<SectorsVolumes.Sector> sectors, int depth)
    {
        // Sector.Equals is callsign-based and == is not overridden, so two instances of the same
        // sector reached by different lookups compare unequal under == - see AfvSectorClaimer's own
        // note on this. Also doubles as the cycle guard: a sector already collected is not walked
        // again.
        if (sectors.Any(s => s.Equals(sector)))
            return;

        sectors.Add(sector);

        if (depth >= 8)
            return;

        foreach (var subsector in sector.SubSectors.ToList())
        {
            // Someone *else* logged in directly on the sub-sector keeps it - that is their position,
            // not part of what the primary picks up.
            //
            // Compared against the position being computed rather than "is anyone online under this
            // callsign", because a sub-sector very often carries the same callsign as its own
            // primary: every one of INL's sub-sectors is BN-INL_CTR. An unqualified check therefore
            // matched the very controller who was logging in, skipped their entire group, and handed
            // them a bare primary sector with none of its airspace.
            if (!string.Equals(subsector.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
                && online.Any(a => a.Callsign == subsector.Callsign))
                continue;

            CollectDefaultSectors(subsector, callsign, online, sectors, depth + 1);
        }
    }

    // IsRealATC filters out observers and the various non-controlling connections that also appear
    // in this list, so only a genuine controller counts as covering a sub-sector.
    public static List<NetworkATC> OnlineRealAtcs() =>
        (Network.GetOnlineATCs ?? new List<NetworkATC>())
        .Where(a => a.IsRealATC && !string.IsNullOrEmpty(a.Callsign))
        .ToList();
}
