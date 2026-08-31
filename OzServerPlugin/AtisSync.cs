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
// Deliberately push-on-change only, subscribed straight to vatsys.ATIS.Updated (which fires from
// UpdateATIS/UpdateATISNoTTS on a real content/letter change, and from DeleteATIS when the ATIS is
// torn down) - no periodic heartbeat. The backend's own staleness rule (90 minutes since the last
// push - see PruneStaleAtisJob) is what ages an abandoned entry out, not a liveness ping, so there is
// nothing to send here beyond "the content changed to this".
//
// Only vatsys.ATIS itself (slot 0) is covered. The SDK's other 3 ATIS slots (vatsys.Network.
// ConnectATIS/DisconnectATIS/UpdateATIS(atisIndex, ...)) are a separate public API meant for a
// third-party multi-position ATIS plugin to manage its own extra broadcasts - not something this
// plugin creates - and the one thing that would let a passive observer identify what's connected in
// those slots (Network.GetATISCallsign) is internal to vatSys, unreadable from here. Syncing slots
// 1-3 would need that other plugin to expose its own state to this one; out of scope until it exists.
public class AtisSync
{
    readonly OzServerApiClient _api = new();

    public AtisSync()
    {
        vatsys.ATIS.Updated += (_, _) => _ = PushAsync();
    }

    async Task PushAsync()
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

        if (!Network.IsConnected)
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
            Errors.Add(new Exception($"Couldn't push ATIS to OzServer: {ex.Message}", ex), "OzServer");
        }
    }
}
