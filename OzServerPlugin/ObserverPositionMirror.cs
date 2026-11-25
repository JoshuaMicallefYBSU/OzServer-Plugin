using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shows an observer the airspace of the position they are watching, without letting them take it.
// An observer connection has no sectors of its own - AfvSectorClaimer grants it none and
// OzServerOwnershipTracker does not claim for one - so without this its scope shows no airspace.
//
// What is mirrored is what the watched controller holds on OzServer, not the position's default
// group from the dataset, so it follows them as they claim, extend and release.
//
// Writing MMI.SectorsControlled is safe because the tracker's claim path returns early for an
// observer: the write raises MMI.SectorsControlledChanged, the claim loop runs, and nothing is
// sent to the API.
public class ObserverPositionMirror
{
    readonly OzServerOwnershipTracker _tracker;
    List<SectorsVolumes.Sector> _mirrored = new();

    public ObserverPositionMirror(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        // Refreshed rather than OwnedChanged: the watched controller's claims and releases move
        // ControlledByOthers, and an observer's own Owned is always empty.
        _tracker.Refreshed += (_, _) => RunOnUiThread(Apply);
        Network.Disconnected += (_, _) => RunOnUiThread(Clear);
    }

    // An observer's callsign is the watched position's prefix plus the observer suffix, so the
    // position is the sector callsign that shares the prefix: BN-ISA_OBS -> BN-ISA_CTR.
    //
    // Null when the prefix matches more than one position (ML_OBS matches ML_TWR, ML_APP and
    // ML_SMC alike): mirroring an arbitrary one would show the observer airspace belonging to a
    // controller they are not watching.
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
        if (OzServerMode.Disabled)
            return;

        // Must never run for a real controller, whose sectors are managed by the rest of the
        // plugin. NetworkIdentity.IsObserver reads the connection's own details rather than
        // Network.Me.IsRealATC, which reads false for a real controller for some seconds after
        // connecting.
        if (!Network.IsConnected || !NetworkIdentity.IsObserver)
            return;

        // Network.Callsign rather than Network.Me?.Callsign: the connection's own field is set as
        // soon as the session exists, while the published ATC record lags it.
        var position = ObservedPositionCallsign(Network.Callsign);
        if (position == null)
            return;

        var mirrored = SectorsVolumes.Sectors
            .Where(sector => _tracker.ControlledByOthers.TryGetValue(sector.Name, out var owner)
                             && string.Equals(owner.Callsign, position, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Written only when it differs: MMI.SetControlledSectors raises SectorsControlledChanged
        // synchronously, which would run the tracker's claim loop on every refresh.
        //
        // An empty set is never written. It means the watched controller holds nothing visible
        // here, which cannot be told apart from not knowing yet, and must not blank the scope.
        if (mirrored.Count == 0 || SameSectors(mirrored, _mirrored))
            return;

        _mirrored = mirrored;
        // A copy: vatSys inserts its placeholder for the session's own callsign into the list it is
        // handed, which for an observer is every time. In _mirrored that extra entry would make
        // every later comparison above differ, and the write - with vatSys's "default owner is
        // online" error - repeat on each refresh.
        MMI.SetControlledSectors(mirrored.ToList());
    }

    void Clear() => _mirrored = new List<SectorsVolumes.Sector>();

    // Sector overrides Equals and GetHashCode but not ==, so two instances of the same sector
    // compare unequal under == and are compared with Equals.
    static bool SameSectors(List<SectorsVolumes.Sector> left, List<SectorsVolumes.Sector> right) =>
        left.Count == right.Count && left.All(sector => right.Any(other => other.Equals(sector)));

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
