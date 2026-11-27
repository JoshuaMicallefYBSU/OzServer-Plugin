namespace OzServerPlugin;

// Where OzServer is. Fixed by the plugin: the API is managed alongside it, so there is no setting
// for a controller to change. An address saved by an earlier build's settings window
// (%AppData%\OzServerPlugin\settings.json) is no longer read.
public static class OzServerSettings
{
    // Never ends in a slash, so callers can append "/api/v1/...".
    public const string BaseUrl = "https://api.ozserver.org";

    // The only place a plugin update is ever downloaded from: this repository's GitHub releases.
    // Ends in a slash. Held to by PluginUpdater whichever source named the release.
    public const string PluginDownloadPrefix = "https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/download/";
}
