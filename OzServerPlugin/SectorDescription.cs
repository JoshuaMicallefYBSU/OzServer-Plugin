using System;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// How a sector is written out to the controller: "ARA - Arafura (BN-ISA_CTR)".
//
// The callsign is the point. A bare three-letter code says nothing about which frequency or which
// job is involved, and the full name alone still does not - the position callsign is what tells a
// controller who they are actually dealing with. It is the same shape OzServerSectorsWindow puts in
// its own lists, so a sector reads identically wherever it is met.
//
// Resolved against the local dataset rather than a request payload, which carries only the name and
// full name.
//
// Degrades a piece at a time rather than all at once: a sector missing from the dataset still prints
// its name, one without a full name still prints its callsign. A notice that has lost a sector's
// detail is worth strictly more than one that has lost the sector.
public static class SectorDescription
{
    public static string Describe(SectorsVolumes.Sector? sector, string? fallbackName = null)
    {
        if (sector == null)
            return fallbackName ?? "";

        var described = string.IsNullOrEmpty(sector.FullName)
            ? sector.Name
            : $"{sector.Name} - {sector.FullName}";

        return string.IsNullOrEmpty(sector.Callsign) ? described : $"{described} ({sector.Callsign})";
    }

    public static string Describe(string name) =>
        Describe(Find(name), name);

    // "STR - Sturt (held by BN-TRT_CTR)" - for a sector being discussed in terms of who has it now.
    //
    // The holder's callsign takes the place of the sector's own rather than joining it. A sector
    // carries a position callsign of its own, and printing both puts two callsigns on one line that
    // read as a contradiction - the question here turns on which controller is holding it, so that
    // is the one that belongs in the brackets.
    public static string DescribeWithOwner(string name, string? ownerCallsign)
    {
        var sector = Find(name);

        var described = sector == null || string.IsNullOrEmpty(sector.FullName)
            ? name
            : $"{sector.Name} - {sector.FullName}";

        return string.IsNullOrEmpty(ownerCallsign) ? described : $"{described} (held by {ownerCallsign})";
    }

    static SectorsVolumes.Sector? Find(string name) =>
        SectorsVolumes.Sectors.FirstOrDefault(sector =>
            string.Equals(sector.Name, name, StringComparison.OrdinalIgnoreCase));
}
