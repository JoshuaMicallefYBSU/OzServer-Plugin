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
//   - closing vatSys while still connected (the confirmation prompt vatSys shows), caught on
//     MainForm.FormClosing;
//   - pressing Disconnect, which can only happen with the Connect window open - so a disconnect
//     arriving while that window is on screen was asked for, and one arriving with no Connect
//     window in sight was not.
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
    bool _hookedMainForm;

    public GracefulDisconnectReleaser()
    {
        Network.Disconnected += (_, _) => OnDisconnected();

        // MainForm does not exist yet when the plugin is constructed - same deferral Plugin uses for
        // its own menu wiring.
        EventHandler? hookMainForm = null;
        hookMainForm = (_, _) =>
        {
            if (_hookedMainForm || Application.OpenForms["MainForm"] is not Form mainForm)
                return;

            _hookedMainForm = true;
            Application.Idle -= hookMainForm;
            mainForm.FormClosing += (_, _) => ReleaseOnShutdown();
        };
        Application.Idle += hookMainForm;
    }

    void OnDisconnected()
    {
        // Still on screen means they are looking at it, which means they pressed the button on it.
        // A dropped connection does not open this window.
        if (!IsConnectWindowOpen())
            return;

        _ = ReleaseAsync();
    }

    static bool IsConnectWindowOpen() =>
        Application.OpenForms[ConnectWindowName] is { IsDisposed: false, Visible: true };

    // Closing vatSys while connected is unambiguous - there is no reading of it where the controller
    // means to keep the sectors.
    void ReleaseOnShutdown()
    {
        if (!Network.IsConnected)
            return;

        try
        {
            // Blocking, unlike every other call in this plugin: the process is about to end, and a
            // fire-and-forget POST would simply be killed mid-flight, which would look to the
            // backend exactly like a crash and hold the sectors for five minutes. Safe to Wait on
            // here specifically because OzServerApiClient awaits with ConfigureAwait(false)
            // throughout, so nothing is waiting on this thread to pump messages.
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
}
