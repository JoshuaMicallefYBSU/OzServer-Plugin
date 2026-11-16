using System;
using System.IO;
using vatsys;

namespace OzServerPlugin;

// Plain-text audit trail of what the plugin does to sectors and tags: one file per calendar day,
// ozserver_<yyyyMMdd>.txt, in vatSys's own files folder (Helpers.GetFilesFolder()). The path is
// derived from the current date on every write, so sessions started on the same day share a file
// and a session running past midnight moves to the next day's.
//
// Log records something that happened; LogAttempt records an API call and its outcome, in one
// wording for every caller.
//
// Never throws into a caller: claiming a sector, pushing an FDR or activating a tag must not fail
// because the log could not be written.
public static class ActionLog
{
    static readonly object Lock = new();

    // Raised for every line, before it is written to the file. ClientLogForwarder subscribes to
    // copy lines to OzServer; the file remains the complete record.
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
            // See the class comment.
        }
    }

    // Outside the file lock and swallowing everything: a subscriber must not hold up the file
    // write or break the operation being logged.
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
