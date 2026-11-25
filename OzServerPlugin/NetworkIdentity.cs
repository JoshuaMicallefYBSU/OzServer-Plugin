using RossCarlson.Vatsim.Network;
using vatsys;

namespace OzServerPlugin;

// Who this session is, as OzServer needs it: a numeric CID and a callsign.
//
// vatSys exposes the CID only as Network.ControllerId, a string holding whatever was typed into the
// connection window; there is no numeric accessor. It is parsed here, in one place, so every caller
// gets the same answer when it is missing or not a number.
static class NetworkIdentity
{
    // Null until connected under a callsign with a numeric CID. Nothing can be said to OzServer
    // before then.
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

    // Whether this session connected as an observer, read from the two fields the vatSys connection
    // window sets: Position (Network.Facility) and Rating (Network.Rating). OBS in either is an
    // observer connection.
    //
    // Not Network.Me.IsRealATC: that flag describes the ATC record the network has published about
    // this session, and reads false for a real controller for some seconds after Connected. A check
    // made on it at connect time refuses a real controller their resume and their sector
    // allocation. Facility and Rating are correct immediately.
    public static bool IsObserver =>
        Network.Rating == NetworkRating.OBS || Network.Facility == NetworkFacility.OBS;

    // 0 when not connected. Only for callers where "not me" is the right reading of an absent
    // identity - never for attributing ownership, where 0 would be recorded as a controller.
    public static int CidOrZero => Current?.Cid ?? 0;
}
