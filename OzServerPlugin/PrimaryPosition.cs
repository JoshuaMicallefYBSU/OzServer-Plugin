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
//
// ---------------------------------------------------------------------------------------------
// Where a group actually comes from
//
// A group is a Position in Positions.xml and the <Sectors> it lists - Alice Springs is exactly
// ASP, ASW, BKE, ESP, FOR, WAR, WRA. That list is what the position owns, and it is reachable at
// runtime as LogicalPositions.Positions.
//
// This used to walk Sectors.xml's <ResponsibleSectors> instead, which is a different thing: what a
// sector *covers* while nobody is on those sectors, not what a position owns. Across this dataset
// the two disagree for 48 of the 99 ASD positions, in both directions, and both are wrong:
//
//   - Under-granting. Walton TCU owns SWA, SRA and SBA; SWA is responsible for nothing, so the
//     controller logging in got SWA alone and had to ask for the rest of their own position.
//   - Over-granting, which is worse. INL is responsible for Brisbane sectors it merely covers, so
//     an arriving Inverell took BAB, BAS, BDN, BDS and SHN - sectors belonging to Brisbane's own
//     position - straight off whoever was legitimately working them.
//
// ---------------------------------------------------------------------------------------------
// Primary versus member
//
// Every sector in a group can also be worked on its own, so a group can be split across as many
// controllers as it has sectors. What decides whether logging on takes the whole group is whether
// that sector is the group's *primary*:
//
//   - The primary logs on -> the whole group is theirs. Somebody who extended into one of the small
//     sectors gives all of it back, because the primary owns the position.
//   - Any other member logs on -> that sector alone. They ask for the rest the ordinary way, which
//     is what makes splitting a group up possible at all.
//
// Nothing in the dataset marks the primary, so it is derived, most reliable signal first:
//
//   1. The member responsible (recursively) for every other member of the position - ASP for Alice
//     Springs, ISA for Mt Isa. Covers 59 of 99.
//   2. Failing that, the member whose FullName is the position's name - Adelaide TCU's "Adelaide
//     TCU". Covers another 33.
//
// The remaining 7 have no primary and are not meant to: Walton TCU and the smaller towers are peer
// roles (ADC, ADCC, SMC) where no one seat owns the others. There every member stands alone, which
// is the safe answer - a controller logging on as one tower role does not take another's.
public static class PrimaryPosition
{
    // Depth guard against any cyclical grouping the dataset might contain - real sector data
    // shouldn't nest anywhere near this deep.
    const int MaxDepth = 8;

    public static List<SectorsVolumes.Sector> DefaultSectorsFor(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return new List<SectorsVolumes.Sector>();

        var own = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == callsign);
        if (own == null)
            return new List<SectorsVolumes.Sector>();

        var group = GroupOwnedBy(own);

        // Not a primary - just their own sector. Everything else in the group has to be requested,
        // which is exactly what lets a group be split between controllers.
        if (group == null)
            return new List<SectorsVolumes.Sector> { own };

        var online = OnlineRealAtcs();

        return group
            // Someone else logged in directly on a member keeps it - that is their position, not
            // part of what the primary picks up.
            //
            // Compared against the position being computed rather than "is anyone online under this
            // callsign", because a member very often carries the same callsign as its own primary.
            // An unqualified check matched the very controller who was logging in, skipped their
            // own group, and handed them a bare primary sector with none of its airspace.
            .Where(member => string.Equals(member.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
                             || !online.Any(atc => string.Equals(atc.Callsign, member.Callsign,
                                                                 StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // The sectors this session should actually hold right now. DefaultSectorsFor answers "what
    // this position normally owns"; this removes top-down cover where somebody else is already
    // logged on to the covered TCU/member position. Benalla still normally covers MAE, but not
    // while ML_APP is online.
    public static List<SectorsVolumes.Sector> DefaultSectorsForCurrentSession(string? callsign) =>
        WithoutStaffedTopDownCover(DefaultSectorsFor(callsign), callsign);

    public static List<SectorsVolumes.Sector> WithoutStaffedTopDownCover(
        IEnumerable<SectorsVolumes.Sector> sectors,
        string? ownCallsign)
    {
        var list = sectors.Where(s => s != null).ToList();
        if (list.Count == 0)
            return list;

        var staffed = new HashSet<string>(
            list.SelectMany(sector => StaffedCoveredSectors(sector, ownCallsign)),
            StringComparer.OrdinalIgnoreCase);

        return list.Where(sector => !staffed.Contains(sector.Name)).ToList();
    }

    // The position this sector is the primary of, or null if it is only a member of one (or of
    // none). See the header for how the primary is derived and why it is not simply stated.
    static List<SectorsVolumes.Sector>? GroupOwnedBy(SectorsVolumes.Sector sector)
    {
        if (!LogicalPositions.Loaded)
            return null;

        foreach (var position in LogicalPositions.Positions)
        {
            // ASMGCS positions are ground displays, not an airspace group to hand over.
            if (position.Type != LogicalPositions.PositionTypes.ASD || position.Sectors == null)
                continue;

            var members = position.Sectors.Where(s => s != null).ToList();
            if (members.Count == 0 || !members.Any(m => m.Equals(sector)))
                continue;

            if (IsPrimaryOf(sector, members, position.Name))
                return members;
        }

        return null;
    }

    static bool IsPrimaryOf(SectorsVolumes.Sector sector, List<SectorsVolumes.Sector> members, string? positionName)
    {
        // 1. Responsible, following the chain, for everything else in the position.
        var covered = new List<SectorsVolumes.Sector>();
        Collect(sector, covered, depth: 0);

        if (members.All(m => covered.Any(c => c.Equals(m))))
            return true;

        // 2. Otherwise the member the position is named after. Only decisive when exactly one member
        // matches - two would make "the primary" a guess, and guessing here takes somebody's
        // airspace off them.
        if (string.IsNullOrEmpty(positionName))
            return false;

        var named = members
            .Where(m => string.Equals(m.FullName, positionName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return named.Count == 1 && named[0].Equals(sector);
    }

    // The sectors a claim for `sector` would sweep up that somebody is actually logged on as, to be
    // sent as that claim's exclusion list.
    //
    // A claim expands through ResponsibleSectors - that is what top-down means: Benalla covers
    // Melbourne Approach because Benalla works that airspace when nobody is on it. What the bundle
    // does not say is whether anybody IS on it, and a sector belongs to whoever is logged on as it.
    // Without this, an enroute controller logging on while Melbourne Approach was already online
    // took MAE - and MDN, MDS, MAV and MAW behind it - straight off them.
    //
    // Note this walks ResponsibleSectors rather than the Positions.xml grouping the rest of this
    // class is so careful to use. That is deliberate and is not the bug the header warns about:
    // this is not deciding what a position owns, it is describing what the *backend's* claim will
    // expand to, so it has to model the backend's expansion exactly in order to carve pieces out
    // of it.
    //
    // Read from Network.GetOnlineATCs - vatSys's own live view of the VATSIM ATC list, fed by the
    // protocol connection itself. It reflects a logon within seconds, where the backend's copy is
    // the public data feed and can lag one by the better part of a minute; and it is true whether
    // or not the other controller is running this plugin at all, which the backend's
    // ownership records are not. Both of those are why the question is answered here and not there.
    // What is withheld is each online controller's whole position, not just the one sector their
    // callsign names. Melbourne Approach is MAE, MDN, MDS, MAV and MAW; withholding only MAE would
    // have handed the enroute controller the other four - the departures and radar sectors - out of
    // the very group whose controller is sitting there working it. DefaultSectorsFor is what decides
    // that, so the rule is the same one the rest of this class applies, from the same definition:
    // a primary logging on holds their group, a member logging on holds only their own sector.
    public static List<string> StaffedCoveredSectors(SectorsVolumes.Sector sector, string? ownCallsign)
    {
        var claimable = new HashSet<string>(CoveredBy(sector).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var staffed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var atc in OnlineRealAtcs())
        {
            if (string.Equals(atc.Callsign, ownCallsign, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var theirs in DefaultSectorsFor(atc.Callsign))
            {
                if (claimable.Contains(theirs.Name))
                    staffed.Add(theirs.Name);
            }
        }

        // Never the sector being claimed. Somebody is logged on as it - this controller - and
        // excluding it would turn its own claim into a no-op.
        staffed.Remove(sector.Name);

        return staffed.ToList();
    }

    // Everything a claim for this sector expands to on the backend - the sector plus its
    // responsible sectors, recursively. Mirrors the backend's own covered().
    public static List<SectorsVolumes.Sector> CoveredBy(SectorsVolumes.Sector sector)
    {
        var covered = new List<SectorsVolumes.Sector>();
        Collect(sector, covered, depth: 0);
        return covered;
    }

    // The sector plus everything it is responsible for, recursively.
    static void Collect(SectorsVolumes.Sector sector, List<SectorsVolumes.Sector> into, int depth)
    {
        // Sector.Equals is callsign-based and == is not overridden, so two instances of the same
        // sector reached by different lookups compare unequal under == - see AfvSectorClaimer's own
        // note on this. Also doubles as the cycle guard: a sector already collected is not walked
        // again.
        if (into.Any(s => s.Equals(sector)))
            return;

        into.Add(sector);

        if (depth >= MaxDepth)
            return;

        foreach (var subsector in sector.SubSectors.ToList())
            Collect(subsector, into, depth + 1);
    }

    // IsRealATC filters out observers and the various non-controlling connections that also appear
    // in this list, so only a genuine controller counts as covering a member sector.
    public static List<NetworkATC> OnlineRealAtcs() =>
        (Network.GetOnlineATCs ?? new List<NetworkATC>())
        .Where(a => a.IsRealATC && !string.IsNullOrEmpty(a.Callsign))
        .ToList();
}
