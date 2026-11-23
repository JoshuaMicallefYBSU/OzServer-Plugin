using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// What one Apply did, for the window to report. Sectors are named rather than typed: some entries
// (a contested sub-sector reported by the server) are never resolved to a Sector.
public class SectorCommitResult
{
    public List<string> Claimed { get; } = new();
    public List<string> Released { get; } = new();
    public List<string> Requested { get; } = new();
    // Covered sub-sectors another controller owns, left with them rather than requested - see
    // CommitSectorChangesAsync.
    public List<string> Skipped { get; } = new();
    public List<string> Failed { get; } = new();
}

// One sector changing hands, and the other party: the controller it went to when this session
// lost it, or the one who had it when this session gained it. The counterparty lets the losing
// client hand the sector's tags to the right controller, and the gaining client recognise the
// resulting handoffs as an OzServer transfer rather than a manual one.
public class SectorTransfer
{
    public SectorTransfer(SectorsVolumes.Sector sector, OzServerControlledSectorOwnerDto? counterparty)
    {
        Sector = sector;
        Counterparty = counterparty;
    }

    public SectorsVolumes.Sector Sector { get; }

    // Null when nobody else is involved - a sector released to nobody, or claimed from nobody.
    public OzServerControlledSectorOwnerDto? Counterparty { get; }
}

public class SectorOwnershipDiff
{
    public List<SectorTransfer> Gained { get; } = new();
    public List<SectorTransfer> Lost { get; } = new();
}

// Keeps OzServer's sector-ownership record and MMI.SectorsControlled in step, whether or not
// OzServerSectorsWindow is open. Constructed unconditionally by Plugin: a VSCS transmit press, a
// logon under a position's callsign and vatSys's own Sectors window only ever change
// MMI.SectorsControlled, and this is what claims the result on OzServer.
//
// Owned is OzServer's ownership record for this controller and nothing else. Every action that can
// change ownership (claim, release, accept, an MMI change) calls its endpoint and then re-derives
// Owned from the server's answer rather than predicting the result locally.
//
// MMI -> OzServer is claim-only. A release is never inferred from a sector's absence in MMI:
// MMI changes for reasons that say nothing about intent, and releasing on a diff fights the claim
// path in a claim/release loop. A release comes only from an explicit action, such as the Sectors
// window or a VSCS line leaving Transmit (ReleaseIdleVscsLineAsync).
//
// OzServer -> MMI: when Owned changes (ReconcileMmiWithOwned), a gained sector is added to
// MMI.SectorsControlled and a lost one removed. VSCS is left alone both ways (issue #5), except
// that gaining this session's own login position puts its line on transmit. An owned sector
// missing from MMI is put back (RestoreOwnedToMmi).
//
// Polls on its own timer, so an accepted request reaches this session even if the Sectors window
// is never opened; the event stream only shortens the wait. Everyone else's ownership is tracked
// too (_controlled, OwnerOf), for the Sectors window and the scope overlays.
public class OzServerOwnershipTracker
{
    // Idle cadence. Has to stay inside the backend's PRESENCE_TIMEOUT_SECONDS, after which a quiet
    // owner's sector may be taken by someone else: a slower poll would make a connected controller
    // look absent. Change the two together.
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    // Cadence while a request is in flight - this controller is waiting on a decision or owes one -
    // which is when the delay in seeing the answer is felt.
    static readonly TimeSpan PendingPollInterval = TimeSpan.FromSeconds(1);

    public event EventHandler? OwnedChanged;
    // Raised after every applied sync, whether or not anything changed. ObserverPositionMirror
    // follows ControlledByOthers from here.
    public event EventHandler? Refreshed;
    // Callsigns the backend restored to this controller on a reconnect inside the resume window,
    // having confirmed nobody else took them meanwhile. Consumed by TagResumeRecovery.
    public event EventHandler<IReadOnlyList<string>>? TagsResumed;
    // Raised when the set of incoming ("Requested From Me") requests changes, carrying the
    // requests so Plugin can name the sector and the requester in its popup. Polled here so the
    // notification works even if the Sectors window has never been opened.
    public event EventHandler<IReadOnlyList<OzServerSectorOwnershipRequestDto>>? IncomingRequestsChanged;
    // Raised when Owned gains or loses a sector, with the counterparty. Consumed by
    // SectorTagHandoff, which moves the aircraft in that sector to match.
    public event EventHandler<SectorOwnershipDiff>? OwnershipChanged;

    // The full requests payload from the last sync, for the Sectors window, which renders both
    // directions and the rejected rows.
    public event EventHandler<OzServerMyRequestsDto>? RequestsChanged;

    public OzServerMyRequestsDto MyRequests { get; private set; } = new();

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _pollTimer;
    List<SectorsVolumes.Sector> _owned = new();
    // Everyone else's active ownership, keyed by sector name. Kept current unconditionally, like
    // Owned, rather than only while the Sectors window is open.
    Dictionary<string, OzServerControlledSectorOwnerDto> _controlled = new(StringComparer.OrdinalIgnoreCase);
    // False until the first sync has been applied. That result is the session's starting state,
    // not a change, so it is recorded without a gained/lost diff (see ApplySync).
    bool _hasBaseline;
    // Until then Owned is empty meaning "not known yet", not "you own nothing" - see
    // OzServerSectorsWindow.SyncOwnedFromTracker.
    public bool HasBaseline => _hasBaseline;
    string? _pendingRequestSignature;
    // Which cadence the timer is on, so it is only reprogrammed when that changes.
    bool _pollingFast;
    // Coalesce MMI.SectorsControlledChanged so that only one claim pass runs at a time - see
    // OnMmiSectorsControlledChanged.
    bool _claimRunning;
    bool _claimQueued;
    // Guards the two flags above, which are not only touched from the UI thread (RunOnUiThread runs
    // inline when it finds no MainForm). Clearing _claimRunning and testing _claimQueued have to be
    // one atomic step: otherwise a change arriving between the two is queued on a loop that has
    // already decided to stop, and the sector never reaches OzServer.
    readonly object _claimGate = new();
    // Serialises applying a sync. ApplySync diffs against the previous result, so two overlapping
    // runs would compute the same gained/lost diff and push it twice. RefreshFromServerAsync waits
    // its turn; RefreshFromServerIfIdleAsync does not queue behind a refresh already running.
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    // A refresh asked for while one was already running, answered as soon as that one finishes -
    // see RefreshFromServerIfIdleAsync. Guarded by its own lock object: SemaphoreSlim may lock on
    // itself internally, so application state is not locked on the same instance.
    readonly object _refreshQueueGate = new();
    bool _refreshQueued;

    // Sectors a claim was answered for without being granted, and when to ask again. A claim
    // succeeds even when the sector it names is not among those granted: the backend withholds a
    // sector whose owner is present and logged on inside the same group (claimGroup's `withheld`).
    // The sector is then still in MMI and still not in Owned, so without a back-off
    // RetryUnclaimedMmiSectors would claim it again on every poll. It is retried rather than
    // abandoned because whoever it was withheld for may log off.
    static readonly TimeSpan WithheldBackoff = TimeSpan.FromSeconds(60);
    readonly object _withheldGate = new();
    readonly Dictionary<string, DateTime> _withheldUntil = new(StringComparer.OrdinalIgnoreCase);

    // How long a release this client sent keeps RestoreOwnedToMmi from putting the sector back
    // while Owned still lists it. Has to outlast the release's round trip and the refresh that
    // follows; anything longer only delays the repair if the release never lands.
    static readonly TimeSpan ReleaseSettleWindow = TimeSpan.FromSeconds(10);
    readonly object _releasedGate = new();
    readonly Dictionary<string, DateTime> _releasedRecently = new(StringComparer.OrdinalIgnoreCase);

    // What the last resume handed back, for a short while afterwards - see RecentlyResumed. Long
    // enough to outlast AfvSectorClaimer's connect-time retries plus a slow resume.
    static readonly TimeSpan ResumedGraceWindow = TimeSpan.FromSeconds(60);
    readonly object _resumedGate = new();
    List<SectorsVolumes.Sector> _resumed = new();
    DateTime _resumedUntil = DateTime.MinValue;

    // True from disconnect until AfvSectorClaimer has applied the logon grant, and nothing in
    // MMI.SectorsControlled is claimed meanwhile. Before connecting, MMI holds whatever display
    // position is open (vatSys's MMI.SetPrimePosition sets the whole position while offline), and
    // vatSys re-applies it on connect before Network.Connected fires. Claimed, that was the
    // display position's group rather than the logon's: ML-BKE_CTR with Alice Springs open took
    // all of ASP. The grant replaces MMI with the logon position's sectors, and claiming starts
    // from there.
    volatile bool _awaitingLogonGrant = !Network.IsConnected;

    // True from disconnect until this session's resume has been answered (or skipped, or failed),
    // and nothing is claimed meanwhile either. The resume is where the backend hands a position
    // over from the previous controller of this callsign, if they left inside its handover window:
    // the position's sectors with their tags, which TagResumeRecovery then takes. A logon claim
    // landing first would take those sectors as an ordinary claim on an absent owner, and that
    // leaves their tags with nobody.
    volatile bool _awaitingResume = !Network.IsConnected;

    // Called by AfvSectorClaimer once the logon grant is in MMI, or once it has stopped trying
    // (an observer, or a callsign that is no sector's). Claims whatever MMI then holds.
    public void LogonGrantSettled()
    {
        if (!_awaitingLogonGrant)
            return;

        _awaitingLogonGrant = false;
        if (!_awaitingResume)
            RunOnUiThread(OnMmiSectorsControlledChanged);
    }

    void ResumeSettled()
    {
        lock (_carriedOverGate)
            _resumeSettledSource.TrySetResult(true);

        if (!_awaitingResume)
            return;

        _awaitingResume = false;
        if (!_awaitingLogonGrant)
            RunOnUiThread(OnMmiSectorsControlledChanged);
    }

    // Bumped on every disconnect. A resume settles only the session it was started for: one still
    // waiting when the connection dropped (it waits up to 5 s for Network.Me, then up to the request
    // timeout) would otherwise finish inside the next session and clear its _awaitingResume before
    // that session's own resume had answered - letting the logon claim pre-empt a handover.
    int _sessionGeneration;

    async Task ResumeThenClaimAsync()
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        try
        {
            await ResumePreviousSessionAsync();
        }
        finally
        {
            if (Volatile.Read(ref _sessionGeneration) == generation)
                ResumeSettled();
        }
    }

    public IReadOnlyList<SectorsVolumes.Sector> Owned => _owned;

    // What OzServer still recorded as this controller's when this session's resume was answered:
    // the previous session's sectors, given back by the resume or never released (a drop), plus a
    // position handed over under this callsign. Nothing of this session's own has been claimed by
    // then - claims wait for the resume (see _awaitingResume) - so the set is exactly what was
    // already this controller's before they connected.
    //
    // Another controller online at connect does not own any of it: had they claimed it while this
    // controller was away, OzServer would no longer list it here. BN-TRT_CTR handed TRT to
    // BN-TRS_CTR, who then reconnected; TRT was given back by the resume and then relinquished to
    // BN-TRT_CTR as "already online" (PrimaryPositionWatcher), taking TRS's own sector with it.
    // Kept for the whole session, cleared on disconnect.
    readonly object _carriedOverGate = new();
    HashSet<string> _carriedOver = new(StringComparer.OrdinalIgnoreCase);
    // Completed once this session's resume has been answered (or skipped, or failed) - see
    // WhenResumeSettled. Already completed when the tracker is built mid-session.
    TaskCompletionSource<bool> _resumeSettledSource = NewResumeSettledSource(settled: Network.IsConnected);

    static TaskCompletionSource<bool> NewResumeSettledSource(bool settled)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (settled)
            source.SetResult(true);
        return source;
    }

    public bool IsCarriedOver(SectorsVolumes.Sector sector)
    {
        lock (_carriedOverGate)
            return _carriedOver.Contains(sector.Name);
    }

    // Completes when this session's resume has been answered. Anything that has to tell the
    // previous session's sectors from this session's own claims (IsCarriedOver) waits on it.
    public Task WhenResumeSettled
    {
        get
        {
            lock (_carriedOverGate)
                return _resumeSettledSource.Task;
        }
    }

    // Sectors a resume put back moments ago. AfvSectorClaimer.Init() replaces MMI.SectorsControlled
    // with the position's defaults and is not ordered against the resume, so it can land afterwards
    // and take the resumed sectors - and any tag TagResumeRecovery restored inside them - back out
    // of MMI. Init merges these in instead.
    public IReadOnlyList<SectorsVolumes.Sector> RecentlyResumed
    {
        get
        {
            lock (_resumedGate)
                return DateTime.UtcNow < _resumedUntil ? _resumed.ToList() : new List<SectorsVolumes.Sector>();
        }
    }


    // Everyone else's ownership as of the last sync. The Sectors window reads this copy rather than
    // fetching its own.
    public IReadOnlyDictionary<string, OzServerControlledSectorOwnerDto> ControlledByOthers => _controlled;

    // Callsigns of every controller running the plugin now, this one included, from the last sync.
    // Null until a sync has carried the list, or when the backend does not send one.
    public HashSet<string>? PluginClients { get; private set; }

    public bool IsMine(SectorsVolumes.Sector sector) => _owned.Any(o => o.Equals(sector));

    // Whoever OzServer says holds the sector: a synthetic record for this session when it is in
    // Owned (which carries no owner record), otherwise the entry in _controlled, otherwise null.
    public OzServerControlledSectorOwnerDto? OwnerOf(SectorsVolumes.Sector sector)
    {
        if (IsMine(sector))
        {
            // CidOrZero is safe here: this only ever describes this session.
            return new OzServerControlledSectorOwnerDto
            {
                Cid = NetworkIdentity.CidOrZero,
                Callsign = Network.Callsign,
            };
        }

        return _controlled.TryGetValue(sector.Name, out var owner) ? owner : null;
    }

    public OzServerOwnershipTracker()
    {
        MMI.SectorsControlledChanged += (_, _) => RunOnUiThread(OnMmiSectorsControlledChanged);
        // The first sync of a session comes from the resume, not from waiting for the poll timer.
        Network.Connected += (_, _) =>
        {
            // Before anything claims: a reconnect inside the resume window keeps the controller's
            // dropped sub-sectors out of the logon claim - see _droppedSubSectors.
            lock (_droppedSubSectors)
            {
                if (DateTime.UtcNow - _droppedDisconnectedAt > DroppedResumeWindow)
                    _droppedSubSectors.Clear();
                _droppedDisconnectedAt = DateTime.MaxValue;
            }

            PrimaryPosition.MarkConnected();
            _ = ResumeThenClaimAsync();
        };
        // Back-offs and resume state do not carry into the next session, which may be on a
        // different position.
        Network.Disconnected += (_, _) =>
        {
            // Kept, not cleared: a reconnect within the resume window is the same session as far
            // as the controller is concerned. Network.Connected decides.
            lock (_droppedSubSectors)
                _droppedDisconnectedAt = DateTime.UtcNow;

            Interlocked.Increment(ref _sessionGeneration);
            _awaitingLogonGrant = true;
            _awaitingResume = true;

            lock (_withheldGate)
                _withheldUntil.Clear();

            lock (_resumedGate)
            {
                _resumed = new List<SectorsVolumes.Sector>();
                _resumedUntil = DateTime.MinValue;
            }

            lock (_carriedOverGate)
            {
                _carriedOver = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_resumeSettledSource.Task.IsCompleted)
                    _resumeSettledSource = NewResumeSettledSource(settled: false);
            }

            // A No to requesting the position is for this session only.
            lock (_ownPositionDeclined)
                _ownPositionDeclined.Clear();

            // And so is either answer about a dropped controller's sectors.
            lock (_takeDroppedGate)
            {
                _takeDroppedAgreed.Clear();
                _takeDroppedDeclined.Clear();
            }

            ResetSession();
        };
        _pollTimer = new System.Threading.Timer(_ =>
        {
            if (!Network.IsConnected)
                return;

            _ = RefreshFromServerIfIdleAsync();
            RunOnUiThread(() =>
            {
                RetryUnclaimedMmiSectors();
                DropGivenAwayFromMmi();
                RestoreOwnedToMmi();
            });
        }, null, PollInterval, PollInterval);

        // A "sectors" signal covers ownership and requests: /sectors/sync returns owned, controlled
        // and requests together, so one refresh answers all three, including the incoming request
        // that drives the popup.
        OzServerEventStream.Shared.EventReceived += name =>
        {
            if (name != "sectors" || !Network.IsConnected)
                return;

            _ = RefreshFromServerIfIdleAsync();
        };

        if (Network.IsConnected)
        {
            _ = RefreshFromServerIfIdleAsync();
        }
    }

    // Counts sessions, so that a reply still in flight when one ends is not applied to the next.
    int _session;

    // Forgets everything learned about the session that has just ended, so the next connection
    // starts as a fresh vatSys start does: its first sync is a baseline, and nothing owned, given
    // away or held back before the disconnect is carried into it. Otherwise a reconnect in the
    // same vatSys run is compared against the old session - the position's sectors read as already
    // owned and are never claimed, then as lost and are taken off the scope.
    void ResetSession()
    {
        Interlocked.Increment(ref _session);

        _owned = new List<SectorsVolumes.Sector>();
        _controlled = new Dictionary<string, OzServerControlledSectorOwnerDto>(StringComparer.OrdinalIgnoreCase);
        PluginClients = null;
        _hasBaseline = false;
        _pendingRequestSignature = null;
        MyRequests = new OzServerMyRequestsDto();

        lock (_withheldGate)
        {
            _givenAway.Clear();
            _transmitPromptHold.Clear();
        }

        lock (_releasedGate)
            _releasedRecently.Clear();
    }

    // Repairs MMI -> OzServer drift: a sector controlled here that OzServer does not record as
    // owned, such as a connect-time grant whose claim was skipped or failed. MMI.SectorsControlled
    // does not change again afterwards, so nothing else would re-fire the claim, and the controller
    // would hold their position's airspace locally with no ownership record and no error.
    //
    // Sectors left contested are pulled back out of MMI by HandleConflictAsync, so a discrepancy
    // resolves rather than retrying forever.
    void RetryUnclaimedMmiSectors()
    {
        if (OzServerMode.Disabled)
            return;

        if (!Network.IsConnected || !IsRealAtc || !_hasBaseline)
            return;

        // The same test the claim pass applies, sector for sector, so the in-sync case - and a
        // sector the pass would skip anyway - does not start a pass and its refresh on every tick.
        var target = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        if (!target.Any(s => NeedsClaim(s, target)))
            return;

        OnMmiSectorsControlledChanged();
    }

    // Whether the claim pass would send a claim for this sector. See ClaimMmiControlledSectorsAsync
    // for the first two rules; the rest are holds described at their own definitions.
    bool NeedsClaim(SectorsVolumes.Sector sector, List<SectorsVolumes.Sector> target) =>
        !_owned.Any(o => o.Equals(sector))
        && !target.Any(other => !other.Equals(sector) && other.SubSectors.Any(sub => sub.Equals(sector)))
        && !IsWithheld(sector.Name)
        && !IsReliefCovered(sector)
        && !IsHeldForTransmitPrompt(sector.Name)
        && !IsGivenAway(sector.Name)
        && !IsBeingLost(sector)
        // Another plugin (VatpacPlugin, on the same transmit press) can still put an enroute or TMA
        // sector into a flow position's MMI. It is never claimed from there - see
        // PrimaryPosition.MayOwn.
        && PrimaryPosition.MayOwn(sector, Network.Me?.Callsign);

    // An FMP is covering this sector with its controller's agreement. The login sector stays in
    // that controller's MMI, so without this every poll would claim it, be refused and - it being
    // their own position - request it straight back. Once the FMP lets it go, the next poll finds
    // it unowned and claims it.
    bool IsReliefCovered(SectorsVolumes.Sector sector) =>
        _controlled.TryGetValue(sector.Name, out var owner) && PrimaryPosition.IsFlowCallsign(owner.Callsign);

    // Sectors this session handed to another controller (see ReconcileMmiWithOwned). Their VSCS
    // line is left alone and may still be transmitting, which is enough for VatpacPlugin to put the
    // sector back into MMI - and the claim pass would then ask for airspace this controller has
    // just agreed to give. Cleared once nobody else holds the sector or it is this session's again;
    // taking it back on purpose is a transmit press, which asks the new owner.
    readonly HashSet<string> _givenAway = new(StringComparer.OrdinalIgnoreCase);

    bool IsGivenAway(string name)
    {
        lock (_withheldGate)
        {
            if (!_givenAway.Contains(name))
                return false;
            if (!_controlled.ContainsKey(name) || _owned.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                _givenAway.Remove(name);
                return false;
            }
            return true;
        }
    }

    // Takes a given-away sector back out of MMI (re-added for its still-transmitting line), VSCS
    // untouched, so the scope does not show airspace that is no longer this controller's.
    void DropGivenAwayFromMmi()
    {
        if (OzServerMode.Disabled)
            return;

        var current = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var keep = current.Where(s => !IsGivenAway(s.Name)).ToList();
        if (keep.Count != current.Count)
            MMI.SetControlledSectors(keep);
    }

    // Sectors ReconcileMmiWithOwned is part-way through taking out of MMI. They are still listed
    // there while their aircraft are handed over, so the claim pass must not mistake them for
    // sectors newly controlled here. UI thread only; the window is a backstop in case the
    // reconcile is cut short before it clears this itself.
    static readonly TimeSpan LosingWindow = TimeSpan.FromSeconds(2);
    List<SectorsVolumes.Sector> _losing = new();
    DateTime _losingUntil = DateTime.MinValue;

    bool IsBeingLost(SectorsVolumes.Sector sector) =>
        DateTime.UtcNow < _losingUntil && _losing.Any(s => s.Equals(sector));

    bool IsWithheld(string name)
    {
        lock (_withheldGate)
            return _withheldUntil.TryGetValue(name, out var until) && DateTime.UtcNow < until;
    }

    // Logged only when a back-off starts, not on every skipped tick.
    void MarkWithheld(SectorsVolumes.Sector sector, IReadOnlyCollection<string> claimed)
    {
        lock (_withheldGate)
            _withheldUntil[sector.Name] = DateTime.UtcNow + WithheldBackoff;

        ActionLog.Log("Ownership",
            $"Claimed {sector.Name}, but OzServer withheld it - a controller logged on inside its group holds it. "
            + $"Not asking again for {WithheldBackoff.TotalSeconds:0}s",
            new
            {
                action = "claim_withheld",
                sector = sector.Name,
                claimed = claimed.ToArray(),
                own_callsign = Network.Me?.Callsign
            });
    }

    // Repairs OzServer -> MMI drift, the mirror of RetryUnclaimedMmiSectors: a sector owned on
    // OzServer but not controlled here. It is held against every other controller while locally it
    // has no infill, no jurisdiction over its tags and no way to give it back, since every release
    // path starts from the sector being in MMI.SectorsControlled. The paths that add an owned
    // sector to MMI each act once and can lose an ordering race; this checks again on every poll
    // tick and after every sync.
    //
    // Adds only, and leaves VSCS alone, as ReconcileMmiWithOwned does for a gained sector. It
    // cannot claim anything: a sector only gets here by already being in Owned.
    void RestoreOwnedToMmi()
    {
        if (OzServerMode.Disabled)
            return;

        if (!Network.IsConnected || !IsRealAtc || !_hasBaseline)
            return;

        var mmiSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var missing = _owned
            .Where(owned => !owned.IsDummy)
            .Where(owned => !mmiSectors.Any(s => s.Equals(owned)))
            // A release this client has just sent: Owned still lists the sector until the release's
            // refresh lands, and putting it back meanwhile would undo the removal on screen.
            .Where(owned => !WasJustReleased(owned.Name))
            // Including one vatSys took off because its own controller has come online: vatSys does
            // that once, as it validates them (Network.VATSIM_ClientQueryResponseReceived), but the
            // sector stays this controller's until they request it and it is accepted. Left out, its
            // infill vanished from ML-HYD_CTR's scope the moment ML-ASP_CTR connected, while HYD
            // still held ASP. A sector being relinquished to a controller already online is excused
            // above instead (PrimaryPositionWatcher marks it with ExpectReleaseOf).
            .ToList();

        if (missing.Count == 0)
            return;

        mmiSectors.AddRange(missing);
        MMI.SetControlledSectors(mmiSectors);

        // Logged every time: this firing means something took the sector out of MMI without
        // releasing it, and the log line is what shows when that happened.
        ActionLog.Log("Ownership",
            $"Put back into vatSys: {string.Join(", ", missing.Select(s => s.Name))} - owned on OzServer but missing from the controlled sectors",
            new
            {
                action = "restore_owned_to_mmi",
                sectors = missing.Select(s => s.Name).ToArray(),
                own_callsign = Network.Me?.Callsign
            });
    }

    // Names this client has just asked the backend to release, excused from RestoreOwnedToMmi
    // until the window passes. Marked before the call goes out so the whole round trip is covered.
    void MarkReleased(IEnumerable<string> names)
    {
        var until = DateTime.UtcNow + ReleaseSettleWindow;

        lock (_releasedGate)
        {
            // Pruned here: this is the only place that grows the set.
            foreach (var expired in _releasedRecently.Where(entry => entry.Value < DateTime.UtcNow).Select(entry => entry.Key).ToList())
                _releasedRecently.Remove(expired);

            foreach (var name in names)
                _releasedRecently[name] = until;
        }
    }

    bool WasJustReleased(string name)
    {
        lock (_releasedGate)
            return _releasedRecently.TryGetValue(name, out var until) && DateTime.UtcNow < until;
    }

    // Excuses everything a release of this sector gives back: the backend's releaseGroup takes the
    // named sector's whole covered group. Excusing only the name would let RestoreOwnedToMmi put
    // the rest of the group back for the length of the round trip, each time with a full
    // MMI.SetControlledSectors and the track-state recompute and tag cleanup that go with it.
    void MarkGroupReleased(SectorsVolumes.Sector sector) =>
        MarkReleased(PrimaryPosition.CoveredBy(sector).Select(covered => covered.Name));

    // For a caller about to release several sectors in turn (PrimaryPositionWatcher). Each release
    // excuses only its own group as it goes out, so the ones still waiting - already off the scope,
    // still in Owned - would otherwise be put back by the refresh after each earlier release.
    public void ExpectReleaseOf(IEnumerable<SectorsVolumes.Sector> sectors)
    {
        foreach (var sector in sectors)
            MarkGroupReleased(sector);
    }

    // Lifts the excuse for sectors that are kept after all - see
    // ReclaimOwnPositionAfterReleaseAsync.
    void UnmarkReleased(IEnumerable<string> names)
    {
        lock (_releasedGate)
        {
            foreach (var name in names)
                _releasedRecently.Remove(name);
        }
    }

    // Runs one ClaimMmiControlledSectorsAsync at a time. MMI.SectorsControlled can change while a
    // pass is in flight - not least from that pass's own effects, since ReconcileMmiWithOwned and
    // HandleConflictAsync write MMI and VSCS and re-fire this event synchronously. Overlapping
    // passes race on _owned: a stale read skips the "already mine" check and re-claims a whole
    // group moments after part of it was released. A change during a pass queues one more pass
    // after it instead.
    void OnMmiSectorsControlledChanged()
    {
        if (OzServerMode.Disabled)
            return;

        // Nothing is synced while offline (vatSys clears MMI.SectorsControlled on disconnect, for
        // one) or on a server OzServer does not operate on, where every claim would fail before it
        // was sent - see CanSync.
        if (!CanSync)
            return;

        // MMI still holds the offline display position (see _awaitingLogonGrant), or a position
        // handover may still be coming in the resume (see _awaitingResume).
        if (_awaitingLogonGrant || _awaitingResume)
            return;

        lock (_claimGate)
        {
            if (_claimRunning)
            {
                _claimQueued = true;
                return;
            }

            _claimRunning = true;
        }

        _ = RunClaimLoop();
    }

    async Task RunClaimLoop()
    {
        try
        {
            while (true)
            {
                await ClaimMmiControlledSectorsAsync();

                // Test-and-clear as one atomic step - see _claimGate.
                lock (_claimGate)
                {
                    if (!_claimQueued)
                    {
                        _claimRunning = false;
                        return;
                    }

                    _claimQueued = false;
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing awaits this loop, so an escape would be an unobserved task exception and would
            // leave _claimRunning stuck true, with every later MMI change ignored.
            lock (_claimGate)
            {
                _claimRunning = false;
                _claimQueued = false;
            }

            OzErrors.Add(new Exception($"Couldn't sync controlled sectors to OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    // Applies the requests half of a sync. Two events because the consumers differ: Plugin only
    // needs to know the incoming set changed, while the Sectors window renders both directions,
    // including this controller's own rejections.
    void ApplyRequests(OzServerMyRequestsDto requests)
    {
        MyRequests = requests;
        RequestsChanged?.Invoke(this, requests);

        // A rejected request in by_me is decided, not in flight, and stays until the Sectors window
        // acknowledges it - which may never be opened - so it does not hold the fast cadence.
        SetPollCadence(requests.ByMe.Any(r => r.RejectedAt == null) || requests.FromMe.Count > 0);

        // Compared on request ids, not the count: one request accepted while another arrives
        // between polls leaves the count unchanged.
        var signature = string.Join(",", requests.FromMe.Select(r => r.Id).OrderBy(id => id));
        if (signature == _pendingRequestSignature)
            return;

        _pendingRequestSignature = signature;
        IncomingRequestsChanged?.Invoke(this, requests.FromMe);
    }

    // No ConfigureAwait(false) anywhere on this path: callers reached from the UI (the Sectors
    // window, which touches controls the moment this returns) rely on resuming on the UI thread.
    public async Task RefreshFromServerAsync()
    {
        if (OzServerMode.Disabled)
            return;

        // Guarded here as well as at the callers inside this class: OzServerSectorsWindow calls
        // this directly.
        if (!CanSync)
            return;

        // Waits its turn, so Owned is current by the time the caller resumes - see _refreshGate.
        await _refreshGate.WaitAsync();
        try
        {
            await RefreshFromServerCoreAsync();
        }
        finally
        {
            ReleaseRefreshGate();
        }
    }

    // The fire-and-forget variant, for the periodic and event-driven triggers (the poll timers, the
    // event stream, the Sectors window opening). A call that finds a refresh already running does
    // not queue behind it: these all ask the same question, and a slow request would otherwise
    // build a backlog. Actions that need Owned current when they return use RefreshFromServerAsync.
    public async Task RefreshFromServerIfIdleAsync()
    {
        if (OzServerMode.Disabled)
            return;

        if (!CanSync)
            return;

        // WaitAsync(0) completes synchronously: take the gate if it is free, otherwise give up.
        // The call is coalesced rather than dropped - an event-stream signal means something
        // changed - so one more pass runs when the in-flight one finishes.
        if (!await _refreshGate.WaitAsync(0))
        {
            lock (_refreshQueueGate)
                _refreshQueued = true;

            return;
        }

        try
        {
            await RefreshFromServerCoreAsync();
        }
        finally
        {
            ReleaseRefreshGate();
        }
    }

    // Releases the refresh gate and answers anything that arrived while it was held. Every path
    // that takes the gate releases through here, so a signal arriving during a claim, release or
    // accept - when ownership is changing - is answered too. Test-and-clear is one step, as with
    // _claimGate, so a signal arriving in between is not lost.
    void ReleaseRefreshGate()
    {
        _refreshGate.Release();

        bool queued;
        lock (_refreshQueueGate)
        {
            queued = _refreshQueued;
            _refreshQueued = false;
        }

        // After the release, so the re-entry finds the gate free. Fire-and-forget.
        if (queued)
            _ = RefreshFromServerIfIdleAsync();
    }

    // Claims a sector, leaving out whatever covered sub-sectors somebody is logged on as. The
    // exclusion list is what stops an enroute controller taking an online approach controller's
    // sectors, and is computed here because this side sees the live VATSIM ATC list - see
    // PrimaryPosition.StaffedCoveredSectors. Recomputed on every claim, it is also what gives the
    // sectors back once that controller logs off (ReclaimTopDownCoverAsync).
    //
    // `also` carries anything else to leave out - the contested sub-sectors from a 409 retry.
    // Answers with what was granted - see OzServerApiClient.ClaimSectorAsync.
    async Task<IReadOnlyList<string>?> ClaimWithExclusionsAsync(SectorsVolumes.Sector sector, IEnumerable<string>? also = null)
    {
        // Every claim path comes through here, so this is the plugin's last check on a flow
        // position taking enroute or TMA airspace - see PrimaryPosition.MayOwn.
        if (!PrimaryPosition.MayOwn(sector, Network.Me?.Callsign))
            return Array.Empty<string>();

        // Just after connecting, the exclusions below cannot see a controller vatSys has not
        // validated yet - see PrimaryPosition.AwaitingValidationFor.
        var waitedUntil = DateTime.UtcNow + PrimaryPosition.ValidationWait;
        while (PrimaryPosition.AwaitingValidationFor(sector, Network.Me?.Callsign) && DateTime.UtcNow < waitedUntil)
            await Task.Delay(250);

        var exclude = PrimaryPosition.StaffedCoveredSectors(sector, Network.Me?.Callsign);

        // Sub-sectors the controller dropped from this group by hand stay dropped - see
        // _droppedSubSectors. Only the members, never the sector being claimed.
        foreach (var name in DroppedSubSectorsOf(sector))
        {
            if (!exclude.Contains(name, StringComparer.OrdinalIgnoreCase))
                exclude.Add(name);
        }

        foreach (var name in also ?? Enumerable.Empty<string>())
        {
            if (!exclude.Contains(name, StringComparer.OrdinalIgnoreCase))
                exclude.Add(name);
        }

        // Anything a dropped controller holds is taken only once this controller has agreed to it
        // (ConfirmTakeDroppedAsync).
        return await _api.ClaimSectorAsync(sector.Name, exclude, CoversAgreedDropped(sector));
    }

    // Takes back top-down cover that was withheld while somebody was logged on as it, once they
    // have left (PrimaryPositionWatcher). Re-claiming a sector this controller already owns is
    // enough, because the exclusion list is recomputed on every claim. Nothing else would pick the
    // sectors up: they were never in this client's MMI, so the MMI-driven claim path does not see
    // them, and vatSys does not restore a sector it dropped when its controller came online.
    public async Task ReclaimTopDownCoverAsync(IReadOnlyCollection<string> freed)
    {
        if (!Network.IsConnected || !IsRealAtc || freed.Count == 0)
            return;

        // Only the groups affected, rather than a burst of claims for airspace that has not
        // changed hands.
        var roots = _owned
            .Where(owned => PrimaryPosition.CoveredBy(owned)
                .Any(covered => freed.Contains(covered.Name, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        foreach (var sector in roots)
        {
            IReadOnlyList<OzServerSectorConflictDto>? conflicts = null;

            try
            {
                await ClaimWithExclusionsAsync(sector);
                ActionLog.Log("Ownership", $"Took back top-down cover under {sector.Name}");
            }
            catch (OzServerApiException ex) when (ex.StatusCode == 409 && ex.Conflicts.Count > 0)
            {
                // Handled after the try - see ClaimAsync.
                conflicts = ex.Conflicts;
            }
            catch (Exception ex)
            {
                OzErrors.Add(new Exception($"Couldn't take back cover under {sector.Name}: {ex.Message}", ex), "OzServer");
            }

            if (conflicts == null)
                continue;

            // Somebody claimed a piece of it meanwhile. Those stay with them and are not requested
            // (see CommitSectorChangesAsync); the rest is taken.
            try
            {
                await ClaimWithExclusionsAsync(sector, conflicts.Select(c => c.Sector));
                ActionLog.Log("Ownership",
                    $"Took back cover under {sector.Name} (excluding {string.Join(", ", conflicts.Select(c => c.Sector))})");
            }
            catch (Exception ex)
            {
                OzErrors.Add(new Exception($"Couldn't take back the rest of {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // Applies a sync that came back with an action (accept, reject, resume, commit) rather than
    // from a poll, under the same gate the polls use. ApplySync diffs against the last sync
    // applied, so outside the gate a poll issued before the action and answered after it could
    // overwrite the action's result with an older payload - and a sector just handed over would
    // reappear as gained and be pushed back into MMI.
    //
    // Waits rather than drops: an action's own result is never redundant.
    async Task ApplySyncGatedAsync(OzServerSyncDto sync)
    {
        if (OzServerMode.Disabled)
            return;

        await _refreshGate.WaitAsync();
        try
        {
            ApplySync(sync);
        }
        finally
        {
            ReleaseRefreshGate();
        }
    }

    // Asks the backend to put this session back on whatever it held when it last left this
    // position, then adopts the answer. Runs instead of the plain connect refresh: resume returns
    // the resulting state either way, so a session with nothing to recover costs the same one
    // round trip.
    //
    // Only what is still free is recovered. A sector or flight somebody picked up while this
    // controller was away stays theirs.
    async Task ResumePreviousSessionAsync()
    {
        if (OzServerMode.Disabled)
            return;

        // Network.Connected fires before IsConnected and Me have settled, so wait for them. The
        // observer test below reads the connection's own Facility and Rating (NetworkIdentity), not
        // Network.Me.IsRealATC, which reads false for a real controller for seconds after Connected
        // and would skip their resume.
        for (var attempt = 0; attempt < 25 && (!Network.IsConnected || Network.Me == null); attempt++)
            await Task.Delay(200);

        if (!Network.IsConnected)
        {
            ActionLog.Log("Resume", "skipped - network still not connected");
            return;
        }

        if (NetworkIdentity.IsObserver)
        {
            ActionLog.Log("Resume", "skipped - observer session, sectors are mirrored not owned");
            return;
        }

        // Same rule as CanSync: the request would fail before it was sent, and be reported as an
        // error on every connect to LocalHost or an unrecognised SweatBox.
        if (NetworkServer.Current == null)
        {
            ActionLog.Log("Resume", "skipped - OzServer does not operate on this server");
            return;
        }

        ActionLog.Log("Resume", $"requesting for {Network.Me?.Callsign ?? "(callsign unknown)"}");

        try
        {
            var positionSectors = PrimaryPosition.DefaultSectorsForCurrentSession(Network.Me?.Callsign)
                .Where(s => !s.IsDummy)
                .Select(s => s.Name)
                .ToList();
            var response = await _api.ResumeAsync(positionSectors);

            if (response.Sync != null)
                await ApplySyncGatedAsync(response.Sync);
            else
                await RefreshFromServerAsync();

            // Read before ResumeSettled lets anything claim - see _carriedOver.
            var carriedOverSectors = _owned.Where(s => !s.IsDummy).ToList();
            lock (_carriedOverGate)
                _carriedOver = new HashSet<string>(carriedOverSectors.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

            // An extension held before connecting gets its VSCS line back on Transmit: vatSys loads
            // only the logon station's group. Whether the resume gave it back or it was never
            // released (a drop, or a vatSys restart) - and it waits for the session to be validated.
            RunOnUiThread(() => VscsLines.TransmitOn(carriedOverSectors, "held before reconnecting"));

            ActionLog.Log("Resume",
                $"backend returned {response.Resumed.Count} sector(s), {response.Flights.Count} tag(s)"
                + (response.Flights.Count > 0 ? ": " + string.Join(", ", response.Flights) : ""));

            // Queued before TagsResumed below, so the sectors are in MMI by the time
            // TagResumeRecovery resolves the tags against it - both post to the UI thread, in order.
            if (response.Resumed.Count > 0)
            {
                var resumed = _owned.ToList();

                lock (_resumedGate)
                {
                    _resumed = resumed;
                    _resumedUntil = DateTime.UtcNow + ResumedGraceWindow;
                }

                RunOnUiThread(() => RestoreResumedToMmi(resumed));
            }

            if (response.Flights.Count > 0)
                TagsResumed?.Invoke(this, response.Flights);

            if (response.Resumed.Count > 0)
            {
                ActionLog.Log("Ownership",
                    $"Resumed previous session: {string.Join(", ", response.Resumed)}");
            }
        }
        catch (Exception ex)
        {
            // A backend without /sectors/resume, or a transient failure: fall back to the plain
            // connect refresh so the session still starts, without recovering anything.
            OzErrors.Add(new Exception($"Couldn't resume the previous session: {ex.Message}", ex), "OzServer");
            await RefreshFromServerIfIdleAsync();
        }
    }

    // Puts what a resume handed back into MMI.SectorsControlled. ReconcileMmiWithOwned only pushes
    // an ownership change, and a resume is usually not one: on the first connect its sync is the
    // baseline, and on a reconnect Owned still holds the previous session's list. Without this MMI
    // would hold only the position defaults AfvSectorClaimer.Init() grants, and TagResumeRecovery
    // could not place the tags inside the resumed sectors. Adds only, and leaves VSCS to the resume
    // (see the VscsLines call there).
    void RestoreResumedToMmi(List<SectorsVolumes.Sector> resumed)
    {
        if (!Network.IsConnected || !IsRealAtc)
            return;

        var mmiSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var missing = resumed.Where(sector => !mmiSectors.Any(s => s.Equals(sector))).ToList();
        if (missing.Count == 0)
            return;

        mmiSectors.AddRange(missing);
        MMI.SetControlledSectors(mmiSectors);

        ActionLog.Log("Resume", $"put resumed sector(s) back into vatSys: {string.Join(", ", missing.Select(s => s.Name))}");
    }

    void SetPollCadence(bool fast)
    {
        if (fast == _pollingFast)
            return;

        _pollingFast = fast;
        var interval = fast ? PendingPollInterval : PollInterval;

        try
        {
            _pollTimer.Change(interval, interval);
        }
        catch (ObjectDisposedException)
        {
            // Plugin shutting down - nothing left to reschedule.
        }
    }

    // One GET for all three read-only views of server state: owned, everyone else's ownership and
    // this controller's requests. A failed sync leaves all three as they were rather than
    // half-updated, so the views never disagree with each other.
    async Task RefreshFromServerCoreAsync()
    {
        var session = Volatile.Read(ref _session);
        OzServerSyncDto sync;
        try
        {
            sync = await _api.GetSyncAsync();
        }
        catch (Exception ex)
        {
            // Once per outage rather than once per poll - see ErrorThrottle.
            ErrorThrottle.Report("sector-refresh", $"Couldn't refresh sector state from OzServer: {ex.Message}", ex);
            return;
        }

        // Asked during a session that has since ended - see ResetSession.
        if (session != Volatile.Read(ref _session))
            return;

        ApplySync(sync);
    }

    // Adopts a sync payload, whether from the poll's GET or from the response of an action, which
    // carries the resulting state so that no second round trip is needed.
    void ApplySync(OzServerSyncDto sync)
    {
        if (OzServerMode.Disabled)
            return;

        // A reply that outlived its session would put the state ResetSession cleared straight back.
        if (!Network.IsConnected)
            return;

        // Captured before the replacement: a sector this session just gained needs the owner it had
        // a moment ago, which the incoming payload no longer mentions.
        var previousControlled = _controlled;

        _controlled = sync.Controlled
            .Where(c => c.Owner != null)
            .ToDictionary(c => c.Name, c => c.Owner!, StringComparer.OrdinalIgnoreCase);

        PluginClients = sync.PluginClients == null
            ? null
            : new HashSet<string>(sync.PluginClients.Select(c => c.Callsign), StringComparer.OrdinalIgnoreCase);

        ApplyRequests(sync.Requests);

        var mine = sync.Mine;
        var previous = _owned;
        var current = mine
            .Select(dto => SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == dto.Name))
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();
        _owned = current;

        // Pass the snapshots, not the fields. RunOnUiThread posts with BeginInvoke, so the reconcile
        // runs after this method has returned and released the refresh gate; reading _owned then
        // could pick up a later refresh's value and diff it against this run's `previous`.
        if (_hasBaseline)
            RunOnUiThread(() => ReconcileMmiWithOwned(previous, current, previousControlled));
        else
            _hasBaseline = true;

        // On every sync as well as on the poll tick, so sectors a resume hands back reach the scope
        // - and TagResumeRecovery, which resolves tags against MMI.SectorsControlled - without
        // waiting for the tick. That includes the baseline sync, which is when a resume lands: it
        // is not pushed into MMI as a change, but an owned sector missing from the scope is drift.
        RunOnUiThread(RestoreOwnedToMmi);

        OwnedChanged?.Invoke(this, EventArgs.Empty);

        // Raised after controlled, requests and owned have all been adopted, so consumers never
        // observe a half-applied sync.
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    // Pushes an Owned change onto MMI.SectorsControlled - see the class comment. Runs on the UI
    // thread. MMI.SetControlledSectors re-fires MMI.SectorsControlledChanged, and so another claim
    // pass and refresh; by then Owned matches what was pushed, so that pass finds no diff and
    // stops.
    void ReconcileMmiWithOwned(List<SectorsVolumes.Sector> previous, List<SectorsVolumes.Sector> current,
        IReadOnlyDictionary<string, OzServerControlledSectorOwnerDto> previousControlled)
    {
        if (OzServerMode.Disabled)
            return;

        // Queued before a disconnect and run after it: there is no session left to reconcile.
        if (!Network.IsConnected)
            return;

        var gained = current.Where(s => !previous.Any(p => p.Equals(s))).ToList();
        var lost = previous.Where(p => !current.Any(s => s.Equals(p))).ToList();

        if (gained.Count == 0 && lost.Count == 0)
            return;

        var diff = new SectorOwnershipDiff();
        // Gained: who had it before, from the snapshot taken before this sync replaced it.
        // Lost: who has it now, which the new snapshot records.
        foreach (var sector in gained)
            diff.Gained.Add(new SectorTransfer(sector,
                previousControlled.TryGetValue(sector.Name, out var had) ? had : null));

        foreach (var sector in lost)
            diff.Lost.Add(new SectorTransfer(sector,
                _controlled.TryGetValue(sector.Name, out var has) ? has : null));

        var mmiSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        // A gained sector is added to MMI, so its airspace and tags appear. Its position's VSCS line
        // is added if missing and put on Transmit further down (VscsLines) - which replaces the
        // earlier rule of leaving that to the controller (issue #5): with VatpacPlugin running, a
        // held sector whose line is on the panel but idle is taken out of MMI with its tags.
        foreach (var sector in gained)
        {
            if (!mmiSectors.Any(s => s.Equals(sector)))
                mmiSectors.Add(sector);
        }

        // Pushed to MMI before OwnershipChanged fires, with the lost sectors still in the list.
        // SectorTagHandoff's accept path (MMI.AcceptJurisdiction -> FDP2.AcceptJurisdiction ->
        // MMI.SetTrackState) decides Jurisdiction versus a demoted state by checking
        // MMI.SectorsControlled.Contains(fdr.ControllingSector) at the moment it runs. If the
        // gained sector were not there yet, a just-accepted tag could be left Announced and need
        // accepting by hand.
        // The write below still lists the lost sectors, and raises SectorsControlledChanged before
        // it returns - which starts a claim pass that would find them in MMI, no longer owned, and
        // claim them straight back. See IsBeingLost.
        _losing = lost;
        _losingUntil = DateTime.UtcNow + LosingWindow;

        if (gained.Count > 0)
            MMI.SetControlledSectors(mmiSectors.ToList());

        // GiveAway (lost) and the accept sweep (gained) both run here, with the gained sectors
        // added and the lost ones still present.
        OwnershipChanged?.Invoke(this, diff);

        // The lost sectors are removed only now, after GiveAway has handed off what this session
        // holds inside them. MMI.SetControlledSectors drops to uncontrolled any tracked aircraft
        // whose ControllingSector has left the list, with no regard for a handoff in progress, so
        // removing them first would leave those aircraft with nobody working them. GiveAway
        // re-points ControllingSector at the new owner's sector as it hands off, which keeps this
        // call from catching the same aircraft.
        //
        // The VSCS line is left as it is: handing a sector over is not leaving its frequency. As
        // the line may still be transmitting, the sector is marked as given away so that the claim
        // pass does not ask for it straight back - see IsGivenAway.
        foreach (var sector in lost)
        {
            mmiSectors.RemoveAll(s => s.Equals(sector));
            if (_controlled.ContainsKey(sector.Name))
            {
                lock (_withheldGate)
                    _givenAway.Add(sector.Name);
            }
        }

        if (lost.Count > 0)
            MMI.SetControlledSectors(mmiSectors);

        _losingUntil = DateTime.MinValue;

        // Every other gained position goes on Transmit too, its line added first if vatSys had not
        // loaded it - see VscsLines. After the MMI writes above, so the line comes on for a sector
        // already in MMI (AfvSectorClaimer.CheckActive then has nothing to add or claim).
        VscsLines.TransmitOn(gained, "sector gained");

        // Gaining this session's own login position - requested at logon from whoever was covering
        // it - puts its line on transmit: being on your own frequency is what logging on to the
        // position means.
        foreach (var sector in gained)
        {
            if (!string.Equals(sector.Callsign, Network.Callsign, StringComparison.OrdinalIgnoreCase))
                continue;

            var freq = Audio.VSCSFrequencies.FirstOrDefault(f => string.Equals(f.Name, sector.Callsign, StringComparison.OrdinalIgnoreCase));
            if (freq == null || freq.Transmit)
                continue;

            freq.Receive = true;
            freq.Transmit = true;
            ActionLog.Log("Primary", $"Put transmit on {sector.Name} - own position gained", new
            {
                action = "vscs_transmit_own_position_gained",
                sector = sector.Name,
                from_callsign = diff.Gained.FirstOrDefault(g => g.Sector.Equals(sector))?.Counterparty?.Callsign
            });
        }
    }

    // Whether this session is connected as ATC rather than as an observer. vatSys's own Sectors
    // window lets an observer populate MMI.SectorsControlled for situational awareness, and the
    // backend trusts the cid and callsign it is sent, so this check is what stops an observer
    // owning sectors (issue #3). Reads the connection's own Facility and Rating, which are correct
    // from the moment of connecting, rather than Network.Me.IsRealATC - see
    // NetworkIdentity.IsObserver.
    static bool IsRealAtc => !NetworkIdentity.IsObserver;

    // Connected, and to a server OzServer operates on. Anywhere else - LocalHost, or a SweatBox
    // hostname NetworkServer does not recognise - every call fails before it is sent (see
    // OzServerApiClient.GetCredentials), and a poll there would only report errors.
    static bool CanSync => Network.IsConnected && NetworkServer.Current != null;

    public async Task ClaimAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected || !IsRealAtc)
            return;

        // The conflict is handled after the try block, not inside the catch clause that detects
        // it. An exception thrown from a catch clause is not seen by that try's other catch
        // clauses, so a failure in HandleConflictAsync (it opens a window) would escape this
        // method and, through an async void click handler, reach vatSys as an unhandled exception.
        IReadOnlyList<OzServerSectorConflictDto>? conflicts = null;

        try
        {
            await ClaimWithExclusionsAsync(sector);
            ActionLog.Log("Ownership", $"Claimed {sector.Name}");
        }
        catch (OzServerApiException ex) when (ex.StatusCode == 409 && ex.Conflicts.Count > 0)
        {
            conflicts = ex.Conflicts;
        }
        catch (Exception ex)
        {
            OzErrors.Add(new Exception(ex.Message, ex), "OzServer");
        }

        if (conflicts != null)
        {
            try
            {
                await HandleConflictAsync(sector, conflicts);
            }
            catch (Exception ex)
            {
                OzErrors.Add(new Exception($"Couldn't resolve the ownership conflict on {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // Sub-sectors the controller released in the Sectors window while keeping the group that
    // covers them (LTA and HBA out of HUO). Every automatic claim of the group leaves them out
    // (ClaimWithExclusionsAsync) - the claim-back after a VSCS line's release, a resume, a retry.
    // Without this, a VSCS click on 16 Nov released HUO and claimed it straight back, and the
    // backend's group claim returned LTA and HBA moments after ML-BLA_CTR had dropped them.
    //
    // A member leaves the set when the controller claims it, or claims or releases the group, in
    // the window: that is them deciding about the whole group again.
    //
    // Kept across a disconnect for as long as the server keeps the resume snapshot
    // (RESUME_WINDOW_MINUTES), and only for the same callsign. ML-BLA_CTR dropped MAV, logged off
    // and back on a minute later, and the logon claim of BLA's group took MAV and MAW straight
    // back: the set had been cleared on disconnect. A longer gap, or another position, starts
    // clean - as the server's own resume does.
    readonly HashSet<string> _droppedSubSectors = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan DroppedResumeWindow = TimeSpan.FromMinutes(5);
    DateTime _droppedDisconnectedAt = DateTime.MaxValue;
    string? _droppedCallsign;

    List<string> DroppedSubSectorsOf(SectorsVolumes.Sector sector)
    {
        lock (_droppedSubSectors)
        {
            // Network.Me settles after Network.Connected, so the callsign is checked here, at the
            // first claim, rather than there.
            var me = Network.Me?.Callsign;
            if (!string.IsNullOrEmpty(me) && _droppedCallsign != null
                && !string.Equals(me, _droppedCallsign, StringComparison.OrdinalIgnoreCase))
            {
                _droppedSubSectors.Clear();
                _droppedCallsign = null;
            }

            if (_droppedSubSectors.Count == 0)
                return new List<string>();

            return PrimaryPosition.CoveredBy(sector)
                .Where(s => s.Name != sector.Name && _droppedSubSectors.Contains(s.Name))
                .Select(s => s.Name)
                .ToList();
        }
    }

    // Called with what an Apply is about to claim and release, before it goes out.
    void NoteWindowDecisions(IReadOnlyList<SectorsVolumes.Sector> toClaim, IReadOnlyList<SectorsVolumes.Sector> toRelease)
    {
        // Everything the releases give back - each named sector plus the owned members of its
        // group, as the backend's releaseGroup takes them (MAV takes MAW, which it covers) - and
        // the groups this controller keeps after the Apply.
        var released = toRelease
            .SelectMany(PrimaryPosition.CoveredBy)
            .Where(s => _owned.Any(o => o.Name == s.Name))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var kept = _owned.Where(o => !o.IsDummy && !released.Any(r => r.Name == o.Name)).ToList();

        lock (_droppedSubSectors)
        {
            _droppedCallsign = Network.Me?.Callsign ?? _droppedCallsign;

            // Claiming a sector is a fresh decision about everything under it.
            foreach (var sector in toClaim)
                foreach (var covered in PrimaryPosition.CoveredBy(sector))
                    _droppedSubSectors.Remove(covered.Name);

            // A released sector still covered by a group that is kept was dropped out of that
            // group. One covered by nothing kept went with its whole group, and is forgotten.
            foreach (var sector in released)
            {
                var coveredByKept = kept.Any(group => group.Name != sector.Name
                    && PrimaryPosition.CoveredBy(group).Any(c => c.Name == sector.Name));
                if (coveredByKept)
                    _droppedSubSectors.Add(sector.Name);
                else
                    _droppedSubSectors.Remove(sector.Name);
            }
        }
    }

    // Commits one Apply: releases what the controller unpicked, claims what they picked and
    // requests what they staged from another controller. Apply is the confirmation, so unlike
    // ClaimAsync's interactive path (HandleConflictAsync) nothing here stops to ask.
    //
    // Owned is refreshed once for the whole batch, not once per sector.
    public async Task<SectorCommitResult> CommitSectorChangesAsync(
        IReadOnlyList<SectorsVolumes.Sector> toClaim,
        IReadOnlyList<SectorsVolumes.Sector> toRelease,
        IReadOnlyList<SectorsVolumes.Sector>? toRequest = null)
    {
        var result = new SectorCommitResult();
        if (!Network.IsConnected)
            return result;

        // An observer may not take ownership - see IsRealAtc. Release and request are left alone:
        // giving something back or asking for something is not what that rule guards against.
        if (!IsRealAtc && toClaim.Count > 0)
        {
            OzErrors.Add(new Exception(
                "Not connected as a real ATC position - sectors can't be claimed while observing."),
                "OzServer");
            result.Failed.AddRange(toClaim.Select(s => s.Name));
            toClaim = Array.Empty<SectorsVolumes.Sector>();
        }

        // A flow position never claims enroute or TMA airspace - see PrimaryPosition.MayOwn. What
        // another controller holds it may ask for as relief cover, so a staged claim on one of those
        // goes as a request instead; what nobody holds it cannot take at all. The backend enforces
        // both; this converts the first and explains the second.
        var ownCallsign = Network.Me?.Callsign;
        var notFlow = toClaim.Where(s => !PrimaryPosition.MayOwn(s, ownCallsign) && !IsMine(s)).ToList();
        if (notFlow.Count > 0)
        {
            var held = notFlow.Where(s => _controlled.ContainsKey(s.Name)).ToList();
            var unheld = notFlow.Where(s => !_controlled.ContainsKey(s.Name)).ToList();

            toClaim = toClaim.Where(s => !notFlow.Contains(s)).ToList();
            if (held.Count > 0)
                toRequest = (toRequest ?? Array.Empty<SectorsVolumes.Sector>())
                    .Concat(held.Where(s => !(toRequest ?? Array.Empty<SectorsVolumes.Sector>()).Any(r => r.Equals(s))))
                    .ToList();

            if (unheld.Count > 0)
            {
                PrimaryPosition.ReportFlowCannotAssume(unheld, ownCallsign);
                result.Failed.AddRange(unheld.Select(s => s.Name));
            }
        }

        // A request is only for airspace someone holds; for a flow position an unheld one would
        // otherwise fail without a reason.
        if (PrimaryPosition.IsFlowCallsign(ownCallsign) && toRequest != null)
        {
            var unheldRequests = toRequest.Where(s => !PrimaryPosition.MayOwn(s, ownCallsign) && !_controlled.ContainsKey(s.Name)).ToList();
            if (unheldRequests.Count > 0)
            {
                PrimaryPosition.ReportFlowCannotAssume(unheldRequests, ownCallsign);
                result.Failed.AddRange(unheldRequests.Select(s => s.Name));
                toRequest = toRequest.Where(s => !unheldRequests.Contains(s)).ToList();
            }
        }

        NoteWindowDecisions(toClaim, toRelease);

        // Excuses every released sector and the group each takes with it from RestoreOwnedToMmi,
        // for both the batched commit and the per-sector fallback - see MarkReleased.
        MarkReleased(toRelease
            .SelectMany(sector => PrimaryPosition.CoveredBy(sector))
            .Select(sector => sector.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        // One call for the whole Apply, answered with the resulting state. The per-sector path
        // below is the fallback for a backend without /sectors/commit.
        try
        {
            // One exclusion list for the whole batch: the union of what each claimed sector would
            // sweep up that somebody is on. A staffed sector may be taken by none of the claims.
            var staffed = toClaim
                .SelectMany(s => PrimaryPosition.StaffedCoveredSectors(s, Network.Me?.Callsign))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Never a sector the controller explicitly staged: they asked for it by name, and
                // if it is somebody else's the ordinary conflict path answers.
                .Where(name => !toClaim.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var response = await _api.CommitAsync(
                toClaim.Select(s => s.Name),
                toRelease.Select(s => s.Name),
                (toRequest ?? Array.Empty<SectorsVolumes.Sector>()).Select(s => s.Name),
                staffed,
                // Staged from a dropped controller after the window asked (ConfirmTakeDroppedAsync).
                toClaim.Any(CoversAgreedDropped));

            result.Claimed.AddRange(response.Result.Claimed);
            result.Released.AddRange(response.Result.Released);
            result.Requested.AddRange(response.Result.Requested);
            result.Skipped.AddRange(response.Result.Skipped);
            result.Failed.AddRange(response.Result.Failed);

            if (response.Result.Withheld.Count > 0)
            {
                ActionLog.Log("Ownership",
                    $"Apply left {string.Join(", ", response.Result.Withheld.Distinct())} with the controller logged on as that position",
                    new
                    {
                        action = "commit_withheld",
                        withheld = response.Result.Withheld.Distinct().ToArray(),
                        claimed = response.Result.Claimed.ToArray(),
                        own_callsign = Network.Me?.Callsign
                    });
            }

            if (response.Sync != null)
                await ApplySyncGatedAsync(response.Sync);
            else
                await RefreshFromServerAsync();

            return result;
        }
        catch (OzServerApiException ex) when (ex.StatusCode == 404)
        {
            // Backend without the batched endpoint - fall through to the per-sector calls.
            result.Claimed.Clear();
            result.Released.Clear();
            result.Requested.Clear();
            result.Skipped.Clear();
            result.Failed.Clear();
        }

        // Releases first: a sector being handed back may be part of a group being claimed in the
        // same Apply, and releasing after the claim would undo it.
        foreach (var sector in toRelease)
        {
            try
            {
                await _api.ReleaseSectorAsync(sector.Name);
                result.Released.Add(sector.Name);
                ActionLog.Log("Ownership", $"Released {sector.Name}");
            }
            catch (Exception ex)
            {
                result.Failed.Add(sector.Name);
                OzErrors.Add(new Exception($"Couldn't release {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        foreach (var sector in toClaim)
        {
            IReadOnlyList<OzServerSectorConflictDto>? conflicts = null;

            try
            {
                await ClaimWithExclusionsAsync(sector);
                result.Claimed.Add(sector.Name);
                ActionLog.Log("Ownership", $"Claimed {sector.Name}");
            }
            catch (OzServerApiException ex) when (ex.StatusCode == 409 && ex.Conflicts.Count > 0)
            {
                // Handled after the try - see ClaimAsync.
                conflicts = ex.Conflicts;
            }
            catch (Exception ex)
            {
                result.Failed.Add(sector.Name);
                OzErrors.Add(new Exception($"Couldn't claim {sector.Name}: {ex.Message}", ex), "OzServer");
            }

            if (conflicts == null)
                continue;

            // The contested sub-sectors are deliberately not requested. A claim covers the sector
            // plus everything it is responsible for, so one staged sector can collide on several
            // sub-sectors it only covers. Requesting each would raise requests for sectors the
            // controller never asked for, and accepting any one would hand over that sector's own
            // covered group in turn. In an Apply a request is only what the controller staged
            // (OzServerSectorsWindow.StageSectorChange); the contested pieces stay with their owner
            // and are reported.
            foreach (var conflict in conflicts)
                result.Skipped.Add(conflict.Sector);

            // Then take the rest of the group, which a claim that collided on one sub-sector would
            // otherwise leave unclaimed even though it is free.
            try
            {
                await ClaimWithExclusionsAsync(sector, conflicts.Select(c => c.Sector));
                result.Claimed.Add(sector.Name);
                ActionLog.Log("Ownership", $"Claimed {sector.Name} (excluding {string.Join(", ", conflicts.Select(c => c.Sector))})");
            }
            catch (Exception ex)
            {
                OzErrors.Add(new Exception($"Couldn't claim the rest of {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        // Owned is re-derived once, at the end, which also pushes the whole result into
        // MMI.SectorsControlled through ReconcileMmiWithOwned.
        await RefreshFromServerAsync();
        return result;
    }

    public async Task ReleaseAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected)
            return;

        MarkGroupReleased(sector);

        try
        {
            await _api.ReleaseSectorAsync(sector.Name);
            ActionLog.Log("Ownership", $"Released {sector.Name}");
            // The backend releases the named sector's whole covered group, which can hold this
            // session's own position: relinquishing TRT to BN-TRT_CTR released TRS with it, and
            // left BN-TRS_CTR logged on to a position OzServer no longer recorded as theirs.
            await ReclaimOwnPositionAfterReleaseAsync(sector);
        }
        catch (Exception ex)
        {
            OzErrors.Add(new Exception(ex.Message, ex), "OzServer");
        }

        await RefreshFromServerAsync();
    }

    // Gives a sector back because its VSCS line stopped transmitting - the mirror of
    // AfvSectorClaimer's claim-on-transmit, which calls this explicitly. The MMI-driven path is
    // claim-only, so without this, dropping a line to Idle would take the sector off the scope
    // (AfvSectorClaimer.CheckActive) while OzServer went on recording it as this controller's and
    // unavailable to everybody else.
    //
    // Releasing by name covers the whole group: the backend's releaseGroup releases the named
    // sector plus every sector it is responsible for that this controller owns, which is the set
    // CheckActive takes out of MMI.
    //
    // Differs from ReleaseAsync in two ways:
    //
    //  - 403 is an ordinary answer and stays out of the Errors window. A line can be dropped to
    //    Idle for a sector OzServer never recorded as this controller's: cover withheld on login,
    //    a claim that lost a conflict, or a sector another path released a moment ago.
    //  - Owned is deliberately not checked first. It trails the server by a round trip, so a line
    //    pressed to Transmit and back to Idle inside that window would skip the release.
    public async Task ReleaseIdleVscsLineAsync(SectorsVolumes.Sector sector)
    {
        // Same gates as the claim path: not offline, not on a server OzServer does not operate on,
        // and not an observer.
        if (!CanSync || !IsRealAtc)
            return;

        // Before the call, so the whole round trip is covered, and for the whole group, which is
        // what both CheckActive and the backend take - see MarkGroupReleased.
        MarkGroupReleased(sector);

        try
        {
            await _api.ReleaseSectorAsync(sector.Name);
        }
        catch (OzServerApiException ex) when (ex.StatusCode == 403)
        {
            // Not this controller's to give back: nothing changed, so there is nothing to refresh.
            // Logged so the idle press can still be traced.
            ActionLog.Log("Ownership", $"VSCS line for {sector.Name} went idle, but OzServer doesn't record it as ours", new
            {
                action = "vscs_idle_release_not_owner",
                sector = sector.Name,
                own_callsign = Network.Me?.Callsign
            });
            return;
        }
        catch (Exception ex)
        {
            // Throttled rather than raised per press: a backend that is down answers every one the
            // same way.
            ErrorThrottle.Report("vscs-release:" + sector.Name,
                $"Couldn't release {sector.Name} on OzServer: {ex.Message}", ex);
            return;
        }

        ActionLog.Log("Ownership", $"Released {sector.Name} - its VSCS line is no longer transmitting", new
        {
            action = "vscs_idle_release",
            sector = sector.Name,
            own_callsign = Network.Me?.Callsign
        });

        await ReclaimOwnPositionAfterReleaseAsync(sector);
        await RefreshFromServerAsync();
    }

    // A release gives back the named sector's whole covered group, which can contain this
    // session's own position: a controller logged on as a sub-sector (SNO) who extended into its
    // parent group (WOL) and then drops WOL's line releases SNO with it, and would be left on a
    // position OzServer no longer records as theirs.
    //
    // The login sector is taken straight back, before the refresh, so Owned is only read once the
    // position is restored. Only the login sector, not everything held by right
    // (DefaultSectorsForCurrentSession): claiming a sector re-creates its whole group, so the
    // broader set would bring back sub-sectors this controller had dropped on purpose, including
    // the one just released.
    async Task ReclaimOwnPositionAfterReleaseAsync(SectorsVolumes.Sector released)
    {
        var ownSector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == Network.Me?.Callsign);
        if (ownSector == null || ownSector.Equals(released))
            return;

        // CoveredBy mirrors the backend's covered(), which is what releaseGroup released.
        if (!PrimaryPosition.CoveredBy(released).Any(covered => covered.Equals(ownSector)))
            return;

        try
        {
            await ClaimWithExclusionsAsync(ownSector);
            // Kept after all, so no longer excused: whatever of the position's own group the
            // dropped line took off the scope should come back now, not when the excuse expires.
            UnmarkReleased(PrimaryPosition.CoveredBy(ownSector).Select(covered => covered.Name));
            ActionLog.Log("Ownership",
                $"Took {ownSector.Name} back - releasing {released.Name} covered this session's own position", new
                {
                    action = "release_keep_own_position",
                    sector = ownSector.Name,
                    released = released.Name,
                    own_callsign = Network.Me?.Callsign
                });
        }
        catch (Exception ex)
        {
            // Reported unthrottled: the controller is left on a position OzServer does not record as
            // theirs.
            OzErrors.Add(new Exception(
                $"Released {released.Name}, but couldn't take {ownSector.Name} back: {ex.Message}", ex), "OzServer");
        }
    }

    public async Task AcceptRequestAsync(int requestId)
    {
        if (!Network.IsConnected)
            return;

        await _api.AcceptRequestAsync(requestId);
        ActionLog.Log("Ownership", $"Accepted request #{requestId}");
        await RefreshFromServerAsync();
    }

    // Applies the state an action's response carried, or refreshes if it carried none. Public so
    // the window's reject and cancel paths share the fallback.
    public async Task ApplyActionResultAsync(OzServerActionResultDto? result)
    {
        if (result?.Sync != null)
            await ApplySyncGatedAsync(result.Sync);
        else
            await RefreshFromServerAsync();
    }

    // Preferred over AcceptRequestAsync per id when accepting several at once - see
    // OzServerApiClient.AcceptRequestsBatchAsync. Returns the per-request results so the caller
    // can report anything that did not go through.
    public async Task<List<OzServerAcceptBatchResultDto>> AcceptRequestsBatchAsync(IEnumerable<int> requestIds)
    {
        if (!Network.IsConnected)
            return new List<OzServerAcceptBatchResultDto>();

        var response = await _api.AcceptRequestsBatchAsync(requestIds);
        foreach (var result in response.Results)
            ActionLog.Log("Ownership", $"Accepted request #{result.RequestId} ({result.Sector}): {(result.Accepted ? "ok" : result.Message)}");

        // The response carries the resulting state; the refresh is the fallback for an older
        // backend that answers without it.
        if (response.Sync != null)
            await ApplySyncGatedAsync(response.Sync);
        else
            await RefreshFromServerAsync();

        return response.Results;
    }

    // Mirrors AcceptRequestsBatchAsync. Declining a grouped request is one call, so the group is
    // refused as the single decision it was made as rather than left partially answered.
    public async Task<List<int>> RejectRequestsBatchAsync(IEnumerable<int> requestIds)
    {
        if (!Network.IsConnected)
            return new List<int>();

        var response = await _api.RejectRequestsBatchAsync(requestIds);
        foreach (var id in response.Rejected)
            ActionLog.Log("Ownership", $"Rejected request #{id}");

        if (response.Sync != null)
            await ApplySyncGatedAsync(response.Sync);
        else
            await RefreshFromServerAsync();

        return response.Rejected;
    }

    // MMI.SectorsControlled changes through paths nothing OzServer-aware drives: a logon under a
    // position's callsign, vatSys's own Sectors window, a VSCS transmit (AfvSectorClaimer), another
    // plugin. This claims whatever is newly controlled, then refreshes Owned once. It never
    // releases - see the class comment.
    //
    // A sector already in Owned is skipped, not re-claimed. A claim recreates the sector's whole
    // covered group with no memory of earlier exclusions, and MMI.SectorsControlledChanged fires
    // for reasons unrelated to any one sector, so re-claiming an owned primary would bring back a
    // sub-sector just released, or one left with another controller after a conflict.
    //
    // A sector not yet owned is also skipped when its primary is in the same MMI snapshot. Claiming
    // the primary (WON) already covers its sub-sectors (HUO, LTA, HBA), and claiming one of them
    // separately would bypass whatever exclusion the primary's conflict handling carved out for
    // it. A sub-sector present without its primary (a logon directly as SNO) is claimed on its own.
    async Task ClaimMmiControlledSectorsAsync()
    {
        // An observer can end up with entries in MMI.SectorsControlled but may not own them - see
        // IsRealAtc.
        if (!IsRealAtc)
            return;

        var target = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        foreach (var sector in target)
        {
            // Shared with RetryUnclaimedMmiSectors, so the two cannot disagree about whether there
            // is anything to do.
            if (!NeedsClaim(sector, target))
                continue;

            // The snapshot is a round trip older for every sector already claimed in this pass.
            // Only what is still controlled is claimed.
            if (!MMI.SectorsControlled.Any(s => !s.IsDummy && s.Equals(sector)))
                continue;

            // Handled after the try - see ClaimAsync. Thrown from the catch clause, a failure
            // would abort the loop and skip every sector after this one.
            IReadOnlyList<OzServerSectorConflictDto>? conflicts = null;

            try
            {
                var claimed = await ClaimWithExclusionsAsync(sector);
                if (claimed != null && !claimed.Contains(sector.Name, StringComparer.OrdinalIgnoreCase))
                    MarkWithheld(sector, claimed);
            }
            catch (OzServerApiException ex) when (ex.StatusCode == 409 && ex.Conflicts.Count > 0)
            {
                conflicts = ex.Conflicts;
            }
            catch (Exception ex)
            {
                // RetryUnclaimedMmiSectors comes back here every poll tick until the claim lands.
                ErrorThrottle.Report("mmi-claim:" + sector.Name, $"Couldn't sync {sector.Name} to OzServer: {ex.Message}", ex);
            }

            if (conflicts == null)
                continue;

            try
            {
                await HandleConflictAsync(sector, conflicts);
            }
            catch (Exception ex)
            {
                OzErrors.Add(new Exception($"Couldn't resolve the ownership conflict on {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // One or more of the sectors a claim for `sector` covers is owned by someone else - GUN
    // extending into WOL while BLA owns WOL's sub-sector SNO, say. Requests what should be
    // requested, then claims whatever the conflict left uncontested. The contested sectors are
    // pulled back out of MMI.SectorsControlled and their VSCS lines taken off Transmit:
    // AfvSectorClaimer.CheckActive adds a primary's whole group to MMI before any of this is known,
    // and a sector must not sit there as if owned when OzServer disagrees.
    async Task HandleConflictAsync(SectorsVolumes.Sector sector, IReadOnlyList<OzServerSectorConflictDto> conflicts)
    {
        if (OzServerMode.Disabled)
            return;

        // Requested once the controller agrees (ConfirmOwnPositionRequestAsync):
        //
        //   - This session's own position. It is requested, never taken: the controller working the
        //     airspace decides when it, and the aircraft in it, move.
        //   - Anything else the same holder has that a requested sector covers. Accepting BLA hands
        //     over its whole covered group (the backend's transferRequest), so it is asked about as
        //     part of the position, not separately.
        //
        // Never requested here:
        //
        //   - Anything an FMP holds. That is relief cover its controller agreed to (see
        //     IsReliefCovered), and ending it is the FMP's decision.
        //   - Anything a transmit press is already asking about (IsHeldForTransmitPrompt).
        //
        // Both are still left out of the claim below, like any conflict.
        //   - A sector held by the controller logged on as it (the sector's own callsign - vatSys's
        //     rule for giving a sector to its controller). It is theirs, not cover to take back:
        //     BLA logging on while ML_APP was online asked ML_APP for MAE, which BLA only works
        //     top-down when ML_APP is away.
        //   - Anything held by a controller without the plugin (Owner.External): nobody can answer.
        //     The controller is told to coordinate it by hand instead (ExternalControl.Notify).
        //   - Anything held for a controller whose connection dropped (Owner.Dropped): nobody can
        //     answer that either. The controller is asked whether to take it now instead
        //     (ConfirmTakeDroppedAsync), and it is claimed with the rest if they say so.
        var dropped = conflicts.Where(c => c.Owner?.Dropped == true).ToList();
        var takeDropped = dropped.Count > 0 && await ConfirmTakeDroppedAsync(dropped);
        // What the claim below leaves out and what is pulled back out of MMI: every conflict, less
        // the dropped controller's sectors once this controller has agreed to take them.
        IReadOnlyList<OzServerSectorConflictDto> leaveOut = takeDropped
            ? conflicts.Where(c => !dropped.Contains(c)).ToList()
            : conflicts;
        conflicts = conflicts.Where(c => !dropped.Contains(c)).ToList();

        var external = conflicts.Where(c => c.Owner?.External == true).ToList();
        foreach (var holder in external.GroupBy(c => c.Owner!.Callsign, StringComparer.OrdinalIgnoreCase))
            ExternalControl.Notify(holder.Select(c => c.Sector), holder.Key);

        var reliefCover = conflicts.Where(c => c.Owner?.External == true
                                               || PrimaryPosition.IsFlowCallsign(c.Owner?.Callsign)
                                               || IsHeldForTransmitPrompt(c.Sector)
                                               || HeldByItsOwnController(c)).ToList();
        if (reliefCover.Count > 0)
            ActionLog.Log("Ownership",
                $"Left {string.Join(", ", reliefCover.Select(c => c.Sector))} with {string.Join(", ", reliefCover.Select(c => c.Owner?.Callsign).Distinct())} - their own position, FMP relief cover, or already being asked about from a transmit press",
                new
                {
                    action = "conflict_left_with_flow_relief",
                    sectors = reliefCover.Select(c => c.Sector).ToArray(),
                    own_callsign = Network.Me?.Callsign
                });

        var myDefaults = PrimaryPosition.DefaultSectorsFor(Network.Me?.Callsign);
        var mineByRight = conflicts.Where(c => !reliefCover.Contains(c) && myDefaults.Any(s => s.Name == c.Sector)).ToList();
        var carriedWith = conflicts
            .Where(c => !mineByRight.Contains(c) && !reliefCover.Contains(c))
            .Where(c => mineByRight.Any(own => own.Owner?.Cid == c.Owner?.Cid && Covers(own.Sector, c.Sector)))
            .ToList();
        var ownPosition = mineByRight.Concat(carriedWith).ToList();
        var contestedByOthers = conflicts.Where(c => !ownPosition.Contains(c) && !reliefCover.Contains(c)).ToList();

        var asked = new List<OzServerSectorConflictDto>();
        if (ownPosition.Count > 0 && await ConfirmOwnPositionRequestAsync(ownPosition))
            asked.AddRange(ownPosition);

        // Laid out lead / list / question like every other popup in the plugin, with each sector
        // written out in full (SectorDescription). The message covers the whole of the sector's
        // status, not only the contested part, so a transmit press on a partly held primary shows
        // which part was taken and which was not.
        var described = contestedByOthers
            .Select(conflict => SectorDescription.DescribeWithOwner(conflict.Sector, conflict.Owner?.Callsign))
            .ToList();

        var contestedNames = new HashSet<string>(leaveOut.Select(c => c.Sector), StringComparer.OrdinalIgnoreCase);
        var staffed = new HashSet<string>(
            PrimaryPosition.StaffedCoveredSectors(sector, Network.Me?.Callsign), StringComparer.OrdinalIgnoreCase);
        var taking = PrimaryPosition.CoveredBy(sector)
            .Where(s => !contestedNames.Contains(s.Name) && !staffed.Contains(s.Name) && !IsMine(s))
            .Select(s => SectorDescription.Describe(s))
            .ToList();

        var nl = Environment.NewLine;
        var question = (taking.Count > 0
                           ? $"{SectorDescription.Describe(sector)} is partly owned by another controller."
                           : $"{SectorDescription.Describe(sector)} is owned by another controller.")
                       + nl + nl
                       + "Owned:" + nl + string.Join(nl, described)
                       + (taking.Count > 0
                           ? nl + nl + "Available - taken by you now:" + nl + string.Join(nl, taking)
                           : "")
                       + nl + nl
                       + (described.Count == 1
                           ? "Send a request for the owned sector?"
                           : "Send a request for the owned sectors?");

        if (contestedByOthers.Count > 0 && await AskYesNoAsync(question, "Sector already owned"))
            asked.AddRange(contestedByOthers);

        await RequestAsOneGroupAsync(asked);

        // Only when the conflict left something to take. Logging on to a position somebody present
        // holds contests all of it, and a retry that excludes every sector the claim covers has
        // nothing to do.
        var leftOut = new HashSet<string>(
            leaveOut.Select(c => c.Sector).Concat(PrimaryPosition.StaffedCoveredSectors(sector, Network.Me?.Callsign)),
            StringComparer.OrdinalIgnoreCase);

        if (PrimaryPosition.CoveredBy(sector).Any(covered => !leftOut.Contains(covered.Name)))
        {
            try
            {
                await ClaimWithExclusionsAsync(sector, leaveOut.Select(c => c.Sector));
            }
            catch (Exception ex)
            {
                OzErrors.Add(new Exception($"Couldn't claim {sector.Name} on OzServer: {ex.Message}", ex), "OzServer");
            }
        }

        RunOnUiThread(() =>
        {
            var contested = leaveOut
                .Select(c => SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == c.Sector))
                .Where(s => s != null)
                .Select(s => s!)
                .ToList();

            var remaining = MMI.SectorsControlled.Where(s => !s.IsDummy && !contested.Any(c => c.Equals(s))).ToList();
            if (remaining.Count != MMI.SectorsControlled.Count(s => !s.IsDummy))
                MMI.SetControlledSectors(remaining);

            foreach (var contestedSector in contested)
            {
                var freq = Audio.VSCSFrequencies.FirstOrDefault(f => f.Name == contestedSector.Callsign);
                if (freq is { Transmit: true })
                    freq.Transmit = false;
            }
        });
    }

    // Sectors held for a controller whose connection dropped (Owner.Dropped) that this controller has
    // agreed to take, each until the server would have freed it anyway (ReleasedAt). Any claim
    // covering one is sent with takeDropped. And the prompts open and answered No, by the sorted
    // sector list, so the claim pass's retries neither stack nor repeat them. Cleared on
    // disconnect.
    readonly Dictionary<string, DateTime> _takeDroppedAgreed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _takeDroppedAsking = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _takeDroppedDeclined = new(StringComparer.OrdinalIgnoreCase);
    readonly object _takeDroppedGate = new();

    public bool AgreedToTakeDropped(string sectorName)
    {
        lock (_takeDroppedGate)
            return _takeDroppedAgreed.TryGetValue(sectorName, out var until) && DateTime.UtcNow < until;
    }

    bool CoversAgreedDropped(SectorsVolumes.Sector sector) =>
        PrimaryPosition.CoveredBy(sector).Any(s => AgreedToTakeDropped(s.Name));

    // A controller dropped without releasing, and OzServer keeps their sectors for them for a few
    // minutes in case they come straight back (the API's DISCONNECT_GRACE_MINUTES). They can still
    // be taken - a crash should not leave airspace unworkable - but only once the controller taking
    // them has been told whose they are and until when, since the dropped controller may be back
    // any moment expecting them. Answers whether to take them.
    //
    // askAgain is for the controller's own explicit actions (a transmit press, the Sectors window),
    // which ask even after an earlier No; the claim pass's automatic retries do not.
    public async Task<bool> ConfirmTakeDroppedAsync(IReadOnlyList<OzServerSectorConflictDto> held, bool askAgain = false)
    {
        held = held.Where(c => c.Owner?.Dropped == true).ToList();
        var names = held.Select(c => c.Sector).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            return false;
        if (names.All(AgreedToTakeDropped))
            return true;

        var key = string.Join(",", names);
        lock (_takeDroppedGate)
        {
            if ((!askAgain && _takeDroppedDeclined.Contains(key)) || !_takeDroppedAsking.Add(key))
                return false;
        }

        try
        {
            var owners = held.Select(c => c.Owner!)
                .GroupBy(o => o.Callsign, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var who = string.Join(", ", owners.Select(o => o.Callsign));
            var lastSeen = owners.Where(o => o.LastSeenAt != null).Select(o => o.LastSeenAt!.Value.ToUniversalTime()).DefaultIfEmpty().Max();
            var until = owners.Where(o => o.ReleasedAt != null).Select(o => o.ReleasedAt!.Value.ToUniversalTime()).DefaultIfEmpty().Max();
            if (until == default)
                until = DateTime.UtcNow.AddMinutes(2);

            var nl = Environment.NewLine;
            var droppedAt = lastSeen == default
                ? ""
                : $" at {lastSeen:HH:mm}Z ({Math.Max(0, (int)(DateTime.UtcNow - lastSeen).TotalMinutes)} min ago)";
            var message = (names.Count == 1 ? "This sector is" : "These sectors are")
                          + $" held for {who}, who disconnected without releasing{droppedAt}."
                          + nl + nl
                          + "Held:" + nl
                          + string.Join(nl, held.Select(c => SectorDescription.DescribeWithOwner(c.Sector, c.Owner?.Callsign)))
                          + nl + nl
                          + $"They are kept for {who} until {until:HH:mm}Z in case they reconnect, but can be taken now if required."
                          + $" If {who} comes back, coordinate with them."
                          + nl + nl
                          + (names.Count == 1 ? "Take this sector now?" : "Take these sectors now?");

            var answer = await AskYesNoAsync(message, "Sectors held for a disconnected controller", "Take Sectors", "Leave");

            ActionLog.Log("Ownership",
                answer
                    ? $"Taking {key} from {who} - disconnected, held until {until:HH:mm}Z"
                    : $"Leaving {key} with {who} - disconnected, held until {until:HH:mm}Z",
                new
                {
                    action = answer ? "take_dropped_accepted" : "take_dropped_declined",
                    sectors = names.ToArray(),
                    owners = owners.Select(o => o.Callsign).ToArray(),
                    last_seen_at = lastSeen == default ? (DateTime?)null : lastSeen,
                    released_at = until,
                    own_callsign = Network.Me?.Callsign
                });

            lock (_takeDroppedGate)
            {
                if (answer)
                {
                    foreach (var name in names)
                        _takeDroppedAgreed[name] = until;
                    _takeDroppedDeclined.Remove(key);
                }
                else
                {
                    _takeDroppedDeclined.Add(key);
                }
            }

            return answer;
        }
        finally
        {
            lock (_takeDroppedGate)
                _takeDroppedAsking.Remove(key);
        }
    }

    // Own-position prompts, by the sorted sector list they ask about: open right now, and answered
    // No this session. The claim pass reaches HandleConflictAsync again on every retry while the
    // position stays contested (RetryUnclaimedMmiSectors), and must not stack prompts or ask again
    // after a No. Cleared on disconnect. Guarded by themselves: the claim pass and an interactive
    // ClaimAsync can both get here.
    readonly HashSet<string> _ownPositionAsking = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _ownPositionDeclined = new(StringComparer.OrdinalIgnoreCase);

    // Asks before requesting this session's own position from whoever holds it. It used to be
    // requested on logon without a word: ML-ASP_CTR connected and ML-HYD_CTR was asked for ASP and
    // its six sub-sectors before ASP had chosen to take them. The holder still decides; this lets the
    // controller logging on decide whether to ask.
    async Task<bool> ConfirmOwnPositionRequestAsync(List<OzServerSectorConflictDto> ownPosition)
    {
        var names = ownPosition.Select(c => c.Sector).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var key = string.Join(",", names);

        // Already asked for and still pending: RequestAsOneGroupAsync skips those anyway.
        var pending = new HashSet<string>(
            MyRequests.ByMe.Where(r => r.RejectedAt == null && r.Sector != null).Select(r => r.Sector!.Name),
            StringComparer.OrdinalIgnoreCase);
        if (names.All(pending.Contains))
            return true;

        lock (_ownPositionDeclined)
        {
            if (_ownPositionDeclined.Contains(key))
                return false;
        }
        lock (_ownPositionAsking)
        {
            if (!_ownPositionAsking.Add(key))
                return false;
        }

        try
        {
            var sectors = names
                .Select(name => SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == name))
                .Where(s => s != null)
                .Select(s => s!)
                .ToList();
            // The logon sector if it is among them, otherwise the one covering the rest.
            var primary = sectors.FirstOrDefault(s => string.Equals(s.Callsign, Network.Me?.Callsign, StringComparison.OrdinalIgnoreCase))
                          ?? sectors.FirstOrDefault(s => sectors.All(other => other.Equals(s) || PrimaryPosition.CoveredBy(s).Any(c => c.Equals(other))))
                          ?? sectors.FirstOrDefault();
            var primaryName = primary?.Name ?? names.First();
            var owners = string.Join(", ", ownPosition.Select(c => c.Owner?.Callsign)
                .Where(callsign => !string.IsNullOrEmpty(callsign))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (owners.Length == 0)
                owners = "another controller";

            var subSectors = ownPosition.Where(c => !string.Equals(c.Sector, primaryName, StringComparison.OrdinalIgnoreCase)).ToList();
            var nl = Environment.NewLine;
            var message = (subSectors.Count > 0
                              ? $"{primaryName} and its sub-sectors are owned by {owners}."
                              : $"{primaryName} is owned by {owners}.")
                          + nl + nl
                          + "Owned:" + nl
                          + string.Join(nl, ownPosition.Select(c => SectorDescription.DescribeWithOwner(c.Sector, c.Owner?.Callsign)))
                          + nl + nl
                          + $"Request {primaryName} Sector from {owners}?";

            var answer = await AskYesNoAsync(message, "Your position is owned", "Request Sectors", "No");

            ActionLog.Log("Ownership",
                answer
                    ? $"Requesting {primaryName} from {owners} - confirmed"
                    : $"Not requesting {primaryName} from {owners} - declined",
                new
                {
                    action = answer ? "own_position_request_confirmed" : "own_position_request_declined",
                    sector = primaryName,
                    sectors = names.ToArray(),
                    owners,
                    own_callsign = Network.Me?.Callsign
                });

            if (!answer)
            {
                lock (_ownPositionDeclined)
                    _ownPositionDeclined.Add(key);
            }
            return answer;
        }
        finally
        {
            lock (_ownPositionAsking)
                _ownPositionAsking.Remove(key);
        }
    }

    static bool HeldByItsOwnController(OzServerSectorConflictDto conflict)
    {
        var owner = conflict.Owner?.Callsign;
        if (string.IsNullOrEmpty(owner))
            return false;
        var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == conflict.Sector);
        return sector != null && string.Equals(sector.Callsign, owner, StringComparison.OrdinalIgnoreCase);
    }

    static bool Covers(string root, string name)
    {
        var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == root);
        return sector != null && PrimaryPosition.CoveredBy(sector).Any(covered => covered.Name == name);
    }

    // Requests every sector as one group, so each holder is asked once about the whole thing rather
    // than once per sector - as an Apply in the Sectors window does.
    //
    // Skips anything already requested and still pending. The conflict can recur while the request
    // waits (another VSCS press, a re-grant into MMI from another plugin), and the backend refuses
    // a second request for the same sector from the same requester.
    async Task RequestAsOneGroupAsync(IEnumerable<OzServerSectorConflictDto> conflicts)
    {
        var pending = new HashSet<string>(
            MyRequests.ByMe.Where(r => r.RejectedAt == null && r.Sector != null).Select(r => r.Sector!.Name),
            StringComparer.OrdinalIgnoreCase);

        var names = conflicts
            .Select(c => c.Sector)
            .Where(name => !pending.Contains(name))
            .Where(name => SectorsVolumes.Sectors.Any(s => s.Name == name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            return;

        var owners = string.Join(", ", conflicts
            .Where(c => names.Contains(c.Sector, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Owner?.Callsign)
            .Where(callsign => !string.IsNullOrEmpty(callsign))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        try
        {
            var response = await _api.CommitAsync(
                Array.Empty<string>(), Array.Empty<string>(), names, Array.Empty<string>());

            ActionLog.Log("Request", $"Requested {string.Join(", ", response.Result.Requested)} from {owners}"
                                     + (response.Result.Failed.Count > 0 ? $" (couldn't request {string.Join(", ", response.Result.Failed)})" : ""),
                new
                {
                    action = "request_contested_group",
                    requested = response.Result.Requested.ToArray(),
                    failed = response.Result.Failed.ToArray(),
                    owners,
                    own_callsign = Network.Me?.Callsign
                });

            // The sync in the response is not applied here: every path into HandleConflictAsync
            // ends with its own refresh.
        }
        catch (Exception ex)
        {
            OzErrors.Add(new Exception($"Couldn't request {string.Join(", ", names)} on OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    // Requests the sector from its holder, after a transmit press the controller has confirmed
    // (AfvSectorClaimer.PromptToRequestHeldSector). The holder gets an ordinary request, marked as
    // FMP relief cover in their Sectors window when this is a flow position. True once the request
    // is on the server, or already was.
    public async Task<bool> RequestHeldSectorAsync(SectorsVolumes.Sector sector)
    {
        if (IsMine(sector) || !_controlled.TryGetValue(sector.Name, out var owner))
            return false;

        if (HasPendingRequestFor(sector))
            return true;

        var flow = PrimaryPosition.IsFlowCallsign(Network.Me?.Callsign);

        try
        {
            var response = await _api.CommitAsync(
                Array.Empty<string>(), Array.Empty<string>(), new[] { sector.Name }, Array.Empty<string>());

            var raised = response.Result.Requested.Contains(sector.Name, StringComparer.OrdinalIgnoreCase);
            ActionLog.Log("Request", $"Requested {sector.Name} from {owner.Callsign}"
                                     + (flow ? " as FMP relief cover" : "")
                                     + (raised ? "" : " - the request could not be raised"),
                new
                {
                    action = flow ? "request_flow_relief_cover" : "request_from_transmit",
                    sector = sector.Name,
                    owner = owner.Callsign,
                    requested = response.Result.Requested.ToArray(),
                    failed = response.Result.Failed.ToArray(),
                    own_callsign = Network.Me?.Callsign
                });

            if (response.Sync != null)
                await ApplySyncGatedAsync(response.Sync);
            else
                await RefreshFromServerAsync();

            if (!raised)
                OzErrors.Add(new Exception($"Couldn't request {sector.Name} from {owner.Callsign}."), "OzServer");
            return raised;
        }
        catch (Exception ex)
        {
            OzErrors.Add(new Exception($"Couldn't request {sector.Name} from {owner.Callsign}: {ex.Message}", ex), "OzServer");
            return false;
        }
    }

    public bool HasPendingRequestFor(SectorsVolumes.Sector sector) =>
        MyRequests.ByMe.Any(r => r.RejectedAt == null && r.Sector != null
                                 && string.Equals(r.Sector.Name, sector.Name, StringComparison.OrdinalIgnoreCase));

    // Sectors a transmit press is asking about (AfvSectorClaimer.PromptToRequestHeldSector), left
    // out of the claim pass meanwhile: the press can put them in MMI (VatpacPlugin does) before the
    // prompt takes them out again, and claiming them would raise a second question about the same
    // sector. Long enough to cover the prompt and a request being answered.
    static readonly TimeSpan TransmitPromptHold = TimeSpan.FromSeconds(120);
    readonly Dictionary<string, DateTime> _transmitPromptHold = new(StringComparer.OrdinalIgnoreCase);

    public void HoldForTransmitPrompt(IEnumerable<string> names)
    {
        lock (_withheldGate)
            foreach (var name in names)
                _transmitPromptHold[name] = DateTime.UtcNow + TransmitPromptHold;
    }

    public void ReleaseTransmitPromptHold(IEnumerable<string> names)
    {
        lock (_withheldGate)
            foreach (var name in names)
                _transmitPromptHold.Remove(name);
    }

    bool IsHeldForTransmitPrompt(string name)
    {
        lock (_withheldGate)
            return _transmitPromptHold.TryGetValue(name, out var until) && DateTime.UtcNow < until;
    }

    // How long the conflict question waits before it withdraws itself as "no". Nothing else can be
    // claimed while it is open - the claim loop that asked is suspended on the answer and later
    // MMI changes queue behind it. "No" leaves the contested sectors with whoever has them.
    static readonly TimeSpan ConflictPromptTimeout = TimeSpan.FromSeconds(60);

    // Asks without blocking. A modal ShowDialog would stop the whole vatSys window taking input,
    // and this prompt is raised by a claim - a VSCS transmit press or a logon - so it can appear
    // while the controller is working traffic. Like every other popup here (see
    // SectorMessageWindow) it is non-modal, and the caller awaits the answer.
    static Task<bool> AskYesNoAsync(string message, string caption, string yesText = "Accept", string noText = "Reject")
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Show()
        {
            try
            {
                var prompt = new SectorConflictPromptWindow(message, caption, yesText, noText);
                var timeout = new System.Windows.Forms.Timer { Interval = (int)ConflictPromptTimeout.TotalMilliseconds };

                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    if (!prompt.IsDisposed)
                        prompt.Close();
                };

                prompt.FormClosed += (_, _) =>
                {
                    timeout.Stop();
                    timeout.Dispose();
                    // Anything but an explicit Yes - No, the title bar's close, the timeout - is no.
                    answer.TrySetResult(prompt.Answer);
                };

                if (Application.OpenForms["MainForm"] is Form mainForm)
                    prompt.Show(mainForm);
                else
                    prompt.Show();

                prompt.BringToFront();
                timeout.Start();
            }
            catch (Exception ex)
            {
                answer.TrySetException(ex);
            }
        }

        RunOnUiThread(Show);

        // Backstop for the timeout above, which is a WinForms timer and only fires where there is a
        // message loop. RunOnUiThread runs inline when it finds no main form, and a window opened on
        // a pool thread would never tick, leaving the claim loop waiting on an answer indefinitely.
        _ = Task.Delay(ConflictPromptTimeout + TimeSpan.FromSeconds(10))
            .ContinueWith(_ => answer.TrySetResult(false), TaskScheduler.Default);

        return answer.Task;
    }

    // Fire-and-forget marshalling to the UI thread. Runs inline when there is no MainForm to
    // marshal through.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
