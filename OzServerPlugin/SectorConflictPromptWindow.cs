using System.Windows.Forms;

namespace OzServerPlugin;

// A Yes/No prompt, such as OzServerOwnershipTracker's "sector already owned - request it?". The
// chrome comes from SectorMessageWindow; this adds the two buttons.
//
// A new instance is created for each question rather than reused, so HideOnClose stays false.
//
// Shown non-modally (see OzServerOwnershipTracker.AskYesNoAsync), so the answer is carried on the
// window rather than in DialogResult, which only applies to a modal form. No and the title bar's
// close button are the same action: Answer starts false and only the Yes button sets it.
public class SectorConflictPromptWindow : SectorMessageWindow
{
    // Read when the window closes. True only if Yes was pressed.
    public bool Answer { get; private set; }

    public SectorConflictPromptWindow(string message, string caption, string yesText = "Yes", string noText = "No")
        : base(message, caption)
    {
        Name = nameof(SectorConflictPromptWindow);

        AddButtonRow(
            CreateButton(yesText, () =>
            {
                Answer = true;
                Close();
            }),
            CreateButton(noText, Close));
    }
}
