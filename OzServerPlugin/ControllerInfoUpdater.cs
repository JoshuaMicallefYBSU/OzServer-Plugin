using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Keeps this session's Controller Info (Network.ControllerInfo - the free-text lines shown to
// other controllers/pilots, e.g. via .info) in sync with which extra VSCS lines are currently
// being transmitted on, alongside the MMI.SectorsControlled claims AfvSectorClaimer already makes
// for the same event. Ported from badvectors/VatpacPlugin's Extending.cs (CheckApproach/
// CheckEnroute/UpdateInfo) - the reference for this exact behaviour. Only the "Extending ..." line
// is ported; VatpacPlugin's own "Uncontactable on ..." feature depends on a hand-authored,
// airspace-specific frequency-substitution table (their Mapping dictionary) with no equivalent
// here, and their unused App dictionary wasn't ported either since nothing in their own source
// actually reads from it.
public static class ControllerInfoUpdater
{
    // Only APP/DEP and CTR positions get an "Extending" line, matching VatpacPlugin's own scope -
    // a controller working e.g. a TWR or GND position isn't the one this line is meant for.
    public static void Update()
    {
        var callsign = Network.Me.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return;

        var suffixes = callsign.EndsWith("_APP") || callsign.EndsWith("_DEP") ? new[] { "_APP", "_DEP" }
            : callsign.EndsWith("_CTR") ? new[] { "_CTR" }
            : Array.Empty<string>();

        if (suffixes.Length == 0)
            return;

        var extending = new List<string>();

        foreach (var frequency in Audio.VSCSFrequencies)
        {
            if (!frequency.Transmit || frequency.Name == callsign)
                continue;

            if (!suffixes.Any(suffix => frequency.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                continue;

            var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == frequency.Name);
            if (sector == null)
                continue;

            extending.Add($"{sector.Name} {Conversions.FrequencyToString(frequency.Frequency)}");
        }

        SetExtendingLine(extending.Count > 0 ? $"Extending {JoinWithAnd(extending)}" : "");
    }

    // "A", "A and B", or "A, B and C" - matches VatpacPlugin's own wording for this line.
    static string JoinWithAnd(List<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];

    // Replaces any previous "Extending ..." line this method itself added, leaving every other
    // Controller Info line (set by the controller, or by anything else) untouched.
    static void SetExtendingLine(string extendingText)
    {
        var controllerInfo = Network.ControllerInfo;
        if (controllerInfo == null)
            return;

        var newInfo = controllerInfo.Where(line => !line.StartsWith("Extending")).ToList();

        if (extendingText != "")
            newInfo.Add(extendingText);

        Network.ControllerInfo = newInfo.ToArray();
    }
}
