using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// The counterpart of AtisSync for badvectors/ATISPlugin (https://github.com/badvectors/ATISPlugin),
// the multi-position ATIS plugin most Australian/Pacific profiles ship, which manages up to four
// broadcasts on vatSys's other ATIS slots (see AtisSync for why those cannot be read through
// vatsys.ATIS).
//
// ATISPlugin has no reference assembly and no public contract beyond the members that currently
// exist (its Plugin class exposes static ATIS1..ATIS4 ATISControl instances), so everything here
// goes through reflection. A vatSys install without ATISPlugin, or a release that renames these
// members, finds nothing and this class does nothing.
public class BadVectorsAtisSync
{
    static readonly string[] SlotPropertyNames = { "ATIS1", "ATIS2", "ATIS3", "ATIS4" };

    // Re-scanned on every tick rather than resolved once: plugin load order is not guaranteed, so
    // ATIS1..4 can still be null for the first few ticks.
    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    readonly OzServerApiClient _api = new();
    readonly Timer _timer;

    // Every ATISControl already hooked, by reference, so a later tick does not hook it again and
    // push more than once per broadcast. The controls live for the plugin's lifetime, so nothing
    // is ever removed.
    readonly List<object> _subscribed = new();
    Type? _pluginType;

    // Every station still on the air is pushed again this often - see AtisSync.
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    DateTime _nextRefresh = DateTime.UtcNow + RefreshInterval;

    public BadVectorsAtisSync()
    {
        _timer = new Timer(_ => Rescan(), null, RescanInterval, RescanInterval);
    }

    void Rescan()
    {
        if (OzServerMode.Disabled)
            return;

        try
        {
            if (DateTime.UtcNow >= _nextRefresh)
            {
                _nextRefresh = DateTime.UtcNow + RefreshInterval;

                // PushAsync declines a station with nothing on it, and when there is no session.
                foreach (var control in _subscribed.ToList())
                    _ = PushAsync(control, quiet: true);
            }

            // Kept once found: an assembly is never unloaded. Until then by FullName, which the
            // runtime caches, rather than GetName(), which builds a new AssemblyName for every
            // assembly in the process on every pass.
            _pluginType ??= AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.FullName.StartsWith("ATISPlugin,", StringComparison.Ordinal))
                ?.GetType("ATISPlugin.Plugin");
            var pluginType = _pluginType;
            if (pluginType == null)
                return;

            foreach (var propName in SlotPropertyNames)
            {
                // ATIS1..4 are public static fields on ATISPlugin.Plugin, for which GetProperty
                // returns null. GetProperty is the fallback in case a later release makes them
                // properties.
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
            // This runs every five seconds; whatever broke will break again on the next pass.
            ErrorThrottle.Report("atisplugin-hook", $"Couldn't hook into badvectors/ATISPlugin: {ex.Message}", ex);
        }
    }

    async Task PushAsync(object? control, bool quiet = false)
    {
        if (OzServerMode.Disabled)
            return;

        // NetworkServer.Current: see AtisSync.PushAsync.
        if (control == null || !Network.IsConnected || NetworkServer.Current == null)
            return;

        var type = control.GetType();
        var icao = type.GetProperty("ICAO")?.GetValue(control) as string;
        var letter = type.GetProperty("ID")?.GetValue(control) as char?;
        var lines = type.GetProperty("Lines")?.GetValue(control) as IEnumerable;
        var frequencyDisplay = type.GetProperty("FrequencyDisplay")?.GetValue(control) as string;
        // IsZulu distinguishes a closed tower's free-text broadcast from a sectioned one
        // (ATISControl.GetInfo branches on it). DateTimeUtc is set to UtcNow in Save, so it is
        // when this letter was issued.
        var isZulu = type.GetProperty("IsZulu")?.GetValue(control) as bool? ?? false;
        var issued = type.GetProperty("DateTimeUtc")?.GetValue(control) as DateTime?;
        // ATISControl.Callsign is private ($"{icao}_ATIS", set in Create), hence NonPublic. It is
        // rebuilt below if it cannot be read.
        var callsign = type.GetProperty("Callsign", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(control) as string;

        // Not gated on Broadcasting: ATISControl.Save sets it false (BroadcastStop) before
        // updating ID/Lines and raising StatusChanged, and BroadcastStart - which raises nothing -
        // sets it back afterwards, so a gate on it would discard every real update. Empty icao,
        // letter or content is what tells Create (station opened, every line still blank) and
        // Delete (ICAO/Lines cleared) from a real Save. Any entry already on the server is left
        // to age out rather than cleared from here.
        if (string.IsNullOrEmpty(icao) || letter == null)
            return;

        var content = ReadContent(lines);
        var zuluText = isZulu ? ReadZulu(lines) : null;
        // A zulu broadcast carries only its own text - ReadContent skips the ZULU line, as
        // ATISControl.GetInfo does - so "nothing on this station" is tested differently for each
        // kind.
        if (isZulu ? string.IsNullOrWhiteSpace(zuluText) : content.Count == 0)
            return;

        // The handover record (POST /atis): per server, sections only, for the next controller on
        // this station rather than for public readers.
        if (content.Count > 0)
        {
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
                if (!quiet)
                    OzErrors.Add(new Exception($"Couldn't push ATISPlugin ATIS to OzServer: {ex.Message}", ex), "OzServer");
            }
        }

        // The broadcast itself (PUT /vatsim/atis/{icao}), which the website map and public
        // readers of /vatsim/atis use.
        //
        // Live only: that record has no server of its own, so publishing from a SweatBox session
        // would put training weather on the public map. The handover push above is per server and
        // still happens.
        if (NetworkServer.Current != NetworkServer.Live)
            return;

        // MODE and TIME_CHECK are editor settings, not broadcast information: the backend drops
        // both and refuses an ATIS with nothing else in it. A station whose mode was auto-selected
        // on Create but is otherwise unfilled is not worth publishing, or reporting as a failure.
        if (!isZulu && content.Keys.All(name => EditorOnlyLines.Contains(name)))
            return;

        var identity = NetworkIdentity.Current;
        if (identity == null)
            return;

        var published = new OzServerAtisPublishDto
        {
            Icao = icao!,
            Callsign = string.IsNullOrWhiteSpace(callsign) ? $"{icao}_ATIS" : callsign!,
            AtisLetter = letter.Value.ToString(),
            Type = isZulu ? "zulu" : "atis",
            Frequency = PublishableFrequency(frequencyDisplay),
            Issued = IsoUtc(issued),
            Content = isZulu ? zuluText! : (object)content,
            Changed = isZulu ? new List<string>() : ReadChanged(lines),
            Controller = new OzServerAtisControllerDto { Callsign = identity.Value.Callsign, Cid = identity.Value.Cid },
            Client = new OzServerAtisClientDto { Name = "vatSys ATIS Plugin", Version = PluginVersion(type) },
        };

        try
        {
            await _api.PublishAtisAsync(published);
        }
        catch (Exception ex)
        {
            if (!quiet)
                OzErrors.Add(new Exception($"Couldn't publish ATISPlugin ATIS to OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    static readonly HashSet<string> EditorOnlyLines = new() { "MODE", "TIME_CHECK" };

    // The backend takes the frequency as displayed text ("126.250") and refuses anything else. A
    // station whose display string is empty or oddly shaped still publishes its ATIS, without a
    // frequency.
    //
    // Reformatted, not just checked: ATISControl.FrequencyDisplay is built by
    // Normalize25KhzFrequency, which pads the number out to hertz ("126.250000"), so it is read as
    // a number and written back to three decimals.
    static readonly Regex DisplayFrequency = new(@"^\d{3}(\.\d+)?$", RegexOptions.Compiled);

    static string? PublishableFrequency(string? display)
    {
        var trimmed = display?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !DisplayFrequency.IsMatch(trimmed!))
            return null;

        // Invariant culture - see ParseFsdFrequency.
        return decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz)
            ? mhz.ToString("000.000", CultureInfo.InvariantCulture)
            : null;
    }

    // ATISControl.DateTimeUtc is UTC (Save sets it from DateTime.UtcNow), but a value restored from
    // a saved ATIS can come back with no Kind; it is labelled UTC, not shifted. An unset value
    // falls back to now.
    static string IsoUtc(DateTime? value)
    {
        var moment = value == null || value.Value == default
            ? DateTime.UtcNow
            : value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime() : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        return moment.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    // ATISPlugin's assembly version, reported so a malformed broadcast can be tied to the build
    // that sent it. Trailing zero components are dropped: "4.3.0.0" is sent as "4.3".
    static string PluginVersion(Type controlType)
    {
        var version = controlType.Assembly.GetName().Version;
        if (version == null)
            return "unknown";
        return version.Revision <= 0
            ? version.Build <= 0 ? version.ToString(2) : version.ToString(3)
            : version.ToString(4);
    }

    // The sections this letter changed. ATISLine.Changed is the flag ATISControl.GetInfo turns into
    // the leading "+" on a line of broadcast text ("+[RWY] 34"); Save clears it on every line and
    // sets it on those whose value changed.
    static List<string> ReadChanged(IEnumerable? lines)
    {
        var changed = new List<string>();
        if (lines == null)
            return changed;

        foreach (var line in lines)
        {
            var type = line.GetType();
            var name = type.GetProperty("Name")?.GetValue(line) as string;
            var visible = type.GetProperty("Visible")?.GetValue(line) as bool? ?? false;
            var marked = type.GetProperty("Changed")?.GetValue(line) as bool? ?? false;

            if (marked && visible && !string.IsNullOrEmpty(name) && name is not ("OFCW_NOTIFY" or "ZULU"))
                changed.Add(name!);
        }

        return changed;
    }

    // The free text a closed tower broadcasts, which ATISPlugin keeps in a pseudo-line named ZULU.
    static string? ReadZulu(IEnumerable? lines)
    {
        if (lines == null)
            return null;

        foreach (var line in lines)
        {
            var type = line.GetType();
            if (type.GetProperty("Name")?.GetValue(line) as string == "ZULU")
                return (type.GetProperty("Value")?.GetValue(line) as string)?.Trim();
        }

        return null;
    }

    // The same name/value shape as vatsys.ATIS.Content (see OzServerAtisUpdateDto): one entry per
    // visible ATISLine, keyed by its Name (WIND, VIS, QNH, ...). OFCW_NOTIFY and ZULU are
    // ATISPlugin's pseudo-lines (a notify flag and the Zulu free text), skipped as
    // ATISControl.GetInfo skips them.
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

    // Inverse of vatSys's Conversions.FSDFrequencyToString: "132.500" becomes 32500, the FSD
    // frequency Network.ATISFrequency?.GetFSDFrequency() reports for the equivalent vatsys.ATIS
    // station, so the field matches AtisSync's.
    //
    // Invariant culture, not the current one: FrequencyDisplay always uses "." as its decimal
    // point, which a locale with "." as its thousands separator (de-DE) reads as 132500 and most
    // other comma-decimal locales fail to parse.
    static int? ParseFsdFrequency(string? display) =>
        double.TryParse(display, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
            ? (int)Math.Round((mhz - 100) * 1000)
            : null;
}
