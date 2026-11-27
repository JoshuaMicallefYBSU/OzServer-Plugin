using System.Reflection;
using System.Text;

namespace OzServerPlugin;

// Stand-ins for the two plugin files TestBuild.csproj leaves out.

// The test host's local server, which plays OzServer and the release downloads (Program.cs).
public static class OzServerSettings
{
    public const string BaseUrl = "http://127.0.0.1:47811";
    public const string PluginDownloadPrefix = BaseUrl + "/releases/download/";
}

// The plugin's log, written to the file the host names in OZ_TEST_LOG, each line marked with the
// build that wrote it. The real one appends to the controller's ozserver_<date>.txt.
//
// OZ_TEST_FAIL_VERSION makes the build of that version throw the first time anything touches its
// log, which is early in Plugin's constructor: a release that cannot start.
public static class ActionLog
{
    static readonly string Build = "v" + Assembly.GetExecutingAssembly().GetName().Version;
    static readonly string? LogPath = Environment.GetEnvironmentVariable("OZ_TEST_LOG");
    static readonly object Lock = new();

    static ActionLog()
    {
        if (Environment.GetEnvironmentVariable("OZ_TEST_FAIL_VERSION") == Assembly.GetExecutingAssembly().GetName().Version?.ToString())
            throw new InvalidOperationException("this test build is made to fail as it starts");
    }

    public static event Action<string, string>? LineWritten;
    public static event Action<string, string, object?>? StructuredLineWritten;

    public static void Log(string category, string message, object? context = null)
    {
        try
        {
            LineWritten?.Invoke(category, message);
            StructuredLineWritten?.Invoke(category, message, context);
        }
        catch
        {
            // As the real log: a subscriber never breaks what is being logged.
        }

        if (LogPath == null)
            return;

        try
        {
            lock (Lock)
            {
                // Shared for writing: two builds in one process write the same file.
                var line = new UTF8Encoding(false).GetBytes($"[{DateTime.Now:HH:mm:ss.fff}] [{Build}] {category}: {message}{Environment.NewLine}");
                using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(line, 0, line.Length);
            }
        }
        catch
        {
        }
    }

    public static void LogAttempt(string method, string path, bool success, string? detail = null) =>
        Log("API", success ? $"{method} {path} -> ok" : $"{method} {path} -> failed: {detail}");
}
