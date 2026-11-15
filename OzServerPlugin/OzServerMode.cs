using System;
using System.Threading;

namespace OzServerPlugin;

// The session-wide "Disable OzServer" switch (Settings > Disable OzServer).
//
// OzServer is going into testing, and a controller who hits a problem needs to be able to take it out
// of their session completely and carry on with plain vatSys. Once disabled, for the rest of the
// session:
//
//  - nothing is sent to OzServer (OzServerApiClient refuses every request, and the event stream
//    closes), so no claims, heartbeats, FDR pushes, ATIS or logs;
//  - nothing in the plugin changes anything in vatSys: every event handler and timer that would
//    touch controlled sectors, VSCS, tags, FDRs or the scope checks Disabled first and returns.
//
// One-way on purpose: switching every subsystem back on mid-session, against whatever the controller
// has done in plain vatSys meanwhile, is exactly the kind of state the switch exists to escape.
// Restarting vatSys brings OzServer back.
public static class OzServerMode
{
    static volatile bool _disabled;

    public static bool Disabled => _disabled;

    public const string DisabledMessage = "OzServer is disabled for this session.";

    // Raised once, on the UI thread of whoever disabled it, after Disabled has turned true.
    public static event EventHandler? DisabledChanged;

    public static void Disable()
    {
        if (_disabled)
            return;

        _disabled = true;
        DisabledChanged?.Invoke(null, EventArgs.Empty);
    }

    // The clean-up made while disabling - releasing this controller's sectors so nobody else is left
    // blocked by them, and sending the last of the log - has to get through after everything else is
    // already shut off. Scoped to the async flow that opened it, so nothing else slips out alongside.
    static readonly AsyncLocal<bool> FinalCalls = new();

    public static bool RequestsAllowed => !_disabled || FinalCalls.Value;

    public static IDisposable AllowFinalCalls()
    {
        FinalCalls.Value = true;
        return new Scope();
    }

    sealed class Scope : IDisposable
    {
        public void Dispose() => FinalCalls.Value = false;
    }
}
