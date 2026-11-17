using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using vatsys;

namespace OzServerPlugin;

// Remembers which VSCS lines were transmitting, so a reconnect puts the controller back on the
// frequencies they were working.
//
// OzServer restores the sectors across a disconnect (POST /sectors/resume), but the VSCS panel is
// vatSys's own state and comes back Idle. Without this a resumed controller owns the airspace and
// is off every frequency for it, with nothing on the panel to show that.
//
// Written to %AppData% because the vatSys Plugins folder is not guaranteed writable, and to a file
// of its own because it is session state: it expires and is rewritten on every transmit press.
//
// Saved on every change rather than on disconnect: the plugin is not told about every disconnect in
// time to write a file - vatSys being killed, the machine losing power, a crash in another
// plugin's handler.
public static class VscsTransmitMemory
{
    // How stale a memory may be before it is ignored. Well past the backend's resume window
    // (RESUME_WINDOW_MINUTES), and short enough that the next day's session does not inherit this
    // one's panel.
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    static readonly string MemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OzServerPlugin", "vscs-transmit.json");

    static readonly object Gate = new();

    class Memory
    {
        [JsonProperty("callsign")] public string Callsign { get; set; } = "";
        [JsonProperty("saved_at")] public DateTime SavedAt { get; set; }
        [JsonProperty("lines")] public List<string> Lines { get; set; } = new();
    }

    // Records the VSCS lines currently transmitting against this session's callsign. A line is
    // remembered by Name (the sector callsign vatSys builds the line from): the VSCSFrequency
    // instances do not survive a position change or a restart.
    public static void Remember()
    {
        var callsign = Network.Me?.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return;

        var lines = Audio.VSCSFrequencies
            .Where(frequency => frequency.Transmit)
            .Select(frequency => frequency.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Write(new Memory { Callsign = callsign!, SavedAt = DateTime.UtcNow, Lines = lines });
    }

    // What was transmitting when this callsign was last connected, or an empty list. Keyed by
    // callsign: a different position has a different panel, and the previous position's
    // frequencies belong to airspace that is not this controller's.
    public static IReadOnlyList<string> Recall(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return Array.Empty<string>();

        var memory = Read();

        if (memory == null
            || !string.Equals(memory.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
            || DateTime.UtcNow - memory.SavedAt > MaxAge)
        {
            return Array.Empty<string>();
        }

        return memory.Lines;
    }

    static Memory? Read()
    {
        try
        {
            lock (Gate)
            {
                return !File.Exists(MemoryPath)
                    ? null
                    : JsonConvert.DeserializeObject<Memory>(File.ReadAllText(MemoryPath));
            }
        }
        catch (Exception)
        {
            // An unreadable memory means a session that does not get its frequencies back. Read
            // from a connect path, so it neither throws nor reports an error.
            return null;
        }
    }

    static void Write(Memory memory)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MemoryPath)!);
                File.WriteAllText(MemoryPath, JsonConvert.SerializeObject(memory, Formatting.Indented));
            }
        }
        catch (Exception)
        {
            // Runs from a TransmitChanged handler, inside vatSys's own VSCS click handling. A failed
            // write costs the next reconnect its frequencies; an exception would cost the click.
        }
    }
}
