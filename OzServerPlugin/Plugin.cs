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

    public Plugin()
    {
        _ownershipTracker = new OzServerOwnershipTracker();
        _afvSectorClaimer = new AfvSectorClaimer();
        _fdrSync = new FdrSync();

        var sectorsMenuItem = new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Settings,
            new ToolStripMenuItem("OzServer Sectors"));
        sectorsMenuItem.Item.Click += (_, _) => OpenSectorsWindow();
        MMI.AddCustomMenuItem(sectorsMenuItem);

        // Hidden until there's an incoming sector request to show. There's no plugin API for a
        // standalone top-menu-bar button (MMI.AddCustomMenuItem only adds *into* an existing
        // dropdown, e.g. CustomToolStripMenuItemCategory.Settings adds under Settings - there's no
        // "Custom" dropdown of its own for CustomToolStripMenuItemCategory.Custom to land in,
        // which is why an earlier attempt at this never actually appeared anywhere), so this is
        // inserted directly into vatSys's own top menu strip instead - see InsertRequestNotificationItem
        // below. _ownershipTracker.PendingRequestCountChanged (not OzServerSectorsWindow's own
        // _requestsFromMe) drives it, so it lights up even if that window has never been opened.
        //
        // BackColor alone does nothing here - vatSys's own menu strip renderer ignores it entirely
        // for top-level items (confirmed against RealLeviticus/vatsys-mumble-reconnect's
        // MenuInjector, which hits the exact same wall for its own connection-status colour and
        // works around it the same way below: paint the fill and text manually).
        var requestNotificationItem = new ToolStripMenuItem { Visible = false };
        requestNotificationItem.Click += (_, _) => OpenSectorsWindow();
        requestNotificationItem.Paint += (sender, e) =>
        {
            var item = (ToolStripMenuItem)sender!;
            var rect = new Rectangle(Point.Empty, item.Size);
            // WindowWarning rather than a hardcoded Color.Orange: this is exactly the "something
            // needs your attention" role vatSys already has an identity for, so the indicator
            // follows whatever the loaded profile defines instead of being the one element on the
            // menu bar that ignores the theme.
            var background = Colours.GetColour(Colours.Identities.WindowWarning);
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, rect);
            TextRenderer.DrawText(e.Graphics, item.Text, item.Font, rect, ContrastingTextColour(background),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        _ownershipTracker.PendingRequestCountChanged += (_, count) =>
        {
            void Apply()
            {
                requestNotificationItem.Text = $"[{count} SECTOR REQUEST]";
                requestNotificationItem.Visible = count > 0;
                requestNotificationItem.Invalidate();
            }

            if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
                mainForm.BeginInvoke((MethodInvoker)Apply);
            else
                Apply();
        };

        // Form.MainMenuStrip is a public, standard WinForms property (vatSys assigns it in
        // MainForm's own designer code - this isn't a private-field reflection hack), so this reads
        // as: find the real "Messages" header among the top menu bar's own top-level items, and
        // insert requestNotificationItem immediately after it. Deferred the same way as the
        // repositioning below - the menu strip may not exist the instant the plugin constructs.
        // Relies on "Messages" still being that header's literal text; if a future vatSys build
        // changes it, this just never finds an anchor and the button silently never appears, rather
        // than throwing.
        EventHandler? insertNotificationItem = null;
        insertNotificationItem = (_, _) =>
        {
            if (Application.OpenForms["MainForm"] is not Form mainForm || mainForm.MainMenuStrip == null)
                return;

            var messagesItem = mainForm.MainMenuStrip.Items.OfType<ToolStripItem>().FirstOrDefault(i => i.Text == "Messages");
            if (messagesItem == null)
                return;

            Application.Idle -= insertNotificationItem;
            mainForm.MainMenuStrip.Items.Insert(mainForm.MainMenuStrip.Items.IndexOf(messagesItem) + 1, requestNotificationItem);
        };
        Application.Idle += insertNotificationItem;

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

            var sectorsItem = settingsMenu.DropDownItems.OfType<ToolStripItem>()
                .FirstOrDefault(i => i.Text == SectorsMenuItemText);
            if (sectorsItem == null)
                return;

            var insertAt = settingsMenu.DropDownItems.IndexOf(sectorsItem) + 1;
            settingsMenu.DropDownItems.Remove(sectorsMenuItem.Item);
            settingsMenu.DropDownItems.Insert(insertAt, sectorsMenuItem.Item);

            settingsMenu.DropDownItems.Remove(settingsMenuItem.Item);
            settingsMenu.DropDownItems.Insert(insertAt + 1, settingsMenuItem.Item);
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
