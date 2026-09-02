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
// differs between them: SectorConflictPromptWindow asks a Yes/No question, SectorNoticeWindow
// reports something that has already happened, SectorRequestPromptWindow offers Accept/Reject.
public abstract class SectorMessageWindow : BaseForm
{
    const int DialogWidth = 380;
    const int Edge = 10;
    const int ButtonWidth = 100;
    const int ButtonHeight = 30;
    const int ButtonGap = 16;
    // A one-line notice should not be a half-empty box, and a long sector list should not be hidden
    // behind a scrollbar - but neither should a runaway message grow the window off the screen.
    const int MinMessageHeight = 56;
    const int MaxMessageHeight = 320;

    readonly int _buttonRowY;

    protected SectorMessageWindow(string message, string caption)
    {
        Text = caption;
        MiddleClickClose = false;
        HasCloseButton = true;
        HideOnClose = false;
        Resizeable = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;

        // CenterScreen, not CenterParent. CenterParent only takes effect for ShowDialog, and every
        // one of these is deliberately shown non-modally with Show() - a modal dialog freezes the
        // whole vatSys UI thread while the controller is working traffic. So the old setting did
        // nothing at all and the windows opened wherever Windows happened to put them.
        StartPosition = FormStartPosition.CenterScreen;

        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

        // Set on the form so every child inherits it - the message pane and, more importantly, the
        // GenericButtons added later by AddButtonRow, which previously had no font set at all and
        // so rendered in the default WinForms font rather than vatSys's.
        Font = MMI.eurofont_winsml;

        var panelWidth = DialogWidth - Edge * 2;
        var textWidth = panelWidth - 8;

        // Measured with the static overload rather than CreateGraphics(): this runs before the form
        // has a handle, and forcing one just to measure text would realise the window early.
        var measured = TextRenderer.MeasureText(
            message, MMI.eurofont_winsml, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;

        var messageHeight = Math.Min(Math.Max(measured + 14, MinMessageHeight), MaxMessageHeight);

        _buttonRowY = Edge + messageHeight + Edge;
        ClientSize = new Size(DialogWidth, _buttonRowY + ButtonHeight + Edge);

        var insetPanel = new InsetPanel
        {
            Location = new Point(Edge, Edge),
            Margin = new Padding(3, 3, 1, 3),
            Name = "messageInsetPanel",
            Size = new Size(panelWidth, messageHeight),
            TabIndex = 0
        };

        var messageBox = new RichTextBox
        {
            BorderStyle = BorderStyle.None,
            Location = new Point(2, 2),
            Size = new Size(panelWidth - 4, messageHeight - 4),
            ReadOnly = true,
            TabStop = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = MMI.eurofont_winsml,
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
            Text = message
        };
        insetPanel.Controls.Add(messageBox);

        Controls.Add(insetPanel);
    }

    // Lays the buttons out as one centred row along the bottom, so a one-button dialog is centred
    // rather than inheriting the two-button layout's left-hand position. The row sits below the
    // message pane, wherever the measured message left it.
    protected void AddButtonRow(params GenericButton[] buttons)
    {
        var totalWidth = buttons.Length * ButtonWidth + (buttons.Length - 1) * ButtonGap;
        var x = (DialogWidth - totalWidth) / 2;

        foreach (var button in buttons)
        {
            button.Location = new Point(x, _buttonRowY);
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
