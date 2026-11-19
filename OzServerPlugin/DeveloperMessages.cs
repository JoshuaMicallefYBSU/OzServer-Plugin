using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Messages from the people running OzServer, sent from the website's /console: a known problem, a
// release to install, a question about something seen in the logs. Each is shown once in a popup
// (DeveloperMessageWindow) with the request sound, and can be answered when the sender allows it
// (DeveloperReplyWindow).
//
// Fetched (GET /messages) when the backend signals "messages" on the event stream, on connecting,
// and every PollInterval as a fallback. The backend hands out only messages this controller has not
// read, and records delivery; closing the popup, by OK or the title bar, marks it read, and so does
// replying. A message is shown at most once a session even if marking it read fails.
public class DeveloperMessages
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    // After connecting, once the session's identity has settled (as FdrResumeActivation waits).
    static readonly TimeSpan ConnectDelay = TimeSpan.FromSeconds(5);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _pollTimer;
    // Shown this session. Guarded by itself.
    readonly HashSet<long> _shown = new();
    // 0 = idle, 1 = a fetch is running; a signal arriving mid-fetch is answered by the next poll.
    int _fetching;

    public DeveloperMessages()
    {
        _pollTimer = new System.Threading.Timer(_ => _ = FetchAsync(), null, PollInterval, PollInterval);

        Network.Connected += (_, _) => _ = Task.Delay(ConnectDelay).ContinueWith(_ => FetchAsync());
        Network.Disconnected += (_, _) =>
        {
            lock (_shown)
                _shown.Clear();
        };
        OzServerEventStream.Shared.EventReceived += name =>
        {
            if (name == "messages")
                _ = FetchAsync();
        };
    }

    async Task FetchAsync()
    {
        if (OzServerMode.Disabled || !Network.IsConnected || NetworkServer.Current == null)
            return;
        if (Interlocked.Exchange(ref _fetching, 1) == 1)
            return;

        try
        {
            var messages = await _api.GetMessagesAsync();
            List<OzServerDeveloperMessageDto> fresh;
            lock (_shown)
                fresh = messages.Where(message => _shown.Add(message.Id)).ToList();

            foreach (var message in fresh)
            {
                ActionLog.Log("Message", $"Message from {message.SentBy}: {Subject(message)}", new
                {
                    action = "developer_message_received",
                    message_id = message.Id,
                    allow_reply = message.AllowReply
                });
                RunOnUiThread(() => Show(message));
            }
        }
        catch (Exception ex)
        {
            // An older backend without /messages answers 404 on every poll: not worth a report.
            if (ex is not OzServerApiException { StatusCode: 404 })
                ErrorThrottle.Report("developer-messages", $"Couldn't check for OzServer messages: {ex.Message}", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    void Show(OzServerDeveloperMessageDto message)
    {
        if (OzServerMode.Disabled)
            return;

        var window = new DeveloperMessageWindow(message);
        DeveloperReplyWindow? replyWindow = null;

        window.ReplyRequested += () =>
        {
            if (replyWindow is { IsDisposed: false })
            {
                replyWindow.BringToFront();
                return;
            }

            replyWindow = new DeveloperReplyWindow(Subject(message), body => ReplyAsync(message, body));
            // Answered: the message is done with too.
            replyWindow.Sent += () =>
            {
                if (!window.IsDisposed)
                    window.Close();
            };
            ShowOwned(replyWindow);
        };

        // However it is closed, it has been read. Replying marks it read on the backend as well.
        window.FormClosed += (_, _) => _ = MarkReadAsync(message);

        ShowOwned(window);
        NotificationSound.PlayRequestArrived();
    }

    async Task<string?> ReplyAsync(OzServerDeveloperMessageDto message, string body)
    {
        try
        {
            await _api.ReplyToMessageAsync(message.Id, body);
            ActionLog.Log("Message", $"Replied to {message.SentBy}'s message: {Subject(message)}", new
            {
                action = "developer_message_replied",
                message_id = message.Id
            });
            return null;
        }
        catch (OzServerApiException ex)
        {
            return ex.Message;
        }
    }

    async Task MarkReadAsync(OzServerDeveloperMessageDto message)
    {
        try
        {
            await _api.MarkMessageReadAsync(message.Id);
        }
        catch (Exception ex)
        {
            // Not shown again this session either way (_shown); a later session may show it again.
            ActionLog.Log("Message", $"Couldn't mark message {message.Id} read: {ex.Message}");
        }
    }

    // What to call a message in the log and the reply window: its title, or the start of its text
    // when it has none.
    static string Subject(OzServerDeveloperMessageDto message)
    {
        if (!string.IsNullOrWhiteSpace(message.Title))
            return message.Title;
        var body = message.Body.Replace(Environment.NewLine, " ").Replace('\n', ' ').Trim();
        return body.Length <= 60 ? body : body.Substring(0, 57) + "...";
    }

    static void ShowOwned(Form form)
    {
        if (Application.OpenForms["MainForm"] is Form mainForm)
            form.Show(mainForm);
        else
            form.Show();
        form.BringToFront();
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
