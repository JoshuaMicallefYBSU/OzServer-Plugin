using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Puts this controller on the frequency of a position they hold but have no line for, and on
// Transmit.
//
// vatSys builds the VSCS panel once per session, from the logon station and its AFV group
// (Audio.LoadFrequencies -> AFV.GetFrequencies with searchGroup). A position gained later - by an
// accepted request, a claim in the Sectors window, or a resume after reconnecting - gets no line.
// BN-TRS_CTR was handed the TRT group by BN-TRT_CTR and had nothing to transmit TRT on.
//
// The line has to be transmitting, not only loaded. VatpacPlugin's Sectors.CheckActive runs on
// every VSCS change and takes out of MMI.SectorsControlled any controlled sector whose line is on
// the panel but not transmitting (ML_APP lost MAV, MAW, MDN and MDS that way on each transmit
// press), dropping every tag inside it. So the line is switched on in the same
// Audio.VSCSFrequenciesChanged that announces it - synchronously, on whatever thread vatSys raises
// it - ahead of VatpacPlugin's handler. vatSys builds its plugins in the order
// Directory.GetFiles lists the Plugins folder (vatsys.Plugin.Plugins), alphabetical on NTFS, so
// OzServer subscribes first; that is not guaranteed. Should VatpacPlugin get there first, the
// tracker puts the sector back (RestoreOwnedToMmi), though tags inside it may have been dropped.
//
// A full panel (vatSys refuses a ninth frequency) gives up an idle line - one neither receiving
// nor transmitting, and not a position this controller holds - to make room.
static class VscsLines
{
    const int MaxLines = 8;

    // How long a line is waited for: its AFV station lookup, and after a reconnect the network's
    // confirmation of the session as a controller (as AfvSectorClaimer's transmit restore waits).
    // A station with no transceivers is never added (Audio.AddVSCSFrequency); the wait then ends
    // with a log line.
    static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(45);

    sealed class Pending
    {
        public string SectorName = "";
        public string Reason = "";
        public DateTime Until;
    }

    // By VSCS line name (the station callsign). Guarded by itself.
    static readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    static OzServerOwnershipTracker? _tracker;

    // Called once at plugin load, before VatpacPlugin subscribes to the same events - see the
    // class comment.
    public static void Initialise(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        Audio.VSCSFrequenciesChanged += (_, _) => UiThread.Guard(ApplyPending)();
        // Transmit is refused until the network has confirmed the session as a controller - the
        // gate vatSys's own Transmit button uses - so a resume's lines wait for it.
        Network.ValidATCChanged += (_, _) => UiThread.Guard(ApplyPending)();
        Network.Disconnected += (_, _) =>
        {
            lock (_pending)
                _pending.Clear();
        };
    }

    // The group primaries among `sectors` - those not covered by another of them - each go on
    // Transmit. TRT, ASH, KIY, DAW and DAE are one position: only TRT's line is wanted.
    public static void TransmitOn(IEnumerable<SectorsVolumes.Sector> sectors, string reason)
    {
        var list = sectors.Where(s => s != null && !s.IsDummy).ToList();
        var primaries = list
            .Where(s => !list.Any(other => other.Name != s.Name
                && PrimaryPosition.CoveredBy(other).Any(c => c.Name == s.Name)))
            .ToList();

        foreach (var sector in primaries)
            TransmitOn(sector, reason);

        RemoveSubSectorLines(primaries, reason);
    }

    // The lines of sub-sectors this controller now works through their primary's line (issue #11):
    // the airspace is answered on the primary's frequency, so they only clog the panel. Left alone:
    // a line that is transmitting (the controller chose to), the logon station's, one waiting to be
    // switched on, and a sub-sector somebody else holds (a neighbour's frequency worth keeping).
    //
    // Also what keeps VatpacPlugin's Sectors.CheckActive off them: it drops any controlled sector
    // whose line is on the panel without transmitting, and every tag inside it with it (PHR, on
    // PH_APP's logon on 23 Nov 2026).
    static void RemoveSubSectorLines(IEnumerable<SectorsVolumes.Sector> primaries, string reason)
    {
        if (OzServerMode.Disabled || !Network.IsConnected)
            return;

        List<string> waiting;
        lock (_pending)
            waiting = _pending.Keys.ToList();

        foreach (var primary in primaries)
        {
            var stations = PrimaryPosition.CoveredBy(primary)
                .Where(s => !s.Equals(primary) && _tracker?.IsMine(s) == true)
                .Select(s => s.Callsign)
                .Where(station => !string.IsNullOrEmpty(station)
                                  && !string.Equals(station, primary.Callsign, StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(station, Network.Callsign, StringComparison.OrdinalIgnoreCase)
                                  && !waiting.Contains(station, StringComparer.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var line in Audio.VSCSFrequencies.Where(f => stations.Contains(f.Name) && !f.Transmit).ToList())
            {
                try
                {
                    Audio.RemoveFrequency(line);
                    ActionLog.Log("Primary", $"Removed VSCS line {line.Name} - worked on {primary.Name}'s line ({reason})", new
                    {
                        action = "vscs_subsector_line_removed",
                        removed = line.Name,
                        primary = primary.Name,
                        reason
                    });
                }
                catch (Exception ex)
                {
                    ErrorThrottle.Report("vscs-remove:" + line.Name, $"Couldn't remove the VSCS line {line.Name}: {ex.Message}", ex);
                }
            }
        }
    }

    public static void TransmitOn(SectorsVolumes.Sector sector, string reason)
    {
        if (OzServerMode.Disabled || !Network.IsConnected || NetworkIdentity.IsObserver)
            return;

        var station = sector.Callsign;
        if (string.IsNullOrEmpty(station))
            return;

        // The logon station's line is vatSys's own, and the tracker puts it on Transmit itself
        // when the position is gained.
        if (string.Equals(station, Network.Callsign, StringComparison.OrdinalIgnoreCase))
            return;

        var lines = Audio.VSCSFrequencies;
        var line = lines.FirstOrDefault(f => string.Equals(f.Name, station, StringComparison.OrdinalIgnoreCase));
        if (line != null && line.Transmit && line.Receive)
            return;

        bool alreadyRequested;
        lock (_pending)
        {
            alreadyRequested = _pending.ContainsKey(station);
            _pending[station] = new Pending { SectorName = sector.Name, Reason = reason, Until = DateTime.UtcNow + PendingWindow };
        }

        if (line != null)
        {
            ApplyPending();
            return;
        }

        // Asked for already and still loading.
        if (alreadyRequested)
            return;

        if (lines.Count >= MaxLines && !MakeRoom(sector))
        {
            lock (_pending)
                _pending.Remove(station);
            return;
        }

        try
        {
            Audio.LoadFrequency(station, false);
            ActionLog.Log("Primary", $"Adding a VSCS line for {sector.Name} ({station}) - {reason}", new
            {
                action = "vscs_line_requested",
                sector = sector.Name,
                station,
                reason
            });
        }
        catch (Exception ex)
        {
            lock (_pending)
                _pending.Remove(station);
            ErrorThrottle.Report("vscs-line:" + station, $"Couldn't add a VSCS line for {sector.Name}: {ex.Message}", ex);
        }
    }

    // Removes one idle line to make room for `forSector`'s. Never the logon station's, a line that
    // is receiving or transmitting, one waiting to be switched on, or the line of a position this
    // controller holds on OzServer.
    static bool MakeRoom(SectorsVolumes.Sector forSector)
    {
        List<string> waiting;
        lock (_pending)
            waiting = _pending.Keys.ToList();

        var idle = Audio.VSCSFrequencies
            .Where(f => !f.Transmit && !f.Receive)
            .Where(f => !string.Equals(f.Name, Network.Callsign, StringComparison.OrdinalIgnoreCase))
            .Where(f => !waiting.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .Where(f => !IsHeldHere(f.Name))
            .ToList();

        // The last idle line: vatSys loads the logon station and its nearest group members first.
        var victim = idle.LastOrDefault();
        if (victim == null)
        {
            ActionLog.Log("Primary", $"No VSCS line added for {forSector.Name} - all {MaxLines} frequencies are in use", new
            {
                action = "vscs_line_not_added_full",
                sector = forSector.Name,
                station = forSector.Callsign,
                lines = Audio.VSCSFrequencies.Select(f => f.Name).ToArray()
            });
            return false;
        }

        Audio.RemoveFrequency(victim);
        ActionLog.Log("Primary", $"Removed idle VSCS line {victim.Name} to make room for {forSector.Name}", new
        {
            action = "vscs_line_removed_for_room",
            removed = victim.Name,
            sector = forSector.Name,
            station = forSector.Callsign
        });
        return true;
    }

    static bool IsHeldHere(string station)
    {
        var sector = SectorsVolumes.Sectors.FirstOrDefault(s => string.Equals(s.Callsign, station, StringComparison.OrdinalIgnoreCase));
        return sector != null && _tracker?.IsMine(sector) == true;
    }

    // Switches on every requested line that has arrived. Runs synchronously from vatSys's events -
    // see the class comment.
    static void ApplyPending()
    {
        List<KeyValuePair<string, Pending>> work;
        lock (_pending)
        {
            if (_pending.Count == 0)
                return;
            work = _pending.ToList();
        }

        foreach (var entry in work)
        {
            var station = entry.Key;
            var pending = entry.Value;

            if (DateTime.UtcNow > pending.Until)
            {
                lock (_pending)
                    _pending.Remove(station);
                ActionLog.Log("Primary", $"VSCS line for {pending.SectorName} ({station}) didn't come on within {PendingWindow.TotalSeconds:0}s", new
                {
                    action = "vscs_line_timed_out",
                    sector = pending.SectorName,
                    station,
                    reason = pending.Reason
                });
                continue;
            }

            if (!Network.IsConnected || !Network.ValidATC)
                continue;

            var line = Audio.VSCSFrequencies.FirstOrDefault(f => string.Equals(f.Name, station, StringComparison.OrdinalIgnoreCase));
            if (line == null)
                continue;

            lock (_pending)
                _pending.Remove(station);

            // Handed on, or released, while the line was loading: not this controller's to talk on.
            var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == pending.SectorName);
            if (sector == null || _tracker?.IsMine(sector) != true)
                continue;

            if (line.Transmit && line.Receive)
                continue;

            // Both flags, in the order vatSys's own Transmit button writes them (VSCSControlDX,
            // VSCS_Button_Freq_Trans): AFV only builds transceivers for lines with Receive on.
            line.Transmit = true;
            line.Receive = true;

            ActionLog.Log("Primary", $"Put transmit on {pending.SectorName} - {pending.Reason}", new
            {
                action = "vscs_transmit_gained_position",
                sector = pending.SectorName,
                station,
                reason = pending.Reason,
                still_in_vatsys = MMI.SectorsControlled.Any(s => s.Name == pending.SectorName)
            });
        }
    }
}
