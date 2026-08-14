using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Keeps OzServer's sector-ownership record in sync with MMI.SectorsControlled, independent of
// whether OzServerSectorsWindow happens to be open. Constructed unconditionally by Plugin (like
// AfvSectorClaimer) rather than lazily: a VSCS/AFV transmit press only ever touches
// MMI.SectorsControlled (see AfvSectorClaimer), same as a login under a position's own callsign or
// the built-in vatSys Sectors window - this is what actually claims it on OzServer, and
// OwnedChanged is what lets OzServerSectorsWindow update immediately when it does, rather than
// waiting on its own next poll tick or the next time it's opened.
//
// The ONLY thing that decides what Owned is - OzServer's own ownership record for this controller
// (SectorOwnershipController::mine), full stop. Every action that can change ownership (claim,
// release, accept, or MMI.SectorsControlled changing) calls the relevant endpoint and then
// re-derives Owned through RefreshFromServerAsync rather than guessing locally what the result
// must have been. That used to be layered with a second opinion - diffing against
// MMI.SectorsControlled and releasing anything Owned showed that MMI didn't - which fought a VSCS
// transmit claim (never reflected in MMI on its own) in a claim/release loop. One source of truth.
public class OzServerOwnershipTracker
{
    public event EventHandler? OwnedChanged;

    readonly OzServerApiClient _api = new();
    List<SectorsVolumes.Sector> _owned = new();

    public IReadOnlyList<SectorsVolumes.Sector> Owned => _owned;

    public OzServerOwnershipTracker()
    {
        MMI.SectorsControlledChanged += (_, _) => RunOnUiThread(() => _ = ClaimMmiControlledSectorsAsync());
        _ = RefreshFromServerAsync();
    }

    public async Task RefreshFromServerAsync()
    {
        List<OzServerSectorDto> mine;
        try
        {
            mine = await _api.GetMySectorsAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't refresh Owned from OzServer: {ex.Message}"), "OzServer");
            return;
        }

        _owned = mine
            .Select(dto => SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == dto.Name))
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();

        OwnedChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClaimAsync(SectorsVolumes.Sector sector)
    {
        try
        {
            await _api.ClaimSectorAsync(sector.Name);
        }
        catch (OzServerApiException ex) when (ex.StatusCode == 409 && ex.Conflicts.Count > 0)
        {
            await HandleConflictAsync(sector, ex.Conflicts);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message), "OzServer");
        }

        await RefreshFromServerAsync();
    }

    public async Task ReleaseAsync(SectorsVolumes.Sector sector)
    {
        try
        {
            await _api.ReleaseSectorAsync(sector.Name);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message), "OzServer");
        }

        await RefreshFromServerAsync();
    }

    public Task<OzServerSectorOwnershipRequestDto> RequestAsync(SectorsVolumes.Sector sector) =>
        _api.RequestSectorAsync(sector.Name);

    public async Task AcceptRequestAsync(int requestId)
    {
        await _api.AcceptRequestAsync(requestId);
        await RefreshFromServerAsync();
    }

    // MMI.SectorsControlled can change through paths nothing OzServer-aware drives directly -
    // connecting under a position's own callsign, the built-in Sectors window, VSCS/AFV transmit
    // (AfvSectorClaimer), a LogicalPositions/profile auto-set, another plugin, etc. This claims
    // whatever's newly controlled (claiming an already-mine sector is a harmless no-op server-side -
    // see SectorOwnershipController::claim - so nothing needs diffing against Owned first) and then
    // refreshes Owned from the server, once, through RefreshFromServerAsync.
    //
    // Deliberately claim-only otherwise: releasing is never inferred from MMI absence (that's the
    // claim/release loop described in the class comment) - it only ever happens from an explicit
    // action (the arrow button) or a genuine conflict, handled below.
    //
    // Claims exactly what MMI.SectorsControlled reports, sector by sector - it used to substitute a
    // bare subordinate (e.g. connecting under a subsector's own callsign, ML-SNO_CTR) for its
    // primary (e.g. WOL) on the theory that the group is meant to be owned together, but that's
    // wrong for a controller who deliberately logged in under just the subsector's own callsign:
    // they end up owning the whole primary and every one of its other subsectors too, not just the
    // one they're actually working. SectorOwnershipController::claim already covers whatever a
    // claimed sector's own responsible_sectors lists, so a bare subsector claim already covers
    // exactly what it should - no client-side substitution needed.
    async Task ClaimMmiControlledSectorsAsync()
    {
        var target = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        foreach (var sector in target)
        {
            try
            {
                await _api.ClaimSectorAsync(sector.Name);
            }
            catch (OzServerApiException ex) when (ex.StatusCode == 409 && ex.Conflicts.Count > 0)
            {
                await HandleConflictAsync(sector, ex.Conflicts);
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't sync {sector.Name} to OzServer: {ex.Message}"), "OzServer");
            }
        }

        await RefreshFromServerAsync();
    }

    // One or more of sector's own covered sub-sectors is already owned by someone else - e.g. GUN
    // extending WOL while BLA already owns WOL's own sub-sector SNO (extended earlier). Asks the
    // controller whether to formally request each contested sub-sector from its current owner, then
    // claims sector regardless, excluding whichever sub-sectors are still contested either way:
    // "No" just leaves them with their current owner (sector opens with them carved out); "Yes"
    // does the same but also puts in a request for each one.
    async Task HandleConflictAsync(SectorsVolumes.Sector sector, IReadOnlyList<OzServerSectorConflictDto> conflicts)
    {
        var question = conflicts.Count == 1
            ? $"{conflicts[0].Sector} is already owned by {conflicts[0].Owner?.Callsign}. Request it from them?"
            : $"These are already owned by someone else: {string.Join(", ", conflicts.Select(c => $"{c.Sector} ({c.Owner?.Callsign})"))}. Request them?";

        if (AskYesNo(question, "Sector already owned"))
        {
            foreach (var conflict in conflicts)
            {
                var conflictSector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == conflict.Sector);
                if (conflictSector == null)
                    continue;

                try
                {
                    await _api.RequestSectorAsync(conflictSector.Name);
                }
                catch (Exception ex)
                {
                    Errors.Add(new Exception($"Couldn't request {conflictSector.Name} on OzServer: {ex.Message}"), "OzServer");
                }
            }
        }

        try
        {
            await _api.ClaimSectorAsync(sector.Name, conflicts.Select(c => c.Sector));
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't claim {sector.Name} on OzServer: {ex.Message}"), "OzServer");
        }
    }

    // _api's calls all run their continuations off the UI thread (see OzServerApiClient's own use
    // of ConfigureAwait(false)), and MMI.SectorsControlledChanged can itself fire off-thread too -
    // showing a dialog has to land back on the UI thread rather than risk a cross-thread control
    // access inside vatSys itself. Invoke (not BeginInvoke) since the caller needs the answer back
    // before deciding what to do next - safe to block on here since the calling thread is never the
    // UI thread itself (that's exactly the case this branch exists for).
    static bool AskYesNo(string message, string caption)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            return (bool)mainForm.Invoke(new Func<bool>(() => ShowYesNo(mainForm, message, caption)));

        return ShowYesNo(null, message, caption);
    }

    static bool ShowYesNo(IWin32Window? owner, string message, string caption)
    {
        var result = owner != null
            ? MessageBox.Show(owner, message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            : MessageBox.Show(message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        return result == DialogResult.Yes;
    }

    // Fire-and-forget UI-thread marshaling for callers (the MMI.SectorsControlledChanged
    // subscription) that don't need anything back - see AskYesNo above for the blocking variant
    // used when a return value is needed.
    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
