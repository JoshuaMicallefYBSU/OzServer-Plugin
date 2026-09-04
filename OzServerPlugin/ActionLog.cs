using System;
using System.IO;
using vatsys;

namespace OzServerPlugin;

// Plain-text audit trail of what this plugin does to sectors and tags - one file per calendar
// day, in Documents\vatSys Files\ (vatsys.Helpers.GetFilesFolder(), the same folder vatSys itself
// uses for profiles and its own logs), named ozserver_<yyyyMMdd>.txt. The path is recomputed from
// the current date on every write rather than cached, so multiple plugin sessions started on the
// same day append to the same file, and a session still running when the date rolls over at
// midnight rolls over to the new day's file with it, with nothing extra needed to make that happen.
//
// Two ways in: Log for a plain record of something that happened, LogAttempt for the
// "attempts to do anything" requirement - every call OzServerApiClient makes, successful or not
// (see its own PostRawAsync/GetAsync). Both funnel through the same file/lock; the split exists
// only so callers don't have to invent their own "ok"/"failed" wording every time.
//
// Deliberately cannot throw into a caller: a sync loop's actual job (claiming a sector, pushing
// an FDR, activating a tag) must never fail because the audit trail couldn't be written to.
public static class ActionLog
{
    static readonly object Lock = new();

    // Raised for every line, after it has been written. Lets ClientLogForwarder copy the decisions
    // to OzServer without this class knowing anything about the network - the file remains the
    // complete record whether or not anything is listening.
    public static event Action<string, string>? LineWritten;
    public static event Action<string, string, object?>? StructuredLineWritten;

    public static void Log(string category, string message, object? context = null) => Write(category, message, context);

    public static void LogAttempt(string method, string path, bool success, string? detail = null) =>
        Write("API", success
            ? $"{method} {path} -> ok{(detail != null ? $" ({detail})" : "")}"
            : $"{method} {path} -> failed: {detail}");

    static void Write(string category, string message, object? context = null)
    {
        Notify(category, message, context);

        try
        {
            lock (Lock)
            {
                var path = Path.Combine(Helpers.GetFilesFolder(), $"ozserver_{DateTime.Now:yyyyMMdd}.txt");

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {category}: {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the reason a real action fails - see the class comment.
        }
    }

    // Outside the file lock and swallowing everything: a subscriber must not be able to hold up the
    // audit trail, nor break the operation whose log line this is.
    static void Notify(string category, string message, object? context)
    {
        try
        {
            LineWritten?.Invoke(category, message);
            StructuredLineWritten?.Invoke(category, message, context);
        }
        catch
        {
        }
    }
}
