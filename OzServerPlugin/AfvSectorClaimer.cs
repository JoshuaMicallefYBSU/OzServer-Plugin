using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Drives MMI.SectorsControlled from the VSCS panel's Transmit state, after VatpacPlugin's
// Sectors.cs (Init/CheckActive). This class never calls OzServerApiClient: it writes
// MMI.SectorsControlled and goes through OzServerOwnershipTracker for anything on OzServer.
//
// Claiming is left downstream: a sector appearing in MMI.SectorsControlled is claimed by the
// tracker's MMI.SectorsControlledChanged handler, which also resolves a sector that turns out to
// be owned by someone else (OzServerOwnershipTracker.HandleConflictAsync). A correct
// MMI.SectorsControlled is also all that vatSys's own Sectors window and map infill need.
//
// Releasing is not symmetrical. The tracker's MMI handler is claim-only - inferring a release
// from a sector's absence in MMI fights a VSCS transmit claim in a claim/release loop - so a
// sector dropping out of MMI releases nothing by itself. CheckActive therefore asks the tracker
// for the release explicitly when a line goes idle; otherwise the sector's infill would leave the
// map while OzServer still listed it as this controller's, unavailable to everyone else.
//
// Controller Info is not touched here: the "Extending ..." line is another plugin's job, and two
// plugins editing the same free-text field would fight over it.
public class AfvSectorClaimer
{
    readonly OzServerOwnershipTracker _tracker;
    readonly HashSet<VSCSFrequency> _subscribed = new();
    // Network.Me.Callsign when Init() last granted a position. VSCSFrequenciesChanged fires both
    // for a position change and for a line added or removed while working the same position, and
    // only the former re-baselines - see the handler.
    string? _lastPrimaryCallsign;

    // Network.Connected fires before the data Init() depends on is usable: Network.Me can still be
    // null, and DefaultSectorsFor matches its callsign against the profile's SectorsVolumes.Sectors.
    // Nothing else writes MMI.SectorsControlled on connect, so Init() is retried until it has
    // granted the position's sectors.
    static readonly TimeSpan InitRetryInterval = TimeSpan.FromSeconds(1);
    const int InitMaxAttempts = 20;

    // How long the first claim waits for vatSys to validate the controllers already online - see
    // AwaitingAtcValidation.
    static readonly TimeSpan AtcValidationWait = PrimaryPosition.ValidationWait;
    DateTime _initStartedAt = DateTime.MinValue;
    readonly System.Threading.Timer _initRetryTimer;
    int _initAttemptsLeft;

    // Puts the controller back on the frequencies they were working before a disconnect. The
    // sectors come back by themselves (POST /sectors/resume); the VSCS panel does not - vatSys
    // rebuilds it Idle every session.
    //
    // Retried, and for longer than Init: vatSys rebuilds the panel's lines, the network has to
    // confirm the session as a controller before vatSys will transmit (Network.ValidATC, the gate
    // its own Transmit button uses), and the sectors only become this controller's again when the
    // resume lands a round trip later. Each arrives at its own pace after a connect.
    static readonly TimeSpan TransmitRestoreInterval = TimeSpan.FromSeconds(1);
    const int TransmitRestoreMaxAttempts = 45;
    readonly System.Threading.Timer _transmitRestoreTimer;
    // Lines still to put back, by VSCS line name. Each is removed once dealt with - restored, or
    // found already transmitting - so no line is written twice, and a line the controller switches
    // off during the restore window stays off.
    readonly HashSet<string> _transmitToRestore = new(StringComparer.OrdinalIgnoreCase);
    int _transmitRestoreAttemptsLeft;

    public AfvSectorClaimer(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;

        // Network.Connected, Audio.VSCSFrequenciesChanged and the retry timers all end in
        // MMI.SetControlledSectors, which touches vatSys's map and UI state, and none of them is
        // guaranteed to fire on the UI thread (vatSys installs no SynchronizationContext on its
        // FSDSession).
        Network.Connected += (_, _) => RunOnUiThread(() =>
        {
            BeginInit();
            BeginTransmitRestore();
        });
        // A list left over from an ended session must not be applied to whatever position this
        // client connects as next.
        Network.Disconnected += (_, _) => RunOnUiThread(StopTransmitRestore);
        // Re-baselines through Init() only when the primary callsign has changed since Init() last
        // ran, i.e. for a position change. Init() resets MMI.SectorsControlled to the position's
        // defaults, which for a line added mid-session would take every extension off the scope
        // while OzServer went on recording them as this controller's. A null Network.Me is not a
        // change: it lags Connected and is briefly null again around a reconnect.
        Audio.VSCSFrequenciesChanged += (_, _) => RunOnUiThread(() =>
        {
            ResubscribeTransmitChanged();

            var callsign = Network.Me?.Callsign;
            if (!string.IsNullOrEmpty(callsign) && callsign != _lastPrimaryCallsign)
                BeginInit();
        });
        _initRetryTimer = new System.Threading.Timer(
            _ => RunOnUiThread(RetryInit), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _transmitRestoreTimer = new System.Threading.Timer(
            _ => RunOnUiThread(RetryTransmitRestore), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        ResubscribeTransmitChanged();
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }

    // Always queued, even from the UI thread - runs after whatever event is being handled right now
    // has finished with every other subscriber.
    static void DeferOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control { IsHandleCreated: true } mainForm)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }

    void BeginInit()
    {
        if (OzServerMode.Disabled)
            return;

        _initAttemptsLeft = InitMaxAttempts;
        _initStartedAt = DateTime.UtcNow;
        RetryInit();
    }

    void StopInitRetries() =>
        _initRetryTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    void RetryInit()
    {
        if (OzServerMode.Disabled)
            return;

        // The session went away while this was waiting for it to become ready.
        if (!Network.IsConnected)
        {
            StopInitRetries();
            return;
        }

        // An observer is never granted a position; their airspace is mirrored from whoever holds
        // it (ObserverPositionMirror). NetworkIdentity.IsObserver is correct immediately, unlike
        // Network.Me.IsRealATC, which reads false for a real controller just after Connected.
        if (NetworkIdentity.IsObserver)
        {
            StopInitRetries();
            return;
        }

        if (Init() || --_initAttemptsLeft <= 0)
        {
            StopInitRetries();
            return;
        }

        // One-shot reschedule, not a repeating period, so a slow Init() cannot overlap itself.
        _initRetryTimer.Change(InitRetryInterval, System.Threading.Timeout.InfiniteTimeSpan);
    }

    // Audio.VSCSFrequencies is a fresh array on every call, and a position change rebuilds the
    // VSCSFrequency instances, so subscriptions are diffed by reference against what is already
    // subscribed.
    void ResubscribeTransmitChanged()
    {
        var current = Audio.VSCSFrequencies;

        foreach (var freq in _subscribed.Except(current).ToList())
        {
            freq.TransmitChanged -= OnLineStateChanged;
            freq.ReceiveChanged -= OnReceiveChanged;
            _subscribed.Remove(freq);
        }

        foreach (var freq in current.Except(_subscribed))
        {
            freq.TransmitChanged += OnLineStateChanged;
            freq.ReceiveChanged += OnReceiveChanged;
            _subscribed.Add(freq);
        }
    }

    // ReceiveChanged matters only for the line going idle - see OnLineStateChanged. A Transmit press
    // raises it too, after TransmitChanged has handled the press, and running the claim again would
    // repeat anything it shows (a transmit prompt is de-duplicated, the FMP "cannot assume" notice
    // is not).
    void OnReceiveChanged(object? sender, EventArgs e)
    {
        if (sender is VSCSFrequency { Transmit: false, Receive: false })
            OnLineStateChanged(sender, e);
    }

    // Both events, because Idle and Receive cannot be told apart from TransmitChanged alone.
    // vatSys's VSCS buttons (VSCSControlDX, VSCS_Button_Freq_*) set the two flags in a fixed order:
    //
    //   Transmit  Transmit = true,  then Receive = true
    //   Receive   Transmit = false, then Receive = true
    //   Idle      Transmit = false, then Receive = false
    //
    // so when TransmitChanged fires for Receive or Idle, Receive still reads true from the
    // transmitting state it is leaving. Only ReceiveChanged, which comes second, shows which button
    // it was. Both setters raise their event on every write, changed or not, so pressing Idle on a
    // line already on Receive still arrives here as a ReceiveChanged with both flags off.
    void OnLineStateChanged(object? sender, EventArgs e)
    {
        if (OzServerMode.Disabled)
            return;

        if (sender is VSCSFrequency frequency)
            CheckActive(frequency);

        // After CheckActive, so what is written is the state the press has settled into. Saved on
        // every change rather than on disconnect - see VscsTransmitMemory.
        VscsTransmitMemory.Remember();
    }

    // Starts putting back the frequencies this callsign was transmitting on before it dropped.
    void BeginTransmitRestore()
    {
        if (OzServerMode.Disabled)
            return;

        StopTransmitRestore();

        if (!Network.IsConnected || NetworkIdentity.IsObserver)
            return;

        // Read once, at connect. Re-reading it on every attempt would pick up the memory this
        // session is itself writing (OnLineStateChanged saves on every change), so a restore the
        // controller undid would keep coming back.
        foreach (var line in VscsTransmitMemory.Recall(Network.Me?.Callsign))
            _transmitToRestore.Add(line);

        if (_transmitToRestore.Count == 0)
            return;

        _transmitRestoreAttemptsLeft = TransmitRestoreMaxAttempts;
        RetryTransmitRestore();
    }

    void StopTransmitRestore()
    {
        _transmitToRestore.Clear();
        _transmitRestoreTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    void RetryTransmitRestore()
    {
        if (OzServerMode.Disabled)
            return;

        if (!Network.IsConnected || NetworkIdentity.IsObserver)
        {
            StopTransmitRestore();
            return;
        }

        RestoreRememberedTransmits();

        if (_transmitToRestore.Count == 0 || --_transmitRestoreAttemptsLeft <= 0)
        {
            // Whatever is left was not this controller's to take back - a sector somebody else
            // picked up while they were away, or a line the panel no longer has. Logged, so a
            // frequency missing after a reconnect can be explained.
            if (_transmitToRestore.Count > 0)
                ActionLog.Log("Primary",
                    $"Did not put back transmit on {string.Join(", ", _transmitToRestore.OrderBy(n => n))} - not owned here after reconnecting",
                    new
                    {
                        action = "vscs_transmit_not_restored",
                        lines = _transmitToRestore.OrderBy(n => n).ToArray(),
                        own_callsign = Network.Me?.Callsign
                    });

            StopTransmitRestore();
            return;
        }

        // One-shot reschedule, as for the init retry, so a slow pass cannot overlap itself.
        _transmitRestoreTimer.Change(TransmitRestoreInterval, System.Threading.Timeout.InfiniteTimeSpan);
    }

    // One pass over the lines still to put back. Each is dropped from the set as soon as it is
    // settled, so nothing is written twice.
    void RestoreRememberedTransmits()
    {
        if (OzServerMode.Disabled)
            return;

        // The gate vatSys's own Transmit button uses (VSCSControlDX's Freq_Trans clickspot): until
        // the network has confirmed this session as a controller, setting Transmit puts the panel
        // in a state AFV does not carry.
        if (!Network.ValidATC)
            return;

        foreach (var name in _transmitToRestore.ToList())
        {
            var frequency = Audio.VSCSFrequencies.FirstOrDefault(f => f.Name == name);

            // The panel may still be being rebuilt - vatSys populates it over the first seconds of
            // a session - so the line is kept for a later pass.
            if (frequency == null)
                continue;

            // Done only when both are on: Transmit without Receive is the broken state the write
            // below avoids, not a restored line.
            if (frequency.Transmit && frequency.Receive)
            {
                _transmitToRestore.Remove(name);
                continue;
            }

            var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == name);

            // Only what OzServer agrees is this controller's: a sector somebody else picked up in
            // the meantime is theirs, and transmitting on it would talk over them. A sector not
            // owned yet may still arrive - the resume lands a round trip after connect - so it
            // stays in the set for the next pass.
            if (sector == null || !_tracker.IsMine(sector))
                continue;

            // Both flags, in the order vatSys's own Transmit button writes them (VSCSControlDX,
            // VSCS_Button_Freq_Trans). AFV only builds transceivers for lines with Receive on
            // (AFV.UpdateTransceivers), and a line comes back from a reconnect Idle - Transmit
            // alone would light the button with nothing keyed on AFV.
            frequency.Transmit = true;
            frequency.Receive = true;
            _transmitToRestore.Remove(name);

            ActionLog.Log("Primary", $"Put transmit back on {sector.Name} after reconnecting", new
            {
                action = "vscs_transmit_restored",
                sector = sector.Name,
                line = name,
                own_callsign = Network.Me?.Callsign
            });
        }
    }

    // Whether to hold the position's grant back until vatSys has validated the controllers already
    // online, so their top-down cover is left out from the start - see
    // PrimaryPosition.AwaitingValidationFor. At most AtcValidationWait from the first attempt.
    bool AwaitingAtcValidation(string? ownCallsign)
    {
        if (DateTime.UtcNow - _initStartedAt > AtcValidationWait)
            return false;

        return PrimaryPosition.DefaultSectorsFor(ownCallsign)
            .Any(sector => PrimaryPosition.AwaitingValidationFor(sector, ownCallsign));
    }

    // Sets the controlled sectors on connect or on a position change (see the
    // VSCSFrequenciesChanged handler): the primary sector matching this session's login callsign,
    // plus the rest of its group, less any top-down cover already staffed by another controller.
    // Based on VatpacPlugin's Sectors.Init(). Returns true once the position's default sectors
    // have been granted; false means "not ready yet", and RetryInit tries again.
    bool Init()
    {
        // Network.Me, VSCS lines and sector callsigns mean nothing before the network connection
        // is up, and a VSCSFrequenciesChanged can arrive before login.
        if (!Network.IsConnected)
            return false;

        var callsign = Network.Me?.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return false;

        // PrimaryPosition, not a local copy of the same rule: PrimaryPositionWatcher applies it on
        // the other controller's side to decide what to release to this session, and the two have
        // to agree or a sector is either released to nobody or never handed over.
        if (AwaitingAtcValidation(callsign))
            return false;

        var unfiltered = PrimaryPosition.DefaultSectorsFor(callsign);
        var sectors = PrimaryPosition.WithoutStaffedTopDownCover(unfiltered, callsign);
        if (sectors.Count == 0)
            return false;

        LogWithheldTopDownCover("Withheld top-down cover on login", unfiltered, sectors);

        // Anything a resume has just handed back stays. Nothing orders this against the resume,
        // and the bare defaults would take the resumed extras back out of MMI - see
        // OzServerOwnershipTracker.RecentlyResumed.
        foreach (var resumed in _tracker.RecentlyResumed)
        {
            if (!sectors.Any(s => s.Equals(resumed)))
                sectors.Add(resumed);
        }

        // Recorded only once the grant has happened. Set earlier, a failed attempt would look
        // handled, and the VSCSFrequenciesChanged handler would see no callsign change to retry on.
        _lastPrimaryCallsign = callsign;

        MMI.SetControlledSectors(sectors);
        return true;
    }

    // Recomputes MMI.SectorsControlled from one VSCS line's state - the line that raised the event
    // (see OnLineStateChanged), never the whole panel:
    //
    //   - Transmit on a bare sub-sector's line (no SubSectors of its own, e.g. SNO) adds only that
    //     sector - never its primary or siblings.
    //   - Transmit on a primary's line (e.g. WOL) adds it and its whole group, less top-down cover
    //     another controller is already online for. OzServer ownership conflicts are resolved
    //     downstream (OzServerOwnershipTracker.HandleConflictAsync), but live ATC presence is
    //     applied here so that cover is not added to MMI before the backend can reject it.
    //   - Idle (Transmit and Receive both off) removes the sector and its covered group from MMI
    //     and releases it on OzServer. Receive does not: the controller is still listening on the
    //     frequency and still holds the airspace and its tags. This session's own primary sector
    //     is never dropped, locally or on OzServer.
    //
    // Scoped to the one line rather than resweeping the panel: every other line's Transmit reads
    // as it did before the event, including sub-sectors Init() granted that have never been
    // transmitted on. A resweep would treat "controlled but not transmitting" as "just turned off"
    // and strip them - and once a sector leaves MMI.SectorsControlled, vatSys backfills it with a
    // dummy infill that transmitting again cannot reclaim.
    //
    // Sectors are compared with Sector.Equals (Callsign-based), not ==: Sector overrides Equals and
    // GetHashCode but not the == operator, so two instances of the same sector are unequal under ==.
    void CheckActive(VSCSFrequency frequency)
    {
        if (OzServerMode.Disabled)
            return;

        // As in Init(): nothing here means anything before the network connection is up.
        if (!Network.IsConnected)
            return;

        // Network.Me lags Connected (see Init), and a Transmit press can arrive in that window.
        var myCallsign = Network.Me?.Callsign;
        if (string.IsNullOrEmpty(myCallsign))
            return;

        var primarySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == myCallsign);
        if (primarySector == null)
            return;

        var frequencySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == frequency.Name);
        if (frequencySector == null)
            return;

        // Transmitting on a sector another controller holds asks whether to request it - see
        // PromptToRequestHeldSector. Checked before anything touches MMI, and whether or not
        // something else (VatpacPlugin, on this same press) has already put it there. This
        // session's own position is left to the logon path, which requests it itself.
        if (frequency.Transmit
            && !primarySector.Equals(frequencySector)
            && !_tracker.IsMine(frequencySector)
            && _tracker.ControlledByOthers.ContainsKey(frequencySector.Name))
        {
            PromptToRequestHeldSector(frequency, frequencySector, myCallsign!);
            return;
        }

        // Neither transmitting nor idle: the line is on Receive, or part-way into a Receive or Idle
        // press (see OnLineStateChanged - Transmit clears before Receive is set). Receive keeps the
        // sector, so there is nothing to do; an Idle press finishes with a ReceiveChanged that
        // arrives back here with both flags off.
        var idle = !frequency.Transmit && !frequency.Receive;
        if (!frequency.Transmit && !idle)
            return;

        // Without vatSys's placeholder for a missing login sector (MMI.SetControlledSectors adds
        // one, carrying the login callsign). Matched as the sector itself, it made a Transmit press
        // on this session's own line a no-op, so a position taken out of MMI could not be brought
        // back from the panel.
        var currentSectors = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var currentlyControlled = currentSectors.FirstOrDefault(s => s.Callsign == frequency.Name);

        // Set only by the drop branch below, and released on OzServer after the MMI write.
        SectorsVolumes.Sector? dropped = null;

        if (currentlyControlled == null)
        {
            if (idle)
            {
                ReleaseQuietLineMissingFromMmi(frequency, frequencySector, primarySector);
                return;
            }

            // A flow position never takes enroute or TMA airspace nobody holds - see
            // PrimaryPosition.MayOwn. (Airspace someone does hold was handled above: it is asked
            // for, as relief cover.) Airspace already given to this session by an accepted request
            // goes back on the scope like any other sector of ours.
            if (!PrimaryPosition.MayOwn(frequencySector, myCallsign) && !_tracker.IsMine(frequencySector))
            {
                PrimaryPosition.ReportFlowCannotAssume(new[] { frequencySector }, myCallsign);
                return;
            }

            var withheld = new HashSet<string>(
                PrimaryPosition.StaffedCoveredSectors(frequencySector, myCallsign),
                StringComparer.OrdinalIgnoreCase);

            if (!withheld.Contains(frequencySector.Name))
                currentSectors.Add(frequencySector);

            foreach (var subsector in frequencySector.SubSectors.ToList())
            {
                if (withheld.Contains(subsector.Name))
                    continue;

                if (!currentSectors.Any(s => s.Equals(subsector)))
                    currentSectors.Add(subsector);
            }

            if (withheld.Count > 0)
                ActionLog.Log("Primary",
                    $"Withheld top-down cover under {frequencySector.Name}: {string.Join(", ", withheld.OrderBy(s => s))}",
                    new
                    {
                        action = "withhold_top_down_cover_transmit",
                        sector = frequencySector.Name,
                        sectors = withheld.OrderBy(s => s).ToArray(),
                        own_callsign = myCallsign
                    });
        }
        else
        {
            if (frequency.Transmit)
                return;

            if (primarySector.Equals(currentlyControlled))
            {
                ActionLog.Log("Primary",
                    $"VSCS line for {currentlyControlled.Name} stopped transmitting - kept, it is this session's own position",
                    new
                    {
                        action = "vscs_quiet_line_kept_own_position",
                        sector = currentlyControlled.Name,
                        own_callsign = myCallsign
                    });
                return;
            }

            currentSectors.Remove(currentlyControlled);

            // The whole covered group, not just the direct sub-sectors: the release below gives back
            // everything the dropped sector is responsible for, all the way down (the backend's
            // releaseGroup walks the chain, and PrimaryPosition.CoveredBy mirrors it). Deeper
            // sub-sectors left in MMI with no owner on OzServer are what the tracker's
            // RetryUnclaimedMmiSectors claims, so they could be claimed straight back after the
            // controller had dropped the line that covered them.
            foreach (var subsector in PrimaryPosition.CoveredBy(currentlyControlled).Where(s => !s.Equals(currentlyControlled)))
            {
                // Never the position this session is logged on as, even when the dropped group is
                // responsible for it: a controller logged on as a bare sub-sector (SNO) who
                // extended into its parent group (WOL) and then drops WOL's line keeps SNO. The
                // release below takes the same group server-side, where it cannot be skipped
                // sector by sector; the tracker's ReclaimOwnPositionAfterReleaseAsync takes the
                // position straight back.
                if (primarySector.Equals(subsector))
                    continue;

                currentSectors.Remove(subsector);
            }

            dropped = currentlyControlled;
        }

        MMI.SetControlledSectors(currentSectors);

        // The release has to be asked for explicitly: the tracker's MMI.SectorsControlledChanged
        // handler is claim-only (see ClaimMmiControlledSectorsAsync and the class comment), so
        // the MMI write above releases nothing on OzServer.
        //
        // After MMI.SetControlledSectors, not before. That call is synchronous, so by the time the
        // release's refresh returns and ReconcileMmiWithOwned diffs the result, MMI already
        // agrees with it. Released first, the refresh could land while the sector was still in
        // MMI, and a sector in MMI that OzServer does not record as ours is what
        // RetryUnclaimedMmiSectors claims - undoing the release on the next poll tick.
        //
        // Fire-and-forget: TransmitChanged is raised inside vatSys's own VSCS click handling
        // (VSCSControlDX.Execute), which cannot wait on a round trip to OzServer.
        // ReleaseIdleVscsLineAsync reports its own failures and re-derives Owned when it succeeds.
        if (dropped != null)
            _ = ReleaseThenRetakeIfTransmittingAsync(frequency, dropped);
    }

    // Releases an idled line's sector, then takes it straight back if the line returned to Transmit
    // while the release was in flight.
    //
    // Without this, Transmit > Idle > Transmit inside one round trip ends with the controller on
    // the frequency and owning nothing: the second Transmit puts the sector back into MMI but the
    // claim pass skips it (Owned still lists it), and when the release's refresh lands
    // ReconcileMmiWithOwned takes it back out of MMI. CheckActive is therefore run again once the
    // release and its refresh are done, from the state an ordinary Transmit press claims from.
    async System.Threading.Tasks.Task ReleaseThenRetakeIfTransmittingAsync(VSCSFrequency frequency, SectorsVolumes.Sector sector)
    {
        await _tracker.ReleaseIdleVscsLineAsync(sector);

        RunOnUiThread(() =>
        {
            if (OzServerMode.Disabled || !frequency.Transmit)
                return;

            ActionLog.Log("Primary", $"VSCS line for {sector.Name} is transmitting again after its release - taking it back",
                new { action = "vscs_retake_after_release", sector = sector.Name, own_callsign = Network.Me?.Callsign });
            CheckActive(frequency);
        });
    }

    // Raised (on the UI thread) once a request has been sent from a transmit press, with the
    // requested sector's name - Plugin opens the Sectors window on it.
    public event Action<string>? SectorRequested;

    // Sectors with a transmit prompt currently open, so pressing again does not stack a second one.
    readonly HashSet<string> _promptOpen = new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan RequestPromptTimeout = TimeSpan.FromSeconds(60);

    const string FlowReliefNote =
        "You are requesting to take ownership of airspace as FMP. This is a temporary operation for "
        + "relieving ATC during bathroom breaks etc. FMP should not control ENR/TMA Airspace for "
        + "extended periods of time.";

    // Transmit was pressed on a sector someone else holds. Rather than claiming it (which the
    // backend refuses) or requesting it unasked, the controller is asked whether to request it and
    // shown everything that would come with it.
    //
    //   Accept - the request goes to the holder, and the Sectors window opens on it.
    //   Reject - (also closing the prompt, or leaving it for a minute) the line goes back to
    //            Receive, since the controller has decided not to work this airspace.
    //
    // Nothing is put on the scope until the holder accepts; then the sector and its tags arrive the
    // ordinary way. A flow position sees an extra note - it may only take airspace this way, as
    // temporary relief.
    void PromptToRequestHeldSector(VSCSFrequency frequency, SectorsVolumes.Sector sector, string myCallsign)
    {
        if (OzServerMode.Disabled)
            return;

        if (!_tracker.ControlledByOthers.TryGetValue(sector.Name, out var owner))
            return;

        // Kept out of the claim path while this is being decided, so the tracker does not also try
        // to claim it and ask its own "already owned" question about the same press.
        _tracker.HoldForTransmitPrompt(PrimaryPosition.CoveredBy(sector).Select(s => s.Name));

        // After every TransmitChanged handler has run - VatpacPlugin's included, which can add the
        // sector's whole group to MMI on this same press. None of it is ours.
        DeferOnUiThread(() => RemoveHeldFromMmi(sector, owner.Cid));

        if (_tracker.HasPendingRequestFor(sector))
        {
            ActionLog.Log("Request", $"Transmit on {sector.Name} - already requested from {owner.Callsign}",
                new { action = "transmit_held_already_requested", sector = sector.Name, owner = owner.Callsign, own_callsign = myCallsign });
            RunOnUiThread(() => SectorRequested?.Invoke(sector.Name));
            return;
        }

        if (!_promptOpen.Add(sector.Name))
            return;

        // What the holder would hand over: the sector and everything under it that they hold
        // themselves, which is what the backend's transferRequest transfers.
        var group = PrimaryPosition.CoveredBy(sector)
            .Where(s => s.Equals(sector)
                        || (_tracker.ControlledByOthers.TryGetValue(s.Name, out var o) && o.Cid == owner.Cid))
            .ToList();

        var message = $"{SectorDescription.Describe(sector)} is owned by another controller."
                      + Environment.NewLine + Environment.NewLine
                      + $"Owned by {owner.Callsign}:" + Environment.NewLine
                      + string.Join(Environment.NewLine, group.Select(s => SectorDescription.Describe(s)))
                      + Environment.NewLine + Environment.NewLine
                      + $"Send a request for {(group.Count == 1 ? "this sector" : "these sectors")} to {owner.Callsign}?";
        if (PrimaryPosition.IsFlowCallsign(myCallsign))
            message += Environment.NewLine + Environment.NewLine + FlowReliefNote;

        ActionLog.Log("Request", $"Transmit on {sector.Name}, held by {owner.Callsign} - asking whether to request it",
            new { action = "transmit_held_prompt", sector = sector.Name, sectors = group.Select(s => s.Name).ToArray(), owner = owner.Callsign, own_callsign = myCallsign });

        DeferOnUiThread(() =>
        {
            var prompt = new SectorConflictPromptWindow(message, "Request sector", "Accept", "Reject");
            var timeout = new System.Windows.Forms.Timer { Interval = (int)RequestPromptTimeout.TotalMilliseconds };
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
                _promptOpen.Remove(sector.Name);

                if (prompt.Answer)
                    _ = RequestFromTransmitAsync(sector);
                else
                    BackToReceive(frequency, sector);
            };

            if (System.Windows.Forms.Application.OpenForms["MainForm"] is System.Windows.Forms.Form mainForm)
                prompt.Show(mainForm);
            else
                prompt.Show();
            prompt.BringToFront();
            timeout.Start();
        });
    }

    async System.Threading.Tasks.Task RequestFromTransmitAsync(SectorsVolumes.Sector sector)
    {
        if (await _tracker.RequestHeldSectorAsync(sector))
            RunOnUiThread(() => SectorRequested?.Invoke(sector.Name));
    }

    // Declined: off Transmit, onto Receive - what vatSys's own Receive button does. Taken out of MMI
    // already (RemoveHeldFromMmi), so the TransmitChanged this raises finds nothing to drop or
    // release, and nothing of anyone else's is touched.
    void BackToReceive(VSCSFrequency frequency, SectorsVolumes.Sector sector)
    {
        if (OzServerMode.Disabled)
            return;

        _tracker.ReleaseTransmitPromptHold(PrimaryPosition.CoveredBy(sector).Select(s => s.Name));
        RemoveHeldFromMmi(sector, _tracker.ControlledByOthers.TryGetValue(sector.Name, out var owner) ? owner.Cid : (int?)null);

        ActionLog.Log("Request", $"Did not request {sector.Name} - its VSCS line is back on Receive",
            new { action = "transmit_held_declined", sector = sector.Name, own_callsign = Network.Me?.Callsign });

        if (frequency.Transmit)
            frequency.Transmit = false;
        frequency.Receive = true;
    }

    // Takes the sector, and whatever under it is not ours, back out of MMI - never this session's
    // own position or anything it owns.
    void RemoveHeldFromMmi(SectorsVolumes.Sector sector, int? ownerCid)
    {
        if (OzServerMode.Disabled)
            return;

        var own = Network.Me?.Callsign;
        var current = MMI.SectorsControlled.ToList();
        var remove = PrimaryPosition.CoveredBy(sector)
            .Where(s => s.Callsign != own && !_tracker.IsMine(s))
            .Where(s => s.Equals(sector) || ownerCid == null
                        || (_tracker.ControlledByOthers.TryGetValue(s.Name, out var o) && o.Cid == ownerCid))
            .Where(s => current.Any(c => c.Equals(s)))
            .ToList();

        if (remove.Count == 0)
            return;

        MMI.SetControlledSectors(current.Where(c => !remove.Any(r => r.Equals(c))).ToList());
    }

    // A line went idle for a sector vatSys does not list as controlled. Nothing comes off the
    // scope, but OzServer can still record the sector as this controller's: MMI.SectorsControlled
    // and OzServer ownership are separate records, and several paths move one without the other - a
    // sector carried out of MMI by another line's drop (CheckActive's group sweep), cover removed
    // locally when a position's own controller logs on (PrimaryPositionWatcher.RemoveLocalCover,
    // which only touches MMI), or an Owned list the Sectors window committed. The controller has
    // taken themselves off this frequency, so whatever OzServer still holds under that sector's
    // name goes back.
    //
    // Gated on Owned, unlike the in-MMI path (which asks the server outright - see
    // OzServerOwnershipTracker.ReleaseIdleVscsLineAsync): otherwise every Idle press on a line
    // that is not controlled - a monitored neighbour's frequency, say - would send a release the
    // backend can only refuse.
    void ReleaseQuietLineMissingFromMmi(VSCSFrequency frequency, SectorsVolumes.Sector frequencySector, SectorsVolumes.Sector primarySector)
    {
        if (OzServerMode.Disabled)
            return;

        // The position this session is logged on as is never given away by going quiet on it, as
        // in the in-MMI path.
        if (frequencySector.Equals(primarySector))
            return;

        if (!_tracker.IsMine(frequencySector))
            return;

        ActionLog.Log("Primary",
            $"VSCS line for {frequencySector.Name} went idle - not in controlled sectors, but still ours on OzServer",
            new
            {
                action = "vscs_quiet_line_owned_not_controlled",
                sector = frequencySector.Name,
                own_callsign = Network.Me?.Callsign
            });

        _ = ReleaseThenRetakeIfTransmittingAsync(frequency, frequencySector);
    }

    static void LogWithheldTopDownCover(
        string lead,
        List<SectorsVolumes.Sector> unfiltered,
        List<SectorsVolumes.Sector> granted)
    {
        var withheld = unfiltered
            .Where(sector => !granted.Any(g => g.Equals(sector)))
            .Select(sector => sector.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();

        if (withheld.Count > 0)
            ActionLog.Log("Primary", $"{lead}: {string.Join(", ", withheld)}", new
            {
                action = "withhold_top_down_cover_login",
                sectors = withheld.ToArray(),
                unfiltered = unfiltered.Select(s => s.Name).ToArray(),
                granted = granted.Select(s => s.Name).ToArray(),
                own_callsign = Network.Me?.Callsign
            });
    }
}
