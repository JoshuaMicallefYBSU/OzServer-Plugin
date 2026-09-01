using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Pulls OzServer's own copy of each flight back down and corrects a local FDR that's stuck at
// STATE_PREACTIVE when the server already has it further along - the counterpart to FdrSync's own
// push direction (see its class comment). Needed because vatSys keeps FDP2.FDR state purely
// per-client - there is no built-in cross-controller sync at all, which is the whole reason
// OzServer's /fdr endpoints exist - and resets every online pilot's flight plan back to
// STATE_PREACTIVE on reconnect (see the pilot-online check in FDP2.cs), with nothing else in
// vatSys or this plugin ever re-activating it afterwards: TagOwnershipSync only activates a flight
// that is physically sitting, right now, inside a subsector *this* controller currently owns on
// OzServer (see its own class comment) - a different, narrower condition than "OzServer already
// has real data for this flight from whoever last worked it".
//
// Deliberately scoped to exactly STATE_PREACTIVE, not "anything not yet ESTed": STATE_INACTIVE
// means no pilot is online yet, which vatSys itself already manages on its own (the same
// pilot-online check above) and isn't this class's business to second-guess.
//
// Only ever raises a flight's data and state to match the server - never its jurisdiction or
// tracking. That split of responsibility belongs entirely to TagOwnershipSync (see its own class
// comment) and is left completely alone here; this class's whole job stops at getting a stuck tag
// out of STATE_PREACTIVE with correct flight-plan data, same as EstFDR's own native "Activate"
// menu item would if a controller had noticed and clicked it by hand.
//
// Also doubles as the plugin's one place that knows which callsigns OzServer currently has an FDR
// row for at all (IsKnownToServer) - TagOwnershipSync's own airborne/near-boundary pre-activation
// trigger reads this to skip a tag OzServer has never seen, rather than reaching out to the API
// itself on every FDR/radar update just to ask the same question this class is already asking
// every poll tick.
public class FdrActivationSync
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _pollTimer;

    // Guards _knownCallsigns - written from the poll timer thread, read from TagOwnershipSync's own
    // event-driven callbacks, which can arrive on a different thread again.
    readonly object _knownLock = new();
    HashSet<string> _knownCallsigns = new(StringComparer.OrdinalIgnoreCase);

    // Every FDR push by any controller publishes an "fdr" signal, and FdrSync pushes every 5s per
    // controller - so across a busy sector group these arrive many times a second. Pulling the
    // whole /fdr/sync set on each one would be quadratic in connected controllers, turning a
    // latency fix into a load problem. Coalesce to at most one pull per interval, which still
    // lands an order of magnitude inside the 10s poll it replaces.
    static readonly TimeSpan MinPushInterval = TimeSpan.FromSeconds(1);

    readonly object _pushLock = new();
    DateTime _lastPollStartedUtc = DateTime.MinValue;
    bool _pushScheduled;

    public FdrActivationSync()
    {
        _pollTimer = new System.Threading.Timer(_ => _ = PollAsync(), null, PollInterval, PollInterval);
        // The poll timer above is kept as the fallback: if the stream is down, this class behaves
        // exactly as it did before. See OzServerEventStream.
        OzServerEventStream.Shared.EventReceived += OnServerEvent;
    }

    void OnServerEvent(string name)
    {
        if (name != "fdr" || !Network.IsConnected)
            return;

        TimeSpan delay;

        lock (_pushLock)
        {
            // A pull is already queued for this burst - every further signal in it is answered by
            // that same pull, since /fdr/sync always returns the whole current set anyway.
            if (_pushScheduled)
                return;

            var since = DateTime.UtcNow - _lastPollStartedUtc;
            if (since >= MinPushInterval)
            {
                _ = PollAsync();
                return;
            }

            _pushScheduled = true;
            delay = MinPushInterval - since;
        }

        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            lock (_pushLock)
                _pushScheduled = false;

            _ = PollAsync();
        });
    }

    public bool IsKnownToServer(string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return false;

        lock (_knownLock)
            return _knownCallsigns.Contains(callsign);
    }

    async Task PollAsync()
    {
        if (!Network.IsConnected)
            return;

        // Recorded here rather than at each call site so the 10s timer and a push both feed the
        // same coalescing window - a push moments after a timer tick is redundant either way.
        lock (_pushLock)
            _lastPollStartedUtc = DateTime.UtcNow;

        List<OzServerFdrRecordDto> records;
        try
        {
            records = await _api.GetFdrSyncAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't pull FDR state from OzServer: {ex.Message}", ex), "OzServer");
            return;
        }

        // Always refreshed, not gated on there being a local STATE_PREACTIVE candidate below (this
        // used to skip the whole fetch when there wasn't one) - IsKnownToServer needs a current
        // answer on every call, independent of whatever this class's own reconciliation loop is
        // doing this tick.
        lock (_knownLock)
        {
            _knownCallsigns = new HashSet<string>(
                records.Select(r => r.Callsign).Where(c => !string.IsNullOrEmpty(c)),
                StringComparer.OrdinalIgnoreCase);
        }

        var candidates = FDP2.GetFDRs
            .Where(f => f.State == FDP2.FDR.FDRStates.STATE_PREACTIVE && !string.IsNullOrEmpty(f.Callsign))
            .ToList();
        if (candidates.Count == 0)
            return;

        var byCallsign = records
            .Where(r => !string.IsNullOrEmpty(r.Callsign))
            .GroupBy(r => r.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var fdr in candidates)
        {
            if (!byCallsign.TryGetValue(fdr.Callsign, out var record))
                continue;

            // The server counts as "further along" the same way ESTed does locally - a state past
            // STATE_PREACTIVE. A state OzServer doesn't recognise (a future FDRStates value this
            // build doesn't know about yet) is treated as no signal rather than guessed at.
            if (!Enum.TryParse(record.State, out FDP2.FDR.FDRStates serverState)
                || serverState <= FDP2.FDR.FDRStates.STATE_PREACTIVE)
                continue;

            var captured = record;
            RunOnUiThread(() => ActivateFromServer(fdr, captured));
        }
    }

    // Runs on the UI thread - every property set here touches live FDP2/vatSys tracking state, same
    // requirement TagOwnershipSync/OzServerOwnershipTracker already document for their own writes.
    static void ActivateFromServer(FDP2.FDR fdr, OzServerFdrRecordDto record)
    {
        // Re-checked here (already checked once in PollAsync) - this callback can run some time
        // after it was posted, and something else (a controller's own manual Activate,
        // TagOwnershipSync's own pickup, a second poll tick that got there first) may have already
        // moved this flight on in the meantime.
        if (fdr.State != FDP2.FDR.FDRStates.STATE_PREACTIVE)
            return;

        // Base flight-plan data first, and in this order: FDP2.FDR.Route's setter parses the new
        // route against whatever DepAirport/DesAirport/Remarks are *already* set on the FDR (see
        // FDP2.cs), and MMI.EstFDR below silently does nothing at all if that leaves ParsedRoute
        // empty (see FDP2.EstFDR) - so every field the route parse depends on has to land before
        // Route itself does, and Route has to land before EstFDR is called.
        if (!string.IsNullOrEmpty(record.FlightRules)) fdr.FlightRules = record.FlightRules;
        if (!string.IsNullOrEmpty(record.AircraftType)) fdr.AircraftType = record.AircraftType;
        if (!string.IsNullOrEmpty(record.AircraftEquip)) fdr.AircraftEquip = record.AircraftEquip;
        if (!string.IsNullOrEmpty(record.AircraftSurvEquip)) fdr.AircraftSurvEquip = record.AircraftSurvEquip;
        if (record.AircraftCount is > 0) fdr.AircraftCount = record.AircraftCount.Value;
        if (!string.IsNullOrEmpty(record.DepAirport)) fdr.DepAirport = record.DepAirport;
        if (!string.IsNullOrEmpty(record.DesAirport)) fdr.DesAirport = record.DesAirport;
        if (!string.IsNullOrEmpty(record.Remarks)) fdr.Remarks = record.Remarks;
        if (!string.IsNullOrEmpty(record.LabelOpData)) fdr.LabelOpData = record.LabelOpData;
        if (!string.IsNullOrEmpty(record.Route)) fdr.Route = record.Route;

        // Exactly what TagOwnershipSync's own "never activated yet" branch does by hand (see its
        // own comment) - assigns default jurisdiction and moves the flight to STATE_COORDINATED,
        // with its own placeholder ETD/SSR code, both overwritten below with the server's real
        // values once they're known to actually apply (only once a route exists to establish
        // against - see the empty-ParsedRoute check next).
        MMI.EstFDR(fdr);

        if (fdr.ParsedRoute.Count == 0)
        {
            // No route to establish against, same as TagOwnershipSync's own "still not eligible"
            // case - EstFDR has already left an EST Warning in vatSys's own error log for this, and
            // fdr is still sitting at STATE_PREACTIVE, so the next poll tick will simply try again.
            return;
        }

        if (record.Rfl is > 0) fdr.RFL = record.Rfl.Value;
        if (record.CflLower is > 0) fdr.CFLLower = record.CflLower.Value;
        if (record.CflUpper is > 0) fdr.CFLUpper = record.CflUpper.Value;
        if (record.AssignedSsrCode is >= 0) fdr.AssignedSSRCode = record.AssignedSsrCode.Value;
        if (record.Atd != null) fdr.ATD = record.Atd.Value;
        if (record.Etd != null) fdr.ETD = record.Etd.Value;
        if (record.EetMinutes is > 0) fdr.EET = TimeSpan.FromMinutes(record.EetMinutes.Value);
        if (record.Tas is > 0) fdr.TAS = record.Tas.Value;

        // Forces a full recompute/repaint after landing several field writes at once, the same
        // "batch of changes, one process pass" pattern vatSys's own FDR-received handler uses (see
        // FDP2.cs) rather than relying on each individual property's own OnPropertyChanged.
        // FDP2.Process's own ProcessTask already raises FDRsChanged itself once it's done (see
        // FDP2.cs) - it can't be raised again from here even if that were still wanted: outside the
        // class that declares it, a public event only permits += / -=, not a direct invocation.
        FDP2.Process(fdr, true);

        ActionLog.Log("Tag", $"Activated {fdr.Callsign} from OzServer (server state was {record.State})");
    }

    // Same fire-and-forget marshaling every other class in this plugin uses for a timer/event
    // callback that has to touch live vatSys UI/tracking state.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
