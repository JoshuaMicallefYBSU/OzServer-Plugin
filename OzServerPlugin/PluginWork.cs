using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace OzServerPlugin;

// How much work this plugin itself did since the last Perf memory line: the fourth line of each
// set, after MemoryBreakdown's two. The memory line says vatSys grew, MemoryBreakdown says what
// it is holding, and this says what share of the process's time and allocation was the plugin's -
// so a long session that slows down either shows the plugin busy, or shows it idle while vatSys
// grew anyway.
//
//  - handlers: the per-update work vatSys calls the plugin for (Plugin.Isolate), by subsystem;
//  - UI thread: everything the plugin runs on vatSys's UI thread (UiThread.Guard) - the time a
//    controller would feel as a stall, with the longest single piece named;
//  - allocated: what those two allocated. Allocation is not retention, but it is what makes the
//    garbage collector run;
//  - API: responses and their size, with the largest named. Each is buffered whole before it is
//    parsed, and a buffer over 85 KB lands on the large object heap, which a 32-bit process is
//    short of.
//
// HandlerTiming still reports a single slow call or a busy second as it happens; these are the
// totals, which a thousand cheap calls can add up to without tripping either.
static class PluginWork
{
    sealed class Tally
    {
        public long Calls;
        public double Ms;
        public long Bytes;
        public double LongestMs;
        public string? Longest;
    }

    static readonly object Gate = new();
    static Dictionary<string, Tally> _handlers = new(StringComparer.Ordinal);
    static Tally _ui = new();
    static long _apiRequests;
    static long _apiBytes;
    static long _apiLargestBytes;
    static string? _apiLargest;
    static DateTime _since = DateTime.UtcNow;

    // GC.GetAllocatedBytesForCurrentThread arrived in .NET Framework 4.8, and the plugin is built
    // against 4.7.2, so it is bound at run time. Without it the allocation figures read zero.
    static readonly Func<long>? AllocatedOnThread = BindAllocated();

    static Func<long>? BindAllocated()
    {
        try
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return method == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static long Allocated() => AllocatedOnThread?.Invoke() ?? 0;

    public static void Handler(string category, double ms, long bytes)
    {
        lock (Gate)
        {
            if (!_handlers.TryGetValue(category, out var tally))
                _handlers[category] = tally = new Tally();
            tally.Calls++;
            tally.Ms += ms;
            tally.Bytes += bytes;
        }
    }

    // The site is only worked out for a new longest piece, so the common call costs a lock and
    // three additions.
    public static void Ui(Action action, double ms, long bytes)
    {
        lock (Gate)
        {
            _ui.Calls++;
            _ui.Ms += ms;
            _ui.Bytes += bytes;
            if (ms > _ui.LongestMs)
            {
                _ui.LongestMs = ms;
                _ui.Longest = Site(action);
            }
        }
    }

    // The size is the body's: bytes for a GET, characters for the short answers to a POST or PUT,
    // which for JSON come to the same.
    public static void Api(string path, long size)
    {
        lock (Gate)
        {
            _apiRequests++;
            _apiBytes += size;
            if (size > _apiLargestBytes)
            {
                _apiLargestBytes = size;
                _apiLargest = path;
            }
        }
    }

    // "Type.Method" for the code a delegate runs. A lambda is compiled to a method named
    // <Enclosing>b__0 on a nested <>c__DisplayClass type: the enclosing method and the outer type
    // are the names a reader would recognise.
    static string Site(Delegate action)
    {
        try
        {
            var method = action.Method;
            var type = method.DeclaringType;
            while (type?.DeclaringType != null && type.Name.StartsWith("<", StringComparison.Ordinal))
                type = type.DeclaringType;

            var name = method.Name;
            var close = name.IndexOf('>');
            if (name.StartsWith("<", StringComparison.Ordinal) && close > 1)
                name = name.Substring(1, close - 1);

            return $"{type?.Name ?? "?"}.{name}";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    // Writes the totals since the last call and starts again.
    public static void Log()
    {
        try
        {
            Dictionary<string, Tally> handlers;
            Tally ui;
            long requests, bytes, largestBytes;
            string? largest;
            TimeSpan over;

            lock (Gate)
            {
                handlers = _handlers;
                ui = _ui;
                requests = _apiRequests;
                bytes = _apiBytes;
                largestBytes = _apiLargestBytes;
                largest = _apiLargest;
                over = DateTime.UtcNow - _since;

                _handlers = new Dictionary<string, Tally>(StringComparer.Ordinal);
                _ui = new Tally();
                _apiRequests = 0;
                _apiBytes = 0;
                _apiLargestBytes = 0;
                _apiLargest = null;
                _since = DateTime.UtcNow;
            }

            var handlerMs = handlers.Values.Sum(tally => tally.Ms);
            var handlerCalls = handlers.Values.Sum(tally => tally.Calls);
            var allocatedMb = (handlers.Values.Sum(tally => tally.Bytes) + ui.Bytes) / (1024.0 * 1024.0);
            var busiest = string.Join(", ", handlers.OrderByDescending(entry => entry.Value.Ms).Take(3)
                .Select(entry => $"{entry.Key} {entry.Value.Ms / 1000:0.0} s"));

            ActionLog.Log("Perf",
                $"Plugin work in the last {over.TotalMinutes:0} min: handlers {handlerMs / 1000:0.0} s over {handlerCalls} calls" +
                (busiest.Length > 0 ? $" ({busiest})" : "") +
                $"; UI thread {ui.Ms / 1000:0.0} s over {ui.Calls} pieces" +
                (ui.Longest != null ? $" (longest {ui.LongestMs:0} ms, {ui.Longest})" : "") +
                $"; {allocatedMb:0} MB allocated" +
                $"; API {requests} responses, {bytes / (1024.0 * 1024.0):0.0} MB" +
                (largest != null ? $" (largest {largestBytes / 1024.0:0} KB, {largest})" : ""), new
            {
                action = "plugin_work",
                minutes = Math.Round(over.TotalMinutes, 1),
                handler_ms = Math.Round(handlerMs),
                handler_calls = handlerCalls,
                handlers = handlers.ToDictionary(entry => entry.Key, entry => new
                {
                    calls = entry.Value.Calls,
                    ms = Math.Round(entry.Value.Ms),
                    allocated_kb = entry.Value.Bytes / 1024
                }),
                ui_ms = Math.Round(ui.Ms),
                ui_pieces = ui.Calls,
                ui_longest_ms = Math.Round(ui.LongestMs),
                ui_longest = ui.Longest,
                ui_allocated_kb = ui.Bytes / 1024,
                allocation_measured = AllocatedOnThread != null,
                api_responses = requests,
                api_bytes = bytes,
                api_largest_bytes = largestBytes,
                api_largest = largest,
                gen0_collections = GC.CollectionCount(0),
                gen1_collections = GC.CollectionCount(1)
            });
        }
        catch (Exception ex)
        {
            ActionLog.Log("Perf", $"Plugin work: could not be totalled ({ex.Message})");
        }
    }
}
