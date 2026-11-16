using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// What the tracking controller has put on a tag's label, beyond the flight plan itself: the cleared
// level, the scratchpad, global ops, and the "C". See OzServerFdrUpdateDto.LabelOpData for what
// each one is and why vatSys loses it - in short, the CFL and the C are local to vatSys, and the
// scratchpad and global ops are only shared by whoever is tracking the tag at the time.
//
// FdrSync pushes all of it from the tracking controller's client. This puts it back onto a tag that
// has just become this controller's, by reconnect (TagResumeRecovery) or by sector change
// (SectorTagHandoff) - the two moments vatSys starts the label over from nothing.
public static class LabelStateRestore
{
    // replaceCfl: a sector change takes the previous controller's cleared level as it stands, since
    // they were the one working the aircraft a moment ago; a reconnect fills it in only where vatSys
    // has none, since anything the controller entered after reconnecting is newer than the record.
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

        // Through vatSys's own setters, which also send them on to every other controller - this
        // controller is the one tracking the tag now, so it is the one vatSys expects to say. Only
        // into an empty field: vatSys shares these itself between controllers, and a value already
        // here arrived that way and is at least as new as the record.
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

        // The CFL's colour (Track.NewCFL, new or acknowledged) is deliberately not carried: vatSys
        // marks the CFL new again whenever a track enters Jurisdiction, at a moment a restore cannot
        // reliably get behind, so it is left as vatSys shows it.
        if (MMI.FindTrack(fdr) is { } track && stored.CPrompt is { } cPrompt && track.CPrompt != cPrompt)
        {
            track.CPrompt = cPrompt;
            applied.Add(cPrompt ? "C on" : "C off");
            MMI.RequestRedraw();
        }

        return applied.Count > 0 ? string.Join(", ", applied) : null;
    }
}

// Shows other controllers the "C" a tracking controller has put on a label.
//
// vatSys keeps it on the Track and sends it nowhere, so without this it was visible only on the
// scope of the controller who set it. The tracking controller's client pushes it (FdrSync); this
// reads it back for every tag this controller is *not* tracking and puts it on the local track.
//
// Applied only when OzServer's value changes, never re-asserted over the top: a controller who
// toggles the C on someone else's tag for their own reference keeps it until the tracking controller
// changes theirs.
public sealed class LabelStateMirror
{
    // A signal is a hint, not an instruction - several FDR pushes a second across every controller
    // would otherwise each fetch the whole set. The timer covers a signal that never arrives.
    static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(15);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _timer;
    readonly Dictionary<string, bool> _applied = new(StringComparer.OrdinalIgnoreCase);
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
            if (record.CPrompt is not { } cPrompt || string.IsNullOrEmpty(record.Callsign))
                continue;
            if (!fdrs.TryGetValue(record.Callsign, out var fdr) || fdr.IsTrackedByMe)
                continue;
            if (MMI.FindTrack(fdr) is not { } track)
                continue;

            lock (_applied)
            {
                if (_applied.TryGetValue(record.Callsign, out var last) && last == cPrompt)
                    continue;
                _applied[record.Callsign] = cPrompt;
            }

            if (track.CPrompt != cPrompt)
            {
                track.CPrompt = cPrompt;
                redraw = true;
            }
        }

        if (redraw)
            MMI.RequestRedraw();
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
