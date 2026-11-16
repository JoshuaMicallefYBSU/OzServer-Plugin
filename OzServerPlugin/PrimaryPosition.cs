using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// The one definition of "which sectors a position takes when its controller is logged on".
//
// Three callers have to agree on it:
//   - AfvSectorClaimer.Init(), granting them to the arriving controller on connect;
//   - PrimaryPositionWatcher, releasing them to a controller who was online first;
//   - OzServerOwnershipTracker.HandleConflictAsync, deciding that a contested sector is this
//     session's own position and so is requested without asking.
// If the releasing and claiming sides differ by one sub-sector, that sector either falls to nobody
// or stays contested.
//
// Where a group comes from
//
// A group is a Position in Positions.xml and the <Sectors> it lists (Alice Springs is ASP, ASW,
// BKE, ESP, FOR, WAR, WRA), available at runtime as LogicalPositions.Positions. It is not
// Sectors.xml's <ResponsibleSectors>, which is what a sector covers while nobody is on those
// sectors rather than what a position owns. The two disagree for many positions, and following
// ResponsibleSectors is wrong in both directions: it under-grants (Walton TCU owns SWA, SRA and
// SBA, but SWA is responsible for nothing) and over-grants (INL is responsible for Brisbane
// sectors it only covers, so an arriving Inverell would take them off whoever is working them).
//
// Primary versus member
//
// Every sector in a group can also be worked on its own, so a group can be split between
// controllers. Whether logging on takes the whole group depends on whether the sector is the
// group's primary:
//
//   - The primary logs on -> the whole group is theirs.
//   - Any other member logs on -> that sector alone; the rest is requested the ordinary way.
//
// The dataset does not mark the primary, so it is derived, most reliable signal first:
//
//   1. The member responsible (recursively) for every other member of the position - ASP for Alice
//      Springs, ISA for Mt Isa.
//   2. Failing that, the one member whose FullName is the position's name - "Adelaide TCU".
//
// A position with neither has no primary: Walton TCU and the smaller towers are peer roles (ADC,
// ADCC, SMC), and each member stands alone.
public static class PrimaryPosition
{
    // Guards against a cyclical grouping in the dataset; real sector data nests nowhere near this
    // deep.
    const int MaxDepth = 8;

    public static List<SectorsVolumes.Sector> DefaultSectorsFor(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return new List<SectorsVolumes.Sector>();

        var own = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == callsign);
        if (own == null)
            return new List<SectorsVolumes.Sector>();

        var group = GroupOwnedBy(own);

        // Not a primary: their own sector only. The rest of the group has to be requested, which
        // is what lets a group be split between controllers.
        if (group == null)
            return new List<SectorsVolumes.Sector> { own };

        var online = OnlineRealAtcs();

        return group
            // Someone else logged on directly as a member keeps it. A member sharing the callsign
            // being computed is always kept: members often carry their primary's callsign, and the
            // controller logging on is online under it themselves, so an unqualified "is anyone
            // online as this" check would strip them of their own group.
            .Where(member => string.Equals(member.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
                             || !online.Any(atc => string.Equals(atc.Callsign, member.Callsign,
                                                                 StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // The sectors this session should hold right now: DefaultSectorsFor, less any top-down cover
    // whose own controller is logged on. Benalla normally covers MAE, but not while ML_APP is
    // online.
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
    // none). See the header for how the primary is derived.
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

        // 2. Otherwise the member the position is named after, and only when exactly one member
        // matches: a guess here takes somebody's airspace off them.
        if (string.IsNullOrEmpty(positionName))
            return false;

        var named = members
            .Where(m => string.Equals(m.FullName, positionName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return named.Count == 1 && named[0].Equals(sector);
    }

    // The sectors a claim for `sector` would sweep up that somebody is logged on as, sent as that
    // claim's exclusion list. A claim expands through ResponsibleSectors (top-down cover: Benalla
    // works Melbourne Approach's airspace when nobody is on it), which says nothing about whether
    // anybody is on it. Without the exclusions an enroute controller logging on would take an
    // online approach controller's sectors off them.
    //
    // This walks ResponsibleSectors rather than the Positions.xml grouping the rest of the class
    // uses, deliberately: it models what the backend's claim expands to, not what a position owns.
    //
    // What is withheld is each online controller's whole position (DefaultSectorsFor), not only
    // the sector their callsign names: Melbourne Approach online withholds MAE, MDN, MDS, MAV and
    // MAW.
    //
    // Answered from Network.GetOnlineATCs rather than by the backend: vatSys's own ATC list
    // reflects a logon within seconds, where the backend's view of who is online lags, and it
    // includes controllers who are not running this plugin.
    public static List<string> StaffedCoveredSectors(SectorsVolumes.Sector sector, string? ownCallsign) =>
        StaffedCoveredSectors(sector, ownCallsign, OnlineRealAtcs());

    public static List<string> StaffedCoveredSectors(
        SectorsVolumes.Sector sector,
        string? ownCallsign,
        IEnumerable<NetworkATC> onlineAtcs)
    {
        var claimable = new HashSet<string>(CoveredBy(sector).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var staffed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var atc in onlineAtcs.Where(a => a.IsRealATC && !string.IsNullOrEmpty(a.Callsign)))
        {
            if (string.Equals(atc.Callsign, ownCallsign, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var theirs in DefaultSectorsFor(atc.Callsign))
            {
                if (claimable.Contains(theirs.Name))
                    staffed.Add(theirs.Name);
            }
        }

        // Never the sector being claimed: excluding it would turn the claim into a no-op.
        staffed.Remove(sector.Name);

        return staffed.ToList();
    }

    // Everything a claim for this sector expands to on the backend: the sector plus its
    // responsible sectors, recursively. Mirrors the backend's covered().
    public static List<SectorsVolumes.Sector> CoveredBy(SectorsVolumes.Sector sector)
    {
        var covered = new List<SectorsVolumes.Sector>();
        Collect(sector, covered, depth: 0);
        return covered;
    }

    // The sector plus everything it is responsible for, recursively.
    static void Collect(SectorsVolumes.Sector sector, List<SectorsVolumes.Sector> into, int depth)
    {
        // Equals, not ==: Sector.Equals is callsign-based and == is not overridden, so two
        // instances of the same sector compare unequal under ==. Also the cycle guard: a sector
        // already collected is not walked again.
        if (into.Any(s => s.Equals(sector)))
            return;

        into.Add(sector);

        if (depth >= MaxDepth)
            return;

        foreach (var subsector in sector.SubSectors.ToList())
            Collect(subsector, into, depth + 1);
    }

    // Flow (FMP) positions hold flow sectors and nothing else. An FMP controller sets transmit on
    // enroute and TMA lines to talk to those units, not to take their airspace. The backend
    // enforces the same rule (claimGroup); this copy keeps the plugin from adding the sector or
    // asking at all.
    public static bool IsFlowCallsign(string? callsign) =>
        !string.IsNullOrEmpty(callsign) && callsign!.EndsWith("_FMP", StringComparison.OrdinalIgnoreCase);

    // Whether a session logged on as ownCallsign may claim this sector outright. A flow position
    // may claim only flow sectors. It can still be given someone else's sector by requesting it -
    // relief cover the holder approves - but never airspace nobody holds.
    public static bool MayOwn(SectorsVolumes.Sector sector, string? ownCallsign) =>
        !IsFlowCallsign(ownCallsign) || IsFlowCallsign(sector.Callsign);

    // Tells a flow position it tried to take airspace nobody holds.
    public static void ReportFlowCannotAssume(IEnumerable<SectorsVolumes.Sector> sectors, string? ownCallsign)
    {
        var names = sectors.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            return;

        ActionLog.Log("Primary",
            $"Unable to assume {string.Join(", ", names)} - {ownCallsign} is FMP and nobody holds it to request it from",
            new { action = "flow_cannot_assume_unheld", sectors = names, own_callsign = ownCallsign });

        var message = $"Unable to assume sector - position {ownCallsign} is FMP."
                      + Environment.NewLine + Environment.NewLine
                      + string.Join(Environment.NewLine, names.Select(SectorDescription.Describe))
                      + Environment.NewLine + Environment.NewLine
                      + "An FMP can only provide relief cover for a sector another controller holds, by requesting it from them.";

        MMI.InvokeOnGUI(() =>
        {
            var notice = new SectorNoticeWindow(message, "Unable to assume sector");
            if (System.Windows.Forms.Application.OpenForms["MainForm"] is System.Windows.Forms.Form mainForm)
                notice.Show(mainForm);
            else
                notice.Show();
            notice.BringToFront();
        });
    }

    // IsRealATC filters out observers and the other non-controlling connections in this list, so
    // only a real controller counts as staffing a sector.
    public static List<NetworkATC> OnlineRealAtcs() =>
        (Network.GetOnlineATCs ?? new List<NetworkATC>())
        .Where(a => a.IsRealATC && !string.IsNullOrEmpty(a.Callsign))
        .ToList();
}
