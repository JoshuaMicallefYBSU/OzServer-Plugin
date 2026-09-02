using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using vatsys;

namespace OzServerPlugin;

// Keeps the plugin up to date from the repository's GitHub releases, so nobody has to be told to go
// and copy a DLL by hand.
//
// The awkward part is that a plugin cannot replace its own file. vatSys loads OzServerPlugin.dll and
// holds it open for the whole session, so the file cannot be overwritten or deleted while it is
// running - which is exactly why installing a build by hand means closing vatSys first. That also
// rules out the obvious reading of "update when vatSys is closed": when vatSys is closed, this code
// isn't running either, and a plugin has no business leaving a separate updater process behind.
//
// Windows does allow a *loaded* file to be renamed, though (the loader shares it for delete, which is
// what Chrome and Firefox's updaters lean on too), so the update is staged instead:
//
//   1. download the new DLL alongside the current one, as .update
//   2. verify it before it is allowed anywhere near the plugin folder's live name
//   3. rename the running DLL to .backup, and move .update into its place
//   4. next time vatSys starts it loads the new one, and deletes the .backup
//
// The controller sees nothing at any point; the session they are in keeps running the version it
// started with, and the new one is simply there next time.
//
// Neither staging file can itself be mistaken for a plugin - vatSys scans the Plugins tree for *.dll,
// and these end in .update/.backup - so a half-finished download is never something vatSys tries to
// load.
//
// Nothing is deleted while it might still be needed. The previous version stays on disk as .backup
// until a later session has successfully started, and step 4 only runs once this plugin has loaded -
// so if an update ever *does* ship something that won't load, the backup is still sitting there and
// recovering is renaming one file back.
public class PluginUpdater
{
    // The list endpoint, not /releases/latest.
    //
    // /latest excludes prereleases, and every release published so far is marked prerelease - so it
    // answered 404 and this quietly concluded there was nothing to update to. Every session, since
    // the updater was written. The list includes them, so a prerelease is found like any other.
    //
    // Drafts still have to be excluded by hand, since the list does include those and a draft is not
    // something anyone should be handed.
    const string ReleasesApi = "https://api.github.com/repos/JoshuaMicallefYBSU/OzServer-Plugin/releases";
    const string AssetName = "OzServerPlugin.dll";
    const string AssemblyIdentity = "OzServerPlugin";

    // Long enough that the check never competes with vatSys's own startup, which is when a controller
    // is most likely to be waiting on the radar to come up.
    static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    // A session can run for a whole shift; this catches a release published part-way through one.
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // Held for the plugin's lifetime, like the other timer-driven syncs - there is no plugin shutdown
    // hook to dispose it in, and the process ending is what stops it.
    readonly Timer _timer;

    public PluginUpdater()
    {
        // Anything left from a previous session's update can go now: whatever was holding the old file
        // open was the *previous* process, which no longer exists.
        TryDeleteStaleBackup();

        _timer = new Timer(_ => _ = CheckAsync(), null, StartupDelay, CheckInterval);
    }

    async Task CheckAsync()
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            var installedPath = Assembly.GetExecutingAssembly().Location;

            // No path means the assembly came from somewhere other than a file on disk (shadow
            // copying, a host that streams it). There is nothing to replace.
            if (string.IsNullOrEmpty(installedPath) || !File.Exists(installedPath))
                return;

            var (version, downloadUrl, isZip) = await GetLatestReleaseAsync().ConfigureAwait(false);

            if (version == null || downloadUrl == null || version.CompareTo(current) <= 0)
                return;

            var staged = installedPath + ".update";
            await DownloadAsync(downloadUrl, staged, isZip).ConfigureAwait(false);

            // Verified before it is allowed to take the live name. A truncated download, or an error
            // page saved under a .dll name, would otherwise be what vatSys tries to load next time -
            // and a plugin that fails to load takes every controller's sector syncing with it.
            if (!IsValidUpgrade(staged, current))
            {
                TryDelete(staged);
                ActionLog.Log("Update", $"Discarded the {version} download - it isn't a newer {AssemblyIdentity}.");
                return;
            }

            Install(installedPath, staged);

            // Done for this session. The running assembly is still the old version, so without this
            // the next check would compare against it, decide it is out of date all over again, and
            // reinstall on top of the file it just wrote - overwriting the real backup in the process.
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            ActionLog.Log("Update", $"Updated to {version}; it will be used the next time vatSys starts.");

            // Not an error, despite the call. The Errors window is simply the only notification
            // surface vatSys gives a plugin, and this is worth one line: the controller is running a
            // version that is no longer the one on disk, and restarting is what closes that gap.
            //
            // It behaves well for the purpose - ErrorWindow.AddError shows the window with
            // SW_SHOWNOACTIVATE and SetWindowPos(..., NOACTIVATE), so it appears above the main form
            // without stealing focus from anything the controller is doing, and Errors_Changed
            // marshals to the UI thread itself (BeginInvoke when InvokeRequired), which matters
            // because this runs on the timer's threadpool thread.
            //
            // Worded without "for OzServer": the label is rendered as Source + ": " + Message, so the
            // line already reads "OzServer: An update ...".
            Errors.Add(
                new Exception($"An update ({version}) was detected and installed. It will be loaded at the next vatSys launch."),
                "OzServer");
        }
        // OperationCanceledException covers the TaskCanceledException HttpClient raises on timeout.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Offline, or GitHub is having a moment. Not worth putting in front of a controller in the
            // Errors window - there is nothing for them to do about it, and the version they already
            // have keeps working - so it goes to the log and the next check picks it up.
            ActionLog.Log("Update", $"Couldn't reach GitHub to check for an update: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Anything else is unexpected enough to be worth surfacing - a permissions problem on the
            // plugin folder, say, which does need someone to look at it.
            Errors.Add(new Exception($"Couldn't check for a plugin update: {ex.Message}", ex), "OzServer");
        }
    }

    static async Task<(Version? Version, string? DownloadUrl, bool IsZip)> GetLatestReleaseAsync()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(ReleasesApi).ConfigureAwait(false);

        // A repository with no releases at all answers 404 here, which is a perfectly ordinary state
        // and not a failure worth logging every session. Handled before EnsureSuccessStatusCode so
        // it isn't reported as an error.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (null, null, false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Version? bestVersion = null;
        string? bestUrl = null;
        var bestIsZip = false;

        foreach (var release in JArray.Parse(body))
        {
            if ((bool?)release["draft"] == true)
                continue;

            // Tags are conventionally written "v0.1.2"; the leading v is not part of the version.
            var tag = (string?)release["tag_name"];
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                continue;

            if (bestVersion != null && version.CompareTo(bestVersion) <= 0)
                continue;

            // The releases published so far attach the plugin as OzServerPlugin-v0.1.4.zip, not as a
            // bare OzServerPlugin.dll - so matching only the exact DLL name found nothing even once
            // the 404 above was dealt with. A raw DLL is still preferred where one exists; a zip is
            // unpacked after download.
            var assets = release["assets"];
            if (assets == null)
                continue;

            var dll = assets.FirstOrDefault(a =>
                string.Equals((string?)a["name"], AssetName, StringComparison.OrdinalIgnoreCase));

            var zip = dll == null
                ? assets.FirstOrDefault(a => ((string?)a["name"])?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                : null;

            var chosen = dll ?? zip;
            if (chosen == null)
                continue;

            bestVersion = version;
            bestUrl = (string?)chosen["browser_download_url"];
            bestIsZip = dll == null;
        }

        return (bestVersion, bestUrl, bestIsZip);
    }

    static async Task DownloadAsync(string url, string destination, bool isZip)
    {
        using var client = CreateClient();
        var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);

        TryDelete(destination);

        if (!isZip)
        {
            File.WriteAllBytes(destination, bytes);
            return;
        }

        // Unpacked to the same staged path a raw DLL would have been written to, so everything
        // downstream - the assembly-identity check, the rename dance - is identical either way.
        // Whatever comes out still has to pass IsValidUpgrade before it can take the live name, so a
        // zip containing the wrong thing is caught exactly like a truncated download is.
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

    // Confirms the download really is a newer build of *this* plugin before it is allowed to replace
    // anything.
    //
    // GetAssemblyName reads the file's metadata and closes it again without adding the assembly to
    // this domain, which is the point: whether the file can be trusted is not a question to answer by
    // first running it. It also leaves no handle behind, so the move below is free to rename it.
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
            // Not a managed assembly at all - a truncated download, or an error page.
            return false;
        }
    }

    // Renames the running DLL aside and moves the new one into its place. Both are permitted on a file
    // Windows currently has loaded; deleting or overwriting one is not, which is why the update is
    // done this way round rather than simply writing over the top.
    static void Install(string installedPath, string staged)
    {
        var backup = installedPath + ".backup";
        TryDelete(backup);

        // If this throws, nothing has moved yet and the folder is still exactly as it was.
        File.Move(installedPath, backup);

        try
        {
            File.Move(staged, installedPath);
        }
        catch
        {
            // Put the working version back rather than leaving the plugin folder with no DLL at all,
            // which would stop the plugin loading entirely next time.
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
            // Housekeeping only, and the next start will try again.
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
            // Still locked, or not ours to remove. Left where it is.
        }
    }

    static HttpClient CreateClient()
    {
        try
        {
            // .NET Framework 4.7.2 picks its protocol from this rather than from the OS default, and
            // GitHub refuses anything below TLS 1.2.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
            // Best-effort - an older Windows build may not define Tls12 in this enum at all.
        }

        var client = new HttpClient { Timeout = RequestTimeout };
        // GitHub rejects API requests that don't identify themselves.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AssemblyIdentity, "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
