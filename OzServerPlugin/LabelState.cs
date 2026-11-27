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
    // includeCPrompt: the C comes back to its owner on reconnect and goes with the tag on a sector
    // change, but not to a controller who picks the tag up any other way (a handoff, an assume) -
    // it is the previous controller's working mark, not part of the clearance.
    //
    // Returns what was written, for the log, or null if nothing was.
    public static string? Apply(FDP2.FDR fdr, OzServerFdrUpdateDto stored, bool replaceCfl, bool includeCPrompt = true)
    {
        if (!fdr.IsTrackedByMe)
            return null;

        var applied = new List<string>();

        var localCflSet = fdr.CFLUpper > 0 || fdr.CFLLower > 0;
        if (replaceCfl || !localCflSet)
        {
            var upper = stored.CflUpper ?? fdr.CFLUpper;
            var lower = stored.CflLower ?? fdr.CFLLower;
            if (fdr.CFLUpper != upper || fdr.CFLLower != lower)
            {
                // Sent, as the tracking controller: everyone connected gets it from vatSys at
                // once. Their own vatSys cleared it when the previous tracker dropped the tag
                // (FDP2.HandoffToNone), and the record has not changed, so LabelStateMirror would
                // not put it back by itself.
                FDP2.SetCFL(fdr, lower, upper, fdr.CFLVisual, true);
                applied.Add($"CFL {stored.CflLower?.ToString() ?? "-"}/{stored.CflUpper?.ToString() ?? "-"}");
            }
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
        // Off-track deviations, under the same rule as the CFL: a sector change takes the previous
        // controller's as they stand; a reconnect or pickup fills in only a side vatSys has none for.
        if (stored.OtdLeft is { } otdLeft && fdr.OTDLeft != otdLeft && (replaceCfl || fdr.OTDLeft == 0))
        {
            fdr.OTDLeft = otdLeft;
            applied.Add($"deviation left {otdLeft}");
        }
        if (stored.OtdRight is { } otdRight && fdr.OTDRight != otdRight && (replaceCfl || fdr.OTDRight == 0))
        {
            fdr.OTDRight = otdRight;
            applied.Add($"deviation right {otdRight}");
        }

        if (includeCPrompt && stored.CPrompt is { } cPrompt)
        {
            if (MMI.FindTrack(fdr) is { } track)
            {
                PendingCPrompt.TryRemove(fdr.Callsign, out _);
                if (track.CPrompt != cPrompt)
                {
                    track.CPrompt = cPrompt;
                    applied.Add(cPrompt ? "C on" : "C off");
                    MMI.RequestRedraw();
                }
            }
            else if (cPrompt)
            {
                // No track yet - straight after a reconnect vatSys has the FDR before the radar
                // track is coupled. Kept until there is one (ApplyPendingCPrompt): otherwise the
                // first push once it appears would report the C as off and erase the record.
                PendingCPrompt[fdr.Callsign] = true;
                applied.Add("C on (once the track appears)");
            }
        }

        // Times over passed waypoints, estimates and holds - see RouteTimesRestore.
        if (stored.RoutePoints is { Count: > 0 } points && RouteTimesRestore.Apply(fdr, points) is { } times)
            applied.Add(times);

        return applied.Count > 0 ? string.Join(", ", applied) : null;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> PendingCPrompt =
        new(StringComparer.OrdinalIgnoreCase);

    // Called by FdrSync as it reads the C for a push, on a tag this controller tracks.
    public static void ApplyPendingCPrompt(FDP2.FDR fdr, Track track)
    {
        if (string.IsNullOrEmpty(fdr.Callsign) || !PendingCPrompt.TryRemove(fdr.Callsign, out var cPrompt))
            return;
        if (track.CPrompt == cPrompt)
            return;

        track.CPrompt = cPrompt;
        MMI.RequestRedraw();
        ActionLog.Log("Tag", $"Put {fdr.Callsign}'s C back now its track has appeared", new
        {
            action = "pending_c_prompt_applied",
            fdr_callsign = fdr.Callsign
        });
    }

    public static void ForgetPending(string callsign) => PendingCPrompt.TryRemove(callsign, out _);

    public static void ForgetAllPending() => PendingCPrompt.Clear();
}

// Shows every controller the label the tracking controller has put on a tag: the cleared level,
// the scratchpad, global ops and off-track deviations. Not the "C", which stays the tracking
// controller's own (see ApplyOne).
//
// vatSys shares the first three only live, from whoever is tracking the tag to whoever is connected
// at that moment: a controller who connects later sees none of it until the tracking controller
// changes something, and deviations are never sent at all. OzServer holds them (FdrSync pushes them
// from the tracking controller's client), so every tag this controller is not tracking is filled
// in from there.
//
// Each field is applied only when OzServer's value changes, or the tag's tracking controller does,
// never re-asserted otherwise: a controller who changes one on someone else's tag for their own
// reference keeps it until the tracking controller changes theirs. Values are written locally, never sent - sending would announce them to the
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
        // Who was tracking the tag when this was recorded. A change starts the tag afresh: vatSys
        // resets the CFL when the tracking controller drops a tag (FDP2.HandoffToNone), while the
        // record keeps it, so with only changes applied the label would stay blank on this scope.
        public string? Tracker;
        public bool CflSeen;
        public int? CflLower;
        public int? CflUpper;
        public string? LabelOpData;
        public string? GlobalOpData;
        public int? OtdLeft;
        public int? OtdRight;
    }

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _timer;
    readonly Dictionary<string, Applied> _applied = new(StringComparer.OrdinalIgnoreCase);
    int _running;
    DateTime _lastRun = DateTime.MinValue;

    public LabelStateMirror()
    {
        MemoryBreakdown.Track("LabelStateMirror.applied", () => _applied.Count);

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
                var tracker = fdr.ControllerTracking?.Callsign;
                if (!_applied.TryGetValue(record.Callsign, out last)
                    || !string.Equals(last.Tracker, tracker, StringComparison.OrdinalIgnoreCase))
                    _applied[record.Callsign] = last = new Applied { Tracker = tracker };
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
        //
        // Not onto a tag nobody is tracking: vatSys clears the cleared level of a dropped tag on
        // every client (FDP2.HandoffToNone, and the DropTrack message), and nobody holds a clearance
        // for it. Written back, A77 showed its old level again on every scope the moment PH_APP
        // dropped it. Left unrecorded, so the level is taken up again once someone tracks the tag.
        if (fdr.ControllerTracking == null)
        {
            // Nothing to do for the cleared level.
        }
        else if (record.CflUpper != last.CflUpper || record.CflLower != last.CflLower)
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
            else if (last.CflSeen && (fdr.CFLUpper != -1 || fdr.CFLLower != -1))
            {
                // No cleared level, as the level menu's NONE leaves it (CFLMenu: SetCFL -1, -1) and
                // as vatSys shows it on every other scope, which are sent that -1.
                //
                // Not the RFL. That is what vatSys does with a TempAlt of 0, but 0 is what it sends
                // for a cleared level equal to the RFL (Network.SendCFL), not for none - and such a
                // level reaches the record as a level. Set to the RFL, OCI325 showed 360 on final
                // into YMML seconds after approach took its level off; and where the tag is tracked
                // outside OzServer, FdrSync.FillOutsideLabel then reported that 360 as the tracking
                // controller's own, so the record held it too.
                FDP2.SetCFL(fdr, -1, -1, fdr.CFLVisual, false);
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

        // Off-track deviations, 0 for none. As for the fields above, a side is written on first
        // sight only if OzServer has a deviation for it.
        if (record.OtdLeft is { } otdLeft && otdLeft != last.OtdLeft)
        {
            if ((last.OtdLeft != null || otdLeft != 0) && fdr.OTDLeft != otdLeft)
            {
                fdr.OTDLeft = otdLeft;
                changed = true;
            }
            last.OtdLeft = otdLeft;
        }
        if (record.OtdRight is { } otdRight && otdRight != last.OtdRight)
        {
            if ((last.OtdRight != null || otdRight != 0) && fdr.OTDRight != otdRight)
            {
                fdr.OTDRight = otdRight;
                changed = true;
            }
            last.OtdRight = otdRight;
        }

        // The C is deliberately not shown here: it is the tracking controller's own working mark,
        // kept on OzServer only so it comes back to them (reconnect) or goes with the tag to whoever
        // takes their sector (LabelStateRestore).

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
