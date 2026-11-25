using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OzServerPlugin;

// Entry point. Kept free of every vatSys type: vatSys.exe and its DLLs are not beside this
// program, and are found by the resolver installed here before anything that names them is
// compiled (hence NoInlining on Shots.Run).
static class EntryPoint
{
    [STAThread]
    static int Main(string[] args)
    {
        var bin = Path.Combine(VatSysFolder(), "bin");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name;
            foreach (var ext in new[] { ".dll", ".exe" })
            {
                var path = Path.Combine(bin, name + ext);
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            return null;
        };

        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "shots");
        Directory.CreateDirectory(output);

        try
        {
            return Shots.Run(output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static string VatSysFolder()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Sawbe\vatSys");
        return key?.GetValue("Path") as string ?? @"C:\Program Files (x86)\vatSys";
    }
}

// Sets vatSys up far enough for the plugin's windows to draw - the profile's colours and sectors,
// a connection that looks made - then opens each window with example data and saves it.
//
// Nothing here connects anywhere. The "connection" is a socket to this program itself, the
// controllers are invented, and every API call fails at once (Stubs.cs).
static class Shots
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    const string MyCallsign = "ML-BLA_CTR";
    const int MyCid = 1000001;

    static string _output = "";
    static Form _host = null!;
    // Kept alive for the life of the program - see FakeConnection.
    static readonly List<object> KeepAlive = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string output)
    {
        _output = output;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Console.Error.WriteLine("UI thread: " + e.Exception);

        LoadProfile();
        FakeConnection();

        _host = new Form
        {
            // The plugin finds vatSys's main window by this name, to post work to its thread and
            // to own the windows it opens.
            Name = "MainForm",
            Text = "WindowShots",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
            ClientSize = new Size(1200, 760),
            BackColor = vatsys.Colours.GetColour(vatsys.Colours.Identities.ASDBackground),
        };
        _host.Shown += async (_, _) =>
        {
            try
            {
                await Scenarios.RunAll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Scenarios: " + ex);
            }

            lock (ActionLog.Lines)
                File.WriteAllLines(Path.Combine(_output, "log.txt"), ActionLog.Lines);
            Environment.Exit(0);
        };
        Application.Run(_host);
        return 0;
    }

    public static Form Host => _host;

    // ---- vatSys state -------------------------------------------------------------------------

    static void LoadProfile()
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vatSys Files", "Profiles", "Australia");

        // In memory only: these are this program's own settings, and they are never saved.
        var settings = typeof(vatsys.MMI).Assembly.GetType("vatsys.Properties.Settings", true)!;
        var instance = settings.GetProperty("Default", Any)!.GetValue(null);
        settings.GetProperty("DatasetPath", Any)!.SetValue(instance, profile);

        Step("colours", () => Static(typeof(vatsys.Colours), "LoadColours"));
        Step("sectors", () => Static(typeof(vatsys.SectorsVolumes), "LoadSectorsAndVolumes"));
        Step("positions", () => Static(typeof(vatsys.LogicalPositions), "Load"));
        Console.WriteLine($"Sectors loaded: {vatsys.SectorsVolumes.Sectors.Count}");
    }

    static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Loading {what}: {(ex as TargetInvocationException)?.InnerException ?? ex}");
        }
    }

    // A Network that reads as connected to live VATSIM as ML-BLA_CTR. Built without its
    // constructor, which starts timers and loads the server list.
    static void FakeConnection()
    {
        var type = typeof(vatsys.Network);
        var network = FormatterServices.GetUninitializedObject(type);

        Set(type, network, "onlineATC", new List<vatsys.NetworkATC>());
        Set(type, network, "onlinePilots", Activator.CreateInstance(type.GetField("onlinePilots", Any)!.FieldType));
        Set(type, network, "officialServer", true);
        var atis = type.GetField("Atis", Any)!;
        atis.SetValue(network, Array.CreateInstance(atis.FieldType.GetElementType()!, 4));

        Set(type, null, "callsign", MyCallsign);
        Set(type, null, "realName", "Example Controller");
        Set(type, null, "controllerID", MyCid.ToString(CultureInfo.InvariantCulture));
        Set(type, null, "validATC", true);
        Set(type, null, "serverIp", "fsd.connect.vatsim.net");
        SetEnum(type, "rating", "C1");
        SetEnum(type, "facility", "CTR");

        // "Connected" is the session's socket being connected, so it gets one: to a listener in
        // this process, which nothing is ever written to.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndpoint);
        var server = listener.AcceptSocket();
        KeepAlive.Add(listener);
        KeepAlive.Add(client);
        KeepAlive.Add(server);

        var sessionField = type.GetField("VATSIM", Any)!;
        var session = FormatterServices.GetUninitializedObject(sessionField.FieldType);
        var socketField = sessionField.FieldType.GetField("mClientSocket", Any)!;
        socketField.SetValue(session, client);
        sessionField.SetValue(network, session);

        var instance = (MemberInfo?)type.GetField("Instance", Any) ?? type.GetProperty("Instance", Any);
        if (instance is FieldInfo field)
            field.SetValue(null, network);
        else
            ((PropertyInfo)instance!).SetValue(null, network);

        Console.WriteLine($"Network.IsConnected={vatsys.Network.IsConnected} server={NetworkServer.Current} me={vatsys.Network.Me?.Callsign}");
    }

    public static void SetOnline(IEnumerable<(string Callsign, string Name)> controllers)
    {
        var list = controllers.Select(c =>
        {
            var atc = new vatsys.NetworkATC { Callsign = c.Callsign, RealName = c.Name, ValidATC = true };
            SetEnumOn(atc, "Rating", "C1");
            SetEnumOn(atc, "Facility", c.Callsign.EndsWith("_APP", StringComparison.Ordinal) ? "APP" : "CTR");
            return atc;
        }).ToList();
        Set(typeof(vatsys.Network), Get(typeof(vatsys.Network), null, "Instance"), "onlineATC", list);
    }

    // ---- reflection ---------------------------------------------------------------------------

    public static object? Get(Type type, object? target, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.GetField(name, Any | BindingFlags.DeclaredOnly) is { } field)
                return field.GetValue(target);
            if (t.GetProperty(name, Any | BindingFlags.DeclaredOnly) is { } property)
                return property.GetValue(target);
        }
        throw new MissingMemberException(type.FullName, name);
    }

    public static void Set(Type type, object? target, string name, object? value)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.GetField(name, Any | BindingFlags.DeclaredOnly) is { } field)
            {
                field.SetValue(target, value);
                return;
            }
            if (t.GetProperty(name, Any | BindingFlags.DeclaredOnly) is { } property)
            {
                if (property.SetMethod != null)
                    property.SetValue(target, value);
                else
                    t.GetField($"<{name}>k__BackingField", Any)!.SetValue(target, value);
                return;
            }
        }
        throw new MissingMemberException(type.FullName, name);
    }

    static void SetEnum(Type type, string field, string name)
    {
        var info = type.GetField(field, Any)!;
        info.SetValue(null, Enum.Parse(info.FieldType, name));
    }

    static void SetEnumOn(object target, string field, string name)
    {
        var info = target.GetType().GetField(field, Any)!;
        info.SetValue(target, Enum.Parse(info.FieldType, name));
    }

    public static object? Static(Type type, string method, params object?[] args) => Call(type, null, method, args);

    public static object? Call(Type type, object? target, string method, params object?[] args)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var found = t.GetMethods(Any | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length);
            if (found != null)
                return found.Invoke(target, args);
        }
        throw new MissingMethodException(type.FullName, method);
    }

    // ---- capture ------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    // Square corners, so nothing of what is behind the window shows in the picture.
    public static void SquareCorners(Form form)
    {
        var doNotRound = 1;
        DwmSetWindowAttribute(form.Handle, 33, ref doNotRound, sizeof(int));
    }

    // The window as the desktop draws it, without its shadow, whatever is in front of it.
    public static void Save(Form form, string name)
    {
        form.Refresh();
        Application.DoEvents();

        GetWindowRect(form.Handle, out var window);
        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try
            {
                PrintWindow(form.Handle, hdc, 2);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var crop = new Rectangle(0, 0, width, height);
        if (DwmGetWindowAttribute(form.Handle, 9, out var frame, Marshal.SizeOf<RECT>()) == 0)
        {
            var visible = Rectangle.FromLTRB(frame.Left - window.Left, frame.Top - window.Top, frame.Right - window.Left, frame.Bottom - window.Top);
            visible.Intersect(crop);
            if (visible.Width > 0 && visible.Height > 0)
                crop = visible;
        }

        var path = Path.Combine(_output, name + ".png");
        using var cropped = full.Clone(crop, PixelFormat.Format32bppArgb);
        cropped.Save(path, ImageFormat.Png);
        Console.WriteLine($"{name}.png  {crop.Width}x{crop.Height}  \"{form.Text}\"");
    }
}
