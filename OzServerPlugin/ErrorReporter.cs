using System;
using System.Text.RegularExpressions;
using vatsys;

namespace OzServerPlugin;

// Copies every error this plugin raises in the vatSys Errors window into the action log, under
// the "Error" category. From there ClientLogForwarder sends it to OzServer, which relays errors to
// the maintainers - so a failure is seen when it happens, not when a controller reports it.
//
// Hooked on vatSys's own Errors.ErrorsChanged rather than at each call site, so every existing and
// future Errors.Add is covered. The handler runs on whichever thread raised the error, inside
// vatSys's Errors.Add, so it does nothing slow and never throws.
static class ErrorReporter
{
    public const string Category = "Error";

    // Every source this plugin reports under starts with this ("OzServer"),
    // and so does the default Source of an exception thrown from this assembly.
    const string SourcePrefix = "OzServer";

    const int MaxCauseLength = 1500;

    static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        Errors.ErrorsChanged += OnErrorsChanged;
    }

    static void OnErrorsChanged(object? sender, Errors.ErrorArgs e)
    {
        try
        {
            // Removed is the controller dismissing an entry; Error is null when the list is cleared.
            if (e.Removed || e.Error == null || e.Error is OzServerNotice)
                return;

            var source = e.Error.Source;
            if (source == null || !source.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
                return;

            // A failure reported with new Exception(message, cause) carries its stack on the cause.
            var cause = WithoutProfilePath((e.Error.InnerException ?? (e.Error.StackTrace != null ? e.Error : null))?.ToString());
            if (cause != null && cause.Length > MaxCauseLength)
                cause = cause.Substring(0, MaxCauseLength);

            ActionLog.Log(Category, WithoutProfilePath(e.Error.Message) ?? "", new
            {
                source,
                exception = (e.Error.InnerException ?? e.Error).GetType().Name,
                cause
            });
        }
        catch
        {
            // Reporting an error must never raise another inside vatSys's Errors.Add.
        }
    }

    // "C:\Users\<Windows account>" written as "~". A file-system exception quotes the path it
    // failed on, these lines are sent to OzServer and on to the maintainers, and the account name
    // is often the controller's own.
    //
    // The folder is looked up here, inside OnErrorsChanged's catch, rather than in a static field:
    // a type initializer that threw would take Install, and the plugin's constructor, with it.
    static string? WithoutProfilePath(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? text
            : Regex.Replace(text!, Regex.Escape(profile), "~", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

// Something shown in the Errors window that is not an error - it is the only notification surface
// vatSys gives a plugin. ErrorReporter leaves these out.
sealed class OzServerNotice : Exception
{
    public OzServerNotice(string message) : base(message)
    {
    }
}
