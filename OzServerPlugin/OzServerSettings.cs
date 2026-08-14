using System;
using System.IO;
using Newtonsoft.Json;

namespace OzServerPlugin;

// Persists the OzServer backend's base URL so it's a one-time edit to jump between the local dev
// server and production, rather than a hardcoded value that needs a recompile. Stored under
// %AppData% (not next to the DLL) since the vatSys Plugins folder isn't guaranteed writable.
public static class OzServerSettings
{
    public const string DefaultBaseUrl = "https://ozserver.org";

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

    static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var data = JsonConvert.DeserializeObject<StoredSettings>(File.ReadAllText(SettingsPath));
                if (!string.IsNullOrWhiteSpace(data?.BaseUrl))
                {
                    _baseUrl = data!.BaseUrl!.TrimEnd('/');
                    return;
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
