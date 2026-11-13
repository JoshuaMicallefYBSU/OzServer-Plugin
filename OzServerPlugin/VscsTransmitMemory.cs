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
// OzServer already restores the *sectors* across a disconnect (POST /sectors/resume), but the VSCS
// panel is vatSys's own state and comes back Idle every time. The controller was left owning
// airspace on OzServer, with the infill on their scope, and silently off every frequency for it -
// the one part of the position that does not announce it is missing, because a frequency you are
// not transmitting on looks exactly like one nobody is calling on.
//
// Written to %AppData% rather than beside the DLL, for the reason OzServerSettings gives: the
// vatSys Plugins folder is not guaranteed writable. Its own file rather than a key in settings.json
// because this is session scratch - it expires, it is rewritten on every transmit press, and losing
// it should never cost someone their backend URL.
//
// Saved on every change rather than on disconnect: a disconnect is not always something this plugin
// is told about in time to write a file - vatSys being killed, the machine losing power, a crash in
// another plugin's handler - and those are exactly the sessions a controller most wants back the
// way they left it.
public static class VscsTransmitMemory
{
    // How stale a memory may be before it is ignored. Comfortably past the backend's own resume
    // window (RESUME_WINDOW_MINUTES), so this is never the reason a resume comes back without its
    // frequencies, and short enough that tomorrow's session does not inherit today's panel.
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

    // The VSCS line names currently transmitting, recorded against the callsign they belong to. A
    // line is remembered by Name (the sector callsign vatSys builds the line from), which is what
    // identifies it again in a later session - the VSCSFrequency instances themselves do not
    // survive a position change, let alone a restart.
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

    // What was transmitting last time this callsign was connected, or an empty list. Keyed by
    // callsign: coming back as a different position is a different panel, and restoring the last
    // position's frequencies onto it would put the controller on frequencies belonging to airspace
    // that is not theirs.
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
            // A memory that cannot be read is simply a session that does not get its frequencies
            // back. Never worth an error in front of a controller, and never worth throwing into
            // whoever asked - this is read from a connect path.
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
            // Same rule as Read, and more so: this runs from a TransmitChanged handler, inside
            // vatSys's own VSCS click handling. A failed write costs the next reconnect its
            // frequencies; an exception here would cost the click.
        }
    }
}
