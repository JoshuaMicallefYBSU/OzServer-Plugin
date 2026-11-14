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
// So a release is never *inferred* from a sector's absence in MMI, but it is still driven by the
// thing that caused the absence, in the one case where that thing is unambiguous: a VSCS line
// dropped out of Transmit. AfvSectorClaimer calls ReleaseIdleVscsLineAsync for it directly, naming
// the one sector the controller just stopped working, rather than this class guessing from a diff
// which of MMI's omissions were deliberate.
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
    // that a sector claimed elsewhere shows up in reasonable time. Also doubles as the server's own
    // floor for how fast it can safely let a different controller take over a sector whose owner
    // has gone quiet (OzServer-API's PRESENCE_TIMEOUT_SECONDS) - that check can never be faster than
    // this without risking a false takeover on a controller who is still connected and simply
    // hasn't ticked yet, so lowering one means lowering the other to match.
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    // Cadence while a handoff is actually in flight - this controller is waiting on someone's
    // decision, or owes one. That is exactly the window where reflection latency is felt, and it is
    // short-lived, so it costs nothing the rest of the time. Without it, being told your request was
    // accepted took up to ten seconds with the Sectors window closed.
    static readonly TimeSpan PendingPollInterval = TimeSpan.FromSeconds(1);

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

    // Sectors the backend answered a claim for without granting them, and when to ask again.
    //
    // A claim is 201 even when the sector it names is not among what it claimed: the backend
    // *withholds* a sector whose owner is present and logged on as part of that same group, rather
    // than calling it a conflict (claimGroup's `withheld`). The plugin never read the answer, so it
    // took that as success - and since the sector was still in MMI and still not in Owned,
    // RetryUnclaimedMmiSectors claimed it again on the next tick, and the next: SNO was POSTed every
    // three seconds for five minutes straight on 13 Nov. Backing off breaks that loop without
    // giving up on the sector for good, because whoever it was withheld for may log off.
    static readonly TimeSpan WithheldBackoff = TimeSpan.FromSeconds(60);
    readonly object _withheldGate = new();
    readonly Dictionary<string, DateTime> _withheldUntil = new(StringComparer.OrdinalIgnoreCase);

    // How long a release this client sent keeps RestoreOwnedToMmi from putting the sector back
    // while Owned still lists it. Only has to outlast the release's own round trip and the refresh
    // that follows it; anything longer just delays the repair if the release never lands.
    static readonly TimeSpan ReleaseSettleWindow = TimeSpan.FromSeconds(10);
    readonly object _releasedGate = new();
    readonly Dictionary<string, DateTime> _releasedRecently = new(StringComparer.OrdinalIgnoreCase);

    // What the last resume handed back, for a short while afterwards - see RecentlyResumed. Long
    // enough to outlast AfvSectorClaimer's own connect-time retries (up to 20s) plus a resume that
    // waited on Network.Me and then a slow round trip.
    static readonly TimeSpan ResumedGraceWindow = TimeSpan.FromSeconds(60);
    readonly object _resumedGate = new();
    List<SectorsVolumes.Sector> _resumed = new();
    DateTime _resumedUntil = DateTime.MinValue;

    public IReadOnlyList<SectorsVolumes.Sector> Owned => _owned;

    // Sectors a resume put back moments ago. AfvSectorClaimer.Init() replaces MMI.SectorsControlled
    // wholesale with the position's defaults, and nothing orders it against the resume: both wait on
    // Network.Me after Connected, so Init can land after RestoreResumedToMmi and silently take the
    // resumed extras back out of MMI - dropping any tag TagResumeRecovery had just restored inside
    // them, through SetControlledSectors' own old/new-sector cleanup. Init merges these in instead.
    public IReadOnlyList<SectorsVolumes.Sector> RecentlyResumed
    {
        get
        {
            lock (_resumedGate)
                return DateTime.UtcNow < _resumedUntil ? _resumed.ToList() : new List<SectorsVolumes.Sector>();
        }
    }


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
            lock (_withheldGate)
                _withheldUntil.Clear();

            lock (_resumedGate)
            {
                _resumed = new List<SectorsVolumes.Sector>();
                _resumedUntil = DateTime.MinValue;
            }
        };
        _pollTimer = new System.Threading.Timer(_ =>
        {
            if (!Network.IsConnected)
                return;

            _ = RefreshFromServerIfIdleAsync();
            RunOnUiThread(() =>
            {
                RetryUnclaimedMmiSectors();
                RestoreOwnedToMmi();
            });
        }, null, PollInterval, PollInterval);

        // A "sectors" signal covers ownership *and* requests: RefreshFromServerIfIdleAsync reads
        // /sectors/sync, which returns owned, controlled and requests together, so one signal
        // answers all three - including the incoming request that drives the popup. Previously
        // that popup waited on a poll tick (1s once a handoff is already in flight).
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

        // The same test the claim pass itself applies, sector for sector. This used to ask only "is
        // anything in MMI unowned and not backed off", which is true for a sector the pass will
        // never claim on its own - a sub-sector whose parent is in MMI too is left to the parent's
        // claim - so one such sector had every tick start a pass that claimed nothing and then
        // fetched /sectors/sync a second time, for as long as it sat there.
        var target = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        if (!target.Any(s => NeedsClaim(s, target)))
            return;

        OnMmiSectorsControlledChanged();
    }

    // Whether the claim pass would actually send a claim for this sector - see
    // ClaimMmiControlledSectorsAsync for each of the three rules.
    bool NeedsClaim(SectorsVolumes.Sector sector, List<SectorsVolumes.Sector> target) =>
        !_owned.Any(o => o.Equals(sector))
        && !target.Any(other => !other.Equals(sector) && other.SubSectors.Any(sub => sub.Equals(sector)))
        && !IsWithheld(sector.Name)
        && !IsReliefCovered(sector)
        && !IsHeldForTransmitPrompt(sector.Name)
        // Something else (VatpacPlugin, on the same transmit press) can still put an enroute or TMA
        // sector into a flow position's MMI. It is never claimed from there - see
        // PrimaryPosition.MayOwn.
        && PrimaryPosition.MayOwn(sector, Network.Me?.Callsign);

    // An FMP is covering this sector for its controller, who agreed to it. The login sector stays in
    // that controller's MMI (vatSys keeps it there), so without this every poll would claim it,
    // be refused, and - it being their own position - request it straight back off the FMP the
    // moment they had given it. It comes back once the FMP lets it go: the next poll finds it
    // unowned and claims it like any other.
    bool IsReliefCovered(SectorsVolumes.Sector sector) =>
        _controlled.TryGetValue(sector.Name, out var owner) && PrimaryPosition.IsFlowCallsign(owner.Callsign);

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

    // The mirror image of RetryUnclaimedMmiSectors above, and it exists for the same reason: the
    // two records drift, nothing says so, and the controller is the one who finds out.
    //
    // RetryUnclaimedMmiSectors repairs MMI -> OzServer ("controlled here, owned nowhere"). This
    // repairs OzServer -> MMI ("owned on OzServer, not controlled here"), which is the drift that
    // actually costs something: the sector is held against every other controller - it is in the
    // OzServer Sectors window, nobody else can take it - while locally there is no infill, no
    // jurisdiction over its tags, and no VSCS line to press that would give it back, because every
    // path that gives a sector back starts from it being in MMI.SectorsControlled.
    //
    // Owned is meant to be a subset of MMI throughout: ReconcileMmiWithOwned pushes every gained
    // sector into MMI, RestoreResumedToMmi does the same for a resume, and AfvSectorClaimer's own
    // grant merges what a resume handed back. Each of those is a one-shot at the moment it happens,
    // and each has an ordering or a readiness condition it can lose - a sector taken out of MMI by
    // a different line's group sweep, a re-baseline landing after a resume, an MMI write made
    // before vatSys was ready for it. One missed moment used to be permanent, because nothing ever
    // looked again. This looks again every poll tick.
    //
    // Adds only, and leaves VSCS alone - the same rule ReconcileMmiWithOwned applies to a gained
    // sector (issue #5: being handed a sector is not consent to be put on its frequency). It cannot
    // claim anything either: a sector only gets here by already being in Owned, which is OzServer's
    // own record of what this controller holds.
    void RestoreOwnedToMmi()
    {
        if (!Network.IsConnected || !IsRealAtc || !_hasBaseline)
            return;

        var mmiSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var missing = _owned
            .Where(owned => !owned.IsDummy)
            .Where(owned => !mmiSectors.Any(s => s.Equals(owned)))
            // A release this client has just sent is the one case where Owned legitimately still
            // holds a sector that is already gone from MMI - Owned catches up when the release's
            // refresh lands. Putting it back for those few hundred milliseconds would undo the
            // removal on screen and then undo that again a moment later.
            .Where(owned => !WasJustReleased(owned.Name))
            .ToList();

        if (missing.Count == 0)
            return;

        mmiSectors.AddRange(missing);
        MMI.SetControlledSectors(mmiSectors);

        // Logged every time rather than silently repaired: this firing at all means something took
        // the sector out of MMI without releasing it, and the log line is what identifies that
        // something - when it happened, and what it took.
        ActionLog.Log("Ownership",
            $"Put back into vatSys: {string.Join(", ", missing.Select(s => s.Name))} - owned on OzServer but missing from the controlled sectors",
            new
            {
                action = "restore_owned_to_mmi",
                sectors = missing.Select(s => s.Name).ToArray(),
                own_callsign = Network.Me?.Callsign
            });
    }

    // Names this client has just asked the backend to release, and when they stop being excused -
    // see RestoreOwnedToMmi, the only reader. Marked before the call goes out, not after it
    // returns, so the whole in-flight window is covered.
    void MarkReleased(IEnumerable<string> names)
    {
        var until = DateTime.UtcNow + ReleaseSettleWindow;

        lock (_releasedGate)
        {
            // Pruned here rather than on a timer of its own: the set is a handful of sector names
            // at worst, and this is the only place that grows it.
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

    // Everything a release of this sector gives back: the backend's releaseGroup takes the named
    // sector's whole covered group with it, not just the sector named.
    //
    // Marking only the name left the rest of the group exposed for the length of the round trip.
    // Dropping MUN's VSCS line took MUN, GTH, OXL and YWE off the scope; Owned still listed all four
    // until the release's refresh landed, only MUN was excused, and RestoreOwnedToMmi put the other
    // three straight back - "Put back into vatSys: GTH, OXL, YWE" - for ReconcileMmiWithOwned to
    // take out again a few milliseconds later. Each of those is a full MMI.SetControlledSectors,
    // with the track-state recompute and the tag cleanup that go with it.
    void MarkGroupReleased(SectorsVolumes.Sector sector) =>
        MarkReleased(PrimaryPosition.CoveredBy(sector).Select(covered => covered.Name));

    // For a caller about to release several sectors one after another (PrimaryPositionWatcher).
    // Each release only excuses its own group as it goes out, so the ones still waiting their turn
    // - already off the scope, still in Owned - were put back by the refresh that follows every
    // release ahead of them in the queue.
    public void ExpectReleaseOf(IEnumerable<SectorsVolumes.Sector> sectors)
    {
        foreach (var sector in sectors)
            MarkGroupReleased(sector);
    }

    // Lifts the excuse again for sectors that turned out to be kept after all - see
    // ReclaimOwnPositionAfterReleaseAsync, the one release that takes part of its own group back.
    void UnmarkReleased(IEnumerable<string> names)
    {
        lock (_releasedGate)
        {
            foreach (var name in names)
                _releasedRecently.Remove(name);
        }
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
        // see the class-wide "don't do anything while offline" rule. An unsupported server counts as
        // offline here too (see CanSync): every claim would fail before it was sent, and report one
        // error per sector on every VSCS press.
        if (!CanSync)
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

        // A rejected request in by_me is not in flight - it is decided, and only waiting for the
        // Sectors window to report and acknowledge it. That window only exists once it has been
        // opened, so a request made from the conflict prompt and then refused kept the poll on its
        // fast cadence indefinitely. Only a request still awaiting an answer counts.
        SetPollCadence(requests.ByMe.Any(r => r.RejectedAt == null) || requests.FromMe.Count > 0);

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
        if (!CanSync)
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
            ReleaseRefreshGate();
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
        if (!CanSync)
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
            await RefreshFromServerCoreAsync();
        }
        finally
        {
            ReleaseRefreshGate();
        }
    }

    // Releases the refresh gate and answers anything that arrived while it was held.
    //
    // Every path that takes the gate releases through here, which is the whole point: draining the
    // flag inside RefreshFromServerIfIdleAsync alone only covered a signal that collided with
    // another poll. One arriving during a claim, release or accept - which hold the gate through
    // RefreshFromServerAsync and ApplySyncGatedAsync - was still dropped, and those are exactly the
    // moments ownership is changing and a push signal matters most.
    //
    // Test-and-clear as one step, the same shape as _claimGate: cleared separately, a signal
    // arriving between the test and the clear would be lost by a pass that had already decided
    // nothing was waiting.
    void ReleaseRefreshGate()
    {
        _refreshGate.Release();

        bool queued;
        lock (_refreshQueueGate)
        {
            queued = _refreshQueued;
            _refreshQueued = false;
        }

        // After the release, so this re-entry finds the gate free rather than queueing behind the
        // pass that is handing it over. Fire-and-forget: it is the same work the caller just did.
        if (queued)
            _ = RefreshFromServerIfIdleAsync();
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
    // Answers with what was granted - see OzServerApiClient.ClaimSectorAsync.
    Task<IReadOnlyList<string>?> ClaimWithExclusionsAsync(SectorsVolumes.Sector sector, IEnumerable<string>? also = null)
    {
        // Every claim path comes through here, so this is the plugin's last word on a flow position
        // taking enroute or TMA airspace - see PrimaryPosition.MayOwn. Nothing claimed.
        if (!PrimaryPosition.MayOwn(sector, Network.Me?.Callsign))
            return Task.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());

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
            ReleaseRefreshGate();
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

        // Same rule as CanSync. The request would fail before it was sent, and the catch below would
        // put that in the Errors window on every connect to LocalHost or an unrecognised SweatBox.
        if (NetworkServer.Current == null)
        {
            ActionLog.Log("Resume", "skipped - OzServer does not operate on this server");
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
            // A backend without /sectors/resume, or any transient failure - fall back to the plain
            // connect refresh so a session still starts correctly, just without recovering anything.
            Errors.Add(new Exception($"Couldn't resume the previous session: {ex.Message}", ex), "OzServer");
            await RefreshFromServerIfIdleAsync();
        }
    }

    // Puts what a resume handed back into MMI.SectorsControlled, which nothing else does.
    //
    // ApplySync only pushes an ownership *change* into MMI, and a resume is usually not one: on the
    // first connect after vatSys restarts, the resume's sync is the baseline, which is recorded
    // without touching MMI; on a reconnect within the same run, Owned still holds the previous
    // session's list, so the resumed sectors diff as unchanged. Either way MMI was left holding only
    // the position defaults AfvSectorClaimer.Init() grants on connect, the extended sectors stayed
    // owned on OzServer but absent locally, and every tag TagResumeRecovery was handed inside them
    // failed with "(no sector)".
    //
    // Adds, never removes, and leaves VSCS alone - the same rule ReconcileMmiWithOwned applies to a
    // gained sector.
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

    async Task RefreshFromServerCoreAsync()
    {
        OzServerSyncDto sync;
        try
        {
            sync = await _api.GetSyncAsync();
        }
        catch (Exception ex)
        {
            // Once per outage rather than once per three-second poll - see ErrorThrottle.
            ErrorThrottle.Report("sector-refresh", $"Couldn't refresh sector state from OzServer: {ex.Message}", ex);
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

        // Right here as well as on the poll tick, because the tick is up to three seconds away and
        // the gap is visible: on a reconnect the resume hands back a position's extended sectors
        // and they are owned from that instant, but the scope has only the position's own defaults
        // until something puts them back. Waiting for the tick showed the controller their airspace
        // arriving several seconds late - and TagResumeRecovery, which resolves each resumed tag
        // against MMI.SectorsControlled, spent those seconds reporting "no radar position inside a
        // held sector" for tags that were already theirs.
        //
        // Runs for the baseline sync too, deliberately. The baseline is not pushed into MMI as a
        // *change* (no gained/lost diff, no VSCS, no tag handoffs - see above), but "OzServer says
        // this is yours" and "it is not on your scope" is drift whenever it is noticed, including
        // on the first answer of a session, which is exactly when a resume lands.
        RunOnUiThread(RestoreOwnedToMmi);

        OwnedChanged?.Invoke(this, EventArgs.Empty);

        // Raised after controlled, requests and owned have all been adopted. Consumers that read
        // more than just requests, especially the Sector Management window's controlled snapshot,
        // must not observe a half-applied sync.
        Refreshed?.Invoke(this, EventArgs.Empty);
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

        var mmiSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        // Gaining a sector adds it to MMI (so the airspace and its tags appear) but deliberately does
        // NOT switch its VSCS line to Transmit - issue #5. Forcing Transmit on put the controller on
        // a frequency they never asked to be on, as a side effect of accepting a request; whether to
        // actually talk on a sector is theirs to decide, and the VSCS panel is where they decide it.
        foreach (var sector in gained)
        {
            if (!mmiSectors.Any(s => s.Equals(sector)))
                mmiSectors.Add(sector);
        }

        // Pushed to MMI *before* OwnershipChanged fires below, with the lost sectors still in the
        // list - not removed yet, see the second call further down for why. SectorTagHandoff's
        // accept path - MMI.AcceptJurisdiction -> FDP2.AcceptJurisdiction -> MMI.SetTrackState -
        // decides Jurisdiction vs. a demoted state (Announced/Preactive/NonJurisdiction) by checking
        // MMI.SectorsControlled.Contains(fdr.ControllingSector) at the exact moment it runs.
        //
        // Firing the event first - as this used to - let that check run against the sector list from
        // before this gain, so a just-accepted tag could be computed as Announced instead of
        // Jurisdiction despite AcceptTransfer itself logging success, because all it actually checks
        // is fdr.State (STATE_CONTROLLED), never the track state SetTrackState wrote. vatSys does
        // reassert every FDR's track state again at the end of MMI.SetControlledSectors, which is why
        // this was intermittent rather than universal - but if a track had a PostJurisdiction timer
        // still armed from an earlier handoff (20s, see vatSys's own PostJurisdictionTimeout), that
        // reassert is skipped too and the demoted state stuck until the timer elapsed - exactly "came
        // back jurisdiction initially but then switched to Announced", needing a manual accept.
        //
        // Updating MMI first removes the race entirely: every SetTrackState call SectorTagHandoff's
        // accept can trigger already sees the correct sector list on its first try.
        if (gained.Count > 0)
            MMI.SetControlledSectors(mmiSectors.ToList());

        // GiveAway (lost) and the accept sweep (gained) both run here, against MMI.SectorsControlled
        // as it stands right now: the gained sector already added above, the lost ones deliberately
        // still present - not yet removed, on purpose. See the second MMI.SetControlledSectors call
        // below for why removing them first would silently cancel every handoff this is about to
        // make.
        OwnershipChanged?.Invoke(this, diff);

        // Only now are the lost sectors actually taken out of MMI, once GiveAway (just above) has
        // had its chance to hand off whatever it holds inside them. Removing them any earlier - as
        // this used to, in the same call as adding the gained ones - handed MMI.SetControlledSectors
        // a list that no longer contained a sector this session still had a tracked aircraft
        // sitting under, and that method's own cleanup (drops to uncontrolled anything whose
        // ControllingSector was in the old list and isn't in the new one, checked purely against
        // ControllingSector/IsTrackedByMe, with no idea an aircraft is mid-handoff) ran before
        // GiveAway ever got a chance to touch it - matching a real FDR ending up flown with nobody
        // working it, mid-transfer, with GiveAway's own log innocently reporting "no tags of ours
        // inside it" because vatSys had already stopped tracking it by the time GiveAway checked.
        // GiveAway itself also reassigns ControllingSector to the new owner the moment it hands an
        // aircraft off (see its own comment), which is what keeps this second call from re-catching
        // the same aircraft on its way out: by now ControllingSector points at the new owner's
        // sector, which was never in this session's own list to begin with.
        foreach (var sector in lost)
        {
            mmiSectors.RemoveAll(s => s.Equals(sector));

            var freq = Audio.VSCSFrequencies.FirstOrDefault(f => f.Name == sector.Callsign);
            if (freq is { Transmit: true })
                freq.Transmit = false;
        }

        if (lost.Count > 0)
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

    // Connected, and to a server OzServer actually operates on. Anywhere else - LocalHost, or a
    // SweatBox hostname NetworkServer does not recognise - every call fails before it is sent (see
    // OzServerApiClient.GetCredentials), so a refresh there is a guaranteed failure, and the poll
    // put one in front of the controller as an error every few seconds for the whole session.
    static bool CanSync => Network.IsConnected && NetworkServer.Current != null;

    public async Task ClaimAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected || !IsRealAtc)
            return;

        // The conflict is handled *after* the try block, not inside the catch clause that detects
        // it. An exception thrown from within a catch clause is not seen by that same try's other
        // catch clauses, so HandleConflictAsync throwing (it opens a window and posts to the UI
        // thread with BeginInvoke - either can fail on a form that is going away) escaped this
        // method entirely, straight out through the async void click
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

        // A flow position never claims enroute or TMA airspace - see PrimaryPosition.MayOwn. What
        // another controller holds it may ask for as relief cover, so a staged claim on one of those
        // goes as a request instead; what nobody holds it cannot take at all. The backend enforces
        // both; this turns the first into what the controller meant and says why for the second.
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
        // otherwise just fail without a reason.
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

        // Covers both the batched commit and the per-sector fallback below, and the whole group
        // each release takes with it - RestoreOwnedToMmi must not put a sector back on screen in
        // the window between this Apply removing it and Owned catching up. See MarkReleased.
        MarkReleased(toRelease
            .SelectMany(sector => PrimaryPosition.CoveredBy(sector))
            .Select(sector => sector.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase));

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

        MarkGroupReleased(sector);

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

    // Gives a sector back because its own VSCS line stopped transmitting - the mirror image of
    // AfvSectorClaimer's claim-on-transmit, and the only release not driven by the Sectors window's
    // arrow button. It has to be called explicitly from there: the MMI-driven path
    // (ClaimMmiControlledSectorsAsync) is claim-only on purpose, so a sector dropping out of
    // MMI.SectorsControlled never releases anything by itself. Without this, dropping a line to
    // Idle took the sector's infill off the scope - AfvSectorClaimer.CheckActive removing it from
    // MMI - while OzServer went on recording it as this controller's, so it stayed in the OzServer
    // Sectors window and stayed unavailable to everybody else.
    //
    // Releasing the sector by name is enough for the whole group that went with it: the backend
    // releases the named sector plus every sector it is responsible for that this controller owns
    // (SectorOwnershipController::releaseGroup, walking the same ResponsibleSectors relation vatSys
    // builds Sector.SubSectors from), which is exactly the set CheckActive takes out of MMI.
    //
    // Kept separate from ReleaseAsync above rather than folded into it, for two reasons:
    //
    //  - 403 is an ordinary answer here, not a fault worth putting the Errors window over the
    //    scope. The backend refuses a release from anyone who isn't the current owner, and a line
    //    can legitimately be dropped to Idle for a sector OzServer never recorded as this
    //    controller's: cover withheld on login, a claim that lost a conflict, or a sector some
    //    other path released a moment ago. The arrow button is an explicit "give this back" and
    //    deserves to be told when it fails; this is a side effect of a frequency press and stays
    //    quiet.
    //  - It deliberately does NOT check Owned first. Owned trails the server by a round trip, so a
    //    line pressed to Transmit and back to Idle inside that window reads as "not mine yet" and
    //    would skip the very release this exists for - leaving behind exactly the drift it is
    //    meant to prevent. The server is the source of truth for ownership (see the class comment);
    //    ask it and let it decide.
    public async Task ReleaseIdleVscsLineAsync(SectorsVolumes.Sector sector)
    {
        // Same gates as the claim path: nothing to release while offline or on a server OzServer
        // doesn't operate on, and an observer never owned it in the first place.
        if (!CanSync || !IsRealAtc)
            return;

        // Before the call, so the whole round trip is covered - see MarkReleased. The sector is
        // already out of MMI by now (CheckActive removed it), and Owned still has it until the
        // refresh below, which is exactly the state RestoreOwnedToMmi would otherwise repair. The
        // whole group, because that is what both CheckActive and the backend take with it.
        MarkGroupReleased(sector);

        try
        {
            await _api.ReleaseSectorAsync(sector.Name);
        }
        catch (OzServerApiException ex) when (ex.StatusCode == 403)
        {
            // Not this controller's to give back - nothing changed, so there is nothing to refresh
            // either. Still recorded: "the line went Idle and OzServer already didn't have it" is
            // the answer to why a sector did not disappear from the Sectors window.
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
            // Throttled rather than raised per press: a backend that is down answers every one of
            // these the same way, and the controller can do nothing about it.
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

    // A release hands back the named sector's *whole* covered group, and that group can contain
    // this session's own position. A controller logged on as a bare sub-sector (say SNO) who
    // extended into the parent group it belongs to (WOL) and then drops WOL's line is releasing a
    // group that covers SNO itself - so the one sector they are certainly entitled to goes with the
    // extension they meant to give up, and they are left logged on to a position OzServer no longer
    // records as theirs by anyone's reckoning.
    //
    // Taken straight back rather than prevented: the alternative is refusing the release outright
    // whenever the group happens to cover the position, which would leave exactly the drift this
    // whole path exists to remove. The sector was released by this same controller a moment ago,
    // so nobody else holds it and the claim is an ordinary one that cannot conflict, and awaiting it before the refresh
    // below means Owned is only ever read once the position is back - no flicker of the controller's
    // own sector through MMI and the Sectors window.
    //
    // Scoped to the login sector alone, not everything held by right (DefaultSectorsForCurrentSession):
    // claiming a sector re-creates its whole group server-side, so re-claiming the broader set would
    // resurrect sub-sectors this controller had deliberately dropped earlier - including, when the
    // sector just released is itself part of the position's group, the very one being released.
    async Task ReclaimOwnPositionAfterReleaseAsync(SectorsVolumes.Sector released)
    {
        var ownSector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == Network.Me?.Callsign);
        if (ownSector == null || ownSector.Equals(released))
            return;

        // CoveredBy mirrors the backend's own covered() walk, which is what releaseGroup used to
        // decide the set it just deleted.
        if (!PrimaryPosition.CoveredBy(released).Any(covered => covered.Equals(ownSector)))
            return;

        try
        {
            await ClaimWithExclusionsAsync(ownSector);
            // Kept after all, so no longer excused: anything of the position's own group that the
            // dropped line took off the scope should come back now rather than when the excuse
            // (which covers the released group as a whole) happens to run out.
            UnmarkReleased(PrimaryPosition.CoveredBy(ownSector).Select(covered => covered.Name));
            ActionLog.Log("Ownership",
                $"Took {ownSector.Name} back - releasing {released.Name} covered this session's own position", new
                {
                    action = "vscs_idle_release_keep_own_position",
                    sector = ownSector.Name,
                    released = released.Name,
                    own_callsign = Network.Me?.Callsign
                });
        }
        catch (Exception ex)
        {
            // Worth a window of its own: unlike the release itself, this one failing means the
            // controller is sitting on a position OzServer does not record as theirs, which no
            // other path will notice and put right on its own.
            Errors.Add(new Exception(
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
    // action (the arrow button, or a VSCS line dropping out of Transmit, which calls
    // ReleaseIdleVscsLineAsync itself rather than leaving it to be guessed from here) or a genuine
    // conflict, handled below.
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
            // Already owned, covered by its own parent's claim, or backed off - see NeedsClaim,
            // which RetryUnclaimedMmiSectors shares so the two can never disagree about whether
            // there is anything here to do.
            if (!NeedsClaim(sector, target))
                continue;

            // Same catch-clause escape as ClaimAsync - see the comment there. Here it would have
            // aborted the whole loop partway through, silently skipping every sector after this
            // one, rather than just failing the one that conflicted.
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
                Errors.Add(new Exception($"Couldn't resolve the ownership conflict on {sector.Name}: {ex.Message}", ex), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // One or more of sector's own covered sub-sectors is already owned by someone else - e.g. GUN
    // extending WOL while BLA already owns WOL's own sub-sector SNO (extended earlier). Requests
    // what has to be requested, then claims sector regardless, excluding whichever sub-sectors are
    // still contested either way. Whatever's still contested is also pulled back out of
    // MMI.SectorsControlled and dropped to Idle on its own VSCS line if it has one - AfvSectorClaimer's
    // CheckActive() adds a whole primary's group to MMI optimistically before any of this is known,
    // so a sub-sector that turns out not to actually be claimable has to come back out again rather
    // than sit in MMI as if this controller owns it when OzServer disagrees.
    async Task HandleConflictAsync(SectorsVolumes.Sector sector, IReadOnlyList<OzServerSectorConflictDto> conflicts)
    {
        // This session's own position is requested without asking. A controller logging on to BLA
        // while Huon holds it wants BLA; "request it from them?" is not a real question, and "no"
        // would leave them sitting on a position they cannot work.
        //
        // Requested, not taken. This used to be a background re-claim that waited for the holder's
        // client to release on its own (PrimaryPositionWatcher), and the backend let the primary
        // override a present owner outright - so the airspace, and the aircraft in it, moved the
        // moment BLA connected with Huon given no say at all. The controller working the airspace
        // decides when it moves, the same as for any other sector.
        //
        // Also requested without asking: anything else the same controller holds that a requested
        // sector covers. Accepting BLA hands over BLA's whole covered group (transferRequest walks it),
        // so MAE's departures and radar sectors arrive with it whatever the answer to a question
        // about them would have been - asking would only have pretended there was a choice.
        //
        // Never anything an FMP holds: that is relief cover its controller agreed to (see
        // IsReliefCovered), and asking for it back - automatically or by prompt - is the FMP's
        // decision to end, not a claim's. It is still left out of the claim below like any conflict.
        //
        // Nor anything a transmit press is already asking about (IsHeldForTransmitPrompt) - that
        // prompt is the one question about it.
        var reliefCover = conflicts.Where(c => PrimaryPosition.IsFlowCallsign(c.Owner?.Callsign)
                                               || IsHeldForTransmitPrompt(c.Sector)).ToList();
        if (reliefCover.Count > 0)
            ActionLog.Log("Ownership",
                $"Left {string.Join(", ", reliefCover.Select(c => c.Sector))} with {string.Join(", ", reliefCover.Select(c => c.Owner?.Callsign).Distinct())} - FMP relief cover, or already being asked about from a transmit press",
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
        var asked = mineByRight.Concat(carriedWith).ToList();
        var contestedByOthers = conflicts.Where(c => !asked.Contains(c) && !reliefCover.Contains(c)).ToList();

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

        if (contestedByOthers.Count > 0 && await AskYesNoAsync(question, "Sector already owned"))
            asked.AddRange(contestedByOthers);

        await RequestAsOneGroupAsync(asked);

        // Only when the conflict left something to take. Every claim used to be followed by this
        // second one regardless, and now that a position's own sectors are a conflict like any
        // other, logging on to a position somebody present holds contests all of it - so the retry
        // excluded every sector the claim covers, the backend answered a claim for nothing with 404,
        // and "Couldn't claim BLA on OzServer: Sector not found." went up in the Errors window over
        // a sector that exists and had just been requested successfully.
        var leftOut = new HashSet<string>(
            conflicts.Select(c => c.Sector).Concat(PrimaryPosition.StaffedCoveredSectors(sector, Network.Me?.Callsign)),
            StringComparer.OrdinalIgnoreCase);

        if (PrimaryPosition.CoveredBy(sector).Any(covered => !leftOut.Contains(covered.Name)))
        {
            try
            {
                await ClaimWithExclusionsAsync(sector, conflicts.Select(c => c.Sector));
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't claim {sector.Name} on OzServer: {ex.Message}", ex), "OzServer");
            }
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

    static bool Covers(string root, string name)
    {
        var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == root);
        return sector != null && PrimaryPosition.CoveredBy(sector).Any(covered => covered.Name == name);
    }

    // Every sector in one request group, so each holder is asked once about the whole thing - the
    // same thing an Apply in the Sectors window does. These used to go one POST per sector, each its
    // own group: BLA's logon put four separate request windows in front of Huon at once.
    //
    // Skips anything already asked for and still waiting. The conflict that produces these can
    // happen again while the request is pending - another VSCS press, a re-grant into MMI from
    // another plugin - and a second request for the same sector is refused by the backend (one per
    // sector per requester), which would otherwise surface as an error about a request that is in
    // fact sitting there fine.
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

            // The sync this answered with is left to the caller's own refresh, which every path
            // into HandleConflictAsync already ends with.
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't request {string.Join(", ", names)} on OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    // Asks the holder of sector for it - from a transmit press the controller has just confirmed
    // (AfvSectorClaimer.PromptToRequestHeldSector). The holder gets an ordinary request, marked as
    // FMP relief cover in their Sectors window when this is a flow position; if they accept, the
    // sector and its tags move the ordinary way. True once the request is on the server (or already
    // was).
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
                Errors.Add(new Exception($"Couldn't request {sector.Name} from {owner.Callsign}."), "OzServer");
            return raised;
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't request {sector.Name} from {owner.Callsign}: {ex.Message}", ex), "OzServer");
            return false;
        }
    }

    public bool HasPendingRequestFor(SectorsVolumes.Sector sector) =>
        MyRequests.ByMe.Any(r => r.RejectedAt == null && r.Sector != null
                                 && string.Equals(r.Sector.Name, sector.Name, StringComparison.OrdinalIgnoreCase));

    // Sectors a transmit press is asking about (AfvSectorClaimer.PromptToRequestHeldSector). Left
    // out of the claim pass meanwhile - the press can put them in MMI (VatpacPlugin does) before the
    // prompt takes them out again, and claiming them would raise the tracker's own "already owned"
    // question about the same thing. Long enough to cover the prompt and a request being answered.
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

    // How long the conflict question waits for an answer before it withdraws itself as "no".
    //
    // Nothing else can be claimed while it is open - the claim loop that asked is suspended on the
    // answer, and later MMI changes queue behind it - so an unanswered prompt cannot be allowed to
    // sit there for the rest of the session. "No" is the safe default: it leaves the contested
    // sectors with whoever has them, which is exactly where they already are.
    static readonly TimeSpan ConflictPromptTimeout = TimeSpan.FromSeconds(60);

    // Asks without blocking. This used to be ShowDialog, which stops the whole vatSys window taking
    // input until it is answered - and it is raised by a claim, which a VSCS transmit press or a
    // logon triggers, so it could land on a controller in the middle of working traffic and lock
    // their scope until they found and dismissed it. The same reasoning that turned every other
    // popup here into a non-modal window (see SectorMessageWindow) applies to this one; it only
    // looked different because the caller needs the answer, and awaiting it does that just as well.
    static Task<bool> AskYesNoAsync(string message, string caption)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Show()
        {
            try
            {
                var prompt = new SectorConflictPromptWindow(message, caption);
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

        // Belt and braces for the timeout above, which is a WinForms timer and so only fires where
        // there is a message loop. RunOnUiThread falls back to running inline when it finds no main
        // form, and a window opened on a pool thread would never tick - leaving the claim loop that
        // asked waiting on an answer for the rest of the session.
        _ = Task.Delay(ConflictPromptTimeout + TimeSpan.FromSeconds(10))
            .ContinueWith(_ => answer.TrySetResult(false), TaskScheduler.Default);

        return answer.Task;
    }

    // Fire-and-forget UI-thread marshaling for callers (the MMI.SectorsControlledChanged
    // subscription) that don't need anything back - see AskYesNoAsync above for the awaited
    // variant used when an answer is needed.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
