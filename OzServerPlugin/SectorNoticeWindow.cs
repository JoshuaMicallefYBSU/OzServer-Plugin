namespace OzServerPlugin;

// A one-way notice: something has already happened and the controller is being told about it -
// applied sector changes, a denied request, a sector that could not be assumed. There is nothing
// to answer, so it has one OK button, and the title bar's close button does the same thing.
//
// Shown non-modally, as SectorConflictPromptWindow is: a modal dialog blocks the vatSys UI thread
// while the controller is working traffic.
public class SectorNoticeWindow : SectorMessageWindow
{
    public SectorNoticeWindow(string message, string caption) : base(message, caption)
    {
        Name = nameof(SectorNoticeWindow);

        AddButtonRow(CreateButton("OK", Close));
    }
}
