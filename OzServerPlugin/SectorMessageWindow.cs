using System;
using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shared chrome for the plugin's message dialogs, matching OzServerSectorsWindow: an InsetPanel
// around the content, the Colours.GetColour palette, MMI.eurofont_* text and a row of buttons along
// the bottom.
//
// The dialog is a fixed landscape width, so a sector list wraps into few lines. Its height is
// measured from the message and clamped between MinMessageHeight and MaxMessageHeight.
//
// Subclasses supply only the buttons: SectorConflictPromptWindow asks Yes/No and
// SectorNoticeWindow reports with a single OK.
public abstract class SectorMessageWindow : BaseForm
{
    // Wide enough for a sector list to wrap sensibly. The panel and text widths derive from it.
    const int DialogWidth = 520;
    const int Edge = 10;
    // OzServerSectorsWindow's Apply/Cancel are 80x30; 90 wide here so "Accept" and "Reject" do not
    // fill the button edge to edge.
    const int ButtonWidth = 110;
    const int ButtonHeight = 30;
    const int ButtonGap = 8;
    const int MinMessageHeight = 76;
    const int MaxMessageHeight = 260;

    // Only the message sets a font; everything else inherits vatSys's defaults.
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

        // CenterScreen, not CenterParent, which only takes effect for ShowDialog. These windows are
        // shown with Show(): a modal dialog blocks the vatSys UI thread while the controller is
        // working traffic.
        StartPosition = FormStartPosition.CenterScreen;

        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

        // Font is deliberately not set on the form: the buttons would inherit it in place of
        // GenericButton's own default and no longer match the other buttons in vatSys.

        var panelWidth = DialogWidth - Edge * 2;
        var textWidth = panelWidth - 8;

        // The static overload rather than CreateGraphics(): the form has no handle yet, and
        // creating one to measure text would realise the window early.
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

        // Vertically centred when the message is shorter than the pane. A message long enough to
        // fill the pane scrolls, and the offset is zero.
        var innerHeight = messageHeight - 4;
        var textHeight = Math.Min(measured, innerHeight);
        var textTop = 2 + Math.Max(0, (innerHeight - textHeight) / 2);

        // Inset by 2 inside the panel, as each TreeViewEx in OzServerSectorsWindow is, so the
        // panel's border stays visible.
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
    // applied once there is a handle - in the constructor it does nothing. The deselect matters: a
    // remaining selection would render as a highlight block.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _messageBox.SelectAll();
        _messageBox.SelectionAlignment = HorizontalAlignment.Center;
        _messageBox.DeselectAll();
    }

    // Centred as one row along the bottom and laid out left to right, so
    // AddButtonRow(Accept, Reject) puts Accept on the left.
    //
    // Each button is ButtonWidth or as wide as its label on one line, whichever is more: at a fixed
    // ButtonWidth, GenericButton wrapped "Close vatSys" onto two lines in the update notice, and the
    // label could not be read.
    protected void AddButtonRow(params GenericButton[] buttons)
    {
        var widths = new int[buttons.Length];
        for (var i = 0; i < buttons.Length; i++)
            widths[i] = Math.Max(ButtonWidth, TextRenderer.MeasureText(buttons[i].Text ?? "", buttons[i].Font).Width + ButtonTextPadding);

        var totalWidth = 0;
        foreach (var width in widths)
            totalWidth += width;
        totalWidth += (buttons.Length - 1) * ButtonGap;
        var x = Math.Max(Edge, (DialogWidth - totalWidth) / 2);

        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            button.Location = new Point(x, _buttonRowY);
            button.Size = new Size(widths[i], ButtonHeight);
            button.TabIndex = i + 1;
            Controls.Add(button);
            x += widths[i] + ButtonGap;
        }
    }

    // Room either side of a button's label, so it does not touch the border.
    const int ButtonTextPadding = 24;

    protected static GenericButton CreateButton(string text, Action onClick)
    {
        var button = new GenericButton { Text = text };
        button.Click += (_, _) => onClick();
        return button;
    }
}
