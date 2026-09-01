using System;
using System.IO;
using Newtonsoft.Json;

namespace OzServerPlugin;

// Persists the OzServer backend's base URL so it's a one-time edit to jump between the local dev
// server and production, rather than a hardcoded value that needs a recompile. Stored under
// %AppData% (not next to the DLL) since the vatSys Plugins folder isn't guaranteed writable.
public static class OzServerSettings
{
    public const string DefaultBaseUrl = "https://api.ozserver.org";
    const string LegacyDefaultBaseUrl = "https://ozserver.org";

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OzServerPlugin", "settings.json");

    static string? _baseUrl;

    // Always trailing-slash-free, so callers can just append "/api/v1/...".
    public static string BaseUrl
    {
        get
        {
            if (_baseUrl == null)
                Load();
            return _baseUrl!;
        }
        set
        {
            _baseUrl = string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.TrimEnd('/');
            Save();
        }
    }

    // Keep remote traffic encrypted and reject URI schemes HttpClient was never intended to use.
    // Two things are checked:
    //   - The scheme must be http or https. Uri.TryCreate(..., UriKind.Absolute) on its own is far
    //     too permissive for this: "mailto:x", "file:///c:/", and "foo:bar" are all absolute URIs
    //     and all passed the old check, leaving every call to fail in a way that pointed nowhere
    //     near the actual cause.
    //   - Plain http is allowed only for a loopback host, so a dev server on localhost still
    //     works while remote traffic cannot be sent in cleartext.
    public static bool IsValidBaseUrl(string? url, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "That doesn't look like a valid URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            error = "The URL must start with https:// (or http:// for a local dev server).";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback)
        {
            error = "Plain http:// is only allowed for localhost - use https:// for a remote server.";
            return false;
        }

        return true;
    }

    static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var data = JsonConvert.DeserializeObject<StoredSettings>(File.ReadAllText(SettingsPath));
                // Validated on the way in, not just on the way out through the settings window -
                // settings.json is a plain file under %AppData% that anything can edit, so a value
                // that never went through the window still has to clear the same bar before the
                // token gets sent to it.
                if (!string.IsNullOrWhiteSpace(data?.BaseUrl))
                {
                    var stored = data!.BaseUrl!.TrimEnd('/');

                    // Older releases persisted the then-default website host on first save. Once
                    // the API moved to its own host that indistinguishable copy of the old default
                    // otherwise became a permanent override, so an upgraded DLL kept sending its
                    // (new API) token to the old Laravel API and received a 401. Only migrate the
                    // exact former production default; real custom/dev endpoints stay untouched.
                    if (string.Equals(stored, LegacyDefaultBaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _baseUrl = DefaultBaseUrl;
                        Save();
                        return;
                    }

                    if (IsValidBaseUrl(stored, out var error))
                    {
                        _baseUrl = stored;
                        return;
                    }

                    vatsys.Errors.Add(
                        new Exception($"Ignoring the OzServer base URL in {SettingsPath} ('{stored}'): {error} Falling back to {DefaultBaseUrl}."),
                        "OzServer Settings");
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt/unreadable settings file shouldn't block the plugin - fall back to default.
            vatsys.Errors.Add(ex, "OzServer Settings");
        }

        _baseUrl = DefaultBaseUrl;
    }

    static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(new StoredSettings { BaseUrl = _baseUrl }, Formatting.Indented));
        }
        catch (Exception ex)
        {
            vatsys.Errors.Add(ex, "OzServer Settings");
        }
    }

    class StoredSettings
    {
        public string? BaseUrl { get; set; }
    }
}
