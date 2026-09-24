using System;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Gives this controller's sectors back the moment they deliberately leave, and stays silent when
// they didn't.
//
// That distinction is the whole feature, and it is one the vatSys SDK does not make for us:
// Network.Disconnected is a bare EventHandler with no reason attached, and both the clean path and
// the network-error path (Network::ProcessNetworkError / VATSIM_NetworkDisconnected) raise the same
// event. Network's own Error event, which would separate them, is internal to vatSys. So intent has
// to be inferred from the two places a controller can actually express it:
//
//   - pressing Disconnect, which can only happen with the Connect window open - so a disconnect
//     arriving while that window is on screen was asked for, and one arriving with no Connect
//     window in sight was not;
//   - closing vatSys while still connected and answering Yes to the "you're still connected,
//     confirm you wish to exit?" prompt MainForm.mainForm_FormClosing shows. That method sets
//     BaseForm.AppClosing = true (public get, so readable here) right before it calls
//     Network.Instance.Disconnect() - the same flag its own recursive Close() call checks to skip
//     re-prompting. Network.Disconnected fires synchronously, nested inside that Disconnect() call,
//     so AppClosing is already true by the time it reaches us.
//
//     This used to hook MainForm.FormClosing directly instead. That doesn't work: vatSys wires its
//     own FormClosing handler in MainForm's constructor, which runs long before this class ever
//     gets a chance to subscribe, so vatSys's handler is always first in that event's invocation
//     list and this class's was always second. By the time a second subscriber gets a turn,
//     mainForm_FormClosing has already run to completion - including the Disconnect() call - so
//     Network.IsConnected was already false and the release never fired. Worse, on a *cancelled*
//     close (the confirm prompt answered No), mainForm_FormClosing leaves e.Cancel = true and
//     returns without ever disconnecting - so the second FormClosing subscriber still ran, saw
//     Network.IsConnected still true, and released the controller's sectors anyway, on a close they
//     had just said not to do. Keying off AppClosing instead of FormClosing sidesteps both bugs:
//     Network.Disconnected (and therefore this class) simply never fires at all unless
//     Network.Instance.Disconnect() actually ran, which only happens once the exit is genuinely
//     confirmed.
//
// Everything else is treated as ungraceful, deliberately: silence is what earns the retention
// window on the backend (SectorOwnership::DISCONNECT_GRACE_MINUTES), so guessing "graceful" wrongly
// loses a controller's sectors for real, while guessing "ungraceful" wrongly only means they are
// held for five minutes before being released anyway. The costs are not symmetric, so the default
// is the cheap mistake.
public class GracefulDisconnectReleaser
{
    // vatsys.ConnectWindow sets this as its form Name, which is what Application.OpenForms is keyed
    // by. The type itself is private to vatSys, so it can only be found by name.
    const string ConnectWindowName = "ConnectWindow";

    // Long enough for one POST on a normal connection, short enough that a wedged or unreachable
    // backend cannot noticeably delay vatSys shutting down.
    static readonly TimeSpan ShutdownReleaseTimeout = TimeSpan.FromSeconds(3);

    readonly OzServerApiClient _api = new();

    public GracefulDisconnectReleaser()
    {
        // vatSys never installs a SynchronizationContext on its FSDSession, so Network.Connected/
        // Disconnected genuinely fire off the UI thread for a real network drop (only a
        // user-initiated disconnect - pressing the button, or confirming Close - happens to already
        // be on it). RunOnUiThread below is what makes the Application.OpenForms/Form reads in
        // OnDisconnected safe either way.
        Network.Disconnected += (_, _) => RunOnUiThread(OnDisconnected);
    }

    void OnDisconnected()
    {
        // Still on screen means they are looking at it, which means they pressed the button on it.
        // A dropped connection does not open this window.
        if (IsConnectWindowOpen())
        {
            _ = ReleaseAsync();
            return;
        }

        // See the class comment: only true once MainForm's own FormClosing handler has committed to
        // actually closing, which only happens after its "still connected, confirm exit?" prompt is
        // answered Yes (or there was nothing to confirm).
        if (BaseForm.AppClosing)
            ReleaseBlocking();
    }

    static bool IsConnectWindowOpen() =>
        Application.OpenForms[ConnectWindowName] is { IsDisposed: false, Visible: true };

    // Blocking, unlike ReleaseAsync below: this runs synchronously inside
    // Network.Instance.Disconnect(), itself called from MainForm's FormClosing handler before its
    // own first await - so vatSys is already mid-shutdown by the time this fires, and a
    // fire-and-forget POST here would just be killed mid-flight racing the process actually
    // exiting, which would look to the backend exactly like a crash and hold the sectors for five
    // minutes anyway. Safe to block here specifically: nothing is waiting on this thread to pump
    // messages yet, and OzServerApiClient awaits with ConfigureAwait(false) throughout.
    void ReleaseBlocking()
    {
        try
        {
            _api.ReleaseAllSectorsAsync().Wait(ShutdownReleaseTimeout);
        }
        catch (Exception ex)
        {
            // Never block or break shutdown over this. Failing here just falls back to the
            // ungraceful path, which is a five minute wait, not a lost sector.
            Errors.Add(new Exception($"Couldn't release sectors on exit: {ex.Message}", ex), "OzServer");
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
            Errors.Add(new Exception($"Couldn't release sectors on disconnect: {ex.Message}", ex), "OzServer");
        }
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
