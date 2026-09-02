using System;
using System.IO;
using System.Media;
using vatsys;

namespace OzServerPlugin;

// The sound an incoming sector request makes, alongside the Settings header's flash.
//
// Tone2.wav is vatSys's own: Audio.Network_ATCMessagesChanged plays it (as PlayVSCSSound(tone2))
// when another controller sends an ATC message. A sector request is the same kind of event - another
// controller wants something from you and it is waiting - so it should sound like one rather than
// introduce a noise controllers have to learn.
//
// Deliberately not Audio.PlayAlert. That is the alert engine, its priorities are P1 to P4, and those
// are the conflict and safety sounds; a sector request must never sound like an STCA. PlayAlert is
// also internal and instance, so reaching it would mean reflecting onto Audio.Instance, and it loops
// until StopAlert is called - none of which suits a one-shot notification.
//
// Played through System.Media rather than through vatSys's audio engine on purpose: the engine mixes
// into the VSCS output device, which is the controller's headset and the thing they are listening to
// aircraft on. A UI notification belongs on the default output device, not in the radio path.
public static class NotificationSound
{
    // Under Helpers.GetProgramFolder(), which is where vatSys itself loads these from - not a path
    // of our own guessing, and it follows a non-default install.
    const string RequestSoundFile = @"\wav\Tone2.wav";

    static readonly object Lock = new();
    static SoundPlayer? _player;
    static bool _loadAttempted;

    // Fired once per new request group, not per poll - see Plugin.AnnounceNewRequestGroups.
    public static void PlayRequestArrived()
    {
        try
        {
            var player = Player();

            // Play, not PlaySync: this runs on the UI thread and PlaySync would block it for the
            // length of the sample, freezing the client mid-notification.
            player?.Play();
        }
        catch (Exception ex)
        {
            // A missing or unplayable sound file must not cost the controller the request itself.
            // The flash and the badge are the load-bearing part of this notification; the sound is
            // an addition to them.
            ActionLog.Log("Sound", $"could not play request notification: {ex.Message}");
        }
    }

    static SoundPlayer? Player()
    {
        lock (Lock)
        {
            // Loaded once and kept. LoadSoundFiles does the same thing on vatSys's side, and
            // re-reading the file on every request would put disk access on the UI thread.
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

            // Loaded up front so the first request is not the one that pays for reading the file,
            // and so a corrupt file fails here - once, into the log - rather than on every request.
            player.Load();
            _player = player;

            return _player;
        }
    }
}
