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
// traffic) to OzServer's /fdr/batch endpoint - but only for a flight this controller currently holds
// and has activated: fdr.IsTrackedByMe && fdr.ESTed, checked fresh on every update (see ShouldPush).
// No grace period, no "used to own it" carve-out: the moment either goes false, nothing more is
// pushed for that flight until (if ever) this controller holds it again. IsTrackedByMe is now
// whatever vatSys itself says it is - nothing in this plugin moves tags between controllers any
// more - so the condition simply follows the controller's own working state.
//
// Because a push only ever happens while this controller holds the flight, the datalink authority it
// reports is always this session's own identity (see FillAuthority) - there is no "assumed by
// someone else" or "free" case left to report. See OzServerFdrUpdateDto's own comment for why that's
// safe to just trust and forward as-is, and FlightDataRecordController::upsert (backend) for what
// actually happens with it. A row nothing has pushed to in 10 minutes - the natural backend
// counterpart to a controller simply no longer pushing anything once they've let go of a flight - is
// dropped server-side; that's a backend-only follow-up (same precedent as the existing 90-minute
// ATIS TTL - see AtisSync/README), not implemented in this repo.
//
// Also reports current_sector (see FillCurrentSector) - the real geographic subsector the aircraft
// is physically inside of right now, resolved via SectorLocator against every sector vatSys knows
// about, not just ones OzServer has an active ownership record for. That's deliberately a different
// question from the datalink authority above: who owns the tag vs. which airspace the aircraft is
// actually in, regardless of whether anyone has claimed it there yet.
//
// Updates are batched rather than sent one request per flight: OnFDRUpdate/OnRadarTrackUpdate just
// merge into _pending (keyed by callsign, newer non-null fields overwriting older ones - a
// position-only radar update doesn't blank out flight-plan fields a previous FDR push already
// filled in, and vice versa), and a timer flushes everything pending as one request periodically.
public class FdrSync
{
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    // Everything except the two datalink-authority fields, which CopyNonNull handles separately -
    // see its own comment for why they can't go through the same "skip nulls" merge as the rest.
    static readonly PropertyInfo[] MergeProperties = typeof(OzServerFdrUpdateDto).GetProperties()
        .Where(p => p.Name != nameof(OzServerFdrUpdateDto.ControllingCid)
                    && p.Name != nameof(OzServerFdrUpdateDto.ControllingCallsign))
        .ToArray();

    readonly OzServerApiClient _api = new();
    readonly object _lock = new();
    readonly Dictionary<string, OzServerFdrUpdateDto> _pending = new();
    readonly Timer _flushTimer;
    // 0 = idle, 1 = a flush is in flight. The timer keeps ticking every FlushInterval regardless of
    // whether the previous flush has come back, so without this a backend slower than the interval
    // accumulates overlapping batches - and because each one takes its own snapshot of _pending and
    // clears it, they carry *different* data and can land out of order, letting an older batch
    // overwrite a newer one's positions server-side. A tick that finds a flush already running just
    // skips; nothing is lost, since whatever accumulates meanwhile goes out on the next one.
    int _flushing;

    public FdrSync()
    {
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    public void OnFdrUpdate(FDP2.FDR fdr)
    {
        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        if (!ShouldPush(fdr))
            return;

        Merge(BuildFdrDto(fdr));
    }

    // Sends without waiting for the flush timer.
    //
    // For a position update, riding the next 5s batch is fine - a slightly stale lat/lon costs
    // nothing. A change of *authority* is different: until it reaches the backend there is no record
    // that this controller holds the tag, so an ungraceful disconnect in that window loses it
    // entirely. On reconnect the backend has nothing to hand back and the controller is offered
    // their own aircraft as a fresh pickup, which is exactly what happened to the last two tags of a
    // five-tag session.
    //
    // Goes through the same _pending merge and the same FlushAsync as everything else, so it cannot
    // race the timer, send a partial DTO, or overlap an in-flight batch. If a flush happens to be
    // running, FlushAsync declines and the update simply stays queued for the next tick - no worse
    // than the old behaviour, and the common case sends at once.
    public void PushNow(FDP2.FDR fdr)
    {
        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        if (!ShouldPush(fdr))
            return;

        Merge(BuildFdrDto(fdr));
        _ = FlushAsync();
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack track)
    {
        var fdr = track.CoupledFDR;
        if (string.IsNullOrEmpty(fdr?.Callsign))
            return;

        if (!ShouldPush(fdr!))
            return;

        var dto = new OzServerFdrUpdateDto
        {
            Callsign = fdr!.Callsign,
            State = fdr.State.ToString()
        };
        FillPosition(dto, track);
        FillAuthority(dto);
        FillCurrentSector(dto, fdr);

        Merge(dto);
    }

    // Gates every push on this session's own relationship to the flight, checked fresh every time -
    // see the class comment.
    static bool ShouldPush(FDP2.FDR fdr) => fdr.IsTrackedByMe && fdr.ESTed;


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
        // Leaves whatever's pending queued rather than dropping it - not connected right now just
        // means nothing gets pushed yet, not that this update never happened.
        if (!Network.IsConnected)
            return;

        // See _flushing - one batch in flight at a time, no overlapping pushes.
        if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
            return;

        try
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
                Errors.Add(new Exception($"Couldn't push FDR batch to OzServer: {ex.Message}", ex), "OzServer");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
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

        FillAuthority(dto);
        FillPredictedPosition(dto, fdr);
        FillCurrentSector(dto, fdr);

        return dto;
    }

    // The geographic subsector fdr is physically inside of right now - a different question from
    // who owns the tag (FillAuthority, above): resolved against the full SectorsVolumes.Sectors
    // list rather than only sectors somebody has claimed, so this reports
    // real geography regardless of whether anyone has actually claimed that sector on OzServer.
    // Left null (and, via CopyNonNull's ordinary skip-null merge, simply not overwritten) when the
    // aircraft isn't inside any known sector volume at its current position/level.
    static void FillCurrentSector(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        dto.CurrentSector = SectorLocator.Resolve(fdr, SectorsVolumes.Sectors)?.Name;
    }

    // A flight never coupled to a radar track - still climbing out of coverage, procedural, a
    // non-radar environment - would otherwise never get any position pushed at all: only
    // OnRadarTrackUpdate's FillPosition sets Lat/Lon, and it only ever fires once there's a track to
    // report. Falls back to fdr.GetLocation()'s own PredictedPosition.Location branch - the same
    // position vatSys itself draws the tag at - so there's always something for OzServer to show
    // even before (or without) a live track. Once a track exists, this backs off entirely and leaves
    // Lat/Lon to FillPosition, which reports the coupled track's own live position - the more
    // authoritative of the two.
    static void FillPredictedPosition(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        if (fdr.CoupledTrack != null)
            return;

        var location = fdr.GetLocation();
        if (location == null)
            return;

        dto.Lat = location.Latitude;
        dto.Lon = location.Longitude;
    }

    // Called only once ShouldPush(fdr) has already passed for this push - i.e. fdr.IsTrackedByMe is
    // true - so the datalink authority a push reports is always this session's own identity.
    static void FillAuthority(OzServerFdrUpdateDto dto)
    {
        // Left null when there is no identity yet rather than defaulted, because null is meaningful
        // on this field: it is what the backend reads as "no datalink authority".
        var me = NetworkIdentity.Current;
        if (me != null)
            dto.ControllingCid = me.Value.Cid;

        dto.ControllingCallsign = Network.Callsign;
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
    //
    // The two datalink-authority fields are copied as a unit, outside the shared skip-nulls loop
    // below, rather than folded into MergeProperties: both producers (BuildFdrDto and the radar
    // path) always run FillAuthority, which always sets them together to this session's own
    // identity - see its own comment - so source's answer is always current and always meant to
    // overwrite whatever target already had, never something to skip past because source "doesn't
    // know" this round. ControllingCid is the one field of the two that can still land as null (see
    // FillAuthority's int.TryParse), which is exactly why they're marked
    // NullValueHandling.Include on OzServerFdrUpdateDto rather than trusted to the default
    // skip-nulls serialization every other field gets.
    static void CopyNonNull(OzServerFdrUpdateDto source, OzServerFdrUpdateDto target)
    {
        target.ControllingCid = source.ControllingCid;
        target.ControllingCallsign = source.ControllingCallsign;

        foreach (var prop in MergeProperties)
        {
            var value = prop.GetValue(source);
            if (value != null)
                prop.SetValue(target, value);
        }
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
