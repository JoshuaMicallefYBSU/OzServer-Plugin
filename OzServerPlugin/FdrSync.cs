using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Pushes FDR/position updates vatSys hands the plugin (Plugin.OnFDRUpdate, Plugin.OnRadarTrackUpdate
// - both fire per flight, for every flight vatSys is tracking, not just this controller's own
// traffic) to OzServer's /fdr/batch endpoint, attributing datalink authority from FDP2.FDR's own
// tracking state rather than this session's identity:
//   - Assumed by me (fdr.IsTrackedByMe) -> authority is this session's own cid/callsign.
//   - Assumed by someone else (fdr.IsTracked, not by me) -> authority is their callsign
//     (fdr.ControllerTracking.Callsign) - vatSys doesn't expose their CID, only OzServerFdrUpdateDto
//     itself only ever needs one for the "assumed by me" case anyway.
//   - Untracked (!fdr.IsTracked) -> no authority (both left null) - free, anyone can update it.
// See OzServerFdrUpdateDto's own comment for why this is safe to just trust and forward as-is, and
// FlightDataRecordController::upsert (backend) for what actually happens with it - the backend, not
// this class, is what enforces that only a currently-online, sector-holding authority controller
// can overwrite another flight's data.
//
// Updates are batched rather than sent one request per flight: OnFDRUpdate/OnRadarTrackUpdate just
// merge into _pending (keyed by callsign, newer non-null fields overwriting older ones - a
// position-only radar update doesn't blank out flight-plan fields a previous FDR push already
// filled in, and vice versa), and a timer flushes everything pending as one request periodically.
public class FdrSync
{
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    static readonly PropertyInfo[] DtoProperties = typeof(OzServerFdrUpdateDto).GetProperties();

    readonly OzServerApiClient _api = new();
    readonly object _lock = new();
    readonly Dictionary<string, OzServerFdrUpdateDto> _pending = new();
    readonly Timer _flushTimer;

    public FdrSync()
    {
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    public void OnFdrUpdate(FDP2.FDR fdr)
    {
        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        Merge(BuildFdrDto(fdr));
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack track)
    {
        var fdr = track.CoupledFDR;
        var callsign = fdr?.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return;

        var dto = new OzServerFdrUpdateDto { Callsign = callsign! };
        FillPosition(dto, track);
        FillAuthority(dto, fdr!);

        Merge(dto);
    }

    void Merge(OzServerFdrUpdateDto dto)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(dto.Callsign, out var existing))
                CopyNonNull(dto, existing);
            else
                _pending[dto.Callsign] = dto;
        }
    }

    async Task FlushAsync()
    {
        List<OzServerFdrUpdateDto> batch;

        lock (_lock)
        {
            if (_pending.Count == 0)
                return;

            batch = _pending.Values.ToList();
            _pending.Clear();
        }

        try
        {
            await _api.UpdateFdrBatchAsync(batch);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't push FDR batch to OzServer: {ex.Message}"), "OzServer");
        }
    }

    static OzServerFdrUpdateDto BuildFdrDto(FDP2.FDR fdr)
    {
        var dto = new OzServerFdrUpdateDto
        {
            Callsign = fdr.Callsign,
            State = fdr.State.ToString(),
            FlightRules = NullIfEmpty(fdr.FlightRules),
            AircraftType = NullIfEmpty(fdr.AircraftType),
            AircraftWake = NullIfEmpty(fdr.AircraftTypeAndWake?.WakeCategory),
            AircraftEquip = NullIfEmpty(fdr.AircraftEquip),
            AircraftSurvEquip = NullIfEmpty(fdr.AircraftSurvEquip),
            AircraftCount = fdr.AircraftCount,
            DepAirport = NullIfEmpty(fdr.DepAirport),
            DesAirport = NullIfEmpty(fdr.DesAirport),
            Route = NullIfEmpty(fdr.Route),
            SidStarString = NullIfEmpty(fdr.SIDSTARString),
            RunwayString = NullIfEmpty(fdr.RunwayString),
            DepartureRunway = NullIfEmpty(fdr.DepartureRunway?.Name),
            // 0 is vatSys's own "not set" for these three, same spirit as the AssignedSSRCode/-1
            // and ATD-ETD/DateTime.MaxValue sentinels below - never a real cleared/requested level
            // or true airspeed in this context.
            Rfl = fdr.RFL > 0 ? fdr.RFL : null,
            CflLower = fdr.CFLLower > 0 ? fdr.CFLLower : null,
            CflUpper = fdr.CFLUpper > 0 ? fdr.CFLUpper : null,
            AssignedSsrCode = fdr.AssignedSSRCode == -1 ? null : fdr.AssignedSSRCode,
            Atd = fdr.ATD == DateTime.MaxValue ? null : fdr.ATD,
            Etd = fdr.ETD == DateTime.MaxValue ? null : fdr.ETD,
            EetMinutes = fdr.EET == TimeSpan.Zero ? null : (int)fdr.EET.TotalMinutes,
            Tas = fdr.TAS > 0 ? fdr.TAS : null,
            TextOnly = fdr.TextOnly,
            ReceiveOnly = fdr.ReceiveOnly,
            LabelOpData = NullIfEmpty(fdr.LabelOpData),
            Remarks = NullIfEmpty(fdr.Remarks),
        };

        FillAuthority(dto, fdr);

        return dto;
    }

    static void FillAuthority(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        if (fdr.IsTrackedByMe)
        {
            if (int.TryParse(Network.ControllerId, out var myCid))
                dto.ControllingCid = myCid;
            dto.ControllingCallsign = Network.Callsign;
        }
        else if (fdr.IsTracked)
        {
            dto.ControllingCallsign = fdr.ControllerTracking.Callsign;
        }
    }

    // RDP.RadarTrack uses sentinel values rather than nulls for "not set" - -1/-1.0 for
    // CorrectedAltitude/GroundSpeed/Heading, -9999.0 for VerticalSpeed - left off the DTO (staying
    // null) rather than sent as literal -1/-9999 readings.
    static void FillPosition(OzServerFdrUpdateDto dto, RDP.RadarTrack track)
    {
        if (track.LatLong != null)
        {
            dto.Lat = track.LatLong.Latitude;
            dto.Lon = track.LatLong.Longitude;
        }

        if (track.CorrectedAltitude != -1)
            dto.Altitude = track.CorrectedAltitude;

        if (track.GroundSpeed >= 0)
            dto.GroundSpeed = (int)Math.Round(track.GroundSpeed);

        if (track.Heading >= 0)
            dto.Heading = (int)Math.Round(track.Heading) % 360;

        if (track.VerticalSpeed > -9999.0)
            dto.VerticalRate = (int)Math.Round(track.VerticalSpeed);

        dto.OnGround = track.OnGround;
    }

    // Copies every non-null property from source onto target - used to merge a newer, partial
    // update (e.g. a radar-only position ping) into whatever's already pending for that callsign
    // without blanking out fields the newer update simply doesn't know about. Reflection-based
    // rather than a hand-written field list so a new OzServerFdrUpdateDto property is merged
    // correctly without this having to be updated to match.
    static void CopyNonNull(OzServerFdrUpdateDto source, OzServerFdrUpdateDto target)
    {
        foreach (var prop in DtoProperties)
        {
            var value = prop.GetValue(source);
            if (value != null)
                prop.SetValue(target, value);
        }
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
