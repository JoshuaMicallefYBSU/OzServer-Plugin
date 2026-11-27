using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Runs vatSys's own plugin loader (vatsys.Plugin.Plugins.Load) over a scratch dataset folder
// holding a test build of the plugin, with a local server playing OzServer and the release
// downloads, and reports which build ended up running and what is left on disk. See Run.ps1.
//
//   HandoverTest <scenario> <work folder> [--keep]
//
// <work folder>\builds\<version>\OzServerPlugin.dll are the test builds (Build\TestBuild.csproj).
// --keep starts from the plugin folder the "update" scenario left, as the next vatSys start would.
static class Program
{
    const int Port = 47811;
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    // Far above any real release, so GitHub - asked when the local server is switched off - never
    // has anything newer.
    const string Installed = "9.9.8";
    const string Newer = "9.9.9";
    const string Newest = "9.9.10";

    static string _vatSysBin;
    static string _work;
    static string _scenario;
    static readonly List<string> Requests = new List<string>();
    static int _releaseRequests;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("HandoverTest <scenario> <work folder> [--keep]   (run it through Run.ps1)");
            return 2;
        }

        _scenario = args[0];
        _work = args[1];
        var keep = args.Contains("--keep");

        _vatSysBin = Path.Combine((string)Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Sawbe\vatSys", "Path", null) ?? @"C:\Program Files (x86)\vatSys", "bin");

        var run = Path.Combine(_work, "runs", keep ? "update" : _scenario);
        var dataset = Path.Combine(run, "dataset");
        var pluginDir = Path.Combine(dataset, @"Plugins\OzServer-Plugin");
        var log = Path.Combine(run, $"log-{_scenario}.txt");

        if (!keep)
        {
            if (Directory.Exists(run)) Directory.Delete(run, true);
            Directory.CreateDirectory(pluginDir);
            File.Copy(Build(Installed), Path.Combine(pluginDir, "OzServerPlugin.dll"));
        }
        if (File.Exists(log)) File.Delete(log);

        // Read by the test builds' log (Build\Stubs.cs).
        Environment.SetEnvironmentVariable("OZ_TEST_LOG", log);
        if (_scenario == "incoming-throws")
            Environment.SetEnvironmentVariable("OZ_TEST_FAIL_VERSION", Newer + ".0");

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        if (_scenario != "offline")
            StartServer();

        Console.WriteLine($"=== {_scenario}{(keep ? " (the next start, same folder)" : "")} ===");
        Console.WriteLine("before: " + Describe(pluginDir));

        var vatSys = Assembly.LoadFrom(Path.Combine(_vatSysBin, "vatSys.exe"));

        // In memory only - never saved, and this host's user.config is not vatSys's anyway.
        var settings = vatSys.GetType("vatsys.Properties.Settings").GetProperty("Default", Any).GetValue(null);
        settings.GetType().GetProperty("DatasetPath").SetValue(settings, dataset);

        // The vatSys singletons the plugin subscribes to as it starts, which MainForm has made by
        // the time it loads plugins. Empty shells: nothing here connects to anything.
        foreach (var name in new[] { "vatsys.Network", "vatsys.Audio" })
            MakeShell(vatSys.GetType(name, true));

        var registry = vatSys.GetType("vatsys.Plugin.Plugins");
        var clock = Stopwatch.StartNew();
        try
        {
            registry.GetMethod("Load", Any).Invoke(null, null);
        }
        catch (Exception ex)
        {
            // What an exception leaving a plugin's constructor does to vatSys.
            Console.WriteLine("!! vatSys's plugin loader threw: " + ex.GetBaseException().Message);
            foreach (Exception e in (IEnumerable)vatSys.GetType("vatsys.Errors").GetProperty("Current").GetValue(null))
                Console.WriteLine("   vatSys error: " + Innermost(e));
            Finish();
        }
        Console.WriteLine($"vatSys's Plugins.Load() took {clock.ElapsedMilliseconds} ms");

        Report("as vatSys finishes loading plugins", vatSys, registry);

        // A second of message loop on this thread, as vatSys's main window gives it: what the
        // plugin left on Application.Idle runs, the incoming build taking over vatSys's record
        // among it.
        var pump = new System.Windows.Forms.Timer { Interval = 1000 };
        pump.Tick += (_, __) => Application.ExitThread();
        pump.Start();
        Application.Run();
        Report("after vatSys's message loop has been idle", vatSys, registry);

        // Long enough for PluginUpdater's own check, three seconds after the plugin starts, and
        // for the downloads the slow scenarios hold back.
        var settle = _scenario.StartsWith("slow") ? 16000 : 6000;
        Thread.Sleep(settle);

        Console.WriteLine($"\n{settle / 1000} s later: " + Describe(pluginDir));

        Console.WriteLine("\nvatSys Errors window:");
        var errors = ((IEnumerable)vatSys.GetType("vatsys.Errors").GetProperty("Current").GetValue(null)).Cast<Exception>().ToList();
        foreach (var e in errors) Console.WriteLine($"  {e.Source}: {e.Message}");
        if (errors.Count == 0) Console.WriteLine("  (empty)");

        Console.WriteLine("\nrequests the local server saw:");
        lock (Requests)
        {
            foreach (var group in Requests.GroupBy(r => r)) Console.WriteLine($"  {group.Count()}x {group.Key}");
            if (Requests.Count == 0) Console.WriteLine("  (none)");
        }

        Console.WriteLine("\nplugin log (Update lines and failures):");
        var lines = File.Exists(log) ? File.ReadAllLines(log) : new string[0];
        foreach (var line in lines.Where(l => l.Contains("] Update:") || l.Contains("failed") || l.Contains("ouldn't")))
            Console.WriteLine("  " + (line.Length > 300 ? line.Substring(0, 300) + "..." : line));
        Console.WriteLine($"  ({lines.Length} lines in all, in {log})");

        Finish();
        return 0;
    }

    // Exit rather than return: the plugin's timers and threads would keep the process alive.
    static void Finish()
    {
        Console.Out.Flush();
        Environment.Exit(0);
    }

    static void MakeShell(Type type)
    {
        var shell = FormatterServices.GetUninitializedObject(type);

        // Its lists and tables empty rather than missing, and its locks there to take.
        foreach (var field in type.GetFields(Any).Where(f => !f.IsStatic && f.GetValue(shell) == null))
        {
            var fieldType = field.FieldType;
            if (fieldType == typeof(object))
                field.SetValue(shell, new object());
            else if (fieldType.IsArray)
                field.SetValue(shell, Array.CreateInstance(fieldType.GetElementType(), 0));
            else if (fieldType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(fieldType)
                     && !fieldType.IsAbstract && fieldType.GetConstructor(Type.EmptyTypes) != null)
                field.SetValue(shell, Activator.CreateInstance(fieldType));
        }

        var slot = type.GetFields(Any).FirstOrDefault(f => f.IsStatic && f.FieldType == type);
        if (slot != null)
            slot.SetValue(null, shell);
        else
            type.GetProperties(Any).First(p => p.PropertyType == type).SetValue(null, shell);
    }

    static object Held(object record) => record.GetType().GetField("plugin", Any).GetValue(record);

    static void Report(string title, Assembly vatSys, Type registry)
    {
        var records = ((IEnumerable)registry.GetProperty("LoadedPlugins", Any).GetValue(null))?.Cast<object>().ToList() ?? new List<object>();

        Console.WriteLine($"\n--- {title} ---");
        foreach (var record in records)
        {
            var held = Held(record);
            var successor = held.GetType().GetField("_successor", Any)?.GetValue(held);
            Console.WriteLine($"vatSys holds: \"{record.GetType().GetProperty("Name").GetValue(record)}\", the object from {Describe(held)}"
                              + (successor == null ? "" : $"\n              which passes its calls on to {Describe(successor)}"));
        }
        if (records.Count == 0) Console.WriteLine("vatSys holds: (no plugins)");

        var dlls = (IEnumerable<AssemblyName>)registry.GetProperty("LoadedPluginDlls", Any).GetValue(null);
        Console.WriteLine("vatSys reports to the network: " + (dlls == null ? "(none)" : string.Join(",", dlls.Select(p => $"{p.Name}/{p.Version}"))));

        Console.WriteLine("builds loaded in the process: " + string.Join(", ", AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "OzServerPlugin")
            .Select(a => $"v{a.GetName().Version} from {Path.GetFileName(a.Location)}")));

        var texts = new List<string>();
        if (vatSys.GetType("vatsys.MMI").GetField("PluginMenuItems", Any).GetValue(null) is IEnumerable items)
            foreach (var item in items)
            {
                var menuItem = item.GetType().GetProperty("Item", Any)?.GetValue(item) ?? item.GetType().GetField("Item", Any)?.GetValue(item);
                texts.Add(((ToolStripItem)menuItem).Text);
            }
        Console.WriteLine("menu entries registered with vatSys: " + (texts.Count == 0 ? "(none)" : string.Join(" | ", texts)));
    }

    // "v9.9.9.0 (running the session)". A Plugin object that got past the handover and started
    // the plugin's work has constructed its updater and its sector tracker; one that handed over,
    // or stayed out of the way, has neither.
    static string Describe(object plugin)
    {
        var type = plugin.GetType();
        var started = type.GetField("_updater", Any)?.GetValue(plugin) != null
                      && type.GetField("_ownershipTracker", Any)?.GetValue(plugin) != null;
        return $"v{type.Assembly.GetName().Version} ({(started ? "running the session" : "started nothing")})";
    }

    static string Describe(string dir)
    {
        var parts = new List<string>();
        foreach (var file in Directory.GetFiles(dir).OrderBy(f => f))
        {
            string version;
            try { version = "v" + AssemblyName.GetAssemblyName(file).Version; } catch { version = "?"; }
            string state;
            try { using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) state = "free"; } catch { state = "IN USE"; }
            parts.Add($"{Path.GetFileName(file)} [{version}, {state}]");
        }
        return string.Join("  ", parts);
    }

    // The exception that left the plugin's constructor, under MEF's layers.
    static string Innermost(Exception e)
    {
        for (var depth = 0; depth < 12; depth++)
        {
            var parts = e.GetType().GetProperty("Errors")?.GetValue(e) as IEnumerable;
            var next = parts?.Cast<object>().Select(x => x.GetType().GetProperty("Exception")?.GetValue(x) as Exception).FirstOrDefault(x => x != null)
                       ?? e.InnerException;
            if (next == null) break;
            e = next;
        }
        var text = e.ToString();
        return text.Length > 3000 ? text.Substring(0, 3000) : text;
    }

    static string Build(string version) => Path.Combine(_work, "builds", version, "OzServerPlugin.dll");

    // vatSys's own references, which it finds beside its exe and through the redirects in its
    // config file. This host has neither.
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        var name = new AssemblyName(e.Name).Name;
        if (name == "OzServerPlugin")
            return null;

        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null)
            return loaded;

        foreach (var extension in new[] { ".dll", ".exe" })
        {
            var path = Path.Combine(_vatSysBin, name + extension);
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        return null;
    }

    // ---- the local server -------------------------------------------------------------------
    //
    // A socket rather than HttpListener, which needs a URL reservation to listen without
    // elevation. One request per connection.

    static void StartServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        new Thread(() =>
        {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => { try { Serve(client); } catch { } finally { client.Close(); } });
            }
        }) { IsBackground = true }.Start();
    }

    static void Serve(TcpClient client)
    {
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var requestLine = reader.ReadLine();
        if (requestLine == null) return;

        var contentLength = 0;
        string header;
        while (!string.IsNullOrEmpty(header = reader.ReadLine()))
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(header.Substring(15).Trim());
        var body = new char[contentLength];
        for (var read = 0; read < contentLength;)
        {
            var n = reader.Read(body, read, contentLength - read);
            if (n <= 0) break;
            read += n;
        }

        var parts = requestLine.Split(' ');
        var path = parts[1].Split('?')[0];
        lock (Requests) Requests.Add(parts[0] + " " + path);

        byte[] payload = null;
        var status = "404 Not Found";

        if (path == "/api/v1/plugin/release")
        {
            var nth = Interlocked.Increment(ref _releaseRequests);
            if (_scenario == "slow-check") Thread.Sleep(5000);

            // What OzServer says is current: nothing new, the newer build, or - from the second
            // time of asking, in one scenario - a newer one still.
            var offered = _scenario == "none" ? null
                : _scenario == "second-update" && nth > 1 ? Newest
                : Newer;

            var json = "null";
            if (offered != null)
            {
                string sha;
                using (var hash = SHA256.Create())
                    sha = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(Build(offered)))).Replace("-", "").ToLowerInvariant();
                if (_scenario == "bad-hash") sha = new string('0', 64);
                json = "{\"version\":\"" + offered + "\",\"url\":\"http://127.0.0.1:" + Port + "/releases/download/v" + offered
                       + "/OzServerPlugin.dll\",\"sha256\":\"" + sha + "\"}";
            }
            payload = Encoding.UTF8.GetBytes(json);
            status = "200 OK";
        }
        else if (path.StartsWith("/releases/download/v") && path.EndsWith("/OzServerPlugin.dll"))
        {
            if (_scenario == "slow-download") Thread.Sleep(12000);
            var version = path.Substring("/releases/download/v".Length).Split('/')[0];
            if (File.Exists(Build(version)))
            {
                payload = File.ReadAllBytes(Build(version));
                status = "200 OK";
            }
        }

        // Everything else the plugin asks OzServer for as it starts.
        payload = payload ?? Encoding.UTF8.GetBytes("{}");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }
}
