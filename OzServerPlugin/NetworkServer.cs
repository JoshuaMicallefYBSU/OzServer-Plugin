using System;
using System.Linq;
using System.Reflection;
using vatsys;

namespace OzServerPlugin;

// Which of OzServer's 3 supported vatSys connection targets this session is on - live VATSIM,
// SweatBox-1 or SweatBox-2 - read the same way NetworkIdentity reads CID and callsign: from
// vatSys's own Network class, once, in one place.
//
// Network.IsOfficialServer and Network.ServerIP are public and static but unused anywhere else in
// this plugin. IsOfficialServer is true only for a real numbered VATSIM server; SweatBox-1,
// SweatBox-2 and LocalHost all read false. ServerIP is the literal FSD hostname dialed.
//
// Which SweatBox is which is read from vatSys rather than from a list of hostnames kept here.
//
// The connection window offers "SweatBox-1" and "SweatBox-2"; what each one dials is whatever the
// loaded profile says (Profile.xml's <SweatBox url> and <SweatBox2 url>, which Profile.cs copies
// into Network.SweatBoxURL and Network.SweatBox2URL), and ServerIP is then simply that string. So
// the question "is this SweatBox-1" is answered exactly by "does ServerIP equal SweatBoxURL" - and
// answering it with a hardcoded hostname instead meant the plugin stopped recognising a SweatBox
// the day VATPAC renamed its servers. That has already happened once: this class knew them as
// sweatbox01-training/sweatbox02-training.vatpac.org, and the Australia profile now ships
// sweatbox1/sweatbox2.vatpac.org - on which every OzServer call was refused as "not available on
// this server".
//
// Both fields are internal to vatSys, so they are read by reflection. If a future vatSys renames
// them, the known hostnames below are the fallback, and the log line at the bottom still names any
// address that matches nothing.
//
// Only VATPAC's own servers count. A profile from another division points the same two slots at
// its own training servers (or leaves vatSys's sweatbox.vatsim.net defaults), and OzServer's "sb1"
// is one shared room - matching on the slot alone would put strangers from an unrelated exercise
// into it.
//
// LocalHost is not a supported environment: OzServer simply does not operate there (every API
// call fails the same way it does while fully disconnected - see GetCredentials), and Current
// returns null for it exactly as it does for "not connected at all". There is nothing to
// distinguish LocalHost from "an unrecognized server" from OzServer's point of view, and no
// reason to.
static class NetworkServer
{
    const string VatpacDomain = ".vatpac.org";

    // Every address each SweatBox has been known by. Only consulted when the profile's own values
    // cannot be read - see the class comment.
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

    // Must match the API's own allowlist (auth.ts SERVERS) exactly. Kept in sync by hand - there is
    // no shared source between a C# plugin and a Node API for this.
    public const string Live = "live";
    public const string SweatBox1 = "sb1";
    public const string SweatBox2 = "sb2";

    // The last unrecognised ServerIP logged. Current is read by every API request, and every poll
    // and flush makes one, so logging on each read wrote a line (and forwarded it to client_logs)
    // every second or two for as long as the session lasted. Once per address says the same thing.
    static string? _lastLoggedUnrecognised;

    // Deliberately NOT gated on Network.IsConnected, unlike an earlier version of this class -
    // IsOfficialServer/ServerIP hold their last real value for a moment after a disconnect (the
    // same way Network.Callsign/ControllerId do, which is what NetworkIdentity.Current already
    // relies on), and GracefulDisconnectReleaser's release-on-disconnect call runs exactly in that
    // window: Network.Disconnected has already fired, IsConnected is already false, but this
    // session's sectors still need to be released *for the server they were actually claimed on*.
    // Gating this on IsConnected made that call fail every time with "not connected", silently
    // skipping the release and leaving sectors to the 5-minute ungraceful grace window instead -
    // caught by actually disconnecting from a real session, not by reading the code. Before any
    // connection has ever been made, ServerIP is blank and IsOfficialServer is false, which falls
    // through to the unrecognized-server branch below and correctly returns null on its own,
    // without needing a separate "never connected" check.
    public static string? Current
    {
        get
        {
            if (Network.IsOfficialServer) return Live;

            var ip = Network.ServerIP ?? "";
            if (IsSweatBox(ip, SweatBox1Field, KnownSweatBox1Hosts)) return SweatBox1;
            if (IsSweatBox(ip, SweatBox2Field, KnownSweatBox2Hosts)) return SweatBox2;

            // Blank (never connected, or LocalHost's own default) or unrecognized. Logged only when
            // there is actually something to report - forwarded to client_logs the same way any
            // other ActionLog line is, so a hostname mismatch (like the one that caught SB1's real
            // address) is diagnosable in the field.
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
