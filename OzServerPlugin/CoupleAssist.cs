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
        if (track.CoupledFDR != null)
            return;

        var fdr = FDP2.GetFDRs.FirstOrDefault(f => string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        if (fdr == null
            || fdr.CoupledTrack != null
            || !fdr.ESTed                                   // not activated: not traffic yet
            || fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED
            || fdr.InhibitAutoCouple)                       // a controller or vatSys turned it off deliberately
            return;

        try
        {
            RDP.Couple(fdr, track);
            ActionLog.Log("Couple", $"Coupled {callsign} to its radar track - vatSys's auto-couple had not", new
            {
                action = "couple_assist",
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
