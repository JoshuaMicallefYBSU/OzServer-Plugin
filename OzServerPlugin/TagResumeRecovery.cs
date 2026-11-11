using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Puts a controller's tags back after a disconnect, and nothing else.
//
// This is all that remains of the plugin's tag handling. TagOwnershipSync and FdrActivationSync -
// which moved tags between controllers, activated flights from server state, and offered pickups as
// aircraft crossed boundaries - were removed to be rebuilt from scratch. Nothing here decides who
// should hold a tag; it only restores what the backend has already confirmed was this controller's
// moments ago.
//
// What still has to exist for that to work, and why it is not "tag handling":
//
//   FdrSync keeps flight_data_records current, including controlling_cid and current_sector. The
//   resume endpoint is built entirely on those two columns - it hands back only flights that are
//   still uncontrolled and still inside a sector this controller holds - so without that push there
//   is nothing for a resume to restore and disconnect recovery cannot work at all.
//
// The backend has already applied every ownership rule by the time this runs (see
// POST /sectors/resume): a flight another controller picked up while this one was away stays with
// them, and one that has flown out of the airspace they just took back is not returned either. So
// the list arriving here is not a proposal to be re-checked - it is the answer, and this simply
// makes vatSys agree with it.
//
// The answer arrives long before vatSys can act on it, which is why this keeps trying rather than
// making one pass. The resume is answered a fraction of a second after Network.Connected. On a
// reconnect inside the same run that is fine - every FDR is still in memory. After vatSys itself
// has been restarted there is nothing in memory at all: flight plans, radar tracks, the sector
// grant and the server's "you are a valid controller" reply all trickle in over the following
// seconds. A single pass at that moment found no FDR by any of the returned callsigns, logged
// "none were restored" with nothing to say why, and never looked again - so closing and reopening
// vatSys lost every tag, while OzServer went on listing them as this controller's.
public class TagResumeRecovery
{
    // How often the outstanding tags are looked for again, and for how long after the resume. The
    // window has to outlast a cold start - connect, flight plans, a first radar return with Mode C -
    // without leaving a tag liable to be taken minutes later, when the controller may have decided
    // otherwise. Same cadence as SectorTagHandoff's deferred-handoff retry.
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(90);

    readonly OzServerOwnershipTracker _tracker;
    readonly System.Threading.Timer _retryTimer;

    // Guards the three fields below. Written from the tracker's continuation (wherever the resume's
    // await resumes), read from the retry timer's thread, and worked through on the UI thread.
    readonly object _gate = new();
    // Callsigns the backend handed back that vatSys does not hold yet, each with why it is still
    // outstanding - reported once at the end if it never comes back.
    readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    DateTime _deadline = DateTime.MinValue;
    // So "waiting on vatSys" is logged once per resume rather than every two seconds.
    bool _waitLogged;

    public TagResumeRecovery(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _tracker.TagsResumed += (_, callsigns) => Begin(callsigns);

        // A session that has ended has nothing to take back, and a list left over from it must not
        // be worked against whatever position this client connects as next.
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
    // the whole truth, not an addition to the previous one.
    void Begin(IReadOnlyList<string> callsigns)
    {
        lock (_gate)
        {
            _pending.Clear();
            foreach (var callsign in callsigns.Where(c => !string.IsNullOrEmpty(c)))
                _pending[callsign] = "no flight plan loaded";

            _deadline = DateTime.UtcNow + RetryWindow;
            _waitLogged = false;
        }

        if (callsigns.Count > 0)
            RunOnUiThread(Attempt);
    }

    // One pass over everything still outstanding. Always on the UI thread: it changes FDR and track
    // state, and being single-threaded there is also what keeps two passes from overlapping.
    void Attempt()
    {
        List<string> wanted;
        DateTime deadline;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;

            wanted = _pending.Keys.ToList();
            deadline = _deadline;
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

        // MMI.AcceptJurisdiction returns without doing anything until the server has confirmed this
        // session as a controller, which lags Connected by a round trip. Attempting before then
        // would leave every tag flashing in STATE_HANDOVER_FIRST until the next pass.
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

            // Someone else has it now. The backend's answer was true when it was given, but this
            // can run well over a minute later, and taking the tag at this point would be
            // accepting a handoff nobody offered.
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

            // Resolved against what this controller actually holds right now. Null covers three
            // things that all clear up on their own: the sector grant has not reached MMI yet, there
            // is no radar return for the aircraft yet, or there is one without Mode C under two
            // stacked sectors.
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

        // Said once, with the reason for each, so a log read mid-wait shows what is being waited on
        // rather than nothing at all.
        if (logWait)
            ActionLog.Log("Tag", $"Waiting to restore: {Describe(waiting)}");

        // Why each one failed, not just that it did - whether the aircraft never appeared, sat
        // outside this controller's sectors, or was in a state that could not be taken is the whole
        // question when a tag does not come back.
        if (expired && waiting.Count > 0)
            ActionLog.Log("Tag", $"Could not restore after {RetryWindow.TotalSeconds:0}s: {Describe(waiting)}");
    }

    static string Describe(Dictionary<string, string> waiting) =>
        string.Join(", ", waiting.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                                 .Select(entry => $"{entry.Key} ({entry.Value})"));

    // Takes jurisdiction without the controller ever seeing a handover, by completing the whole
    // transition inside one UI callback and only repainting at the end. These tags were this
    // controller's a moment ago and the backend has confirmed nobody else took them, so making them
    // flash for acceptance would be asking a question that has already been answered.
    //
    // The FDP2.Process call is the part that matters, and the part that took three attempts to get
    // right. HandoffFirst only queues the state change; accepting has nothing to resolve until that
    // has been through a process pass, which is why calling the two back to back left the flight
    // sitting in STATE_HANDOVER_FIRST, flashing.
    //
    // Returns whether it actually worked, checked against the resulting state rather than assumed -
    // two other orderings were tried and each failed in its own way.
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

        // Only now is anything painted, so the intermediate handover state never reaches the screen.
        var track = MMI.FindTrack(fdr);
        if (track != null)
            MMI.SetTrackState(track);

        return true;
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
