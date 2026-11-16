using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Puts a controller's tags back after a disconnect. Nothing here decides who should hold a tag; it
// restores what the backend has confirmed is this controller's.
//
// The backend applies every ownership rule before answering (POST /sectors/resume): a flight
// another controller picked up in the meantime stays with them, and one that has flown out of the
// airspace this controller took back is not returned. It decides that from the controlling_cid and
// current_sector FdrSync keeps current on each record. The list arriving here is therefore the
// answer, and this makes vatSys agree with it.
//
// The answer arrives a fraction of a second after Network.Connected, usually before vatSys can act
// on it. After a vatSys restart nothing is in memory: flight plans, radar tracks, the sector grant
// and the server's confirmation of this session as a controller arrive over the following seconds.
// So outstanding tags are retried for RetryWindow rather than attempted once.
public class TagResumeRecovery
{
    // How often the outstanding tags are looked for again, and for how long after the resume. The
    // window has to outlast a cold start - connect, flight plans, a first radar return with Mode C -
    // without leaving a tag liable to be taken minutes later, when the controller may have decided
    // otherwise.
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(90);

    readonly OzServerOwnershipTracker _tracker;
    readonly System.Threading.Timer _retryTimer;

    // Guards the fields below, which are written from the tracker's continuation, read on the retry
    // timer's thread and worked through on the UI thread.
    readonly object _gate = new();
    // Callsigns the backend handed back that vatSys does not hold yet, each with the reason it is
    // still outstanding - logged if it never comes back.
    readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    DateTime _deadline = DateTime.MinValue;
    // So the wait is logged once per resume rather than on every retry.
    bool _waitLogged;

    // The label state OzServer held for each resumed tag (CFL, scratchpad, global ops, C), read
    // when the resume arrives - see FetchLabelStateAsync. _labelStateRead is false until that read
    // has finished, and no tag is restored before it has.
    readonly Dictionary<string, OzServerFdrUpdateDto> _labelState = new(StringComparer.OrdinalIgnoreCase);
    bool _labelStateRead = true;
    readonly OzServerApiClient _api = new();

    public TagResumeRecovery(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _tracker.TagsResumed += (_, callsigns) => Begin(callsigns);

        // A list left over from an ended session must not be worked against whatever position this
        // client connects as next.
        Network.Disconnected += (_, _) =>
        {
            lock (_gate)
                _pending.Clear();
        };

        _retryTimer = new System.Threading.Timer(_ =>
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                    return;
            }

            RunOnUiThread(Attempt);
        }, null, RetryInterval, RetryInterval);
    }

    // A new resume replaces whatever an earlier one left outstanding: the backend's latest answer is
    // complete, not an addition to the previous one.
    void Begin(IReadOnlyList<string> callsigns)
    {
        if (OzServerMode.Disabled)
            return;

        var wanted = callsigns.Where(c => !string.IsNullOrEmpty(c)).ToList();

        lock (_gate)
        {
            _pending.Clear();
            _labelState.Clear();
            _labelStateRead = wanted.Count == 0;
            foreach (var callsign in wanted)
                _pending[callsign] = "no flight plan loaded";

            _deadline = DateTime.UtcNow + RetryWindow;
            _waitLogged = false;
        }

        if (wanted.Count > 0)
        {
            _ = FetchLabelStateAsync(wanted);
            RunOnUiThread(Attempt);
        }
    }

    // A restored tag comes back with a bare label: the CFL and the C are vatSys's local state, and
    // the scratchpad and global ops are only sent by whoever is tracking the tag - nobody, while
    // this controller was away. OzServer has all of it (FdrSync pushes it), so it is read back.
    //
    // Read while the tag is not yet ours, and finished before any tag is restored (Attempt waits on
    // _labelStateRead): once this controller tracks a tag FdrSync pushes it, and a push states the
    // label outright - an empty scratchpad as "", no CFL as "none". A restore that beat this read
    // would have its first push erase the record it needs.
    static readonly TimeSpan LabelReadLimit = TimeSpan.FromSeconds(8);

    async System.Threading.Tasks.Task ReadLabelStateAsync(string callsign)
    {
        try
        {
            var flight = await _api.GetFlightAsync(callsign);
            if (flight == null)
                return;

            lock (_gate)
                _labelState[callsign] = flight;
        }
        catch (Exception ex)
        {
            ActionLog.Log("Tag", $"Couldn't read {callsign}'s label state for its restore: {ex.Message}");
        }
    }

    async System.Threading.Tasks.Task FetchLabelStateAsync(List<string> callsigns)
    {
        // Together, and not waited on past LabelReadLimit: one after another, a slow backend could
        // hold every tag back until the restore's own window had run out.
        var reads = System.Threading.Tasks.Task.WhenAll(callsigns.Select(ReadLabelStateAsync));
        await System.Threading.Tasks.Task.WhenAny(reads, System.Threading.Tasks.Task.Delay(LabelReadLimit));

        // Released even when a read failed: a tag restored without its label is better than one not
        // restored at all.
        lock (_gate)
            _labelStateRead = true;

        RunOnUiThread(Attempt);
    }

    // Puts the stored label state onto a just-restored tag, once.
    void ApplyLabelState(FDP2.FDR fdr)
    {
        OzServerFdrUpdateDto? stored;
        lock (_gate)
        {
            if (!_labelState.TryGetValue(fdr.Callsign, out stored))
                return;
            _labelState.Remove(fdr.Callsign);
        }

        var applied = LabelStateRestore.Apply(fdr, stored, replaceCfl: false);
        if (applied == null)
            return;

        ActionLog.Log("Tag", $"Put {fdr.Callsign}'s label back after reconnect: {applied}", new
        {
            action = "resume_label_state_applied",
            fdr_callsign = fdr.Callsign,
            cfl_lower = stored.CflLower,
            cfl_upper = stored.CflUpper,
            label_op_data = stored.LabelOpData,
            global_op_data = stored.GlobalOpData,
            c_prompt = stored.CPrompt
        });
    }

    // One pass over everything still outstanding. Always on the UI thread: it changes FDR and track
    // state, and running there also keeps two passes from overlapping.
    void Attempt()
    {
        if (OzServerMode.Disabled)
            return;

        List<string> wanted;
        DateTime deadline;
        bool labelStateRead;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;

            wanted = _pending.Keys.ToList();
            deadline = _deadline;
            labelStateRead = _labelStateRead;
        }

        if (!Network.IsConnected)
        {
            lock (_gate)
                _pending.Clear();
            return;
        }

        var restored = new List<string>();
        var abandoned = new List<string>();
        var waiting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var callsign in wanted)
            waiting[callsign] = "no flight plan loaded";

        // MMI.AcceptJurisdiction does nothing until the server has confirmed this session as a
        // controller, which lags Connected by a round trip. Attempting before then would leave
        // every tag flashing in STATE_HANDOVER_FIRST until the next pass.
        var canAccept = Network.ValidATC;
        var mine = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        foreach (var fdr in FDP2.GetFDRs)
        {
            if (string.IsNullOrEmpty(fdr.Callsign) || !waiting.ContainsKey(fdr.Callsign))
                continue;

            // Already back - taken by hand, or by an earlier pass whose result was not recorded.
            if (fdr.IsTrackedByMe)
            {
                waiting.Remove(fdr.Callsign);
                restored.Add(fdr.Callsign);
                continue;
            }

            // Someone else has it now. The backend's answer was true when given, but this can run
            // over a minute later, and taking the tag would be accepting a handoff nobody offered.
            if (fdr.IsTracked)
            {
                waiting.Remove(fdr.Callsign);
                abandoned.Add($"{fdr.Callsign} (now tracked by {fdr.ControllerTracking?.Callsign ?? "another controller"})");
                continue;
            }

            if (!canAccept)
            {
                waiting[fdr.Callsign] = "not yet confirmed as a controller";
                continue;
            }

            if (!labelStateRead)
            {
                waiting[fdr.Callsign] = "reading its label state from OzServer";
                continue;
            }

            // Resolved against what this controller holds now. Null covers three things that clear
            // up by themselves: the sector grant has not reached MMI, there is no radar return for
            // the aircraft, or there is one without Mode C under two stacked sectors.
            var sector = SectorLocator.Resolve(fdr, mine);
            if (sector == null)
            {
                waiting[fdr.Callsign] = mine.Count == 0 ? "no sectors held yet" : "no radar position inside a held sector";
                continue;
            }

            if (TryTakeJurisdiction(fdr, sector))
            {
                waiting.Remove(fdr.Callsign);
                restored.Add(fdr.Callsign);
                // In the same pass: FdrSync's first push of the now-tracked tag must carry the
                // label, not an empty one - see FetchLabelStateAsync.
                ApplyLabelState(fdr);
            }
            else
            {
                waiting[fdr.Callsign] = $"could not be taken, state {fdr.State}";
            }
        }

        var expired = DateTime.UtcNow >= deadline;
        bool logWait;
        lock (_gate)
        {
            // Begin may have replaced the list while this pass ran. Only what this pass was working
            // on is settled; anything it does not recognise belongs to the newer resume.
            foreach (var callsign in wanted.Where(c => !waiting.ContainsKey(c)))
                _pending.Remove(callsign);

            foreach (var entry in waiting)
            {
                if (!_pending.ContainsKey(entry.Key))
                    continue;

                if (expired)
                    _pending.Remove(entry.Key);
                else
                    _pending[entry.Key] = entry.Value;
            }

            logWait = !expired && waiting.Count > 0 && !_waitLogged;
            if (logWait)
                _waitLogged = true;
        }

        if (restored.Count > 0)
            ActionLog.Log("Tag", $"Restored after reconnect: {string.Join(", ", restored)}");

        if (abandoned.Count > 0)
            ActionLog.Log("Tag", $"Not restored: {string.Join(", ", abandoned)}");

        // Logged once, with the reason for each, so a log read mid-wait shows what is outstanding.
        if (logWait)
            ActionLog.Log("Tag", $"Waiting to restore: {Describe(waiting)}");

        // With the reason each one failed: the aircraft never appeared, was outside this
        // controller's sectors, or was in a state that could not be taken.
        if (expired && waiting.Count > 0)
            ActionLog.Log("Tag", $"Could not restore after {RetryWindow.TotalSeconds:0}s: {Describe(waiting)}");
    }

    static string Describe(Dictionary<string, string> waiting) =>
        string.Join(", ", waiting.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                                 .Select(entry => $"{entry.Key} ({entry.Value})"));

    // Takes jurisdiction without the controller seeing a handover: the whole transition completes
    // inside one UI callback and is painted only at the end. These tags were this controller's and
    // the backend has confirmed nobody else took them, so there is nothing to ask.
    //
    // The order matters. HandoffFirst only queues the state change, and AcceptJurisdiction has
    // nothing to resolve until FDP2.Process has run it - without the Process call the flight stays
    // flashing in STATE_HANDOVER_FIRST.
    //
    // Returns whether it worked, checked against the resulting state.
    static bool TryTakeJurisdiction(FDP2.FDR fdr, SectorsVolumes.Sector sector)
    {
        FDP2.HandoffFirst(fdr);
        fdr.HandoffSector = sector;

        FDP2.Process(fdr, true);

        MMI.AcceptJurisdiction(fdr);

        if (fdr.State != FDP2.FDR.FDRStates.STATE_CONTROLLED)
            return false;

        // Reasserted after the accept: vatSys resolves jurisdiction against its own default
        // geometry, which knows nothing about OzServer subsectors.
        fdr.ControllingSector = sector;

        // Painted only now, so the intermediate handover state never reaches the screen.
        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);

        return true;
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
