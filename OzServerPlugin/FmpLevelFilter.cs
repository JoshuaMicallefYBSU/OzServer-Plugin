using System;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Sets the display level filter to 998-999 for FMP positions, which hides the labels of every
// non-jurisdiction track. FMP doesn't work traffic, so departure and overflight tags are only clutter.
//
// Positions.xml has nothing for this: LogicalPositions.Load reads no filter attribute, and vatSys
// keeps the filter as one global value in MMI.LowerLevelFilter/UpperLevelFilter, not per position.
// So it's applied here whenever the position looks like an FMP:
//   - the Positions.xml position just selected lists an "_FMP" callsign under <ControllerInfo>, or
//   - this session is connected under an "_FMP" callsign.
//
// Only tracks the scope treats as non-jurisdiction are affected (ASDControlDX checks the band only
// for tracks that aren't jurisdiction, quicklooked or alerting), so anything handed to FMP still shows.
//
// Moving to a non-FMP position puts back whatever was set before, unless the controller has
// changed the filter by hand in the meantime. In that case it's their filter, and it's left alone.
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

    void Evaluate()
    {
        try
        {
            if (IsFmp())
                Apply();
            else
                Restore();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't set the FMP level filter: {ex.Message}", ex), "OzServer");
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

        // Changed by hand since it was applied: that's the controller's filter now.
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
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
