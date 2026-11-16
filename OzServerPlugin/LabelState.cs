using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// What the tracking controller has put on a tag's label beyond the flight plan: the cleared level,
// the scratchpad, global ops and the "C". The CFL and the C are local to vatSys, and the scratchpad
// and global ops are only shared by whoever is tracking the tag at the time - see
// OzServerFdrUpdateDto.LabelOpData.
//
// FdrSync pushes all of it from the tracking controller's client. This puts it back onto a tag that
// has just become this controller's - by reconnect (TagResumeRecovery), by sector change
// (SectorTagHandoff) or by pickup (FdrSync.AdoptLabelAsync) - when vatSys starts the label from
// nothing.
public static class LabelStateRestore
{
    // replaceCfl: a sector change takes the previous controller's cleared level as it stands; a
    // reconnect or pickup fills it in only where vatSys has none, since anything this controller
    // has entered is newer than the record.
    //
    // Returns what was written, for the log, or null if nothing was.
    public static string? Apply(FDP2.FDR fdr, OzServerFdrUpdateDto stored, bool replaceCfl)
    {
        if (!fdr.IsTrackedByMe)
            return null;

        var applied = new List<string>();

        var localCflSet = fdr.CFLUpper > 0 || fdr.CFLLower > 0;
        if (replaceCfl || !localCflSet)
        {
            var cflChanged = false;
            if (stored.CflUpper is { } upper && fdr.CFLUpper != upper)
            {
                fdr.CFLUpper = upper;
                cflChanged = true;
            }
            if (stored.CflLower is { } lower && fdr.CFLLower != lower)
            {
                fdr.CFLLower = lower;
                cflChanged = true;
            }
            if (cflChanged)
                applied.Add($"CFL {stored.CflLower?.ToString() ?? "-"}/{stored.CflUpper?.ToString() ?? "-"}");
        }

        // Through vatSys's own setters, which also send the value to every other controller - as
        // the tracking controller, this client is the one vatSys expects to send it. Only into an
        // empty field: a value already here was shared by vatSys and is at least as new as the
        // record.
        if (!string.IsNullOrEmpty(stored.LabelOpData) && string.IsNullOrEmpty(fdr.LabelOpData))
        {
            FDP2.SetLabelData(fdr, stored.LabelOpData);
            applied.Add($"scratchpad \"{stored.LabelOpData}\"");
        }

        if (!string.IsNullOrEmpty(stored.GlobalOpData) && string.IsNullOrEmpty(fdr.GlobalOpData))
        {
            FDP2.SetGlobalOps(fdr, stored.GlobalOpData);
            applied.Add($"global ops \"{stored.GlobalOpData}\"");
        }

        // The CFL's colour (Track.NewCFL, new or acknowledged) is not carried: vatSys marks the CFL
        // new again whenever a track enters Jurisdiction, so it is left as vatSys shows it.
        if (MMI.FindTrack(fdr) is { } track && stored.CPrompt is { } cPrompt && track.CPrompt != cPrompt)
        {
            track.CPrompt = cPrompt;
            applied.Add(cPrompt ? "C on" : "C off");
            MMI.RequestRedraw();
        }

        return applied.Count > 0 ? string.Join(", ", applied) : null;
    }
}

// Shows every controller the label the tracking controller has put on a tag: the cleared level,
// the scratchpad, global ops and the "C".
//
// vatSys shares the first three only live, from whoever is tracking the tag to whoever is connected
// at that moment: a controller who connects later sees none of it until the tracking controller
// changes something, and the C is never sent at all. OzServer holds all four (FdrSync pushes them
// from the tracking controller's client), so every tag this controller is not tracking is filled
// in from there.
//
// Each field is applied only when OzServer's value changes, never re-asserted: a controller who
// changes one on someone else's tag for their own reference keeps it until the tracking controller
// changes theirs. Values are written locally, never sent - sending would announce them to the
// network as this controller's own.
public sealed class LabelStateMirror
{
    // A signal is a hint: FDR pushes arrive several a second across all controllers, and each
    // would otherwise fetch the whole set. The timer covers a signal that never arrives.
    static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(15);

    // What was last put onto each tag from OzServer, field by field.
    sealed class Applied
    {
        public bool? CPrompt;
        public bool CflSeen;
        public int? CflLower;
        public int? CflUpper;
        public string? LabelOpData;
        public string? GlobalOpData;
    }

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _timer;
    readonly Dictionary<string, Applied> _applied = new(StringComparer.OrdinalIgnoreCase);
    int _running;
    DateTime _lastRun = DateTime.MinValue;

    public LabelStateMirror()
    {
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, FallbackInterval, FallbackInterval);

        OzServerEventStream.Shared.EventReceived += name =>
        {
            if (name == "fdr")
                _ = RefreshAsync();
        };

        Network.Disconnected += (_, _) =>
        {
            lock (_applied)
                _applied.Clear();
        };
    }

    async Task RefreshAsync()
    {
        if (OzServerMode.Disabled || !Network.IsConnected || NetworkServer.Current == null)
            return;
        if (DateTime.UtcNow - _lastRun < MinInterval)
            return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        try
        {
            _lastRun = DateTime.UtcNow;
            var records = await _api.GetAllFlightsAsync();
            RunOnUiThread(() => Apply(records));
        }
        catch (Exception ex)
        {
            ErrorThrottle.Report("label-mirror", $"Couldn't read label state from OzServer: {ex.Message}", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    void Apply(List<OzServerFdrRecordDto> records)
    {
        if (OzServerMode.Disabled)
            return;

        var fdrs = FDP2.GetFDRs
            .Where(f => !string.IsNullOrEmpty(f.Callsign))
            .GroupBy(f => f.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var redraw = false;
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.Callsign))
                continue;
            if (!fdrs.TryGetValue(record.Callsign, out var fdr))
                continue;

            Applied? last;
            lock (_applied)
            {
                // A tag this controller tracks is reported by FdrSync. Forgotten here so that, once
                // it is handed on, the next controller's label is taken in full.
                if (fdr.IsTrackedByMe)
                {
                    _applied.Remove(record.Callsign);
                    continue;
                }
                if (!_applied.TryGetValue(record.Callsign, out last))
                    _applied[record.Callsign] = last = new Applied();
            }

            try
            {
                redraw |= ApplyOne(fdr, record, last!);
            }
            catch (Exception ex)
            {
                ErrorThrottle.Report("label-mirror-apply", $"Couldn't show {record.Callsign}'s label from OzServer: {ex.Message}", ex);
            }
        }

        if (redraw)
            MMI.RequestRedraw();
    }

    // The first time a tag is seen, only what OzServer holds is written - an empty field is left
    // alone, since a value already here came from vatSys and is at least as new. After that, any
    // change is written, a cleared field included.
    static bool ApplyOne(FDP2.FDR fdr, OzServerFdrRecordDto record, Applied last)
    {
        var changed = false;

        // A record states its cleared level only alongside the rest of the label (see
        // OzServerFdrUpdateDto.ClearanceKnown), so null after a level has been seen means cleared.
        if (record.CflUpper != last.CflUpper || record.CflLower != last.CflLower)
        {
            if (record.CflUpper is { } upper)
            {
                var lower = record.CflLower ?? -1;
                if (fdr.CFLUpper != upper || fdr.CFLLower != lower)
                {
                    // sendMessage: false - shown here, not announced to the network as ours.
                    FDP2.SetCFL(fdr, lower, upper, fdr.CFLVisual, false);
                    changed = true;
                }
            }
            else if (last.CflSeen)
            {
                // What vatSys itself does on being told a level was cleared (a TempAlt of 0).
                FDP2.SetCFL(fdr, -1, fdr.RFL, fdr.CFLVisual, false);
                changed = true;
            }
            last.CflLower = record.CflLower;
            last.CflUpper = record.CflUpper;
            last.CflSeen |= record.CflUpper != null;
        }

        if (record.LabelOpData != null && record.LabelOpData != last.LabelOpData)
        {
            if ((last.LabelOpData != null || record.LabelOpData != "") && fdr.LabelOpData != record.LabelOpData)
            {
                fdr.LabelOpData = record.LabelOpData;
                changed = true;
            }
            last.LabelOpData = record.LabelOpData;
        }

        if (record.GlobalOpData != null && record.GlobalOpData != last.GlobalOpData)
        {
            if ((last.GlobalOpData != null || record.GlobalOpData != "") && fdr.GlobalOpData != record.GlobalOpData)
            {
                fdr.GlobalOpData = record.GlobalOpData;
                changed = true;
            }
            last.GlobalOpData = record.GlobalOpData;
        }

        if (record.CPrompt is { } cPrompt && cPrompt != last.CPrompt && MMI.FindTrack(fdr) is { } track)
        {
            if (track.CPrompt != cPrompt)
            {
                track.CPrompt = cPrompt;
                changed = true;
            }
            last.CPrompt = cPrompt;
        }

        return changed;
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
