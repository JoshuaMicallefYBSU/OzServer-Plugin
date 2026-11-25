using System;
using vatsys;

namespace OzServerPlugin;

// A planned OzServer API restart - every deploy is one - during which errors that only say "the API
// is not answering" are held back instead of opening vatSys's Errors window over the scope.
//
// The API announces it as it shuts down: a "restarting" event on the event stream
// (OzServerEventStream). From then until the API answers again - any successful call, or the event
// stream reconnecting - or MaxHold has passed, OzErrors drops errors caused by an unreachable API: a
// transport failure (no response, timeout) or a 5xx, which the proxy answers with while the
// container is down. Anything else - a refused claim, a 4xx - is still shown. Every dropped error is
// still in the action log.
static class ApiOutage
{
    // Long enough for a rebuild and a rolled-back deploy; past it, errors show again whatever the
    // reason, so a restart that never finishes is not hidden.
    static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(5);

    static readonly object Gate = new();
    static DateTime _holdUntil = DateTime.MinValue;
    static int _suppressed;

    public static bool Holding
    {
        get
        {
            lock (Gate)
                return DateTime.UtcNow < _holdUntil;
        }
    }

    // The API said it is restarting.
    public static void Begin()
    {
        lock (Gate)
        {
            _holdUntil = DateTime.UtcNow + MaxHold;
            _suppressed = 0;
        }

        ActionLog.Log("Outage", $"OzServer API is restarting - holding back connection errors for up to {MaxHold.TotalMinutes:0} min",
            new { action = "api_restart_announced" });
    }

    // The API answered: the restart is over.
    public static void End(string how)
    {
        int suppressed;
        lock (Gate)
        {
            if (_holdUntil == DateTime.MinValue)
                return;
            _holdUntil = DateTime.MinValue;
            suppressed = _suppressed;
        }

        ActionLog.Log("Outage", $"OzServer API is back ({how}) - {suppressed} connection error(s) held back while it restarted",
            new { action = "api_restart_over", how, suppressed });
    }

    // Whether this error is one to hold back now: the API is restarting and the error comes from it
    // not answering.
    public static bool ShouldSuppress(Exception error)
    {
        if (!Holding || !IsUnreachable(error))
            return false;

        lock (Gate)
            _suppressed++;
        return true;
    }

    // An OzServerApiException anywhere in the chain with no status (nothing came back) or a 5xx.
    static bool IsUnreachable(Exception error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            if (current is OzServerApiException api)
                return api.StatusCode == null || api.StatusCode >= 500;
        }
        return false;
    }
}

// Where the plugin raises its errors, in place of vatSys's Errors.Add: the same, except that an
// error held back by ApiOutage during an API restart goes only to the action log. Filtering has to
// happen before Errors.Add - vatSys's Errors window subscribes to Errors.ErrorsChanged at start-up,
// ahead of any plugin, and opens itself for an entry before a plugin could take it back.
static class OzErrors
{
    public static void Add(Exception error, string source = "")
    {
        if (ApiOutage.ShouldSuppress(error))
        {
            ActionLog.Log("Outage", $"held back during API restart: {error.Message}", new { action = "api_restart_error_suppressed" });
            return;
        }

        Errors.Add(error, source);
    }
}
