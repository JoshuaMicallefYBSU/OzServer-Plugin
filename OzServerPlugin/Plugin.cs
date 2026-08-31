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
    readonly AtisSync _atisSync;
    readonly BadVectorsAtisSync _badVectorsAtisSync;
    // Held for the same reason as the two above: it lives entirely off Network's own events, and a
    // position has to be handed back whether or not the Sectors window was ever opened.
    readonly PrimaryPositionWatcher _primaryPositionWatcher;
    // Also purely event-driven, and has to be alive from plugin load: the disconnect it reacts to
    // can happen long before the Sectors window is ever opened.
    readonly GracefulDisconnectReleaser _gracefulDisconnectReleaser;

    public Plugin()
    {
        _ownershipTracker = new OzServerOwnershipTracker();
        _afvSectorClaimer = new AfvSectorClaimer();
        _fdrSync = new FdrSync();
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

        // An incoming request announces itself with a window on screen rather than an indicator
        // painted into vatSys's own menu bar. The menu-bar item had to be injected directly into
        // MainForm.MainMenuStrip (MMI.AddCustomMenuItem only adds *into* an existing dropdown) and
        // custom-painted, because vatSys's renderer ignores BackColor on top-level items - a lot of
        // machinery for something easy to miss while looking at the radar. A popup naming the sector
        // and who asked is both simpler and harder to overlook.
        _ownershipTracker.IncomingRequestsChanged += (_, requests) =>
        {
            void Show()
            {
                if (requests.Count == 0)
                    return;

                var lines = requests.Select(r =>
                    $"    {r.Sector?.Name ?? "Unknown"} - requested by {r.RequestingCallsign}");

                var message = (requests.Count == 1
                                  ? "A controller has requested a sector from you:"
                                  : $"{requests.Count} controllers have requested sectors from you:")
                              + Environment.NewLine + Environment.NewLine
                              + string.Join(Environment.NewLine, lines)
                              + Environment.NewLine + Environment.NewLine
                              + "Open OzServer Sectors to accept or reject them.";

                var notice = new SectorNoticeWindow(message, "Sector requested");
                if (Application.OpenForms["MainForm"] is Form owner)
                    notice.Show(owner);
                else
                    notice.Show();

                notice.BringToFront();
            }

            // The poll this comes from runs on a background timer thread.
            if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
                mainForm.BeginInvoke((MethodInvoker)Show);
            else
                Show();
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
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated)
    {
        _fdrSync.OnRadarTrackUpdate(updated);
    }
}
