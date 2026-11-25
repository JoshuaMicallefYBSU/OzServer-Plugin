using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// A debug window for SELCAL32 (Selcal32.cs): a list of SELCAL16, SELCAL32 and invalid codes, or any
// code typed in, and three things to do with one:
//
//  - Check: what each layer makes of it now - which tones are SELCAL16 or SELCAL32, the frequency
//    AFV holds for each (missing means AFV would refuse it), and whether vatSys's SELCAL window
//    would accept it.
//  - Play: the tones synthesised here, through the speakers. AFV sends SELCAL to the frequency and
//    never plays it back to the controller, so this is the only way to hear one locally. It uses
//    AFV's own frequencies, so it is what AFV would transmit.
//  - Transmit: on the air, exactly as vatSys's SELCAL window sends it (Audio.TransmitSELCAL with the
//    four tones), reporting what AFV says. Audio only: no FSD SELCAL message is sent.
sealed class SelcalDebugWindow : BaseForm
{
    // Shown dash-separated, sent without the dash.
    static readonly (string Group, string Code)[] Samples =
    {
        ("SELCAL16", "AB-CD"), ("SELCAL16", "FG-HJ"), ("SELCAL16", "KM-PS"), ("SELCAL16", "AS-LR"),
        ("SELCAL32", "AT-1Z"), ("SELCAL32", "TU-VW"), ("SELCAL32", "XY-Z1"), ("SELCAL32", "23-45"),
        ("SELCAL32", "67-89"), ("SELCAL32", "A9-S8"),
        ("Invalid", "AI-CD"), ("Invalid", "A0-CD"), ("Invalid", "AO-NB"), ("Invalid", "AB-C"), ("Invalid", "AB-CDE")
    };

    // ICAO Annex 10: two tone pulses of 1.0 s, 0.2 s apart.
    const double PulseSeconds = 1.0;
    const double GapSeconds = 0.2;
    const int SampleRate = 44100;

    readonly ListBox _samples;
    readonly TextField _code;
    readonly TextBox _output;
    // This window's own, unlike vatSys's shared fonts: a control does not dispose the font it is
    // given, so it is disposed with the window.
    readonly Font _outputFont = new(FontFamily.GenericMonospace, 9f);
    SoundPlayer? _player;

    public SelcalDebugWindow()
    {
        Name = nameof(SelcalDebugWindow);
        Text = "SELCAL Debug";
        HasCloseButton = true;
        MiddleClickClose = false;
        KeyPreview = true;
        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
        ClientSize = new Size(560, 380);
        MinimumSize = new Size(480, 300);

        var font = MMI.eurofont_winsml;
        var text = Colours.GetColour(Colours.Identities.InteractiveText);

        _samples = new ListBox
        {
            Location = new Point(6, 6),
            Size = new Size(170, 368),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            Font = font,
            BackColor = BackColor,
            ForeColor = text
        };
        foreach (var (group, code) in Samples)
            _samples.Items.Add($"{group,-9} {code}");
        _samples.SelectedIndexChanged += (_, _) =>
        {
            if (_samples.SelectedIndex >= 0)
            {
                _code!.Text = Samples[_samples.SelectedIndex].Code;
                Check();
            }
        };
        _samples.DoubleClick += (_, _) => Play();

        var codeLabel = new Label { Text = "Code", Location = new Point(184, 10), AutoSize = true, Font = font, ForeColor = text };
        _code = new TextField
        {
            Location = new Point(226, 6),
            Width = 80,
            MaxLength = 8,
            CharacterCasing = CharacterCasing.Upper,
            Font = font,
            TakesReturn = true
        };
        _code.OnReturn += (_, _) => Check();

        var check = Button("Check", 314, Check);
        var play = Button("Play", 392, Play);
        var transmit = Button("Transmit on air", 470, Transmit);
        transmit.Width = 84;

        _output = new TextBox
        {
            Location = new Point(184, 40),
            Size = new Size(370, 334),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Font = _outputFont,
            BackColor = Colours.GetColour(Colours.Identities.StripBackground),
            ForeColor = Colours.GetColour(Colours.Identities.StripText)
        };

        Controls.AddRange(new Control[] { _samples, codeLabel, _code, check, play, transmit, _output });
        Log("Pick a code on the left (double-click plays it), or type one and press Check.");
        Log("Play is local only. Transmit sends the tones on your TX frequencies.");
    }

    GenericButton Button(string label, int x, Action onClick)
    {
        var button = new GenericButton { Text = label, Location = new Point(x, 5), Size = new Size(72, 24) };
        button.Click += (_, _) => onClick();
        return button;
    }

    // The four tones, as vatSys sends them: dash and spaces gone.
    string Tones => new string((_code.Text ?? "").ToUpperInvariant().Where(c => c != '-' && c != ' ').ToArray());

    void Check()
    {
        var tones = Tones;
        if (tones.Length == 0)
            return;
        Log("");
        foreach (var line in Selcal32.Diagnose(tones))
            Log(line);
    }

    void Play()
    {
        var tones = Tones;
        if (tones.Length == 0)
            return;
        Check();

        if (tones.Length != 4)
        {
            Log("Not played: a SELCAL code is four tones.");
            return;
        }

        // AFV's frequency where it has one: that is what goes out. A tone AFV lacks is played at
        // its ICAO frequency, and flagged, since AFV would refuse to send it.
        var hz = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (Selcal32.AfvFrequency(tones[i]) is { } afv)
                hz[i] = afv;
            else if (Selcal32.IcaoFrequency(tones[i]) is { } icao)
            {
                hz[i] = icao;
                Log($"  {tones[i]} is not in AFV's table - played at its ICAO {icao:0.0} Hz, but AFV would refuse it");
            }
            else
            {
                Log($"Not played: {tones[i]} is not a SELCAL tone.");
                return;
            }
        }

        try
        {
            _player?.Stop();
            _player?.Dispose();
            _player = new SoundPlayer(Synthesise(hz));
            _player.Play();
            Log($"Playing {tones.Substring(0, 2)} ({hz[0]:0.0} + {hz[1]:0.0} Hz), then {tones.Substring(2)} ({hz[2]:0.0} + {hz[3]:0.0} Hz)");
        }
        catch (Exception ex)
        {
            Log($"Couldn't play: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Exactly what vatSys's SELCAL window does on Send, minus the FSD message.
    void Transmit()
    {
        var tones = Tones;
        if (tones.Length == 0)
            return;
        Check();

        // The two checks vatSys's own SELCAL window makes before it sends (sendButton_Click). This
        // goes out on a live frequency, and Audio.TransmitSELCAL makes neither.
        if (!Network.ValidATC)
        {
            Log("Not transmitted: not connected as a valid ATC position.");
            return;
        }
        if (!AFV.IsConnected)
        {
            Log("Not transmitted: AFV is not connected.");
            return;
        }
        // After the AFV check: there are no VSCS frequencies to read before audio has started.
        if (!Audio.VSCSFrequencies.Any(f => f.Transmit))
        {
            Log("Not transmitted: no frequency is set to transmit.");
            return;
        }
        if (Audio.Transmitting)
        {
            Log("Not transmitted: already transmitting.");
            return;
        }

        try
        {
            Audio.TransmitSELCAL(tones);
            Log(Audio.SelcalTransmitting
                ? $"Transmitting {tones} on your TX frequencies - AFV accepted it."
                : $"Audio.TransmitSELCAL({tones}) returned without transmitting (AFV not started?).");
        }
        catch (Exception ex)
        {
            Log($"AFV refused {tones}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Two pulses of two summed sines, as 16-bit mono PCM in a WAV container, with short fades so the
    // edges do not click.
    static MemoryStream Synthesise(double[] hz)
    {
        var pulse = (int)(PulseSeconds * SampleRate);
        var gap = (int)(GapSeconds * SampleRate);
        var fade = SampleRate / 100;
        var samples = new short[pulse * 2 + gap];

        void WritePulse(int offset, double first, double second)
        {
            for (var i = 0; i < pulse; i++)
            {
                var t = (double)i / SampleRate;
                var envelope = Math.Min(1.0, Math.Min(i, pulse - 1 - i) / (double)fade);
                var value = 0.35 * (Math.Sin(2 * Math.PI * first * t) + Math.Sin(2 * Math.PI * second * t)) * envelope;
                samples[offset + i] = (short)(value * short.MaxValue);
            }
        }

        WritePulse(0, hz[0], hz[1]);
        WritePulse(pulse + gap, hz[2], hz[3]);

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = samples.Length * 2;
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataBytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);
            foreach (var sample in samples)
                writer.Write(sample);
        }
        stream.Position = 0;
        return stream;
    }

    void Log(string line)
    {
        _output.AppendText(line + Environment.NewLine);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _player?.Stop();
        _player?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        // After the controls, so nothing is still drawing with it.
        base.Dispose(disposing);
        if (disposing)
            _outputFont.Dispose();
    }
}
