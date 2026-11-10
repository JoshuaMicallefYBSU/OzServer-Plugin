using System.Windows.Forms;

namespace OzServerPlugin;

// OzServerOwnershipTracker's "sector already owned - request it?" confirmation. The dark chat-style
// chrome it is drawn in comes from SectorMessageWindow; all this adds is the Yes/No pair.
//
// A one-shot window, not a reused singleton like OzServerSectorsWindow/OzServerSettingsWindow - a
// new instance is created each time a conflict needs asking about, so HideOnClose stays false:
// closing this one really should close it.
//
// Shown non-modally, like every other popup in the plugin (see OzServerOwnershipTracker's
// AskYesNoAsync for why it stopped being a ShowDialog). So the answer is carried on the window
// itself rather than in DialogResult, which only means anything for a modal form. "No" and the
// title bar's own close button are deliberately the same action - declining a request isn't a
// distinct outcome from just dismissing the prompt - so Answer starts false and only the Yes button
// ever changes it.
public class SectorConflictPromptWindow : SectorMessageWindow
{
    // Read when the window closes. True only if Yes was pressed.
    public bool Answer { get; private set; }

    public SectorConflictPromptWindow(string message, string caption) : base(message, caption)
    {
        Name = nameof(SectorConflictPromptWindow);

        AddButtonRow(
            CreateButton("Yes", () =>
            {
                Answer = true;
                Close();
            }),
            CreateButton("No", Close));
    }
}
