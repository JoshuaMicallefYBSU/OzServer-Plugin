using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using vatsys;

namespace OzServerPlugin;

// Thrown for transport failures (server unreachable) and API-reported failures (4xx/5xx with a
// {"message": "..."} body) alike; Message is suitable to show the controller in both cases.
// Conflicts is populated only for a claim's 409: the covered sectors already owned by someone
// else, each with its owner, so the caller can deal with each one individually (see
// OzServerOwnershipTracker.HandleConflictAsync).
public class OzServerApiException : Exception
{
    public int? StatusCode { get; }
    public IReadOnlyList<OzServerSectorConflictDto> Conflicts { get; }

    public OzServerApiException(string message, int? statusCode = null, IReadOnlyList<OzServerSectorConflictDto>? conflicts = null) : base(message)
    {
        StatusCode = statusCode;
        Conflicts = conflicts ?? Array.Empty<OzServerSectorConflictDto>();
    }
}

public class OzServerSectorConflictDto
{
    [JsonProperty("sector")] public string Sector { get; set; } = "";
    [JsonProperty("owner")] public OzServerControlledSectorOwnerDto? Owner { get; set; }
}

public class OzServerSectorDto
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("full_name")] public string FullName { get; set; } = "";
}

// One forwarded plugin log line - see ClientLogForwarder.
public class OzServerClientLogLineDto
{
    [JsonProperty("at")] public DateTimeOffset At { get; set; }
    [JsonProperty("category")] public string Category { get; set; } = "";
    [JsonProperty("message")] public string Message { get; set; } = "";
    [JsonProperty("plugin_version")] public string? PluginVersion { get; set; }
    [JsonProperty("session_id")] public string? SessionId { get; set; }
    [JsonProperty("sequence")] public long? Sequence { get; set; }
    [JsonProperty("context")] public object? Context { get; set; }
}

public class OzServerSectorOwnershipRequestDto
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("sector_id")] public int SectorId { get; set; }
    // Every sector requested by one Apply carries the same group id, so the receiving controller
    // is notified once for the whole request rather than once per sector.
    [JsonProperty("group_id")] public string GroupId { get; set; } = "";
    [JsonProperty("requesting_cid")] public int RequestingCid { get; set; }
    [JsonProperty("requesting_callsign")] public string RequestingCallsign { get; set; } = "";
    [JsonProperty("target_cid")] public int TargetCid { get; set; }
    [JsonProperty("target_callsign")] public string TargetCallsign { get; set; } = "";
    // Populated in request lists, not in the response to POST .../request.
    [JsonProperty("sector")] public OzServerSectorDto? Sector { get; set; }
    // Set once the owner has refused the request. A rejected request is kept server-side so the
    // controller who asked can be told; the row would otherwise vanish from their list as it does
    // on an accept or a cancel. Null while awaiting a decision, and only ever seen in `by_me`:
    // `from_me` is filtered to pending server-side.
    [JsonProperty("rejected_at")] public DateTimeOffset? RejectedAt { get; set; }
}

public class OzServerMyRequestsDto
{
    [JsonProperty("by_me")] public List<OzServerSectorOwnershipRequestDto> ByMe { get; set; } = new();
    [JsonProperty("from_me")] public List<OzServerSectorOwnershipRequestDto> FromMe { get; set; } = new();
}

// Everything the ownership poll needs in one response (GET /sectors/sync). The three members are
// the payloads GET /sectors/mine, /sectors/controlled and /sector-requests return individually.
public class OzServerSyncDto
{
    [JsonProperty("mine")] public List<OzServerSectorDto> Mine { get; set; } = new();
    [JsonProperty("controlled")] public List<OzServerControlledSectorDto> Controlled { get; set; } = new();
    [JsonProperty("requests")] public OzServerMyRequestsDto Requests { get; set; } = new();
    // Every controller whose plugin is calling OzServer now, sectors or not. Null from a backend
    // that predates the list - see OzServerOwnershipTracker.PluginClients.
    [JsonProperty("plugin_clients")] public List<OzServerControlledSectorOwnerDto>? PluginClients { get; set; }
    // Controllers without the plugin whose airspace is among Controlled (Owner.External).
    [JsonProperty("external_controllers")] public List<OzServerExternalControllerDto>? ExternalControllers { get; set; }
}

public class OzServerAcceptBatchResultDto
{
    [JsonProperty("request_id")] public int RequestId { get; set; }
    [JsonProperty("sector")] public string? Sector { get; set; }
    [JsonProperty("accepted")] public bool Accepted { get; set; }
    [JsonProperty("message")] public string Message { get; set; } = "";
}

public class OzServerRejectBatchResponseDto
{
    [JsonProperty("rejected")] public List<int> Rejected { get; set; } = new();
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

public class OzServerAcceptBatchResponseDto
{
    [JsonProperty("results")] public List<OzServerAcceptBatchResultDto> Results { get; set; } = new();
    // The state the accept produced, so the caller does not have to ask for it separately.
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

// What POST /sectors/commit reports: which names moved, so the window can report a partial
// outcome.
public class OzServerCommitResultDto
{
    [JsonProperty("claimed")] public List<string> Claimed { get; set; } = new();
    [JsonProperty("released")] public List<string> Released { get; set; } = new();
    [JsonProperty("requested")] public List<string> Requested { get; set; } = new();
    [JsonProperty("skipped")] public List<string> Skipped { get; set; } = new();
    // Covered sectors left with a controller who is logged on as that position inside the claimed
    // group (the backend's claimGroup `withheld`). Not a conflict and not shown to the controller,
    // but logged, so "why did that Apply not take X" has an answer.
    [JsonProperty("withheld")] public List<string> Withheld { get; set; } = new();
    [JsonProperty("failed")] public List<string> Failed { get; set; } = new();
}

// A message from the people running OzServer, sent from the website's /console (GET /messages).
public class OzServerDeveloperMessageDto
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = "";
    [JsonProperty("body")] public string Body { get; set; } = "";
    // Whether the sender asked for answers: the popup offers Reply only then.
    [JsonProperty("allow_reply")] public bool AllowReply { get; set; }
    [JsonProperty("sent_by")] public string SentBy { get; set; } = "";
    [JsonProperty("created_at")] public DateTime CreatedAt { get; set; }
}

// What POST /sectors/resume answers with: the sectors recovered, plus the resulting state.
// Anything not listed was taken by someone else while this controller was away.
public class OzServerResumeResponseDto
{
    [JsonProperty("resumed")] public List<string> Resumed { get; set; } = new();
    // Tags the backend handed back: flights this controller was working before they dropped,
    // minus any another controller has since picked up. They come straight back rather than
    // flashing for acceptance - see TagResumeRecovery.
    [JsonProperty("flights")] public List<string> Flights { get; set; } = new();
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

public class OzServerCommitResponseDto
{
    [JsonProperty("result")] public OzServerCommitResultDto Result { get; set; } = new();
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

// What every ownership-changing action answers with: the resulting state, in the shape
// GET /sectors/sync returns, which saves a follow-up GET.
public class OzServerActionResultDto
{
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

public class OzServerControlledSectorOwnerDto
{
    [JsonProperty("cid")] public int Cid { get; set; }
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
    // A controller connected without the plugin, whose airspace OzServer counts as held (the API's
    // external-control.ts). Nothing can be requested from them: see ExternalControl. Their Cid is 0
    // where OzServer does not know it.
    [JsonProperty("external")] public bool External { get; set; }
}

// One controller connected without the plugin, as OzServer worked out their airspace.
public class OzServerExternalControllerDto
{
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
    [JsonProperty("sectors")] public List<string> Sectors { get; set; } = new();
}

// What this client's vatSys sees of one other controller (POST /network/controllers).
public class OzServerNetworkControllerDto
{
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
    [JsonProperty("real_name")] public string? RealName { get; set; }
    [JsonProperty("frequencies")] public List<string> Frequencies { get; set; } = new();
    // Their position, by the profile's Positions.xml (PrimaryPosition.DefaultSectorsForCurrentSession).
    [JsonProperty("sectors")] public List<string> Sectors { get; set; } = new();
    // Their controller information lines, which often say what else they are covering
    // ("Extending MUN, GUN"). Read by the API, not here.
    [JsonProperty("atis")] public List<string> Atis { get; set; } = new();
}

// One sector another controller owns. The backend expands a claimed grouping sector (e.g. TBD)
// into one row per covered sector (TBD, AUG, AAE, ...) with the same owner, so the list is flat
// and needs no client-side expansion.
public class OzServerControlledSectorDto
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("full_name")] public string FullName { get; set; } = "";
    [JsonProperty("owner")] public OzServerControlledSectorOwnerDto? Owner { get; set; }
}

class OzServerErrorDto
{
    [JsonProperty("message")] public string Message { get; set; } = "";
    [JsonProperty("conflicts")] public List<OzServerSectorConflictDto>? Conflicts { get; set; }
}

// One flight in POST /fdr/batch; mirrors flightSchema in OzServer-API (routes/flights.ts).
// controlling_cid/controlling_callsign are the flight's datalink authority, not the session
// identity PostRawAsync attaches: this session's own when it tracks the flight, and null - "nobody
// tracks this tag" - when it pushes an untracked FDR it holds the data authority for (see
// FdrSync.ShouldPush). Every field besides Callsign is nullable, so a partial or position-only
// push need not invent values.
public class OzServerFdrUpdateDto
{
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";

    // Always sent, even when null: for these two fields null is itself meaningful ("free", no
    // authority). Every other field is omitted when null (see BodySerializer), so a partial push -
    // a position-only radar update, say - cannot blank a value a fuller push set.
    [JsonProperty("controlling_cid", NullValueHandling = NullValueHandling.Include)] public int? ControllingCid { get; set; }
    [JsonProperty("controlling_callsign", NullValueHandling = NullValueHandling.Include)] public string? ControllingCallsign { get; set; }

    [JsonProperty("state")] public string? State { get; set; }
    [JsonProperty("flight_rules")] public string? FlightRules { get; set; }
    [JsonProperty("aircraft_type")] public string? AircraftType { get; set; }
    [JsonProperty("aircraft_wake")] public string? AircraftWake { get; set; }
    [JsonProperty("aircraft_equip")] public string? AircraftEquip { get; set; }
    [JsonProperty("aircraft_surv_equip")] public string? AircraftSurvEquip { get; set; }
    [JsonProperty("aircraft_count")] public int? AircraftCount { get; set; }
    [JsonProperty("dep_airport")] public string? DepAirport { get; set; }
    [JsonProperty("des_airport")] public string? DesAirport { get; set; }
    [JsonProperty("route")] public string? Route { get; set; }
    [JsonProperty("sid_star_string")] public string? SidStarString { get; set; }
    [JsonProperty("runway_string")] public string? RunwayString { get; set; }
    [JsonProperty("departure_runway")] public string? DepartureRunway { get; set; }
    [JsonProperty("rfl")] public int? Rfl { get; set; }

    // The cleared level, where "none" has to be stated: a block clearance (lower + upper) can
    // revert to a single level, and if the null were left out the backend would keep the old
    // lower level and SectorTagHandoff.ApplyClearanceDataAsync would put that stale block on the
    // next controller's label. Both are sent as explicit nulls when the push read them off the
    // FDR (ClearanceKnown, a full FDR push) and left out when it did not (a radar-only update).
    [JsonProperty("cfl_lower", NullValueHandling = NullValueHandling.Include)] public int? CflLower { get; set; }
    [JsonProperty("cfl_upper", NullValueHandling = NullValueHandling.Include)] public int? CflUpper { get; set; }
    [JsonIgnore] public bool ClearanceKnown { get; set; }
    // Newtonsoft's conditional serialisation: ShouldSerialize + the property name.
    public bool ShouldSerializeCflLower() => ClearanceKnown;
    public bool ShouldSerializeCflUpper() => ClearanceKnown;

    // The rest of the tracking controller's label data, under the same rule: stated by the
    // controller tracking the tag (ClearanceKnown) and left out by anyone else. Empty text is sent
    // as "", so a cleared scratchpad reads as cleared.
    //
    //   label_op_data   the scratchpad (FDR.LabelOpData)
    //   global_op_data  global ops (FDR.GlobalOpData)
    //   c_prompt        the "C" on the label (Track.CPrompt), which is local to each vatSys client
    //
    // vatSys shares the scratchpad and global ops itself, but only from whoever is tracking the
    // tag - after a reconnect nobody is, and both come back empty.
    [JsonProperty("label_op_data")] public string? LabelOpData { get; set; }
    [JsonProperty("global_op_data")] public string? GlobalOpData { get; set; }
    [JsonProperty("c_prompt")] public bool? CPrompt { get; set; }
    public bool ShouldSerializeLabelOpData() => ClearanceKnown;
    public bool ShouldSerializeGlobalOpData() => ClearanceKnown;
    // Null when there was no track to read it from: left out, not stated as "off".
    public bool ShouldSerializeCPrompt() => ClearanceKnown && CPrompt != null;

    // Off-track deviations either side, in nm (FDR.OTDLeft / OTDRight, 0 for none). Local to each
    // vatSys client - nothing on the network carries them - so OzServer is the only way another
    // controller sees one. Same rule as the label above: stated only by the tag's owner.
    [JsonProperty("otd_left")] public int? OtdLeft { get; set; }
    [JsonProperty("otd_right")] public int? OtdRight { get; set; }
    // True once the controller tracking the tag has dropped it to nobody; false once somebody
    // tracks it again. A dropped tag is not picked up by the next controller to connect
    // (FdrResumeActivation) - the drop was a decision, not a gap. Left out when not known.
    [JsonProperty("dropped")] public bool? Dropped { get; set; }
    public bool ShouldSerializeDropped() => Dropped != null;

    public bool ShouldSerializeOtdLeft() => ClearanceKnown && OtdLeft != null;
    public bool ShouldSerializeOtdRight() => ClearanceKnown && OtdRight != null;
    [JsonProperty("assigned_ssr_code")] public int? AssignedSsrCode { get; set; }
    [JsonProperty("atd")] public DateTime? Atd { get; set; }
    [JsonProperty("etd")] public DateTime? Etd { get; set; }
    // The landing time: the estimate for the last point of the parsed route, as vatSys's own Arrival
    // List shows it. What the Arrival List window shows (ArrivalListWindow).
    [JsonProperty("eta")] public DateTime? Eta { get; set; }
    [JsonProperty("eet_minutes")] public int? EetMinutes { get; set; }
    [JsonProperty("tas")] public int? Tas { get; set; }
    [JsonProperty("text_only")] public bool? TextOnly { get; set; }
    [JsonProperty("receive_only")] public bool? ReceiveOnly { get; set; }
    [JsonProperty("remarks")] public string? Remarks { get; set; }

    [JsonProperty("lat")] public double? Lat { get; set; }
    [JsonProperty("lon")] public double? Lon { get; set; }
    [JsonProperty("altitude")] public int? Altitude { get; set; }
    [JsonProperty("ground_speed")] public int? GroundSpeed { get; set; }
    [JsonProperty("heading")] public int? Heading { get; set; }
    [JsonProperty("vertical_rate")] public int? VerticalRate { get; set; }
    [JsonProperty("on_ground")] public bool? OnGround { get; set; }

    // The sector the aircraft is physically inside (SectorLocator, resolved against every sector
    // in SectorsVolumes.Sectors, not only claimed ones) - a different question from who owns the
    // tag. Null when it is inside no known sector volume at its position and level, or when no
    // live or predicted position is available.
    [JsonProperty("current_sector")] public string? CurrentSector { get; set; }

    // vatSys's data authority for the FDR: fdr.ControllingSector, the sector whose controller may
    // edit an untracked FDR (FDR.HavePermission). For an activated FDR nobody tracks (still on the
    // ground, say), the plugin holding this sector is the one pushing it, and the backend uses it
    // to recognise that push - see FdrSync.ShouldPush.
    [JsonProperty("authority_sector")] public string? AuthoritySector { get; set; }

    // The callsign tracking the FDR in vatSys, whoever it is; empty when nobody is. For a
    // controller not on OzServer (a neighbouring FIR's, say) this is the only record of them:
    // controlling_cid/controlling_callsign only ever name an OzServer controller.
    [JsonProperty("tracked_by")] public string? TrackedBy { get; set; }

    // Whether vatSys has the FDR coupled to a radar track on the reporting client. The website
    // map shows an uncoupled FDR as a box (a flight plan track) rather than an aircraft symbol.
    [JsonProperty("coupled")] public bool? Coupled { get; set; }

    // The route as vatSys parsed it (fdr.ParsedRoute): every waypoint with its position, so the
    // map can show where the aircraft is going rather than guess from the route string. Sent only
    // when it differs from what this session last sent for the callsign, and on the periodic
    // sweep; left out, the backend keeps the last one. route_next_index is the index in
    // route_points of the next waypoint to be flown, sent on every full FDR push.
    [JsonProperty("route_points")] public List<OzServerRoutePointDto>? RoutePoints { get; set; }
    [JsonProperty("route_next_index")] public int? RouteNextIndex { get; set; }

    // The aerodromes' positions, from vatSys's airport data. The map shows an FDR that has not
    // departed at its departure aerodrome and a completed one at its destination; neither
    // necessarily has a live position.
    [JsonProperty("dep_lat")] public double? DepLat { get; set; }
    [JsonProperty("dep_lon")] public double? DepLon { get; set; }
    [JsonProperty("des_lat")] public double? DesLat { get; set; }
    [JsonProperty("des_lon")] public double? DesLon { get; set; }
}

// A stored flight record as /fdr/sync returns it: everything that was pushed, plus when the server
// last heard about it.
public class OzServerFdrRecordDto : OzServerFdrUpdateDto
{
    [JsonProperty("last_seen_at")] public DateTimeOffset? LastSeenAt { get; set; }
}

// One waypoint of OzServerFdrUpdateDto.RoutePoints. Short keys and 4dp (~10 m) positions: a long
// route is dozens of these, for every FDR a controller answers for.
public class OzServerRoutePointDto
{
    [JsonProperty("n")] public string Name { get; set; } = "";
    [JsonProperty("lat")] public double Lat { get; set; }
    [JsonProperty("lon")] public double Lon { get; set; }

    // Set only on a sector crossing (a vatSys ZPOINT - see FdrSync.FillRoute) and left out of an
    // ordinary waypoint:
    //   z   true: this is where the route enters a volume, not a waypoint on the route
    //   v   the volume entered (n is "Z" + this)
    //   s   the vatSys sector that volume belongs to (Sector.Name, as the handoff window shows it)
    //   sc  that sector's callsign
    // s and sc are absent for a volume no sector in the profile contains.
    [JsonProperty("z")] public bool Crossing { get; set; }
    [JsonProperty("v")] public string? Volume { get; set; }
    [JsonProperty("s")] public string? Sector { get; set; }
    [JsonProperty("sc")] public string? SectorCallsign { get; set; }
    public bool ShouldSerializeCrossing() => Crossing;
    public bool ShouldSerializeVolume() => Volume != null;
    public bool ShouldSerializeSector() => Sector != null;
    public bool ShouldSerializeSectorCallsign() => SectorCallsign != null;
}

// One airport's Arrival List (GET /arrivals/:ades, and the answer to a reorder) - see
// ArrivalListWindow. Flights are listed top of the window first: the unsequenced ones above the
// bar, then the sequence from its top down.
public class OzServerArrivalListDto
{
    [JsonProperty("ades")] public string Ades { get; set; } = "";
    [JsonProperty("flights")] public List<OzServerArrivalFlightDto> Flights { get; set; } = new();
    // How many controllers have this airport's list open, this one included.
    [JsonProperty("viewers")] public int Viewers { get; set; }
    // Their callsigns, for the hover on the window's Connected badge.
    [JsonProperty("viewer_callsigns")] public List<string> ViewerCallsigns { get; set; } = new();
    [JsonProperty("fmp")] public OzServerArrivalFmpDto Fmp { get; set; } = new();
    // Whether this controller may reorder the list: anyone while the FMP is offline, only the FMP
    // while it is online. The backend enforces it either way.
    [JsonProperty("can_move")] public bool CanMove { get; set; }
}

public class OzServerArrivalFmpDto
{
    // The flow position the airport's list belongs to: CB_FMP for YSCB.
    [JsonProperty("position")] public string Position { get; set; } = "";
    [JsonProperty("online")] public bool Online { get; set; }
    // The callsign it is online under (CB_FMP, or a relief CB_1_FMP); null while offline.
    [JsonProperty("callsign")] public string? Callsign { get; set; }
}

public class OzServerArrivalFlightDto
{
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
    // -1: not sequenced (above the bar). Otherwise 0 is the bottom of the sequence.
    [JsonProperty("seq_index")] public int SeqIndex { get; set; }
    // As FdrSync sent them: the landing time, type, wake and global ops.
    [JsonProperty("eta")] public DateTime? Eta { get; set; }
    [JsonProperty("aircraft_type")] public string? AircraftType { get; set; }
    [JsonProperty("aircraft_wake")] public string? AircraftWake { get; set; }
    [JsonProperty("global_op_data")] public string? GlobalOpData { get; set; }
}

public class OzServerArrivalViewersDto
{
    [JsonProperty("viewers")] public Dictionary<string, int> Viewers { get; set; } = new();
}

// POST /atis: one airport's current ATIS on this server, kept for the next controller on the
// station. Sent by AtisSync and BadVectorsAtisSync when the ATIS changes, and periodically while
// it is on the air.
public class OzServerAtisUpdateDto
{
    [JsonProperty("icao")] public string Icao { get; set; } = "";
    [JsonProperty("atis_letter")] public string AtisLetter { get; set; } = "";
    // Field name (WIND, VIS, QNH, ...) -> value, as in vatsys.ATIS.Content.
    [JsonProperty("content")] public Dictionary<string, string> Content { get; set; } = new();
    [JsonProperty("frequency")] public int? Frequency { get; set; }
}

// PUT /api/v1/vatsim/atis/{icao}: the broadcast itself, published as the controller makes it.
//
// Unlike OzServerAtisUpdateDto, the per-server handover record, this is the public record that
// the website map and readers of /api/v1/vatsim/atis use, so it carries the whole broadcast.
//
// It replaces the entry for its airport, and sending the same one twice is harmless, so a
// periodic re-send says "still on the air": the backend drops an ATIS not heard again within
// NETWORK_ATIS_RETAIN_MINUTES (90), and there is no way to withdraw one sooner.
public class OzServerAtisPublishDto
{
    [JsonProperty("icao")] public string Icao { get; set; } = "";
    // The ATIS station as connected - "YSSY_ATIS".
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
    [JsonProperty("atis_letter")] public string AtisLetter { get; set; } = "";
    // "atis" for a sectioned broadcast, "zulu" for the free text a closed tower leaves running.
    // Decides what Content may be; the backend refuses a mismatch.
    [JsonProperty("type")] public string Type { get; set; } = "atis";
    // "126.250", as displayed. Left out when the station has no frequency of its own.
    [JsonProperty("frequency")] public string? Frequency { get; set; }
    // ISO 8601 UTC. When the controller issued this letter, not when it was sent: re-publishing
    // an unchanged ATIS must not make it look newer than the broadcast is.
    [JsonProperty("issued")] public string Issued { get; set; } = "";
    // Section name (WIND, QNH, ...) -> value for an "atis"; the broadcast text, as a string, for
    // a "zulu". A section name the backend does not serve is dropped there rather than refused.
    [JsonProperty("content")] public object Content { get; set; } = new Dictionary<string, string>();
    // The sections this letter changed - those marked with a leading + in the ATIS text.
    [JsonProperty("changed")] public List<string> Changed { get; set; } = new();
    // Who is broadcasting it, and what published it. Stored but never served publicly, so a wrong
    // or stale ATIS can be traced to the session that sent it.
    [JsonProperty("controller")] public OzServerAtisControllerDto Controller { get; set; } = new();
    [JsonProperty("client")] public OzServerAtisClientDto? Client { get; set; }
}

public class OzServerAtisControllerDto
{
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
    [JsonProperty("cid")] public int Cid { get; set; }
}

public class OzServerAtisClientDto
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
}

// Client for the OzServer API. Every call attaches this session's CID, callsign and server
// (NetworkServer.Current: live, sb1 or sb2), which the API uses as the controller's identity
// without waiting for the VATSIM datafeed, and to keep each server's data apart. No credential is
// compiled into or sent by the plugin.
public class OzServerApiClient
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    // A shorter limit for the ownership poll. Every request is a heartbeat, presence on the
    // backend lapses after a few seconds without one, and the tracker runs one poll at a time -
    // so a poll left hanging for the full RequestTimeout would let a connected session read as
    // gone.
    static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(6);

    // Enough that background traffic (FDR batches, polls, ATIS) cannot occupy every slot and
    // leave a controller-initiated request waiting for one - see CreateClient.
    const int TargetConnectionLimit = 8;

    static readonly HttpClient Http = CreateClient();

    // Null properties are left out of a request body, recursively (inside UpdateFdrBatchAsync's
    // array of DTOs too), so a partial push cannot blank a value a fuller one set - see
    // OzServerFdrUpdateDto. A property opts back in with NullValueHandling.Include where null is
    // itself meaningful.
    static readonly JsonSerializer BodySerializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

    static HttpClient CreateClient()
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
            // Best effort.
        }

        // .NET Framework allows two connections per host by default, and every call in this
        // plugin shares the one HttpClient below, so one ServicePoint and one cap. With FDR
        // batches, polls and ATIS pushes in the background, a request the controller is waiting
        // on would queue behind them.
        //
        // Raised, never lowered: the property is process-wide, so it belongs to vatSys and other
        // plugins as much as to this one.
        try
        {
            if (ServicePointManager.DefaultConnectionLimit < TargetConnectionLimit)
                ServicePointManager.DefaultConnectionLimit = TargetConnectionLimit;
        }
        catch
        {
            // The cap only costs latency, never correctness.
        }

        // Well under HttpClient's default of 100 seconds, which would leave a controller with a
        // dead Sectors window and nothing in the error log. The timer-driven callers refuse to
        // stack up by themselves, so this only bounds how long one request can hang, with
        // headroom for a large FDR batch on a slow connection.
        var client = new HttpClient { Timeout = RequestTimeout };
        // Error bodies are parsed as JSON ({"message": "..."} - see ParseError), so JSON is asked
        // for explicitly.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public Task ClaimSectorAsync(string sectorName) => PostAsync($"/sectors/{Uri.EscapeDataString(sectorName)}/claim");

    // exclude leaves the named covered sectors out of the claim entirely - not taken, not
    // conflict-checked. Used to retry a claim that returned 409, carving out the sectors someone
    // else owns rather than failing the whole claim.
    //
    // Answers with the sector names the claim granted, which is not always every one it covers,
    // or even the one it names - see OzServerOwnershipTracker's _withheldUntil. Null when the body
    // is not a list of names, meaning "not known".
    //
    // The backend never takes a sector off a present controller, not even for that position's
    // own primary; it has to be requested.
    public async Task<IReadOnlyList<string>?> ClaimSectorAsync(string sectorName, IEnumerable<string> exclude)
    {
        var body = await PostRawAsync($"/sectors/{Uri.EscapeDataString(sectorName)}/claim", new
        {
            exclude = exclude.ToArray()
        }).ConfigureAwait(false);

        try
        {
            return JsonConvert.DeserializeObject<List<string>>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task ReleaseSectorAsync(string sectorName) => PostAsync($"/sectors/{Uri.EscapeDataString(sectorName)}/release");

    public Task<OzServerSectorOwnershipRequestDto> RequestSectorAsync(string sectorName) =>
        PostAsync<OzServerSectorOwnershipRequestDto>($"/sectors/{Uri.EscapeDataString(sectorName)}/request");

    public Task AcceptRequestAsync(int requestId) => PostAsync($"/sector-requests/{requestId}/accept");

    // Accepts several incoming requests in one call, processed in order server-side. Preferred
    // over a loop of AcceptRequestAsync: separate accepts sent back to back can overlap each
    // other's claim and refresh, and leave a request row behind after its sector has moved.
    public Task<OzServerAcceptBatchResponseDto> AcceptRequestsBatchAsync(IEnumerable<int> requestIds) =>
        PostAsync<OzServerAcceptBatchResponseDto>("/sector-requests/accept-batch", new { request_ids = requestIds.ToArray() });

    public Task<OzServerActionResultDto> RejectRequestAsync(int requestId) =>
        PostAsync<OzServerActionResultDto>($"/sector-requests/{requestId}/reject");

    // Declines a whole grouped request in one call, so a group is refused as the single decision
    // it is.
    public Task<OzServerRejectBatchResponseDto> RejectRequestsBatchAsync(IEnumerable<int> requestIds) =>
        PostAsync<OzServerRejectBatchResponseDto>("/sector-requests/reject-batch", new { request_ids = requestIds.ToArray() });

    public Task<OzServerActionResultDto> CancelRequestAsync(int requestId) =>
        PostAsync<OzServerActionResultDto>($"/sector-requests/{requestId}/cancel");

    // Confirms this controller has been shown a rejection of their own request, which is what
    // deletes it server-side. Until then the rejection keeps coming back in `by_me`, so it
    // survives the plugin being closed before the controller saw it.
    public Task AcknowledgeRejectionAsync(int requestId) =>
        PostAsync($"/sector-requests/{requestId}/acknowledge-rejection");

    // One Apply in one call, rather than a POST per staged sector.
    //
    // exclude applies to every sector in `claim`, for the reason the single-sector claim takes
    // one: a claim expands through the dataset's responsible sectors, and the parts of that
    // expansion somebody is logged on as are not this controller's to take. See
    // PrimaryPosition.StaffedCoveredSectors.
    public Task<OzServerCommitResponseDto> CommitAsync(
        IEnumerable<string> claim, IEnumerable<string> release, IEnumerable<string> request,
        IEnumerable<string> exclude) =>
        PostAsync<OzServerCommitResponseDto>("/sectors/commit", new
        {
            claim = claim.ToArray(),
            release = release.ToArray(),
            request = request.ToArray(),
            exclude = exclude.ToArray(),
        });

    // One round trip for owned, controlled and requests, which are always consumed together.
    public Task<OzServerSyncDto> GetSyncAsync() =>
        GetAsync("/sectors/sync", () => new OzServerSyncDto(), SyncTimeout);

    // Sectors other controllers own on OzServer (the backend excludes this CID). Taken from
    // OzServer's ownership records, not live VATSIM presence, so a position never claimed
    // through OzServer does not appear.
    public Task<List<OzServerControlledSectorDto>> GetControlledSectorsAsync() =>
        GetAsync<List<OzServerControlledSectorDto>>("/sectors/controlled", () => new List<OzServerControlledSectorDto>());

    // Gives up everything this controller owns in one call. Sent only on a deliberate disconnect
    // or when OzServer is disabled: silence is what tells the backend a disconnect was
    // ungraceful, and holds the sectors for the resume window.
    public Task ReleaseAllSectorsAsync() => PostAsync("/sectors/release-all");

    // Asks the backend to put this session back on what it held when it last left this position,
    // if that was recent enough. positionSectors is the logon position's own sectors
    // (PrimaryPosition.DefaultSectorsForCurrentSession): if a different controller left this
    // callsign moments ago, the backend hands over those of them they still held, with their tags.
    // The backend cannot work the position out itself - positions come from the profile's
    // Positions.xml, which it does not have.
    public Task<OzServerResumeResponseDto> ResumeAsync(IEnumerable<string> positionSectors) =>
        PostAsync<OzServerResumeResponseDto>("/sectors/resume", new { position_sectors = positionSectors.ToArray() });

    // Every flight FdrSync has pending in one request. Each flight succeeds or is refused
    // independently server-side; the per-flight results in the response are not read.
    public Task UpdateFdrBatchAsync(IEnumerable<OzServerFdrUpdateDto> flights) =>
        PostAsync("/fdr/batch", new { flights = flights.ToArray() });

    // Deletes a flight's record because its owning controller cancelled the FDR - see
    // FdrSync.ReportOwnerCancel. The backend refuses (403) anyone but the owner.
    public Task CancelFlightAsync(string callsign) =>
        PostAsync($"/fdr/{Uri.EscapeDataString(callsign)}/cancel");

    // One flight's server-side record. Accepting jurisdiction moves who is tracking a tag, not
    // vatSys's local FDR fields, so the clearance data (CFL in particular) a previous controller
    // entered is fetched and applied by hand - see SectorTagHandoff.ApplyClearanceDataAsync. Null
    // on a 404: no record yet is an ordinary case, not a failure.

    public async Task<OzServerFdrUpdateDto?> GetFlightAsync(string callsign)
    {
        try
        {
            return await GetAsync<OzServerFdrUpdateDto?>($"/fdr/{Uri.EscapeDataString(callsign)}", () => null);
        }
        catch (OzServerApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    // Every flight record this server holds, for FdrResumeActivation (which on reconnect
    // activates and positions the ones vatSys has not) and LabelStateMirror. The backend leaves
    // route_points, most of a record's size, out of this list; GetFlightAsync returns it.
    public Task<List<OzServerFdrRecordDto>> GetAllFlightsAsync() =>
        GetAsync<List<OzServerFdrRecordDto>>("/fdr/sync", () => new List<OzServerFdrRecordDto>());

    // The Arrival List window's data - see ArrivalListWindow.
    public Task<OzServerArrivalListDto> GetArrivalListAsync(string ades) =>
        GetAsync<OzServerArrivalListDto>($"/arrivals/{Uri.EscapeDataString(ades)}", () => new OzServerArrivalListDto { Ades = ades });

    // A flight dragged to a new place. The backend refuses (403) a flight another controller has,
    // on the same terms as an FDR update.
    public Task<OzServerArrivalListDto> SetArrivalSequenceAsync(string ades, string callsign, int seqIndex) =>
        PostAsync<OzServerArrivalListDto>($"/arrivals/{Uri.EscapeDataString(ades)}/sequence", new { callsign, seq_index = seqIndex });

    // Every Arrival List this controller has open. Answers how many controllers have each open.
    // Every controller this client's vatSys can see - see ExternalControl.
    public Task ReportNetworkControllersAsync(IEnumerable<OzServerNetworkControllerDto> controllers) =>
        PostAsync("/network/controllers", new { controllers = controllers.ToArray() });

    public Task<OzServerArrivalViewersDto> ReportArrivalListsOpenAsync(IEnumerable<string> airports) =>
        PostAsync<OzServerArrivalViewersDto>("/arrivals/viewing", new { airports = airports.ToArray() });

    // Console messages waiting for this controller - see DeveloperMessages. The backend records them
    // as delivered as it answers.
    public Task<List<OzServerDeveloperMessageDto>> GetMessagesAsync() =>
        GetAsync<List<OzServerDeveloperMessageDto>>("/messages", () => new List<OzServerDeveloperMessageDto>());

    public Task MarkMessageReadAsync(long id) => PostAsync($"/messages/{id}/read");

    public Task ReplyToMessageAsync(long id, string body) => PostAsync($"/messages/{id}/reply", new { body });

    // ClientLogForwarder swallows a failure here rather than reporting it - see its comments.
    public Task SendClientLogsAsync(IEnumerable<OzServerClientLogLineDto> lines) =>
        PostAsync("/client-logs", new { lines = lines.ToList() });

    // Upserts this ICAO's handover record - see OzServerAtisUpdateDto.
    public Task UpdateAtisAsync(OzServerAtisUpdateDto atis) => PostAsync("/atis", atis);

    // Publishes the broadcast itself (see OzServerAtisPublishDto). PUT rather than POST, and the
    // only call here that does not carry this session's controller_cid/controller_callsign/server:
    // the endpoint does not use the controller identity, and the ATIS names its controller in the
    // body.
    public Task PublishAtisAsync(OzServerAtisPublishDto atis) =>
        PutAsync($"/vatsim/atis/{Uri.EscapeDataString(atis.Icao)}", atis);

    async Task<T> GetAsync<T>(string path, Func<T> empty, TimeSpan? timeout = null)
    {
        if (!OzServerMode.RequestsAllowed)
            throw new OzServerApiException(OzServerMode.DisabledMessage);

        var (cid, callsign, server) = GetCredentials();
        var url = $"{OzServerSettings.BaseUrl}/api/v1{path}?controller_cid={cid}&controller_callsign={Uri.EscapeDataString(callsign)}&server={Uri.EscapeDataString(server)}";

        HttpResponseMessage response;
        try
        {
            if (timeout is { } limit)
            {
                // The client's own Timeout still applies; this only shortens it.
                using var cancel = new System.Threading.CancellationTokenSource(limit);
                response = await Http.GetAsync(url, cancel.Token).ConfigureAwait(false);
            }
            else
            {
                response = await Http.GetAsync(url).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var transportMessage = DescribeTransportFailure(ex, timeout ?? RequestTimeout);
            ActionLog.LogAttempt("GET", path, false, transportMessage);
            throw new OzServerApiException(transportMessage);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var (message, conflicts) = ParseError(body, (int)response.StatusCode);
                ActionLog.LogAttempt("GET", path, false, message);
                throw new OzServerApiException(message, (int)response.StatusCode, conflicts);
            }

            ActionLog.LogAttempt("GET", path, true);
            // An answer means the API is up: a restart announced earlier is over (ApiOutage).
            ApiOutage.End("API answered");
            return JsonConvert.DeserializeObject<T>(body) ?? empty();
        }
    }

    async Task PostAsync(string path) => await PostRawAsync(path).ConfigureAwait(false);

    async Task<T> PostAsync<T>(string path)
    {
        var body = await PostRawAsync(path).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<T>(body) ?? throw new OzServerApiException("Empty response from OzServer.");
    }

    async Task<T> PostAsync<T>(string path, object requestBody)
    {
        var body = await PostRawAsync(path, requestBody).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<T>(body) ?? throw new OzServerApiException("Empty response from OzServer.");
    }

    async Task PostAsync(string path, object body) => await PostRawAsync(path, body).ConfigureAwait(false);

    // The body's own fields are serialised first, then controller_cid, controller_callsign and
    // server are merged in, so no DTO has to carry the session's identity itself.
    async Task<string> PostRawAsync(string path, object? requestBody = null)
    {
        if (!OzServerMode.RequestsAllowed)
            throw new OzServerApiException(OzServerMode.DisabledMessage);

        var (cid, callsign, server) = GetCredentials();
        var json = requestBody != null ? JObject.FromObject(requestBody, BodySerializer) : new JObject();
        json["controller_cid"] = cid;
        json["controller_callsign"] = callsign;
        json["server"] = server;
        var payload = json.ToString(Formatting.None);

        HttpResponseMessage response;
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            response = await Http.PostAsync(OzServerSettings.BaseUrl + "/api/v1" + path, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var transportMessage = DescribeTransportFailure(ex);
            ActionLog.LogAttempt("POST", path, false, transportMessage);
            throw new OzServerApiException(transportMessage);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var (message, conflicts) = ParseError(responseBody, (int)response.StatusCode);
                ActionLog.LogAttempt("POST", path, false, message);
                throw new OzServerApiException(message, (int)response.StatusCode, conflicts);
            }

            ActionLog.LogAttempt("POST", path, true);
            // An answer means the API is up: a restart announced earlier is over (ApiOutage).
            ApiOutage.End("API answered");
            return responseBody;
        }
    }

    // Sent as given, with no identity merged in as PostRawAsync does: the one endpoint this serves
    // does not use it.
    async Task PutAsync(string path, object requestBody)
    {
        if (!OzServerMode.RequestsAllowed)
            throw new OzServerApiException(OzServerMode.DisabledMessage);

        var payload = JObject.FromObject(requestBody, BodySerializer).ToString(Formatting.None);

        HttpResponseMessage response;
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            response = await Http.PutAsync(OzServerSettings.BaseUrl + "/api/v1" + path, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var transportMessage = DescribeTransportFailure(ex);
            ActionLog.LogAttempt("PUT", path, false, transportMessage);
            throw new OzServerApiException(transportMessage);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var (message, _) = ParseError(responseBody, (int)response.StatusCode);
                ActionLog.LogAttempt("PUT", path, false, message);
                throw new OzServerApiException(message, (int)response.StatusCode);
            }

            ActionLog.LogAttempt("PUT", path, true);
            // An answer means the API is up: a restart announced earlier is over (ApiOutage).
            ApiOutage.End("API answered");
        }
    }

    // HttpClient reports a timeout as a TaskCanceledException whose Message is just "A task was
    // canceled.", so the real cause is named instead.
    static string DescribeTransportFailure(Exception ex, TimeSpan? timeout = null) =>
        ex is OperationCanceledException // TaskCanceledException, which is what a timeout raises, derives from this
            ? $"OzServer at {OzServerSettings.BaseUrl} didn't respond within {(timeout ?? RequestTimeout).TotalSeconds:0.#}s."
            : $"Couldn't reach OzServer at {OzServerSettings.BaseUrl}: {ex.Message}";

    static (string Message, List<OzServerSectorConflictDto> Conflicts) ParseError(string body, int statusCode)
    {
        try
        {
            var parsed = JsonConvert.DeserializeObject<OzServerErrorDto>(body);
            if (!string.IsNullOrEmpty(parsed?.Message))
                return (parsed!.Message, parsed.Conflicts ?? new List<OzServerSectorConflictDto>());
        }
        catch
        {
            // Not the {"message": "..."} shape. The generic message below keeps a snippet of the
            // body, so an unexpected one (an HTML error page, say) can be diagnosed.
        }

        var snippet = body.Length > 120 ? body.Substring(0, 120) + "…" : body;
        return ($"OzServer request failed (HTTP {statusCode}): {snippet}", new List<OzServerSectorConflictDto>());
    }

    static (int Cid, string Callsign, string Server) GetCredentials()
    {
        var identity = NetworkIdentity.Current
            ?? throw new OzServerApiException("Not connected to VATSIM under a callsign yet.");
        // The server is named when there is one: on LocalHost or an unrecognised SweatBox, "not
        // connected" would contradict what the controller sees.
        var server = NetworkServer.Current
            ?? throw new OzServerApiException(string.IsNullOrEmpty(Network.ServerIP)
                ? "Not connected to VATSIM under a callsign yet."
                : $"OzServer isn't available on this server ({Network.ServerIP}) - only live VATSIM and the VATPAC SweatBoxes are supported.");
        return (identity.Cid, identity.Callsign, server);
    }
}
