using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OzServerPlugin;

// Measures how long vatSys's UI thread takes to get to a no-op posted to it, and logs (Perf) when
// that is long enough for a controller to see the scope stall. The plugin's per-update timing
// (HandlerTiming) only sees the plugin's own handlers, and only the part that runs inline: work
// they hand to the UI thread, and anything else holding it, never shows there. On 16 Nov the
// plugin froze two slower PCs (ML-BLA_CTR, ML-GUN_CTR) without a single Perf line; this is what
// would have shown it.
//
// A single post every Interval, never more than one outstanding, so it costs the UI thread almost
// nothing itself.
//
// Also logs the process's memory and handle counts every MemoryEvery (and with each stall), so a
// slow climb over a long session shows in the logs. ML-SNO_CTR froze 3.5 hours into a session on
// 16 Nov with nothing to say whether vatSys had run short of memory, GDI handles or neither. Each
// memory line is followed by MemoryBreakdown's two, which say what the totals are made of.
public sealed class UiStallWatchdog
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    const double StallMs = 1000;
    // Logged at most this often, so a long freeze is one line, not one every Interval.
    static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MemoryEvery = TimeSpan.FromMinutes(15);

    readonly System.Threading.Timer _timer;
    int _outstanding;
    DateTime _lastLogged = DateTime.MinValue;
    DateTime _lastMemory = DateTime.UtcNow;

    public UiStallWatchdog()
    {
        _timer = new System.Threading.Timer(_ => Probe(), null, Interval, Interval);
    }

    void Probe()
    {
        if (DateTime.UtcNow - _lastMemory >= MemoryEvery)
        {
            _lastMemory = DateTime.UtcNow;
            var memory = Memory();
            ActionLog.Log("Perf", $"Memory: {memory.PrivateMb} MB private, {memory.ManagedMb} MB managed, " +
                                  $"{memory.Gdi} GDI / {memory.User} USER objects, {memory.Handles} handles", new
            {
                action = "memory",
                private_mb = memory.PrivateMb,
                managed_mb = memory.ManagedMb,
                working_set_mb = memory.WorkingSetMb,
                gdi_objects = memory.Gdi,
                user_objects = memory.User,
                handles = memory.Handles,
                gen2_collections = GC.CollectionCount(2)
            });

            // What those totals are made of, then what the plugin itself did meanwhile - see
            // MemoryBreakdown and PluginWork.
            //
            // Contained here as well as inside each: this is a timer thread, where an exception
            // nothing catches ends vatSys, and none of this is worth that.
            try
            {
                MemoryBreakdown.Log(memory.User);
                PluginWork.Log();
            }
            catch (Exception)
            {
            }
        }

        // Only the stall probe stops with OzServer disabled. The memory lines above carry on: they
        // read and write nothing but the log file, and a disabled session is the baseline that
        // shows whether vatSys grows the same way without the plugin doing anything.
        if (OzServerMode.Disabled)
            return;

        if (Application.OpenForms["MainForm"] is not Control main || !main.IsHandleCreated)
            return;
        if (System.Threading.Interlocked.Exchange(ref _outstanding, 1) == 1)
            return;

        var posted = Stopwatch.GetTimestamp();
        try
        {
            main.BeginInvoke(new Action(() =>
            {
                System.Threading.Interlocked.Exchange(ref _outstanding, 0);
                var waitedMs = (Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency;
                if (waitedMs < StallMs || DateTime.UtcNow - _lastLogged < LogEvery)
                    return;

                _lastLogged = DateTime.UtcNow;
                var memory = Memory();
                ActionLog.Log("Perf", $"vatSys's UI thread was blocked for {waitedMs:0} ms " +
                                      $"({memory.PrivateMb} MB private, {memory.Gdi} GDI objects)", new
                {
                    action = "ui_stall",
                    waited_ms = Math.Round(waitedMs),
                    private_mb = memory.PrivateMb,
                    managed_mb = memory.ManagedMb,
                    working_set_mb = memory.WorkingSetMb,
                    gdi_objects = memory.Gdi,
                    user_objects = memory.User,
                    handles = memory.Handles
                });
            }));
        }
        catch (Exception)
        {
            // The main form went away between the check and the post.
            System.Threading.Interlocked.Exchange(ref _outstanding, 0);
        }
    }

    [DllImport("user32.dll")]
    static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    // GetGuiResources flags: 0 = GDI objects, 1 = USER objects. Windows caps each at 10,000 per
    // process by default, and a process at the cap stops drawing.
    static (long PrivateMb, long ManagedMb, long WorkingSetMb, uint Gdi, uint User, int Handles) Memory()
    {
        var managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
        try
        {
            using var process = Process.GetCurrentProcess();
            return (process.PrivateMemorySize64 / (1024 * 1024), managedMb, process.WorkingSet64 / (1024 * 1024),
                    GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1), process.HandleCount);
        }
        catch (Exception)
        {
            return (-1, managedMb, -1, 0, 0, -1);
        }
    }
}
