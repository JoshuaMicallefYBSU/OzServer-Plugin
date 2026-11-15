using System;
using System.Collections.Generic;

namespace OzServerPlugin;

// What OzServer last said about each FDR: when it was last updated, and from which sector. Refreshed
// by FdrResumeActivation's scan (every 20 s), and read by FdrSync's watchdog to tell whether the
// controller chosen to report an untracked aircraft actually is - see FdrSync.TakeOverIfStale.
public static class ServerFdrSnapshot
{
    public readonly struct Entry
    {
        public Entry(DateTimeOffset? lastSeenAt, string? authoritySector, int? controllingCid)
        {
            LastSeenAt = lastSeenAt;
            AuthoritySector = authoritySector;
            ControllingCid = controllingCid;
        }

        public DateTimeOffset? LastSeenAt { get; }
        public string? AuthoritySector { get; }
        public int? ControllingCid { get; }
    }

    static readonly object Gate = new();
    static Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    static DateTime _takenAt = DateTime.MinValue;

    public static void Update(IEnumerable<OzServerFdrRecordDto> records)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!string.IsNullOrEmpty(record.Callsign))
                entries[record.Callsign] = new Entry(record.LastSeenAt, record.AuthoritySector, record.ControllingCid);
        }

        lock (Gate)
        {
            _entries = entries;
            _takenAt = DateTime.UtcNow;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            _takenAt = DateTime.MinValue;
        }
    }

    // How old the snapshot itself is - an answer from a snapshot older than this is not worth acting on.
    public static TimeSpan Age
    {
        get
        {
            lock (Gate)
                return _takenAt == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.UtcNow - _takenAt;
        }
    }

    public static bool TryGet(string callsign, out Entry entry)
    {
        lock (Gate)
            return _entries.TryGetValue(callsign, out entry);
    }
}
