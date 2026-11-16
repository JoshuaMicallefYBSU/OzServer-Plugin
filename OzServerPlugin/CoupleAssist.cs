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

        var fdr = FDP2.GetFDRs.FirstOrDefault(f => string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        if (fdr == null
            || fdr.CoupledTrack != null
            || !fdr.ESTed                                   // not activated: not traffic yet
            || fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED)
            return;

        // Auto-couple was turned off for this flight on purpose, so it is never overridden. Logged
        // once, so the log explains why the flight stays uncoupled.
        if (fdr.InhibitAutoCouple)
        {
            if (_inhibitLogged.Add(callsign))
                ActionLog.Log("Couple", $"Not coupling {callsign} - auto-couple is turned off for it", new { action = "couple_assist_inhibited", fdr_callsign = callsign });
            return;
        }

        Couple(fdr, track, "vatSys's auto-couple had not");
    }

    static readonly HashSet<string> _inhibitLogged = new(StringComparer.OrdinalIgnoreCase);

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
        if (WouldBeUncoupled(fdr, track))
            return;

        var callsign = fdr.Callsign;
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
