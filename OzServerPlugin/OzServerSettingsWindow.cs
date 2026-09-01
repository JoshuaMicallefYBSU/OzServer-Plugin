using System;
using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

public class OzServerSettingsWindow : BaseForm
{
    readonly TextBox _baseUrlBox;
    readonly TextLabel _statusLabel;
    readonly GenericButton _saveButton;
    readonly GenericButton _resetButton;
    readonly GenericButton _closeButton;

    public OzServerSettingsWindow()
    {
        Text = "OzServer Settings";
        Name = nameof(OzServerSettingsWindow);
        HasCloseButton = true;
        HideOnClose = true;
        Resizeable = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 140);
        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

        var label = new TextLabel
        {
            AutoSize = true,
            HasBorder = false,
            InteractiveText = false,
            ForeColor = Colours.GetColour(Colours.Identities.GenericText),
            Location = new Point(12, 14),
            Text = "OzServer Base URL:",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _baseUrlBox = new TextBox
        {
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            BorderStyle = BorderStyle.FixedSingle,
            Font = MMI.eurofont_winverysml,
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
            Location = new Point(12, 36),
            Size = new Size(416, 24)
        };

        _statusLabel = new TextLabel
        {
            AutoSize = true,
            HasBorder = false,
            InteractiveText = false,
            ForeColor = Colours.GetColour(Colours.Identities.GenericText),
            Location = new Point(12, 68),
            Text = "",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _resetButton = new GenericButton
        {
            Location = new Point(12, 96),
            Size = new Size(120, 30),
            Text = "Reset",
        };
        _resetButton.Click += (_, _) => _baseUrlBox.Text = OzServerSettings.DefaultBaseUrl;

        _saveButton = new GenericButton
        {
            Location = new Point(264, 96),
            Size = new Size(80, 30),
            Text = "Save",
        };
        _saveButton.Click += SaveButton_Click;

        _closeButton = new GenericButton
        {
            Location = new Point(348, 96),
            Size = new Size(80, 30),
            Text = "Close",
        };
        _closeButton.Click += (_, _) => Close();

        Controls.Add(label);
        Controls.Add(_baseUrlBox);
        Controls.Add(_statusLabel);
        Controls.Add(_resetButton);
        Controls.Add(_saveButton);
        Controls.Add(_closeButton);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _baseUrlBox.Text = OzServerSettings.BaseUrl;
            _statusLabel.Text = "";
        }
    }

    void SaveButton_Click(object? sender, EventArgs e)
    {
        var url = _baseUrlBox.Text.Trim();
        // Shares OzServerSettings' own rule rather than re-checking here, so a URL typed into this
        // box and one hand-edited into settings.json are held to the same standard - see
        // IsValidBaseUrl for why an absolute-URI check on its own isn't enough.
        if (!OzServerSettings.IsValidBaseUrl(url, out var error))
        {
            _statusLabel.Text = error;
            return;
        }

        OzServerSettings.BaseUrl = url;
        _baseUrlBox.Text = OzServerSettings.BaseUrl;
        _statusLabel.Text = "Saved.";
    }
}
