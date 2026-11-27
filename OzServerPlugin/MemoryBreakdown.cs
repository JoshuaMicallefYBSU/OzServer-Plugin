using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// What is behind the totals on the Perf memory line (UiStallWatchdog). On 26 Nov ML-HUO_CTR's
// vatSys went from 1.07 GB to 2.29 GB and from 627 to 2,291 GDI objects in 3.5 hours, and the
// totals alone could not say whether OzServer, vatSys or one of the other plugins in the process
// was doing it. These are the counts that can:
//
//  - every window in the process, by the window that owns it and by control type. Each is named
//    with the assembly that defines it, so a plugin that keeps creating controls shows by name. A
//    WinForms control holds a USER object for its handle and usually a font and a brush as well,
//    so leaked controls raise the GDI count with them;
//  - how much vatSys is holding - flight plans, radar tracks, maps - which separates a busy
//    evening from a leak;
//  - loaded assemblies and threads, each a classic slow leak of its own;
//  - the sizes of the plugin's own per-flight collections (Track).
//
// Everything here runs on the watchdog's timer thread and only reads. Nothing is posted to
// vatSys's UI thread: the window walk is the Win32 one, which needs no help from the thread that
// owns the windows.
public static class MemoryBreakdown
{
    // ---------------------------------------------------------------------------------------------
    // Windows
    // ---------------------------------------------------------------------------------------------

    public sealed class WindowCounts
    {
        public int Total;
        // Keyed by the top-level window: everything inside it, itself included.
        public readonly Dictionary<string, int> ByOwner = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> ByType = new(StringComparer.Ordinal);
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();

    static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE

    // A runaway guard, not a limit anyone should meet: Windows stops a process at 10,000 USER
    // objects.
    const int MaxTopLevel = 20000;

    public static WindowCounts Windows()
    {
        var counts = new WindowCounts();
        var mine = GetCurrentProcessId();

        var topLevel = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == mine)
                topLevel.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        // EnumWindows leaves out message-only windows, and WinForms parks every control that has a
        // handle but no parent under one of those - which is exactly where controls taken off a
        // form and never disposed end up.
        var seen = 0;
        for (var hwnd = FindWindowEx(MessageOnlyParent, IntPtr.Zero, null, null);
             hwnd != IntPtr.Zero && seen < MaxTopLevel;
             hwnd = FindWindowEx(MessageOnlyParent, hwnd, null, null), seen++)
        {
            if (GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == mine)
                topLevel.Add(hwnd);
        }

        foreach (var top in topLevel)
        {
            var owner = Describe(top);
            var inside = 1;
            Add(counts.ByType, owner, 1);

            // Every descendant, not only direct children.
            EnumChildWindows(top, (child, _) =>
            {
                inside++;
                Add(counts.ByType, Describe(child), 1);
                return true;
            }, IntPtr.Zero);

            Add(counts.ByOwner, owner, inside);
            counts.Total += inside;
        }

        return counts;
    }

    // "Assembly:Type" for a window WinForms knows, which is what names the plugin; the Win32 class
    // for anything else (Chromium's, DirectX's, the input method's).
    static string Describe(IntPtr hwnd)
    {
        try
        {
            // Both are lock-free table lookups, safe off the UI thread. NativeWindow.FromHandle
            // covers windows that are not Controls, such as vatSys's InfoPopupW.
            var type = Control.FromHandle(hwnd)?.GetType() ?? NativeWindow.FromHandle(hwnd)?.GetType();
            if (type != null)
                return Name(type);
        }
        catch (Exception)
        {
            // The window went away mid-walk; fall through to its class, or to "gone".
        }

        var name = new StringBuilder(128);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? $"native:{name}" : "native:gone";
    }

    // "Assembly:Type", which is what says whose a window is.
    public static string Name(Type type) => $"{type.Assembly.GetName().Name}:{type.Name}";

    public static void Add(Dictionary<string, int> into, string key, int count)
    {
        into.TryGetValue(key, out var current);
        into[key] = current + count;
    }

    // ---------------------------------------------------------------------------------------------
    // The plugin's own collections
    // ---------------------------------------------------------------------------------------------

    static readonly Dictionary<string, Func<int>> Tracked = new(StringComparer.Ordinal);

    // Registers a collection whose size goes on the memory line. For the ones that grow with the
    // flights seen in a session; the count is read without the owner's lock, which for a
    // Dictionary, HashSet or List is one field read and at worst a moment stale.
    public static void Track(string name, Func<int> count)
    {
        lock (Tracked)
            Tracked[name] = count;
    }

    public static Dictionary<string, int> TrackedSizes()
    {
        KeyValuePair<string, Func<int>>[] tracked;
        lock (Tracked)
            tracked = Tracked.ToArray();

        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in tracked)
        {
            try
            {
                sizes[entry.Key] = entry.Value();
            }
            catch (Exception)
            {
                sizes[entry.Key] = -1;
            }
        }
        return sizes;
    }

    // ---------------------------------------------------------------------------------------------
    // What vatSys holds, and the process and PC around it
    // ---------------------------------------------------------------------------------------------

    public sealed class ObjectCounts
    {
        public int FlightPlans = -1;
        public int RadarTracks = -1;
        public int Maps = -1;
        public int OpenForms = -1;
        public int Assemblies = -1;
        public int Threads = -1;
        public long VirtualMb = -1;
        // The whole PC, not vatSys: how much of its RAM is in use, and how much is free. Low free
        // memory is what turns vatSys's private memory into paging, and so into stutter.
        public int SystemLoadPercent = -1;
        public long SystemAvailableMb = -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // Each count on its own: vatSys's lists are live and can change under a read from this thread,
    // and one that does should cost that number, not the line.
    public static ObjectCounts Objects()
    {
        var counts = new ObjectCounts();

        Try(() => counts.FlightPlans = FDP2.GetFDRs.Count);
        Try(() => counts.RadarTracks = RDP.RadarTracks.Count);
        Try(() => counts.Maps = DisplayMaps.Maps.Count);
        Try(() => counts.OpenForms = Application.OpenForms.Count);
        Try(() => counts.Assemblies = AppDomain.CurrentDomain.GetAssemblies().Length);
        Try(() =>
        {
            using var process = Process.GetCurrentProcess();
            counts.Threads = process.Threads.Count;
            counts.VirtualMb = process.VirtualMemorySize64 / (1024 * 1024);
        });
        Try(() =>
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status))
                return;
            counts.SystemLoadPercent = (int)status.MemoryLoad;
            counts.SystemAvailableMb = (long)(status.AvailPhys / (1024 * 1024));
        });

        return counts;
    }

    static void Try(Action read)
    {
        try
        {
            read();
        }
        catch (Exception)
        {
            // Left at -1.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The log lines
    // ---------------------------------------------------------------------------------------------

    // How many owners and types are named in the text file. The structured copy sent to OzServer
    // carries more (ContextTop), and both stay far inside ClientLogForwarder's 2,000 characters.
    const int TextTop = 6;
    const int ContextTop = 25;

    // Three lines after each memory line - windows, objects, and ClosedWindowSweep's. userObjects is the total the memory line reported: windows
    // are most of it, and menus, cursors, icons and hooks the rest.
    public static void Log(uint userObjects)
    {
        try
        {
            var windows = Windows();
            ActionLog.Log("Perf", $"Windows: {windows.Total} of {userObjects} USER objects - " +
                                  $"by owner: {Top(windows.ByOwner, TextTop)}; by type: {Top(windows.ByType, TextTop)}", new
            {
                action = "memory_windows",
                windows = windows.Total,
                user_objects = userObjects,
                by_owner = Largest(windows.ByOwner, ContextTop),
                by_type = Largest(windows.ByType, ContextTop)
            });
        }
        catch (Exception ex)
        {
            ActionLog.Log("Perf", $"Windows: could not be counted ({ex.Message})");
        }

        try
        {
            var objects = Objects();
            var tracked = TrackedSizes();
            ActionLog.Log("Perf", $"Objects: {objects.FlightPlans} flight plans, {objects.RadarTracks} radar tracks, " +
                                  $"{objects.Maps} maps, {objects.OpenForms} open forms, {objects.Assemblies} assemblies, " +
                                  $"{objects.Threads} threads, {objects.VirtualMb} MB virtual; " +
                                  $"PC memory {objects.SystemLoadPercent}% in use, {objects.SystemAvailableMb} MB free; " +
                                  $"plugin collections: {tracked.Values.Where(size => size > 0).Sum()} entries" +
                                  (tracked.Count > 0 ? $" (largest {Top(tracked, 3)})" : ""), new
            {
                action = "memory_objects",
                flight_plans = objects.FlightPlans,
                radar_tracks = objects.RadarTracks,
                maps = objects.Maps,
                open_forms = objects.OpenForms,
                assemblies = objects.Assemblies,
                threads = objects.Threads,
                virtual_mb = objects.VirtualMb,
                system_load_percent = objects.SystemLoadPercent,
                system_available_mb = objects.SystemAvailableMb,
                plugin_collections = tracked
            });
        }
        catch (Exception ex)
        {
            ActionLog.Log("Perf", $"Objects: could not be counted ({ex.Message})");
        }

        ClosedWindowSweep.Log();
    }

    public static Dictionary<string, int> Largest(Dictionary<string, int> counts, int take) =>
        counts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal)
              .Take(take).ToDictionary(entry => entry.Key, entry => entry.Value);

    public static string Top(Dictionary<string, int> counts, int take)
    {
        if (counts.Count == 0)
            return "none";

        var largest = Largest(counts, take);
        var text = string.Join(", ", largest.Select(entry => $"{entry.Key} {entry.Value}"));
        var rest = counts.Count - largest.Count;
        return rest > 0 ? $"{text}, and {rest} more" : text;
    }
}
