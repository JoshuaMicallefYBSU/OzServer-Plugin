using vatsys;

namespace OzServerPlugin;

// Who this session is, as OzServer needs it: a numeric CID and a callsign.
//
// vatSys stores both exactly as typed into the connection window - Network.Connect writes them
// straight to its own `controllerID`/`callsign` fields - and exposes the CID only as
// Network.ControllerId, a string. There is no numeric accessor anywhere on Network or NetworkATC, so
// parsing is unavoidable; the point of this class is that it happens once, in one place, rather than
// in each caller with its own idea of what to do when it fails.
//
// It used to be parsed in three places with three different failure behaviours: the API client threw,
// FdrSync skipped the flight, and the ownership tracker ignored the result and carried on with cid 0
// - which would have attributed a sector to "controller 0" rather than to nobody. Now they all get
// the same answer, and the one that needs to fail loudly is the one that asks for it that way.
static class NetworkIdentity
{
    // Null until connected under a real callsign with a numeric CID. Anything that talks to OzServer
    // has nothing meaningful to say before that point.
    public static (int Cid, string Callsign)? Current
    {
        get
        {
            var callsign = Network.Callsign;

            return string.IsNullOrEmpty(callsign) || !int.TryParse(Network.ControllerId, out var cid)
                ? null
                : (cid, callsign);
        }
    }

    // 0 when not connected. Only for callers where "not me" is the right reading of an absent
    // identity - never for attributing ownership, where 0 would be a real controller id.
    public static int CidOrZero => Current?.Cid ?? 0;
}
