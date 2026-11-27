using System;

namespace OzServerPlugin;

// vatSys runs with UnhandledExceptionMode.ThrowException and does not guard Application.Run, so an
// exception escaping a delegate posted to its UI thread - or thrown back into one of its own event
// raises - ends the whole process. Everything this plugin runs there goes through Guard, which
// turns such an exception into an entry in the Errors window instead.
static class UiThread
{
    //
    // Guard also times what it runs, for the Perf line that totals the plugin's share of the UI
    // thread (PluginWork). A piece run inline from a thread that is not the UI thread - before
    // the main form exists - is counted with the rest; there are few.
    public static Action Guard(Action action) => () =>
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        long allocated = 0;
        try
        {
            allocated = PluginWork.Allocated();
            action();
        }
        catch (Exception ex)
        {
            // Keyed on where it was thrown, so one recurring fault is reported once per window
            // without hiding a different one.
            var site = ex.TargetSite is { } method ? $"{method.DeclaringType?.Name}.{method.Name}" : "unknown";
            ErrorThrottle.Report("ui:" + site, $"Something went wrong in OzServer ({site}): {ex.Message}", ex);
        }
        finally
        {
            // The measuring must not be the thing that lets an exception out of here.
            try
            {
                PluginWork.Ui(action,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                    PluginWork.Allocated() - allocated);
            }
            catch (Exception)
            {
            }
        }
    };
}
