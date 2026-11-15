using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace OzServerPlugin;

[Export(typeof(IPlugin))]
public class Plugin : IPlugin
{
    public const string Name = "OzServer Plugin";

    // Text of the built-in "Sectors..." entry in the Settings menu (vatsys.MainForm.sectorsToolStripMenuItem)
    // that our own entry gets slotted in underneath.
    const string SectorsMenuItemText = "Sectors...";
    // The same item's designer-assigned control name (vatsys.MainForm::sectorsToolStripMenuItem).
    const string SectorsMenuItemName = "sectorsToolStripMenuItem";

    string IPlugin.Name => Name;

    OzServerSectorsWindow? _sectorsWindow;
    OzServerSettingsWindow? _settingsWindow;
    // Both kept alive by their own event subscriptions (Audio.VSCSFrequenciesChanged/
    // MMI.SectorsControlledChanged) as much as by these fields, but held here too for clarity and
    // so neither is eligible for collection before those subscriptions are even made. Constructed
    // unconditionally (unlike the windows above, which are lazy) since they have to keep working
    // whether or not the controller ever opens the OzServer Sectors window - AfvSectorClaimer turns
    // a VSCS transmit press into a MMI.SectorsControlled change, and _ownershipTracker is what
    // reacts to that (and every other way MMI.SectorsControlled can change) by claiming on OzServer
    // - see both classes' own comments.
    readonly AfvSectorClaimer _afvSectorClaimer;
    readonly OzServerOwnershipTracker _ownershipTracker;
    // Keeps OzServer's flight_data_records current. Retained through the tag-handling removal
    // because disconnect recovery is built entirely on the controlling_cid and current_sector this
    // pushes - without it a resume has nothing to give back.
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
    // Held for the same reason as the two above: it lives entirely off Network's own events, and a
    // position has to be handed back whether or not the Sectors window was ever opened.
    readonly PrimaryPositionWatcher _primaryPositionWatcher;
    // Also purely event-driven, and has to be alive from plugin load: the disconnect it reacts to
    // can happen long before the Sectors window is ever opened.
    readonly GracefulDisconnectReleaser _gracefulDisconnectReleaser;
    readonly ObserverPositionMirror _observerPositionMirror;
    readonly RequestedSectorOverlay _requestedSectorOverlay;
    // Display levels 998-999 on FMP positions. Event-driven, so it has to exist before the first
    // position is selected.
    readonly FmpLevelFilter _fmpLevelFilter;
    // Timer-driven and entirely invisible - it never touches the running session, only what is on
    // disk for the next one. See its own class comment for why it can't just overwrite the DLL.
    readonly PluginUpdater _updater;
    // Copies this session's decisions to OzServer so every controller's log can be read together.
    readonly ClientLogForwarder _clientLogForwarder;

    // Incoming requests waiting on this controller, driving the Settings header's flash.
    int _pendingRequests;

    // Debug menu only - whether its highlight toggle is currently on.
    bool _debugHighlightShown;
    // Request groups already put to the controller, so a group is prompted once rather than
    // again on every refresh while it sits unanswered. Pruned to whatever is still pending, so
    // a request that is withdrawn and made again does prompt a second time.
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

    public Plugin()
    {
        // First, so every decision logged below is forwarded from the outset.
        _clientLogForwarder = new ClientLogForwarder();
        // Before anything that writes MMI.SectorsControlled, so its first snapshot is taken before
        // this plugin has had a chance to change the list itself.
        MmiSectorWatch.Install();
        _ownershipTracker = new OzServerOwnershipTracker();
        // After the tracker, whose RecentlyResumed its connect-time grant keeps.
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
        _fdrResumeActivation = new FdrResumeActivation();
        _labelStateMirror = new LabelStateMirror();
        // After the tracker, whose OwnershipChanged tells it a sector moved.
        _sectorTagHandoff = new SectorTagHandoff(_ownershipTracker, _fdrSync);
        // After the tracker, whose own open requests it reads alongside the window's staged set.
        _pendingSectorGhosts = new PendingSectorGhosts(_ownershipTracker);
        _atisSync = new AtisSync();
        _badVectorsAtisSync = new BadVectorsAtisSync();
        // After the tracker, which it releases through.
        _primaryPositionWatcher = new PrimaryPositionWatcher(_ownershipTracker);
        _gracefulDisconnectReleaser = new GracefulDisconnectReleaser();
        // After the tracker, whose ControlledByOthers it mirrors.
        _observerPositionMirror = new ObserverPositionMirror(_ownershipTracker);
        // After the tracker, whose incoming requests it outlines on the scope.
        _requestedSectorOverlay = new RequestedSectorOverlay(_ownershipTracker);
        // Last, and dependent on nothing: it only ever reads GitHub and writes files next to this
        // assembly, so it has no ordering relationship with anything above it.
        _updater = new PluginUpdater();

        _fmpLevelFilter = new FmpLevelFilter();

        var sectorsMenuItem = new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Settings,
            new ToolStripMenuItem("OzServer Sectors"));
        sectorsMenuItem.Item.Click += (_, _) => OpenSectorsWindow();
        MMI.AddCustomMenuItem(sectorsMenuItem);

        // An incoming request is surfaced the same way vatSys's own "Messages" menu surfaces an
        // unread one: the Settings header flashes (a solid colour flip, on vatSys's own menu-bar
        // flash timer - see MenuRenderer.FlashTimer_Tick/PaintBackground) until Settings is opened,
        // and OzServer Sectors carries a steady "[N]" badge underneath it until the request is
        // actually dealt with - the same two-tier presentation Messages gives its own top-level
        // flash plus each unread recipient's badge. The window's own title bar flashes too
        // (BaseForm.FlashTitleBar, as the ATIS window does) for when it is already open but not
        // focused. The Requested From Me heading itself is handled inside the window.
        //
        // This used to paint its own ForeColor on a private timer, which never actually rendered:
        // mainMenu's Renderer is vatSys's own MenuRenderer, and both
        // OnRenderMenuItemBackground/OnRenderItemText recompute every item's colour from scratch
        // (by Name, CheckState and whether a child carries a Tag) rather than ever reading the
        // item's own ForeColor/BackColor - so setting it here was a no-op the whole time. Checked
        // and Tag are the two properties that renderer actually looks at, so those are what this
        // drives now, and vatSys's own already-running flash timer repaints them - no timer of our
        // own needed any more.
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
                mainForm.BeginInvoke((MethodInvoker)Apply);
            else
                Apply();
        };

        // Opens each popup on demand, so their layout can be looked at in the real client without
        // waiting for another controller to request a sector or for a position to be relinquished.
        // These windows derive from vatSys's BaseForm, which closes itself immediately outside the
        // vatSys process - so a standalone preview harness cannot render them and this is the only
        // way to actually see one.
        var debugMenu = new ToolStripMenuItem("OzServer Popups (Debug)");

        // Simulates a request arriving and being looked at: the notification sound, and the
        // highlight the sector management window would reveal. A toggle, because with the popup
        // gone there is no Accept/Reject to clear it and nothing else here would.
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("Sector request (sound + highlight)", null,
            (_, _) =>
            {
                _debugHighlightShown = !_debugHighlightShown;

                if (!_debugHighlightShown)
                {
                    _requestedSectorOverlay.Clear();
                    return;
                }

                NotificationSound.PlayRequestArrived();

                // SetRevealed as well as SetIncoming: nothing is drawn until the window opens, and
                // this is standing in for that having happened.
                var sectors = DebugHighlightSectors();
                _requestedSectorOverlay.SetIncoming(sectors);
                _requestedSectorOverlay.SetRevealed(true);

                ActionLog.Log("Debug", $"highlighting {string.Join(", ", sectors.Select(s => s.Name))}");
            }));
        // Both previews below are written to match what the real code actually sends, sector
        // descriptions included - a preview that formats sectors more prettily than the live path
        // is worse than no preview, because it is the thing the layout gets judged against.
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("Notice (OK)", null,
            (_, _) => ShowDebugPopup(new SectorNoticeWindow(
                // Mirrors PrimaryPositionWatcher.ShowNotice.
                "BN-ISA_CTR has logged on." + Environment.NewLine + Environment.NewLine
                + "These sectors belong to that position and are being relinquished to them:"
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine,
                    new[] { "ARA", "STR" }.Select(SectorDescription.Describe)),
                "Position relinquished"))));
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("Conflict (Yes/No)", null,
            (_, _) => ShowDebugPopup(new SectorConflictPromptWindow(
                // Mirrors OzServerOwnershipTracker.HandleConflictAsync, which lists one sector per
                // line with the controller currently holding it.
                "This sector is already owned by another controller:"
                + Environment.NewLine + Environment.NewLine
                + SectorDescription.DescribeWithOwner("STR", "BN-TRT_CTR")
                + Environment.NewLine + Environment.NewLine + "Request it from them?",
                "Sector already owned"))));

        MMI.AddCustomMenuItem(new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Settings,
            debugMenu));

        var settingsMenuItem = new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Settings,
            new ToolStripMenuItem("OzServer Settings"));
        settingsMenuItem.Item.Click += (_, _) => OpenSettingsWindow();
        MMI.AddCustomMenuItem(settingsMenuItem);

        // Under Tools, not Settings: switching OzServer off mid-session is something done to the
        // running session, alongside vatSys's own on/off tools (Disable Filters, Disable GRIB
        // Winds), not a setting. vatSys appends plugin Tools items at the bottom of that menu.
        _disableItem = new ToolStripMenuItem("Disable OzServer");
        _disableItem.Click += (_, _) => ConfirmDisable();
        MMI.AddCustomMenuItem(new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Tools,
            _disableItem));

        _ozServerMenuItems.Add(sectorsMenuItem.Item);
        _ozServerMenuItems.Add(settingsMenuItem.Item);
        _ozServerMenuItems.Add(debugMenu);

        // The vatsys plugin API always inserts Settings-category items at the bottom of the
        // Settings menu (just above its final separator), not next to any specific built-in
        // entry. Application.Idle keeps firing (harmlessly) until vatsys has actually built the
        // main menu and dropped both items into it - only then can they be moved, so the move is
        // deferred rather than done here where the menu doesn't exist yet.
        EventHandler? repositionUnderSectors = null;
        repositionUnderSectors = (_, _) =>
        {
            if (sectorsMenuItem.Item.OwnerItem is not ToolStripMenuItem settingsMenu)
                return;

            Application.Idle -= repositionUnderSectors;

            // Name first, Text only as a fallback: vatSys assigns this item the name
            // "sectorsToolStripMenuItem" in MainForm's designer code, which is far less likely to
            // change between builds than the displayed "Sectors..." label.
            var sectorsItem = settingsMenu.DropDownItems.OfType<ToolStripItem>()
                .FirstOrDefault(i => i.Name == SectorsMenuItemName)
                ?? settingsMenu.DropDownItems.OfType<ToolStripItem>()
                    .FirstOrDefault(i => i.Text == SectorsMenuItemText);
            if (sectorsItem == null)
                return;

            var insertAt = settingsMenu.DropDownItems.IndexOf(sectorsItem) + 1;
            settingsMenu.DropDownItems.Remove(sectorsMenuItem.Item);
            settingsMenu.DropDownItems.Insert(insertAt, sectorsMenuItem.Item);

            settingsMenu.DropDownItems.Remove(settingsMenuItem.Item);
            settingsMenu.DropDownItems.Insert(insertAt + 1, settingsMenuItem.Item);

            _builtInSectorsItem = sectorsItem;

            // Captured here rather than earlier: this is the first point at which both the Settings
            // header and our own entry inside it definitely exist.
            _settingsMenuHeader = settingsMenu;
            _ozServerSectorsItem = sectorsMenuItem.Item;

            // A request that arrived before the menu existed set _pendingRequests with nothing to
            // paint it on, and IncomingRequestsChanged only fires again when the set changes - so
            // without this its flash and badge would not appear until another request came in.
            RefreshRequestFlash();

            // Opening Settings is "clicked" for the header's own flash, exactly like vatSys's real
            // Messages header stopping on its Click/MouseUp (see MainForm's
            // messagesToolStripMenuItem_Click/_MouseUp) rather than waiting for the request itself
            // to be dealt with - the OzServer Sectors badge underneath keeps showing until then.
            settingsMenu.DropDownOpened += (_, _) => settingsMenu.Checked = false;

            // OzServer Sectors replaces the built-in Sectors window rather than sitting beside it:
            // both write MMI.SectorsControlled, but only this plugin's window also tells OzServer,
            // so a claim made through the built-in one is invisible to every other controller until
            // something happens to nudge the tracker. Two windows that look equivalent and aren't is
            // the worse outcome, so the built-in entry is hidden while the plugin is loaded.
            //
            // Visible=false, not Remove: MainForm still holds the field and its click handler, so
            // hiding leaves vatSys's own state entirely intact and is trivially reversible - and
            // vatSys can still open the window itself (MMI.SectorsWindow) if it ever needs to.
            // Left in place in the collection so the indices computed above stay meaningful.
            sectorsItem.Visible = false;
        };
        Application.Idle += repositionUnderSectors;
    }

    // Drives the two properties MenuRenderer itself actually keys its painting off - see the
    // IncomingRequestsChanged comment above for why ForeColor/BackColor never worked here.
    // One window per request group, not per sector. Everything another controller asked for in a
    // single Apply shares a group id, so a request covering three sectors is one decision and is put
    // to the controller once, listing all three - which is the whole point of grouping them.
    //
    // Accept and Reject both act on every id in the group through the batch endpoints, so a grouped
    // request can never end up half-answered.
    // Sounds the notification for requests that have not been announced yet.
    //
    // This used to open a SectorRequestPromptWindow per group. That popup is gone: a request is not
    // urgent enough to put a window over a controller's scope while they are working traffic, and
    // one that appears unbidden gets dismissed reflexively - which lost the request, since closing
    // it deliberately meant "not now" rather than "no". Every request is answered in the sector
    // management window instead, which is where the rest of the decision already lived, and where
    // the airspace involved is shaded on the scope (RequestedSectorOverlay).
    //
    // Grouping is still what decides when to make a noise. Requests arrive by poll and by SSE, so
    // the same pending request is seen many times over; only a group not announced before counts as
    // an arrival. Once per group rather than once per request, so three sectors asked for in one
    // Apply is one notification - the same reason the popup was grouped.
    void AnnounceNewRequestGroups(IReadOnlyList<OzServerSectorOwnershipRequestDto> requests)
    {
        var groups = requests
            .Where(request => request.RejectedAt == null)
            .GroupBy(request => string.IsNullOrEmpty(request.GroupId) ? $"request-{request.Id}" : request.GroupId,
                     StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToList();

        // Anything no longer pending is forgotten, so the same sectors requested again later are a
        // new arrival and sound again.
        _announcedRequestGroups.IntersectWith(groups);

        var arrived = groups.Count(group => _announcedRequestGroups.Add(group));
        if (arrived == 0)
            return;

        // One sound however many groups landed together. Two arriving in the same poll is two
        // entries in the window, not a reason to play the tone twice over itself.
        NotificationSound.PlayRequestArrived();

        ActionLog.Log("Requests", $"{arrived} new request group(s) announced");
    }

    // Deliberately a couple of sectors rather than everything to hand. The point of the debug entry
    // is to see which airspace a highlight covers and where the border between two sectors falls,
    // and a whole position - seven sectors for ASP - shades most of the scope as one continuous
    // blob that shows neither.
    const int DebugHighlightCount = 2;

    // What the debug entry highlights. Prefers whatever this controller actually has selected, so
    // the highlight lands where they are already looking and on sectors that adjoin each other -
    // which is what makes the border between them worth looking at. Falls back to sectors straight
    // out of the dataset so the highlight can be tested while disconnected: MMI.SectorsControlled is
    // empty until a position is taken, which made the debug entry silently highlight nothing, and
    // that is indistinguishable from the overlay not working.
    static List<SectorsVolumes.Sector> DebugHighlightSectors()
    {
        var mine = MMI.SectorsControlled.Where(sector => !sector.IsDummy).ToList();
        if (mine.Count > 0)
            return mine.Take(DebugHighlightCount).ToList();

        return SectorsVolumes.Sectors
            .Where(sector => sector.Volumes != null
                             && sector.Volumes.Any(volume => volume.Boundary != null && volume.Boundary.Count >= 3))
            .Take(DebugHighlightCount)
            .ToList();
    }

    // Shown exactly the way the real ones are - non-modally, parented to the main form - so what the
    // debug menu puts on screen is the same window in the same state, not an approximation of it.
    static void ShowDebugPopup(Form popup)
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

        // A positive int Tag is what MenuRenderer.OnRenderItemText renders as a coloured "[N]"
        // badge (via ShortcutKeyDisplayString - see its own comment) - the same steady per-item
        // indicator vatSys gives an unread entry under Messages. It also satisfies the *other*
        // thing that Tag drives: MenuRenderer only flashes an item whose DropDownItems contain a
        // child with a non-null Tag (see its background/text colour checks), which is what makes
        // Checked below actually flash the Settings header rather than silently doing nothing.
        if (_ozServerSectorsItem != null)
            _ozServerSectorsItem.Tag = pending ? _pendingRequests : null;

        // Checked is the flag MenuRenderer's own already-running flash timer keys off (see its
        // "windowsToolStripMenuItem"/"messagesToolStripMenuItem"/hasTaggedChild check) - sets it
        // true whenever something is pending, false the moment nothing is (mirroring
        // messagesToolStripMenuItem.Checked being cleared once ChatWindow_Opened finds no
        // recipient left Indeterminate). Opening Settings (see DropDownOpened above) can also
        // clear it earlier, before the request itself is gone - exactly like Messages stopping its
        // own flash on Click without waiting for every unread entry to be read.
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
        ShowDebugPopup(prompt);
    }

    // Shuts OzServer out of the session. On the UI thread (the menu click and the prompt both are).
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
        _settingsWindow?.Hide();
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
            _builtInSectorsItem.Visible = true;
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

    // The last calls OzServer gets from this session: this controller's sectors released, so nobody
    // else is left blocked by a controller who has stopped reporting, and the log sent. Inside AllowFinalCalls, which is what lets them through.
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
            mainForm.BeginInvoke((MethodInvoker)(() => ShowDebugPopup(notice)));
        else
            ShowDebugPopup(notice);
    }

    void OpenSectorsWindow()
    {
        if (OzServerMode.Disabled)
            return;

        // OzServerSectorsWindow hides rather than closes, so once created the same
        // instance is reused (and its event subscriptions stay alive) for the plugin's lifetime.
        if (_sectorsWindow == null)
        {
            _sectorsWindow = new OzServerSectorsWindow(_ownershipTracker, _pendingSectorGhosts, _requestedSectorOverlay);

            // What puts the requested sectors on the scope, and takes them off again. Hooked here
            // rather than inside the window so the window stays about managing sectors and knows
            // nothing about the map - VisibleChanged covers both directions on its own, including
            // the hide-on-close that never raises a Closed event.
            _sectorsWindow.VisibleChanged += (_, _) =>
                _requestedSectorOverlay.SetRevealed(_sectorsWindow is { IsDisposed: false, Visible: true });
        }

        ShowWindow(_sectorsWindow);
    }

    void OpenSettingsWindow()
    {
        if (OzServerMode.Disabled)
            return;

        _settingsWindow ??= new OzServerSettingsWindow();
        ShowWindow(_settingsWindow);
    }

    // ShowWithPlacement rather than Show: it is how every vatSys window is opened, and it restores
    // the position and size the controller last left the window at, keyed on Control.Name (which is
    // why both windows set a unique Name). Plain Show() ignores saved placement entirely, so the
    // window reappeared at its StartPosition every single time it was opened.
    //
    // Falls back to Show() only if MainForm isn't there to own it - ShowWithPlacement needs a Form,
    // and an owner is also what keeps the window above the maximised main form rather than dropping
    // behind it the moment focus returns.
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
        if (OzServerMode.Disabled)
            return;

        // Each subsystem isolated from the next. vatSys calls this inside its own catch-and-log, so
        // a throw in the first call used to take the other two with it and leave no trace anywhere
        // OzServer can see - a tag silently never accepted, ghosts silently never reasserted, and
        // nothing in the log to say which. These are three unrelated jobs that happen to share a
        // callback; one failing is not a reason to skip the others.
        // Watches for the handoffs an OzServer sector transfer produces, so one is accepted the
        // moment it lands rather than on a poll of its own.
        Isolate("TagHandoff", () => _sectorTagHandoff.OnFdrUpdate(updated));
        // Run after TagHandoff so an accepted incoming strip is published as this controller's
        // jurisdiction, not as the handover state vatSys delivered at the start of the callback.
        Isolate("FdrSync", () => _fdrSync.OnFdrUpdate(updated));
        // A flight plan just activated by hand is coupled to its radar return if vatSys will not.
        Isolate("Couple", () => _coupleAssist.OnFdrUpdate(updated));
        // vatSys has just recomputed this track's state, so any ghost of ours needs reasserting.
        Isolate("Ghost", () => _pendingSectorGhosts.Reassert());
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated)
    {
        if (OzServerMode.Disabled)
            return;

        Isolate("FdrSync", () => _fdrSync.OnRadarTrackUpdate(updated));
        Isolate("Ghost", () => _pendingSectorGhosts.Reassert());
        Isolate("Couple", () => _coupleAssist.OnRadarTrackUpdate(updated));
    }

    // Runs one of the per-update subsystems, reporting a failure rather than letting it escape into
    // vatSys's own handler where nothing this plugin logs would ever record it.
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

// How much of vatSys's own time the plugin's per-update handlers take - on whichever thread vatSys
// calls them on, which may be the UI thread. A controller reported vatSys freezing on activating an
// FDR; the logs could show the UI thread was alive but not whether the plugin was what kept it busy.
// This makes it provable: a single call over SlowCallMs, or a category over BusyMsPerSecond in one
// second, is logged with the count of calls that second. Silent during a freeze means it was not us.
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
