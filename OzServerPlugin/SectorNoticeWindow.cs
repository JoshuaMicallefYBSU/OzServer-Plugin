namespace OzServerPlugin;

// A one-way notice: something has already happened and the controller is being told about it.
// Used for a primary position taking its sectors back (PrimaryPositionWatcher), a request of theirs
// being denied, and a request arriving from someone else (Plugin).
//
// Deliberately a notice, not a question - none of those are the controller's to refuse, so there is
// nothing to answer. One OK button, and the title bar's close button does the same thing.
//
// Shown non-modally (see PrimaryPositionWatcher.ShowNotice) rather than with ShowDialog like
// SectorConflictPromptWindow: a modal dialog freezes the whole vatSys UI thread until it is
// dismissed, which is not something to do to a controller who is working traffic and has no
// decision to make here anyway.
public class SectorNoticeWindow : SectorMessageWindow
{
    public SectorNoticeWindow(string message, string caption) : base(message, caption)
    {
        Name = nameof(SectorNoticeWindow);

        AddButtonRow(CreateButton("OK", Close));
    }
}
