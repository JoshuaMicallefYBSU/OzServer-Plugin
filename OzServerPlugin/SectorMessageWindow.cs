using System;
using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shared chrome for the plugin's message dialogs, built to sit alongside OzServerSectorsWindow
// rather than look like a bare OS message box - same InsetPanel-bordered content area, same
// Colours.GetColour palette, same MMI.eurofont_* text, and the same bottom-right button row that
// window's Apply/Cancel pair uses.
//
// Proportions are deliberately landscape. A dialog that grows straight down ends up a tall narrow
// column for anything longer than a sentence, which reads nothing like vatSys's own windows; a wide
// box lets a sector list wrap into far fewer lines and keeps the shape rectangular whether it is
// carrying one line or ten.
//
// Height is measured from the message rather than fixed, then clamped: below the minimum a short
// notice would be a squat strip, above the maximum a long one would run off the screen, and between
// them the window is exactly as tall as it needs to be.
//
// Subclasses supply only the buttons, which is the only thing that actually differs between them:
// SectorConflictPromptWindow asks Yes/No and SectorNoticeWindow reports with a single OK.
public abstract class SectorMessageWindow : BaseForm
{
    // Wide enough for a sector list to wrap sensibly, and close to the proportions of vatSys's own
    // dialogs. Everything else is derived from it so there is one number to change.
    const int DialogWidth = 520;
    const int Edge = 10;
    // 80x30 is the size OzServerSectorsWindow uses for Apply/Cancel; 90 here so "Accept" and
    // "Reject" sit comfortably in Terminus rather than filling the button edge to edge.
    const int ButtonWidth = 90;
    const int ButtonHeight = 30;
    const int ButtonGap = 8;
    const int MinMessageHeight = 76;
    const int MaxMessageHeight = 260;

    // 14px, the same size the message pane has always used. Only the message sets a font at all -
    // everything else inherits vatSys's own defaults, as OzServerSectorsWindow does.
    static Font MessageFont => MMI.eurofont_winverysml;

    readonly int _buttonRowY;
    readonly RichTextBox _messageBox;

    protected SectorMessageWindow(string message, string caption)
    {
        Text = caption;
        MiddleClickClose = false;
        HasCloseButton = true;
        HideOnClose = false;
        Resizeable = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;

        // CenterScreen, not CenterParent: CenterParent only takes effect for ShowDialog, and these
        // are shown non-modally with Show() - a modal dialog freezes the whole vatSys UI thread
        // while the controller is working traffic. With CenterParent they simply opened wherever
        // Windows happened to put them.
        StartPosition = FormStartPosition.CenterScreen;

        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

        // Font is deliberately NOT set on the form. OzServerSectorsWindow never sets one anywhere -
        // GenericButton and the vatSys controls carry their own, and a GenericButton left alone is
        // Terminus 18px. Setting eurofont_winsml (16px) here inherited down into the buttons and
        // shrank them below every other button in vatSys, which is what stopped these dialogs
        // looking like part of the client.

        var panelWidth = DialogWidth - Edge * 2;
        var textWidth = panelWidth - 8;

        // Measured with the static overload rather than CreateGraphics(): this runs before the form
        // has a handle, and forcing one just to measure text would realise the window early.
        var measured = TextRenderer.MeasureText(
            message, MessageFont, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;

        var messageHeight = Math.Min(Math.Max(measured + 16, MinMessageHeight), MaxMessageHeight);

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

        // Vertically centred when the message is shorter than the pane - which it usually is, since
        // the pane has a minimum height so a one-line notice isn't a squat strip. A message long
        // enough to fill the pane simply fills it and scrolls, and the offset falls out at zero.
        var innerHeight = messageHeight - 4;
        var textHeight = Math.Min(measured, innerHeight);
        var textTop = 2 + Math.Max(0, (innerHeight - textHeight) / 2);

        // Inset by 2 inside the panel, the same way every TreeViewEx in OzServerSectorsWindow sits
        // inside its own InsetPanel, so the border reads as a border rather than a stripe.
        _messageBox = new RichTextBox
        {
            BorderStyle = BorderStyle.None,
            Location = new Point(2, textTop),
            Size = new Size(panelWidth - 4, textHeight),
            ReadOnly = true,
            TabStop = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = MessageFont,
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
            Text = message
        };
        insetPanel.Controls.Add(_messageBox);

        Controls.Add(insetPanel);
    }

    // RichTextBox alignment is a property of the selection, not of the control, so it can only be
    // applied once there is a handle to select within - doing it in the constructor silently does
    // nothing. Selecting everything, setting the alignment and deselecting is the standard way, and
    // the deselect matters: a left-over selection would render as a highlight block.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _messageBox.SelectAll();
        _messageBox.SelectionAlignment = HorizontalAlignment.Center;
        _messageBox.DeselectAll();
    }

    // Centred as one row along the bottom, so a single OK sits under the middle of the message
    // rather than off in a corner, and a Yes/No pair reads as a matched choice. Laid out left to
    // right, so AddButtonRow(Accept, Reject) puts Accept on the left - the order they are read in.
    protected void AddButtonRow(params GenericButton[] buttons)
    {
        var totalWidth = buttons.Length * ButtonWidth + (buttons.Length - 1) * ButtonGap;
        var x = (DialogWidth - totalWidth) / 2;

        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            button.Location = new Point(x, _buttonRowY);
            button.Size = new Size(ButtonWidth, ButtonHeight);
            button.TabIndex = i + 1;
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
