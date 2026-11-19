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

    // A notice that cannot be dismissed, only acted on - the update notice and its "Restart". Its
    // one button runs the action and the window stays where it is: with HasCloseButton off,
    // BaseForm draws no close button in the title bar and cancels every Close() until vatSys itself
    // is exiting (BaseForm.AppClosing), which is also what finally closes it. Still non-modal, so
    // it can be moved aside and vatSys used behind it.
    public SectorNoticeWindow(string message, string caption, string actionText, Action onAction) : base(message, caption)
    {
        Name = nameof(SectorNoticeWindow);
        HasCloseButton = false;

        AddButtonRow(CreateButton(actionText, onAction));
    }
}
