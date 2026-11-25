using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Controllers connected without the OzServer plugin (ML-TBD_CTR on plain vatSys, extending onto
// MUN and GUN).
//
// They claim nothing on OzServer, so their airspace would look free. Every plugin reports the
// controllers its vatSys can see - their position by Positions.xml, their frequencies and their
// controller information lines - and OzServer works out which sectors they are working (the API's
// external-control.ts: an "Extending MUN, GUN" line, then their frequencies, matched only to a
// sector of their own kind and unit). Those come back in the sync as controlled, with
// Owner.External set:
//
//   - They are shown in the Sectors window's Controlled list, marked "no plugin".
//   - They cannot be claimed (OzServer answers a conflict) and are never requested: there is no
//     plugin on the other end to answer. Notify explains instead that the sector has to be
//     coordinated, and its tags handed off, by hand.
//
// Constructed once by Plugin; reports while a session on a supported server is up.
public sealed class ExternalControl
{
    // The API counts a report for NETWORK_REPORT_WINDOW_SECONDS (45 s), three of these.
    static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(15);
    // vatSys raises OnlineATCChanged for every update from every controller, several a second, so
    // a change is reported once things have been quiet this long.
    static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(3);

    readonly OzServerApiClient _api = new();
    readonly Timer _timer;
    readonly Timer _changeTimer;
    int _reporting;
    string? _lastSignature;
    DateTime _lastReportedAt = DateTime.MinValue;

    public ExternalControl()
    {
        _timer = new Timer(_ => _ = ReportAsync(force: true), null, ReportInterval, ReportInterval);
        _changeTimer = new Timer(_ => _ = ReportAsync(force: false), null, Timeout.Infinite, Timeout.Infinite);
        Network.OnlineATCChanged += (_, _) => _changeTimer.Change(ChangeDelay, Timeout.InfiniteTimeSpan);
        Network.Disconnected += (_, _) => _lastSignature = null;
    }

    async Task ReportAsync(bool force)
    {
        if (OzServerMode.Disabled || !Network.IsConnected || NetworkServer.Current == null || NetworkIdentity.Current == null)
            return;
        if (Interlocked.Exchange(ref _reporting, 1) == 1)
            return;
        try
        {
            var controllers = SeenControllers();
            var signature = string.Join("|", controllers.Select(c =>
                $"{c.Callsign}:{string.Join(",", c.Frequencies)}:{string.Join(",", c.Sectors)}:{string.Join("\n", c.Atis)}"));
            // A change is sent at once; an unchanged list only as the periodic refresh that keeps it
            // counted.
            if (signature == _lastSignature && (!force || DateTime.UtcNow - _lastReportedAt < ReportInterval - TimeSpan.FromSeconds(1)))
                return;

            await _api.ReportNetworkControllersAsync(controllers).ConfigureAwait(false);
            _lastSignature = signature;
            _lastReportedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // An outage is reported by the calls that matter to the controller; this one retries in
            // ReportInterval.
            ActionLog.Log("External", $"Couldn't report the controllers online: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _reporting, 0);
        }
    }

    // Every controller vatSys lists, plugin users included - OzServer sorts them out. This session
    // is not in vatSys's list.
    static List<OzServerNetworkControllerDto> SeenControllers() =>
        PrimaryPosition.OnlineRealAtcs()
            .Where(atc => PrimaryPosition.IsControllerCallsign(atc.Callsign))
            .GroupBy(atc => atc.Callsign, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(atc => atc.Callsign, StringComparer.OrdinalIgnoreCase)
            .Select(atc => new OzServerNetworkControllerDto
            {
                Callsign = atc.Callsign.ToUpperInvariant(),
                RealName = string.IsNullOrEmpty(atc.RealName) ? null : atc.RealName,
                // Formatted here, as vatSys's Conversions.FSDFrequencyToString does but without the
                // PC's regional settings: with a decimal comma that gives "134,2", which OzServer
                // cannot read as a frequency.
                Frequencies = (atc.Frequencies ?? Array.Empty<int>())
                    .Select(f => "1" + (f / 1000.0).ToString("00.0##", CultureInfo.InvariantCulture))
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Distinct()
                    .ToList(),
                Sectors = PrimaryPosition.DefaultSectorsForCurrentSession(atc.Callsign).Select(s => s.Name).ToList(),
                Atis = (atc.ATIS ?? Array.Empty<string>()).Where(line => !string.IsNullOrWhiteSpace(line)).Take(20).ToList()
            })
            .ToList();

    // Each notice once in this long per controller and set of sectors: a transmit press or a claim
    // retry can reach the same answer several times a second.
    static readonly TimeSpan NoticeRepeat = TimeSpan.FromMinutes(2);
    static readonly Dictionary<string, DateTime> Shown = new(StringComparer.OrdinalIgnoreCase);

    // Explains that these sectors belong to a controller without the plugin and have to be dealt
    // with by hand. Safe from any thread.
    public static void Notify(IEnumerable<string> sectorNames, string callsign)
    {
        var names = sectorNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            return;

        var key = callsign + "|" + string.Join(",", names);
        lock (Shown)
        {
            if (Shown.TryGetValue(key, out var at) && DateTime.UtcNow - at < NoticeRepeat)
                return;
            Shown[key] = DateTime.UtcNow;
        }

        ActionLog.Log("External", $"{string.Join(", ", names)} held by {callsign} without the plugin - not requested",
            new { action = "external_holder_notice", sectors = names, holder = callsign });

        var nl = Environment.NewLine;
        var message = (names.Count == 1 ? "This sector is" : "These sectors are")
                      + $" controlled by {callsign}, who is not using the OzServer plugin:"
                      + nl + nl
                      + string.Join(nl, names.Select(SectorDescription.Describe))
                      + nl + nl
                      + "OzServer can't request or hand over sectors with them. Coordinate with "
                      + $"{callsign} directly, and hand off tags manually.";

        // MMI.InvokeOnGUI posts to vatSys's UI thread as it is given, so the work is contained here.
        var show = UiThread.Guard(() =>
        {
            var notice = new SectorNoticeWindow(message, "Controller not using OzServer");
            if (System.Windows.Forms.Application.OpenForms["MainForm"] is System.Windows.Forms.Form mainForm)
                notice.Show(mainForm);
            else
                notice.Show();
            notice.BringToFront();
        });
        MMI.InvokeOnGUI(() => show());
    }
}
