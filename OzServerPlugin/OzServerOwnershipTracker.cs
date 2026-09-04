using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// What one Apply actually did, so the window can report it rather than leaving the controller to
// infer it from which rows moved. Sectors are named rather than typed: some entries (a contested
// sub-sector reported by the server) are names the client never resolved to a Sector at all.
public class SectorCommitResult
{
    public List<string> Claimed { get; } = new();
    public List<string> Released { get; } = new();
    public List<string> Requested { get; } = new();
    // Covered sub-sectors another controller already owned, left with them rather than requested -
    // see CommitSectorChangesAsync for why asking for these automatically was wrong.
    public List<string> Skipped { get; } = new();
    public List<string> Failed { get; } = new();
}

// One sector changing hands, and who the other party was - the controller it went to when this
// session lost it, or the one who had it when this session gained it.
//
// The counterparty is the point. "I lost ASW" alone says nothing about what to do with the aircraft
// in it; "I lost ASW to BN-TRT_CTR" is what lets the losing client hand those tags to the right
// controller, and the gaining client recognise the resulting handoffs as an OzServer transfer
// rather than a manual one somebody made by hand.
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

// Keeps OzServer's sector-ownership record in sync with MMI.SectorsControlled, independent of
// whether OzServerSectorsWindow happens to be open. Constructed unconditionally by Plugin (like
// AfvSectorClaimer) rather than lazily: a VSCS/AFV transmit press only ever touches
// MMI.SectorsControlled (see AfvSectorClaimer), same as a login under a position's own callsign or
// the built-in vatSys Sectors window - this is what actually claims it on OzServer, and
// OwnedChanged is what lets OzServerSectorsWindow update immediately when it does, rather than
// waiting on its own next poll tick or the next time it's opened.
//
// The ONLY thing that decides what Owned is - OzServer's own ownership record for this controller
// (SectorOwnershipController::mine), full stop. Every action that can change ownership (claim,
// release, accept, or MMI.SectorsControlled changing) calls the relevant endpoint and then
// re-derives Owned through RefreshFromServerAsync rather than guessing locally what the result
// must have been. That used to be layered with a second opinion - diffing against
// MMI.SectorsControlled and releasing anything Owned showed that MMI didn't - which fought a VSCS
// transmit claim (never reflected in MMI on its own) in a claim/release loop. One source of truth.
//
// Ownership also flows the other way now: whenever Owned actually changes (see
// ReconcileMmiWithOwned), MMI.SectorsControlled and the matching VSCS line are updated to match -
// a sector gained (most notably: someone accepted a request I sent them) is added to
// MMI.SectorsControlled - but its VSCS line is deliberately left alone (issue #5), since putting a
// controller on a frequency is not a side effect accepting a sector should have; a sector lost (I
// accepted an incoming request, giving it away, or released it myself) is removed and its line
// dropped back to Idle, which is releasing something rather than taking it on their behalf.
// Polls independently of OzServerSectorsWindow (RefreshTimer below) specifically so an accepted
// request still reaches this controller's VSCS panel even if they never open that window - the
// other controller's Accept click only changes ownership on the server, with no direct signal to
// this session at all otherwise.
//
// Also tracks everyone else's ownership (_controlled/OwnerOf), not just this
// session's own Owned - unconditional
// for the same reason Owned itself is: anything deciding who should hold a tag needs the full live
// picture of who owns which subsector. Nothing does at the moment - see TagResumeRecovery for what
// remains of tag handling - but the picture is cheap to keep and the rebuild will want it.
public class OzServerOwnershipTracker
{
    // Idle cadence. Nothing is expected to change on its own, so this only has to be often enough
    // that a sector claimed elsewhere shows up in reasonable time.
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    // Cadence while a handoff is actually in flight - this controller is waiting on someone's
    // decision, or owes one. That is exactly the window where reflection latency is felt, and it is
    // short-lived, so it costs nothing the rest of the time. Without it, being told your request was
    // accepted took up to ten seconds with the Sectors window closed.
    static readonly TimeSpan PendingPollInterval = TimeSpan.FromSeconds(2);

    public event EventHandler? OwnedChanged;
    // Raised after every successful refresh, whether or not anything changed.
    // ObserverPositionMirror follows ControlledByOthers - which moves when *other*
    // controllers claim and release - and that never reaches OwnedChanged, since an
    // observer's own Owned is permanently empty.
    public event EventHandler? Refreshed;
    // Callsigns the backend restored to this controller on a reconnect inside the resume window.
    // Consumed by TagResumeRecovery, which brings them back without an acceptance flash - they were
    // already this controller's a moment ago, and the backend has confirmed nobody else took them
    // in the meantime.
    public event EventHandler<IReadOnlyList<string>>? TagsResumed;
    // Fires whenever the set of incoming ("Requested From Me") requests changes - polled here
    // rather than left to OzServerSectorsWindow alone so the notification works even if that window
    // has never been opened this session.
    //
    // Carries the incoming requests themselves, not just how many there are: Plugin shows them in a
    // popup naming the sector and who asked, which a bare count cannot do. Still only raised when
    // the set actually changes, so it fires once per new request rather than once per poll.
    public event EventHandler<IReadOnlyList<OzServerSectorOwnershipRequestDto>>? IncomingRequestsChanged;
    // Fires whenever Owned actually gains or loses a sector, with who it changed hands with.
    // Consumed by SectorTagHandoff, which moves the aircraft in that sector to match.
    public event EventHandler<SectorOwnershipDiff>? OwnershipChanged;

    // The full requests payload from the last sync, for the Sectors window - which renders both
    // directions and needs the rejected rows too, not just a changed/unchanged signal.
    public event EventHandler<OzServerMyRequestsDto>? RequestsChanged;

    public OzServerMyRequestsDto MyRequests { get; private set; } = new();

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _pollTimer;
    // Push channel. The poll timer above stays exactly as it was - this only removes latency, it
    // is never the only thing keeping Owned current. See OzServerEventStream.
    List<SectorsVolumes.Sector> _owned = new();
    // Everyone else's active ownership record, keyed by sector name - the same data
    // OzServerSectorsWindow's "Controlled" pane shows, but kept current unconditionally (like Owned)
    // rather than only while that window happens to be open, since anything deciding tag ownership
    // needs the full picture - who owns what, not just what this session owns - on every FDR/radar update, not just
    // when a controller happens to have the Sectors window up.
    Dictionary<string, OzServerControlledSectorOwnerDto> _controlled = new(StringComparer.OrdinalIgnoreCase);
    // False until RefreshFromServerAsync has run once - the very first result is this session's
    // starting baseline (whatever was already owned from a previous session, claimed elsewhere,
    // etc.), not something *newly* gained just now, so it's recorded without also seizing
    // MMI/VSCS for it - only actual changes from here on do that.
    bool _hasBaseline;
    // Until the first refresh lands, Owned is an empty list that means "not known yet", not "you
    // own nothing" - a distinction OzServerSectorsWindow has to make, because rendering the second
    // reading puts every sector the controller actually owns into Available. See its
    // SyncOwnedFromTracker.
    public bool HasBaseline => _hasBaseline;
    string? _pendingRequestSignature;
    // Which cadence the timer is currently on, so it is only reprogrammed when the answer changes
    // rather than on every sync.
    bool _pollingFast;
    // Coalesces MMI.SectorsControlledChanged - see OnMmiSectorsControlledChanged's own comment for
    // why running ClaimMmiControlledSectorsAsync re-entrantly (a second, overlapping call starting
    // before the first has finished) was a real bug, not just a theoretical one: it raced reads and
    // writes of _owned between the two overlapping passes, which could let a stale, pre-release read
    // of _owned skip the "already mine, don't re-claim" check and re-claim a sector's whole
    // responsible_sectors group moments after part of it had been deliberately released - hitting a
    // genuine conflict if someone else had already picked up the released piece, and popping the
    // "already owned, request it?" dialog in the middle of what should have been a plain release.
    // The same re-entrancy also explains sectors flickering on connect (Init() -> claim -> Owned
    // refresh -> ReconcileMmiWithOwned's own MMI writes re-triggering this event mid-flight) and a
    // request occasionally surviving its own accept (two overlapping accept cascades racing the
    // same request's delete).
    bool _claimRunning;
    bool _claimQueued;
    // Guards the two flags above. They are read and written from the UI thread
    // (OnMmiSectorsControlledChanged) and from wherever RunClaimLoop's continuations happen to
    // resume, which is only the UI thread while RunOnUiThread actually finds a MainForm to marshal
    // through - its fallback runs the handler inline on whatever thread raised the event. Beyond
    // the visibility problem that creates for two plain bools, clearing _claimRunning and testing
    // _claimQueued had to become a single atomic step: done separately, a change arriving between
    // the test and the clear set _claimQueued on a loop that had already decided to stop, and was
    // then never acted on at all - the sector silently never made it to OzServer.
    readonly object _claimGate = new();
    // Serialises the refresh. It is public and driven from six places (this class's own poll timer,
    // Network.Connected, the Sectors window's poll timer and its OnVisibleChanged, and every
    // claim/release/accept), none of which coordinated with each other. Two overlapping runs both
    // read `previous = _owned` before either had written it, so both computed the same gained/lost
    // diff and both pushed it - duplicate MMI.SetControlledSectors writes and duplicate VSCS
    // Transmit toggles for one real change. Serialising means each run sees the previous run's
    // result as its own baseline, which is what ReconcileMmiWithOwned's diff assumes. This is the
    // same hazard the claim loop above already guarded against; this path simply never got it.
    //
    // Two ways through it, because the callers want different things when it is already held:
    // RefreshFromServerAsync waits its turn (an action needs Owned current before it returns),
    // while RefreshFromServerIfIdleAsync gives up (a periodic poll has nothing to add by asking the
    // same question again a moment later, and queueing them just builds a backlog).
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    // A push signal that arrived while a refresh was already running, to be answered as soon as it
    // finishes rather than dropped - see RefreshFromServerIfIdleAsync.
    //
    // Its own lock object rather than the semaphore: SemaphoreSlim takes an internal lock of its
    // own on some runtimes, and locking application state on the same instance is how that turns
    // into contention nobody can see from here.
    readonly object _refreshQueueGate = new();
    bool _refreshQueued;

    // Sectors this session is the primary for that were already owned by someone else at the moment
    // it logged on - see HandleConflictAsync. Keyed by sector name, valued by how many poll ticks
    // are left to keep trying. Bounded rather than infinite: the previous holder might not be
    // running this plugin at all, or might have crashed out still holding the position, and quietly
    // re-POSTing a doomed claim forever is worse than stopping and leaving it to be claimed by hand.
    const int PrimaryClaimRetryTicks = 12; // ~2 minutes at PollInterval
    readonly object _primaryClaimGate = new();
    readonly Dictionary<string, int> _pendingPrimaryClaims = new(StringComparer.OrdinalIgnoreCase);
    bool _primaryClaimRetryRunning;

    public IReadOnlyList<SectorsVolumes.Sector> Owned => _owned;


    // Everyone else's ownership as the window renders it, straight from the last refresh.
    //
    // The window used to GET /sectors/controlled on its own 2s poll as well, which - once this
    // class started fetching the same endpoint unconditionally - meant every tick pulled that
    // response twice. Reading the copy already in hand costs nothing and halves the traffic on the
    // heaviest of the three queries.
    public IReadOnlyDictionary<string, OzServerControlledSectorOwnerDto> ControlledByOthers => _controlled;

    public bool IsMine(SectorsVolumes.Sector sector) => _owned.Any(o => o.Equals(sector));

    // Whoever OzServer currently says holds sector - a synthetic "me" when it's in Owned (Owned
    // itself only ever carries the sector, not an owner record, since it's never needed anything
    // else about itself), otherwise whatever _controlled has for it, otherwise null (nobody has an
    // active ownership record for it at all).
    public OzServerControlledSectorOwnerDto? OwnerOf(SectorsVolumes.Sector sector)
    {
        if (IsMine(sector))
        {
            // CidOrZero rather than a parse of its own: this only ever describes *this* session, so
            // there is no case where a missing identity should be attributed to some other cid.
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
        // Connected fires the moment a session actually comes up, which is a faster path to a
        // first-connect refresh than waiting up to PollInterval for the timer below to notice.
        Network.Connected += (_, _) =>
        {
            _ = ResumePreviousSessionAsync();
        };
        // Nothing to take back once this session is over, and a stale entry would otherwise be
        // retried against whatever position it reconnects as.
        Network.Disconnected += (_, _) =>
        {
            lock (_primaryClaimGate)
                _pendingPrimaryClaims.Clear();
        };
        _pollTimer = new System.Threading.Timer(_ =>
        {
            if (!Network.IsConnected)
                return;

            _ = RefreshFromServerIfIdleAsync();
            _ = RetryPendingPrimaryClaimsAsync();
            RunOnUiThread(RetryUnclaimedMmiSectors);
        }, null, PollInterval, PollInterval);

        // A "sectors" signal covers ownership *and* requests: RefreshFromServerIfIdleAsync reads
        // /sectors/sync, which returns owned, controlled and requests together, so one signal
        // answers all three - including the incoming request that drives the popup. Previously
        // that popup waited on a 10s poll tick (2s once a handoff was already in flight).
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

    // Self-heal for a connect-time claim that was dropped before it ever reached the network.
    // ClaimMmiControlledSectorsAsync returns early when Network.Me has not reported IsRealATC yet,
    // which is entirely possible at the instant AfvSectorClaimer grants a position its default
    // sectors on connect - and because MMI.SectorsControlled does not change again afterwards,
    // nothing re-fired the claim. The result was a controller logged in on their own position,
    // holding its airspace in MMI, with no OzServer ownership record at all and no error anywhere
    // to say so; they had to claim their own sector group by hand.
    //
    // Only re-enters the claim path when MMI and Owned genuinely disagree, so the ordinary in-sync
    // case doesn't pay for an extra pass (and its RefreshFromServerAsync) on every tick. Sectors
    // left contested are pulled back out of MMI by HandleConflictAsync, so a discrepancy here
    // resolves rather than retrying forever.
    void RetryUnclaimedMmiSectors()
    {
        if (!Network.IsConnected || !IsRealAtc || !_hasBaseline)
            return;

        if (!MMI.SectorsControlled.Any(s => !s.IsDummy && !_owned.Any(o => o.Equals(s))))
            return;

        OnMmiSectorsControlledChanged();
    }

    // Never lets more than one ClaimMmiControlledSectorsAsync run at a time. MMI.SectorsControlled
    // can change again *while* a pass is still in flight - most notably from that same pass's own
    // downstream effects (ReconcileMmiWithOwned/HandleConflictAsync calling MMI.SetControlledSectors
    // or toggling a VSCS Transmit, both of which re-fire this event synchronously, before the
    // original call has returned) - so a second, overlapping run is queued to happen right after the
    // first finishes instead of starting immediately alongside it.
    void OnMmiSectorsControlledChanged()
    {
        // A stray MMI.SectorsControlled change before/after the network session is up (e.g. vatSys
        // clearing it out on disconnect) shouldn't touch OzServer or push anything back into MMI -
        // see the class-wide "don't do anything while offline" rule.
        if (!Network.IsConnected)
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

                // Test-and-clear as one atomic step under the lock - see _claimGate for the race
                // that splitting these apart used to lose a queued change to.
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
            // Nothing awaits this loop (OnMmiSectorsControlledChanged fires it and forgets), so an
            // escape would otherwise be an unobserved task exception - invisible, and worse, it
            // would leave _claimRunning stuck true and every later MMI change silently ignored for
            // the rest of the session.
            lock (_claimGate)
            {
                _claimRunning = false;
                _claimQueued = false;
            }

            Errors.Add(new Exception($"Couldn't sync controlled sectors to OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    // Applies the requests half of a sync. Raised as two separate events because the two consumers
    // want different things: Plugin only cares that the incoming set changed (to flash), while the
    // Sectors window renders both directions including this controller's own rejections.
    void ApplyRequests(OzServerMyRequestsDto requests)
    {
        MyRequests = requests;
        RequestsChanged?.Invoke(this, requests);

        // A request in either direction means a handoff is mid-flight: either this controller is
        // waiting to hear back, or someone is waiting on them. Poll faster until it resolves.
        Refreshed?.Invoke(this, EventArgs.Empty);

        SetPollCadence(requests.ByMe.Count > 0 || requests.FromMe.Count > 0);

        // Compared on request ids, not on the count: one request being accepted while another
        // arrives between polls leaves the count identical while the actual requests differ, and
        // that used to pass silently.
        var signature = string.Join(",", requests.FromMe.Select(r => r.Id).OrderBy(id => id));
        if (signature == _pendingRequestSignature)
            return;

        _pendingRequestSignature = signature;
        IncomingRequestsChanged?.Invoke(this, requests.FromMe);
    }

    // Deliberately no ConfigureAwait(false) anywhere on this path: callers reached from the UI
    // (ClaimSectorAsync/ReleaseSectorAsync in the Sectors window, which touch controls the moment
    // this returns) rely on their continuation resuming on the UI thread.
    public async Task RefreshFromServerAsync()
    {
        // Also called directly by OzServerSectorsWindow (on open, and on its own poll tick), not
        // just from within this class - guarded here too so neither path can slip an API call or an
        // MMI write past the "do nothing while offline" rule.
        if (!Network.IsConnected)
            return;

        // See _refreshGate. Callers still await a real refresh rather than being turned away, so
        // ClaimAsync and friends keep their guarantee that Owned is current by the time they
        // return - the runs just happen one after another instead of on top of each other.
        await _refreshGate.WaitAsync();
        try
        {
            await RefreshFromServerCoreAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // The fire-and-forget variant, for the periodic and event-driven triggers (both poll timers,
    // Network.Connected, the Sectors window opening). A call that finds a refresh already running
    // drops instead of queueing behind it: these all ask the identical question, so a slow or
    // timing-out request would otherwise build a backlog of refreshes that each re-ask it on
    // arrival. The next tick covers whatever was skipped. Actions that need Owned to be current by
    // the time they return (claim, release, accept) use RefreshFromServerAsync above and do queue.
    public async Task RefreshFromServerIfIdleAsync()
    {
        if (!Network.IsConnected)
            return;

        // WaitAsync(0) completes synchronously - "take it if it's free, otherwise give up".
        //
        // Giving up is right for a poll tick, which is asking a question nothing prompted. It is
        // wrong for the push channel: an SSE "sectors" signal means something actually changed, and
        // dropping it because a poll happened to be in flight left the client on the pre-change
        // picture until its next tick - up to ten seconds of a transferred tag sitting in handover
        // waiting for its sector to arrive. Those are coalesced instead: one more pass runs when the
        // in-flight one finishes, however many signals arrived while it was running.
        if (!await _refreshGate.WaitAsync(0))
        {
            lock (_refreshQueueGate)
                _refreshQueued = true;

            return;
        }

        try
        {
            while (true)
            {
                await RefreshFromServerCoreAsync();

                // Test-and-clear as one step, the same shape as _claimGate: cleared separately, a
                // signal arriving between the test and the clear would be dropped by a pass that had
                // already decided to stop.
                lock (_refreshQueueGate)
                {
                    if (!_refreshQueued)
                        break;

                    _refreshQueued = false;
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // Claims a sector, carving out whatever covered sub-sectors somebody is currently logged on as.
    //
    // Every claim on the backend expands through the dataset's responsible_sectors, which is what
    // makes top-down work at all - but the expansion says nothing about who is actually on those
    // sectors, so an enroute controller logging on while an approach controller was already online
    // took the approach sectors straight off them. The exclusion list is what stops that, and it is
    // computed here rather than on the backend because this side can see the live VATSIM ATC list;
    // see PrimaryPosition.StaffedCoveredSectors.
    //
    // The same list, recomputed on every claim, is also what gives the sectors back: once that
    // controller logs off they stop being excluded, so the next claim takes them. See
    // PrimaryPositionWatcher, which runs one on a departure.
    //
    // `also` carries anything else to leave out - the contested sub-sectors from a 409 retry.
    Task ClaimWithExclusionsAsync(SectorsVolumes.Sector sector, IEnumerable<string>? also = null)
    {
        var exclude = PrimaryPosition.StaffedCoveredSectors(sector, Network.Me?.Callsign);

        foreach (var name in also ?? Enumerable.Empty<string>())
        {
            if (!exclude.Contains(name, StringComparer.OrdinalIgnoreCase))
                exclude.Add(name);
        }

        return _api.ClaimSectorAsync(sector.Name, exclude);
    }

    // Takes back top-down cover that was withheld while somebody was logged on as it, now that they
    // have left. The other half of the rule ClaimWithExclusionsAsync implements: if an approach
    // controller is online their sectors are not the enroute controller's to take, and when they log
    // off they are again.
    //
    // Re-claiming a sector this controller already owns is all it takes, because the exclusion list
    // is recomputed on every claim - the pieces of the group that have since gone unstaffed simply
    // stop being excluded. Nothing else would pick them up: the withheld sector was never in this
    // client's MMI, so the MMI-driven claim path never looks at it, and vatSys has no give-back of
    // its own - it drops a sector when its controller comes online and never restores it.
    public async Task ReclaimTopDownCoverAsync(IReadOnlyCollection<string> freed)
    {
        if (!Network.IsConnected || !IsRealAtc || freed.Count == 0)
            return;

        // Only the groups actually affected. Re-claiming every owned sector on any departure would
        // put a burst of writes on the backend for airspace that has not changed hands.
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
                // Same catch-clause escape rule as the other claim paths - handled after the try.
                conflicts = ex.Conflicts;
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't take back cover under {sector.Name}: {ex.Message}", ex), "OzServer");
            }

            if (conflicts == null)
                continue;

            // Somebody claimed a piece of it in the meantime. Leave those with them, take the rest -
            // deliberately not requested, for the reason ClaimMmiControlledSectorsAsync gives.
            try
            {
                await ClaimWithExclusionsAsync(sector, conflicts.Select(c => c.Sector));
                ActionLog.Log("Ownership",
                    $"Took back cover under {sector.Name} (excluding {string.Join(", ", conflicts.Select(c => c.Sector))})");
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't take back the rest of {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // Applies a sync that came back attached to an action (claim, release, accept, resume, batch)
    // rather than from a poll, under the same gate the polls use.
    //
    // These used to call ApplySync directly, which put them outside _refreshGate entirely - and the
    // gate is not about the HTTP call, it is about the fact that ApplySync *diffs against the last
    // thing applied*. A poll issued before an action and answered after it would overwrite _owned
    // with a payload the server built before the action ran, then diff the next real update against
    // that stale baseline. The visible result is a sector reappearing seconds after it was given
    // away: accepting a request for ESP logged "ESP went to ML-HYD_CTR", and twelve seconds later
    // the same client logged "Gained ESP from ML-HYD_CTR" and pushed it back into MMI - a sector it
    // had just handed over, taken back from a controller who by then legitimately owned it.
    //
    // Waits rather than dropping: an action's own result is never redundant, which is exactly the
    // difference between this and RefreshFromServerIfIdleAsync.
    async Task ApplySyncGatedAsync(OzServerSyncDto sync)
    {
        await _refreshGate.WaitAsync();
        try
        {
            ApplySync(sync);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // One GET for all three of this plugin's read-only views of server state - owned, everyone
    // else's ownership, and this controller's requests in both directions.
    //
    // These were three separate calls, made together on the same tick from two different places
    // (this class's poll, and the Sectors window's faster one). None of them was expensive for the
    // database; the cost was paying the framework's per-request overhead three times over, from
    // every connected client, every couple of seconds. They are always consumed together, so they
    // are now fetched together - see SectorOwnershipController::sync.
    //
    // A failed sync leaves every one of the three exactly as it was rather than half-updating: the
    // previous values staying briefly stale is always better than one view moving while the others
    // do not, which is what made the lists disagree with each other during a blip.
    // Asks the backend to put this session back on whatever it was holding when it last left this
    // same position, then adopts whatever came back.
    //
    // Runs instead of the plain connect refresh, not alongside it: resume answers with the resulting
    // state either way, so a session with nothing to recover costs exactly the same one round trip
    // the refresh would have.
    //
    // Only ever recovers what is still free - see SectorOwnershipController::resume. A sector or a
    // flight somebody picked up while this controller was away stays theirs, so this can never pull
    // something out from under someone actively working it.
    async Task ResumePreviousSessionAsync()
    {
        // Waits for IsConnected and Me to settle. Network.Connected fires before either has -
        // AfvSectorClaimer retries for exactly that reason - and this used to read IsConnected once
        // at entry, find it false, and return silently, with nothing logged and nothing to retry it.
        //
        // The observer test reads Position/Rating from the connection itself (NetworkIdentity), not
        // Network.Me.IsRealATC. Keying it on that flag is what skipped resume for a real
        // ML-ASP_CTR session twice over - it reads false for a genuine controller for seconds after
        // Connected. Position and Rating are correct the moment the session exists.
        //
        // No ConfigureAwait(false), matching the rest of this file: callers reached from the UI rely
        // on their continuation resuming on the UI thread.
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

        ActionLog.Log("Resume", $"requesting for {Network.Me?.Callsign ?? "(callsign unknown)"}");

        try
        {
            var response = await _api.ResumeAsync();

            if (response.Sync != null)
                await ApplySyncGatedAsync(response.Sync);
            else
                await RefreshFromServerAsync();

            ActionLog.Log("Resume",
                $"backend returned {response.Resumed.Count} sector(s), {response.Flights.Count} tag(s)"
                + (response.Flights.Count > 0 ? ": " + string.Join(", ", response.Flights) : ""));

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
            // A backend without /sectors/resume, or any transient failure - fall back to the plain
            // connect refresh so a session still starts correctly, just without recovering anything.
            Errors.Add(new Exception($"Couldn't resume the previous session: {ex.Message}", ex), "OzServer");
            await RefreshFromServerIfIdleAsync();
        }
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

    async Task RefreshFromServerCoreAsync()
    {
        OzServerSyncDto sync;
        try
        {
            sync = await _api.GetSyncAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't refresh sector state from OzServer: {ex.Message}", ex), "OzServer");
            return;
        }

        ApplySync(sync);
    }

    // Adopts a sync payload, wherever it came from: the poll's own GET, or the response of an action
    // that just changed something. Actions used to POST and then immediately GET to find out the
    // result, so accepting a request cost two sequential round trips - and the second could queue
    // behind an in-flight poll before it even started. The server now returns the resulting state
    // with the action, so the same code applies it either way and the UI moves on the first reply.
    void ApplySync(OzServerSyncDto sync)
    {
        // Captured before the replacement: a sector this session just gained needs the owner it had
        // a moment ago, which the incoming payload no longer mentions.
        var previousControlled = _controlled;

        _controlled = sync.Controlled
            .Where(c => c.Owner != null)
            .ToDictionary(c => c.Name, c => c.Owner!, StringComparer.OrdinalIgnoreCase);

        ApplyRequests(sync.Requests);

        var mine = sync.Mine;
        var previous = _owned;
        var current = mine
            .Select(dto => SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == dto.Name))
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();
        _owned = current;

        // Pass the snapshot, not the field. RunOnUiThread posts with BeginInvoke, so the reconcile
        // runs some time after this method has returned and released the refresh gate - a lambda
        // reading _owned at that point would pick up whatever a *later* refresh had since assigned
        // and diff it against this run's `previous`, reporting a change neither run actually saw.
        if (_hasBaseline)
            RunOnUiThread(() => ReconcileMmiWithOwned(previous, current, previousControlled));
        else
            _hasBaseline = true;

        OwnedChanged?.Invoke(this, EventArgs.Empty);
    }

    // Pushes an Owned change onto MMI.SectorsControlled and the matching VSCS line - see the class
    // comment. Runs on the UI thread (RunOnUiThread callers) since both of those feed real vatSys
    // UI. Calling MMI.SetControlledSectors here re-fires MMI.SectorsControlledChanged, which calls
    // ClaimMmiControlledSectorsAsync -> RefreshFromServerAsync again - harmless: by then Owned
    // already matches what was just pushed, so that second pass finds no further diff and stops.
    void ReconcileMmiWithOwned(List<SectorsVolumes.Sector> previous, List<SectorsVolumes.Sector> current,
        IReadOnlyDictionary<string, OzServerControlledSectorOwnerDto> previousControlled)
    {
        var gained = current.Where(s => !previous.Any(p => p.Equals(s))).ToList();
        var lost = previous.Where(p => !current.Any(s => s.Equals(p))).ToList();

        if (gained.Count == 0 && lost.Count == 0)
            return;

        var diff = new SectorOwnershipDiff();
        // Gained: who had it before, from the snapshot taken before this sync overwrote it.
        // Lost: who has it now, which is exactly what the new snapshot records.
        foreach (var sector in gained)
            diff.Gained.Add(new SectorTransfer(sector,
                previousControlled.TryGetValue(sector.Name, out var had) ? had : null));

        foreach (var sector in lost)
            diff.Lost.Add(new SectorTransfer(sector,
                _controlled.TryGetValue(sector.Name, out var has) ? has : null));

        OwnershipChanged?.Invoke(this, diff);

        var mmiSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        // Gaining a sector adds it to MMI (so the airspace and its tags appear) but deliberately does
        // NOT switch its VSCS line to Transmit - issue #5. Forcing Transmit on put the controller on
        // a frequency they never asked to be on, as a side effect of accepting a request; whether to
        // actually talk on a sector is theirs to decide, and the VSCS panel is where they decide it.
        // Losing a sector still drops its line to Idle below, which is cleanup of something this
        // plugin is giving up rather than something it is taking on the controller's behalf.
        foreach (var sector in gained)
        {
            if (!mmiSectors.Any(s => s.Equals(sector)))
                mmiSectors.Add(sector);
        }

        foreach (var sector in lost)
        {
            mmiSectors.RemoveAll(s => s.Equals(sector));

            var freq = Audio.VSCSFrequencies.FirstOrDefault(f => f.Name == sector.Callsign);
            if (freq is { Transmit: true })
                freq.Transmit = false;
        }

        MMI.SetControlledSectors(mmiSectors);
    }

    // Whether this session is actually connected as ATC, not merely a connection that happens to
    // have parsed into a valid-looking position - see NetworkATC.IsRealATC (the same check
    // PrimaryPosition.OnlineRealAtcs already uses to keep observers out of the online-controller
    // picture). vatSys's own Sectors window still lets an observer connection populate
    // MMI.SectorsControlled - that's for local situational awareness (watching a sector's tags/
    // strips), not a claim of authority over it - and OzServer's claim endpoint trusts whatever
    // controller_cid/controller_callsign the plugin sends it. The local IsRealATC flag is what
    // distinguishes a controlling connection from an observer - see issue #3, "CM_OBS Can Own
    // Sectors".
    // Reads the connection's own Position/Rating rather than Network.Me.IsRealATC - see
    // NetworkIdentity.IsObserver for why that flag cannot be trusted at connect time. As a bonus
    // this is correct immediately, so a claim made in the first seconds of a session is no longer
    // silently dropped while the published ATC record catches up.
    static bool IsRealAtc => !NetworkIdentity.IsObserver;

    public async Task ClaimAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected || !IsRealAtc)
            return;

        // The conflict is handled *after* the try block, not inside the catch clause that detects
        // it. An exception thrown from within a catch clause is not seen by that same try's other
        // catch clauses, so HandleConflictAsync throwing (its AskYesNo marshals a dialog with a
        // blocking Invoke, and RunOnUiThread posts with BeginInvoke - both can fail on a form that
        // is going away) escaped this method entirely, straight out through the async void click
        // handler that called it and into vatSys as an unhandled exception.
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
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }

        if (conflicts != null)
        {
            try
            {
                await HandleConflictAsync(sector, conflicts);
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't resolve the ownership conflict on {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // Commits one Apply: releases everything the controller unpicked, then claims everything they
    // picked, turning any sector another controller already owns into a request instead of a
    // prompt. Apply *is* the confirmation - the controller has already said what they want by the
    // time this runs - so unlike ClaimAsync's interactive path (HandleConflictAsync) nothing here
    // stops to ask.
    //
    // One RefreshFromServerAsync for the whole batch, not one per sector as Claim/ReleaseAsync each
    // do individually: that per-sector GET is what made moving several sectors take seconds, and
    // the intermediate states it published were never worth rendering anyway.
    public async Task<SectorCommitResult> CommitSectorChangesAsync(
        IReadOnlyList<SectorsVolumes.Sector> toClaim,
        IReadOnlyList<SectorsVolumes.Sector> toRelease,
        IReadOnlyList<SectorsVolumes.Sector>? toRequest = null)
    {
        var result = new SectorCommitResult();
        if (!Network.IsConnected)
            return result;

        // Same rule as ClaimAsync/ClaimMmiControlledSectorsAsync - an observer connection can still
        // stage a claim in the Sectors window (nothing there checks facility either), but is not
        // entitled to actually take ownership of it. Release and request are left alone: giving back
        // something already held, or asking for something, aren't the problem IsRealAtc guards
        // against.
        if (!IsRealAtc && toClaim.Count > 0)
        {
            Errors.Add(new Exception(
                "Not connected as a real ATC position - sectors can't be claimed while observing."),
                "OzServer");
            result.Failed.AddRange(toClaim.Select(s => s.Name));
            toClaim = Array.Empty<SectorsVolumes.Sector>();
        }

        // One call for the whole Apply, answering with the resulting state - so committing several
        // staged sectors costs one round trip rather than one per sector plus a refresh, and the
        // lists move on the first reply. The per-sector path below is kept as a fallback for a
        // backend that predates /sectors/commit.
        try
        {
            // One exclusion list for the whole batch - the union of what each claimed sector would
            // sweep up that somebody is on. A name only excluded for one of the claims is still
            // correct to exclude for all of them: it is staffed, so none of them may take it.
            var staffed = toClaim
                .SelectMany(s => PrimaryPosition.StaffedCoveredSectors(s, Network.Me?.Callsign))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Never carve out a sector the controller explicitly staged: they asked for it by
                // name, and if it is somebody else's the ordinary conflict path is what answers.
                .Where(name => !toClaim.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var response = await _api.CommitAsync(
                toClaim.Select(s => s.Name),
                toRelease.Select(s => s.Name),
                (toRequest ?? Array.Empty<SectorsVolumes.Sector>()).Select(s => s.Name),
                staffed);

            result.Claimed.AddRange(response.Result.Claimed);
            result.Released.AddRange(response.Result.Released);
            result.Requested.AddRange(response.Result.Requested);
            result.Skipped.AddRange(response.Result.Skipped);
            result.Failed.AddRange(response.Result.Failed);

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
                Errors.Add(new Exception($"Couldn't release {sector.Name}: {ex.Message}", ex), "OzServer");
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
                // Same catch-clause escape rule as ClaimAsync - handled after the try, not inside it.
                conflicts = ex.Conflicts;
            }
            catch (Exception ex)
            {
                result.Failed.Add(sector.Name);
                Errors.Add(new Exception($"Couldn't claim {sector.Name}: {ex.Message}", ex), "OzServer");
            }

            if (conflicts == null)
                continue;

            // Deliberately does NOT request the contested sub-sectors.
            //
            // A claim covers the sector plus everything its dataset entry is responsible for
            // (Sector::coveredSectors), so one staged sector can collide on several sub-sectors it
            // merely covers. Firing a request at each of those turned "I want ASP" into pending
            // requests against half a dozen sectors the controller never asked for - and accepting
            // any one of them handed over that sector's own covered group in turn. That is exactly
            // the "requested one sector, received an unrelated one" case.
            //
            // Asking for a sector is now only ever something the controller does explicitly, by
            // staging a sector another controller owns (OzServerSectorsWindow.StageSectorChange).
            // Here the contested pieces are simply left with their current owner and reported.
            foreach (var conflict in conflicts)
                result.Skipped.Add(conflict.Sector);

            // Then take the rest of the group. Without this second call a claim that collided on one
            // sub-sector would hand over none of the others, even though they were free.
            try
            {
                await ClaimWithExclusionsAsync(sector, conflicts.Select(c => c.Sector));
                result.Claimed.Add(sector.Name);
                ActionLog.Log("Ownership", $"Claimed {sector.Name} (excluding {string.Join(", ", conflicts.Select(c => c.Sector))})");
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't claim the rest of {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        // Owned is re-derived once, at the end - which is also what pushes the whole result into
        // MMI.SectorsControlled and the VSCS panel in one go, through ReconcileMmiWithOwned.
        await RefreshFromServerAsync();
        return result;
    }

    public async Task ReleaseAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected)
            return;

        try
        {
            await _api.ReleaseSectorAsync(sector.Name);
            ActionLog.Log("Ownership", $"Released {sector.Name}");
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }

        await RefreshFromServerAsync();
    }

    public async Task AcceptRequestAsync(int requestId)
    {
        if (!Network.IsConnected)
            return;

        await _api.AcceptRequestAsync(requestId);
        ActionLog.Log("Ownership", $"Accepted request #{requestId}");
        await RefreshFromServerAsync();
    }

    // Prefer this over calling AcceptRequestAsync once per id when accepting more than one at a
    // time - see AcceptRequestsBatchAsync's own comment on the API client for why firing separate
    // accept calls back-to-back could leave a request row behind. Returns the raw per-request
    // results so the caller can report anything that didn't go through (already accepted/rejected
    // by someone else in the meantime, no longer the current owner, ...).
    // Applies the state an action's own response carried, if it carried one. Public so the window's
    // reject/cancel paths get the same one-round-trip treatment as accept without each of them
    // reimplementing the fallback.
    public async Task ApplyActionResultAsync(OzServerActionResultDto? result)
    {
        if (result?.Sync != null)
            await ApplySyncGatedAsync(result.Sync);
        else
            await RefreshFromServerAsync();
    }

    public async Task<List<OzServerAcceptBatchResultDto>> AcceptRequestsBatchAsync(IEnumerable<int> requestIds)
    {
        if (!Network.IsConnected)
            return new List<OzServerAcceptBatchResultDto>();

        var response = await _api.AcceptRequestsBatchAsync(requestIds);
        foreach (var result in response.Results)
            ActionLog.Log("Ownership", $"Accepted request #{result.RequestId} ({result.Sector}): {(result.Accepted ? "ok" : result.Message)}");

        // The accept's own response carries the resulting state, so there is nothing left to ask
        // for. Falls back to a refresh only if an older backend answered without it.
        if (response.Sync != null)
            await ApplySyncGatedAsync(response.Sync);
        else
            await RefreshFromServerAsync();

        return response.Results;
    }

    // Mirrors AcceptRequestsBatchAsync. Declining a grouped request is one call, so the whole
    // group is refused as the single decision it was made as - rejecting sector by sector would
    // leave a partially-answered request that neither controller can reason about.
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

    // MMI.SectorsControlled can change through paths nothing OzServer-aware drives directly -
    // connecting under a position's own callsign, the built-in Sectors window, VSCS/AFV transmit
    // (AfvSectorClaimer), a LogicalPositions/profile auto-set, another plugin, etc. This claims
    // whatever's newly controlled (claiming an already-mine sector is a harmless no-op server-side -
    // see SectorOwnershipController::claim - so nothing needs diffing against Owned first) and then
    // refreshes Owned from the server, once, through RefreshFromServerAsync.
    //
    // Deliberately claim-only otherwise: releasing is never inferred from MMI absence (that's the
    // claim/release loop described in the class comment) - it only ever happens from an explicit
    // action (the arrow button) or a genuine conflict, handled below.
    //
    // A sector already in Owned is skipped entirely, not re-claimed - claiming a primary always
    // asks the backend to recreate its *whole* responsible_sectors group unconditionally (see
    // SectorOwnershipController::claim), with no memory of any partial exclusion already in place.
    // MMI.SectorsControlledChanged fires for all sorts of reasons unrelated to any one particular
    // sector (releasing a single sub-sector while its primary and siblings stay controlled is
    // itself one such reason), and re-claiming an already-owned primary on every one of them would
    // silently re-claim every sub-sector it covers too - including one just deliberately released,
    // or one a prior conflict deliberately excluded - undoing that a moment later. Only a sector
    // genuinely new to Owned needs claiming at all.
    //
    // A sector *not yet* owned is also skipped here (not claimed individually) when its own primary
    // is *also* in this same MMI snapshot - e.g. extending WON, whose own SubSectors include
    // HUO/LTA/HBA: claiming WON already covers all three server-side, so claiming HUO again right
    // after would be redundant, and worse, would bypass whatever exclusion WON's own conflict
    // handling (below) just carved out for it if HUO turned out to already be owned by someone
    // else. A bare sub-sector present *without* its primary (e.g. logging on directly under SNO's
    // own callsign, with WOL nowhere in MMI at all) is still claimed on its own exactly as listed -
    // this only skips the redundant case, never substitutes one sector for another.
    async Task ClaimMmiControlledSectorsAsync()
    {
        // An observer connection can still end up with entries in MMI.SectorsControlled - vatSys's
        // own Sectors window doesn't gate that on facility either - but isn't entitled to turn that
        // into a real OzServer ownership record. See IsRealAtc.
        if (!IsRealAtc)
            return;

        var target = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        foreach (var sector in target)
        {
            if (_owned.Any(o => o.Equals(sector)))
                continue;

            if (target.Any(other => !other.Equals(sector) && other.SubSectors.Any(sub => sub.Equals(sector))))
                continue;

            // Same catch-clause escape as ClaimAsync - see the comment there. Here it would have
            // aborted the whole loop partway through, silently skipping every sector after this
            // one, rather than just failing the one that conflicted.
            IReadOnlyList<OzServerSectorConflictDto>? conflicts = null;

            try
            {
                await ClaimWithExclusionsAsync(sector);
            }
            catch (OzServerApiException ex) when (ex.StatusCode == 409 && ex.Conflicts.Count > 0)
            {
                conflicts = ex.Conflicts;
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't sync {sector.Name} to OzServer: {ex.Message}", ex), "OzServer");
            }

            if (conflicts == null)
                continue;

            try
            {
                await HandleConflictAsync(sector, conflicts);
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't resolve the ownership conflict on {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // One or more of sector's own covered sub-sectors is already owned by someone else - e.g. GUN
    // extending WOL while BLA already owns WOL's own sub-sector SNO (extended earlier). Asks the
    // controller whether to formally request each contested sub-sector from its current owner, then
    // claims sector regardless, excluding whichever sub-sectors are still contested either way:
    // "No" just leaves them with their current owner; "Yes" does the same but also puts in a
    // request for each one. Either way, whatever's still contested is also pulled back out of
    // MMI.SectorsControlled and dropped to Idle on its own VSCS line if it has one - AfvSectorClaimer's
    // CheckActive() adds a whole primary's group to MMI optimistically before any of this is known,
    // so a sub-sector that turns out not to actually be claimable has to come back out again rather
    // than sit in MMI as if this controller owns it when OzServer disagrees.
    async Task HandleConflictAsync(SectorsVolumes.Sector sector, IReadOnlyList<OzServerSectorConflictDto> conflicts)
    {
        // A sector this session is the primary for is not something to ask about. The position is
        // this controller's by right the moment they log on, and the controller holding it is
        // already being told to hand it back - PrimaryPositionWatcher does exactly that on their
        // side as soon as their client sees this logon. "Request it from them?" is the wrong
        // question to put to someone about their own position, and answering "no" would leave them
        // permanently locked out of it.
        //
        // The catch is timing: this claim fires on Network.Connected, while the holder only finds
        // out on their next OnlineATCChanged, which follows VATSIM's own update cycle a few seconds
        // later. So these are split out of the question and retried in the background instead (see
        // RetryPendingPrimaryClaimsAsync) until the release lands.
        var myDefaults = PrimaryPosition.DefaultSectorsFor(Network.Me?.Callsign);
        var mineByRight = conflicts.Where(c => myDefaults.Any(s => s.Name == c.Sector)).ToList();
        var contestedByOthers = conflicts.Where(c => !mineByRight.Contains(c)).ToList();

        QueuePrimaryClaimRetries(mineByRight);

        // Laid out lead / list / question, the same shape as every other popup in the plugin, with
        // each sector written out in full (SectorDescription). This used to name sectors by bare
        // code - "STR is already owned by BN-TRT_CTR" - and run several of them together inline,
        // which asked a controller to answer for airspace it never actually identified.
        var described = contestedByOthers
            .Select(conflict => SectorDescription.DescribeWithOwner(conflict.Sector, conflict.Owner?.Callsign))
            .ToList();

        var question = (described.Count == 1
                           ? "This sector is already owned by another controller:"
                           : "These sectors are already owned by other controllers:")
                       + Environment.NewLine + Environment.NewLine
                       + string.Join(Environment.NewLine, described)
                       + Environment.NewLine + Environment.NewLine
                       + (described.Count == 1
                           ? "Request it from them?"
                           : "Request them from their current owners?");

        if (contestedByOthers.Count > 0 && AskYesNo(question, "Sector already owned"))
        {
            foreach (var conflict in contestedByOthers)
            {
                var conflictSector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == conflict.Sector);
                if (conflictSector == null)
                    continue;

                try
                {
                    var request = await _api.RequestSectorAsync(conflictSector.Name);
                    ActionLog.Log("Request", $"Requested {conflictSector.Name} from {request.TargetCallsign}");
                }
                catch (Exception ex)
                {
                    Errors.Add(new Exception($"Couldn't request {conflictSector.Name} on OzServer: {ex.Message}", ex), "OzServer");
                }
            }
        }

        try
        {
            await ClaimWithExclusionsAsync(sector, conflicts.Select(c => c.Sector));
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't claim {sector.Name} on OzServer: {ex.Message}", ex), "OzServer");
        }

        RunOnUiThread(() =>
        {
            var contested = conflicts
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

    // _api's calls all run their continuations off the UI thread (see OzServerApiClient's own use
    // of ConfigureAwait(false)), and MMI.SectorsControlledChanged can itself fire off-thread too -
    // showing a dialog has to land back on the UI thread rather than risk a cross-thread control
    // access inside vatSys itself. Invoke (not BeginInvoke) since the caller needs the answer back
    // before deciding what to do next - safe to block on here since the calling thread is never the
    // UI thread itself (that's exactly the case this branch exists for).
    void QueuePrimaryClaimRetries(IEnumerable<OzServerSectorConflictDto> conflicts)
    {
        lock (_primaryClaimGate)
        {
            foreach (var conflict in conflicts)
                _pendingPrimaryClaims[conflict.Sector] = PrimaryClaimRetryTicks;
        }
    }

    // Re-attempts the claims parked by HandleConflictAsync, once per poll tick, until the previous
    // holder's release lands. Runs on the tracker's own existing timer rather than a delay loop of
    // its own: the poll is already the heartbeat for "has anything changed on the server", and a
    // claim that succeeds here reaches MMI and the VSCS panel through the same
    // RefreshFromServerAsync -> ReconcileMmiWithOwned path every other ownership gain uses.
    async Task RetryPendingPrimaryClaimsAsync()
    {
        List<string> names;
        lock (_primaryClaimGate)
        {
            if (_primaryClaimRetryRunning || _pendingPrimaryClaims.Count == 0)
                return;

            _primaryClaimRetryRunning = true;
            names = _pendingPrimaryClaims.Keys.ToList();
        }

        try
        {
            // Re-derived every tick, not captured when the conflict happened: a position change, or
            // someone logging in directly on one of the sub-sectors, means it is no longer this
            // session's to take and the retry should stop rather than fight them for it.
            var stillMine = PrimaryPosition.DefaultSectorsFor(Network.Me?.Callsign);
            var claimedAny = false;

            foreach (var name in names)
            {
                if (!Network.IsConnected)
                    return;

                var sector = stillMine.FirstOrDefault(s => s.Name == name);

                if (sector == null || _owned.Any(o => o.Name == name))
                {
                    Drop(name);
                    continue;
                }

                try
                {
                    await ClaimWithExclusionsAsync(sector);
                    claimedAny = true;
                    Drop(name);
                    continue;
                }
                catch (OzServerApiException ex) when (ex.StatusCode == 409)
                {
                    // Still held - the previous holder hasn't released yet. Spend a tick.
                }
                catch (Exception ex)
                {
                    Errors.Add(new Exception($"Couldn't take {name} back for this position: {ex.Message}", ex), "OzServer");
                }

                lock (_primaryClaimGate)
                {
                    if (!_pendingPrimaryClaims.TryGetValue(name, out var ticksLeft))
                        continue;

                    if (ticksLeft <= 1)
                        _pendingPrimaryClaims.Remove(name);
                    else
                        _pendingPrimaryClaims[name] = ticksLeft - 1;
                }
            }

            // Only when something actually changed hands - this runs every tick, and the plain
            // refresh the timer already fires covers the no-op case.
            if (claimedAny)
                await RefreshFromServerAsync();
        }
        finally
        {
            lock (_primaryClaimGate)
                _primaryClaimRetryRunning = false;
        }

        void Drop(string name)
        {
            lock (_primaryClaimGate)
                _pendingPrimaryClaims.Remove(name);
        }
    }

    static bool AskYesNo(string message, string caption)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            return (bool)mainForm.Invoke(new Func<bool>(() => ShowYesNo(mainForm, message, caption)));

        return ShowYesNo(null, message, caption);
    }

    static bool ShowYesNo(IWin32Window? owner, string message, string caption)
    {
        using var prompt = new SectorConflictPromptWindow(message, caption);
        var result = owner != null ? prompt.ShowDialog(owner) : prompt.ShowDialog();

        return result == DialogResult.Yes;
    }

    // Fire-and-forget UI-thread marshaling for callers (the MMI.SectorsControlledChanged
    // subscription) that don't need anything back - see AskYesNo above for the blocking variant
    // used when a return value is needed.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
