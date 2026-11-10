using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using vatsys;

namespace OzServerPlugin;

// Consumes GET /api/v1/events, the backend's Server-Sent Events channel.
//
// The server sends signals, never data: an "event: sectors" line means "something under sectors
// changed, go re-read it", and the caller answers with the same sync call it already makes on its
// poll timer. So nothing here parses a payload, and no DTO change can reach this class - it decides
// only *when* to refresh, never what the answer is. That is also why the endpoint asks for no
// identity: it carries nothing worth protecting.
//
// The stream is per server. The backend keeps live VATSIM and each SweatBox entirely apart, and
// requires ?server= to know which one this subscriber is watching - without it the request is
// answered 400. This class used to connect once, at plugin load, with no server at all: every
// attempt was refused, the failure was (deliberately) silent, and the plugin ran on its fallback
// polls alone from the day the backend gained server isolation. Now it connects only while there is
// a session on a server OzServer operates on, names that server, and drops the connection when the
// session ends so the next one - possibly on a different server - starts its own.
//
// The poll timers elsewhere are deliberately left running as a fallback. If this stream is down -
// server restarting, network blip, a proxy that eats long-lived responses - the plugin degrades to
// polling rather than silently freezing, which for an ATC tool matters considerably more than the
// latency it wins.
public sealed class OzServerEventStream : IDisposable
{
    // Its own client, with no timeout. The shared one in OzServerApiClient has a request timeout
    // (correct for a request/response call, fatal for a connection meant to stay open all session).
    static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    // How often to look for a session to subscribe on behalf of while there is none.
    static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(1);
    // The server writes a keepalive comment every 20 seconds. Three missed in a row is not a quiet
    // channel, it is a dead socket that nothing has told us about - and a read on one of those
    // otherwise waits forever, looking connected and delivering nothing.
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(65);
    static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    // When the open connection last delivered a line of any kind, keepalives included. There is
    // only ever one connection at a time, so one field serves.
    long _lastLineTicks;

    // What subscribers are told to re-read the moment a connection comes up. Whatever was signalled
    // while there was no stream was never seen, so "connected" has to mean "assume everything
    // changed" - once - rather than "nothing has happened yet".
    static readonly string[] CatchUpEvents = { "sectors", "annotations" };

    // One connection for the whole plugin. Several components care about the same stream
    // (ownership, drawing, notes) and opening a socket per subscriber would multiply long-lived
    // connections per controller for no benefit - the server fans out to all of them identically.
    public static OzServerEventStream Shared { get; } = new OzServerEventStream();

    // Carries the event name only ("sectors", "fdr", "atis", "annotations") - see the class comment
    // on why there is deliberately no payload to hand out.
    public event Action<string>? EventReceived;

    readonly CancellationTokenSource _cancel = new();
    // The connection currently open, if any - cancelled on its own when the session it belongs to
    // ends, without stopping the stream as a whole.
    readonly object _connectionGate = new();
    CancellationTokenSource? _connection;

    OzServerEventStream()
    {
        Network.Disconnected += (_, _) => DropConnection();
        _ = RunAsync(_cancel.Token);
    }

    // One subscriber throwing must not tear the stream down for the others, or a single bad
    // handler silently costs every other component its push updates for the rest of the session.
    void Raise(string name)
    {
        var handlers = EventReceived;
        if (handlers == null)
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
            // Nothing to subscribe to without a session, and nothing to subscribe *as* on a server
            // OzServer does not operate on (LocalHost, an unrecognised SweatBox).
            var server = Network.IsConnected ? NetworkServer.Current : null;
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
                // A clean end of stream is an ordinary server restart or redeploy, not a fault -
                // reconnect promptly rather than carrying a punitive backoff into it.
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Deliberately silent. The fallback polls keep everything correct without this
                // stream, so a flapping connection must not fill the controller's vatSys error
                // list mid-session with something that isn't costing them anything.
                //
                // Dropped on purpose (the session ended) is not a failure to back off from.
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

        // ResponseHeadersRead is required, not an optimisation: the default buffers the entire
        // response before returning, and this response is designed never to end - so the await
        // would simply never complete and no event would ever be delivered.
        using (var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            // ReadLineAsync takes no token on .NET Framework, so cancelling has to pull the stream
            // out from under the read instead - which is what ends it when the session does.
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
            // One watchdog for the life of the connection rather than a timeout per read: on a busy
            // server a line arrives several times a second, and a fresh 65 second timer for each
            // would leave hundreds of them pending at any moment. It closes the stream the same way
            // cancellation does, which fails the read below and sends RunAsync round again.
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

                    // The session this connection was opened for is over, or has become one on a
                    // different server. Either way this stream is no longer the right one.
                    if (!Network.IsConnected || NetworkServer.Current != server)
                        return;

                    // Lines starting with ':' are SSE comments - the server's keepalive pings,
                    // which exist purely to stop an idle intermediary dropping the connection.
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
