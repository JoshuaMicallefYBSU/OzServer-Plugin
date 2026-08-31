using System;
using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shared chrome for the plugin's small message dialogs, styled to look like vatSys's own private
// message chat window (a dark, bordered message pane) rather than a bare OS message box - vatsys's
// own ChatWindow/ChatBox aren't public types a plugin can subclass or construct directly, so this
// rebuilds the same look (InsetPanel-bordered read-only message area, taking its font from
// MMI.eurofont_* and its colours from Colours.GetColour exactly as every other OzServer window
// here does) rather than reusing them.
//
// Subclasses supply only the buttons along the bottom, which is the only thing that actually
// differs between them: SectorConflictPromptWindow asks a Yes/No question,
// SectorRelinquishNoticeWindow just reports something that has already happened.
public abstract class SectorMessageWindow : BaseForm
{
    const int DialogWidth = 360;
    const int ButtonWidth = 80;
    const int ButtonHeight = 30;
    const int ButtonGap = 20;
    const int ButtonRowY = 172;

    protected SectorMessageWindow(string message, string caption)
    {
        Text = caption;
        MiddleClickClose = false;
        HasCloseButton = true;
        HideOnClose = false;
        Resizeable = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(DialogWidth, 220);
        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

        var insetPanel = new InsetPanel
        {
            Location = new Point(10, 10),
            Margin = new Padding(3, 3, 1, 3),
            Name = "messageInsetPanel",
            Size = new Size(340, 150),
            TabIndex = 0
        };

        var messageBox = new RichTextBox
        {
            BorderStyle = BorderStyle.None,
            Location = new Point(2, 2),
            Size = new Size(336, 146),
            ReadOnly = true,
            TabStop = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = MMI.eurofont_winverysml,
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
            Text = message
        };
        insetPanel.Controls.Add(messageBox);

        Controls.Add(insetPanel);
    }

    // Lays the buttons out as one centred row along the bottom, so a one-button dialog is centred
    // rather than inheriting the two-button layout's left-hand position.
    protected void AddButtonRow(params GenericButton[] buttons)
    {
        var totalWidth = buttons.Length * ButtonWidth + (buttons.Length - 1) * ButtonGap;
        var x = (DialogWidth - totalWidth) / 2;

        foreach (var button in buttons)
        {
            button.Location = new Point(x, ButtonRowY);
            button.Size = new Size(ButtonWidth, ButtonHeight);
            Controls.Add(button);
            x += ButtonWidth + ButtonGap;
        }
    }

    protected static GenericButton CreateButton(string text, Action onClick)
    {
        var button = new GenericButton { Text = text };
        button.Click += (_, _) => onClick();
        return button;
    }
}
