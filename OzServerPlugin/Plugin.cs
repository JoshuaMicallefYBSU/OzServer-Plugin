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
    readonly FdrSync _fdrSync;
    readonly FdrActivationSync _fdrActivationSync;
    readonly TagOwnershipSync _tagOwnershipSync;
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
    // Timer-driven and entirely invisible - it never touches the running session, only what is on
    // disk for the next one. See its own class comment for why it can't just overwrite the DLL.
    readonly PluginUpdater _updater;

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

    public Plugin()
    {
        _ownershipTracker = new OzServerOwnershipTracker();
        _afvSectorClaimer = new AfvSectorClaimer();
        _fdrSync = new FdrSync();
        // Purely timer/poll-driven, same as _ownershipTracker - constructed unconditionally so a
        // flight stuck at STATE_PREACTIVE gets corrected whether or not any plugin window is ever
        // opened. See its own class comment for why this is a separate concern from FdrSync/
        // TagOwnershipSync rather than folded into either.
        _fdrActivationSync = new FdrActivationSync();
        // After the tracker, which it reads live subsector ownership through; after FdrSync, which
        // it calls into to clear OzServer's own record of who holds a tag the moment this controller
        // drops it to none (see TagOwnershipSync.OnFdrsChanged); and after FdrActivationSync, whose
        // IsKnownToServer it reads to gate its own airborne/near-boundary pre-activation trigger.
        _tagOwnershipSync = new TagOwnershipSync(_ownershipTracker, _fdrSync, _fdrActivationSync);
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

                // SetRevealed as well as SetRequested: nothing is drawn until the window opens, and
                // this is standing in for that having happened.
                var sectors = DebugHighlightSectors();
                _requestedSectorOverlay.SetRequested(sectors);
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

            // Captured here rather than earlier: this is the first point at which both the Settings
            // header and our own entry inside it definitely exist.
            _settingsMenuHeader = settingsMenu;
            _ozServerSectorsItem = sectorsMenuItem.Item;

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

    void OpenSectorsWindow()
    {
        // OzServerSectorsWindow hides rather than closes, so once created the same
        // instance is reused (and its event subscriptions stay alive) for the plugin's lifetime.
        if (_sectorsWindow == null)
        {
            _sectorsWindow = new OzServerSectorsWindow(_ownershipTracker);

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
        _fdrSync.OnFdrUpdate(updated);
        _tagOwnershipSync.OnFdrUpdate(updated);
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated)
    {
        _fdrSync.OnRadarTrackUpdate(updated);
        _tagOwnershipSync.OnRadarTrackUpdate(updated);
    }
}
