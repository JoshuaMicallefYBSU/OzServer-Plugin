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

    void OpenSectorsWindow()
    {
        // OzServerSectorsWindow hides rather than closes, so once created the same
        // instance is reused (and its event subscriptions stay alive) for the plugin's lifetime.
        _sectorsWindow ??= new OzServerSectorsWindow(_ownershipTracker);

        if (!_sectorsWindow.Visible)
        {
            _sectorsWindow.Show(Application.OpenForms["MainForm"]);
        }

        _sectorsWindow.BringToFront();
    }

    void OpenSettingsWindow()
    {
        _settingsWindow ??= new OzServerSettingsWindow();

        if (!_settingsWindow.Visible)
        {
            _settingsWindow.Show(Application.OpenForms["MainForm"]);
        }

        _settingsWindow.BringToFront();
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
