using System;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// How a sector is written out to the controller: "ARA - Arafura (BN-ISA_CTR)", the same form
// OzServerSectorsWindow uses in its lists. The position callsign is included because it tells a
// controller who they are dealing with, which the code and full name do not.
//
// Resolved against the local dataset, since a request payload carries only the name and full name.
// A sector missing from the dataset still prints its name, and one without a full name still
// prints its callsign.
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

    // "STR - Sturt (held by BN-TRT_CTR)", for a sector discussed in terms of who holds it. The
    // holder's callsign replaces the sector's own: two callsigns on one line would read as a
    // contradiction.
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
