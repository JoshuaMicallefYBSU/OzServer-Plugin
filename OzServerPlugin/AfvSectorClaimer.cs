using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Drives MMI.SectorsControlled from the VSCS/AFV panel's own Transmit state - ported from
// badvectors/VatpacPlugin's Sectors.cs (Init/CheckActive), the reference for how a working plugin
// wires VSCS transmit to vatSys's own sector control. This class only ever touches
// MMI.SectorsControlled, never OzServerApiClient directly - OzServer syncing (including asking
// whether to request a sub-sector that turns out to already be owned by someone else - see
// OzServerOwnershipTracker.HandleConflictAsync) is handled entirely downstream by
// OzServerOwnershipTracker's own MMI.SectorsControlledChanged subscription. That's what makes a
// transmit press show up in both the built-in vatSys Sectors window (which just displays
// MMI.SectorsControlled - nothing else needed for that one) and the OzServer window, and (as a
// consequence of MMI.SectorsControlled being correct) is also what makes vatSys's own map display
// an extension normally - no separate API call needed for that. Every trigger here also calls
// ControllerInfoUpdater.Update() (ported from the same reference plugin's Extending.cs), which
// keeps the "Extending ..." Controller Info line in sync with the same Transmit state,
// independently of MMI/OzServer.
public class AfvSectorClaimer
{
    readonly HashSet<VSCSFrequency> _subscribed = new();
    // What Network.Me.Callsign was the last time Init() actually ran - see the
    // VSCSFrequenciesChanged handler for why this matters: that event fires both for a genuine
    // position change (the panel reloading for a new primary) and for a station merely being
    // added/removed while working the *same* position (e.g. adding another AFV line mid-session),
    // and only the former should re-baseline.
    string? _lastPrimaryCallsign;

    // Network.Connected fires the moment the session comes up, which is NOT the same moment the
    // data Init() depends on is usable: Network.Me can still be null, and DefaultSectorsFor matches
    // Network.Me.Callsign against the profile's own SectorsVolumes.Sectors. A single attempt at
    // that instant silently granted nothing whenever either side wasn't ready - Init() returned on
    // the empty result, MMI.SectorsControlled was never written, and since nothing else touches it
    // on connect there was no second chance. The symptom was logging in on a position and being
    // handed none of its airspace, having to claim your own sector group by hand.
    static readonly TimeSpan InitRetryInterval = TimeSpan.FromSeconds(1);
    const int InitMaxAttempts = 20;
    readonly System.Threading.Timer _initRetryTimer;
    int _initAttemptsLeft;

    public AfvSectorClaimer()
    {
        Network.Connected += (_, _) => BeginInit();
        // VSCSFrequenciesChanged re-baselines via Init() (not CheckActive() - see Init()'s own
        // comment for why) only when the primary callsign has actually changed since Init() last
        // ran, i.e. only for a genuine position change. Init() unconditionally resets
        // MMI.SectorsControlled to primary + its own direct subsectors - running that on *every*
        // list change, including a mid-session station add that has nothing to do with a position
        // change, wiped out whatever had already been extended into (the map's infill for those
        // sectors disappearing, until a later TransmitChanged happened to sweep them back in via
        // CheckActive()'s own full read of current Transmit state - which is why pressing transmit
        // on the new station appeared to "bring everything back").
        Audio.VSCSFrequenciesChanged += (_, _) =>
        {
            ResubscribeTransmitChanged();

            if (Network.Me?.Callsign != _lastPrimaryCallsign)
                BeginInit();

            ControllerInfoUpdater.Update();
        };
        _initRetryTimer = new System.Threading.Timer(
            _ => RetryInit(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        ResubscribeTransmitChanged();
    }

    void BeginInit()
    {
        _initAttemptsLeft = InitMaxAttempts;
        RetryInit();
    }

    void StopInitRetries() =>
        _initRetryTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    void RetryInit()
    {
        // Session went away while still waiting for it to become ready - nothing left to grant.
        if (!Network.IsConnected)
        {
            StopInitRetries();
            return;
        }

        // An observer is never granted a position of their own - their airspace is mirrored from
        // whoever actually holds it (ObserverPositionMirror). Read from the connection's own
        // Position/Rating, which are correct immediately; the previous test on Network.Me.IsRealATC
        // abandoned a real controller's grant outright, taking their whole position off them.
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

        // One-shot reschedule rather than a repeating period, so a slow Init() can never overlap
        // itself.
        _initRetryTimer.Change(InitRetryInterval, System.Threading.Timeout.InfiniteTimeSpan);
    }

    // Audio.VSCSFrequencies is a fresh array snapshot on every call, and the VSCSFrequency
    // instances it hands back aren't guaranteed to be the same ones across a
    // VSCSFrequenciesChanged event (a position change tears down and rebuilds the whole list) - so
    // this diffs by reference against what's already subscribed rather than assuming it's safe to
    // subscribe once and forget.
    void ResubscribeTransmitChanged()
    {
        var current = Audio.VSCSFrequencies;

        foreach (var freq in _subscribed.Except(current).ToList())
        {
            freq.TransmitChanged -= OnTransmitChanged;
            _subscribed.Remove(freq);
        }

        foreach (var freq in current.Except(_subscribed))
        {
            freq.TransmitChanged += OnTransmitChanged;
            _subscribed.Add(freq);
        }
    }

    void OnTransmitChanged(object? sender, EventArgs e)
    {
        if (sender is VSCSFrequency frequency)
            CheckActive(frequency);

        ControllerInfoUpdater.Update();
    }

    // Sets the initial controlled-sector set on connect (or on a genuine position change - see the
    // VSCSFrequenciesChanged handler): the primary sector matching this session's own login
    // callsign, plus every one of its direct sub-sectors that nobody else is already real-ATC
    // online for. Based on VatpacPlugin's Sectors.Init().
    // True once the position's default sectors have actually been granted, which is what tells
    // RetryInit to stop. False means "not ready yet", never "nothing to do" - see the retry fields.
    bool Init()
    {
        // Network.Me (and everything else here - VSCS lines, sector callsigns) is only meaningful
        // once actually connected to the network; running this beforehand (e.g. a stray
        // VSCSFrequenciesChanged before login) would touch MMI.SectorsControlled with garbage.
        if (!Network.IsConnected)
            return false;

        var callsign = Network.Me?.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return false;

        // PrimaryPosition, not an open-coded copy of the same rule: PrimaryPositionWatcher applies
        // it on the *other* controller's side to decide what to release to this session, and the
        // two have to agree exactly or a sector is either released to nobody or never handed over.
        var sectors = PrimaryPosition.DefaultSectorsFor(callsign);
        if (sectors.Count == 0)
            return false;

        // Only recorded once the grant actually happened. Setting it on the way in marked the
        // position as handled even when nothing was granted, so the VSCSFrequenciesChanged
        // re-baseline below then saw no callsign change and never gave it a second attempt.
        _lastPrimaryCallsign = callsign;

        MMI.SetControlledSectors(sectors);
        return true;
    }

    // Recomputes MMI.SectorsControlled from ONE VSCS line's current Transmit state - the specific
    // line that just raised TransmitChanged (see OnTransmitChanged, which passes it in via sender),
    // never the whole panel. Two cases, exactly:
    //   - A bare sub-sector's own line (no SubSectors of its own, e.g. SNO) activating adds only
    //     that sector - never its primary, never siblings. There is no legitimate reading of
    //     "extend into SNO" that should also hand over WOL and WOL's other sub-sectors.
    //   - A primary's own line (e.g. WOL, or WON) activating adds it plus its *whole* group -
    //     matching real "extend" semantics (you're covering the area on your own scope/tags).
    //     Whether each individual sub-sector is actually claimable on OzServer (some may already be
    //     owned by someone else) is resolved downstream, once, by
    //     OzServerOwnershipTracker.HandleConflictAsync - the same "ask, then claim what's left"
    //     flow already used for a manual claim conflict - not filtered here by a live-ATC-presence
    //     check, which only catches someone logged in directly under that exact callsign and misses
    //     someone else who reached it by extending too (that was the actual bug: WON's own extend
    //     silently absorbing HUO/LTA/HBA with no chance to ask, because that live-presence check
    //     doesn't know about OzServer ownership at all).
    //
    // Deliberately scoped to just this one frequency rather than resweeping every VSCS line's
    // current state (an earlier version did that): every OTHER line's Transmit still reads exactly
    // what it read before this event, including any sub-sector Init() had already granted into
    // MMI.SectorsControlled but that's never individually been transmitted on - resweeping those
    // treated "controlled but not currently transmitting" as "just turned off" on every unrelated
    // transmit press, silently stripping them back out (and, worse, once a sector drops out of
    // MMI.SectorsControlled entirely vatSys backfills it with a dummy placeholder infill that VSCS
    // can no longer reclaim by transmitting again - the actual bug this was rewritten to fix:
    // pressing Transmit on a primary like BLA was wiping BLA's own already-granted infill back to a
    // dummy and leaving it unrecoverable). The primarySector self-protection below still matters for
    // its own sake (turning the primary's own line off should never drop the primary itself), but
    // scoping to one frequency is what actually stops every OTHER granted sector from being caught
    // in the blast radius of a change that has nothing to do with them.
    // Uses Sector.Equals throughout (Callsign-based), not == or ReferenceEquals - Sector overrides
    // Equals/GetHashCode but not the == operator, so two Sector instances for the same real sector
    // reached via different lookups compare unequal under == even though they're the same sector.
    static void CheckActive(VSCSFrequency frequency)
    {
        // Same reasoning as Init() above - nothing here means anything before the network
        // connection is actually up.
        if (!Network.IsConnected)
            return;

        var primarySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == Network.Me.Callsign);
        if (primarySector == null)
            return;

        var frequencySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == frequency.Name);
        if (frequencySector == null)
            return;

        var currentSectors = MMI.SectorsControlled.ToList();
        var currentlyControlled = currentSectors.FirstOrDefault(s => s.Callsign == frequency.Name);

        if (currentlyControlled == null)
        {
            if (!frequency.Transmit)
                return;

            currentSectors.Add(frequencySector);

            foreach (var subsector in frequencySector.SubSectors.ToList())
            {
                if (!currentSectors.Any(s => s.Equals(subsector)))
                    currentSectors.Add(subsector);
            }
        }
        else
        {
            if (frequency.Transmit)
                return;

            if (primarySector.Equals(currentlyControlled))
                return;

            currentSectors.Remove(currentlyControlled);

            foreach (var subsector in currentlyControlled.SubSectors.ToList())
                currentSectors.Remove(subsector);
        }

        MMI.SetControlledSectors(currentSectors);
    }
}
