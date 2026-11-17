using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Pushes the FDR and radar updates vatSys hands the plugin - for every flight, not only this
// controller's - to OzServer's /fdr/batch endpoint. A flight is pushed only while ShouldPush
// holds: this controller tracks it and it is activated, or no OzServer controller tracks it and
// this session is the one responsible for reporting it. There is no grace period.
//
// A tracked push names this session as the tag's owner; an untracked one names nobody (see
// FillAuthority). current_sector (FillCurrentSector) answers a separate question: which sector
// the aircraft is physically inside.
//
// Updates merge into _pending by callsign, newer non-null fields overwriting older ones, and a
// timer flushes everything pending as one request. The backend drops a record that has had no
// update for FDR_RETAIN_MINUTES.
public class FdrSync
{
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    // The properties CopyNonNull merges by skipping nulls. The authority fields and the label state
    // are excluded: CopyNonNull copies each of those as a unit.
    static readonly PropertyInfo[] MergeProperties = typeof(OzServerFdrUpdateDto).GetProperties()
        .Where(p => p.Name != nameof(OzServerFdrUpdateDto.ControllingCid)
                    && p.Name != nameof(OzServerFdrUpdateDto.ControllingCallsign)
                    && p.Name != nameof(OzServerFdrUpdateDto.CflLower)
                    && p.Name != nameof(OzServerFdrUpdateDto.CflUpper)
                    && p.Name != nameof(OzServerFdrUpdateDto.LabelOpData)
                    && p.Name != nameof(OzServerFdrUpdateDto.GlobalOpData)
                    && p.Name != nameof(OzServerFdrUpdateDto.CPrompt)
                    && p.Name != nameof(OzServerFdrUpdateDto.OtdLeft)
                    && p.Name != nameof(OzServerFdrUpdateDto.OtdRight)
                    && p.Name != nameof(OzServerFdrUpdateDto.ClearanceKnown))
        .ToArray();

    readonly OzServerApiClient _api = new();
    readonly object _lock = new();
    readonly Dictionary<string, OzServerFdrUpdateDto> _pending = new();
    readonly Timer _flushTimer;
    // 0 = idle, 1 = a flush is in flight. The timer ticks whether or not the previous flush has
    // returned, and overlapping batches can land out of order, an older one overwriting a newer
    // one's positions. A tick that finds a flush running skips.
    int _flushing;

    readonly OzServerOwnershipTracker _tracker;

    public FdrSync(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);

        // Whatever is still queued belongs to the session that ended. FlushAsync holds it while
        // disconnected, so unless cleared it would go out after reconnecting - naming this
        // controller as the authority for flights someone else may since have picked up, or,
        // after a switch between live and a SweatBox, into the wrong server's records (the server
        // is attached when the batch is sent).
        Network.Disconnected += (_, _) =>
        {
            lock (_lock)
            {
                _pending.Clear();
                // Sent under this session's identity or not at all; the record then ages out.
                _pendingCancels.Clear();
                // A new session resends every route and every tag's label state.
                _sentRouteKeys.Clear();
                _labelStateKeys.Clear();
            }
            // And adopts every tag's label from the record again when next tracked.
            LabelAdopted.Clear();
            LabelStateRestore.ForgetAllPending();
            lock (_trackedHere)
                _trackedHere.Clear();
            lock (_outsideLabel)
                _outsideLabel.Clear();
            lock (_lastRadarPush)
                _lastRadarPush.Clear();
            Landed.Clear();
        };
    }

    public void OnFdrUpdate(FDP2.FDR fdr)
    {
        if (OzServerMode.Disabled)
            return;

        if (string.IsNullOrEmpty(fdr.Callsign))
            return;

        // Ahead of ReportDrop: a cancel also stops this controller tracking the tag, and reported
        // as a drop first, the record would no longer name them as its owner by the time the
        // delete went out.
        if (ReportOwnerCancel(fdr))
            return;

        if (ReportDrop(fdr))
            return;

        if (!ShouldPush(fdr))
            return;

        Merge(BuildFdrDto(fdr, forceRoute: false));
    }

    static readonly TimeSpan RadarEvery = TimeSpan.FromSeconds(2);
    readonly Dictionary<string, DateTime> _lastRadarPush = new(StringComparer.OrdinalIgnoreCase);

    // Callsigns whose record is to be deleted on the next flush, after the batch - see FlushAsync.
    // Guarded by _lock with _pending.
    readonly HashSet<string> _pendingCancels = new(StringComparer.OrdinalIgnoreCase);
    // FDRs this session has cancelled on the server. Held by reference: vatSys keeps a finished FDR
    // for ten minutes and builds a new one if the pilot files again, and that one is reported as
    // usual. Nothing more is sent for a cancelled one unless it is activated again.
    readonly HashSet<FDP2.FDR> _cancelled = new();
    static readonly TimeSpan CancelRecent = TimeSpan.FromSeconds(30);

    // The controller who owns a flight cancelled its FDR - Finish in the aircraft menu, or Cancel
    // in the flight plan window, both FDP2.CancelFDR. Its record is deleted from OzServer rather
    // than left as completed for FDR_RETAIN_MINUTES, so nobody's client picks it back up.
    //
    // Told apart from a pilot disconnecting, which must keep the record for those 15 minutes in
    // case they reconnect: vatSys cancels for a disconnect only once the pilot has gone from its
    // picture (Network.ProcessRemovedPilot - at once for an untracked flight, after ten minutes for
    // a tracked one). A controller cancels with the pilot still online. Also not a landing: vatSys
    // finishes a coupled flight by itself on touchdown (RDP.TryAutoFinishFDR), and that stays a
    // completed record (see Landed).
    //
    // "Owns" is this session tracking the tag up to the cancel (CancelFDR stops the tracking), or
    // OzServer naming this controller. The backend checks it again.
    bool ReportOwnerCancel(FDP2.FDR fdr)
    {
        lock (_cancelled)
        {
            if (_cancelled.Contains(fdr))
            {
                // Activated again after the cancel: an ordinary flight from here on.
                if (fdr.ESTed && fdr.State != FDP2.FDR.FDRStates.STATE_FINISHED)
                    _cancelled.Remove(fdr);
                else
                    return true;
            }
        }

        if (fdr.State != FDP2.FDR.FDRStates.STATE_FINISHED && fdr.State != FDP2.FDR.FDRStates.STATE_INACTIVE)
            return false;
        if (fdr.CancellationTime == DateTime.MaxValue || DateTime.UtcNow - fdr.CancellationTime > CancelRecent)
            return false;
        if (!Network.IsConnected || !IsPilotOnline(fdr.Callsign))
            return false;
        if (Landed.ContainsKey(fdr.Callsign) || IsCredibleFinish(fdr))
            return false;

        bool trackedHere;
        lock (_trackedHere)
            trackedHere = _trackedHere.Contains(fdr.Callsign);
        var namedOnServer = ServerFdrSnapshot.TryGet(fdr.Callsign, out var record)
                            && record.ControllingCid != null
                            && record.ControllingCid == NetworkIdentity.Current?.Cid;
        if (!trackedHere && !namedOnServer)
            return false;

        lock (_cancelled)
            _cancelled.Add(fdr);
        lock (_trackedHere)
            _trackedHere.Remove(fdr.Callsign);
        lock (_lock)
        {
            // Whatever was queued for it would recreate the record after the delete.
            _pending.Remove(fdr.Callsign);
            _pendingCancels.Add(fdr.Callsign);
        }

        ActionLog.Log("FDR", $"{fdr.Callsign} cancelled by its controller - deleting it from OzServer", new
        {
            action = "fdr_owner_cancel",
            fdr_callsign = fdr.Callsign,
            state = fdr.State.ToString(),
            tracked_here = trackedHere,
            named_on_server = namedOnServer
        });
        return true;
    }

    // Tags this session has tracked, to recognise the moment one is dropped.
    readonly HashSet<string> _trackedHere = new(StringComparer.OrdinalIgnoreCase);

    // A tag this controller was tracking that now has no tracker at all has been dropped by them -
    // a handoff leaves it tracked by the receiver. Reported as dropped, from here whether or not
    // this session would otherwise report the flight, so the next controller to connect does not
    // pick it straight back up (FdrResumeActivation). QLK1035, dropped by ML-MUN_CTR, was taken
    // again by their own next connect two seconds later.
    bool ReportDrop(FDP2.FDR fdr)
    {
        bool wasMine;
        lock (_trackedHere)
        {
            if (fdr.IsTrackedByMe)
            {
                _trackedHere.Add(fdr.Callsign);
                return false;
            }
            wasMine = _trackedHere.Remove(fdr.Callsign);
        }

        // Not while disconnecting: vatSys lets every tag go then, and that is not a drop.
        if (!wasMine || fdr.IsTracked || !Network.IsConnected)
            return false;

        var dto = BuildFdrDto(fdr, forceRoute: false);
        dto.Dropped = true;
        Merge(dto);

        ActionLog.Log("FDR", $"Dropped {fdr.Callsign} - it will not be picked up automatically", new
        {
            action = "fdr_dropped",
            fdr_callsign = fdr.Callsign,
            state = fdr.State.ToString()
        });
        return true;
    }

    // Sends without waiting for the flush timer. A change of authority cannot ride the next batch:
    // until it reaches the backend there is no record that this controller holds the tag, and an
    // ungraceful disconnect in that window leaves a resume nothing to hand back. Uses the same
    // _pending merge and FlushAsync as everything else, so if a flush is already running the
    // update goes out on the next tick.
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
        // plan track is - see FloatingSweep - not where an uncoupled return with the same callsign
        // happens to be; CoupleAssist couples the two when they are the same aircraft.
        var fdr = track.CoupledFDR;
        if (string.IsNullOrEmpty(fdr?.Callsign))
            return;

        // At most one position per aircraft every RadarEvery. The batch goes out every
        // FlushInterval and keeps only the latest position, while vatSys raises an update for every
        // fast-position report - several a second per aircraft nearby - and each one works out the
        // responsible sector and the sector the aircraft is in. Slower PCs could not spare that.
        var now = DateTime.UtcNow;
        lock (_lastRadarPush)
        {
            if (_lastRadarPush.TryGetValue(fdr!.Callsign, out var last) && now - last < RadarEvery)
                return;
            _lastRadarPush[fdr.Callsign] = now;
        }

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

    // Gates every push, checked every time. A flight qualifies when:
    //
    //  - this controller tracks it and it is activated; or
    //  - no OzServer controller tracks it, it is activated (ESTed) or completed (STATE_FINISHED),
    //    and this session is the one responsible for reporting it (ResponsibleSectorForUntracked).
    //
    // Connected as well: vatSys keeps delivering updates for a while after a disconnect, and what
    // is queued then would be sent first in the next session.
    bool ShouldPush(FDP2.FDR fdr)
    {
        if (!Network.IsConnected)
            return false;

        ForgetLandingOfEarlierFlight(fdr);

        if (fdr.IsTrackedByMe)
            return fdr.ESTed;

        // Tracked by another OzServer controller: theirs to report. One tracked by a controller who
        // is not on OzServer is treated as untracked below - see TrackedOutsideOzServer.
        if (fdr.IsTracked && !TrackedOutsideOzServer(fdr))
            return false;

        // vatSys finishes an untracked FDR when its pilot drops off this client's picture
        // (Network.ProcessRemovedPilot -> FDP2.CancelFDR), whether the pilot disconnected or only
        // left visibility range. A finish is passed on only when the aircraft was last seen down
        // at its destination; otherwise the record is left for a client that can still see it.
        if (fdr.State == FDP2.FDR.FDRStates.STATE_FINISHED && !IsCredibleFinish(fdr))
        {
            // Only when the pilot has gone from this client's picture. A finish made while they
            // are still on it is a controller's, and is not one to bring back.
            if (!string.IsNullOrEmpty(fdr.Callsign) && !IsPilotOnline(fdr.Callsign)
                && FinishedOutOfView.TryAdd(fdr.Callsign, DateTime.UtcNow))
            {
                ActionLog.Log("FDR", $"{fdr.Callsign} finished by vatSys out of view - not reported, kept active on OzServer", new
                {
                    action = "fdr_finish_out_of_view",
                    fdr_callsign = fdr.Callsign
                });
            }
            return false;
        }

        // An untracked FDR is reported only from a radar return, so it has to be coupled: a
        // floating one's position is a prediction. (A finish that passed the test above was seen
        // on radar too - see IsCredibleFinish.) The exception is an aircraft still on the ground:
        // vatSys couples nothing below 50 kt (RDP.TryAutoCouple), so an activated flight at the
        // gate or taxiing is uncoupled until its take-off roll, and nobody reported it at all. Its
        // own return, matched by callsign, is a real position, so it stands in for the coupling.
        if (fdr.State != FDP2.FDR.FDRStates.STATE_FINISHED && fdr.CoupledTrack == null && GroundReturn(fdr) == null)
            return false;

        return ResponsibleSectorForUntracked(fdr, out _);
    }

    // Landings seen on radar, reported to OzServer as completed.
    //
    // vatSys finishes an FDR on landing only at the instant it uncouples the track, and only if the
    // aircraft is then below 50 kt, on the ground and within 2 nm of an assigned arrival runway's
    // threshold (RDP.TryAutoDeCouple -> TryAutoFinishFDR). A landing that misses any of those stays
    // UNCONTROLLED at the aerodrome until its record ages out. So CheckLanded watches the radar
    // return itself, and the flight is reported FINISHED from then on (see Merge). vatSys's own
    // FDR is left alone: the tower may still be working the taxi, and finishing it would take the
    // tag off their scope.
    //
    // Keyed by callsign, with the flight that landed (ATD and destination), because a callsign
    // outlives a landing: circuits take off again on the same flight plan, and a turnaround files
    // a new one. The landing is forgotten when the aircraft is at speed again (CheckLanded) or the
    // callsign carries another flight plan (ForgetLandingOfEarlierFlight). Forgetting it wrongly
    // costs nothing: an aircraft still down at its destination is seen landed again.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Atd, string Destination)> Landed =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _landingChecked = new(StringComparer.OrdinalIgnoreCase);

    // Forgets a landing when the callsign now carries a different flight plan - another departure
    // time or destination. Run before every push decision (ShouldPush), so the new flight is never
    // reported as completed.
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
            // At speed again after a landing: a take-off roll. ContainsKey first, since this runs
            // for every moving track on every radar update.
            if (Landed.ContainsKey(callsign!))
                ForgetLanding(callsign!, "at speed again");
            return;
        }

        if (Landed.ContainsKey(callsign!))
            return;

        // Slow tracks are mostly aircraft parked at gates: one look per callsign every 15 s.
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
        // Tracked by another OzServer controller: their client reports it.
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
        // An untracked report has to name a sector this session holds - see Choose.
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

    // Tracked by a controller who is not on OzServer - a neighbouring FIR's, or anyone without the
    // plugin. They never report it, so for reporting it counts as untracked and the untracked rules
    // decide who keeps the record current.
    //
    // OzServer lists every controller whose plugin is calling it (PluginClients), and that decides.
    // Holding a sector also counts, for the moment between a controller's claim and the next sync.
    //
    // Without the list (a backend that predates it), a controller who holds no sector counts as on
    // OzServer only while the record names them as the controller. Any name used to do: a tag
    // handed to a controller without the plugin kept the record naming whoever handed it over, and
    // every client then left it to the new controller, who never reports - so FDX656 sat in
    // handover on OzServer.
    bool TrackedOutsideOzServer(FDP2.FDR fdr)
    {
        var tracker = fdr.ControllerTracking?.Callsign;
        if (!fdr.IsTracked || fdr.IsTrackedByMe || string.IsNullOrEmpty(tracker))
            return false;
        if (_tracker.ControlledByOthers.Values.Any(o => string.Equals(o.Callsign, tracker, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (_tracker.PluginClients is { } pluginClients)
            return !pluginClients.Contains(tracker!);

        if (!string.IsNullOrEmpty(fdr.Callsign) && ServerFdrSnapshot.TryGet(fdr.Callsign, out var entry)
            && entry.ControllingCid != null
            && string.Equals(entry.ControllingCallsign, tracker, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // FDRs vatSys finished because they went out of view here (see ShouldPush). FdrResumeActivation
    // activates one again if it reappears while OzServer still has it active.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> FinishedOutOfView =
        new(StringComparer.OrdinalIgnoreCase);

    // Whether vatSys has this pilot on its network picture. It drops a pilot before it finishes
    // their FDR (Network.ProcessRemovedPilot), while the radar track coasts on for some 20 seconds
    // - so this, not the presence of a track, says whether an aircraft is really in view.
    public static bool IsPilotOnline(string callsign)
    {
        try
        {
            return Network.GetOnlinePilots?.Any(p => string.Equals(p.Callsign, callsign, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Landed, or as good as: within 15 nm of its destination, with a radar altitude less than
    // 3,000 ft above it. An aircraft with no Mode C return never qualifies.
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

    // One connected controller keeps each untracked activated (or completed) FDR current on the
    // server, so its record follows the aircraft after its controller has gone. Only controllers
    // who can see the aircraft count, since a client receives positions only inside its
    // visibility range. Among those:
    //
    //  1. whoever holds the sector the aircraft is in;
    //  2. otherwise whoever holds the held sector nearest to it (ties broken by sector name, so
    //     every client picks the same one).
    //
    // This session sees the aircraft when it has a live radar track for it. Another controller is
    // taken to see it when vatSys lists them online and it is within the Range of their Position -
    // their main vis centre only, so an error leaves two clients reporting (the server merges
    // them) rather than none.
    //
    // Returns the deciding sector for the push to carry as authority_sector: one this session
    // holds, which is what the backend checks an untracked report against.
    bool ResponsibleSectorForUntracked(FDP2.FDR fdr, out string? sectorName)
    {
        sectorName = null;
        if (fdr.IsTracked && !TrackedOutsideOzServer(fdr))
            return false;
        if (!fdr.ESTed && fdr.State != FDP2.FDR.FDRStates.STATE_FINISHED)
            return false;

        // Where the aircraft is, by this client's own radar. With no return here it is never ours.
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

        // Somebody else's, as far as this client can tell - unless they are not reporting it.
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

    // The watchdog. The choice above rests on this client's estimate of the others, and it cannot
    // know whether the chosen client has the aircraft coupled, has it within its real coverage,
    // can reach OzServer, or agrees on who holds what. Any of those can leave every client that
    // sees the aircraft deferring to one that is not reporting it.
    //
    // So the server's record decides. A client that was not chosen takes over when the record has
    // not been updated for StaleAfter, or does not exist, and continues while the record's last
    // update came from one of its own sectors (authority_sector). Once the chosen controller
    // reports, its sector is on the record and this client stops; at worst two clients report for
    // one scan interval.
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    // A snapshot older than this says nothing about now, and no takeover is based on it.
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

        // Reported as from one of this session's own sectors, which is what the backend checks.
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
    // vatSys draws their flight plan track, which moves along the route without raising an FDR
    // event. Every FloatingEvery-th flush they are queued again with that position, so the map
    // follows. Only tracked ones: ShouldPush rejects an unfinished floating FDR nobody tracks.
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

    // The radar return of an activated flight that is uncoupled only because it has not started
    // its take-off roll: below vatSys's 50 kt auto-couple floor, matched by callsign. Null for
    // anything moving faster - an uncoupled return at speed is CoupleAssist's to couple, and its
    // flight plan is not reported until it is.
    const double GroundSpeedFloor = 50;

    static RDP.RadarTrack? GroundReturn(FDP2.FDR fdr)
    {
        if (fdr.CoupledTrack != null || !fdr.ESTed)
            return null;
        return SectorLocator.LiveTrack(fdr) is { LatLong: not null } track && track.GroundSpeed < GroundSpeedFloor
            ? track
            : null;
    }

    // Every SweepEvery-th flush, every FDR this session answers for is queued again in full. An FDR
    // that raises no events - parked at the gate, or completed - would otherwise age out of the
    // backend (FDR_RETAIN_MINUTES), and one this session has just become responsible for (a sector
    // claimed or handed over) would not appear until something about it changed.
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

        // A landing stands on the server's record, whatever state this client's vatSys still has
        // the FDR in, until the callsign flies again (see Landed).
        if (Landed.ContainsKey(dto.Callsign))
            dto.State = nameof(FDP2.FDR.FDRStates.STATE_FINISHED);

        lock (_lock)
        {
            // Cancelled and awaiting its delete: nothing may recreate it (see ReportOwnerCancel).
            if (_pendingCancels.Contains(dto.Callsign))
                return;

            if (_pending.TryGetValue(dto.Callsign, out var existing))
                CopyNonNull(dto, existing);
            else
                _pending[dto.Callsign] = dto;
        }
    }

    // The label state last queued per tracked flight: scratchpad, global ops and the "C" (see
    // OzServerFdrUpdateDto.CPrompt). The C lives on the Track and vatSys raises nothing when a
    // middle click toggles it, so no FDR update carries the change. It is looked for once per flush,
    // on this controller's own tags only, and queued only when something has changed.
    readonly Dictionary<string, string> _labelStateKeys = new(StringComparer.OrdinalIgnoreCase);

    // Tags whose label this client must not speak for yet: just accepted from another controller,
    // with the previous controller's label still being read back (SectorTagHandoff.AcceptTransfer).
    // Until released, a push for one leaves the whole label out, as a non-tracking client's would.
    // The hold expires by itself so a read that never completes cannot silence a tag for good.
    static readonly TimeSpan LabelHoldLimit = TimeSpan.FromSeconds(15);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LabelStateHeld =
        new(StringComparer.OrdinalIgnoreCase);

    public static void HoldLabelState(string callsign) => LabelStateHeld[callsign] = DateTime.UtcNow + LabelHoldLimit;

    public static void ReleaseLabelState(string callsign) => LabelStateHeld.TryRemove(callsign, out _);

    static bool IsLabelStateHeld(string? callsign) =>
        !string.IsNullOrEmpty(callsign) && LabelStateHeld.TryGetValue(callsign!, out var until) && DateTime.UtcNow < until;

    // Whether this session has taken over a tag's label from OzServer's record. A tag assumed in
    // vatSys after its controller logged off arrives with a bare label - vatSys shares the CFL,
    // scratchpad and global ops only live, from whoever is tracking the tag - and pushing that as
    // fact would erase the record. So the label is left out of every push until the record has
    // been read and anything it holds put into whatever the label is missing; what the controller
    // has already typed is kept. This applies however the tag became this controller's.
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
            // Taken over anyway: better to start from the label in front of the controller than
            // never to speak for it.
            ActionLog.Log("FDR", $"Couldn't read {callsign}'s label from OzServer on pickup: {ex.Message}");
        }

        RunOnUiThread(() =>
        {
            try
            {
                if (stored == null || !fdr.IsTrackedByMe)
                    return;

                // Without the C: a reconnect (TagResumeRecovery) and a sector change
                // (SectorTagHandoff) bring it with them, a handoff or assume does not.
                var applied = LabelStateRestore.Apply(fdr, stored, replaceCfl: false, includeCPrompt: false);
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
                // The next flush pushes the whole label: QueueLabelStateChanges has no key for it.
                LabelAdopted.TryUpdate(callsign, true, false);
            }
        });
    }

    static void RunOnUiThread(Action action)
    {
        if (System.Windows.Forms.Application.OpenForms["MainForm"] is System.Windows.Forms.Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }

    void QueueLabelStateChanges()
    {
        // Copied first and guarded throughout, like the two sweeps above: this runs on the flush
        // timer's thread while vatSys adds and removes FDRs, and an exception here would escape
        // FlushAsync and hold the whole batch back until the next tick.
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
                    LabelStateRestore.ForgetPending(fdr.Callsign);
                    if (LabelAdopted.TryRemove(fdr.Callsign, out _))
                        lock (_lock)
                            _labelStateKeys.Remove(fdr.Callsign);
                    continue;
                }

                if (!fdr.ESTed || !IsLabelAdopted(fdr) || IsLabelStateHeld(fdr.Callsign))
                    continue;

                var track = MMI.FindTrack(fdr);
                // The deviation menu writes OTDLeft/OTDRight and raises nothing either.
                var key = $"{fdr.LabelOpData}\u001f{fdr.GlobalOpData}\u001f{track?.CPrompt}\u001f{fdr.OTDLeft}\u001f{fdr.OTDRight}";

                lock (_lock)
                {
                    if (_labelStateKeys.TryGetValue(fdr.Callsign, out var last) && last == key)
                        continue;
                }

                Merge(BuildFdrDto(fdr, forceRoute: false));

                // Recorded only once the update is queued, so a read that failed part-way is retried
                // on the next flush.
                lock (_lock)
                    _labelStateKeys[fdr.Callsign] = key;
            }
            catch (Exception ex)
            {
                ErrorThrottle.Report("fdr-label-state", $"Couldn't read {fdr.Callsign}'s label state for OzServer: {ex.Message}", ex);
            }
        }
    }

    const int MaxBatchSize = 200;

    async Task FlushAsync()
    {
        if (OzServerMode.Disabled)
            return;

        // Pending updates stay queued rather than being dropped. An unsupported server (see
        // NetworkServer.Current) is treated like no connection: every push would fail before it
        // was sent, and report an error on every flush.
        if (!Network.IsConnected || NetworkServer.Current == null)
            return;

        // One batch in flight at a time - see _flushing.
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
            List<string> cancels;

            lock (_lock)
            {
                if (_pending.Count == 0 && _pendingCancels.Count == 0)
                    return;

                batch = _pending.Values.ToList();
                _pending.Clear();
                cancels = _pendingCancels.ToList();
                _pendingCancels.Clear();
            }

            if (batch.Count > 0)
            {
                try
                {
                    // In parts: the server takes at most 500 flights and 2 MB in one request, and a
                    // sweep carrying full routes for a busy session can pass either.
                    for (var offset = 0; offset < batch.Count; offset += MaxBatchSize)
                        await _api.UpdateFdrBatchAsync(batch.Skip(offset).Take(MaxBatchSize));
                }
                catch (Exception ex)
                {
                    // Once per outage rather than once per flush - see ErrorThrottle.
                    ErrorThrottle.Report("fdr-flush", $"Couldn't push FDR batch to OzServer: {ex.Message}", ex);
                }
            }

            // After the batch, and inside the one-flush-at-a-time guard, so no update for the
            // flight - this batch's or an earlier one still in flight - can land after the delete
            // and recreate it.
            foreach (var callsign in cancels)
            {
                try
                {
                    await _api.CancelFlightAsync(callsign);
                    ActionLog.Log("FDR", $"Deleted {callsign} from OzServer - cancelled by its controller",
                        new { action = "fdr_owner_cancel_sent", fdr_callsign = callsign });
                }
                catch (OzServerApiException ex) when (ex.StatusCode is 403 or 404)
                {
                    // Not ours by the server's record, or already gone: nothing to retry. The
                    // record ages out as it would have.
                    ActionLog.Log("FDR", $"OzServer didn't delete {callsign}: {ex.Message}",
                        new { action = "fdr_owner_cancel_refused", fdr_callsign = callsign, status = ex.StatusCode });
                }
                catch (Exception ex)
                {
                    // Offline or the server erred: tried again on the next flush.
                    lock (_lock)
                        _pendingCancels.Add(callsign);
                    ErrorThrottle.Report("fdr-cancel", $"Couldn't delete {callsign} from OzServer: {ex.Message}", ex);
                }
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
            // 0 (or below) is vatSys's "not set" for these levels and for TAS, as -1 is for
            // AssignedSSRCode and DateTime.MaxValue for ATD and ETD below.
            Rfl = fdr.RFL > 0 ? fdr.RFL : null,
            CflLower = fdr.CFLLower > 0 ? fdr.CFLLower : null,
            CflUpper = fdr.CFLUpper > 0 ? fdr.CFLUpper : null,
            // Whether this push states the label as fact, "no cleared level" included - see
            // OzServerFdrUpdateDto.CflLower. Only the tracking controller's client does: any other
            // client's copy is usually empty, and stating it would erase what the tracking
            // controller set. Also withheld until this session has taken the label over from
            // OzServer's record (LabelAdopted) and while it is held (LabelStateHeld).
            ClearanceKnown = fdr.IsTrackedByMe && IsLabelAdopted(fdr) && !IsLabelStateHeld(fdr.Callsign),
            AssignedSsrCode = fdr.AssignedSSRCode == -1 ? null : fdr.AssignedSSRCode,
            Atd = fdr.ATD == DateTime.MaxValue ? null : fdr.ATD,
            Etd = fdr.ETD == DateTime.MaxValue ? null : fdr.ETD,
            EetMinutes = fdr.EET == TimeSpan.Zero ? null : (int)fdr.EET.TotalMinutes,
            Tas = fdr.TAS > 0 ? fdr.TAS : null,
            TextOnly = fdr.TextOnly,
            ReceiveOnly = fdr.ReceiveOnly,
            // "" rather than null: a cleared scratchpad is a fact the tracking controller states
            // (see OzServerFdrUpdateDto.LabelOpData). Serialised only when ClearanceKnown.
            LabelOpData = fdr.LabelOpData ?? "",
            GlobalOpData = fdr.GlobalOpData ?? "",
            // Off-track deviations, 0 for none - see OzServerFdrUpdateDto.OtdLeft.
            OtdLeft = fdr.OTDLeft,
            OtdRight = fdr.OTDRight,
            Remarks = NullIfEmpty(fdr.Remarks),
        };

        // The "C", which vatSys keeps on the track rather than the FDR - see
        // OzServerFdrUpdateDto.CPrompt. Left unset with no track to read it from. A C restored before
        // the track existed is put on first, so this push does not report it as off.
        if (MMI.FindTrack(fdr) is { } track)
        {
            if (fdr.IsTrackedByMe)
                LabelStateRestore.ApplyPendingCPrompt(fdr, track);
            dto.CPrompt = track.CPrompt;
        }

        FillAuthority(dto, fdr);
        // Tracked again, so no longer dropped - see OzServerFdrUpdateDto.Dropped.
        if (fdr.IsTrackedByMe)
            dto.Dropped = false;
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
        FillOutsideLabel(dto, fdr);
        return dto;
    }

    // The label of a tag tracked by a controller not on OzServer (TrackedOutsideOzServer): nobody's
    // client saves it, so it went stale on OzServer - ML-BLA_CTR, with OzServer turned off, changed
    // JST520's level and the record kept ML_APP's. vatSys shares the tracking controller's cleared
    // level, scratchpad and global ops live with every connected controller, so the client
    // reporting the flight has their real values, and states them.
    //
    // Only those three. The C and off-track deviations exist only on the tracking controller's own
    // vatSys; what this client holds is its own, and is left out.
    //
    // And only once something has changed while this client watched. A client that connected
    // after the controller set them holds empty fields - vatSys sends them only when they change -
    // and stating those would erase the record. A change of tracker starts the watch again.
    readonly Dictionary<string, (string? Tracker, string Key, bool Live)> _outsideLabel =
        new(StringComparer.OrdinalIgnoreCase);

    void FillOutsideLabel(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        if (fdr.IsTrackedByMe || string.IsNullOrEmpty(fdr.Callsign))
            return;

        var tracker = fdr.ControllerTracking?.Callsign;
        if (!TrackedOutsideOzServer(fdr))
        {
            lock (_outsideLabel)
                _outsideLabel.Remove(fdr.Callsign);
            return;
        }

        var key = $"{fdr.CFLLower}\u001f{fdr.CFLUpper}\u001f{fdr.LabelOpData}\u001f{fdr.GlobalOpData}";
        bool live;
        lock (_outsideLabel)
        {
            if (!_outsideLabel.TryGetValue(fdr.Callsign, out var seen)
                || !string.Equals(seen.Tracker, tracker, StringComparison.OrdinalIgnoreCase))
            {
                _outsideLabel[fdr.Callsign] = (tracker, key, false);
                return;
            }

            live = seen.Live || seen.Key != key;
            _outsideLabel[fdr.Callsign] = (tracker, key, live);
        }

        if (!live)
            return;

        dto.ClearanceKnown = true;
        dto.CPrompt = null;
        dto.OtdLeft = null;
        dto.OtdRight = null;
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
            // predicted trajectory enters a volume (FDP2.CalculateAffectedSectors), with the volume
            // in Tag. Identified by segment type, never by name. The sector is vatSys's own answer
            // for that volume (SectorsVolumes.FindSector), the lookup its handoff window uses.
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

    // The sector the aircraft is physically inside - a different question from who owns the tag
    // (FillAuthority) - resolved against the full SectorsVolumes.Sectors list, whether or not anyone
    // has claimed that sector on OzServer. Null when the aircraft is inside no known sector volume
    // at its position and level; CopyNonNull then leaves any pending value in place.
    static void FillCurrentSector(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        dto.CurrentSector = SectorLocator.Resolve(fdr, SectorsVolumes.Sectors)?.Name;
    }

    // A flight with no coupled radar track - out of coverage, procedural - gets no position from
    // FillPosition, which runs only for a radar track. This reports fdr.GetLocation() instead: the
    // predicted position vatSys draws the tag at. Once a track is coupled this does nothing, and
    // the track's live position from FillPosition stands.
    static void FillPredictedPosition(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        if (fdr.CoupledTrack != null)
            return;

        // On the ground the aircraft's own return is where it is, not the flight plan's guess at
        // the aerodrome (see GroundReturn).
        if (GroundReturn(fdr) is { } ground)
        {
            FillPosition(dto, ground);
            // Stated, not left to the track's own flag: vatSys works that out from terrain height
            // for an uncoupled return, and one read as airborne would have every other client
            // place this flight on its route as departed (FdrResumeActivation.FloatAt).
            if (fdr.ATD == DateTime.MaxValue)
                dto.OnGround = true;
            return;
        }

        var location = fdr.GetLocation();
        if (location == null)
            return;

        dto.Lat = location.Latitude;
        dto.Lon = location.Longitude;
    }

    // A tracked push names this session; an untracked one names nobody - null is what the backend
    // and the map read as "no tag owner". AuthoritySector is vatSys's controlling sector here;
    // callers replace it on an untracked push (FillResponsibleSector, CheckLanded) with a sector
    // this session holds, which is what the backend accepts an untracked report against.
    static void FillAuthority(OzServerFdrUpdateDto dto, FDP2.FDR fdr)
    {
        dto.AuthoritySector = fdr.ControllingSector?.Name;
        // Who tracks it in vatSys, whoever that is - for a controller not on OzServer this is the
        // only place their callsign reaches the record (controlling_* is null for them).
        dto.TrackedBy = fdr.ControllerTracking?.Callsign ?? "";

        if (!fdr.IsTrackedByMe)
        {
            dto.ControllingCid = null;
            dto.ControllingCallsign = null;
            return;
        }

        // Left null rather than defaulted when there is no identity yet: null is what the backend
        // reads as "no tag owner".
        var me = NetworkIdentity.Current;
        if (me != null)
            dto.ControllingCid = me.Value.Cid;

        dto.ControllingCallsign = Network.Callsign;
    }

    // RDP.RadarTrack uses sentinels rather than nulls for "not set": -1 for CorrectedAltitude,
    // GroundSpeed and Heading, -9999 for VerticalSpeed. Those are left null on the DTO rather than
    // sent as readings.
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

    // Merges a newer update into the one already pending for the callsign: every non-null property
    // of source overwrites target's, so a partial update (a radar-only position) does not blank
    // fields it does not carry. Reflection-based, so a new OzServerFdrUpdateDto property is merged
    // without a change here.
    //
    // The two authority fields are copied as a unit, nulls included: every producer runs
    // FillAuthority, so source's answer is current, and null is itself an answer ("no tag owner").
    // That is also why they are marked NullValueHandling.Include on OzServerFdrUpdateDto.
    static void CopyNonNull(OzServerFdrUpdateDto source, OzServerFdrUpdateDto target)
    {
        target.ControllingCid = source.ControllingCid;
        target.ControllingCallsign = source.ControllingCallsign;

        // The label state is also copied as a unit, but only from a source that read it. A full
        // FDR push's null means "no cleared level" and has to replace whatever is pending; a
        // radar-only update's null means it never looked, and must not undo a queued clearance.
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
            target.OtdLeft = source.OtdLeft;
            target.OtdRight = source.OtdRight;
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
