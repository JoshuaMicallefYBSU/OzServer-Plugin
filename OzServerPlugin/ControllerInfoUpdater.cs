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
        if (!Network.IsConnected)
            return;

        var callsign = Network.Me.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return;

        var suffixes = callsign.EndsWith("_APP") || callsign.EndsWith("_DEP") ? new[] { "_APP", "_DEP" }
            : callsign.EndsWith("_CTR") ? new[] { "_CTR" }
            : Array.Empty<string>();

        // Still has to clear, not just return: a position that gets no "Extending" line of its own
        // may well have had one a moment ago. Returning here left a stale line published for the
        // rest of the session after a change from e.g. a _CTR position to a _TWR one.
        if (suffixes.Length == 0)
        {
            SetExtendingLine("");
            return;
        }

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

    // The exact line this class last published, so the replacement below can remove it by value.
    // Matching on the "Extending" prefix instead meant deleting any line the controller had
    // written themselves that happened to start with that word ("Extending hours until 1200", say)
    // - Controller Info is free text they own, and this only has standing to remove its own line.
    static string? _publishedLine;
    // Whether the one-per-session prefix sweep below has run yet.
    static bool _swept;

    // Replaces any previous "Extending ..." line this class itself added, leaving every other
    // Controller Info line (set by the controller, or by anything else) untouched.
    static void SetExtendingLine(string extendingText)
    {
        var controllerInfo = Network.ControllerInfo;
        if (controllerInfo == null)
            return;

        IEnumerable<string> kept = controllerInfo;

        if (!_swept)
        {
            // First update of the session still sweeps by prefix, once: a line left behind by a
            // previous session (or a crash) predates _publishedLine and has nothing to match
            // against, so it would otherwise sit there permanently. Every update after this one
            // removes only the exact line this class put there.
            kept = kept.Where(line => !line.StartsWith("Extending ", StringComparison.Ordinal));
            _swept = true;
        }
        else if (_publishedLine != null)
        {
            kept = kept.Where(line => line != _publishedLine);
        }

        var newInfo = kept.ToList();

        if (extendingText != "")
            newInfo.Add(extendingText);

        _publishedLine = extendingText == "" ? null : extendingText;

        Network.ControllerInfo = newInfo.ToArray();
    }
}
