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
    // The sector matching the callsign, plus each of its direct sub-sectors that nobody is
    // separately logged in on. A controller logged in directly on a sub-sector keeps it: that is
    // their position, not part of what the primary picks up.
    public static List<SectorsVolumes.Sector> DefaultSectorsFor(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return new List<SectorsVolumes.Sector>();

        var primary = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == callsign);
        if (primary == null)
            return new List<SectorsVolumes.Sector>();

        var sectors = new List<SectorsVolumes.Sector> { primary };
        var online = OnlineRealAtcs();

        foreach (var subsector in primary.SubSectors.ToList())
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

            // Sector.Equals is callsign-based and == is not overridden, so two instances of the
            // same sector reached by different lookups compare unequal under == - see
            // AfvSectorClaimer's own note on this.
            if (!sectors.Any(s => s.Equals(subsector)))
                sectors.Add(subsector);
        }

        return sectors;
    }

    // IsRealATC filters out observers and the various non-controlling connections that also appear
    // in this list, so only a genuine controller counts as covering a sub-sector.
    public static List<NetworkATC> OnlineRealAtcs() =>
        (Network.GetOnlineATCs ?? new List<NetworkATC>())
        .Where(a => a.IsRealATC && !string.IsNullOrEmpty(a.Callsign))
        .ToList();
}
