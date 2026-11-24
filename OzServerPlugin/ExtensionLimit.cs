using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using vatsys;

namespace OzServerPlugin;

// How many enroute positions one controller may work at once (issue #11): their own primary plus
// four extensions, or six once they have 150 hours controlling at C1 or above. Each position is one
// VSCS frequency - a primary with its sub-sectors counts once, a sub-sector held on its own counts
// once - so the limit is also what keeps the VSCS panel workable.
//
// Hours come from VATSIM's public member statistics (c1 and every rating above it), fetched once
// per connection. Until they arrive, or if VATSIM cannot be reached, the higher limit applies: a
// slow API must never stop a controller extending.
//
// Enforced here, in the plugin, wherever this controller adds a position themselves: a transmit
// press (AfvSectorClaimer.CheckActive), the Sectors window (OzServerSectorsWindow.StageSectorChange)
// and a request (OzServerOwnershipTracker.RequestHeldSectorAsync). Never on a logon, a resume or a
// handover the controller did not ask for, and never by taking anything away.
static class ExtensionLimit
{
    const double ExperiencedHours = 150;
    const int Extensions = 4;
    const int ExperiencedExtensions = 6;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Null until known for this connection.
    static double? _hours;
    static int _session;

    public static void Initialise()
    {
        Network.Connected += (_, _) =>
        {
            var session = ++_session;
            _hours = null;
            _ = FetchAsync(session);
        };
        Network.Disconnected += (_, _) => _session++;
    }

    // The logon primary included.
    public static int MaxPositions => 1 + (_hours is { } hours && hours < ExperiencedHours ? Extensions : ExperiencedExtensions);

    static async Task FetchAsync(int session)
    {
        // Network.Me lags Connected; the identity this session logged on with does not.
        var cid = 0;
        for (var i = 0; i < 20 && cid == 0; i++)
        {
            cid = NetworkIdentity.CidOrZero;
            if (cid == 0)
                await Task.Delay(500);
        }
        if (cid == 0 || session != _session)
            return;

        try
        {
            var body = await Http.GetStringAsync($"https://api.vatsim.net/v2/members/{cid}/stats").ConfigureAwait(false);
            var stats = JObject.Parse(body);
            var hours = new[] { "c1", "c2", "c3", "i1", "i2", "i3" }
                .Sum(rating => stats.Value<double?>(rating) ?? 0);
            if (session != _session)
                return;
            _hours = hours;
            ActionLog.Log("Primary", $"{hours:0} hours at C1 and above - up to {MaxPositions - 1} enroute extensions",
                new { action = "extension_limit", hours, max_positions = MaxPositions });
        }
        catch (Exception ex)
        {
            ActionLog.Log("Primary", $"Couldn't read controller hours from VATSIM ({ex.Message}) - allowing {MaxPositions - 1} extensions",
                new { action = "extension_limit_unknown", max_positions = MaxPositions });
        }
    }

    public static bool IsEnroute(SectorsVolumes.Sector sector) =>
        sector.Callsign is { } callsign
        && (callsign.EndsWith("_CTR", StringComparison.OrdinalIgnoreCase) || callsign.EndsWith("_FSS", StringComparison.OrdinalIgnoreCase));

    // The enroute positions among `held`: each sector not covered by another of them.
    public static List<SectorsVolumes.Sector> Positions(IEnumerable<SectorsVolumes.Sector> held)
    {
        var list = held.Where(s => s != null && !s.IsDummy && IsEnroute(s)).Distinct().ToList();
        return list
            .Where(s => !list.Any(other => !other.Equals(s) && PrimaryPosition.CoveredBy(other).Any(c => c.Equals(s))))
            .ToList();
    }

    // Whether adding `sector` to what is `held` stays within the limit. Anything that does not add
    // a position - TMA or tower airspace, a sector already covered, a group taking in positions
    // already held - is always allowed.
    public static bool Allows(SectorsVolumes.Sector sector, IEnumerable<SectorsVolumes.Sector> held)
    {
        var before = held.ToList();
        var after = Positions(before.Append(sector)).Count;
        return after <= MaxPositions || after <= Positions(before).Count;
    }

    // "Unable to add sector XXX, Maximum of 8 ENR Sectors has already been reached." - the wording
    // issue #11 asks for, with this controller's own maximum.
    public static void ReportFull(SectorsVolumes.Sector sector)
    {
        ActionLog.Log("Primary", $"Not adding {sector.Name} - already at {MaxPositions} enroute positions",
            new { action = "extension_limit_reached", sector = sector.Name, max_positions = MaxPositions, hours = _hours });

        var message = $"Unable to add sector {sector.Name}, Maximum of {MaxPositions} ENR Sectors has already been reached."
                      + Environment.NewLine + Environment.NewLine
                      + (_hours is { } hours && hours < ExperiencedHours
                          ? $"Controllers with under {ExperiencedHours:0} hours at C1 and above may work their own position and {Extensions} extensions. Refer ATS Policy 2.13.3 for details."
                          : $"Controllers may work their own position and {ExperiencedExtensions} extensions. Refer ATS Policy 2.13.3 for details.")
                      + " Release a sector first to add another.";

        MMI.InvokeOnGUI(() =>
        {
            var notice = new SectorNoticeWindow(message, "Maximum sectors reached");
            if (System.Windows.Forms.Application.OpenForms["MainForm"] is System.Windows.Forms.Form mainForm)
                notice.Show(mainForm);
            else
                notice.Show();
            notice.BringToFront();
        });
    }
}
