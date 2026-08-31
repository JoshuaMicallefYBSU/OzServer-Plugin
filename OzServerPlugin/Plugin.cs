using System;
using System.ComponentModel.Composition;
using System.Drawing;
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
    readonly TagOwnershipSync _tagOwnershipSync;
    readonly AtisSync _atisSync;
    readonly BadVectorsAtisSync _badVectorsAtisSync;
    // Held for the same reason as the two above: it lives entirely off Network's own events, and a
    // position has to be handed back whether or not the Sectors window was ever opened.
    readonly PrimaryPositionWatcher _primaryPositionWatcher;
    // Also purely event-driven, and has to be alive from plugin load: the disconnect it reacts to
    // can happen long before the Sectors window is ever opened.
    readonly GracefulDisconnectReleaser _gracefulDisconnectReleaser;

    // Incoming requests waiting on this controller, and the flash state driving the menu trail.
    int _pendingRequests;
    bool _requestFlashOn;
    readonly System.Windows.Forms.Timer _requestFlashTimer = new() { Interval = 500 };
    ToolStripMenuItem? _settingsMenuHeader;
    ToolStripItem? _ozServerSectorsItem;

    public Plugin()
    {
        _ownershipTracker = new OzServerOwnershipTracker();
        _afvSectorClaimer = new AfvSectorClaimer();
        _fdrSync = new FdrSync();
        // After the tracker, which it reads live subsector ownership through.
        _tagOwnershipSync = new TagOwnershipSync(_ownershipTracker);
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

        // An incoming request flashes its way inward, one level at a time, so it is visible from
        // wherever the controller happens to be looking without anything covering the radar:
        //
        //   Settings (menu bar)  ->  OzServer Sectors (inside Settings)  ->  Requested From Me
        //
        // Each level shows only while the level outside it is already open, which is what makes it
        // a trail to follow rather than three things blinking at once. The window's own title bar
        // flashes too (BaseForm.FlashTitleBar, as the ATIS window does) for when it is already open
        // but not focused. The Requested From Me heading itself is handled inside the window.
        //
        // Replaces a popup, and before that a custom-painted menu-bar indicator. The popup also had
        // a duplication bug: this event is raised from a poll, and two overlapping polls could both
        // observe the old request set and both fire. Flashing is idempotent, so that class of
        // problem does not arise.
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

        // Repaint on a timer, which is how the flash actually happens - a ToolStripItem only shows a
        // colour change when it is asked to redraw.
        _requestFlashTimer.Tick += (_, _) =>
        {
            _requestFlashOn = _pendingRequests > 0 && !_requestFlashOn;
            RefreshRequestFlash();
        };
        _requestFlashTimer.Start();

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

    // Paints the current step of the trail. Only one level is lit at a time: the Settings header
    // until Settings is open, then the OzServer Sectors entry inside it. Colour comes from the
    // profile's WindowWarning identity - the same "needs attention" role used for staged rows.
    void RefreshRequestFlash()
    {
        var lit = _pendingRequests > 0 && _requestFlashOn;
        var settingsOpen = _settingsMenuHeader?.DropDown is { Visible: true };

        if (_settingsMenuHeader != null)
        {
            _settingsMenuHeader.ForeColor = lit && !settingsOpen
                ? Colours.GetColour(Colours.Identities.WindowWarning)
                : Color.Empty;
        }

        if (_ozServerSectorsItem != null)
        {
            _ozServerSectorsItem.ForeColor = lit && settingsOpen
                ? Colours.GetColour(Colours.Identities.WindowWarning)
                : Color.Empty;
        }
    }

    // Black or white, whichever actually reads against the given fill. The indicator's background
    // now comes from the profile (see the Paint handler above), and a profile is free to define
    // WindowWarning as something dark - fixing the text at black would make the whole indicator
    // unreadable in that case. ITU-R BT.601 luma, the usual weighting for this.
    static Color ContrastingTextColour(Color background) =>
        (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) > 140
            ? Color.Black
            : Color.White;

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
