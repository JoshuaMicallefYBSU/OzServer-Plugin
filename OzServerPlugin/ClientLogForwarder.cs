using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Sends this session's log lines to OzServer, so every controller's log can be read in one place:
// a fault involving two controllers is two clients disagreeing, and neither log alone shows it.
//
// The local file remains the complete record; this is a partial copy. Not sent:
//
//   - API lines. The server already logs every HTTP call it serves, and they are most of the
//     volume.
//   - Anything logged while not connected, which cannot be attributed to a controller. Errors
//     (ErrorReporter) are the exception: they are held and sent once a session gives them one.
//
// What is sent is the decisions: what the plugin concluded and why, and every error it raised.
public class ClientLogForwarder
{
    // Batched rather than sent per line: a reconnect produces a burst of decisions within a second
    // or two.
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    // Matches the server's per-batch cap. Beyond it the oldest are dropped: if lines are produced
    // faster than they can be sent, the recent ones are the ones that explain why.
    const int MaxQueued = 200;

    // Never forwarded - see the class comment.
    const string ApiCategory = "API";

    // The server's limit on one line's message (batchSchema in routes/client-logs.ts).
    const int MaxMessageLength = 2000;

    static readonly string SessionId = Guid.NewGuid().ToString("N");
    static readonly string? PluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _timer;
    readonly object _lock = new();
    readonly List<OzServerClientLogLineDto> _queued = new();
    long _sequence;
    bool _sending;

    public ClientLogForwarder()
    {
        ActionLog.StructuredLineWritten += OnLine;
        _timer = new System.Threading.Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    void OnLine(string category, string message, object? context)
    {
        if (category == ApiCategory || string.IsNullOrWhiteSpace(message))
            return;

        // An error is kept even before a session is up, and goes out with the first flush that has
        // an identity to send it under. Anything else logged while offline is not worth holding.
        if (!Network.IsConnected && category != ErrorReporter.Category)
            return;

        // The server refuses a whole batch for one line over its limit.
        if (message.Length > MaxMessageLength)
            message = message.Substring(0, MaxMessageLength);

        lock (_lock)
        {
            _queued.Add(new OzServerClientLogLineDto
            {
                At = DateTimeOffset.UtcNow,
                Category = category,
                Message = message,
                PluginVersion = PluginVersion,
                SessionId = SessionId,
                Sequence = ++_sequence,
                Context = context
            });

            if (_queued.Count > MaxQueued)
                _queued.RemoveRange(0, _queued.Count - MaxQueued);
        }
    }

    // The last of the log, sent while disabling (inside OzServerMode.AllowFinalCalls).
    public Task FlushNowAsync() => FlushAsync();

    async Task FlushAsync()
    {
        if (!OzServerMode.RequestsAllowed)
            return;

        List<OzServerClientLogLineDto> batch;

        lock (_lock)
        {
            // One flush at a time: overlapping sends would arrive out of order. An identity is the
            // only requirement, so an observer's log is forwarded like anyone's.
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
            // Dropped without re-queueing or logging: this is a diagnostic convenience, and a
            // failure that logged itself would produce more lines to send.
        }
        finally
        {
            lock (_lock)
                _sending = false;
        }
    }
}
