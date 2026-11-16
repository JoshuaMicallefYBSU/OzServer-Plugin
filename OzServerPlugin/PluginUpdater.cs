using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using vatsys;

namespace OzServerPlugin;

// Keeps the plugin up to date without anyone copying a DLL by hand.
//
// Which release to install is asked of OzServer (GET /plugin/release), which keeps a cached copy of
// the repository's GitHub releases - one cheap request per client, no GitHub API quota spent, and a
// release can be held back centrally. If OzServer cannot answer, GitHub's releases API is asked
// directly. Either way the file itself is only ever downloaded from this repository's own GitHub
// releases (DownloadPrefix).
//
// A plugin cannot replace its own file: vatSys holds OzServerPlugin.dll open for the whole session,
// so it cannot be overwritten or deleted while running. Windows does allow a loaded file to be
// renamed, so the update is staged:
//
//   1. download the new DLL beside the current one, as .update
//   2. verify it (VerifyDownload, IsValidUpgrade)
//   3. rename the running DLL to .backup and move .update into its place
//   4. the next vatSys start loads the new DLL and deletes the .backup
//
// The running session keeps the version it started with. Neither staging file ends in .dll, so
// vatSys never tries to load one, and the previous version stays on disk as .backup until a later
// session has started successfully - recovering from a bad release is renaming that file back.
public class PluginUpdater
{
    const string Repository = "JoshuaMicallefYBSU/OzServer-Plugin";

    // The list endpoint rather than /releases/latest, which excludes prereleases - and releases are
    // published as prereleases.
    const string GitHubReleasesApi = "https://api.github.com/repos/" + Repository + "/releases";

    // The only location a download is accepted from, whichever source named it. A base URL changed
    // in settings, or a compromised OzServer, can choose among published releases but cannot serve
    // a file of its own.
    const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    const string AssetName = "OzServerPlugin.dll";
    const string AssemblyIdentity = "OzServerPlugin";

    // Far above any real build (about 300 KB), and a bound on what a bad response can make us buffer.
    const long MaxDownloadBytes = 20 * 1024 * 1024;

    // Clear of vatSys's own startup, when a controller is waiting on the radar to come up.
    static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    // Catches a release published during a long session.
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // Held for the plugin's lifetime: vatSys gives a plugin no shutdown hook to dispose it in.
    readonly Timer _timer;

    sealed class Release
    {
        public Version Version = new();
        public string Url = "";
        // Hex SHA-256 of the asset, when the source reported one.
        public string? Sha256;
        public bool IsZip;
    }

    public PluginUpdater()
    {
        // The process that held the previous version open has exited, so its backup can go.
        TryDeleteStaleBackup();

        _timer = new Timer(_ => _ = CheckAsync(), null, StartupDelay, CheckInterval);
    }

    async Task CheckAsync()
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            var installedPath = Assembly.GetExecutingAssembly().Location;

            // An assembly that was not loaded from a file on disk has nothing to replace.
            if (string.IsNullOrEmpty(installedPath) || !File.Exists(installedPath))
                return;

            var release = await FindReleaseAsync().ConfigureAwait(false);
            if (release == null || release.Version.CompareTo(current) <= 0)
                return;

            var staged = installedPath + ".update";
            await DownloadAsync(release, staged).ConfigureAwait(false);

            // Checked before the file can take the live name: a plugin that fails to load takes
            // sector syncing with it.
            if (!IsValidUpgrade(staged, current))
            {
                TryDelete(staged);
                ActionLog.Log("Update", $"Discarded the {release.Version} download - it isn't a newer {AssemblyIdentity}.");
                return;
            }

            Install(installedPath, staged);

            // The running assembly is still the old version, so a later check would find the same
            // release again and reinstall over the backup. One install per session.
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            ActionLog.Log("Update", $"Updated to {release.Version}; it will be used the next time vatSys starts.");

            // Not an error, but the Errors window is the only notification surface vatSys gives a
            // plugin. It is shown without taking focus and marshals to the UI thread itself, so it
            // is safe to call from this timer thread. vatSys prefixes the source, so the line reads
            // "OzServer: An update ...". An OzServerNotice, so it is not reported as a failure.
            Errors.Add(
                new OzServerNotice($"An update ({release.Version}) was detected and installed. It will be loaded at the next vatSys launch."),
                "OzServer");
        }
        // OperationCanceledException covers the TaskCanceledException HttpClient raises on timeout.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Offline, or the source is unavailable. The installed version keeps working and the
            // next check retries, so this goes to the log rather than in front of the controller.
            ActionLog.Log("Update", $"Couldn't check for an update: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Anything else needs someone to look at it - a plugin folder that isn't writable, say.
            Errors.Add(new Exception($"Couldn't check for a plugin update: {ex.Message}", ex), "OzServer");
        }
    }

    // OzServer's answer if it gives one, GitHub's otherwise. Null when there is no release to offer.
    static async Task<Release?> FindReleaseAsync()
    {
        // A disabled session sends nothing to OzServer, and this is no exception.
        if (!OzServerMode.Disabled)
        {
            try
            {
                var (answered, release) = await GetOzServerReleaseAsync().ConfigureAwait(false);
                if (answered)
                    return release;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                ActionLog.Log("Update", $"OzServer couldn't be asked for the current release ({ex.Message}); asking GitHub instead.");
            }
        }

        return await GetGitHubReleaseAsync().ConfigureAwait(false);
    }

    // Answered is false when OzServer has nothing usable to say - an older backend without the
    // endpoint, one that has not reached GitHub yet, or a reply naming a download this plugin will
    // not accept - and the caller should ask GitHub instead.
    static async Task<(bool Answered, Release? Release)> GetOzServerReleaseAsync()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(OzServerSettings.BaseUrl + "/api/v1/plugin/release").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (false, null);

        var body = JToken.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        // A JSON null is a definite answer: there is no release to offer.
        if (body.Type == JTokenType.Null)
            return (true, null);

        var url = (string?)body["url"];
        if (body.Type != JTokenType.Object
            || !TryParseVersion((string?)body["version"], out var version)
            || !IsAcceptedDownload(url))
            return (false, null);

        return (true, new Release { Version = version, Url = url!, Sha256 = (string?)body["sha256"] });
    }

    static async Task<Release?> GetGitHubReleaseAsync()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(GitHubReleasesApi).ConfigureAwait(false);

        // A repository with no releases answers 404, which is an ordinary state rather than a failure.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Release? best = null;

        foreach (var release in JArray.Parse(body))
        {
            if ((bool?)release["draft"] == true)
                continue;

            if (!TryParseVersion((string?)release["tag_name"], out var version))
                continue;

            if (best != null && version.CompareTo(best.Version) <= 0)
                continue;

            var assets = release["assets"];
            if (assets == null)
                continue;

            // A bare DLL is preferred. Early releases attached only a zip, which is unpacked after
            // download.
            var dll = assets.FirstOrDefault(a =>
                string.Equals((string?)a["name"], AssetName, StringComparison.OrdinalIgnoreCase));

            var zip = dll == null
                ? assets.FirstOrDefault(a => ((string?)a["name"])?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                : null;

            var chosen = dll ?? zip;
            var url = (string?)chosen?["browser_download_url"];
            if (chosen == null || !IsAcceptedDownload(url))
                continue;

            // GitHub reports the digest as "sha256:<hex>"; older assets have none.
            var digest = (string?)chosen["digest"];

            best = new Release
            {
                Version = version,
                Url = url!,
                Sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : null,
                IsZip = dll == null
            };
        }

        return best;
    }

    // Tags are written "v0.1.2"; the leading v is not part of the version.
    static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version();
        return text != null && Version.TryParse(text.Trim().TrimStart('v', 'V'), out version!);
    }

    static bool IsAcceptedDownload(string? url) =>
        url != null && url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase);

    static async Task DownloadAsync(Release release, string destination)
    {
        using var client = CreateClient();
        client.MaxResponseContentBufferSize = MaxDownloadBytes;
        var bytes = await client.GetByteArrayAsync(release.Url).ConfigureAwait(false);

        VerifyDownload(bytes, release);

        TryDelete(destination);

        if (!release.IsZip)
        {
            File.WriteAllBytes(destination, bytes);
            return;
        }

        // Unpacked to the same staged path a bare DLL is written to, so everything after this point
        // is identical either way.
        var archivePath = destination + ".zip";
        try
        {
            File.WriteAllBytes(archivePath, bytes);

            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, AssetName, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
                throw new InvalidOperationException($"The release archive does not contain {AssetName}.");

            entry.ExtractToFile(destination, overwrite: true);
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    // Catches a truncated or substituted download before anything is written. A release with no
    // recorded digest is still held to IsValidUpgrade.
    static void VerifyDownload(byte[] bytes, Release release)
    {
        if (string.IsNullOrWhiteSpace(release.Sha256))
            return;

        using var sha = SHA256.Create();
        var actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");

        if (!string.Equals(actual, release.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The {release.Version} download does not match its published checksum.");
    }

    // Whether the staged file is a newer build of this plugin. GetAssemblyName reads the metadata
    // without loading the assembly, so the file is never run in order to be judged, and no handle
    // is left open to block the rename.
    static bool IsValidUpgrade(string path, Version? current)
    {
        try
        {
            var name = AssemblyName.GetAssemblyName(path);

            return string.Equals(name.Name, AssemblyIdentity, StringComparison.Ordinal)
                   && name.Version != null
                   && name.Version.CompareTo(current) > 0;
        }
        catch
        {
            // Not a managed assembly - a truncated download or an error page.
            return false;
        }
    }

    // Renames the running DLL aside and moves the new one into its place. Windows permits both on
    // a loaded file, where it would refuse a delete or an overwrite.
    static void Install(string installedPath, string staged)
    {
        var backup = installedPath + ".backup";
        TryDelete(backup);

        // If this throws, nothing has moved and the folder is as it was.
        File.Move(installedPath, backup);

        try
        {
            File.Move(staged, installedPath);
        }
        catch
        {
            // Put the working version back rather than leave the folder with no DLL to load.
            File.Move(backup, installedPath);
            throw;
        }
    }

    static void TryDeleteStaleBackup()
    {
        try
        {
            var installedPath = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(installedPath))
                TryDelete(installedPath + ".backup");
        }
        catch
        {
            // Housekeeping only; the next start tries again.
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Still locked, or not ours to remove.
        }
    }

    static HttpClient CreateClient()
    {
        try
        {
            // .NET Framework 4.7.2 takes its protocols from here rather than the OS default, and
            // GitHub refuses anything below TLS 1.2.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
            // Best effort.
        }

        var client = new HttpClient { Timeout = RequestTimeout };
        // GitHub rejects API requests that don't identify themselves.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AssemblyIdentity, "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
