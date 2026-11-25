using System;

namespace OzServerPlugin;

// vatSys runs with UnhandledExceptionMode.ThrowException and does not guard Application.Run, so an
// exception escaping a delegate posted to its UI thread - or thrown back into one of its own event
// raises - ends the whole process. Everything this plugin runs there goes through Guard, which
// turns such an exception into an entry in the Errors window instead.
static class UiThread
{
    public static Action Guard(Action action) => () =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Keyed on where it was thrown, so one recurring fault is reported once per window
            // without hiding a different one.
            var site = ex.TargetSite is { } method ? $"{method.DeclaringType?.Name}.{method.Name}" : "unknown";
            ErrorThrottle.Report("ui:" + site, $"Something went wrong in OzServer ({site}): {ex.Message}", ex);
        }
    };
}
