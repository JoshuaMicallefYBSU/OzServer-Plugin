using System;
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

    // Incoming requests waiting on this controller, driving the Settings header's flash.
    int _pendingRequests;
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
            }

            // The poll this comes from runs on a background timer thread.
            if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
                mainForm.BeginInvoke((MethodInvoker)Apply);
            else
                Apply();
        };

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
        _sectorsWindow ??= new OzServerSectorsWindow(_ownershipTracker);
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
