using System;
using System.Collections.Generic;
using vatsys;

namespace OzServerPlugin;

// Puts a recurring failure in front of the controller once, not once per attempt.
//
// vatSys's Errors window is the only notification surface a plugin has, and it opens itself for
// every entry. That is right for something the controller did (a claim that was refused), and wrong
// for the background loops: the ownership poll runs every three seconds and the FDR flush every
// five, so a backend restart - which every deploy is - raised the Errors window over the scope
// several times a minute on every connected controller, for a fault none of them could do anything
// about and that fixed itself.
//
// Each caller names what is failing with a key. The first failure under a key is reported; further
// ones inside the window are dropped (they are still in the action log - every API call is, through
// OzServerApiClient). Deliberately not reset by a success: a connection that flaps would otherwise
// report every other attempt, which is the same noise at half the rate.
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
