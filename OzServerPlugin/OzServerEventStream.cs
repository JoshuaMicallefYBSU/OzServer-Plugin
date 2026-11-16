using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Consumes GET /api/v1/events, the backend's Server-Sent Events channel.
//
// An event is a signal, not data: "event: sectors" means something under sectors changed, and the
// subscriber answers with the same sync call its poll timer makes. Nothing here parses a payload;
// this class decides only when to refresh. The endpoint asks for no identity for the same reason.
//
// The stream is per server: the backend keeps live VATSIM and each SweatBox apart, and answers 400
// without ?server=. A connection is therefore held only while there is a session on a server
// OzServer operates on, and dropped when that session ends, so the next one - possibly on a
// different server - opens its own.
//
// The poll timers elsewhere keep running as a fallback. If the stream is down - a server restart,
// a network blip, a proxy that cuts long-lived responses - the plugin degrades to polling rather
// than freezing.
public sealed class OzServerEventStream : IDisposable
{
    // Its own client with no timeout: the request timeout on OzServerApiClient's shared client
    // would end a connection meant to stay open all session.
    static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    // How often to look for a session while there is none.
    static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(1);
    // The server writes a keepalive comment every 20 seconds. Three missed in a row means a dead
    // socket, on which a read would otherwise wait forever.
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(65);
    static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    // When the open connection last delivered a line, keepalives included. One field serves
    // because there is only one connection at a time.
    long _lastLineTicks;

    // What subscribers are told to re-read when a connection comes up: anything signalled while
    // there was no stream was missed.
    static readonly string[] CatchUpEvents = { "sectors" };

    // One connection for the whole plugin; the server sends every subscriber the same events.
    public static OzServerEventStream Shared { get; } = new OzServerEventStream();

    // Carries the event name only ("sectors", "fdr", "atis") - see the class comment.
    public event Action<string>? EventReceived;

    readonly CancellationTokenSource _cancel = new();
    // The connection currently open, if any. Cancelled when its session ends, without stopping
    // the stream as a whole.
    readonly object _connectionGate = new();
    CancellationTokenSource? _connection;

    OzServerEventStream()
    {
        Network.Disconnected += (_, _) => DropConnection();
        // Disabled is permanent for the session, so RunAsync never connects again.
        OzServerMode.DisabledChanged += (_, _) => DropConnection();
        _ = RunAsync(_cancel.Token);
    }

    // A subscriber that throws must not end the stream for the others.
    void Raise(string name)
    {
        var handlers = EventReceived;
        if (handlers == null || OzServerMode.Disabled)
            return;

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(name);
            }
            catch
            {
                // Subscriber's problem, not the stream's.
            }
        }
    }

    void DropConnection()
    {
        lock (_connectionGate)
        {
            try
            {
                _connection?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished on its own.
            }
        }
    }

    async Task RunAsync(CancellationToken token)
    {
        var backoff = MinBackoff;

        while (!token.IsCancellationRequested)
        {
            // Nothing to subscribe to without a session, or on a server OzServer does not operate
            // on (LocalHost, an unrecognised SweatBox).
            var server = Network.IsConnected && !OzServerMode.Disabled ? NetworkServer.Current : null;
            if (server == null)
            {
                backoff = MinBackoff;

                try
                {
                    await Task.Delay(IdleCheck, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (_connectionGate)
                _connection = connection;

            try
            {
                await ConsumeAsync(server, connection.Token).ConfigureAwait(false);
                // A clean end of stream is an ordinary server restart or redeploy, so reconnect
                // promptly.
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Silent: the fallback polls keep everything correct, so a flapping stream must not
                // fill the Errors window. A connection dropped because its session ended is not a
                // failure to back off from.
                backoff = connection.IsCancellationRequested
                    ? MinBackoff
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            finally
            {
                lock (_connectionGate)
                {
                    _connection = null;
                    connection.Dispose();
                }
            }

            try
            {
                await Task.Delay(backoff, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task ConsumeAsync(string server, CancellationToken token)
    {
        var url = $"{OzServerSettings.BaseUrl}/api/v1/events?server={Uri.EscapeDataString(server)}";

        // ResponseHeadersRead is required: the default buffers the whole response before
        // returning, and this response never ends.
        using (var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            // ReadLineAsync takes no token on .NET Framework, so cancellation disposes the stream
            // to end the read.
            using (token.Register(() =>
                   {
                       try
                       {
                           stream.Dispose();
                       }
                       catch
                       {
                           // Racing its own disposal below; either one closing it is enough.
                       }
                   }))
            // One watchdog for the connection rather than a timeout per read, which on a busy
            // server would leave hundreds of timers pending. It closes the stream as cancellation
            // does, which fails the read below and sends RunAsync round again.
            using (new System.Threading.Timer(_ =>
                   {
                       if (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastLineTicks) < ReadTimeout.Ticks)
                           return;

                       try
                       {
                           stream.Dispose();
                       }
                       catch
                       {
                           // As above.
                       }
                   }, null, WatchdogInterval, WatchdogInterval))
            using (var reader = new StreamReader(stream))
            {
                Volatile.Write(ref _lastLineTicks, DateTime.UtcNow.Ticks);

                foreach (var name in CatchUpEvents)
                    Raise(name);

                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    Volatile.Write(ref _lastLineTicks, DateTime.UtcNow.Ticks);

                    // Server closed the stream; RunAsync reconnects.
                    if (line == null)
                        return;

                    // The session this connection was opened for is over, or has moved to a
                    // different server.
                    if (!Network.IsConnected || NetworkServer.Current != server)
                        return;

                    // Only event lines are acted on. Lines starting with ':' are SSE comments -
                    // the server's keepalives.
                    if (line.StartsWith("event:", StringComparison.Ordinal))
                        Raise(line.Substring("event:".Length).Trim());
                }
            }
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }
}
