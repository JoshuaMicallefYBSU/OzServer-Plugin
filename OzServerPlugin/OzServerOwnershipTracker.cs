using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

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
// MMI.SectorsControlled and its VSCS line switched to Transmit; a sector lost (I accepted an
// incoming request, giving it away, or released it myself) is removed and dropped back to Idle.
// Polls independently of OzServerSectorsWindow (RefreshTimer below) specifically so an accepted
// request still reaches this controller's VSCS panel even if they never open that window - the
// other controller's Accept click only changes ownership on the server, with no direct signal to
// this session at all otherwise.
public class OzServerOwnershipTracker
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    public event EventHandler? OwnedChanged;
    // Fires with the new count whenever the number of incoming ("Requested From Me") requests
    // changes - polled here rather than left to OzServerSectorsWindow alone so a plugin-wide
    // notification (see Plugin's own navbar indicator) works even if that window has never been
    // opened this session.
    public event EventHandler<int>? PendingRequestCountChanged;

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _pollTimer;
    List<SectorsVolumes.Sector> _owned = new();
    // False until RefreshFromServerAsync has run once - the very first result is this session's
    // starting baseline (whatever was already owned from a previous session, claimed elsewhere,
    // etc.), not something *newly* gained just now, so it's recorded without also seizing
    // MMI/VSCS for it - only actual changes from here on do that.
    bool _hasBaseline;
    int _pendingRequestCount = -1;
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

    public IReadOnlyList<SectorsVolumes.Sector> Owned => _owned;

    public OzServerOwnershipTracker()
    {
        MMI.SectorsControlledChanged += (_, _) => RunOnUiThread(OnMmiSectorsControlledChanged);
        // Connected fires the moment a session actually comes up, which is a faster path to a
        // first-connect refresh than waiting up to PollInterval for the timer below to notice.
        Network.Connected += (_, _) =>
        {
            _ = RefreshFromServerIfIdleAsync();
            _ = RefreshPendingRequestCountAsync();
        };
        _pollTimer = new System.Threading.Timer(_ =>
        {
            if (!Network.IsConnected)
                return;

            _ = RefreshFromServerIfIdleAsync();
            _ = RefreshPendingRequestCountAsync();
        }, null, PollInterval, PollInterval);

        if (Network.IsConnected)
        {
            _ = RefreshFromServerIfIdleAsync();
            _ = RefreshPendingRequestCountAsync();
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

    async Task RefreshPendingRequestCountAsync()
    {
        OzServerMyRequestsDto requests;
        try
        {
            requests = await _api.GetMyRequestsAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't refresh pending sector requests: {ex.Message}", ex), "OzServer");
            return;
        }

        var count = requests.FromMe.Count;
        if (count == _pendingRequestCount)
            return;

        _pendingRequestCount = count;
        PendingRequestCountChanged?.Invoke(this, count);
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
        if (!await _refreshGate.WaitAsync(0))
            return;

        try
        {
            await RefreshFromServerCoreAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    async Task RefreshFromServerCoreAsync()
    {
        List<OzServerSectorDto> mine;
        try
        {
            mine = await _api.GetMySectorsAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't refresh Owned from OzServer: {ex.Message}", ex), "OzServer");
            return;
        }

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
            RunOnUiThread(() => ReconcileMmiWithOwned(previous, current));
        else
            _hasBaseline = true;

        OwnedChanged?.Invoke(this, EventArgs.Empty);
    }

    // Pushes an Owned change onto MMI.SectorsControlled and the matching VSCS line - see the class
    // comment. Runs on the UI thread (RunOnUiThread callers) since both of those feed real vatSys
    // UI. Calling MMI.SetControlledSectors here re-fires MMI.SectorsControlledChanged, which calls
    // ClaimMmiControlledSectorsAsync -> RefreshFromServerAsync again - harmless: by then Owned
    // already matches what was just pushed, so that second pass finds no further diff and stops.
    void ReconcileMmiWithOwned(List<SectorsVolumes.Sector> previous, List<SectorsVolumes.Sector> current)
    {
        var gained = current.Where(s => !previous.Any(p => p.Equals(s))).ToList();
        var lost = previous.Where(p => !current.Any(s => s.Equals(p))).ToList();

        if (gained.Count == 0 && lost.Count == 0)
            return;

        var mmiSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        foreach (var sector in gained)
        {
            if (!mmiSectors.Any(s => s.Equals(sector)))
                mmiSectors.Add(sector);

            var freq = Audio.VSCSFrequencies.FirstOrDefault(f => f.Name == sector.Callsign);
            if (freq is { Transmit: false })
                freq.Transmit = true;
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

    public async Task ClaimAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected)
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
            await _api.ClaimSectorAsync(sector.Name);
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

    public async Task ReleaseAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected)
            return;

        try
        {
            await _api.ReleaseSectorAsync(sector.Name);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }

        await RefreshFromServerAsync();
    }

    public async Task<OzServerSectorOwnershipRequestDto?> RequestAsync(SectorsVolumes.Sector sector)
    {
        if (!Network.IsConnected)
            return null;

        return await _api.RequestSectorAsync(sector.Name);
    }

    public async Task AcceptRequestAsync(int requestId)
    {
        if (!Network.IsConnected)
            return;

        await _api.AcceptRequestAsync(requestId);
        await RefreshFromServerAsync();
    }

    // Prefer this over calling AcceptRequestAsync once per id when accepting more than one at a
    // time - see AcceptRequestsBatchAsync's own comment on the API client for why firing separate
    // accept calls back-to-back could leave a request row behind. Returns the raw per-request
    // results so the caller can report anything that didn't go through (already accepted/rejected
    // by someone else in the meantime, no longer the current owner, ...).
    public async Task<List<OzServerAcceptBatchResultDto>> AcceptRequestsBatchAsync(IEnumerable<int> requestIds)
    {
        if (!Network.IsConnected)
            return new List<OzServerAcceptBatchResultDto>();

        var response = await _api.AcceptRequestsBatchAsync(requestIds);
        await RefreshFromServerAsync();
        return response.Results;
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
                await _api.ClaimSectorAsync(sector.Name);
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
        var question = conflicts.Count == 1
            ? $"{conflicts[0].Sector} is already owned by {conflicts[0].Owner?.Callsign}. Request it from them?"
            : $"These are already owned by someone else: {string.Join(", ", conflicts.Select(c => $"{c.Sector} ({c.Owner?.Callsign})"))}. Request them?";

        if (AskYesNo(question, "Sector already owned"))
        {
            foreach (var conflict in conflicts)
            {
                var conflictSector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == conflict.Sector);
                if (conflictSector == null)
                    continue;

                try
                {
                    await _api.RequestSectorAsync(conflictSector.Name);
                }
                catch (Exception ex)
                {
                    Errors.Add(new Exception($"Couldn't request {conflictSector.Name} on OzServer: {ex.Message}", ex), "OzServer");
                }
            }
        }

        try
        {
            await _api.ClaimSectorAsync(sector.Name, conflicts.Select(c => c.Sector));
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
