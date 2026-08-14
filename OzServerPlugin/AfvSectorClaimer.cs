using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Drives MMI.SectorsControlled from the VSCS/AFV panel's own Transmit state - ported from
// badvectors/VatpacPlugin's Sectors.cs (Init/CheckActive), the reference for how a working plugin
// wires VSCS transmit to vatSys's own sector control. This class only ever touches
// MMI.SectorsControlled, never OzServerApiClient directly - OzServer syncing is handled entirely
// downstream by OzServerOwnershipTracker's own MMI.SectorsControlledChanged subscription. That's
// what makes a transmit press show up in both the built-in vatSys Sectors window (which just
// displays MMI.SectorsControlled - nothing else needed for that one) and the OzServer window: a
// transmit press goes through the exact same path vatSys itself already uses for every other kind
// of sector change, rather than a second, OzServer-only path that only one specific window would
// ever hear about. Every trigger here also calls ControllerInfoUpdater.Update() (ported from the
// same reference plugin's Extending.cs), which keeps the "Extending ..." Controller Info line in
// sync with the same Transmit state, independently of MMI/OzServer.
public class AfvSectorClaimer
{
    readonly HashSet<VSCSFrequency> _subscribed = new();

    public AfvSectorClaimer()
    {
        Network.Connected += (_, _) => Init();
        Audio.VSCSFrequenciesChanged += (_, _) =>
        {
            ResubscribeTransmitChanged();
            CheckActive();
            ControllerInfoUpdater.Update();
        };
        ResubscribeTransmitChanged();
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
        CheckActive();
        ControllerInfoUpdater.Update();
    }

    // Sets the initial controlled-sector set on connect: the primary sector matching this
    // session's own login callsign, plus every one of its direct sub-sectors that nobody else is
    // already real-ATC online for. Ported directly from VatpacPlugin's Sectors.Init().
    static void Init()
    {
        var primarySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == Network.Me.Callsign);
        if (primarySector == null)
            return;

        var sectors = new List<SectorsVolumes.Sector> { primarySector };

        foreach (var subsector in primarySector.SubSectors.ToList())
        {
            var onlineAtc = (Network.GetOnlineATCs ?? new List<NetworkATC>())
                .FirstOrDefault(a => a.Callsign == subsector.Callsign && a.IsRealATC);
            if (onlineAtc != null)
                continue;

            sectors.Add(subsector);
        }

        MMI.SetControlledSectors(sectors);
    }

    // Recomputes MMI.SectorsControlled from every VSCS line's current Transmit state. Based on
    // VatpacPlugin's Sectors.CheckActive() - the primary sector (matching this session's own login
    // callsign) is never removed by idling its own line, only additional sub-sector lines come and
    // go with Transmit - but with one deliberate addition VatpacPlugin's own version doesn't have:
    // a frequency is only ever acted on (gained or lost) if its sector is actually primarySector
    // itself or one of its own descendants (see IsWithinPrimary). Without that, a subsector
    // controller (e.g. logging on as SNO, primary = SNO) whose panel also happens to show a
    // *parent* line (WOL) sitting at Transmit=true - vatSys's own default, not something they
    // pressed - had that parent line read as "extend into WOL", handing them WOL's entire
    // responsible_sectors group (SNO included) even though their own primary is just SNO. The
    // built-in Sectors window never does this since it doesn't look at VSCS at all; scoping this to
    // primarySector's own tree is what makes this class match that for a subsector login too.
    static void CheckActive()
    {
        var primarySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == Network.Me.Callsign);
        if (primarySector == null)
            return;

        var currentSectors = MMI.SectorsControlled.ToList();

        foreach (var frequency in Audio.VSCSFrequencies.ToList())
        {
            var frequencySector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == frequency.Name);
            if (frequencySector == null)
                continue;

            if (!IsWithinPrimary(frequencySector, primarySector))
                continue;

            var currentlyControlled = currentSectors.FirstOrDefault(s => s.Callsign == frequency.Name);

            if (currentlyControlled == null)
            {
                if (!frequency.Transmit)
                    continue;

                currentSectors.Add(frequencySector);

                foreach (var subsector in frequencySector.SubSectors.ToList())
                {
                    var onlineAtc = (Network.GetOnlineATCs ?? new List<NetworkATC>())
                        .FirstOrDefault(a => a.Callsign == subsector.Callsign && a.IsRealATC);
                    if (onlineAtc != null)
                        continue;

                    currentSectors.Add(subsector);
                }
            }
            else
            {
                if (frequency.Transmit)
                    continue;

                if (primarySector == currentlyControlled)
                    continue;

                currentSectors.Remove(currentlyControlled);

                foreach (var subsector in currentlyControlled.SubSectors.ToList())
                    currentSectors.Remove(subsector);
            }
        }

        MMI.SetControlledSectors(currentSectors);
    }

    // Whether sector is primary itself or (recursively) one of its own SubSectors - depth is a
    // backstop against a self-referencing grouping (some sectors list themselves inside their own
    // SubSectors - see the similar guard/comment on OzServerSectorsWindow.BuildOwnedSectorNode)
    // looping forever.
    static bool IsWithinPrimary(SectorsVolumes.Sector sector, SectorsVolumes.Sector primary, int depth = 0)
    {
        if (ReferenceEquals(sector, primary))
            return true;

        if (depth >= 8)
            return false;

        foreach (var sub in primary.SubSectors)
        {
            if (!ReferenceEquals(sub, primary) && IsWithinPrimary(sector, sub, depth + 1))
                return true;
        }

        return false;
    }
}
