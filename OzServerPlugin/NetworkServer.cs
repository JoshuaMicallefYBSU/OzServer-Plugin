using System;
using vatsys;

namespace OzServerPlugin;

// Which of the 4 fully isolated vatSys connection targets this session is actually on - live
// VATSIM, SweatBox-1, SweatBox-2, or LocalHost - read the same way NetworkIdentity reads CID and
// callsign: from vatSys's own Network class, once, in one place.
//
// Network.IsOfficialServer and Network.ServerIP are public and static but unused anywhere else in
// this plugin. IsOfficialServer is true only for a real numbered VATSIM server; SweatBox-1,
// SweatBox-2 and LocalHost all read false. ServerIP is the literal FSD hostname dialed - decompiled
// against the installed vatSys build, the two SweatBox hostnames below are VATSIM-wide defaults
// baked into vatSys's own static constructor, not something the Australia division's Profile.xml
// has ever needed to override - but a division profile's own <Servers> block could in principle
// replace them, which is exactly what the fallback log line below exists to surface if it ever
// happens: a real SweatBox connection would otherwise silently misclassify as NewSweatBox.
static class NetworkServer
{
    const string SweatBox1Host = "sweatbox.vatsim.net";
    const string SweatBox2Host = "sweatbox-2.vatsim.net";

    // Must match the API's own allowlist (auth.ts SERVERS) exactly. Kept in sync by hand - there is
    // no shared source between a C# plugin and a Node API for this.
    public const string Live = "live";
    public const string SweatBox1 = "sb1";
    public const string SweatBox2 = "sb2";
    public const string NewSweatBox = "newsb"; // LocalHost

    // Null until connected - same contract as NetworkIdentity.Current, so both fail the same way
    // for a caller that needs both (see OzServerApiClient.GetCredentials).
    public static string? Current
    {
        get
        {
            if (!Network.IsConnected) return null;
            if (Network.IsOfficialServer) return Live;

            var ip = Network.ServerIP ?? "";
            if (string.Equals(ip, SweatBox1Host, StringComparison.OrdinalIgnoreCase)) return SweatBox1;
            if (string.Equals(ip, SweatBox2Host, StringComparison.OrdinalIgnoreCase)) return SweatBox2;

            // Blank (LocalHost's own default) or unrecognized. Logged - and, via ClientLogForwarder,
            // forwarded to client_logs - so a hostname mismatch is diagnosable in the field instead
            // of silently misclassifying a real SweatBox connection as LocalHost.
            ActionLog.Log("Network", $"Server detection fell through to {NewSweatBox}: IsOfficialServer=false, ServerIP='{ip}'");
            return NewSweatBox;
        }
    }
}
