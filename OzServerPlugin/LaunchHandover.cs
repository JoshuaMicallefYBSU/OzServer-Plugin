using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace OzServerPlugin;

// Applies an update in the vatSys launch that finds it, so a controller who opens vatSys after a
// release is on that release at once, with no restart and nothing to click.
//
// vatSys loads OzServerPlugin.dll once, as it starts, and holds it for the session (see
// PluginUpdater). The build it loaded cannot be unloaded - but until that build's Plugin
// constructor has started anything, there is nothing of it to stop. So the constructor first asks
// whether a newer release exists (PluginUpdater.InstallNewerAtLaunch, which also puts it on disk
// for later starts). If one does, this build - "the outgoing build" below - loads it into the
// running vatSys, constructs its Plugin, and does nothing else for the session. The newer one -
// "the incoming build" - starts exactly as it would have had vatSys loaded it.
//
// Loading it takes a copy of the file and Assembly.LoadFile. vatSys's own Assembly.LoadFrom hands
// back the build already loaded for any file with the same assembly name, and LoadFile does the
// same for the path that build was loaded from, whatever file is there now (both tried against
// vatSys 0.4.9305). LoadFile on any other path loads that file as a second assembly. The copy's
// name does not end in .dll, so vatSys never lists it; PluginUpdater deletes it at a later start.
//
// vatSys still holds the outgoing build's Plugin object, which passes every call on to the
// incoming one (Plugin._successor). Once vatSys has finished loading plugins, the incoming build
// puts itself in that object's place in vatSys's own list (TakeOverRecord): vatSys then calls it
// directly, sees whichever plugin interfaces it implements rather than the outgoing build's, and
// reports its version.
//
// THE TWO BUILDS ARE DIFFERENT RELEASES. The outgoing side below runs as it was written in
// whatever release a controller has installed; the incoming side is always newer. What passes
// between them can therefore never change, and is kept small:
//
//   - the incoming build is found as the type "OzServerPlugin.Plugin", which has a public
//     constructor taking no arguments and implements vatsys.Plugin.IPlugin;
//   - while that constructor runs, the AppDomain data named HandoverKey holds an
//     IDictionary<string, object> with "installedPath" (where OzServerPlugin.dll is installed -
//     the incoming assembly's own Location is the copy), "outgoing" (the object vatSys holds) and
//     "outgoingVersion" (text);
//   - the outgoing build has by then taken DuplicateInstallGuard's claim, and started nothing.
//
// A later build may add entries to the dictionary. None may be removed or change meaning.
static class LaunchHandover
{
    const string HandoverKey = "OzServerPlugin.Handover";
    const string InstalledPathEntry = "installedPath";
    const string OutgoingEntry = "outgoing";
    const string OutgoingVersionEntry = "outgoingVersion";
    const string PluginTypeName = "OzServerPlugin.Plugin";
    const string AssemblyIdentity = "OzServerPlugin";

    // ---- the incoming build ----------------------------------------------------------------

    static string? _installedPath;
    static object? _outgoing;
    static string? _outgoingVersion;

    // True in a build that was started by an older one handing over to it.
    public static bool Arrived => _installedPath != null;

    // Where OzServerPlugin.dll is installed: the file to replace on an update, and the folder
    // other installed copies are looked for around. Not the running assembly's Location in an
    // incoming build, which was loaded from a copy.
    public static string InstalledPath => _installedPath ?? Assembly.GetExecutingAssembly().Location;

    // Reads what the outgoing build left for this one. Called first thing in Plugin's
    // constructor: the entry is only there while that constructor runs.
    public static void Receive()
    {
        try
        {
            if (AppDomain.CurrentDomain.GetData(HandoverKey) is not IDictionary<string, object> handover
                || !handover.TryGetValue(InstalledPathEntry, out var path)
                || path is not string installedPath
                || installedPath.Length == 0)
                return;

            handover.TryGetValue(OutgoingEntry, out _outgoing);
            _outgoingVersion = handover.TryGetValue(OutgoingVersionEntry, out var version) ? version as string : null;
            // Last: Arrived turns true only with everything else read.
            _installedPath = installedPath;
        }
        catch
        {
            // Not handed over, as far as this build can tell: it starts as the only one.
        }
    }

    // Called at the end of an incoming build's Plugin constructor, so only by one that started.
    // Nothing escapes it: the constructor it is the last line of has succeeded, and an exception
    // from here would have the outgoing build treat it as one that failed.
    public static void Complete(IPlugin incoming)
    {
        if (!Arrived)
            return;

        try
        {
            Announce(incoming);
        }
        catch (Exception ex)
        {
            ActionLog.Log("Update", $"Couldn't finish taking over from the outgoing build: {ex.Message}");
        }
    }

    static void Announce(IPlugin incoming)
    {
        var from = Version.TryParse(_outgoingVersion, out var outgoingVersion)
            ? PluginUpdater.Display(outgoingVersion)
            : "an earlier version";

        // Told once vatSys's main window is up: the update itself needed nothing from them, but a
        // plugin that changes under a controller should say so.
        _ = PluginUpdater.AnnounceAppliedAsync(outgoingVersion);
        ActionLog.Log("Update", $"Updated from {from} to {PluginUpdater.RunningVersionDisplay} as vatSys started - no restart needed.", new
        {
            action = "launch_handover",
            from,
            to = PluginUpdater.RunningVersionDisplay
        });

        if (_outgoing is not { } outgoing)
            return;

        // vatSys is still inside the outgoing build's constructor, and builds its list of plugins
        // only once every plugin's constructor has returned. Application.Idle is first raised
        // after the main window has loaded, well past that. The constructor runs on vatSys's UI
        // thread, which is the thread Idle is raised on.
        var tries = 0;
        EventHandler? onIdle = null;
        onIdle = (_, _) =>
        {
            // Raised on vatSys's UI thread between messages: nothing may escape it.
            try
            {
                if (!TakeOverRecord(outgoing, incoming) && ++tries < 50)
                    return;

                Application.Idle -= onIdle;
            }
            catch (Exception ex)
            {
                Application.Idle -= onIdle;
                // The outgoing build goes on passing every call along, so nothing is lost but the
                // version vatSys reports.
                ActionLog.Log("Update", $"Couldn't take the outgoing build's place in vatSys's plugin list: {ex.GetBaseException().Message}",
                    new { action = "launch_handover_record_kept" });
            }
        };
        Application.Idle += onIdle;
    }

    // Puts the incoming build where the outgoing one is in vatSys's own record of its plugins.
    // False while vatSys has not built that record yet.
    //
    // vatsys.Plugin.Plugins (internal) keeps one vatsys.Plugin.Plugin per plugin object in an
    // array, and the assembly names it reports to a client-information query in another. The
    // wrapper works out which plugin interfaces its object implements when it is constructed, so
    // a new wrapper is made round the incoming build rather than the old one being repointed.
    // vatSys reads the array on other threads; replacing an element is a single reference write.
    static bool TakeOverRecord(object outgoing, IPlugin incoming)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        var vatSys = typeof(IPlugin).Assembly;
        var registryType = vatSys.GetType("vatsys.Plugin.Plugins", throwOnError: true)!;
        var recordType = vatSys.GetType("vatsys.Plugin.Plugin", throwOnError: true)!;

        var registry = Field(registryType, f => f.IsStatic && f.FieldType == registryType).GetValue(null);
        if (registry == null)
            return false;

        if (Field(registryType, f => !f.IsStatic && f.FieldType == recordType.MakeArrayType()).GetValue(registry) is not Array records)
            return false;

        var held = Field(recordType, f => !f.IsStatic && f.FieldType == typeof(IPlugin));
        var replaced = 0;
        for (var i = 0; i < records.Length; i++)
        {
            if (records.GetValue(i) is not { } record || !ReferenceEquals(held.GetValue(record), outgoing))
                continue;

            records.SetValue(Activator.CreateInstance(recordType, incoming), i);
            replaced++;
        }

        if (replaced == 0)
            throw new InvalidOperationException("vatSys holds no record of the build it loaded");

        var outgoingName = outgoing.GetType().Assembly.GetName();
        var incomingName = incoming.GetType().Assembly.GetName();
        if (Field(registryType, f => !f.IsStatic && f.FieldType == typeof(AssemblyName[])).GetValue(registry) is AssemblyName[] names)
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (names[i]?.Name == outgoingName.Name && Equals(names[i].Version, outgoingName.Version))
                    names[i] = incomingName;
            }
        }

        ActionLog.Log("Update", $"vatSys now calls {PluginUpdater.RunningVersionDisplay} directly.", new { action = "launch_handover_record_taken" });
        return true;

        static FieldInfo Field(Type type, Func<FieldInfo, bool> match) =>
            type.GetFields(Any).SingleOrDefault(match)
            ?? throw new MissingFieldException($"This vatSys keeps {type.Name} differently");
    }

    // ---- the outgoing build ----------------------------------------------------------------

    // True when this build must start nothing: a newer one is running in its place (successor),
    // or began to start and failed (successor is null). False when this build is the one to run.
    //
    // Called from Plugin's constructor, on vatSys's UI thread, while vatSys is still behind its
    // splash screen. Nothing escapes it: an exception leaving that constructor stops vatSys
    // loading any plugin.
    public static bool TryHandOver(object outgoing, out IPlugin? successor)
    {
        successor = null;

        Version? installed;
        Type pluginType;
        string installedPath;
        var self = Assembly.GetExecutingAssembly();
        var current = self.GetName().Version;

        try
        {
            // An incoming build never hands over again, and an assembly that was not loaded from
            // a file on disk has nothing to replace.
            installedPath = self.Location;
            if (Arrived || string.IsNullOrEmpty(installedPath) || !File.Exists(installedPath))
                return false;

            installed = PluginUpdater.InstallNewerAtLaunch(installedPath, current);
            if (installed == null)
                return false;
        }
        catch (Exception ex)
        {
            // Offline, a download that would not verify, a folder that cannot be written to. This
            // build runs, and PluginUpdater's own check, moments from now, reports what needs
            // reporting.
            ActionLog.Log("Update", $"Couldn't update while starting: {ex.GetBaseException().Message}");
            return false;
        }

        // The new release is on disk from here on, whatever happens next.
        try
        {
            var next = Assembly.LoadFile(CopyToRunFrom(installedPath, installed));
            var name = next.GetName();
            if (ReferenceEquals(next, self) || name.Name != AssemblyIdentity || name.Version == null || name.Version.CompareTo(current) <= 0)
                throw new InvalidOperationException($"loading it gave back {name.Name} {name.Version}");

            pluginType = next.GetType(PluginTypeName, throwOnError: true)!;
            if (!typeof(IPlugin).IsAssignableFrom(pluginType))
                throw new InvalidOperationException($"its {PluginTypeName} is not a vatSys plugin");
        }
        catch (Exception ex)
        {
            // Nothing of the new build has run. This one does, and its updater tells the
            // controller a restart will apply what is on disk, as after any other install.
            PluginUpdater.InstalledAtLaunch = installed;
            ActionLog.Log("Update", $"Installed {installed} but couldn't switch to it without a restart: {ex.GetBaseException().Message}");
            return false;
        }

        try
        {
            AppDomain.CurrentDomain.SetData(HandoverKey, new Dictionary<string, object>
            {
                [InstalledPathEntry] = installedPath,
                [OutgoingEntry] = outgoing,
                [OutgoingVersionEntry] = current?.ToString() ?? ""
            });

            successor = (IPlugin)Activator.CreateInstance(pluginType);
            return true;
        }
        catch (Exception ex)
        {
            // The new build's constructor started and threw. However far it got - timers, event
            // handlers, menu entries - cannot be taken back, so this build must not start beside
            // it. The session runs without OzServer, and says so.
            ReportFailedStart(installed, ex.GetBaseException());
            return true;
        }
        finally
        {
            AppDomain.CurrentDomain.SetData(HandoverKey, null);
        }
    }

    // The file the incoming build is loaded from: a copy of the installed DLL, named for its
    // version so that a second vatSys, started while this one runs from an earlier copy, does not
    // need the same name.
    static string CopyToRunFrom(string installedPath, Version version)
    {
        var copy = $"{installedPath}.{version}.live";
        try
        {
            File.Copy(installedPath, copy, overwrite: true);
        }
        catch (Exception) when (SameContent(installedPath, copy))
        {
            // Another vatSys is already running from this copy, which is why it cannot be
            // written. It is the same file, and can be loaded here as well.
        }

        return copy;
    }

    // The files PluginUpdater clears away at a later start, for a DLL of this file name.
    public static string CopyPattern(string installedFileName) => installedFileName + ".*.live";

    static bool SameContent(string a, string b)
    {
        try
        {
            return File.Exists(b) && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }

    static void ReportFailedStart(Version installed, Exception error)
    {
        try
        {
            ActionLog.Log("Update", $"{installed} was installed as vatSys started but failed to start: {error}");
            // Errors.Add directly rather than OzErrors: nothing else in this build is initialised.
            // vatSys prefixes the source ("OzServer: ...").
            Errors.Add(new Exception(
                $"Version {installed} was installed as vatSys started, but could not start ({error.Message}). "
                + "OzServer is not running in this session - restart vatSys. If it fails again, close vatSys, "
                + "delete OzServerPlugin.dll and rename OzServerPlugin.dll.backup back.", error), "OzServer");
        }
        catch
        {
            // Reporting is best effort.
        }
    }
}
