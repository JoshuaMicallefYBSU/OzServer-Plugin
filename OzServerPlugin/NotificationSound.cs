using System;
using System.IO;
using System.Media;
using vatsys;

namespace OzServerPlugin;

// The sound an incoming sector request makes, alongside the Settings header's flash.
//
// Tone2.wav is vatSys's own: Audio.Network_ATCMessagesChanged plays it when another controller
// sends an ATC message, and a sector request is the same kind of event.
//
// Not Audio.PlayAlert: that is the alert engine, whose P1 to P4 priorities are the conflict and
// safety sounds, and a sector request must never sound like an STCA. It is also internal, and
// loops until StopAlert is called.
//
// Played through vatSys's VSCS sound engine, the way vatSys plays Tone2 itself for an ATC message
// (Audio.PlayVSCSSound): into the controller's headset, where they will hear it. On the default
// output device instead, ML-HYD_CTR - on a headset - did not hear ML-ASP_CTR's request arrive. The
// engine is private to vatSys, so it is reached by reflection; if this vatSys build does not have
// it in that shape, the sound falls back to the default output device.
public static class NotificationSound
{
    static readonly System.Reflection.FieldInfo? AudioInstance =
        typeof(Audio).GetField("Instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    static readonly System.Reflection.FieldInfo? Tone2 =
        typeof(Audio).GetField("tone2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    static readonly System.Reflection.MethodInfo? PlayVscsSound =
        typeof(Audio).GetMethod("PlayVSCSSound", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    // Through the VSCS engine, as vatSys plays its own ATC-message tone. False if that is not
    // available, or failed.
    static bool TryPlayThroughVscs()
    {
        try
        {
            var audio = AudioInstance?.GetValue(null);
            var tone = audio == null ? null : Tone2?.GetValue(audio);
            if (audio == null || tone == null || PlayVscsSound == null)
                return false;

            var parameters = PlayVscsSound.GetParameters();
            PlayVscsSound.Invoke(audio, parameters.Length == 2 ? new[] { tone, false } : new[] { tone });
            return true;
        }
        catch (Exception ex)
        {
            ActionLog.Log("Sound", $"could not play the request sound through VSCS, using the default device: {ex.Message}");
            return false;
        }
    }

    // Relative to Helpers.GetProgramFolder(), where vatSys loads its own sounds from, so it
    // follows a non-default install.
    const string RequestSoundFile = @"\wav\Tone2.wav";

    static readonly object Lock = new();
    static SoundPlayer? _player;
    static bool _loadAttempted;

    // Called once per new request group, not per poll - see Plugin.AnnounceNewRequestGroups.
    public static void PlayRequestArrived()
    {
        if (TryPlayThroughVscs())
            return;

        try
        {
            var player = Player();

            // Play, not PlaySync: this runs on the UI thread, which PlaySync would block for the
            // length of the sample.
            player?.Play();
        }
        catch (Exception ex)
        {
            // A missing or unplayable file must not affect the request itself. The flash and the
            // badge carry the notification; the sound is an addition to them.
            ActionLog.Log("Sound", $"could not play request notification: {ex.Message}");
        }
    }

    static SoundPlayer? Player()
    {
        lock (Lock)
        {
            // Loaded once and kept, so the file is not read on the UI thread for every request.
            if (_loadAttempted)
                return _player;

            _loadAttempted = true;

            var path = Helpers.GetProgramFolder() + RequestSoundFile;
            if (!File.Exists(path))
            {
                ActionLog.Log("Sound", $"request notification sound not found at {path}");
                return null;
            }

            var player = new SoundPlayer(path);

            // Loaded up front so a corrupt file fails here, once, rather than on every request.
            player.Load();
            _player = player;

            return _player;
        }
    }
}
