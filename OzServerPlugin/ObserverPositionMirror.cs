using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shows an observer the airspace of the position they are watching, without ever letting them take
// it. An observer connection has no sectors of its own - AfvSectorClaimer deliberately grants it
// nothing, and OzServerOwnershipTracker refuses to claim for a session that is not real ATC - so
// without this an observer sat on a bare scope with no airspace drawn at all.
//
// What is mirrored is whatever the real controller currently holds on OzServer, not the position's
// default group out of the dataset, so it follows them live as they claim, extend and release
// rather than showing airspace nobody is working.
//
// Writing MMI.SectorsControlled is safe here precisely because the claim path is gated on
// IsRealATC: the write raises MMI.SectorsControlledChanged, the tracker's claim loop runs, and it
// returns without touching the API. The observer sees the airspace; OzServer never hears about it.
public class ObserverPositionMirror
{
    readonly OzServerOwnershipTracker _tracker;
    List<SectorsVolumes.Sector> _mirrored = new();

    public ObserverPositionMirror(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        // ControlledByOthers is what moves when the watched controller claims or releases, and that
        // never reaches OwnedChanged - an observer's own Owned is permanently empty.
        _tracker.Refreshed += (_, _) => RunOnUiThread(Apply);
        Network.Disconnected += (_, _) => RunOnUiThread(Clear);
    }

    // An observer's callsign is the position they are watching plus the network's observer suffix,
    // so the position is whatever sector callsign shares the prefix: BN-ISA_OBS -> BN-ISA_CTR.
    //
    // Returns null when the prefix matches more than one position - a bare ML_OBS matches ML_TWR,
    // ML_APP and ML_SMC alike, and mirroring an arbitrary one of those is worse than mirroring
    // nothing: the observer would be shown airspace belonging to a controller they are not watching,
    // with no way to tell that is what happened.
    public static string? ObservedPositionCallsign(string? observerCallsign)
    {
        if (string.IsNullOrEmpty(observerCallsign))
            return null;

        var separator = observerCallsign!.LastIndexOf('_');
        if (separator <= 0)
            return null;

        var prefix = observerCallsign.Substring(0, separator + 1);

        var matches = SectorsVolumes.Sectors
            .Where(sector => !string.IsNullOrEmpty(sector.Callsign)
                             && sector.Callsign.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(sector => sector.Callsign!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    void Apply()
    {
        // A real controller manages their own sectors through every other path in this plugin, so
        // this must never run for one. Read from the connection's own Position/Rating
        // (NetworkIdentity.IsObserver), never Network.Me.IsRealATC: that flag reads false for a
        // genuine controller for seconds after Connected, and in that window this ran for a real
        // ML-ASP_CTR session, resolved their own position, found nothing under "controlled by
        // *others*" - their sectors are their own, not somebody else's - and wrote an empty set,
        // taking their entire position off them.
        if (!Network.IsConnected || !NetworkIdentity.IsObserver)
            return;

        // Network.Callsign, not Network.Me?.Callsign - the connection's own field, set the moment
        // the session exists, rather than the published ATC record which lags it (the same reason
        // NetworkIdentity.IsObserver reads Rating/Facility).
        var position = ObservedPositionCallsign(Network.Callsign);
        if (position == null)
            return;

        var mirrored = SectorsVolumes.Sectors
            .Where(sector => _tracker.ControlledByOthers.TryGetValue(sector.Name, out var owner)
                             && string.Equals(owner.Callsign, position, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Only written when it actually differs. Every MMI.SetControlledSectors re-fires
        // SectorsControlledChanged synchronously, so rewriting an unchanged set on every refresh
        // would spin the tracker's claim loop for nothing several times a minute.
        // Never clear a scope. An empty result means the watched controller holds nothing we can
        // see - which is indistinguishable from not knowing yet - and blanking MMI over that is the
        // most destructive thing this class could do. Only ever writes an actual set of sectors.
        if (mirrored.Count == 0 || SameSectors(mirrored, _mirrored))
            return;

        _mirrored = mirrored;
        MMI.SetControlledSectors(mirrored);
    }

    void Clear() => _mirrored = new List<SectorsVolumes.Sector>();

    // Sector overrides Equals/GetHashCode but not ==, so two instances of the same real sector
    // reached by different lookups compare unequal under == - the same trap documented in
    // AfvSectorClaimer.CheckActive and PrimaryPosition.
    static bool SameSectors(List<SectorsVolumes.Sector> left, List<SectorsVolumes.Sector> right) =>
        left.Count == right.Count && left.All(sector => right.Any(other => other.Equals(sector)));

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
