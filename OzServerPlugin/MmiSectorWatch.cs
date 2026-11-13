using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Records who takes a sector out of MMI.SectorsControlled.
//
// MMI.SectorsControlled is process-wide state with exactly one setter (MMI.SetControlledSectors)
// and no record of who called it, and *every* plugin loaded into vatSys can write it. When a sector
// this controller owns silently disappears from the scope, nothing in vatSys or in this plugin says
// which code did it - the symptom (no infill, no jurisdiction over the sector's tags, a VSCS line
// that no longer drops anything because the sector is already gone) shows up long after the write,
// and reads as a bug in whatever noticed it last. Two rounds of this were spent reasoning backwards
// from an ownership log that could only show what reached the backend.
//
// So this watches the list itself. Every MMI.SectorsControlledChanged is diffed against the last
// snapshot; a removal is logged with the sectors that went and the managed stack that was running
// at the time, which names the assembly and method that called SetControlledSectors - this plugin's
// own paths included.
//
// Only removals, and only the frames outside vatSys's own event plumbing, so an ordinary session
// writes nothing: sectors are added far more often than they are taken away, and a deliberate drop
// is a handful of lines an hour rather than a running commentary.
//
// Note for reading the output: vatSys raises SectorsControlledChanged from inside
// MMI.SetControlledSectors, synchronously, so the caller really is on this stack. A write marshalled
// onto the UI thread (BeginInvoke) shows only the message pump above it - that frame is this
// plugin's own RunOnUiThread, and the action's origin is in the log line that follows it.
public static class MmiSectorWatch
{
    static readonly object Gate = new();
    static List<string> _last = new();
    static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        Snapshot();
        MMI.SectorsControlledChanged += (_, _) => OnChanged();
    }

    static List<string> Current() =>
        MMI.SectorsControlled.Where(s => !s.IsDummy).Select(s => s.Name).ToList();

    static void Snapshot()
    {
        lock (Gate)
            _last = Current();
    }

    static void OnChanged()
    {
        List<string> removed;

        lock (Gate)
        {
            var current = Current();
            removed = _last.Where(name => !current.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
            _last = current;
        }

        if (removed.Count == 0)
            return;

        ActionLog.Log("Mmi", $"Removed from controlled sectors: {string.Join(", ", removed)} - by {CallerSummary()}", new
        {
            action = "mmi_sectors_removed",
            sectors = removed.ToArray(),
            caller = CallerFrames()
        });
    }

    // The first frame that is neither this class nor the plumbing every write goes through, which
    // is the one that actually decided to change the list.
    static string CallerSummary() => CallerFrames().FirstOrDefault() ?? "an unidentified caller";

    static string[] CallerFrames()
    {
        try
        {
            return new StackTrace(false).GetFrames()?
                .Select(frame => frame.GetMethod())
                .Where(method => method?.DeclaringType != null)
                .Select(method => $"{method!.DeclaringType!.FullName}.{method.Name}")
                // This class's own frames say nothing; MMI.SetControlledSectors and the event
                // invocation are on every single one of these stacks by construction.
                .Where(name => !name.StartsWith("OzServerPlugin.MmiSectorWatch", StringComparison.Ordinal)
                               && !name.StartsWith("vatsys.MMI.SetControlledSectors", StringComparison.Ordinal)
                               && !name.StartsWith("System.", StringComparison.Ordinal))
                .Take(6)
                .ToArray()
                   ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            // A diagnostic must never be the thing that breaks a session. Nothing here is worth
            // failing an MMI write for, and this runs on the UI thread inside vatSys's own event.
            return Array.Empty<string>();
        }
    }
}
