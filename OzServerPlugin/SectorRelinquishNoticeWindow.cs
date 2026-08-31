namespace OzServerPlugin;

// Tells the controller that a primary position has just logged on and that the sectors they were
// holding on that position's behalf have been handed back - see PrimaryPositionWatcher, which is
// what decides that and performs the release.
//
// Deliberately a notice, not a question: the handover is not the controller's to refuse. A sector
// belongs to whoever is logged in on it, so by the time this appears the release has already been
// sent. One OK button, and the title bar's close button does the same thing.
//
// Shown non-modally (see PrimaryPositionWatcher.ShowNotice) rather than with ShowDialog like
// SectorConflictPromptWindow: a modal dialog freezes the whole vatSys UI thread until it is
// dismissed, which is not something to do to a controller who is working traffic and has no
// decision to make here anyway.
public class SectorRelinquishNoticeWindow : SectorMessageWindow
{
    public SectorRelinquishNoticeWindow(string message, string caption) : base(message, caption)
    {
        Name = nameof(SectorRelinquishNoticeWindow);

        AddButtonRow(CreateButton("OK", Close));
    }
}
