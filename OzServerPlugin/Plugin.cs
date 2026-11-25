using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace OzServerPlugin;

[Export(typeof(IPlugin))]
public class Plugin : IPlugin
{
    public const string Name = "OzServer Plugin";

    // Label and designer name of vatSys's built-in Settings > Sectors... entry, which the OzServer
    // entry is placed beneath.
    const string SectorsMenuItemText = "Sectors...";
    const string SectorsMenuItemName = "sectorsToolStripMenuItem";
    // What the hidden built-in entry is renamed to once our own entry takes over SectorsMenuItemName.
    const string HiddenSectorsMenuItemName = "sectorsToolStripMenuItem_hiddenByOzServer";

    string IPlugin.Name => Name;

    OzServerSectorsWindow? _sectorsWindow;
    // Constructed at load, unlike the lazily created windows above: they must work whether or not
    // the Sectors window is ever opened. AfvSectorClaimer turns a VSCS transmit press into an
    // MMI.SectorsControlled change, and the tracker answers every change to that list by claiming
    // on OzServer.
    readonly AfvSectorClaimer _afvSectorClaimer;
    readonly OzServerOwnershipTracker _ownershipTracker;
    // Keeps OzServer's flight_data_records current. Disconnect recovery is built on the
    // controlling_cid and current_sector it pushes.
    readonly FdrSync _fdrSync;
    // Restores tags the backend confirms were this controller's moments ago.
    readonly TagResumeRecovery _tagResumeRecovery;
    // Activates and positions, on reconnect, the flights OzServer still holds that vatSys has not.
    readonly FdrResumeActivation _fdrResumeActivation;
    // Puts the tracking controller's "C" on everyone else's copy of the label - see LabelStateMirror.
    readonly LabelStateMirror _labelStateMirror;
    // Couples an activated flight plan to the radar track carrying its callsign when vatSys will not.
    readonly CoupleAssist _coupleAssist = new();
    // Moves aircraft with their sector when one changes hands on OzServer.
    readonly SectorTagHandoff _sectorTagHandoff;
    // Previews which aircraft a staged or pending sector request would bring with it.
    readonly PendingSectorGhosts _pendingSectorGhosts;
    readonly AtisSync _atisSync;
    readonly BadVectorsAtisSync _badVectorsAtisSync;
    // Messages sent from the website's /console - shown in a popup, answered when allowed.
    readonly DeveloperMessages _developerMessages;
    // vatSys's Arrival Lists, served from OzServer rather than vatSys's own sequence server.
    readonly ArrivalListSync _arrivalListSync;
    readonly ExternalControl _externalControl;
    SelcalDebugWindow? _selcalDebugWindow;
    // Driven by Network's events: a position is handed back whether or not the Sectors window was
    // ever opened.
    readonly PrimaryPositionWatcher _primaryPositionWatcher;
    // Event-driven too, and needed from plugin load: a disconnect can come before the Sectors
    // window is ever opened.
    readonly GracefulDisconnectReleaser _gracefulDisconnectReleaser;
    readonly ObserverPositionMirror _observerPositionMirror;
    readonly RequestedSectorOverlay _requestedSectorOverlay;
    // Display levels 998-999 on FMP positions. Event-driven, so it must exist before the first
    // position is selected.
    readonly FmpLevelFilter _fmpLevelFilter;
    // Timer-driven. Never touches the running session, only what is on disk for the next one -
    // see PluginUpdater.
    readonly PluginUpdater _updater;
    // Copies this session's decisions to OzServer so every controller's log can be read together.
    readonly ClientLogForwarder _clientLogForwarder;
    readonly UiStallWatchdog _uiStallWatchdog;

    // Incoming requests waiting on this controller, driving the Settings header's flash.
    int _pendingRequests;

    // Request groups already announced, so a group sounds once rather than on every refresh while
    // it is unanswered. Pruned to what is still pending, so a request withdrawn and made again is
    // announced again.
    readonly HashSet<string> _announcedRequestGroups = new(StringComparer.OrdinalIgnoreCase);
    ToolStripMenuItem? _settingsMenuHeader;
    ToolStripItem? _ozServerSectorsItem;
    // vatSys's own Settings > Sectors... entry, hidden while OzServer runs and shown again when it is
    // disabled.
    ToolStripItem? _builtInSectorsItem;
    // Every menu entry this plugin adds, hidden when OzServer is disabled - apart from the Disable
    // entry itself, which stays to say so.
    readonly List<ToolStripItem> _ozServerMenuItems = new();
    ToolStripMenuItem? _disableItem;

    // Set on a second installed copy, which does nothing for the session - see DuplicateInstallGuard.
    readonly bool _inactive;

    // An inactive copy returns before assigning the fields below; nothing reads them when it is.
#pragma warning disable CS8618
    public Plugin()
    {
        // Before anything else: a second copy must not start a single timer or touch vatSys.
        if (!DuplicateInstallGuard.TryClaim())
        {
            _inactive = true;
            return;
        }

        // First, so every decision logged below is forwarded from the outset.
        _clientLogForwarder = new ClientLogForwarder();
        // Straight after it, so an error raised by anything constructed below is forwarded too.
        ErrorReporter.Install();
        // Logs when vatSys's UI thread stalls, whatever is holding it - see UiStallWatchdog.
        _uiStallWatchdog = new UiStallWatchdog();
        // Before anything that writes MMI.SectorsControlled, so its first snapshot predates any
        // change this plugin makes to the list.
        MmiSectorWatch.Install();
        _ownershipTracker = new OzServerOwnershipTracker();
        // As early as possible: its VSCSFrequenciesChanged handler has to run before VatpacPlugin's.
        VscsLines.Initialise(_ownershipTracker);
        ExtensionLimit.Initialise();
        // After the tracker: its connect-time grant keeps the tracker's RecentlyResumed sectors.
        _afvSectorClaimer = new AfvSectorClaimer(_ownershipTracker);
        // A request sent from a VSCS transmit press opens the Sectors window on it.
        _afvSectorClaimer.SectorRequested += sectorName =>
        {
            if (OzServerMode.Disabled)
                return;
            OpenSectorsWindow();
            _sectorsWindow?.HighlightRequest(sectorName);
        };
        _fdrSync = new FdrSync(_ownershipTracker);
        // After the tracker, whose TagsResumed it listens to.
        _tagResumeRecovery = new TagResumeRecovery(_ownershipTracker);
        // A tag vatSys let go without the controller dropping it - its pilot disconnected, or it was
        // lost moments after a restore - is taken back.
        _fdrSync.TagLost += lost => _tagResumeRecovery.Retake(lost);
        _fdrResumeActivation = new FdrResumeActivation();
        _labelStateMirror = new LabelStateMirror();
        // After the tracker, whose OwnershipChanged tells it a sector moved.
        _sectorTagHandoff = new SectorTagHandoff(_ownershipTracker, _fdrSync);
        // After the tracker, whose own open requests it reads alongside the window's staged set.
        _pendingSectorGhosts = new PendingSectorGhosts(_ownershipTracker);
        _atisSync = new AtisSync();
        _badVectorsAtisSync = new BadVectorsAtisSync();
        _developerMessages = new DeveloperMessages();
        // After the tracker, which it releases through.
        _primaryPositionWatcher = new PrimaryPositionWatcher(_ownershipTracker);
        _gracefulDisconnectReleaser = new GracefulDisconnectReleaser();
        // After the tracker, whose ControlledByOthers it mirrors.
        _observerPositionMirror = new ObserverPositionMirror(_ownershipTracker);
        // After the tracker, whose incoming requests it outlines on the scope.
        _requestedSectorOverlay = new RequestedSectorOverlay(_ownershipTracker);
        // Depends on nothing constructed above: it asks the OzServer API which release to install,
        // falls back to GitHub, and writes files beside this assembly.
        _updater = new PluginUpdater();

        _fmpLevelFilter = new FmpLevelFilter();
        // Patches vatSys's own SELCAL window and AFV's tone table once, at load, so the window
        // accepts SELCAL32 codes. Nothing to hold: there is no state beyond the patch itself.
        Selcal32.Apply();
        // Swaps the HttpClient vatSys's Arrival Lists poll through, once, at load. Until a session
        // on a supported server starts, every request still goes to vatSys's own server.
        _arrivalListSync = new ArrivalListSync();
        // Reports the controllers vatSys can see, so OzServer can hold the airspace of those without
        // the plugin.
        _externalControl = new ExternalControl();

        var sectorsMenuItem = new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Settings,
            // Plain "Sectors": it takes the place of vatSys's own Sectors... entry, which is hidden
            // while OzServer runs (see repositionUnderSectors), and opens the OzServer Sectors
            // window. Not "Sectors..." - the built-in entry is found by that text when its
            // designer name cannot be.
            new ToolStripMenuItem("Sectors"));
        sectorsMenuItem.Item.Click += (_, _) => OpenSectorsWindow();
        MMI.AddCustomMenuItem(sectorsMenuItem);

        // An incoming request is surfaced the way vatSys's "Messages" menu surfaces an unread
        // message: the Settings header flashes until Settings is opened, and OzServer Sectors
        // carries a steady "[N]" badge until the request is dealt with. The window's title bar
        // flashes too when it is open but not focused.
        //
        // Driven through Checked and Tag (see RefreshRequestFlash): vatSys's MenuRenderer computes
        // every item's colours itself and never reads ForeColor or BackColor.
        _ownershipTracker.IncomingRequestsChanged += (_, requests) =>
        {
            void Apply()
            {
                if (OzServerMode.Disabled)
                    return;
                _pendingRequests = requests.Count;
                RefreshRequestFlash();

                if (_sectorsWindow is { IsDisposed: false } window)
                    window.FlashTitleBar = _pendingRequests > 0 && !window.ContainsFocus;

                AnnounceNewRequestGroups(requests);
            }

            // The poll this comes from runs on a background timer thread.
            if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
                mainForm.BeginInvoke(UiThread.Guard(Apply));
            else
                Apply();
        };

        // Under Tools, not Settings: switching OzServer off acts on the running session, like
        // vatSys's own Disable Filters and Disable GRIB Winds. vatSys appends plugin Tools items
        // at the bottom of that menu.
        _disableItem = new ToolStripMenuItem("Disable OzServer");
        _disableItem.Click += (_, _) => ConfirmDisable();
        MMI.AddCustomMenuItem(new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Tools,
            _disableItem));

        // A debug window for SELCAL32 (Selcal32.cs). Not one of _ozServerMenuItems: it has nothing to
        // do with OzServer, and stays when OzServer is turned off.
        var selcalDebugItem = new ToolStripMenuItem("SELCAL Debug");
        selcalDebugItem.Click += (_, _) =>
        {
            if (_selcalDebugWindow == null || _selcalDebugWindow.IsDisposed)
                _selcalDebugWindow = new SelcalDebugWindow();
            ShowWindow(_selcalDebugWindow);
        };
        MMI.AddCustomMenuItem(new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Tools,
            selcalDebugItem));

        // Contained: an exception leaving this constructor stops vatSys loading any plugin, and
        // the profile. That includes a vatSys build without a member AddHowToButton names, which
        // is raised as AddHowToButton is compiled - hence a method of its own that is never
        // inlined here.
        try
        {
            AddHowToButton();
        }
        catch (Exception ex)
        {
            ActionLog.Log("Ui", $"The OzServer How-To button couldn't be added: {ex.Message}",
                new { action = "how_to_button_failed" });
        }

        _ozServerMenuItems.Add(sectorsMenuItem.Item);

        // vatSys inserts Settings-category plugin items at the bottom of the Settings menu, not
        // beside a built-in entry, and the menu does not exist yet. The move is deferred to
        // Application.Idle, which fires until vatSys has built the menu with the item in it.
        EventHandler? repositionUnderSectors = null;
        repositionUnderSectors = (_, _) =>
        {
            if (sectorsMenuItem.Item.OwnerItem is not ToolStripMenuItem settingsMenu)
                return;

            Application.Idle -= repositionUnderSectors;

            // Captured before anything that can return early: the request flash and badge
            // (RefreshRequestFlash) need only these two, not the built-in entry. Captured after it,
            // as they were, a vatSys build whose Sectors entry was not found by name or text left
            // the flash with nothing to paint on for the whole session.
            _settingsMenuHeader = settingsMenu;
            _ozServerSectorsItem = sectorsMenuItem.Item;
            // Opening Settings stops the header's flash, as vatSys's Messages header stops on its
            // click. The OzServer Sectors badge stays until the request is dealt with.
            settingsMenu.DropDownOpened += (_, _) => settingsMenu.Checked = false;
            // A request that arrived before the menu existed had nothing to paint on, and
            // IncomingRequestsChanged fires again only when the set changes.
            RefreshRequestFlash();

            // Name first, Text as a fallback: the designer name is less likely to change between
            // vatSys builds than the displayed label.
            var sectorsItem = settingsMenu.DropDownItems.OfType<ToolStripItem>()
                .FirstOrDefault(i => i.Name == SectorsMenuItemName)
                ?? settingsMenu.DropDownItems.OfType<ToolStripItem>()
                    .FirstOrDefault(i => i.Text == SectorsMenuItemText);
            if (sectorsItem == null)
            {
                ActionLog.Log("Ui", "vatSys's own Sectors... entry was not found in Settings - OzServer Sectors left where vatSys put it",
                    new { action = "settings_sectors_entry_missing" });
                return;
            }

            var insertAt = settingsMenu.DropDownItems.IndexOf(sectorsItem) + 1;
            settingsMenu.DropDownItems.Remove(sectorsMenuItem.Item);
            settingsMenu.DropDownItems.Insert(insertAt, sectorsMenuItem.Item);

            _builtInSectorsItem = sectorsItem;

            // OzServer Sectors replaces the built-in Sectors window rather than sitting beside it:
            // both write MMI.SectorsControlled, but only the plugin's window also tells OzServer.
            // The built-in entry is hidden so there are not two windows that look equivalent.
            //
            // Visible=false, not Remove: MainForm keeps its field and click handler, vatSys can
            // still open the window itself (MMI.SectorsWindow), and DisableNow shows the entry
            // again.
            sectorsItem.Visible = false;

            // Other plugins reach the Sectors window by finding this item by its designer name and
            // calling PerformClick - Eurocat UI's Airspace button does exactly that. PerformClick is
            // a silent no-op on an item that isn't Available, so with the built-in entry hidden that
            // click did nothing at all, and even if it had fired it would have opened the built-in
            // window rather than this one. Handing the name over to our own entry routes every such
            // lookup here instead.
            //
            // Safe for vatSys itself: its only use of the "sectorsToolStripMenuItem" string is the
            // designer's own Name assignment (MainForm keeps the item in a field, and MenuRenderer
            // keys only off the Windows/Messages names), unlike those items which it looks up by name.
            sectorsItem.Name = HiddenSectorsMenuItemName;
            sectorsMenuItem.Item.Name = SectorsMenuItemName;
        };
        Application.Idle += repositionUnderSectors;
    }
#pragma warning restore CS8618

    // Sounds the notification for request groups not announced before, and opens the Sectors window,
    // where a request is answered and the airspace involved is shaded on the scope
    // (RequestedSectorOverlay). Opened rather than left to be found: ML-HYD_CTR's flash went
    // unnoticed when ML-ASP_CTR asked for ASP.
    //
    // Requests arrive by poll and by SSE, so the same pending request is seen many times; only a
    // group not announced before counts as an arrival. Everything another controller asked for in
    // one Apply shares a group id, so three sectors requested together are one notification.
    void AnnounceNewRequestGroups(IReadOnlyList<OzServerSectorOwnershipRequestDto> requests)
    {
        var groups = requests
            .Where(request => request.RejectedAt == null)
            .GroupBy(request => string.IsNullOrEmpty(request.GroupId) ? $"request-{request.Id}" : request.GroupId,
                     StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToList();

        // Anything no longer pending is forgotten, so the same sectors requested again later
        // sound again.
        _announcedRequestGroups.IntersectWith(groups);

        var arrived = groups.Count(group => _announcedRequestGroups.Add(group));
        if (arrived == 0)
            return;

        // One sound however many groups arrived together.
        NotificationSound.PlayRequestArrived();

        // Brought up whether it was closed, hidden or behind another window. Not focused when it
        // is already open: ShowWindow only shows and raises it.
        OpenSectorsWindow();

        ActionLog.Log("Requests", $"{arrived} new request group(s) announced - Sectors window opened");
    }

    // Shows a popup the way the plugin's notices and prompts are shown: non-modal, owned by the
    // main form.
    static void ShowPopup(Form popup)
    {
        if (Application.OpenForms["MainForm"] is Form mainForm)
            popup.Show(mainForm);
        else
            popup.Show();

        popup.BringToFront();
    }

    void RefreshRequestFlash()
    {
        var pending = _pendingRequests > 0;

        // A positive int Tag is what MenuRenderer renders as a coloured "[N]" badge, as vatSys
        // does for an unread entry under Messages. MenuRenderer also flashes only an item with a
        // tagged child, which is what lets Checked below flash the Settings header.
        if (_ozServerSectorsItem != null)
            _ozServerSectorsItem.Tag = pending ? _pendingRequests : null;

        // Checked is the flag MenuRenderer's flash timer keys off. Opening Settings clears it
        // before the request is gone (see DropDownOpened in the constructor), as Messages stops
        // flashing on its click.
        if (_settingsMenuHeader != null)
            _settingsMenuHeader.Checked = pending;
    }

    // Tools > Disable OzServer. One-way for the session (see OzServerMode), so it asks first.
    void ConfirmDisable()
    {
        if (OzServerMode.Disabled)
            return;

        var prompt = new SectorConflictPromptWindow(
            "Disable OzServer for the rest of this session?" + Environment.NewLine + Environment.NewLine
            + "Your sectors are released on OzServer, nothing more is sent to it, and the plugin stops "
            + "changing anything in vatSys - sectors, VSCS, tags and flight plans are left entirely to you. "
            + "vatSys's own Sectors window comes back under Settings." + Environment.NewLine + Environment.NewLine
            + "Restart vatSys to use OzServer again.",
            "Disable OzServer", "Disable", "Cancel");
        prompt.FormClosed += (_, _) =>
        {
            if (prompt.Answer)
                DisableNow();
        };
        ShowPopup(prompt);
    }

    // Shuts OzServer out of the session. Runs on the UI thread, as the menu click and the prompt
    // both do.
    void DisableNow()
    {
        if (OzServerMode.Disabled)
            return;

        var sectorsWindowWasOpen = _sectorsWindow is { IsDisposed: false, Visible: true };
        ActionLog.Log("OzServer", "Disabled for this session by the controller", new
        {
            action = "ozserver_disabled",
            callsign = Network.Callsign,
            connected = Network.IsConnected
        });

        // From here every handler and timer in the plugin is a no-op, and no request leaves it.
        OzServerMode.Disable();

        // The OzServer windows and indicators go away.
        _sectorsWindow?.Hide();
        _pendingRequests = 0;
        RefreshRequestFlash();
        if (_settingsMenuHeader != null)
            _settingsMenuHeader.Checked = false;

        // Anything the plugin had put on the scope comes off, and anything it changed is handed back.
        Isolate("Disable", () => _requestedSectorOverlay.Clear());
        Isolate("Disable", () => _pendingSectorGhosts.Reassert());   // releases painted ghosts
        Isolate("Disable", () => _fmpLevelFilter.Shutdown());

        // The menu: vatSys's own Sectors entry back, the OzServer ones gone, and this one saying so.
        foreach (var item in _ozServerMenuItems)
            item.Visible = false;
        if (_builtInSectorsItem != null)
        {
            _builtInSectorsItem.Visible = true;
            // Hand the designer name back too, or other plugins that click the Sectors entry by
            // name (Eurocat UI's Airspace button) would keep finding our now-hidden entry and get
            // nothing.
            if (_ozServerSectorsItem != null)
                _ozServerSectorsItem.Name = string.Empty;
            _builtInSectorsItem.Name = SectorsMenuItemName;
        }
        if (_disableItem != null)
        {
            _disableItem.Text = "OzServer disabled (restart vatSys to re-enable)";
            _disableItem.Enabled = false;
        }

        // Someone working from the OzServer Sectors window is left with vatSys's in its place.
        if (sectorsWindowWasOpen)
            _builtInSectorsItem?.PerformClick();

        _ = FinishDisablingAsync();
    }

    // The last calls OzServer gets from this session, let through by AllowFinalCalls: this
    // controller's sectors released, so nobody else is left blocked by a controller who has
    // stopped reporting, and the log sent.
    async System.Threading.Tasks.Task FinishDisablingAsync()
    {
        var released = false;
        using (OzServerMode.AllowFinalCalls())
        {
            try
            {
                if (Network.IsConnected && NetworkServer.Current != null)
                {
                    await new OzServerApiClient().ReleaseAllSectorsAsync();
                    released = true;
                }
            }
            catch (Exception ex)
            {
                ActionLog.Log("OzServer", $"Couldn't release sectors while disabling: {ex.Message}");
            }

            if (released)
                ActionLog.Log("OzServer", "Released all sectors on OzServer - disabled for this session", new { action = "ozserver_disabled_released" });

            try
            {
                await _clientLogForwarder.FlushNowAsync();
            }
            catch (Exception)
            {
                // Nothing else will be sent anyway.
            }
        }

        var notice = new SectorNoticeWindow(
            "OzServer is disabled for this session." + Environment.NewLine + Environment.NewLine
            + (released ? "Your sectors have been released on OzServer. " : "")
            + "Use Settings > Sectors... for vatSys's own Sectors window." + Environment.NewLine + Environment.NewLine
            + "Restart vatSys to use OzServer again.",
            "OzServer disabled");
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(() => ShowPopup(notice)));
        else
            ShowPopup(notice);
    }

    // "OzServer How-To" under vatSys's Info menu, opening the controller guide in vatSys's own browser
    // window - beside the profile's own browser entries, which open the same kind of window. Stays
    // when OzServer is disabled: the guide covers that too, and it changes nothing.
    //
    // It is put into the Info menu by hand. vatSys has an Info category for plugin entries
    // (CustomToolStripMenuItemCategory.Info), but MainForm.LoadPluginMenuItem looks for a separator
    // named "infoSeparator" in the Settings menu, finds none, and adds nothing. So the entry waits
    // for the main window's menu to exist and is appended to Info's dropdown, after the profile's
    // entries. The separator above those is hidden when the profile has none, and is shown so that
    // this entry does not run on from "AIS Display".
    //
    // If this vatSys has no menu by that name, the entry goes under Tools through the plugin API
    // instead, so the guide can always be reached.
    //
    // NoInlining: see the call in the constructor.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void AddHowToButton()
    {
        var entry = new ToolStripMenuItem(HowToMenuText);
        entry.Click += (_, _) => OpenHowTo();

        EventHandler? place = null;
        place = (_, _) =>
        {
            // Raised on vatSys's UI thread between messages: nothing may escape it.
            try
            {
                if (Application.OpenForms["MainForm"] is not Form { MainMenuStrip: { } menu })
                    return;

                Application.Idle -= place;

                if (menu.Items[InfoMenuName] is ToolStripMenuItem info)
                {
                    if (info.DropDownItems[InfoSeparatorName] is { } separator)
                        separator.Visible = true;
                    info.DropDownItems.Add(entry);
                    return;
                }

                ActionLog.Log("Ui", "vatSys's Info menu wasn't found; the OzServer How-To entry is under Tools instead",
                    new { action = "how_to_under_tools" });
                MMI.AddCustomMenuItem(new CustomToolStripMenuItem(
                    CustomToolStripMenuItemWindowType.Main, CustomToolStripMenuItemCategory.Tools, entry));
            }
            catch (Exception ex)
            {
                Application.Idle -= place;
                ActionLog.Log("Ui", $"The OzServer How-To entry couldn't be added: {ex.Message}",
                    new { action = "how_to_button_failed" });
            }
        };
        Application.Idle += place;
    }

    const string HowToMenuText = "OzServer How-To";
    // The Info menu and the separator above its browser entries, as vatSys's MainForm names them.
    const string InfoMenuName = "aISToolStripMenuItem";
    const string InfoSeparatorName = "infoStripSeparator";
    const string HowToUrl = "https://ozserver.org/how-to";

    BaseForm? _howToWindow;

    // vatSys's browser window (vatsys.BrowserWindow, as Info's browser entries open) is internal, so
    // it is built by reflection through its public (string name, Uri url) constructor. Should that
    // ever fail, the guide opens in the default web browser instead.
    void OpenHowTo()
    {
        try
        {
            if (_howToWindow is { IsDisposed: false })
            {
                ShowWindow(_howToWindow);
                return;
            }

            var browserType = typeof(BaseForm).Assembly.GetType("vatsys.BrowserWindow", throwOnError: true)!;
            _howToWindow = (BaseForm)Activator.CreateInstance(browserType, HowToMenuText, new Uri(HowToUrl));
            ShowWindow(_howToWindow);
        }
        catch (Exception ex)
        {
            ActionLog.Log("Ui", $"Couldn't open the guide in vatSys's browser ({ex.GetBaseException().Message}); opening it in the default browser",
                new { action = "how_to_browser_fallback" });
            try
            {
                System.Diagnostics.Process.Start(HowToUrl);
            }
            catch (Exception fallback)
            {
                OzErrors.Add(new OzServerNotice($"The OzServer guide is at {HowToUrl} (couldn't open it: {fallback.Message})."), "OzServer");
            }
        }
    }

    void OpenSectorsWindow()
    {
        if (OzServerMode.Disabled)
            return;

        // OzServerSectorsWindow hides rather than closes, so one instance, and its event
        // subscriptions, last for the plugin's lifetime.
        if (_sectorsWindow == null)
        {
            _sectorsWindow = new OzServerSectorsWindow(_ownershipTracker, _pendingSectorGhosts, _requestedSectorOverlay);

            // Puts the requested sectors on the scope and takes them off again. Hooked here so
            // the window knows nothing about the scope; VisibleChanged also covers hide-on-close,
            // which raises no Closed event.
            _sectorsWindow.VisibleChanged += (_, _) =>
                _requestedSectorOverlay.SetRevealed(_sectorsWindow is { IsDisposed: false, Visible: true });
        }

        ShowWindow(_sectorsWindow);
    }

    // ShowWithPlacement rather than Show: it is how vatSys opens its own windows, and it restores
    // the position and size the window was last left at, keyed on Control.Name (which is why both
    // windows set a unique Name).
    //
    // Falls back to Show only when there is no MainForm to own the window. The owner is also what
    // keeps it above the maximised main form.
    static void ShowWindow(BaseForm window)
    {
        if (window.Visible)
        {
            window.BringToFront();
            return;
        }

        if (Application.OpenForms["MainForm"] is Form mainForm)
            window.ShowWithPlacement(mainForm);
        else
            window.Show();

        window.BringToFront();
    }

    public void OnFDRUpdate(FDP2.FDR updated)
    {
        if (_inactive || OzServerMode.Disabled)
            return;

        // Each subsystem is isolated from the next: vatSys calls this inside its own
        // catch-and-log, so a throw in one would skip the rest and leave nothing in the plugin's
        // log.
        //
        // Accepts a handoff produced by an OzServer sector transfer as it lands.
        Isolate("TagHandoff", () => _sectorTagHandoff.OnFdrUpdate(updated));
        // After TagHandoff, so an accepted incoming strip is published as this controller's
        // jurisdiction rather than in the handover state vatSys delivered.
        Isolate("FdrSync", () => _fdrSync.OnFdrUpdate(updated));
        // A flight plan just activated by hand is coupled to its radar return if vatSys will not.
        Isolate("Couple", () => _coupleAssist.OnFdrUpdate(updated));
        // A finished flight comes off the Arrival Lists straight away.
        Isolate("Arrivals", () => _arrivalListSync.OnFdrUpdate(updated));
        // vatSys has just recomputed this track's state, so any ghost needs reasserting.
        Isolate("Ghost", () => _pendingSectorGhosts.Reassert());
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated)
    {
        if (_inactive || OzServerMode.Disabled)
            return;

        Isolate("FdrSync", () => _fdrSync.OnRadarTrackUpdate(updated));
        Isolate("Ghost", () => _pendingSectorGhosts.Reassert());
        Isolate("Couple", () => _coupleAssist.OnRadarTrackUpdate(updated));
    }

    // Runs one per-update subsystem, logging a failure rather than letting it escape into vatSys's
    // handler, where the plugin's log would never record it.
    static void Isolate(string category, Action work)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            work();
        }
        catch (Exception ex)
        {
            ActionLog.Log(category, $"update handler failed: {ex.Message}");
        }
        finally
        {
            HandlerTiming.Record(category, started);
        }
    }
}

// How much of vatSys's time the plugin's per-update handlers take, on whichever thread vatSys
// calls them on - which may be the UI thread. A single call over SlowCallMs, or a category over
// BusyMsPerSecond within one second, is logged with that second's call count, so a freeze can be
// attributed to the plugin or ruled out.
static class HandlerTiming
{
    const double SlowCallMs = 100;
    const double BusyMsPerSecond = 300;
    static readonly object Gate = new();
    static readonly Dictionary<string, (long Second, double Ms, int Calls, bool Reported)> Window = new();
    static DateTime _lastSlowLog = DateTime.MinValue;

    public static void Record(string category, long started)
    {
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        string? report = null;

        lock (Gate)
        {
            Window.TryGetValue(category, out var w);
            if (w.Second != second)
                w = (second, 0, 0, false);
            w.Ms += ms;
            w.Calls++;
            if (!w.Reported && w.Ms > BusyMsPerSecond)
            {
                w.Reported = true;
                report = $"{category} handlers used {w.Ms:0} ms of vatSys's time in one second ({w.Calls} calls)";
            }
            Window[category] = w;

            if (report == null && ms > SlowCallMs && DateTime.UtcNow - _lastSlowLog > TimeSpan.FromSeconds(5))
            {
                _lastSlowLog = DateTime.UtcNow;
                report = $"{category} handler took {ms:0} ms";
            }
        }

        if (report != null)
            ActionLog.Log("Perf", report, new
            {
                action = "handler_slow",
                category,
                thread = System.Threading.Thread.CurrentThread.ManagedThreadId,
                ui_thread = System.Windows.Forms.Application.OpenForms["MainForm"] is Control main && !main.InvokeRequired
            });
    }
}
