using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Pushes the controller's built-in vatSys ATIS (vatsys.ATIS, the ATIS Editor's broadcast) to
// OzServer's /atis endpoint, so a controller taking over the station later can pull the last
// broadcast back in with GET /atis/{icao}.
//
// Pushed on change, from vatsys.ATIS.Updated (which also fires when the ATIS is deleted), and
// again every RefreshInterval while it is still broadcasting. The backend ages an entry out 60
// minutes after its last push (ATIS_RETAIN_MINUTES in OzServer-API), which would otherwise remove
// an ATIS that is on the air and has not changed for an hour.
//
// Only vatsys.ATIS is covered. vatSys's other three ATIS slots (Network.ConnectATIS and
// UpdateATIS(atisIndex, ...)) are for a third-party ATIS plugin's own broadcasts, and what is
// connected in them cannot be read from here: Network.GetATISCallsign is internal. See
// BadVectorsAtisSync for ATISPlugin's.
public class AtisSync
{
    // Well inside the backend's 60 minute retention, so one missed push does not cost the entry.
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _refreshTimer;

    public AtisSync()
    {
        vatsys.ATIS.Updated += (_, _) => _ = PushAsync();
        // PushAsync declines when nothing is broadcasting or there is no session.
        _refreshTimer = new System.Threading.Timer(_ => _ = PushAsync(quiet: true), null, RefreshInterval, RefreshInterval);
    }

    // quiet: the periodic re-push, whose failure is not worth the Errors window - the next change
    // or interval sends the same thing again.
    async Task PushAsync(bool quiet = false)
    {
        if (OzServerMode.Disabled)
            return;

        var icao = vatsys.ATIS.AirportIcao;
        var letter = vatsys.ATIS.Code;
        var content = vatsys.ATIS.Content;

        // A deleted or not-yet-configured ATIS has nothing to send. Any entry already on the
        // server is left to age out rather than cleared from here.
        if (string.IsNullOrEmpty(icao) || letter == null || content == null || content.Count == 0 || !vatsys.ATIS.IsBroadcasting)
            return;

        // NetworkServer.Current: on LocalHost or an unrecognised SweatBox the push would fail before
        // it was sent, and reach the Errors window on every ATIS change.
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
                OzErrors.Add(new Exception($"Couldn't push ATIS to OzServer: {ex.Message}", ex), "OzServer");
        }
    }
}
