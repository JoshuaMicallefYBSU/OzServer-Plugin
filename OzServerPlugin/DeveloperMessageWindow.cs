using System;

namespace OzServerPlugin;

// A message from the people running OzServer (DeveloperMessages): its title, text and sender, with
// OK, and Reply when the sender asked for answers. Non-modal like every popup in the plugin.
public class DeveloperMessageWindow : SectorMessageWindow
{
    // Raised when the controller chooses Reply. The window stays open until the reply is sent.
    public event Action? ReplyRequested;

    public DeveloperMessageWindow(OzServerDeveloperMessageDto message)
        : base(Format(message), "OzServer Announcement")
    {
        Name = nameof(DeveloperMessageWindow);

        if (message.AllowReply)
            AddButtonRow(CreateButton("Reply", () => ReplyRequested?.Invoke()), CreateButton("OK", Close));
        else
            AddButtonRow(CreateButton("OK", Close));
    }

    static string Format(OzServerDeveloperMessageDto message)
    {
        var nl = Environment.NewLine;
        var sent = message.CreatedAt == default ? "" : $", {message.CreatedAt.ToUniversalTime():HHmm}z";
        // A title is optional: a short notice can be all body.
        var title = string.IsNullOrWhiteSpace(message.Title) ? "" : message.Title + nl + nl;
        return title + message.Body + nl + nl + $"- {message.SentBy}{sent}";
    }
}
