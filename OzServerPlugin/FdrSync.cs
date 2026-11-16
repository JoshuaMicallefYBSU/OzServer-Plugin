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
// and has activated: fdr.IsTrackedByMe && fdr.ESTed, checked fresh on every update (see ShouldPush) -
// or, for an activated/completed FDR nobody tracks, when this controller holds its data authority
// (ShouldPush's second rule; those are reported with no tag owner, see FillAuthority).
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
// dropped server-side; that's a backend-only follow-up (same precedent as the existing 60-minute
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
                    && p.Name != nameof(OzServerFdrUpdateDto.ControllingCallsign)
                    // The cleared level is merged as a unit too - see CopyNonNull.
                    && p.Name != nameof(OzServerFdrUpdateDto.CflLower)
                    && p.Name != nameof(OzServerFdrUpdateDto.CflUpper)
                    && p.Name != nameof(OzServerFdrUpdateDto.LabelOpData)
                    && p.Name != nameof(OzServerFdrUpdateDto.GlobalOpData)
                    && p.Name != nameof(OzServerFdrUpdateDto.CPrompt)
                    && p.Name != nameof(OzServerFdrUpdateDto.ClearanceKnown))
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

    readonly OzServerOwnershipTracker _tracker;

    public FdrSync(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);

        // Whatever was still queued belongs to the session that just ended. FlushAsync holds it
        // while disconnected, so it used to go out on the first tick after reconnecting - naming
        // this controller as the authority for flights someone else may have picked up while they
        // were away, and, after a switch between live and a SweatBox, into the wrong server's
        // records entirely (the server is attached when the batch is sent, not when it is queued).
        Network.Disconnected += (_, _) =>
        {
            lock (_lock)
            {
                _pending.Clear();
                // A new session starts with nothing sent: every route goes out again.
                _sentRouteKeys.Clear();
                // Likewise every tag's label state, the first time it is seen tracked again.
                _labelStateKeys.Clear();
            }
            // And every tag is taken over from the record again when next tracked.
            LabelAdopted.Clear();
            Landed.Clear();
        };
    }

    public void OnFdrUpdate(FDP2.FDR fdr)
    {
        if (OzServerMode.Disabled)
            return;

        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        if (!ShouldPush(fdr))
            return;

        Merge(BuildFdrDto(fdr, forceRoute: false));
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
        if (OzServerMode.Disabled)
            return;

        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        if (!ShouldPush(fdr))
            return;

        Merge(BuildFdrDto(fdr, forceRoute: false));
        ActionLog.Log("FDR", $"Queued immediate FDR authority push for {fdr.Callsign}", new
        {
            action = "fdr_push_now",
            fdr_callsign = fdr.Callsign,
            state = fdr.State.ToString(),
            is_tracked_by_me = fdr.IsTrackedByMe,
            current_sector = SectorLocator.Resolve(fdr, SectorsVolumes.Sectors)?.Name,
            controlling_sector = fdr.ControllingSector?.Name,
            controlling_callsign = Network.Callsign
        });
        _ = FlushAsync();
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack track)
    {
        if (OzServerMode.Disabled)
            return;

        CheckLanded(track);

        // Coupled flight plans only. A floating FDR (no coupled track) is reported where its flight
        // plan track is - see FloatingSweep - not where some uncoupled return with the same callsign
        // happens to be; CoupleAssist couples the two when they really are the same aircraft.
        var fdr = track.CoupledFDR;
        if (string.IsNullOrEmpty(fdr?.Callsign))
            return;

        if (!ShouldPush(fdr!))
            return;

        var dto = new OzServerFdrUpdateDto
        {
            Callsign = fdr!.Callsign,
            State = fdr.State.ToString(),
            Coupled = fdr.CoupledTrack == track
        };
        FillPosition(dto, track);
        FillAuthority(dto, fdr);
        FillResponsibleSector(dto, fdr);
        FillCurrentSector(dto, fdr);

        Merge(dto);
    }

    // An untracked push carries the sector that made this session responsible for it (see
    // ResponsibleSectorForUntracked) - the backend accepts an untracked report from the holder of
    // the sector named here.
    void FillResponsibleSector(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        if (!fdr.IsTrackedByMe && ResponsibleSectorForUntracked(fdr, out var sector) && sector != null)
            dto.AuthoritySector = sector;
    }

    // Gates every push on this session's own relationship to the flight, checked fresh every time.
    // Two ways to qualify:
    //
    //  - this controller tracks it and it is activated (the original rule - see the class comment);
    //  - nobody tracks it, and this controller holds its data authority: the sector vatSys names as
    //    the FDR's ControllingSector is one of this session's controlled sectors. That is vatSys's
    //    own rule for who may edit an untracked FDR (FDR.HavePermission), so exactly one online
    //    controller answers for each such FDR - an activated tag still on the ground at YBBN is kept
    //    up to date by whoever holds the sector its data authority sits in, and a completed one by
    //    whoever holds it at the destination. Only activated (ESTed) or completed (STATE_FINISHED)
    //    FDRs: a pre-active plan is not traffic yet.
    //
    // Connected as well: vatSys keeps delivering updates for a while after a disconnect, and what is
    // queued then would be sent first on the next session - see the Disconnected handler above.
    bool ShouldPush(FDP2.FDR fdr)
    {
        if (!Network.IsConnected)
            return false;

        ForgetLandingOfEarlierFlight(fdr);

        if (fdr.IsTrackedByMe)
            return fdr.ESTed;

        // Tracked by another OzServer controller: theirs to report. Tracked by someone who is not on
        // OzServer at all (WAAF_CTR, say) is nobody's - so it is treated exactly like untracked below.
        if (fdr.IsTracked && !TrackedOutsideOzServer(fdr))
            return false;

        // vatSys finishes an untracked FDR the moment its pilot drops off this client's picture - it
        // cannot tell "left my visibility range" from "disconnected" (Network.ProcessRemovedPilot ->
        // FDP2.CancelFDR). JST946 was finished at FL360 when CS1 cut its range to 10 nm, and CS_APP
        // then reported it finished to everyone. A finish is only passed on when the aircraft was
        // last seen down at its destination; anything else is this client losing sight of it, and
        // the record is left as it was for a client that can still see it.
        if (fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED && !IsCredibleFinish(fdr))
        {
            if (!string.IsNullOrEmpty(fdr.Callsign) && FinishedOutOfView.TryAdd(fdr.Callsign, DateTime.UtcNow))
            {
                ActionLog.Log("FDR", $"{fdr.Callsign} finished by vatSys out of view - not reported, kept active on OzServer", new
                {
                    action = "fdr_finish_out_of_view",
                    fdr_callsign = fdr.Callsign
                });
            }
            return false;
        }

        // An untracked FDR is only ever reported from a radar return: coupled to its track. A floating
        // one - no coupled track - is the tracking controller's to report or nobody's; its flight plan
        // position is a prediction, and the server's record of an untracked aircraft has to be where
        // it really is. (A finish passed above came from the radar return too: seen landing.)
        if (fdr.State != FDP2.FDR.FDRStates.STATE_FINISHED && fdr.CoupledTrack == null)
            return false;

        return ResponsibleSectorForUntracked(fdr, out _);
    }

    // Landing seen on radar, reported to OzServer as completed.
    //
    // vatSys finishes an FDR on landing only at the instant it uncouples the track, and only if the
    // aircraft is below 50 kt, within 2 nm of an assigned arrival runway's threshold and on the ground
    // (RDP.TryAutoDeCouple -> TryAutoFinishFDR). Miss any of that - uncoupled still rolling out at
    // 55 kt, no arrival runway set, a long runway - and it never looks again: QFA597 landed at YSSY
    // and sat UNCONTROLLED at the aerodrome, on the map as if still flying, until it aged out.
    //
    // So the plugin watches the radar return itself: an activated, departed FDR whose aircraft is on
    // the ground (within 200 ft of aerodrome elevation, or flagged on ground with no Mode C) below
    // 50 kt within 4 nm of its destination is reported FINISHED, and from then on reported that way
    // (see Merge) for as long as it is still that landing. vatSys's own FDR is left alone - the tower
    // may still be working the taxi, and finishing it here would take the tag off their scope.
    // Reported by whoever sees it and holds a sector (or tracks it); a finish is the same from anyone,
    // so two reporting is harmless.
    //
    // Kept by callsign, with the flight that landed (its departure time and destination), because a
    // callsign outlives a landing: an aircraft flying circuits takes off again on the same flight
    // plan, and a turnaround files a new one under the same callsign. Held for the whole session, the
    // first landing had every later push for that callsign - tracked ones included - reported as
    // completed. So the landing is forgotten when the aircraft is at speed again (CheckLanded) or the
    // callsign's flight plan is no longer the one that landed (ForgetLandingOfEarlierFlight). Either
    // one being wrong costs nothing: still down at its destination, it is seen landed again within
    // the next look.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Atd, string Destination)> Landed =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _landingChecked = new(StringComparer.OrdinalIgnoreCase);

    // A different flight plan under a callsign with a landing on record - the next leg, not yet
    // departed or departed at another time, or bound somewhere else. Run before every push decision
    // (ShouldPush), so the new flight is never reported with the old one's "completed".
    static void ForgetLandingOfEarlierFlight(FDP2.FDR fdr)
    {
        if (string.IsNullOrEmpty(fdr.Callsign) || !Landed.TryGetValue(fdr.Callsign, out var landed))
            return;

        if (fdr.ATD != landed.Atd || !string.Equals(fdr.DesAirport, landed.Destination, StringComparison.OrdinalIgnoreCase))
            ForgetLanding(fdr.Callsign, "a different flight plan is using the callsign");
    }

    static void ForgetLanding(string callsign, string reason)
    {
        if (!Landed.TryRemove(callsign, out var landed))
            return;

        ActionLog.Log("FDR", $"{callsign} no longer reported completed - {reason}", new
        {
            action = "fdr_landed_cleared",
            fdr_callsign = callsign,
            landed_at = landed.Destination,
            reason
        });
    }

    void CheckLanded(RDP.RadarTrack track)
    {
        var callsign = track.ActualAircraft?.Callsign;
        if (string.IsNullOrEmpty(callsign) || track.LatLong == null)
            return;

        if (track.GroundSpeed >= 50)
        {
            // At speed again after a landing: a take-off roll. ContainsKey first - this runs for
            // every moving track on every radar update, and almost none of them has landed.
            if (Landed.ContainsKey(callsign!))
                ForgetLanding(callsign!, "at speed again");
            return;
        }

        if (Landed.ContainsKey(callsign!))
            return;

        // Slow tracks are mostly aircraft sitting at gates; one look per callsign every 15 s.
        lock (_landingChecked)
        {
            if (_landingChecked.TryGetValue(callsign!, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(15))
                return;
            _landingChecked[callsign!] = DateTime.UtcNow;
        }

        FDP2.FDR? fdr = track.CoupledFDR;
        if (fdr == null)
        {
            try
            {
                fdr = FDP2.GetFDRs.FirstOrDefault(f => string.Equals(f.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        if (fdr == null || !fdr.ESTed || fdr.ATD == DateTime.MaxValue || string.IsNullOrEmpty(fdr.DesAirport))
            return;
        // Someone else is tracking it: their client reports it.
        if (fdr.IsTracked && !fdr.IsTrackedByMe && !TrackedOutsideOzServer(fdr))
            return;
        if (!fdr.IsTrackedByMe && _tracker.Owned.Count == 0)
            return;
        if (Airspace2.GetAirport(fdr.DesAirport) is not { LatLong: { } destination } airport)
            return;
        if (Conversions.CalculateDistance(track.LatLong, destination) > 4)
            return;

        var elevation = airport.Elevation > 0 ? airport.Elevation : 0;
        var onGround = track.CorrectedAltitude != -1 ? track.CorrectedAltitude < elevation + 200 : track.OnGround;
        if (!onGround)
            return;

        Landed[callsign!] = (fdr.ATD, fdr.DesAirport);

        var dto = new OzServerFdrUpdateDto
        {
            Callsign = fdr.Callsign,
            State = nameof(FDP2.FDR.FDRStates.STATE_FINISHED),
            Coupled = fdr.CoupledTrack == track
        };
        FillPosition(dto, track);
        FillAuthority(dto, fdr);
        // An untracked report has to name a sector this session holds - the nearest of them.
        if (!fdr.IsTrackedByMe)
        {
            var mine = _tracker.Owned.Where(s => !s.IsDummy).ToList();
            if (mine.Count > 0)
                dto.AuthoritySector = Choose(fdr, mine.Select(s => (Sector: s, Mine: true)).ToList(), track.LatLong).Sector.Name;
        }
        Merge(dto);

        ActionLog.Log("FDR", $"{fdr.Callsign} landed at {fdr.DesAirport} - reported completed", new
        {
            action = "fdr_landed",
            fdr_callsign = fdr.Callsign,
            destination = fdr.DesAirport,
            altitude = track.CorrectedAltitude,
            ground_speed = track.GroundSpeed,
            vatsys_state = fdr.State.ToString()
        });
    }

    // Tracked by a controller who is not on OzServer - a neighbouring FIR's (WAAF_CTR), or anyone
    // without the plugin. They will never report it, so for reporting it is as good as untracked: the
    // OzServer controller responsible for it by the untracked rules keeps the record current. "Not on
    // OzServer" is: they hold no OzServer sector, and no OzServer client is reporting itself as the
    // flight's controller - the second so that an OzServer user holding nothing yet is not mistaken
    // for an outsider.
    bool TrackedOutsideOzServer(FDP2.FDR fdr)
    {
        var tracker = fdr.ControllerTracking?.Callsign;
        if (!fdr.IsTracked || fdr.IsTrackedByMe || string.IsNullOrEmpty(tracker))
            return false;
        if (_tracker.ControlledByOthers.Values.Any(o => string.Equals(o.Callsign, tracker, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrEmpty(fdr.Callsign) && ServerFdrSnapshot.TryGet(fdr.Callsign, out var entry) && entry.ControllingCid != null)
            return false;
        return true;
    }

    // FDRs vatSys finished because they went out of view here (see ShouldPush). FdrResumeActivation
    // activates one again if it reappears while OzServer still has it active.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> FinishedOutOfView =
        new(StringComparer.OrdinalIgnoreCase);

    // Landed, or as good as: last seen within 15 nm of its destination and below 3,000 ft above it
    // (or on the ground). Uses the radar return when there still is one, otherwise vatSys's last
    // predicted position - which, for an aircraft lost in the cruise, is nowhere near the ground.
    static bool IsCredibleFinish(FDP2.FDR fdr)
    {
        if (string.IsNullOrEmpty(fdr.DesAirport) || Airspace2.GetAirport(fdr.DesAirport) is not { LatLong: { } destination } airport)
            return false;

        var position = SectorLocator.ApproximatePosition(fdr);
        if (position == null || Conversions.CalculateDistance(position, destination) > 15)
            return false;

        var altitude = SectorLocator.LiveAltitude(fdr);
        var elevation = airport.Elevation > 0 ? airport.Elevation : 0;
        return altitude != null && altitude < elevation + 3000;
    }

    // Exactly one connected controller keeps each untracked activated (or completed) FDR current on
    // the server, at all times - so its position there follows the aircraft on VATSIM even when the
    // controller who was working it has gone. Only a controller who can actually see the aircraft
    // counts: a client receives an aircraft's position only while it is inside that controller's
    // visibility range, and one picked without it - CS_APP for JST946 at FL360, 94 nm out - would be
    // left reporting a tag it never receives. Among those who can see it:
    //
    //  1. whoever holds the sector the aircraft is in right now;
    //  2. otherwise whoever holds the held sector nearest to it, by straight-line distance to that
    //     sector's boundary (ties broken by sector name, so every client picks the same one).
    //
    // "Can see it": for this session, it has a live radar track for the aircraft. For another
    // controller, the aircraft is within their visibility range of their vis centre - the Position
    // and Range vatSys has for them off the network, which is what VATSIM itself sends positions by.
    // That uses only their main vis centre; extra vis points they may have only widen what they see,
    // so when this is wrong it leaves two clients reporting (harmless - the server merges them) rather
    // than none. A controller vatSys no longer lists as online does not count at all.
    //
    // The sector that decided it is returned for the push to carry (authority_sector) - always one
    // this session holds when the answer is "us" - which is what the backend checks an untracked
    // report against.
    bool ResponsibleSectorForUntracked(FDP2.FDR fdr, out string? sectorName)
    {
        sectorName = null;
        if (fdr.IsTracked && !TrackedOutsideOzServer(fdr))
            return false;
        if (!fdr.ESTed && fdr.State != FDP2.FDR.FDRStates.STATE_FINISHED)
            return false;

        // Not visible here: never ours, whatever anyone else can see.
        // Where the aircraft is, by this client's own radar. No return here, never ours.
        var seen = SectorLocator.LivePosition(fdr);
        if (seen == null)
            return false;
        var position = seen;

        var myCid = NetworkIdentity.Current?.Cid;
        var onlineAtc = (Network.GetOnlineATCs ?? new List<NetworkATC>())
            .Where(a => !string.IsNullOrEmpty(a.Callsign))
            .GroupBy(a => a.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var others = _tracker.ControlledByOthers;

        bool CanSee(string callsign) =>
            onlineAtc.TryGetValue(callsign, out var atc)
            && atc.Position != null && atc.Range > 0
            && Conversions.CalculateDistance(atc.Position, position) <= atc.Range;

        var mine = _tracker.Owned.Where(s => !s.IsDummy).ToList();
        var othersHeld = SectorsVolumes.Sectors
            .Where(s => !s.IsDummy && others.TryGetValue(s.Name, out var owner) && owner.Cid != myCid && online(owner.Callsign))
            .ToList();
        bool online(string callsign) => onlineAtc.ContainsKey(callsign);

        // Only controllers who can see the aircraft count - this session (it has the return) and any
        // other online holder whose visibility range reaches it.
        var candidates = mine.Select(s => (Sector: s, Mine: true))
            .Concat(othersHeld.Where(s => CanSee(others[s.Name].Callsign)).Select(s => (Sector: s, Mine: false)))
            .ToList();
        if (candidates.Count == 0)
            return false;

        var chosen = Choose(fdr, candidates, position);
        if (chosen.Mine)
        {
            sectorName = chosen.Sector.Name;
            StoppedStandingIn(fdr.Callsign);
            return true;
        }

        // Somebody else's - as far as this client can tell. See TakeOverIfStale for when that is not
        // the end of it.
        return TakeOverIfStale(fdr, mine, position, chosen.Sector.Name, out sectorName);
    }

    // 1. the candidate sector the aircraft is in, level and all; 2. otherwise the nearest one.
    static (SectorsVolumes.Sector Sector, bool Mine) Choose(FDP2.FDR fdr, List<(SectorsVolumes.Sector Sector, bool Mine)> candidates, Coordinate position)
    {
        var here = SectorLocator.Resolve(fdr, candidates.Select(h => h.Sector));
        if (here != null)
            return candidates.First(h => h.Sector.Equals(here));

        var nearest = candidates
            .Select(h => (h.Sector, h.Mine, Distance: SectorLocator.DistanceNm(h.Sector, position)))
            .OrderBy(h => h.Distance)
            .ThenBy(h => h.Sector.Name, StringComparer.Ordinal)
            .First();
        return (nearest.Sector, nearest.Mine);
    }

    // The watchdog. Choosing who reports an untracked aircraft is done from what this client can
    // estimate about everyone else, and some of it can only be known on the other client: whether
    // their vatSys has the aircraft coupled (they will not report an uncoupled one), whether their
    // real coverage reaches it, whether their plugin can reach OzServer at all, and their own view of
    // who holds what in the seconds after a change. Any of those can leave every client that sees the
    // aircraft deferring to one that is not reporting it.
    //
    // So what the server actually received decides it. A client that can report the aircraft itself
    // (it sees it, has it coupled, holds a sector) but was not chosen takes over when OzServer's
    // record has not been updated for StaleAfter - or has no record at all - and keeps going while
    // the record's last update came from one of its own sectors (authority_sector). The moment the
    // chosen controller does report, its sector is on the record and this one stops. At worst that is
    // two clients reporting for one scan interval, which the server merges.
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    // Older than this and the snapshot says nothing about now - no takeover on its strength.
    static readonly TimeSpan SnapshotUsableFor = TimeSpan.FromSeconds(60);
    readonly HashSet<string> _standingIn = new(StringComparer.OrdinalIgnoreCase);

    bool TakeOverIfStale(FDP2.FDR fdr, List<SectorsVolumes.Sector> mine, Coordinate position, string chosenSector, out string? sectorName)
    {
        sectorName = null;
        if (mine.Count == 0 || string.IsNullOrEmpty(fdr.Callsign) || ServerFdrSnapshot.Age > SnapshotUsableFor)
        {
            StoppedStandingIn(fdr.Callsign);
            return false;
        }

        string reason;
        if (!ServerFdrSnapshot.TryGet(fdr.Callsign, out var entry))
            reason = "no record on OzServer";
        else if (!string.IsNullOrEmpty(entry.AuthoritySector) && mine.Any(s => string.Equals(s.Name, entry.AuthoritySector, StringComparison.OrdinalIgnoreCase)))
            reason = "still reporting it";
        else if (entry.LastSeenAt == null || DateTimeOffset.UtcNow - entry.LastSeenAt.Value > StaleAfter)
            reason = $"not updated for {(entry.LastSeenAt == null ? "ever" : $"{(DateTimeOffset.UtcNow - entry.LastSeenAt.Value).TotalSeconds:0}s")}";
        else
        {
            StoppedStandingIn(fdr.Callsign);
            return false;
        }

        // Reported as from the nearest of this session's own sectors, which is what the backend checks.
        sectorName = Choose(fdr, mine.Select(s => (Sector: s, Mine: true)).ToList(), position).Sector.Name;

        bool started;
        lock (_standingIn)
            started = _standingIn.Add(fdr.Callsign);
        if (started)
        {
            ActionLog.Log("FDR", $"Reporting {fdr.Callsign} in place of {chosenSector}'s controller - {reason}", new
            {
                action = "fdr_standing_in",
                fdr_callsign = fdr.Callsign,
                chosen_sector = chosenSector,
                own_sector = sectorName,
                reason
            });
        }
        return true;
    }

    void StoppedStandingIn(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return;
        bool stopped;
        lock (_standingIn)
            stopped = _standingIn.Remove(callsign!);
        if (stopped)
            ActionLog.Log("FDR", $"Stopped reporting {callsign} - the chosen controller has it", new { action = "fdr_standing_in_stopped", fdr_callsign = callsign });
    }

    // Floating FDRs this controller tracks - activated, no coupled radar track - are reported where
    // vatSys draws their flight plan track, which moves along the route by itself and raises no FDR
    // event as it does. Every FloatingEvery-th flush they are queued again with that position, so
    // the map's box moves with vatSys's. Only tracked ones: ShouldPush never reports a floating FDR
    // nobody tracks.
    const int FloatingEvery = 3; // x FlushInterval = every 15 s

    void FloatingSweep()
    {
        if (OzServerMode.Disabled)
            return;

        List<FDP2.FDR> fdrs;
        try
        {
            fdrs = FDP2.GetFDRs.ToList();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var fdr in fdrs)
        {
            try
            {
                if (string.IsNullOrEmpty(fdr.Callsign) || fdr.CoupledTrack != null || !fdr.ESTed || !ShouldPush(fdr))
                    continue;

                var dto = new OzServerFdrUpdateDto { Callsign = fdr.Callsign, State = fdr.State.ToString(), Coupled = false };
                FillAuthority(dto, fdr);
                FillResponsibleSector(dto, fdr);
                FillPredictedPosition(dto, fdr);
                FillCurrentSector(dto, fdr);
                Merge(dto);
            }
            catch (Exception ex)
            {
                ErrorThrottle.Report("fdr-floating", $"Couldn't read FDR {fdr.Callsign} for OzServer: {ex.Message}", ex);
            }
        }
    }

    // Every SweepEvery-th flush, every FDR this session answers for is queued again in full. An
    // untracked FDR parked at the gate, or one already completed, raises no FDR or radar events of
    // its own, so without this it would age out of the backend (FDR_RETAIN_MINUTES) while still
    // very much there - and an FDR whose data authority only just became ours (a sector claimed or
    // handed over) would not appear until something about it happened to change.
    const int SweepEvery = 12; // x FlushInterval = once a minute
    int _flushCount;

    void SweepAuthorityFdrs()
    {
        if (OzServerMode.Disabled)
            return;

        List<FDP2.FDR> fdrs;
        try
        {
            fdrs = FDP2.GetFDRs.ToList();
        }
        catch (Exception)
        {
            return; // modified mid-copy; the next sweep gets it
        }

        foreach (var fdr in fdrs)
        {
            try
            {
                if (!string.IsNullOrEmpty(fdr.Callsign) && ShouldPush(fdr))
                    Merge(BuildFdrDto(fdr, forceRoute: true));
            }
            catch (Exception ex)
            {
                ErrorThrottle.Report("fdr-sweep", $"Couldn't read FDR {fdr.Callsign} for OzServer: {ex.Message}", ex);
            }
        }
    }


    void Merge(OzServerFdrUpdateDto dto)
    {
        if (OzServerMode.Disabled)
            return;

        // A landing stands for the server's record, whatever this client's vatSys still has the FDR
        // as - until the callsign flies again (see Landed).
        if (Landed.ContainsKey(dto.Callsign))
            dto.State = nameof(FDP2.FDR.FDRStates.STATE_FINISHED);

        lock (_lock)
        {
            if (_pending.TryGetValue(dto.Callsign, out var existing))
                CopyNonNull(dto, existing);
            else
                _pending[dto.Callsign] = dto;
        }
    }

    // The label state last queued per tracked flight: scratchpad, global ops, and the "C"
    // (see OzServerFdrUpdateDto.CPrompt). The C lives on the Track and vatSys raises nothing when a
    // middle click flips it, so no FDR update ever carries the change - it has to be looked for.
    // Checked once per flush, for this controller's own tags only (the only ones it reports these
    // for), and queued only when something actually moved.
    readonly Dictionary<string, string> _labelStateKeys = new(StringComparer.OrdinalIgnoreCase);

    // Tags whose label this client must not speak for yet: just accepted from another controller,
    // with the previous controller's label still being read back (SectorTagHandoff.AcceptTransfer).
    // Until released, a push for one leaves the whole label out, as a non-tracking client's would.
    // Expires on its own so a read that never completes cannot silence a tag for good.
    static readonly TimeSpan LabelHoldLimit = TimeSpan.FromSeconds(15);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LabelStateHeld =
        new(StringComparer.OrdinalIgnoreCase);

    public static void HoldLabelState(string callsign) => LabelStateHeld[callsign] = DateTime.UtcNow + LabelHoldLimit;

    public static void ReleaseLabelState(string callsign) => LabelStateHeld.TryRemove(callsign, out _);

    static bool IsLabelStateHeld(string? callsign) =>
        !string.IsNullOrEmpty(callsign) && LabelStateHeld.TryGetValue(callsign!, out var until) && DateTime.UtcNow < until;

    // Whether this session has taken over a tag's label from OzServer's record yet. Every way a tag
    // becomes this controller's goes through here, not only the two that used to read the record
    // back (a reconnect - TagResumeRecovery - and an OzServer transfer - SectorTagHandoff). A tag
    // simply assumed in vatSys, after the controller who had it logged off, arrives with a bare
    // label: vatSys only shares the CFL, scratchpad and global ops live, from whoever is tracking it
    // at the time, and there was nobody. Its first push then stated that bare label as fact and
    // erased the record - EFA69 on 16 Nov: CFL 350, "MEOW" and "SSSS" set by ML-ASP_CTR, gone the
    // moment ML-BLA_CTR picked the tag up after ASP had logged off.
    //
    // So the label is left out of every push (as a non-tracking client's is) until the record has
    // been read and anything it holds put into whatever the label is missing; only then does this
    // client speak for it. Anything the controller has already typed is kept.
    //
    // false while the read is in flight, true once done. Forgotten when the tag stops being ours, so
    // picking it up again reads the record again.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> LabelAdopted =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly OzServerApiClient AdoptApi = new();

    static bool IsLabelAdopted(FDP2.FDR fdr)
    {
        if (LabelAdopted.TryGetValue(fdr.Callsign, out var done))
            return done;
        if (LabelAdopted.TryAdd(fdr.Callsign, false))
            _ = AdoptLabelAsync(fdr);
        return false;
    }

    static async Task AdoptLabelAsync(FDP2.FDR fdr)
    {
        var callsign = fdr.Callsign;
        OzServerFdrUpdateDto? stored = null;
        try
        {
            stored = await AdoptApi.GetFlightAsync(callsign);
        }
        catch (Exception ex)
        {
            // Taken over anyway: a label this client never speaks for is worse than one that starts
            // from what the controller has in front of them.
            ActionLog.Log("FDR", $"Couldn't read {callsign}'s label from OzServer on pickup: {ex.Message}");
        }

        RunOnUiThread(() =>
        {
            try
            {
                if (stored == null || !fdr.IsTrackedByMe)
                    return;

                var applied = LabelStateRestore.Apply(fdr, stored, replaceCfl: false);
                if (applied != null)
                {
                    ActionLog.Log("FDR", $"Took {callsign}'s label from OzServer on pickup: {applied}", new
                    {
                        action = "pickup_label_state_applied",
                        fdr_callsign = callsign,
                        cfl_lower = stored.CflLower,
                        cfl_upper = stored.CflUpper,
                        label_op_data = stored.LabelOpData,
                        global_op_data = stored.GlobalOpData,
                        c_prompt = stored.CPrompt
                    });
                }
            }
            catch (Exception ex)
            {
                ErrorThrottle.Report("label-adopt", $"Couldn't apply {callsign}'s label from OzServer: {ex.Message}", ex);
            }
            finally
            {
                // The next flush pushes the whole label (QueueLabelStateChanges has no key for it yet).
                LabelAdopted.TryUpdate(callsign, true, false);
            }
        });
    }

    static void RunOnUiThread(Action action)
    {
        if (System.Windows.Forms.Application.OpenForms["MainForm"] is System.Windows.Forms.Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }

    void QueueLabelStateChanges()
    {
        // Copied first and guarded throughout, like the two sweeps above: this runs on the flush
        // timer's thread while vatSys adds and removes FDRs on its own, and an enumeration that
        // collided with one threw straight out of FlushAsync - the whole batch (every position and
        // authority update queued behind it) then waited another five seconds, each time it happened.
        List<FDP2.FDR> fdrs;
        try
        {
            fdrs = FDP2.GetFDRs.ToList();
        }
        catch (Exception)
        {
            return; // modified mid-copy; the next flush looks again
        }

        foreach (var fdr in fdrs)
        {
            try
            {
                if (string.IsNullOrEmpty(fdr.Callsign))
                    continue;

                // No longer ours: forget the takeover, and the label last sent, so a later pickup
                // reads the record again and then pushes the label in full.
                if (!fdr.IsTrackedByMe)
                {
                    if (LabelAdopted.TryRemove(fdr.Callsign, out _))
                        lock (_lock)
                            _labelStateKeys.Remove(fdr.Callsign);
                    continue;
                }

                if (!fdr.ESTed || !IsLabelAdopted(fdr) || IsLabelStateHeld(fdr.Callsign))
                    continue;

                var track = MMI.FindTrack(fdr);
                var key = $"{fdr.LabelOpData}\u001f{fdr.GlobalOpData}\u001f{track?.CPrompt}";

                lock (_lock)
                {
                    if (_labelStateKeys.TryGetValue(fdr.Callsign, out var last) && last == key)
                        continue;
                }

                Merge(BuildFdrDto(fdr, forceRoute: false));

                // Recorded only once the update is queued, so a read that failed part-way is looked
                // at again on the next flush rather than remembered as already sent.
                lock (_lock)
                    _labelStateKeys[fdr.Callsign] = key;
            }
            catch (Exception ex)
            {
                ErrorThrottle.Report("fdr-label-state", $"Couldn't read {fdr.Callsign}'s label state for OzServer: {ex.Message}", ex);
            }
        }
    }

    async Task FlushAsync()
    {
        if (OzServerMode.Disabled)
            return;

        // Leaves whatever's pending queued rather than dropping it - not connected right now just
        // means nothing gets pushed yet, not that this update never happened. An unsupported server
        // (see NetworkServer.Current) is the same: every push would fail before it was sent, and
        // report that as an error every five seconds.
        if (!Network.IsConnected || NetworkServer.Current == null)
            return;

        // See _flushing - one batch in flight at a time, no overlapping pushes.
        if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
            return;

        try
        {
            if (++_flushCount % SweepEvery == 0)
                SweepAuthorityFdrs();
            else if (_flushCount % FloatingEvery == 0)
                FloatingSweep();

            QueueLabelStateChanges();

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
                // Once per outage, not once per five-second flush - see ErrorThrottle.
                ErrorThrottle.Report("fdr-flush", $"Couldn't push FDR batch to OzServer: {ex.Message}", ex);
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
            Coupled = fdr.CoupledTrack != null,
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
            // Read straight off the FDR, so "no cleared level" here is a fact to report, not a gap
            // in what this push knows - see OzServerFdrUpdateDto.CflLower.
            //
            // But only from the controller tracking it. The cleared level is that controller's own
            // entry; a client reporting an untracked aircraft on OzServer's behalf (the untracked
            // rules, a stand-in for a dropped controller) holds whatever its own copy happens to
            // say, which after a disconnect is usually nothing. Stated as "none", that erased the
            // CFL a disconnected controller had set - JST516 came back to ML-BLA_CTR with no level
            // because ML-SNO_CTR had reported it in the meantime. Left unstated, the backend keeps
            // what the tracking controller last said.
            // And only once this session has taken the label over from OzServer's record - see
            // LabelAdopted.
            ClearanceKnown = fdr.IsTrackedByMe && IsLabelAdopted(fdr) && !IsLabelStateHeld(fdr.Callsign),
            AssignedSsrCode = fdr.AssignedSSRCode == -1 ? null : fdr.AssignedSSRCode,
            Atd = fdr.ATD == DateTime.MaxValue ? null : fdr.ATD,
            Etd = fdr.ETD == DateTime.MaxValue ? null : fdr.ETD,
            EetMinutes = fdr.EET == TimeSpan.Zero ? null : (int)fdr.EET.TotalMinutes,
            Tas = fdr.TAS > 0 ? fdr.TAS : null,
            TextOnly = fdr.TextOnly,
            ReceiveOnly = fdr.ReceiveOnly,
            // "" rather than null - a cleared scratchpad is a fact the tracking controller states (see
            // OzServerFdrUpdateDto.LabelOpData). Not sent at all from anyone else.
            LabelOpData = fdr.LabelOpData ?? "",
            GlobalOpData = fdr.GlobalOpData ?? "",
            Remarks = NullIfEmpty(fdr.Remarks),
        };

        // The "C", which vatSys keeps on the track rather than the FDR - see
        // OzServerFdrUpdateDto.CPrompt. Left unset with no track to read it from.
        if (MMI.FindTrack(fdr) is { } track)
            dto.CPrompt = track.CPrompt;

        FillAuthority(dto, fdr);
        FillPredictedPosition(dto, fdr);
        FillCurrentSector(dto, fdr);
        FillAerodromes(dto, fdr);

        return dto;
    }

    // What route this session last queued per callsign, so an unchanged route - the usual case on
    // every FDR update - is not resent. The sweep sends it regardless (forceRoute), which also
    // covers a batch that failed to go out after the key was recorded here.
    readonly Dictionary<string, string> _sentRouteKeys = new(StringComparer.OrdinalIgnoreCase);

    OzServerFdrUpdateDto BuildFdrDto(FDP2.FDR fdr, bool forceRoute)
    {
        var dto = BuildFdrDto(fdr);
        FillResponsibleSector(dto, fdr);
        FillRoute(dto, fdr, forceRoute);
        return dto;
    }

    void FillRoute(OzServerFdrUpdateDto dto, FDP2.FDR fdr, bool force)
    {
        List<FDP2.FDR.ExtractedRoute.Segment> segments;
        int overflown;
        try
        {
            segments = fdr.ParsedRoute.ToList();
            overflown = fdr.ParsedRoute.OverflownIndex;
        }
        catch (Exception)
        {
            return; // being re-parsed on vatSys's thread; the next update gets it
        }

        var points = new List<OzServerRoutePointDto>();
        var nextIndex = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            // Trial routes are a controller's what-if, not the aircraft's route.
            if (segment?.Intersection?.LatLong == null || segment.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.TRIAL)
                continue;

            if (i <= overflown)
                nextIndex = points.Count + 1;

            var point = new OzServerRoutePointDto
            {
                Name = segment.Intersection.Name ?? "",
                Lat = Math.Round(segment.Intersection.LatLong.Latitude, 4),
                Lon = Math.Round(segment.Intersection.LatLong.Longitude, 4)
            };

            // A sector crossing, not a waypoint: vatSys inserts a ZPOINT segment wherever the
            // predicted trajectory enters a volume (FDP2.CalculateAffectedSectors), named "Z" + the
            // volume's name - ZMUN, ZMLTWR, ZYTCUZONE - with the volume itself in Tag. Told apart by
            // the segment type, never the name: a volume can be called anything. The sector is
            // vatSys's own answer for that volume (SectorsVolumes.FindSector), the same lookup its
            // handoff window uses to pick the next sector.
            if (segment.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.ZPOINT)
            {
                point.Crossing = true;
                if (segment.Tag is SectorsVolumes.Volume volume)
                {
                    point.Volume = volume.Name;
                    if (SectorsVolumes.FindSector(volume) is { } sector)
                    {
                        point.Sector = sector.Name;
                        point.SectorCallsign = sector.Callsign;
                    }
                }
            }

            points.Add(point);
        }

        if (points.Count < 2)
            return;

        dto.RouteNextIndex = Math.Min(nextIndex, points.Count - 1);

        var key = string.Join(";", points.Select(p => $"{p.Name},{p.Lat},{p.Lon},{p.Sector}"));
        lock (_lock)
        {
            if (!force && _sentRouteKeys.TryGetValue(fdr.Callsign, out var sent) && sent == key)
                return;
            _sentRouteKeys[fdr.Callsign] = key;
        }

        dto.RoutePoints = points;
    }

    static void FillAerodromes(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        if (!string.IsNullOrEmpty(fdr.DepAirport) && Airspace2.GetAirport(fdr.DepAirport) is { LatLong: { } dep })
        {
            dto.DepLat = dep.Latitude;
            dto.DepLon = dep.Longitude;
        }

        if (!string.IsNullOrEmpty(fdr.DesAirport) && Airspace2.GetAirport(fdr.DesAirport) is { LatLong: { } des })
        {
            dto.DesLat = des.Latitude;
            dto.DesLon = des.Longitude;
        }
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

    // Called only once ShouldPush(fdr) has passed. A tracked push names this session; an untracked
    // one (pushed because this session holds the data authority) names nobody, since nobody tracks
    // it - null is what the backend and the map read as "no tag owner". Either way the data
    // authority sector goes with it, which is what lets the backend accept the untracked report.
    static void FillAuthority(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        dto.AuthoritySector = fdr.ControllingSector?.Name;
        // Who really tracks it, whoever that is - for a controller not on OzServer this is the only
        // place their name reaches the record (controlling_* is OzServer's own authority, null here).
        dto.TrackedBy = fdr.ControllerTracking?.Callsign ?? "";

        if (!fdr.IsTrackedByMe)
        {
            dto.ControllingCid = null;
            dto.ControllingCallsign = null;
            return;
        }

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

        // The cleared level, likewise as a unit and likewise outside the skip-nulls loop - but only
        // from a source that actually read it. A full FDR push's null means "no cleared level" and
        // has to replace whatever is pending; a radar-only update's null means it never looked, and
        // must not undo a clearance a fuller push has already queued.
        if (source.ClearanceKnown)
        {
            target.ClearanceKnown = true;
            target.CflLower = source.CflLower;
            target.CflUpper = source.CflUpper;
            target.LabelOpData = source.LabelOpData;
            target.GlobalOpData = source.GlobalOpData;
            // Null here means no track was read, not "off" - see BuildFdrDto.
            if (source.CPrompt != null)
                target.CPrompt = source.CPrompt;
        }

        foreach (var prop in MergeProperties)
        {
            var value = prop.GetValue(source);
            if (value != null)
                prop.SetValue(target, value);
        }
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
