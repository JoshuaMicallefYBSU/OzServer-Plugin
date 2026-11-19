using System;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Sets the display level filter to 998-999 on FMP positions, which hides the labels of every
// non-jurisdiction track. FMP does not work traffic, so those tags are only clutter.
//
// Positions.xml cannot express this: LogicalPositions.Load reads no filter attribute, and vatSys
// keeps the filter as one global value in MMI.LowerLevelFilter/UpperLevelFilter. A position counts
// as FMP when:
//   - the selected Positions.xml position lists an "_FMP" callsign under <ControllerInfo>, or
//   - this session is connected under an "_FMP" callsign.
//
// ASDControlDX applies the band only to tracks that are not jurisdiction, quicklooked or alerting,
// so anything handed to FMP still shows.
//
// Leaving an FMP position restores the previous filter, unless the controller has changed it by
// hand in the meantime.
public class FmpLevelFilter
{
    const string FmpMarker = "_FMP";
    // MMI's setters take flight levels; its getters return feet.
    const int FmpLower = 998;
    const int FmpUpper = 999;

    // Feet, as MMI's getters return them. Null while the FMP filter isn't applied.
    (int Lower, int Upper)? _saved;

    public FmpLevelFilter()
    {
        MMI.PrimePositonChanged += (_, _) => RunOnUiThread(Evaluate);
        Network.Connected += (_, _) => RunOnUiThread(Evaluate);
    }

    static bool IsFmp()
    {
        if (Network.IsConnected && Contains(Network.Callsign))
            return true;

        var info = MMI.PrimePosition?.ControllerInfo;
        return info != null && info.Any(c => Contains(c?.Callsign));
    }

    static bool Contains(string? callsign) =>
        !string.IsNullOrEmpty(callsign) && callsign!.IndexOf(FmpMarker, StringComparison.OrdinalIgnoreCase) >= 0;

    // Called when OzServer is disabled, to restore the controller's own display levels.
    public void Shutdown() => RunOnUiThread(Evaluate);

    void Evaluate()
    {
        try
        {
            if (IsFmp() && !OzServerMode.Disabled)
                Apply();
            else
                Restore();
        }
        catch (Exception ex)
        {
            OzErrors.Add(new Exception($"Couldn't set the FMP level filter: {ex.Message}", ex), "OzServer");
        }
    }

    void Apply()
    {
        if (_saved != null)
            return;

        _saved = (MMI.LowerLevelFilter, MMI.UpperLevelFilter);

        // Upper first: MMI rejects a lower level that isn't below the current upper one, and the
        // controller may have lowered the upper filter by hand. 999 resets it to "no upper limit".
        MMI.UpperLevelFilter = FmpUpper;
        MMI.LowerLevelFilter = FmpLower;

        ActionLog.Log("Filters", $"FMP position - display levels set to {FmpLower:d3}-{FmpUpper:d3}",
            new { action = "fmp_filter_applied", position = MMI.PrimePosition?.Name, callsign = Network.Callsign });
    }

    void Restore()
    {
        if (_saved is not { } saved)
            return;

        _saved = null;

        // Changed by hand since it was applied, so it is the controller's filter and is left alone.
        if (MMI.LowerLevelFilter != FmpLower * 100 || MMI.UpperLevelFilter != FmpUpper * 100)
            return;

        // Lower to zero first so neither setter can trip over the other's check on the way back.
        MMI.LowerLevelFilter = 0;
        MMI.UpperLevelFilter = saved.Upper / 100;
        MMI.LowerLevelFilter = saved.Lower / 100;

        ActionLog.Log("Filters",
            $"Left FMP position - display levels restored to {saved.Lower / 100:d3}-{saved.Upper / 100:d3}",
            new { action = "fmp_filter_restored", position = MMI.PrimePosition?.Name });
    }

    // Network's events can arrive off the UI thread; MMI and the scope belong to it.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
