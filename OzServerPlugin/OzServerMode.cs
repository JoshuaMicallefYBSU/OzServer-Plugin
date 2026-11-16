using System;
using System.Threading;

namespace OzServerPlugin;

// The session-wide "Disable OzServer" switch (Tools > Disable OzServer), which lets a controller
// who hits a problem carry on with plain vatSys. Once disabled, for the rest of the session:
//
//  - nothing is sent to OzServer: OzServerApiClient refuses every request and the event stream
//    closes;
//  - the plugin changes nothing in vatSys: every event handler and timer that would touch
//    controlled sectors, VSCS, tags, FDRs or the scope checks Disabled first and returns.
//
// One-way by design: re-enabling mid-session would have to reconcile every subsystem with whatever
// the controller has done in plain vatSys meanwhile. Restarting vatSys brings OzServer back.
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

    // Lets the clean-up made while disabling - releasing this controller's sectors and sending the
    // last of the log - through after Disabled is set. Scoped to the async flow that opened it, so
    // no other request gets out alongside.
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
