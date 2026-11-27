using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace OzServerPlugin;

// Lets go of the closed windows vatSys keeps hold of.
//
// Every vatSys window - its own and every plugin's - derives from NonClientBaseForm, whose
// constructor ends in Application.AddMessageFilter(this). Nothing ever calls RemoveMessageFilter,
// so WinForms keeps a reference to every such window ever made, closed and disposed or not. And
// BaseForm's constructor gives each one its own copy of vatSys's 256x256 icon (Resources.MainIcon
// builds a new Icon on every read), which Form.Dispose does not dispose.
//
// So each window opened and closed is never collected. Measured against vatSys's own BaseForm in
// a 32-bit process: 266 KB of managed memory, about 470 KB of private memory, three GDI objects
// and one USER object for each, for the life of the process. The CFL, level and time menus are
// such windows, built afresh on every click (MMI.OpenCFLMenu), so a controller leaves one behind
// every few seconds of working traffic. ML-HUO_CTR's session of 26 Nov gained 510 GDI and 135 USER
// objects an hour - the three to one this leaves.
//
// Every SweepEvery this takes the disposed windows out of the filter list and takes their icon
// off them. Both are safe on a disposed window, and only on one:
//
//  - its filter does nothing: NonClientBaseForm.PreFilterMessage answers false for everything but
//    a mouse button, and for those WmButtonDown and WmButtonUp return false as soon as they see
//    IsDisposed. Removing it changes what no message does;
//  - it can never be shown again, so it has no use for an icon. The icon is not disposed here -
//    one a plugin shares between its windows must survive - only let go of, and the runtime
//    destroys it when nothing else holds it.
//
// The icon is dropped as well as the filter because the filter is not always the only thing
// holding a closed window. Accessibility software that looked at it while it was open (a screen
// reader, some overlays and remote-control tools) is left holding its accessibility object, and
// with it the window: in testing that kept between a tenth and nine tenths of them alive after the
// filter was gone. What is left of such a window without its icon is a few kilobytes.
//
// The filter list is private to WinForms (Application.ThreadContext.messageFilters), so it is read
// by reflection, and if a later runtime moves it the sweep says so once and does nothing more.
// Removal itself is the public Application.RemoveMessageFilter.
//
// Not while OzServer is disabled: disabled means the plugin changes nothing in vatSys, and a
// disabled session is the baseline the Perf memory lines are compared against.
public static class ClosedWindowSweep
{
    static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);

    static System.Threading.Timer? _timer;
    static bool _unavailable;

    // Since the last Perf line. Guarded by Gate: written on the UI thread, read on the watchdog's.
    static readonly object Gate = new();
    static Dictionary<string, int> _released = new(StringComparer.Ordinal);
    static int _releasedTotal;
    static int _stillOpen = -1;

    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ =>
        {
            // A timer thread: an exception nothing catches here ends vatSys. Posting can throw
            // while the main form is closing.
            try
            {
                AsdMapLayer.RunOnUiThread(() => Sweep());
            }
            catch (Exception)
            {
            }
        }, null, SweepEvery, SweepEvery);
    }

    // The message filters of the calling thread, or null if WinForms no longer keeps them where
    // this looks.
    static IList? Filters()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;
        var context = typeof(Application).GetNestedType("ThreadContext", Any);
        var current = context?.GetMethod("FromCurrent", Any | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
        return current == null ? null : context!.GetField("messageFilters", Any | BindingFlags.Instance)?.GetValue(current) as IList;
    }

    // Returns how many closed windows were let go of. On the thread whose windows they are: the
    // filter list is per thread.
    public static int Sweep()
    {
        if (OzServerMode.Disabled || _unavailable)
            return 0;

        // Only on vatSys's UI thread. RunOnUiThread runs its work inline when there is no main form
        // to post to, which would be the timer's own thread: it has no windows and no filters, and
        // asking WinForms for its filter list would make it set that thread up as a UI thread.
        if (Application.OpenForms["MainForm"] is not Control main || main.InvokeRequired)
            return 0;

        return SweepThisThread();
    }

    // The sweep itself, on whichever thread calls it. Separate so it can be exercised without a
    // vatSys main form.
    public static int SweepThisThread()
    {
        IList? filters;
        try
        {
            filters = Filters();
        }
        catch (Exception)
        {
            filters = null;
        }

        if (filters == null)
        {
            // Nothing has been registered on this thread yet, or the list has moved. Told apart by
            // whether the field can be found at all.
            if (!FilterListExists())
            {
                _unavailable = true;
                ActionLog.Log("Perf", "Closed windows: WinForms no longer keeps its message filters where OzServer looks - closed windows are not being released");
            }
            return 0;
        }

        var released = 0;
        var open = 0;

        // A copy: RemoveMessageFilter changes the list being walked.
        foreach (var filter in filters.Cast<object>().ToArray())
        {
            if (filter is not Form form)
                continue;

            if (!form.IsDisposed)
            {
                open++;
                continue;
            }

            try
            {
                Application.RemoveMessageFilter((IMessageFilter)filter);
                form.Icon = null;
            }
            catch (Exception)
            {
                // Off the list or not, there is nothing more to do for this one.
            }

            released++;
            var name = MemoryBreakdown.Name(form.GetType());
            lock (Gate)
                MemoryBreakdown.Add(_released, name, 1);
        }

        lock (Gate)
        {
            _releasedTotal += released;
            _stillOpen = open;
        }

        return released;
    }

    static bool FilterListExists()
    {
        try
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;
            return typeof(Application).GetNestedType("ThreadContext", Any)?.GetField("messageFilters", Any | BindingFlags.Instance) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // One line with the Perf memory set: how many closed windows were let go of since the last,
    // and whose they were. This is also the record of what would otherwise have piled up.
    public static void Log()
    {
        Dictionary<string, int> released;
        int total, open;
        lock (Gate)
        {
            released = _released;
            total = _releasedTotal;
            open = _stillOpen;
            _released = new Dictionary<string, int>(StringComparer.Ordinal);
            _releasedTotal = 0;
        }

        if (OzServerMode.Disabled)
            return;

        ActionLog.Log("Perf",
            total == 0
                ? $"Closed windows: none to release; {open} open"
                : $"Closed windows: released {total} that vatSys was still holding - {MemoryBreakdown.Top(released, 6)}; {open} open",
            new
            {
                action = "closed_windows",
                released = total,
                open,
                by_type = MemoryBreakdown.Largest(released, 25),
                available = !_unavailable
            });
    }
}
