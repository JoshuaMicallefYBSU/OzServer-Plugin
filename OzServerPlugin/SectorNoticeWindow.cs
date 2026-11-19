using System;

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

    // A notice whose one button does something rather than dismissing it - the update notice and
    // its "Restart". Nothing in the window offers a way to skip the action, but the title bar keeps
    // its close button, as every vatSys window has, and that dismisses the notice. The button
    // leaves the window open: if the action is backed out of, the notice is still there.
    public SectorNoticeWindow(string message, string caption, string actionText, Action onAction) : base(message, caption)
    {
        Name = nameof(SectorNoticeWindow);

        AddButtonRow(CreateButton(actionText, onAction));
    }
}
