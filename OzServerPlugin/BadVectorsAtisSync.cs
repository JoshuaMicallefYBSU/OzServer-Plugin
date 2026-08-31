using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Mirrors AtisSync, but for badvectors/ATISPlugin (https://github.com/badvectors/ATISPlugin) - the
// third-party multi-position ATIS plugin most Australian/Pacific profiles ship by default, which
// manages its own up-to-four broadcasts on vatSys's other ATIS slots (see AtisSync's own comment for
// why those aren't reachable through the public vatSys SDK/vatsys.ATIS).
//
// ATISPlugin was never designed for inter-plugin use - there is no reference assembly to compile
// against, and no public contract beyond "these members currently exist" (its own Plugin class
// exposes static ATIS1..ATIS4 ATISControl instances). Everything here goes through reflection rather
// than a project reference, so a vatSys install without ATISPlugin - or a future ATISPlugin release
// that renamed/removed these members - just finds nothing and this class quietly does nothing,
// rather than failing to build or throwing at runtime.
public class BadVectorsAtisSync
{
    static readonly string[] SlotPropertyNames = { "ATIS1", "ATIS2", "ATIS3", "ATIS4" };

    // Re-scanned on every tick rather than resolved once: vatSys's plugin load order between two
    // separate plugins isn't guaranteed, so if OzServerPlugin's own constructor runs before
    // ATISPlugin's, ATIS1..4 are all still null the first few ticks. Cheap enough at this interval to
    // just keep checking rather than build a one-shot-with-retry state machine.
    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    readonly OzServerApiClient _api = new();
    readonly Timer _timer;

    // Every ATISControl instance we've already attached our handler to, by reference - once a slot
    // is found and hooked, later ticks must not hook it again (that would fire PushAsync multiple
    // times per real broadcast). The control objects live for the plugin's lifetime once created,
    // so there's nothing to remove this on.
    readonly List<object> _subscribed = new();

    public BadVectorsAtisSync()
    {
        _timer = new Timer(_ => Rescan(), null, RescanInterval, RescanInterval);
    }

    void Rescan()
    {
        try
        {
            var pluginType = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ATISPlugin")
                ?.GetType("ATISPlugin.Plugin");
            if (pluginType == null)
                return;

            foreach (var propName in SlotPropertyNames)
            {
                // ATIS1..4 are plain public static fields on ATISPlugin.Plugin (confirmed against its
                // source), not properties - GetProperty silently returns null for these, which is why
                // this used to never find anything. GetField first, with a GetProperty fallback in
                // case a future ATISPlugin release turns them into auto-properties instead.
                var member = pluginType.GetField(propName, BindingFlags.Public | BindingFlags.Static) as MemberInfo
                             ?? pluginType.GetProperty(propName, BindingFlags.Public | BindingFlags.Static);
                var control = member switch
                {
                    FieldInfo f => f.GetValue(null),
                    PropertyInfo p => p.GetValue(null),
                    _ => null,
                };
                if (control == null || _subscribed.Any(c => ReferenceEquals(c, control)))
                    continue;

                var statusChanged = control.GetType().GetEvent("StatusChanged");
                if (statusChanged == null)
                    continue;

                _subscribed.Add(control);
                statusChanged.AddEventHandler(control, new EventHandler((sender, e) => { _ = PushAsync(sender); }));
            }
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't hook into badvectors/ATISPlugin: {ex.Message}", ex), "OzServer");
        }
    }

    async Task PushAsync(object? control)
    {
        if (control == null || !Network.IsConnected)
            return;

        var type = control.GetType();
        var icao = type.GetProperty("ICAO")?.GetValue(control) as string;
        var letter = type.GetProperty("ID")?.GetValue(control) as char?;
        var lines = type.GetProperty("Lines")?.GetValue(control) as IEnumerable;
        var frequencyDisplay = type.GetProperty("FrequencyDisplay")?.GetValue(control) as string;

        // Deliberately NOT gated on Broadcasting: ATISControl.Save() (source confirmed) sets
        // Broadcasting = false via BroadcastStop() *before* updating ID/Lines and firing
        // StatusChanged, and only BroadcastStart() - which never raises StatusChanged - sets it back
        // to true afterwards. Gating on Broadcasting here meant this fired for every real update and
        // discarded it every time. Empty icao/letter/content is what actually distinguishes
        // ATISControl.Create() (station just opened, nothing broadcast yet - Lines exists but every
        // line's Value is still blank) and Delete() (station torn down, ICAO/Lines cleared) from a
        // real Save(), so that's the guard instead. The entry already on the server (if any) just
        // ages out via PruneStaleAtisJob rather than being cleared explicitly here.
        if (string.IsNullOrEmpty(icao) || letter == null)
            return;

        var content = ReadContent(lines);
        if (content.Count == 0)
            return;

        var dto = new OzServerAtisUpdateDto
        {
            Icao = icao!,
            AtisLetter = letter.Value.ToString(),
            Content = content,
            Frequency = ParseFsdFrequency(frequencyDisplay),
        };

        try
        {
            await _api.UpdateAtisAsync(dto);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't push ATISPlugin ATIS to OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    // Same field name/value shape as vatsys.ATIS.Content (see OzServerAtisUpdateDto) - one entry per
    // visible ATISLine, keyed by its Name (WIND, VIS, QNH, ...). OFCW_NOTIFY/ZULU are ATISPlugin's own
    // pseudo-lines (an internal notify flag and the raw spoken-Zulu text), not real ATIS fields -
    // skipped the same way ATISControl.GetInfo() skips them when building the text it actually
    // broadcasts.
    static Dictionary<string, string> ReadContent(IEnumerable? lines)
    {
        var content = new Dictionary<string, string>();
        if (lines == null)
            return content;

        PropertyInfo? nameProp = null, valueProp = null, visibleProp = null;

        foreach (var line in lines)
        {
            var type = line.GetType();
            nameProp ??= type.GetProperty("Name");
            valueProp ??= type.GetProperty("Value");
            visibleProp ??= type.GetProperty("Visible");

            var visible = visibleProp?.GetValue(line) as bool? ?? false;
            var name = nameProp?.GetValue(line) as string;

            if (!visible || string.IsNullOrEmpty(name) || name is "OFCW_NOTIFY" or "ZULU")
                continue;

            content[name!] = valueProp?.GetValue(line) as string ?? "";
        }

        return content;
    }

    // Inverse of vatSys's own Conversions.FSDFrequencyToString ("1" + (freq/1000.0).ToString("00.0##")
    // - see the decompiled reference under .csproj/Conversions.cs). ATISPlugin's FrequencyDisplay is
    // set from the same Normalize25KhzFrequency formatting ("1xx.xxx"), so this round-trips a value
    // like "132.500" back to 32500 - the same FSD frequency Network.ATISFrequency?.GetFSDFrequency()
    // would report for the equivalent vatsys.ATIS station, keeping this consistent with AtisSync's
    // own Frequency field.
    static int? ParseFsdFrequency(string? display) =>
        double.TryParse(display, out var mhz) ? (int)Math.Round((mhz - 100) * 1000) : null;
}
