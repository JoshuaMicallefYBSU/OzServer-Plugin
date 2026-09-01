using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OzServerPlugin;

// Consumes GET /api/v1/events, the backend's Server-Sent Events channel.
//
// The server sends signals, never data: an "event: sectors" line means "something under sectors
// changed, go re-read it", and the caller answers with the same authenticated sync call it already
// makes on its poll timer. So nothing here parses a payload, and no DTO change can reach this
// class - it decides only *when* to refresh, never what the answer is. That is also why the
// endpoint needs no token: it carries nothing worth authenticating.
//
// The existing poll timers are deliberately left running as a fallback. If this stream is down -
// server restarting, network blip, a proxy that eats long-lived responses - the plugin degrades to
// exactly the behaviour it had before rather than silently freezing, which for an ATC tool matters
// considerably more than the latency it wins.
public sealed class OzServerEventStream : IDisposable
{
    // Its own client, with no timeout. The shared one in OzServerApiClient has a 20 second timeout
    // (correct for a request/response call, fatal for a connection meant to stay open all session)
    // and its bearer token, which this endpoint does not want.
    static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // One connection for the whole plugin. Several components care about the same stream
    // (ownership, FDR activation) and opening a socket per subscriber would multiply long-lived
    // connections per controller for no benefit - the server fans out to all of them identically.
    public static OzServerEventStream Shared { get; } = new OzServerEventStream();

    // Carries the event name only ("sectors", "fdr", "atis") - see the class comment on why there
    // is deliberately no payload to hand out.
    public event Action<string>? EventReceived;

    readonly CancellationTokenSource _cancel = new();

    OzServerEventStream()
    {
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

    async Task RunAsync(CancellationToken token)
    {
        var backoff = MinBackoff;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(token).ConfigureAwait(false);
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
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
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

    async Task ConsumeAsync(CancellationToken token)
    {
        // ResponseHeadersRead is required, not an optimisation: the default buffers the entire
        // response before returning, and this response is designed never to end - so the await
        // would simply never complete and no event would ever be delivered.
        using (var response = await Http
            .GetAsync(OzServerSettings.BaseUrl + "/api/v1/events", HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);

                    // Server closed the stream; RunAsync reconnects.
                    if (line == null)
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
