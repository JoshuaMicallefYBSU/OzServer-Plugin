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
// Played through System.Media rather than vatSys's audio engine, which mixes into the VSCS output
// device - the controller's headset. A UI notification belongs on the default output device, not
// in the radio path.
public static class NotificationSound
{
    // Relative to Helpers.GetProgramFolder(), where vatSys loads its own sounds from, so it
    // follows a non-default install.
    const string RequestSoundFile = @"\wav\Tone2.wav";

    static readonly object Lock = new();
    static SoundPlayer? _player;
    static bool _loadAttempted;

    // Called once per new request group, not per poll - see Plugin.AnnounceNewRequestGroups.
    public static void PlayRequestArrived()
    {
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
