using System;

namespace OzServerPlugin;

// One incoming request, answered without opening the Sectors window. Shares the chat-style chrome
// of the plugin's other message dialogs (SectorMessageWindow); all this adds is the Accept/Reject
// pair.
//
// One window per request *group*, not per sector: every sector another controller asked for in a
// single Apply carries the same group id, so a request for three sectors is one decision and gets
// one window listing all three. Before groups existed the backend wrote three unrelated rows and
// there was no way to tell them apart from three separate requests.
//
// Non-modal, like SectorNoticeWindow and unlike SectorConflictPromptWindow: ShowDialog freezes the
// whole vatSys UI thread until dismissed, which is not something to do to a controller working
// traffic - and unlike a claim conflict, nothing here is blocked waiting on the answer.
//
// Closing the window without choosing is deliberately not a decision. The request stays pending and
// is still there in the Sectors window; treating a dismissed window as a refusal would silently
// decline a request the controller merely clicked away while busy.
public class SectorRequestPromptWindow : SectorMessageWindow
{
    public SectorRequestPromptWindow(string message, string caption, Action onAccept, Action onReject)
        : base(message, caption)
    {
        Name = nameof(SectorRequestPromptWindow);

        AddButtonRow(
            CreateButton("Accept", () =>
            {
                // Closed first so the window cannot be pressed twice while the API call is in
                // flight - accept and reject are both one-shot decisions about the same group.
                Close();
                onAccept();
            }),
            CreateButton("Reject", () =>
            {
                Close();
                onReject();
            }));
    }
}
