namespace OzServerPlugin;

// Stand-ins for the two plugin files WindowShots.csproj leaves out.

// An address nothing listens on: every API call made while a window is being drawn fails at once,
// on this PC, and never reaches OzServer.
public static class OzServerSettings
{
    public const string BaseUrl = "http://127.0.0.1:9";
    public const string PluginDownloadPrefix = BaseUrl + "/releases/download/";
}

// The plugin's log, kept in memory. The real one appends to the controller's ozserver_<date>.txt.
public static class ActionLog
{
    public static readonly List<string> Lines = new();

    public static event Action<string, string>? LineWritten;
    public static event Action<string, string, object?>? StructuredLineWritten;

    public static void Log(string category, string message, object? context = null)
    {
        lock (Lines)
            Lines.Add($"{category}: {message}");
        try
        {
            LineWritten?.Invoke(category, message);
            StructuredLineWritten?.Invoke(category, message, context);
        }
        catch
        {
            // As the real log: a subscriber never breaks what is being logged.
        }
    }

    public static void LogAttempt(string method, string path, bool success, string? detail = null) =>
        Log("API", success ? $"{method} {path} -> ok" : $"{method} {path} -> failed: {detail}");
}
