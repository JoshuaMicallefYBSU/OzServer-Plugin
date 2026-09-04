using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Sends this session's log lines to OzServer, so every controller's log can be read in one place.
//
// Diagnosing anything involving two controllers used to mean collecting a text file from each of
// them and lining the timestamps up by hand - and the interesting client was usually the one whose
// log nobody had. A sector handed to the wrong person, a tag that flashed instead of landing, a
// position relinquished to somebody already online: each of those is two clients disagreeing, and
// neither log alone shows it.
//
// The local file is unchanged and remains the complete record. This is a copy, and a partial one.
//
// What is not sent, and why:
//
//   - API lines. Every HTTP call this plugin makes is already logged by the server that served it,
//     and they are the overwhelming majority of the volume - forwarding them would mean posting a
//     log line about posting a log line.
//   - Anything logged while not connected, which cannot be attributed to a controller anyway.
//
// What is sent is the decisions: what the plugin concluded and why. Those are the lines worth
// comparing between two clients.
public class ClientLogForwarder
{
    // Batched rather than sent per line. A reconnect produces a burst of decisions in a second or
    // two, and one request for the burst is cheaper for everyone than one per line.
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    // Matches the server's per-batch cap. Beyond this the oldest are dropped rather than the newest:
    // if something is generating log lines faster than they can be sent, the recent ones are the
    // ones that explain it.
    const int MaxQueued = 200;

    // Never forwarded - see the class comment.
    const string ApiCategory = "API";

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _timer;
    readonly object _lock = new();
    readonly List<OzServerClientLogLineDto> _queued = new();
    bool _sending;

    public ClientLogForwarder()
    {
        ActionLog.LineWritten += OnLine;
        _timer = new System.Threading.Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    void OnLine(string category, string message)
    {
        if (category == ApiCategory || !Network.IsConnected)
            return;

        lock (_lock)
        {
            _queued.Add(new OzServerClientLogLineDto
            {
                At = DateTimeOffset.UtcNow,
                Category = category,
                Message = message
            });

            if (_queued.Count > MaxQueued)
                _queued.RemoveRange(0, _queued.Count - MaxQueued);
        }
    }

    async Task FlushAsync()
    {
        List<OzServerClientLogLineDto> batch;

        lock (_lock)
        {
            // One flush at a time. Overlapping sends would reorder a narrative whose whole value is
            // its order.
            // Identity is all this needs - a line has to be attributable to a controller to be
            // worth anything. Deliberately not the markup rule: an observer's log is as useful for
            // diagnosis as anyone's, and forwarding it changes nothing anyone else sees.
            if (_sending || _queued.Count == 0 || NetworkIdentity.Current == null)
                return;

            _sending = true;
            batch = _queued.ToList();
            _queued.Clear();
        }

        try
        {
            await _api.SendClientLogsAsync(batch).ConfigureAwait(false);
        }
        catch
        {
            // Silently dropped, and deliberately not re-queued or logged. This is a diagnostic
            // convenience: it must never cost the controller anything, and a failure that logged
            // its own failure would feed itself.
        }
        finally
        {
            lock (_lock)
                _sending = false;
        }
    }
}
