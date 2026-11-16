using System;
using System.Linq;
using System.Reflection;
using vatsys;

namespace OzServerPlugin;

// Which of OzServer's three connection targets this session is on - live VATSIM, SweatBox-1 or
// SweatBox-2 - read from vatSys's Network class in one place, as NetworkIdentity does for CID and
// callsign.
//
// Network.IsOfficialServer is true only for a numbered VATSIM server; both SweatBoxes and
// LocalHost read false. Network.ServerIP is the FSD hostname dialled.
//
// Which SweatBox is which comes from vatSys, not from a hostname list kept here. The connection
// window's "SweatBox-1" and "SweatBox-2" dial whatever the loaded profile names (Profile.xml's
// <SweatBox url> and <SweatBox2 url>, held in Network.SweatBoxURL and Network.SweatBox2URL), so
// ServerIP equalling SweatBoxURL identifies SweatBox-1, and a renamed server is still recognised.
// Both fields are internal to vatSys and read by reflection; if they cannot be read, the known
// hostnames below are the fallback, and any address that matches nothing is logged.
//
// Only VATPAC's own servers count. A profile from another division points the same two slots at
// its own training servers (or leaves vatSys's sweatbox.vatsim.net defaults), and OzServer's "sb1"
// is one shared room - matching on the slot alone would put controllers from an unrelated exercise
// into it.
//
// LocalHost is not supported: Current returns null for it, as for any unrecognised server, and
// every API call then fails as it does while disconnected (see OzServerApiClient.GetCredentials).
static class NetworkServer
{
    const string VatpacDomain = ".vatpac.org";

    // Every address each SweatBox has been known by, accepted alongside the profile's own value -
    // the fallback when that cannot be read.
    static readonly string[] KnownSweatBox1Hosts = { "sweatbox1.vatpac.org", "sweatbox01-training.vatpac.org" };
    static readonly string[] KnownSweatBox2Hosts = { "sweatbox2.vatpac.org", "sweatbox02-training.vatpac.org" };

    static readonly FieldInfo? SweatBox1Field =
        typeof(Network).GetField("SweatBoxURL", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    static readonly FieldInfo? SweatBox2Field =
        typeof(Network).GetField("SweatBox2URL", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    // Read each time rather than cached: the profile is loaded after plugins are constructed, and
    // can be changed without restarting vatSys.
    static string? ProfileHost(FieldInfo? field)
    {
        try
        {
            return field?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    static bool IsSweatBox(string ip, FieldInfo? profileField, string[] knownHosts)
    {
        if (!ip.EndsWith(VatpacDomain, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(ip, ProfileHost(profileField), StringComparison.OrdinalIgnoreCase)
               || knownHosts.Contains(ip, StringComparer.OrdinalIgnoreCase);
    }

    // Must match the API's allowlist (SERVERS in auth.ts). Kept in sync by hand.
    public const string Live = "live";
    public const string SweatBox1 = "sb1";
    public const string SweatBox2 = "sb2";

    // The last unrecognised ServerIP logged. Current is read by every API request, so each address
    // is logged once rather than on every read.
    static string? _lastLoggedUnrecognised;

    // Not gated on Network.IsConnected. IsOfficialServer and ServerIP keep their last value for a
    // moment after a disconnect, as Callsign and ControllerId do, and GracefulDisconnectReleaser
    // releases this session's sectors in that window - after Network.Disconnected has fired - on
    // the server they were claimed on. Gating on IsConnected would fail that call and leave the
    // sectors to the server's disconnect grace period. Before any connection, ServerIP is blank
    // and IsOfficialServer false, which returns null below.
    public static string? Current
    {
        get
        {
            if (Network.IsOfficialServer) return Live;

            var ip = Network.ServerIP ?? "";
            if (IsSweatBox(ip, SweatBox1Field, KnownSweatBox1Hosts)) return SweatBox1;
            if (IsSweatBox(ip, SweatBox2Field, KnownSweatBox2Hosts)) return SweatBox2;

            // Blank (never connected, or LocalHost's default) or unrecognised. A non-blank address
            // is logged, so a hostname mismatch can be diagnosed in the field.
            if (!string.IsNullOrEmpty(ip)
                && !string.Equals(ip, _lastLoggedUnrecognised, StringComparison.OrdinalIgnoreCase))
            {
                _lastLoggedUnrecognised = ip;
                ActionLog.Log("Network", $"Unrecognized server - OzServer will not operate here: ServerIP='{ip}'");
            }

            return null;
        }
    }
}
