using System;
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
// The two SweatBox hostnames below are VATPAC's own dedicated training servers, not the
// VATSIM-wide sweatbox.vatsim.net/-2 defaults this class originally assumed - confirmed against
// the real ServerIP a live SweatBox-1 connection reported (via the fallback log line below, which
// is exactly what caught this the first time): VATPAC's Profile.xml overrides both targets
// entirely, division-wide, the same way any division's own training server would. SweatBox-2's
// value below mirrors SB1's naming and has not yet been directly observed - if it's wrong, the
// same fallback line will say so the next time someone connects to it.
//
// LocalHost is not a supported environment: OzServer simply does not operate there (every API
// call fails the same way it does while fully disconnected - see GetCredentials), and Current
// returns null for it exactly as it does for "not connected at all". There is nothing to
// distinguish LocalHost from "an unrecognized server" from OzServer's point of view, and no
// reason to.
static class NetworkServer
{
    const string SweatBox1Host = "sweatbox01-training.vatpac.org";
    const string SweatBox2Host = "sweatbox02-training.vatpac.org"; // unconfirmed - see comment above

    // Must match the API's own allowlist (auth.ts SERVERS) exactly. Kept in sync by hand - there is
    // no shared source between a C# plugin and a Node API for this.
    public const string Live = "live";
    public const string SweatBox1 = "sb1";
    public const string SweatBox2 = "sb2";

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
            if (string.Equals(ip, SweatBox1Host, StringComparison.OrdinalIgnoreCase)) return SweatBox1;
            if (string.Equals(ip, SweatBox2Host, StringComparison.OrdinalIgnoreCase)) return SweatBox2;

            // Blank (never connected, or LocalHost's own default) or unrecognized. Logged only when
            // there is actually something to report - forwarded to client_logs the same way any
            // other ActionLog line is, so a hostname mismatch (like the one that caught SB1's real
            // address) is diagnosable in the field.
            if (!string.IsNullOrEmpty(ip))
                ActionLog.Log("Network", $"Unrecognized server - OzServer will not operate here: ServerIP='{ip}'");

            return null;
        }
    }
}
