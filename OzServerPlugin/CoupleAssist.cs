using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Couples an activated flight plan to its radar track when vatSys's own auto-couple will not.
//
// vatSys only auto-couples a radar return that agrees with where the flight plan predicts the
// aircraft to be (RDP's route check). An FDR activated by hand for an aircraft that has been airborne
// for hours gets an ETD of "now + 10 minutes", and departing it then makes vatSys believe it has just
// taken off: its flight plan track sits at the departure aerodrome while the real aircraft is far
// along the route. GIA716 was activated and departed out of Perth while the aircraft itself was
// descending into Bali - nearly 1,900 nm on - and it never coupled, because the radar return could
// not be where vatSys was looking for it.
//
// The callsign says which track it is. So when an activated, uncoupled FDR has a moving radar track
// carrying exactly its callsign that is not coupled to anything, this couples the two with vatSys's
// own RDP.Couple - which also runs vatSys's automatic position report from the radar track, moving
// the flight plan to where the aircraft actually is. Coupling is local to this client; nothing is
// sent to the network, and the tag's jurisdiction is not touched.
public class CoupleAssist
{
    // Below this the track is taxiing or parked, and an FDR still at the gate couples on its own once
    // it is rolling - vatSys applies the same floor.
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
            // into the take-off roll, and this is only for the ones it never will.
            var now = DateTime.UtcNow;
            if (!_firstSeen.TryGetValue(callsign!, out var first) || now - first > ForgetAfter)
            {
                _firstSeen[callsign!] = now;
                return;
            }
            if (now - first < LeaveToVatSys)
                return;

            // Radar updates arrive every few seconds per aircraft; one look per callsign per RetryEvery.
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

        // A controller or vatSys turned auto-couple off for this flight on purpose - never overridden,
        // but said once, so a flight that stays uncoupled for this reason is not a mystery in the log.
        if (fdr.InhibitAutoCouple)
        {
            if (_inhibitLogged.Add(callsign))
                ActionLog.Log("Couple", $"Not coupling {callsign} - auto-couple is turned off for it", new { action = "couple_assist_inhibited", fdr_callsign = callsign });
            return;
        }

        Couple(fdr, track, "vatSys's auto-couple had not");
    }

    static readonly HashSet<string> _inhibitLogged = new(StringComparer.OrdinalIgnoreCase);

    // The activation path. A flight plan activated by hand for an aircraft already in the air gets an
    // ETD of "now + 10 minutes", which puts vatSys's flight plan track back at the departure end of
    // the route - so vatSys's own auto-couple, which only takes a return close to where the plan
    // expects it, never couples it. Activating it is the controller saying "this is that aircraft":
    // one radar cycle after activation (vatSys's own chance first), an FDR still uncoupled is coupled
    // to the moving return carrying its callsign, and vatSys's position report moves the plan there.
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

    static void Couple(FDP2.FDR fdr, RDP.RadarTrack track, string why)
    {
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
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
