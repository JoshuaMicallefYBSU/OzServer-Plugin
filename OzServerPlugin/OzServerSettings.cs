using System;
using System.IO;
using Newtonsoft.Json;

namespace OzServerPlugin;

// Persists the OzServer base URL, so switching between a local dev server and production is a
// settings edit rather than a recompile. Stored under %AppData% because the vatSys Plugins folder
// is not guaranteed writable.
public static class OzServerSettings
{
    public const string DefaultBaseUrl = "https://api.ozserver.org";
    const string LegacyDefaultBaseUrl = "https://ozserver.org";

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OzServerPlugin", "settings.json");

    static string? _baseUrl;

    // Never ends in a slash, so callers can append "/api/v1/...".
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

    // The scheme must be http or https - Uri.TryCreate alone accepts any absolute URI, "mailto:x"
    // and "file:///c:/" included - and plain http is allowed only for a loopback host, so a local
    // dev server works while remote traffic is never sent in cleartext.
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
                // Validated on load as well as in the settings window: settings.json is a plain
                // file that anything can edit.
                if (!string.IsNullOrWhiteSpace(data?.BaseUrl))
                {
                    var stored = data!.BaseUrl!.TrimEnd('/');

                    // A settings file saved by an early release holds the former default, the
                    // website host, which would otherwise override the API's own host for good.
                    // Only that exact value is migrated; custom and dev endpoints are left alone.
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
            // A corrupt or unreadable settings file falls back to the default.
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
