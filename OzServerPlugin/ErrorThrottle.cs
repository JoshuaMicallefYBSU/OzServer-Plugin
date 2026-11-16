using System;
using System.Collections.Generic;
using vatsys;

namespace OzServerPlugin;

// Puts a recurring failure in front of the controller once, not once per attempt.
//
// vatSys's Errors window is the only notification surface a plugin has, and it opens itself for
// every entry. That suits something the controller did (a refused claim) but not the background
// loops, which run every few seconds: a backend restart - every deploy is one - would raise the
// window over the scope several times a minute for a fault that fixes itself.
//
// Each caller names what is failing with a key. The first failure under a key is reported and
// further ones inside the window are dropped; OzServerApiClient still records every call in the
// action log. Not reset by a success: a flapping connection would otherwise report every other
// attempt.
static class ErrorThrottle
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    static readonly object Gate = new();
    static readonly Dictionary<string, DateTime> QuietUntil = new(StringComparer.Ordinal);

    public static void Report(string key, string message, Exception? cause = null)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (QuietUntil.TryGetValue(key, out var until) && now < until)
                return;

            QuietUntil[key] = now + Window;
        }

        Errors.Add(new Exception(message, cause), "OzServer");
    }
}
