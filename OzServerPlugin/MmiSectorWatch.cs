using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Logs what removes a sector from MMI.SectorsControlled.
//
// MMI.SectorsControlled is process-wide state with one setter (MMI.SetControlledSectors), which
// any plugin loaded into vatSys can call and which keeps no record of its caller. When a sector
// this controller owns disappears from the scope, the symptoms show up well after the write and
// nothing identifies the code that made it.
//
// Each MMI.SectorsControlledChanged is diffed against the previous snapshot. A removal is logged
// with the sectors that went and the managed stack at the time, which names the method that called
// SetControlledSectors - this plugin's own paths included. Additions are not logged, so an
// ordinary session writes little.
//
// Reading the output: vatSys raises SectorsControlledChanged synchronously from inside
// SetControlledSectors, so the caller is on the stack. A write marshalled onto the UI thread with
// BeginInvoke shows only the marshalling frames; its origin is in the log line that follows it.
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

    // The first frame that is neither this class nor the plumbing every write goes through: the
    // one that decided to change the list.
    static string CallerSummary() => CallerFrames().FirstOrDefault() ?? "an unidentified caller";

    static string[] CallerFrames()
    {
        try
        {
            return new StackTrace(false).GetFrames()?
                .Select(frame => frame.GetMethod())
                .Where(method => method?.DeclaringType != null)
                .Select(method => $"{method!.DeclaringType!.FullName}.{method.Name}")
                // Frames that are on every one of these stacks: this class,
                // MMI.SetControlledSectors and the event invocation.
                .Where(name => !name.StartsWith("OzServerPlugin.MmiSectorWatch", StringComparison.Ordinal)
                               && !name.StartsWith("vatsys.MMI.SetControlledSectors", StringComparison.Ordinal)
                               && !name.StartsWith("System.", StringComparison.Ordinal))
                .Take(6)
                .ToArray()
                   ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            // A diagnostic must not fail an MMI write: this runs on the UI thread inside vatSys's
            // own event.
            return Array.Empty<string>();
        }
    }
}
