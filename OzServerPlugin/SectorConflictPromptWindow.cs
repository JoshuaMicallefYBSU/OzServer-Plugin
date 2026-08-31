using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Replaces a plain MessageBox.Show(...) Yes/No prompt for OzServerOwnershipTracker's own
// "sector already owned - request it?" confirmation, styled to look like vatSys's own private
// message chat window (a dark, bordered message pane) rather than a bare OS dialog box - vatsys's
// own ChatWindow/ChatBox aren't public types a plugin can subclass or construct directly, so this
// rebuilds the same look (InsetPanel-bordered read-only message area, taking its font from
// MMI.eurofont_* and its colours from Colours.GetColour exactly as every other OzServer window
// here does) rather than reusing them.
//
// A one-shot dialog, not a reused singleton like OzServerSectorsWindow/OzServerSettingsWindow - a
// new instance is created and shown modally (ShowDialog, exactly like the MessageBox.Show call it
// replaces) each time a conflict needs asking about, so HideOnClose stays false: closing this one
// really should close it. "No" and the title bar's own close button are deliberately the same
// action - declining a request isn't a distinct outcome from just dismissing the prompt, so the
// close button is never wired to anything beyond the form's own default Close()/DialogResult.Cancel
// behaviour, and ShowYesNo (OzServerOwnershipTracker) already treats anything other than
// DialogResult.Yes as "no".
public class SectorConflictPromptWindow : BaseForm
{
    public SectorConflictPromptWindow(string message, string caption)
    {
        Text = caption;
        Name = nameof(SectorConflictPromptWindow);
        MiddleClickClose = false;
        HasCloseButton = true;
        HideOnClose = false;
        Resizeable = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 220);
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

        var yesButton = new GenericButton
        {
            Location = new Point(90, 172),
            Size = new Size(80, 30),
            Text = "Yes",
        };
        yesButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Yes;
            Close();
        };

        var noButton = new GenericButton
        {
            Location = new Point(190, 172),
            Size = new Size(80, 30),
            Text = "No",
        };
        noButton.Click += (_, _) => Close();

        Controls.Add(insetPanel);
        Controls.Add(yesButton);
        Controls.Add(noButton);
    }
}
