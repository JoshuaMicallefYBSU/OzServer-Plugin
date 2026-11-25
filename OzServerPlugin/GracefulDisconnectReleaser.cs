using System;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Releases this controller's sectors when they deliberately leave, and sends nothing when the
// connection dropped.
//
// vatSys does not make that distinction: Network.Disconnected carries no reason, the clean path and
// the network-error path both raise it, and Network's Error event is internal. Intent is inferred
// from the two ways a controller can express it:
//
//   - Pressing Disconnect, which requires the Connect window to be open. A disconnect arriving
//     while that window is on screen was asked for.
//   - Closing vatSys while connected and confirming the exit prompt. MainForm.mainForm_FormClosing
//     sets BaseForm.AppClosing = true just before it calls Network.Instance.Disconnect(), and
//     Network.Disconnected fires synchronously inside that call, so AppClosing is already true
//     when the handler here runs.
//
// AppClosing is read rather than subscribing to MainForm.FormClosing. vatSys's own FormClosing
// handler always runs first, so a later subscriber sees the connection already closed and never
// releases; and on a cancelled close it still runs, while connected, and would release sectors the
// controller chose to keep.
//
// Everything else is treated as a drop. Silence is what makes the backend hold the sectors for its
// resume window (RESUME_WINDOW_MINUTES in the API, five minutes by default), so wrongly guessing
// "deliberate" loses a controller's sectors, while wrongly guessing "drop" only delays their
// release.
public class GracefulDisconnectReleaser
{
    // The form Name vatsys.ConnectWindow gives itself, which Application.OpenForms is keyed by. The
    // type is private to vatSys, so it can only be found by name.
    const string ConnectWindowName = "ConnectWindow";

    // Long enough for one POST on a normal connection, short enough that an unreachable backend
    // does not noticeably delay vatSys shutting down.
    static readonly TimeSpan ShutdownReleaseTimeout = TimeSpan.FromSeconds(3);

    readonly OzServerApiClient _api = new();

    // Set once the network has confirmed this session as a controller, and taken back by the
    // disconnect that ends it. vatSys raises Network.Disconnected for a logon that never got that
    // far as well - a refused password, "callsign in use", a server that cannot be reached
    // (Network.ProcessNetworkError) - and always with the Connect window open, since that is where
    // Connect was pressed. Read as a deliberate disconnect, a failed attempt to get back on after a
    // drop would release the very sectors OzServer is holding for this controller.
    volatile bool _established;

    public GracefulDisconnectReleaser()
    {
        // Only ever set here: Network.Disconnect clears ValidATC, and raises this, before it raises
        // Disconnected.
        Network.ValidATCChanged += (_, _) =>
        {
            if (Network.ValidATC)
                _established = true;
        };

        // Network.Disconnected fires off the UI thread for a network drop (vatSys installs no
        // SynchronizationContext on its FSDSession) and on it for a user-initiated disconnect.
        // OnDisconnected reads Application.OpenForms, so it is marshalled either way. The latch is
        // read and reset before the marshal, so it cannot pick up the next session's.
        Network.Disconnected += (_, _) =>
        {
            var established = _established;
            _established = false;
            RunOnUiThread(() => OnDisconnected(established));
        };
    }

    void OnDisconnected(bool established)
    {
        if (OzServerMode.Disabled)
            return;

        // Nothing is claimed on a server OzServer does not operate on (LocalHost, an unrecognised
        // SweatBox), and the release would fail before it was sent.
        if (NetworkServer.Current == null)
            return;

        // True only once MainForm's FormClosing handler has committed to closing - see the class
        // comment. Tested first: with the Connect window also open, the fire-and-forget release
        // below could be killed with the process.
        if (BaseForm.AppClosing)
        {
            ReleaseBlocking();
            return;
        }

        // The Connect window is on screen, so the controller pressed Disconnect on it. A dropped
        // connection does not open this window. Only for a session that was established - see
        // _established.
        if (established && IsConnectWindowOpen())
            _ = ReleaseAsync();
    }

    static bool IsConnectWindowOpen() =>
        Application.OpenForms[ConnectWindowName] is { IsDisposed: false, Visible: true };

    // Blocking, unlike ReleaseAsync: this runs inside Network.Instance.Disconnect(), called from
    // MainForm's FormClosing handler, so vatSys is already shutting down and a fire-and-forget POST
    // could be killed with the process - which the backend would see as a dropped connection.
    // Blocking is safe because OzServerApiClient awaits with ConfigureAwait(false) throughout, so
    // the request does not need this thread to complete.
    void ReleaseBlocking()
    {
        if (OzServerMode.Disabled)
            return;

        try
        {
            _api.ReleaseAllSectorsAsync().Wait(ShutdownReleaseTimeout);
        }
        catch (Exception ex)
        {
            // Never break shutdown over this. A failure falls back to the dropped-connection path:
            // the sectors are held for the resume window, not lost.
            OzErrors.Add(new Exception($"Couldn't release sectors on exit: {ex.Message}", ex), "OzServer");
        }
    }

    async System.Threading.Tasks.Task ReleaseAsync()
    {
        try
        {
            await _api.ReleaseAllSectorsAsync();
        }
        catch (Exception ex)
        {
            OzErrors.Add(new Exception($"Couldn't release sectors on disconnect: {ex.Message}", ex), "OzServer");
        }
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
