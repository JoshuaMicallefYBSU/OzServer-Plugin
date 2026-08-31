using System.Windows.Forms;

namespace OzServerPlugin;

// OzServerOwnershipTracker's "sector already owned - request it?" confirmation. The dark chat-style
// chrome it is drawn in comes from SectorMessageWindow; all this adds is the Yes/No pair.
//
// A one-shot dialog, not a reused singleton like OzServerSectorsWindow/OzServerSettingsWindow - a
// new instance is created and shown modally (ShowDialog, exactly like the MessageBox.Show call it
// originally replaced) each time a conflict needs asking about, so HideOnClose stays false: closing
// this one really should close it. "No" and the title bar's own close button are deliberately the
// same action - declining a request isn't a distinct outcome from just dismissing the prompt, so
// the close button is never wired to anything beyond the form's own default Close()/
// DialogResult.Cancel behaviour, and ShowYesNo (OzServerOwnershipTracker) already treats anything
// other than DialogResult.Yes as "no".
public class SectorConflictPromptWindow : SectorMessageWindow
{
    public SectorConflictPromptWindow(string message, string caption) : base(message, caption)
    {
        Name = nameof(SectorConflictPromptWindow);

        AddButtonRow(
            CreateButton("Yes", () =>
            {
                DialogResult = DialogResult.Yes;
                Close();
            }),
            CreateButton("No", Close));
    }
}
