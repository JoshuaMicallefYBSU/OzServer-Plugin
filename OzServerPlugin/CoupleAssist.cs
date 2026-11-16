using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Couples an activated flight plan to its radar track when vatSys's own auto-couple will not.
//
// vatSys only auto-couples a radar return that agrees with where the flight plan predicts the
// aircraft to be (RDP's route check). An FDR activated by hand for an aircraft already airborne gets
// an ETD of "now + 10 minutes", so its flight plan track sits at the departure aerodrome while the
// aircraft is far along the route, and the two never couple.
//
// The callsign identifies the track. When an activated, uncoupled FDR has a moving, uncoupled radar
// track carrying its callsign, this couples the two with vatSys's own RDP.Couple - which also runs
// vatSys's automatic position report from the radar track, moving the flight plan to where the
// aircraft is. Coupling is local to this client: nothing is sent to the network, and the tag's
// jurisdiction is not touched.
public class CoupleAssist
{
    // Below this the track is taxiing or parked, and an FDR still at the gate couples by itself
    // once it is rolling - vatSys applies the same floor.
    const double MinGroundSpeed = 50;
    static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    // How long a moving track has to have stayed uncoupled before this steps in.
    static readonly TimeSpan LeaveToVatSys = TimeSpan.FromSeconds(20);
    // A gap this long in a callsign's uncoupled sightings starts its wait over.
    static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(2);

    readonly object _gate = new();
    readonly Dictionary<string, DateTime> _lastTried = new(StringComparer.OrdinalIgnoreCase);
    // When each callsign was first seen moving with no coupled flight plan.
    readonly Dictionary<string, DateTime> _firstSeen = new(StringComparer.OrdinalIgnoreCase);

    public void OnRadarTrackUpdate(RDP.RadarTrack track)
    {
        if (OzServerMode.Disabled)
            return;

        if (!Network.IsConnected || track.CoupledFDR != null || track.GroundSpeed < MinGroundSpeed)
            return;

        var callsign = track.ActualAircraft?.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return;

        lock (_gate)
        {
            // vatSys gets the first chance: an ordinary departure couples by itself a few seconds
            // into the take-off roll.
            var now = DateTime.UtcNow;
            if (!_firstSeen.TryGetValue(callsign!, out var first) || now - first > ForgetAfter)
            {
                _firstSeen[callsign!] = now;
                return;
            }
            if (now - first < LeaveToVatSys)
                return;

            // Radar updates arrive every few seconds per aircraft: one look per callsign per
            // RetryEvery.
            if (_lastTried.TryGetValue(callsign!, out var last) && now - last < RetryEvery)
                return;
            _lastTried[callsign!] = now;
            _firstSeen[callsign!] = now - LeaveToVatSys; // stays eligible while it keeps reporting
        }

        RunOnUiThread(() => TryCouple(track, callsign!));
    }

    static void TryCouple(RDP.RadarTrack track, string callsign)
    {
        if (OzServerMode.Disabled)
            return;

        if (track.CoupledFDR != null)
            return;

        var matches = FDP2.GetFDRs.Where(f => string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase)).ToList();
        var fdr = matches.FirstOrDefault();

        // Every path below that leaves the track uncoupled says why, once per flight (see
        // ExplainUncoupled) - JST372 departed uncoupled on 16 Nov with nothing in the log to say
        // whether vatSys or this class had turned it down, or on what grounds.
        if (fdr == null)
        {
            ExplainUncoupled(track, callsign, null, matches.Count, "no flight plan with its callsign");
            return;
        }
        if (fdr.CoupledTrack != null)
        {
            ExplainUncoupled(track, callsign, fdr, matches.Count, "its flight plan is coupled to a different track");
            return;
        }
        if (!fdr.ESTed)
        {
            ExplainUncoupled(track, callsign, fdr, matches.Count, "its flight plan is not activated");
            return;
        }
        if (fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED)
        {
            ExplainUncoupled(track, callsign, fdr, matches.Count, "its flight plan is finished");
            return;
        }

        // Auto-couple was turned off for this flight on purpose, so it is never overridden.
        if (fdr.InhibitAutoCouple)
        {
            ExplainUncoupled(track, callsign, fdr, matches.Count, "auto-couple is turned off for it");
            return;
        }

        if (WouldBeUncoupled(fdr, track))
        {
            ExplainUncoupled(track, callsign, fdr, matches.Count,
                "Mode C-only return squawking a code other than the flight plan's - vatSys would uncouple it again");
            return;
        }

        Couple(fdr, track, "vatSys's auto-couple had not");
    }

    // One line per flight per ExplainEvery, giving everything vatSys's auto-couple
    // (RDP.TryAutoCouple) and this class decide on: the flight plan's state, code and auto-couple
    // flag, the return's squawk and radar types, how many flight plans carry the callsign, and
    // whether vatSys's route check (RDP.CheckForRAM) would accept the return.
    static readonly TimeSpan ExplainEvery = TimeSpan.FromMinutes(5);
    static readonly Dictionary<string, DateTime> _explained = new(StringComparer.OrdinalIgnoreCase);

    static void ExplainUncoupled(RDP.RadarTrack track, string callsign, FDP2.FDR? fdr, int fdrCount, string reason)
    {
        lock (_explained)
        {
            if (_explained.TryGetValue(callsign, out var last) && DateTime.UtcNow - last < ExplainEvery)
                return;
            _explained[callsign] = DateTime.UtcNow;
        }

        var squawk = Octal(track.ActualAircraft?.TransponderCode);
        var assigned = Octal(fdr?.AssignedSSRCode);
        var onRoute = fdr != null ? OnRoute(track, fdr) : null;

        var detail = fdr == null
            ? $"squawking {squawk}, radar {track.RadarTypes}"
            : $"{fdr.State}, auto-couple {(fdr.InhibitAutoCouple ? "off" : "on")}, " +
              $"assigned {assigned}, squawking {squawk}, radar {track.RadarTypes}, " +
              $"on route {(onRoute == null ? "unknown" : onRoute.Value ? "yes" : "no")}" +
              (fdrCount > 1 ? $", {fdrCount} flight plans with this callsign" : "");

        ActionLog.Log("Couple", $"Not coupling {callsign} - {reason} ({detail})", new
        {
            action = "couple_assist_declined",
            reason,
            fdr_callsign = callsign,
            fdr_count = fdrCount,
            state = fdr?.State.ToString(),
            inhibit_auto_couple = fdr?.InhibitAutoCouple,
            assigned_ssr = assigned,
            squawk,
            radar_types = track.RadarTypes.ToString(),
            on_route = onRoute,
            atd = fdr == null || fdr.ATD == DateTime.MaxValue ? null : (DateTime?)fdr.ATD,
            ground_speed = track.GroundSpeed,
            altitude = track.CorrectedAltitude,
            lat = track.LatLong?.Latitude,
            lon = track.LatLong?.Longitude
        });
    }

    // SSR codes are held as the integer value of their octal digits.
    static string? Octal(int? code) =>
        code is int c && c >= 0 ? Convert.ToString(c, 8).PadLeft(4, '0') : null;

    // vatSys's own route check, asked the way TryAutoCouple asks it: true when the return agrees
    // with the flight plan's route. Private to vatSys, so reached by reflection; null if this
    // vatSys build does not have it in that shape.
    static readonly System.Reflection.MethodInfo? CheckForRam = typeof(RDP).GetMethod(
        "CheckForRAM",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
        null,
        new[] { typeof(RDP.RadarTrack), typeof(bool), typeof(List<FDP2.FDR.ExtractedRoute.Segment>), typeof(int) },
        null);

    static bool? OnRoute(RDP.RadarTrack track, FDP2.FDR fdr)
    {
        if (CheckForRam == null)
            return null;
        try
        {
            var route = fdr.ParsedRoute.ToList();
            var stale = route.FindLastIndex(s => s.ETO < DateTime.UtcNow - TimeSpan.FromMinutes(30));
            var start = Math.Max(Math.Min(stale, fdr.ParsedRoute.OverflownIndex), 0);
            // CheckForRAM answers whether the return is off the route.
            return !(bool)CheckForRam.Invoke(null, new object[] { track, true, route, start });
        }
        catch
        {
            return null;
        }
    }

    // The activation path. Activating a flight plan is the controller identifying the aircraft, so
    // one radar cycle after activation (vatSys's own chance first) an FDR still uncoupled is coupled
    // to the moving return carrying its callsign - see the class comment.
    static readonly TimeSpan AfterActivation = TimeSpan.FromSeconds(6);
    readonly HashSet<FDP2.FDR> _activationHandled = new();

    public void OnFdrUpdate(FDP2.FDR fdr)
    {
        if (OzServerMode.Disabled || !Network.IsConnected || string.IsNullOrEmpty(fdr.Callsign))
            return;

        lock (_activationHandled)
        {
            // Back to pre-active (or finished): a later activation is a new one.
            if (!fdr.ESTed)
            {
                _activationHandled.Remove(fdr);
                return;
            }
            if (fdr.CoupledTrack != null || fdr.InhibitAutoCouple || !_activationHandled.Add(fdr))
                return;
        }

        System.Threading.Timer? once = null;
        once = new System.Threading.Timer(_ =>
        {
            once?.Dispose();
            RunOnUiThread(() => CoupleAfterActivation(fdr));
        }, null, AfterActivation, System.Threading.Timeout.InfiniteTimeSpan);
    }

    static void CoupleAfterActivation(FDP2.FDR fdr)
    {
        if (OzServerMode.Disabled || !fdr.ESTed || fdr.CoupledTrack != null || fdr.InhibitAutoCouple
            || fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED)
            return;

        RDP.RadarTrack? match = null;
        try
        {
            foreach (var track in RDP.RadarTracks)
            {
                if (track?.CoupledFDR == null
                    && string.Equals(track?.ActualAircraft?.Callsign, fdr.Callsign, StringComparison.OrdinalIgnoreCase)
                    && track!.GroundSpeed >= MinGroundSpeed)
                {
                    match = track;
                    break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            return; // radar list changed mid-scan; the radar-update path still has it
        }

        if (match != null)
            Couple(fdr, match, "on activation");
    }

    // vatSys uncouples a Mode C-only return squawking anything but its assigned code on the next
    // radar update (RDP.TryAutoDeCouple, which spares 7600 and 7700). Coupling one would be undone
    // at once and tried again every RetryEvery.
    static bool WouldBeUncoupled(FDP2.FDR fdr, RDP.RadarTrack track)
    {
        var types = track.RadarTypes;
        if (!types.HasFlag(RDP.RadarTypes.SSR_ModeC) || types.HasFlag(RDP.RadarTypes.SSR_ModeS) || types.HasFlag(RDP.RadarTypes.ADSB))
            return false;

        var code = track.ActualAircraft?.TransponderCode;
        return code != null && code != fdr.AssignedSSRCode
               && code != Convert.ToInt32("7600", 8) && code != Convert.ToInt32("7700", 8);
    }

    static void Couple(FDP2.FDR fdr, RDP.RadarTrack track, string why)
    {
        var callsign = fdr.Callsign;
        if (WouldBeUncoupled(fdr, track))
        {
            ExplainUncoupled(track, callsign, fdr, 1,
                "Mode C-only return squawking a code other than the flight plan's - vatSys would uncouple it again");
            return;
        }

        try
        {
            RDP.Couple(fdr, track);
            ActionLog.Log("Couple", $"Coupled {callsign} to its radar track - {why}", new
            {
                action = "couple_assist",
                reason = why,
                fdr_callsign = callsign,
                state = fdr.State.ToString(),
                atd = fdr.ATD == DateTime.MaxValue ? null : (DateTime?)fdr.ATD,
                lat = track.LatLong?.Latitude,
                lon = track.LatLong?.Longitude,
                ground_speed = track.GroundSpeed
            });
        }
        catch (Exception ex)
        {
            ErrorThrottle.Report("couple-assist", $"Couldn't couple {callsign} to its radar track: {ex.Message}", ex);
        }
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
