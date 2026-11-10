using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Pushes the controller's own built-in vatSys ATIS (vatsys.ATIS - the ATIS Editor's single voice/
// text broadcast, "slot 0" of vatSys's internal 4-slot ATIS API) to OzServer's /atis endpoint
// whenever its letter/content actually changes, so a controller taking over the same station later
// can pull the last-broadcast info back in via GET /atis/{icao} (AtisController, backend).
//
// Pushed on change, subscribed straight to vatsys.ATIS.Updated (which fires from
// UpdateATIS/UpdateATISNoTTS on a real content/letter change, and from DeleteATIS when the ATIS is
// torn down) - and again every RefreshInterval for as long as it is still broadcasting.
//
// The second half exists because the backend ages an entry out 60 minutes after its last push (see
// OzServer-API's runMaintenance/ATIS_RETAIN_MINUTES), which is how an abandoned one goes away. With
// change-only pushes that same rule also removed an ATIS that was still on the air and simply had
// not changed for an hour - settled weather, which is most of the time - from the map and from the
// record the next controller on the station picks up.
//
// Only vatsys.ATIS itself (slot 0) is covered. The SDK's other 3 ATIS slots (vatsys.Network.
// ConnectATIS/DisconnectATIS/UpdateATIS(atisIndex, ...)) are a separate public API meant for a
// third-party multi-position ATIS plugin to manage its own extra broadcasts - not something this
// plugin creates - and the one thing that would let a passive observer identify what's connected in
// those slots (Network.GetATISCallsign) is internal to vatSys, unreadable from here. Syncing slots
// 1-3 would need that other plugin to expose its own state to this one; out of scope until it exists.
public class AtisSync
{
    // Comfortably inside the backend's 60 minute retention, so one missed push does not cost the entry.
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _refreshTimer;

    public AtisSync()
    {
        vatsys.ATIS.Updated += (_, _) => _ = PushAsync();
        // PushAsync already declines when nothing is broadcasting or there is no session.
        _refreshTimer = new System.Threading.Timer(_ => _ = PushAsync(quiet: true), null, RefreshInterval, RefreshInterval);
    }

    // quiet: the periodic re-push. A failure there is not worth the Errors window - the next change,
    // or the next interval, sends the same thing again.
    async Task PushAsync(bool quiet = false)
    {
        var icao = vatsys.ATIS.AirportIcao;
        var letter = vatsys.ATIS.Code;
        var content = vatsys.ATIS.Content;

        // Empty icao/no letter/no content covers DeleteATIS (torn down) and the not-yet-configured
        // state - nothing broadcast, so nothing worth telling OzServer. The entry already on the
        // server (if any) just ages out on its own via PruneStaleAtisJob rather than being cleared
        // explicitly here.
        if (string.IsNullOrEmpty(icao) || letter == null || content == null || content.Count == 0 || !vatsys.ATIS.IsBroadcasting)
            return;

        // NetworkServer.Current: on LocalHost or an unrecognised SweatBox the push would fail before
        // it was sent, and put that in the Errors window on every ATIS change.
        if (!Network.IsConnected || NetworkServer.Current == null)
            return;

        var dto = new OzServerAtisUpdateDto
        {
            Icao = icao,
            AtisLetter = letter.Value.ToString(),
            Content = new Dictionary<string, string>(content),
            Frequency = Network.ATISFrequency?.GetFSDFrequency(),
        };

        try
        {
            await _api.UpdateAtisAsync(dto);
        }
        catch (Exception ex)
        {
            if (!quiet)
                Errors.Add(new Exception($"Couldn't push ATIS to OzServer: {ex.Message}", ex), "OzServer");
        }
    }
}
