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

    // Every station still on the air is pushed again this often, for the reason AtisSync gives: the
    // backend drops an entry 60 minutes after its last push, and an ATIS that has not changed for an
    // hour is still an ATIS.
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    DateTime _nextRefresh = DateTime.UtcNow + RefreshInterval;

    public BadVectorsAtisSync()
    {
        _timer = new Timer(_ => Rescan(), null, RescanInterval, RescanInterval);
    }

    void Rescan()
    {
        try
        {
            if (DateTime.UtcNow >= _nextRefresh)
            {
                _nextRefresh = DateTime.UtcNow + RefreshInterval;

                // PushAsync declines a station with nothing on it, and when there is no session.
                foreach (var control in _subscribed.ToList())
                    _ = PushAsync(control, quiet: true);
            }

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
            // This runs every five seconds; whatever broke will break again on the next pass.
            ErrorThrottle.Report("atisplugin-hook", $"Couldn't hook into badvectors/ATISPlugin: {ex.Message}", ex);
        }
    }

    async Task PushAsync(object? control, bool quiet = false)
    {
        // NetworkServer.Current: see AtisSync.PushAsync.
        if (control == null || !Network.IsConnected || NetworkServer.Current == null)
            return;

        var type = control.GetType();
        var icao = type.GetProperty("ICAO")?.GetValue(control) as string;
        var letter = type.GetProperty("ID")?.GetValue(control) as char?;
        var lines = type.GetProperty("Lines")?.GetValue(control) as IEnumerable;
        var frequencyDisplay = type.GetProperty("FrequencyDisplay")?.GetValue(control) as string;
        // IsZulu is the only thing that distinguishes a closed tower's free-text broadcast from a
        // sectioned one (ATISControl.GetInfo branches on exactly this), and DateTimeUtc is the only
        // time it keeps - set to UtcNow in Save, so it is when this letter was issued.
        var isZulu = type.GetProperty("IsZulu")?.GetValue(control) as bool? ?? false;
        var issued = type.GetProperty("DateTimeUtc")?.GetValue(control) as DateTime?;
        // ATISControl.Callsign is private ($"{icao}_ATIS", set in Create), so NonPublic - and the
        // same thing is rebuilt below if a future release renames or hides it differently.
        var callsign = type.GetProperty("Callsign", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(control) as string;

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
        var zuluText = isZulu ? ReadZulu(lines) : null;
        // A zulu broadcast carries nothing but its own text - ReadContent skips the ZULU line the
        // same way ATISControl.GetInfo does - so "nothing on this station" is a different question
        // for each kind.
        if (isZulu ? string.IsNullOrWhiteSpace(zuluText) : content.Count == 0)
            return;

        // The handover record (POST /atis), unchanged: per server, sections only, for the next
        // controller on this station rather than for anyone reading publicly.
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
                    Errors.Add(new Exception($"Couldn't push ATISPlugin ATIS to OzServer: {ex.Message}", ex), "OzServer");
            }
        }

        // And the broadcast itself (PUT /vatsim/atis/{icao}), which is what the website map and
        // every public reader of /vatsim/atis draw from.
        //
        // Live only, deliberately: that record has no server of its own (an ATIS is a broadcast on
        // the network), so publishing from a SweatBox session would put training weather on the
        // public map for everyone. The handover push above stays per-server and still happens.
        if (NetworkServer.Current != NetworkServer.Live)
            return;

        // MODE and TIME_CHECK are editor settings, not broadcast information - the backend drops
        // both and refuses an ATIS with nothing else left in it. A station whose mode auto-selected
        // on Create but which nobody has filled in yet is exactly that, and is not worth publishing
        // (or reporting as a failure to the controller).
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
                Errors.Add(new Exception($"Couldn't publish ATISPlugin ATIS to OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    static readonly HashSet<string> EditorOnlyLines = new() { "MODE", "TIME_CHECK" };

    // The backend takes the frequency as the text it is read out as ("126.250") and refuses
    // anything that is not one. A station whose display string is empty or shaped oddly should
    // still publish its ATIS - without a frequency - rather than have the whole thing rejected.
    //
    // Reformatted, not just checked. ATISControl.FrequencyDisplay is not the "126.250" this was
    // written against: Create builds it with Normalize25KhzFrequency, which pads the number out to
    // hertz and puts the point back after the third digit - "126.250000". Matching that against a
    // three-decimal pattern failed for every station, so every ATIS was published with no
    // frequency at all (YSWS on 13 Nov: 127.000 in the handover record, null in the public one).
    // Read as a number and written back the way a frequency is displayed.
    static readonly Regex DisplayFrequency = new(@"^\d{3}(\.\d+)?$", RegexOptions.Compiled);

    static string? PublishableFrequency(string? display)
    {
        var trimmed = display?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !DisplayFrequency.IsMatch(trimmed!))
            return null;

        // Invariant culture for the reason ParseFsdFrequency gives: the point is always ".".
        return decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz)
            ? mhz.ToString("000.000", CultureInfo.InvariantCulture)
            : null;
    }

    // ATISControl.DateTimeUtc is UTC by construction (Save sets it from DateTime.UtcNow), but a
    // value restored from a saved ATIS can come back with no Kind at all - labelled, not shifted,
    // so a 09:14Z broadcast is never published as 09:14 local. Nothing set at all (a slot whose
    // ATIS predates this field, say) falls back to now, which is the one time we do know.
    static string IsoUtc(DateTime? value)
    {
        var moment = value == null || value.Value == default
            ? DateTime.UtcNow
            : value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime() : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        return moment.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    // ATISPlugin's own assembly version - 4.3 at the time of writing. Reported so a broadcast that
    // arrives malformed can be tied to the build that sent it; "4.3.0.0" is noise, so the trailing
    // zeroes come off.
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
    // the leading "+" on a line of broadcast text ("+[RWY] 34"); it is cleared for every line at
    // the top of Save and set again only on the ones whose value actually moved.
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

    // The free text a closed tower broadcasts, which ATISPlugin keeps in a pseudo-line named ZULU
    // rather than in any field of its own.
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
    //
    // Invariant culture, not the current one: FrequencyDisplay always uses "." as its decimal point.
    // On a Windows install whose locale uses "." as the thousands separator (de-DE, for one) the
    // culture-default overload read "132.500" as 132500 and pushed a frequency of 132400000 to
    // OzServer; in most other comma-decimal locales it failed to parse and sent none at all.
    static int? ParseFsdFrequency(string? display) =>
        double.TryParse(display, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
            ? (int)Math.Round((mhz - 100) * 1000)
            : null;
}
