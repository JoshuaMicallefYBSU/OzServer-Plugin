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

// Thrown for both transport failures (server unreachable) and API-reported failures (4xx/5xx with
// a {"message": "..."} body) - callers only need Message, which is already the right thing to show
// the controller either way. Conflicts is only ever populated for a claim's 409 response
// (SectorOwnershipController::claim) - the specific covered sub-sector(s) already owned by someone
// else, each with its own owner, so a caller can decide what to do about each one individually
// (see OzServerOwnershipTracker.HandleConflictAsync) rather than the whole claim just failing.
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

public class OzServerSectorOwnershipRequestDto
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("sector_id")] public int SectorId { get; set; }
    // Every sector requested by one Apply carries the same group id, so the receiving controller is
    // asked once about the whole thing instead of once per sector. Requests made before the backend
    // had groups each carry their own, which is correct - they really were independent.
    [JsonProperty("group_id")] public string GroupId { get; set; } = "";
    [JsonProperty("requesting_cid")] public int RequestingCid { get; set; }
    [JsonProperty("requesting_callsign")] public string RequestingCallsign { get; set; } = "";
    [JsonProperty("target_cid")] public int TargetCid { get; set; }
    [JsonProperty("target_callsign")] public string TargetCallsign { get; set; } = "";
    // Only populated by GET /sector-requests, not by POST .../request's own response.
    [JsonProperty("sector")] public OzServerSectorDto? Sector { get; set; }
    // Set once the owner has denied this request. A rejected request is kept server-side purely so
    // the controller who asked can be told (SectorOwnershipController::reject) - nothing else would
    // ever tell them, since the row otherwise just disappears from their list exactly as it does on
    // an accept, a cancel or a stale prune. Null on every request still awaiting a decision, and
    // only ever seen in `by_me`: `from_me` is filtered to pending server-side.
    [JsonProperty("rejected_at")] public DateTimeOffset? RejectedAt { get; set; }
}

public class OzServerMyRequestsDto
{
    [JsonProperty("by_me")] public List<OzServerSectorOwnershipRequestDto> ByMe { get; set; } = new();
    [JsonProperty("from_me")] public List<OzServerSectorOwnershipRequestDto> FromMe { get; set; } = new();
}

// Everything the Sectors window's poll needs, in one response - see SectorOwnershipController::sync.
// The three members are exactly the payloads GET /sectors/mine, /sectors/controlled and
// /sector-requests return individually, so they deserialise into the same DTOs and nothing
// downstream has to care which route the data arrived by.
public class OzServerSyncDto
{
    [JsonProperty("mine")] public List<OzServerSectorDto> Mine { get; set; } = new();
    [JsonProperty("controlled")] public List<OzServerControlledSectorDto> Controlled { get; set; } = new();
    [JsonProperty("requests")] public OzServerMyRequestsDto Requests { get; set; } = new();
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

// What POST /sectors/commit reports back: which names actually moved, alongside the resulting state.
// Mirrors SectorCommitResult so the window can report a partial outcome without re-deriving it.
public class OzServerCommitResultDto
{
    [JsonProperty("claimed")] public List<string> Claimed { get; set; } = new();
    [JsonProperty("released")] public List<string> Released { get; set; } = new();
    [JsonProperty("requested")] public List<string> Requested { get; set; } = new();
    [JsonProperty("skipped")] public List<string> Skipped { get; set; } = new();
    [JsonProperty("failed")] public List<string> Failed { get; set; } = new();
}

// What POST /sectors/resume answers with: which sectors were actually recovered, plus the resulting
// state. Anything not listed was taken by someone else while this controller was away.
public class OzServerResumeResponseDto
{
    [JsonProperty("resumed")] public List<string> Resumed { get; set; } = new();
    // Tags the backend actually handed back - flights this controller was working before they
    // dropped, minus any another controller picked up while they were away (the backend refuses to
    // take those). These come straight back to the controller rather than flashing for acceptance;
    // see TagOwnershipSync.OnTagsResumed.
    [JsonProperty("flights")] public List<string> Flights { get; set; } = new();
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

public class OzServerCommitResponseDto
{
    [JsonProperty("result")] public OzServerCommitResultDto Result { get; set; } = new();
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

// What every ownership-changing action answers with: the resulting state, in the same shape
// GET /sectors/sync returns. Saves the follow-up GET each of these used to require.
public class OzServerActionResultDto
{
    [JsonProperty("sync")] public OzServerSyncDto? Sync { get; set; }
}

public class OzServerControlledSectorOwnerDto
{
    [JsonProperty("cid")] public int Cid { get; set; }
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
}

// One row per sector GET /sectors/controlled returns - the backend already expands a claimed
// grouping sector (e.g. TBD) into one row per covered sector (TBD, AUG, AAE, ...) with the same
// owner, so this is rendered as a flat, already-expanded list rather than something that needs
// its own client-side recursion the way Available/Owned do.
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

// Mirrors UpdateFlightDataRecordRequest's validation rules (app/Http/Requests on the backend) field
// for field. controlling_cid/controlling_callsign are the flight's datalink authority, deliberately
// separate from this session's own identity (attached automatically by PostRawAsync, same as every
// other endpoint) even though, now, they're always this session's own cid/callsign: FdrSync only
// ever pushes while fdr.IsTrackedByMe is true (see FdrSync.ShouldPush) - a flight merely observed,
// handed off elsewhere, or not yet activated is never pushed for at all, so there's no "someone
// else's callsign" or "free/null" authority left to report. Every field besides Callsign is
// nullable so a partial or position-only push doesn't have to fabricate values for whatever FdrSync
// couldn't read off the FDR.
public class OzServerFdrUpdateDto
{
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";

    // Always sent, even when null - the two fields where null is itself meaningful ("free", no
    // authority) rather than "this particular push doesn't know". Every other field below is
    // omitted from the request entirely when null (see PostRawAsync's NullValueHandling.Ignore
    // default) rather than sent as an explicit null, so a partial push - a position-only radar
    // ping that never touched aircraft_count, say - can't blank out a column a fuller push already
    // set, and can't violate a NOT NULL column (aircraft_count has no default nullability) by
    // sending null for something it simply doesn't have an answer for yet.
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
    [JsonProperty("cfl_lower")] public int? CflLower { get; set; }
    [JsonProperty("cfl_upper")] public int? CflUpper { get; set; }
    [JsonProperty("assigned_ssr_code")] public int? AssignedSsrCode { get; set; }
    [JsonProperty("atd")] public DateTime? Atd { get; set; }
    [JsonProperty("etd")] public DateTime? Etd { get; set; }
    [JsonProperty("eet_minutes")] public int? EetMinutes { get; set; }
    [JsonProperty("tas")] public int? Tas { get; set; }
    [JsonProperty("text_only")] public bool? TextOnly { get; set; }
    [JsonProperty("receive_only")] public bool? ReceiveOnly { get; set; }
    [JsonProperty("label_op_data")] public string? LabelOpData { get; set; }
    [JsonProperty("remarks")] public string? Remarks { get; set; }

    [JsonProperty("lat")] public double? Lat { get; set; }
    [JsonProperty("lon")] public double? Lon { get; set; }
    [JsonProperty("altitude")] public int? Altitude { get; set; }
    [JsonProperty("ground_speed")] public int? GroundSpeed { get; set; }
    [JsonProperty("heading")] public int? Heading { get; set; }
    [JsonProperty("vertical_rate")] public int? VerticalRate { get; set; }
    [JsonProperty("on_ground")] public bool? OnGround { get; set; }

    // The geographic subsector this aircraft is physically inside of right now (SectorLocator,
    // resolved against the full SectorsVolumes.Sectors list - not tracker.ClaimedSectors), which is
    // deliberately a different question from who owns the tag (controlling_cid/controlling_callsign,
    // above). Null when the aircraft isn't inside any known sector volume at its current
    // position/level, or when neither a live nor a predicted position is available yet.
    [JsonProperty("current_sector")] public string? CurrentSector { get; set; }
}

// One row from GET /fdr/sync (FlightDataRecordController::sync) - OzServer's own live copy of a
// flight, for FdrActivationSync to compare against this client's local FDP2.FDR. Same field set as
// OzServerFdrUpdateDto (same table) plus the three fields that DTO never needs to send back up:
// State/Callsign (needed here just to identify and compare rows) and LastSeenAt (not currently
// used - the endpoint itself only ever returns live rows, see its own comment - but kept so a
// future caller doesn't have to touch the DTO to get at it).
public class OzServerFdrRecordDto
{
    [JsonProperty("callsign")] public string Callsign { get; set; } = "";
    // Who OzServer says is working this flight. GET /fdr/sync has always returned these; nothing
    // mapped them, so the plugin had no way to tell a tag another controller holds from a free one.
    // vatSys keeps jurisdiction per client with no cross-controller sync - the whole reason this
    // backend exists - so fdr.IsTracked only ever describes this session.
    [JsonProperty("controlling_cid")] public int? ControllingCid { get; set; }
    [JsonProperty("controlling_callsign")] public string? ControllingCallsign { get; set; }
    [JsonProperty("state")] public string? State { get; set; }
    [JsonProperty("flight_rules")] public string? FlightRules { get; set; }
    [JsonProperty("aircraft_type")] public string? AircraftType { get; set; }
    [JsonProperty("aircraft_equip")] public string? AircraftEquip { get; set; }
    [JsonProperty("aircraft_surv_equip")] public string? AircraftSurvEquip { get; set; }
    [JsonProperty("aircraft_count")] public int? AircraftCount { get; set; }
    [JsonProperty("dep_airport")] public string? DepAirport { get; set; }
    [JsonProperty("des_airport")] public string? DesAirport { get; set; }
    [JsonProperty("route")] public string? Route { get; set; }
    [JsonProperty("rfl")] public int? Rfl { get; set; }
    [JsonProperty("cfl_lower")] public int? CflLower { get; set; }
    [JsonProperty("cfl_upper")] public int? CflUpper { get; set; }
    [JsonProperty("assigned_ssr_code")] public int? AssignedSsrCode { get; set; }
    [JsonProperty("atd")] public DateTime? Atd { get; set; }
    [JsonProperty("etd")] public DateTime? Etd { get; set; }
    [JsonProperty("eet_minutes")] public int? EetMinutes { get; set; }
    [JsonProperty("tas")] public int? Tas { get; set; }
    [JsonProperty("label_op_data")] public string? LabelOpData { get; set; }
    [JsonProperty("remarks")] public string? Remarks { get; set; }
    [JsonProperty("last_seen_at")] public DateTimeOffset? LastSeenAt { get; set; }
}

// AtisController::update (backend) - upserts one airport's current ATIS. Sent by AtisSync only when
// vatsys.ATIS's own content/letter actually changes (there is deliberately no periodic heartbeat -
// see AtisSync's own comment), so this always represents a real broadcast update, not a liveness
// ping.
public class OzServerAtisUpdateDto
{
    [JsonProperty("icao")] public string Icao { get; set; } = "";
    [JsonProperty("atis_letter")] public string AtisLetter { get; set; } = "";
    // Field name (WIND, VIS, QNH, ...) -> value, straight from vatsys.ATIS.Content.
    [JsonProperty("content")] public Dictionary<string, string> Content { get; set; } = new();
    [JsonProperty("frequency")] public int? Frequency { get; set; }
}

// Talks to the OzServer backend's sector-ownership API. Every call attaches this vatSys session's
// CID and callsign, which the API uses as the controller identity without waiting for the lagging
// VATSIM datafeed. No shared credential is compiled into or sent by the plugin.
public class OzServerApiClient
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    // Enough that the background traffic (FDR batch, two polls, ATIS) can never occupy every slot
    // and leave a controller-initiated request waiting for one - see CreateClient.
    const int TargetConnectionLimit = 8;

    static readonly HttpClient Http = CreateClient();

    // Null properties are left out of a POST body entirely by default (recursively, so this
    // applies inside e.g. UpdateFdrBatchAsync's array of DTOs too) rather than sent as an explicit
    // null - see OzServerFdrUpdateDto's own comment for why a partial push needs that (a
    // position-only ping doesn't have an aircraft_count, and mustn't blank out one a fuller push
    // already set, or violate that column's own NOT NULL constraint). A property can still opt back
    // in with [JsonProperty(NullValueHandling = NullValueHandling.Include)] when null is itself
    // meaningful for that field.
    static readonly JsonSerializer BodySerializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

    static HttpClient CreateClient()
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
            // Best-effort - older Windows builds may not define Tls12 in this enum at all.
        }

        // .NET Framework caps outbound connections per host at 2 by default (the old RFC 2616
        // recommendation), and every call in this plugin shares the one HttpClient below - so it
        // shares one ServicePoint and that one cap. FdrSync flushes a batch every 5s, the ownership
        // tracker polls every 10s, the Sectors window polls requests on its own 10s timer, and ATIS
        // pushes on change: between them the two slots are routinely busy, and anything the
        // controller actually *waits* on queues behind them. That is what made pressing Controlled
        // take seconds - the GET wasn't slow, it hadn't started yet, stuck behind an FDR batch
        // upload on a connection that wouldn't free up.
        //
        // Raised, never lowered: this property is process-wide, so it belongs to vatSys as much as
        // to this plugin, and clamping down something the host (or another plugin) had deliberately
        // raised would be the same bug in reverse.
        try
        {
            if (ServicePointManager.DefaultConnectionLimit < TargetConnectionLimit)
                ServicePointManager.DefaultConnectionLimit = TargetConnectionLimit;
        }
        catch
        {
            // Not worth failing construction over - the cap only costs latency, never correctness.
        }

        // HttpClient's own default is 100 seconds - long enough that a backend which accepts a
        // connection and then stops answering leaves a controller staring at a dead Sectors window
        // for over a minute with nothing in the error log. Not set anywhere near the 5s/10s timer
        // intervals, though: those callers already refuse to stack up on their own (FdrSync's
        // _flushing flag, the tracker's RefreshFromServerIfIdleAsync), so this only has to bound
        // how long any single request can hang, and needs enough headroom for a genuinely large
        // FDR batch on a slow connection.
        var client = new HttpClient { Timeout = RequestTimeout };
        // Without this, Laravel's exception handler can't tell this apart from a browser
        // navigation and falls back to rendering its HTML error page instead of the
        // {"message": "..."} JSON body every error path here is written to expect - confirmed by
        // hitting the live API directly: identical request, text/html without this header,
        // application/json with it.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public Task ClaimSectorAsync(string sectorName) => PostAsync($"/sectors/{Uri.EscapeDataString(sectorName)}/claim");

    // exclude leaves the named covered sub-sector(s) out of the claim entirely (not touched, not
    // conflict-checked) - see SectorOwnershipController::claim. Used to retry a claim that 409'd,
    // carving out whichever sub-sectors turned out to already be owned by someone else rather than
    // failing the whole thing.
    public Task ClaimSectorAsync(string sectorName, IEnumerable<string> exclude) =>
        PostAsync($"/sectors/{Uri.EscapeDataString(sectorName)}/claim", new { exclude = exclude.ToArray() });

    public Task ReleaseSectorAsync(string sectorName) => PostAsync($"/sectors/{Uri.EscapeDataString(sectorName)}/release");

    public Task<OzServerSectorOwnershipRequestDto> RequestSectorAsync(string sectorName) =>
        PostAsync<OzServerSectorOwnershipRequestDto>($"/sectors/{Uri.EscapeDataString(sectorName)}/request");

    public Task AcceptRequestAsync(int requestId) => PostAsync($"/sector-requests/{requestId}/accept");

    // SectorOwnershipController::acceptBatch - accepts several incoming requests in one call,
    // processed sequentially server-side. Prefer this over calling AcceptRequestAsync in a loop for
    // more than one request: firing separate accept calls back-to-back left a window where each
    // one's own claim/refresh cascade could still be in flight when the next landed, occasionally
    // leaving a request row undeleted even though its sector's authority had already moved on.
    public Task<OzServerAcceptBatchResponseDto> AcceptRequestsBatchAsync(IEnumerable<int> requestIds) =>
        PostAsync<OzServerAcceptBatchResponseDto>("/sector-requests/accept-batch", new { request_ids = requestIds.ToArray() });

    public Task<OzServerActionResultDto> RejectRequestAsync(int requestId) =>
        PostAsync<OzServerActionResultDto>($"/sector-requests/{requestId}/reject");

    // Mirrors AcceptRequestsBatchAsync: declines a whole grouped request in one call rather than
    // one per sector, so a group is accepted or refused as the single decision it is.
    public Task<OzServerRejectBatchResponseDto> RejectRequestsBatchAsync(IEnumerable<int> requestIds) =>
        PostAsync<OzServerRejectBatchResponseDto>("/sector-requests/reject-batch", new { request_ids = requestIds.ToArray() });

    public Task<OzServerActionResultDto> CancelRequestAsync(int requestId) =>
        PostAsync<OzServerActionResultDto>($"/sector-requests/{requestId}/cancel");

    // Confirms this controller has been shown a rejection of their own request, which is what
    // finally deletes it server-side - see SectorOwnershipController::acknowledgeRejection. Until
    // this is called the rejection keeps coming back in every `by_me`, so it survives the plugin
    // being closed before the controller ever saw it.
    public Task AcknowledgeRejectionAsync(int requestId) =>
        PostAsync($"/sector-requests/{requestId}/acknowledge-rejection");

    // One round trip for owned + controlled + requests. The Sectors window polls every two seconds
    // while open and used to make three separate GETs per tick, each paying the framework's own
    // per-request cost for data that is always consumed together.
    // One Apply in one call. Sending a POST per sector meant applying three staged sectors paid full
    // latency four times over, with the lists frozen until the last one returned.
    public Task<OzServerCommitResponseDto> CommitAsync(
        IEnumerable<string> claim, IEnumerable<string> release, IEnumerable<string> request) =>
        PostAsync<OzServerCommitResponseDto>("/sectors/commit", new
        {
            claim = claim.ToArray(),
            release = release.ToArray(),
            request = request.ToArray(),
        });

    public Task<OzServerSyncDto> GetSyncAsync() =>
        GetAsync("/sectors/sync", () => new OzServerSyncDto());

    public Task<OzServerMyRequestsDto> GetMyRequestsAsync() =>
        GetAsync<OzServerMyRequestsDto>("/sector-requests", () => new OzServerMyRequestsDto());

    // Sectors someone *else* currently owns on OzServer (the backend excludes my own CID) - this
    // is "Controlled" mode's actual data source, not live VATSIM presence (see
    // SectorOwnershipController::controlled - it's scoped to sector_ownerships rows, so a sector
    // OzServer's DB doesn't even have a row for, like a stray callsign someone's logged into that
    // was never claimed through here, correctly never shows up).
    public Task<List<OzServerControlledSectorDto>> GetControlledSectorsAsync() =>
        GetAsync<List<OzServerControlledSectorDto>>("/sectors/controlled", () => new List<OzServerControlledSectorDto>());

    // Gives up everything this controller owns in one call - see
    // SectorOwnershipController::releaseAll. Only ever sent on a *graceful* disconnect: staying
    // silent is what tells the backend a disconnect was ungraceful, and buys the 5-minute window to
    // reconnect into the same sectors.
    public Task ReleaseAllSectorsAsync() => PostAsync("/sectors/release-all");

    // Asks the backend to put this session back on whatever it was holding when it last left this
    // same position, if that was recent enough - see SectorOwnershipController::resume.
    public Task<OzServerResumeResponseDto> ResumeAsync() =>
        PostAsync<OzServerResumeResponseDto>("/sectors/resume");

    // The authoritative "what do I actually own" check (SectorOwnershipController::mine) - used to
    // refresh Owned from OzServer's own record every time the Sectors window opens, rather than
    // trust locally-accumulated state that can drift (a claim from a previous vatSys session, an
    // ownership released some other way, a claim call that silently failed, ...).
    public Task<List<OzServerSectorDto>> GetMySectorsAsync() =>
        GetAsync<List<OzServerSectorDto>>("/sectors/mine", () => new List<OzServerSectorDto>());

    // FlightDataRecordController::update - upserts one flight, keyed by callsign. Handles both a
    // full FDR push and a lighter, position-only ping (FdrSync decides which fields to fill in on
    // the DTO), same endpoint either way.
    public Task UpdateFdrAsync(OzServerFdrUpdateDto fdr) => PostAsync("/fdr", fdr);

    // FlightDataRecordController::batchUpdate - every flight FdrSync has pending in one request,
    // rather than one HTTP call per aircraft. Each flight succeeds or is blocked independently
    // server-side (its own datalink authority being online with sectors doesn't stop any other
    // flight in the same batch) - callers that need per-flight results should inspect the response
    // themselves; FdrSync currently just fires this and moves on.
    public Task UpdateFdrBatchAsync(IEnumerable<OzServerFdrUpdateDto> flights) =>
        PostAsync("/fdr/batch", new { flights = flights.ToArray() });

    // FlightDataRecordController::sync - every FDR row OzServer currently holds, for
    // FdrActivationSync's own comparison against local FDP2.FDR state.
    public Task<List<OzServerFdrRecordDto>> GetFdrSyncAsync() =>
        GetAsync<List<OzServerFdrRecordDto>>("/fdr/sync", () => new List<OzServerFdrRecordDto>());

    // AtisController::update - upserts this ICAO's current ATIS state, keyed by icao.
    public Task UpdateAtisAsync(OzServerAtisUpdateDto atis) => PostAsync("/atis", atis);

    async Task<T> GetAsync<T>(string path, Func<T> empty)
    {
        var (cid, callsign) = GetCredentials();
        var url = $"{OzServerSettings.BaseUrl}/api/v1{path}?controller_cid={cid}&controller_callsign={Uri.EscapeDataString(callsign)}";

        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var transportMessage = DescribeTransportFailure(ex);
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

    // body's own fields (if any) are serialized first, then controller_cid/controller_callsign are
    // merged in on top via JObject rather than requiring every DTO to carry those two fields
    // itself - every endpoint attaches this session's own identity the same way regardless of what,
    // if anything, else is in the request.
    async Task PostAsync(string path, object body) => await PostRawAsync(path, body).ConfigureAwait(false);

    async Task<string> PostRawAsync(string path, object? requestBody = null)
    {
        var (cid, callsign) = GetCredentials();
        var json = requestBody != null ? JObject.FromObject(requestBody, BodySerializer) : new JObject();
        json["controller_cid"] = cid;
        json["controller_callsign"] = callsign;
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
            return responseBody;
        }
    }

    // HttpClient reports its own Timeout as a bare TaskCanceledException whose Message is just
    // "A task was canceled." - indistinguishable, to anyone reading the vatSys error log, from a
    // cancellation this plugin asked for (it never does). Name the real cause instead.
    static string DescribeTransportFailure(Exception ex) =>
        ex is OperationCanceledException // TaskCanceledException, which is what a timeout raises, derives from this
            ? $"OzServer at {OzServerSettings.BaseUrl} didn't respond within {RequestTimeout.TotalSeconds:0.#}s."
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
            // Not the {"message": "..."} shape every endpoint here is documented to return -
            // fall through to the generic message below, but keep enough detail that a body
            // shaped unexpectedly (like an HTML error page) is diagnosable instead of just
            // silently becoming "request failed" again.
        }

        var snippet = body.Length > 120 ? body.Substring(0, 120) + "…" : body;
        return ($"OzServer request failed (HTTP {statusCode}): {snippet}", new List<OzServerSectorConflictDto>());
    }

    static (int Cid, string Callsign) GetCredentials() =>
        NetworkIdentity.Current
        ?? throw new OzServerApiException("Not connected to VATSIM under a callsign yet.");
}
