using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Where a controller types their answer to a developer message (DeveloperMessages). Laid out like
// SectorMessageWindow - an InsetPanel holding the text, a row of buttons beneath - with a text box in
// place of the message. Send stays open on a failure, with the reason, so nothing typed is lost.
public class DeveloperReplyWindow : BaseForm
{
    const int DialogWidth = 520;
    const int Edge = 10;
    const int LabelHeight = 20;
    const int TextHeight = 130;
    const int StatusHeight = 18;
    const int ButtonWidth = 110;
    const int ButtonHeight = 30;
    const int ButtonGap = 8;
    public const int MaxLength = 1000;

    static Font TextFont => MMI.eurofont_winverysml;

    readonly TextBox _text;
    readonly Label _status;
    readonly GenericButton _send;
    readonly Func<string, Task<string?>> _sendReply;

    // sendReply answers null when the reply arrived, otherwise the reason it did not.
    public DeveloperReplyWindow(string title, Func<string, Task<string?>> sendReply)
    {
        _sendReply = sendReply;

        Name = nameof(DeveloperReplyWindow);
        Text = "Reply to OzServer message";
        MiddleClickClose = false;
        HasCloseButton = true;
        HideOnClose = false;
        Resizeable = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

        var panelWidth = DialogWidth - Edge * 2;
        var y = Edge;

        var label = new Label
        {
            Location = new Point(Edge, y),
            Size = new Size(panelWidth, LabelHeight),
            Font = TextFont,
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
            Text = $"Re: {title}",
            AutoEllipsis = true
        };
        Controls.Add(label);
        y += LabelHeight;

        var panel = new InsetPanel
        {
            Location = new Point(Edge, y),
            Size = new Size(panelWidth, TextHeight),
            TabIndex = 0
        };
        _text = new TextBox
        {
            Location = new Point(2, 2),
            Size = new Size(panelWidth - 4, TextHeight - 4),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            MaxLength = MaxLength,
            Font = TextFont,
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText)
        };
        panel.Controls.Add(_text);
        Controls.Add(panel);
        y += TextHeight + 4;

        _status = new Label
        {
            Location = new Point(Edge, y),
            Size = new Size(panelWidth, StatusHeight),
            Font = TextFont,
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText)
        };
        Controls.Add(_status);
        y += StatusHeight + 4;

        _send = new GenericButton { Text = "Send" };
        _send.Click += async (_, _) => await SendAsync();
        var cancel = new GenericButton { Text = "Cancel" };
        cancel.Click += (_, _) => Close();

        var x = (DialogWidth - (ButtonWidth * 2 + ButtonGap)) / 2;
        _send.Location = new Point(x, y);
        _send.Size = new Size(ButtonWidth, ButtonHeight);
        cancel.Location = new Point(x + ButtonWidth + ButtonGap, y);
        cancel.Size = new Size(ButtonWidth, ButtonHeight);
        Controls.Add(_send);
        Controls.Add(cancel);

        ClientSize = new Size(DialogWidth, y + ButtonHeight + Edge);
    }

    // Raised once the reply has reached OzServer, just before this window closes.
    public event Action? Sent;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _text.Focus();
    }

    async Task SendAsync()
    {
        var body = _text.Text.Trim();
        if (body.Length == 0)
        {
            _status.Text = "Type a reply first.";
            return;
        }

        _send.Enabled = false;
        _status.Text = "Sending...";

        string? failure;
        try
        {
            failure = await _sendReply(body);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (IsDisposed)
            return;

        if (failure == null)
        {
            Sent?.Invoke();
            Close();
            return;
        }

        _send.Enabled = true;
        _status.Text = $"Not sent: {failure}";
    }
}
