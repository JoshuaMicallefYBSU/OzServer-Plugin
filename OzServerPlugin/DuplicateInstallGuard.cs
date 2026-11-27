using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Deals with the plugin being installed more than once - typically a manual install left in place
// when the vatSys Launcher installs it again into Plugins\OzServer-Plugin.
//
// vatSys (vatsys.Plugin.Plugins) lists every *.dll under <dataset>\Plugins and then
// <program folder>\bin\Plugins, subfolders included, Assembly.LoadFroms each and skips any whose
// FullName is already loaded. OzServerPlugin is not strong-named, so .NET Framework binds a second
// LoadFrom of it by simple name alone and hands back the copy already loaded, whatever its version:
// only the first copy listed ever runs, and the rest are dropped without a word. That copy may be
// the older one, and updates applied to the other (by the launcher) would never take effect.
//
// So the copy that runs looks for the others on disk and names them in vatSys's Errors window
// (ReportOtherCopiesAsync). Should two copies ever be loaded side by side after all - a vatSys that
// loads plugins differently - the first one constructed records itself in the AppDomain and every
// later one stays inert (TryClaim). AppDomain data rather than a static, because each copy would
// be a separate assembly with statics of its own; the AppDomain is also the one lock both share.
static class DuplicateInstallGuard
{
    const string RunningCopyKey = "OzServerPlugin.RunningCopy";
    const string AssemblyIdentity = "OzServerPlugin";

    // True for the copy that should run. Called at the top of Plugin's constructor.
    public static bool TryClaim()
    {
        var self = Assembly.GetExecutingAssembly();
        var here = Describe(self.GetName().Version, LaunchHandover.InstalledPath);
        string? running;

        lock (AppDomain.CurrentDomain)
        {
            running = AppDomain.CurrentDomain.GetData(RunningCopyKey) as string;
            // A build the claimant itself started, to run in its place (LaunchHandover), is not a
            // second copy: the claim passes to it.
            if (running == null || LaunchHandover.Arrived)
            {
                AppDomain.CurrentDomain.SetData(RunningCopyKey, here);
                running = null;
            }
        }

        if (running == null)
            return true;

        // Errors.Add directly rather than OzErrors: nothing else in this copy is initialised.
        Report("Installed more than once. Running " + running + "; the copy " + here
               + " has switched itself off. " + Advice);
        return false;
    }

    const string Advice = "Delete the copy you don't want and restart vatSys. The vatSys Launcher installs OzServer "
                          + @"into Plugins\OzServer-Plugin.";

    // Names the other installed copies, which vatSys ignores. Called by the copy that goes on to
    // run the session. Off the load path: it reads every DLL's metadata under both plugin folders.
    public static void ReportOtherCopies()
    {
        var runningPath = LaunchHandover.InstalledPath;
        _ = Task.Run(() => ReportOtherCopies(runningPath));
    }

    static void ReportOtherCopies(string runningPath)
    {
        try
        {
            if (string.IsNullOrEmpty(runningPath))
                return;

            var others = PluginRoots(runningPath)
                .Where(Directory.Exists)
                .SelectMany(root => SafeListDlls(root))
                .Where(path => !SamePath(path, runningPath))
                .Select(path => (Path: path, Version: AssemblyVersionIfOurs(path)))
                .Where(copy => copy.Version != null)
                .ToList();

            if (others.Count == 0)
                return;

            var running = Describe(Assembly.GetExecutingAssembly().GetName().Version, runningPath);
            var ignored = string.Join("; ", others.Select(o => Describe(o.Version, o.Path)));

            ActionLog.Log("Plugin", $"Other installed copies found and ignored by vatSys: {ignored}", new
            {
                action = "duplicate_install",
                running = runningPath,
                ignored = others.Select(o => o.Path).ToArray()
            });

            Report("Installed more than once. vatSys is running " + running
                   + ", and ignores " + ignored + ". " + Advice);
        }
        catch (Exception ex)
        {
            // Advisory only; never let it disturb the session.
            ActionLog.Log("Plugin", $"Couldn't check for other installed copies: {ex.Message}");
        }
    }

    // The folders vatSys loads plugins from, as vatsys.Plugin.Plugins builds them. The dataset path
    // comes from vatSys's internal Settings; if that can't be read, the Plugins folder this copy was
    // loaded from stands in for it.
    static IEnumerable<string> PluginRoots(string runningPath)
    {
        var roots = new List<string>();

        try
        {
            var settings = typeof(Errors).Assembly.GetType("vatsys.Properties.Settings");
            var instance = settings?.GetProperty("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (instance?.GetType().GetProperty("DatasetPath")?.GetValue(instance) is string dataset && dataset.Length > 0)
                roots.Add(dataset + @"\Plugins");
        }
        catch
        {
            // Fall through to the running copy's own folder.
        }

        try
        {
            roots.Add(Helpers.GetProgramFolder() + @"\bin\Plugins");
        }
        catch
        {
        }

        for (var dir = Path.GetDirectoryName(runningPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (string.Equals(Path.GetFileName(dir), "Plugins", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(dir!);
                break;
            }
        }

        return roots.Select(r => Path.GetFullPath(r).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static IEnumerable<string> SafeListDlls(string root)
    {
        try
        {
            return Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // The version of the file if it is a build of this plugin, whatever the file is called, else
    // null. GetAssemblyName reads metadata without loading the assembly.
    static Version? AssemblyVersionIfOurs(string path)
    {
        try
        {
            var name = AssemblyName.GetAssemblyName(path);
            return string.Equals(name.Name, AssemblyIdentity, StringComparison.OrdinalIgnoreCase)
                ? name.Version ?? new Version()
                : null;
        }
        catch
        {
            // Native DLLs, locked or unreadable files.
            return null;
        }
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // "v0.3.2 at C:\...\OzServerPlugin.dll"
    static string Describe(Version? version, string location) =>
        "v" + (version?.ToString(3) ?? "?") + " at "
        + (string.IsNullOrEmpty(location) ? "(not loaded from a file)" : location);

    static void Report(string message)
    {
        try
        {
            // vatSys prefixes the source ("OzServer: ..."). An OzServerNotice, so ErrorReporter
            // doesn't relay a setup problem on the controller's PC as a plugin failure.
            Errors.Add(new OzServerNotice(message), "OzServer");
        }
        catch
        {
            // Reporting is best effort.
        }
    }
}
