using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Puts the plugin's own Arrival List (ArrivalListWindow) in place of vatSys's, and keeps every open
// one current from OzServer.
//
// vatSys opens its list (a SequenceWindow) from the main menu's Arrival List field and from the
// position's saved lists, and announces each one on MMI.ArrivalListWindowsChanged. While OzServer is
// available, each one is closed as it appears and ours opened for the same airport, in the same
// place. While it is not (off, or not on a supported server), vatSys's stays, and turning OzServer
// off hands every list back to vatSys.
//
// Each open list is read every RefreshInterval and at once when OzServer announces a change
// ("arrivals"). Which lists are open is reported every ViewingInterval and as one opens or closes,
// for the count in every controller's title bar.
//
// vatSys's list and its event are internal: found by reflection, and if they are not there in the
// shape expected, vatSys's own list is left alone and the reason logged.
sealed class ArrivalListSync
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    // The backend counts a viewer for three of these (VIEWER_TIMEOUT_SECONDS in routes/arrivals.ts).
    public static readonly TimeSpan ViewingInterval = TimeSpan.FromSeconds(10);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer? _refreshTimer;
    readonly System.Threading.Timer? _viewingTimer;

    // Ours, by airport. Touched on the UI thread only; _openAirports is the copy other threads read.
    readonly Dictionary<string, ArrivalListWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    volatile string[] _openAirports = Array.Empty<string>();
    string[] _lastReported = Array.Empty<string>();
    readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.OrdinalIgnoreCase);
    // Lists that have had at least one answer from OzServer.
    readonly ConcurrentDictionary<string, byte> _loaded = new(StringComparer.OrdinalIgnoreCase);
    int _reporting;

    PropertyInfo? _vatsysLists;
    PropertyInfo? _vatsysListAirport;
    MethodInfo? _unsubscribe;

    public ArrivalListSync()
    {
        try
        {
            if (!Install(out var reason))
            {
                ActionLog.Log("Arrivals", $"Arrival Lists left as vatSys's own: {reason}");
                return;
            }
        }
        catch (Exception ex)
        {
            ActionLog.Log("Arrivals", $"Arrival Lists left as vatSys's own: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        _refreshTimer = new System.Threading.Timer(_ => Tick(), null, RefreshInterval, RefreshInterval);
        _viewingTimer = new System.Threading.Timer(_ => _ = ReportViewingAsync(), null, ViewingInterval, ViewingInterval);

        OzServerEventStream.Shared.EventReceived += name =>
        {
            if (name == "arrivals")
                RefreshAll();
        };
        Network.Disconnected += (_, _) =>
        {
            // A new session reports from scratch; meanwhile the lists show nothing they cannot vouch for.
            _lastReported = Array.Empty<string>();
            RunOnUiThread(() =>
            {
                foreach (var window in _windows.Values)
                    window.Apply(null);
            });
        };
        OzServerMode.DisabledChanged += (_, _) => RunOnUiThread(HandBackToVatSys);
    }

    // From Plugin.OnFDRUpdate: a flight vatSys has just finished comes off every open list now,
    // not at the next refresh.
    public void OnFdrUpdate(FDP2.FDR updated)
    {
        if (updated.State != FDP2.FDR.FDRStates.STATE_FINISHED)
            return;
        RunOnUiThread(() =>
        {
            foreach (var window in _windows.Values)
                window.RemoveFinished();
        });
    }

    bool Install(out string reason)
    {
        const BindingFlags Statics = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        var vatsysAssembly = typeof(MMI).Assembly;

        _vatsysLists = typeof(MMI).GetProperty("ArrivalLists", Statics);
        var window = vatsysAssembly.GetType("vatsys.SequenceWindow", throwOnError: false);
        _vatsysListAirport = window?.GetProperty("Airport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_vatsysLists == null || !typeof(IEnumerable).IsAssignableFrom(_vatsysLists.PropertyType)
            || window == null || !typeof(Form).IsAssignableFrom(window) || _vatsysListAirport?.PropertyType != typeof(string))
        {
            reason = "MMI.ArrivalLists or SequenceWindow.Airport not found";
            return false;
        }

        // Not public, so EventInfo.AddEventHandler (which only takes a public add accessor) cannot
        // be used.
        var changed = typeof(MMI).GetEvent("ArrivalListWindowsChanged", Statics);
        var add = changed?.GetAddMethod(nonPublic: true);
        if (changed?.EventHandlerType != typeof(EventHandler) || add == null)
        {
            reason = "MMI.ArrivalListWindowsChanged not found";
            return false;
        }

        // Optional: stops vatSys polling its own server for a list it no longer shows.
        _unsubscribe = vatsysAssembly.GetType("vatsys.SequenceLadder", throwOnError: false)
            ?.GetMethod("UnsubscribeAirport", Statics, null, new[] { typeof(string) }, null);

        // Raised from inside vatSys's open and close, so the takeover waits for it to finish.
        add.Invoke(null, new object[] { new EventHandler((_, _) => RunLaterOnUiThread(TakeOverVatSysLists)) });

        reason = "";
        return true;
    }

    static bool OzServerServes =>
        !OzServerMode.Disabled && Network.IsConnected && NetworkServer.Current != null && NetworkIdentity.Current != null;

    void Tick()
    {
        // vatSys's lists can be open from before OzServer was available, a connection ago.
        RunOnUiThread(TakeOverVatSysLists);
        RefreshAll();
    }

    // UI thread.
    void TakeOverVatSysLists()
    {
        if (!OzServerServes)
            return;

        foreach (var list in VatSysLists())
        {
            string airport;
            try
            {
                airport = (_vatsysListAirport!.GetValue(list) as string)?.Trim().ToUpperInvariant() ?? "";
            }
            catch (Exception)
            {
                continue;
            }
            if (airport == "")
                continue;

            var bounds = list.Bounds;
            list.Close();
            try
            {
                _unsubscribe?.Invoke(null, new object[] { airport });
            }
            catch (Exception)
            {
                // Only saves vatSys some polling.
            }
            ActionLog.Log("Arrivals", $"Opened OzServer's {airport} Arrival List in place of vatSys's");
            Open(airport, bounds);
        }
    }

    Form[] VatSysLists()
    {
        try
        {
            return _vatsysLists?.GetValue(null) is IEnumerable lists
                ? lists.OfType<Form>().Where(f => !f.IsDisposed).ToArray()
                : Array.Empty<Form>();
        }
        catch (Exception)
        {
            return Array.Empty<Form>();
        }
    }

    // UI thread.
    void Open(string airport, Rectangle? bounds)
    {
        if (_windows.TryGetValue(airport, out var existing) && !existing.IsDisposed)
        {
            existing.BringToFront();
            return;
        }

        var window = new ArrivalListWindow(airport, this);
        if (bounds is { } place)
        {
            window.StartPosition = FormStartPosition.Manual;
            window.Bounds = place;
        }
        window.FormClosed += (_, _) =>
        {
            if (_windows.TryGetValue(airport, out var open) && open == window)
                _windows.Remove(airport);
            OpenAirportsChanged();
        };
        _windows[airport] = window;
        OpenAirportsChanged();

        if (Application.OpenForms["MainForm"] is Form mainForm)
            window.ShowWithPlacement(mainForm);
        else
            window.Show();
        window.BringToFront();

        _ = RefreshAsync(airport);
    }

    // UI thread. OzServer turned off: every list goes back to being vatSys's.
    void HandBackToVatSys()
    {
        if (!OzServerMode.Disabled)
            return;
        foreach (var window in _windows.Values.ToList())
        {
            var airport = window.Airport;
            window.Close();
            try
            {
                MMI.OpenArrivalListWindow(airport);
            }
            catch (Exception ex)
            {
                ActionLog.Log("Arrivals", $"Couldn't reopen vatSys's {airport} Arrival List: {ex.Message}");
            }
        }
    }

    void OpenAirportsChanged()
    {
        _openAirports = _windows.Keys.OrderBy(a => a, StringComparer.Ordinal).ToArray();
        _ = ReportViewingAsync();
    }

    void RefreshAll()
    {
        foreach (var airport in _openAirports)
            _ = RefreshAsync(airport);
    }

    async Task RefreshAsync(string airport)
    {
        if (!OzServerServes || !_refreshing.TryAdd(airport, 0))
            return;
        try
        {
            var list = await _api.GetArrivalListAsync(airport).ConfigureAwait(false);
            _loaded[airport] = 0;
            Show(airport, list);
        }
        catch (OzServerApiException ex)
        {
            // Already in the action log (OzServerApiClient). Not put in front of the controller: this
            // repeats every few seconds, and an outage is reported elsewhere. A brief failure leaves
            // the list as it was; a missing route (an API without Arrival Lists) or a list that has
            // never loaded says so in the title instead of passing for an empty list.
            if (ex.StatusCode == 404 || !_loaded.ContainsKey(airport))
                RunOnUiThread(() =>
                {
                    if (_windows.TryGetValue(airport, out var window))
                        window.ShowUnavailable();
                });
        }
        finally
        {
            _refreshing.TryRemove(airport, out _);
        }
    }

    void Show(string airport, OzServerArrivalListDto list) => RunOnUiThread(() =>
    {
        if (_windows.TryGetValue(airport, out var window))
            window.Apply(list);
    });

    // A strip dropped in a new place. A refusal is put in front of the controller - the strip has
    // already moved on their screen - and the list read again to put it back.
    public async Task MoveAsync(string airport, string callsign, int seqIndex)
    {
        try
        {
            var list = await _api.SetArrivalSequenceAsync(airport, callsign, seqIndex).ConfigureAwait(false);
            ActionLog.Log("Arrivals", $"Moved {callsign} in the {airport} Arrival List", new
            {
                action = "arrival_sequence",
                fdr_callsign = callsign,
                ades = airport,
                seq_index = seqIndex
            });
            Show(airport, list);
        }
        catch (OzServerApiException ex)
        {
            ActionLog.Log("Arrivals", $"Couldn't move {callsign} in the {airport} Arrival List: {ex.Message}", new
            {
                action = "arrival_sequence_failed",
                fdr_callsign = callsign,
                ades = airport,
                seq_index = seqIndex,
                status = ex.StatusCode
            });
            OzErrors.Add(new Exception(ex.StatusCode == 403
                ? ex.Message
                : $"Couldn't move {callsign} in the {airport} Arrival List: {ex.Message}", ex), "OzServer");
            await RefreshAsync(airport).ConfigureAwait(false);
        }
    }

    async Task ReportViewingAsync()
    {
        if (!OzServerServes || Interlocked.Exchange(ref _reporting, 1) == 1)
            return;
        try
        {
            var airports = _openAirports;
            // Nothing open now or at the last report: nothing to tell.
            if (airports.Length == 0 && _lastReported.Length == 0)
                return;

            await _api.ReportArrivalListsOpenAsync(airports).ConfigureAwait(false);
            _lastReported = airports;
        }
        catch (OzServerApiException)
        {
            // Already in the action log; the next report tries again.
        }
        catch (Exception ex)
        {
            ActionLog.Log("Arrivals", $"Couldn't report the open Arrival Lists: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _reporting, 0);
        }
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }

    // Always queued, even from the UI thread: for work that must not run inside the vatSys call
    // that raised the event.
    static void RunLaterOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.IsHandleCreated)
            mainForm.BeginInvoke(UiThread.Guard(action));
    }
}
