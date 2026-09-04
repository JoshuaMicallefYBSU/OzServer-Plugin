using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Shares vatSys's own text areas between controllers (issue #9). A note one controller writes on the
// map appears on everyone's scope.
//
// Nothing here adds a way to write a note. vatSys already has one - F9, or Settings > Text Area -
// and it already draws them, moves them and edits them. This watches the text areas the controller
// creates, publishes them, and puts everybody else's into the same list so they are ordinary text
// areas as far as vatSys is concerned.
//
// Middle click is the reason this works as neatly as it does. PaintTextAreas registers a clickspot
// per note with TypeMouseLeft = TextArea_Edit, TypeMouseRight = TextArea_Move and TypeMouseMiddle =
// TextArea_Delete - so middle click already removes a text area, and RemoveTextArea only touches the
// local ASDControlDX.TextAreas array. Removing a note for yourself therefore needs no code at all;
// what needs code is noticing it happened, so it is not simply put back on the next sync.
//
// Which makes the gesture mean two different things, correctly:
//
//   - your own note   -> deleted for everyone. You are retracting your own statement.
//   - somebody else's -> hidden for you alone. Their note is theirs to withdraw, not yours.
//
// Dismissals are remembered for the session only. A note dismissed today is not dismissed forever -
// it lasts as long as the note itself would, and the author's session is what expires it.
public class SharedNotes
{
    // Text areas change at human speed - somebody types one every few minutes at most - so this
    // never needs to be fast. It shares the interval AsdMapLayer uses.
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    readonly OzServerApiClient _api = new();
    readonly System.Threading.Timer _timer;

    // Server ids the controller has middle-clicked away. Never re-injected while this session lasts.
    readonly HashSet<string> _dismissed = new(StringComparer.OrdinalIgnoreCase);

    // Notes we put on this scope, by server id. One ASDTextArea instance per note, shared across
    // every open scope - vatSys only ever reads Text and Position off it, so one object can sit in
    // more than one control's array.
    readonly Dictionary<string, object> _injected = new(StringComparer.OrdinalIgnoreCase);

    // Text areas this controller wrote, and what the server currently holds for them. Keyed by the
    // instance itself, by reference: two notes with the same words at the same point are still two
    // notes, and only identity distinguishes them.
    readonly Dictionary<object, LocalNote> _local = new(ReferenceEqualityComparer.Instance);

    // The last set fetched from the server. Held so a poll can inject without waiting on a request,
    // and refreshed by the annotations signal rather than by polling the API.
    List<OzServerAnnotationDto> _remote = new();

    sealed class LocalNote
    {
        public string? Id;
        public string Text = "";
        public double Latitude;
        public double Longitude;
    }

    sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }

    public SharedNotes()
    {
        OzServerEventStream.Shared.EventReceived += name =>
        {
            if (name == "annotations")
                _ = RefreshRemoteAsync();
        };

        Network.Connected += (_, _) => _ = RefreshRemoteAsync();

        // Everything is dropped on disconnect: the injected notes belong to sessions we are no
        // longer watching, and our own tracking refers to a server that no longer knows us.
        Network.Disconnected += (_, _) => AsdMapLayer.RunOnUiThread(Forget);

        _timer = new System.Threading.Timer(
            _ => AsdMapLayer.RunOnUiThread(Poll), null, PollInterval, PollInterval);
    }

    void Forget()
    {
        foreach (var control in AsdMapLayer.AsdControls())
        {
            var mine = ReadTextAreas(control).Where(area => !_injected.ContainsValue(area)).ToList();
            WriteTextAreas(control, mine);
        }

        _injected.Clear();
        _dismissed.Clear();
        _local.Clear();
        _remote = new List<OzServerAnnotationDto>();
    }

    // One pass over every scope: notice what the controller changed, then make the scope match what
    // the server says. Runs on the UI thread, so nothing here races the controller's own edits.
    void Poll()
    {
        try
        {
            var controls = AsdMapLayer.AsdControls().ToList();
            if (controls.Count == 0)
                return;

            var present = new HashSet<object>(
                controls.SelectMany(ReadTextAreas), ReferenceEqualityComparer.Instance);

            DetectDismissals(present);
            DetectLocalChanges(present);
            Inject(controls);
        }
        catch (Exception ex)
        {
            ActionLog.Log("Notes", $"sync failed: {ex.Message}");
        }
    }

    // A note of ours that has left every scope was middle-clicked away. Whose it was decides what
    // that meant.
    void DetectDismissals(HashSet<object> present)
    {
        foreach (var pair in _injected.Where(pair => !present.Contains(pair.Value)).ToList())
        {
            _injected.Remove(pair.Key);
            _dismissed.Add(pair.Key);

            ActionLog.Log("Notes", $"dismissed {pair.Key} on this scope only");
        }
    }

    void DetectLocalChanges(HashSet<object> present)
    {
        // Gone from every scope: the author removed their own note, so it goes for everyone.
        foreach (var pair in _local.Where(pair => !present.Contains(pair.Key)).ToList())
        {
            _local.Remove(pair.Key);

            if (pair.Value.Id is not { } id)
                continue;

            // Remembered as dismissed too, so the copy still sitting in _remote until the next
            // fetch cannot be injected back onto this scope in the meantime.
            _dismissed.Add(id);
            _ = Safely(_api.DeleteAnnotationAsync(id), "delete note");

            ActionLog.Log("Notes", "own note deleted for everyone");
        }

        foreach (var area in present)
        {
            // Somebody else's note, put here by us - not ours to publish.
            if (_injected.ContainsValue(area))
                continue;

            var text = TextOf(area);
            var position = PositionOf(area);
            if (string.IsNullOrWhiteSpace(text) || position == null)
                continue;

            if (_local.TryGetValue(area, out var known))
            {
                // vatSys edits a note in place (left click) and moves it in place (right click), so
                // the same instance can change under us - which is an update, not a new note.
                if (known.Text == text
                    && Math.Abs(known.Latitude - position.Latitude) < 0.000001
                    && Math.Abs(known.Longitude - position.Longitude) < 0.000001)
                    continue;

                known.Text = text;
                known.Latitude = position.Latitude;
                known.Longitude = position.Longitude;

                if (known.Id is { } id && NetworkIdentity.CanPublishMarkup)
                    _ = Safely(_api.UpdateNoteAsync(id, text, position), "update note");

                continue;
            }

            var note = new LocalNote
            {
                Text = text,
                Latitude = position.Latitude,
                Longitude = position.Longitude
            };

            // Recorded before the request so a second poll two seconds later does not publish the
            // same note again while the first call is still in flight.
            _local[area] = note;

            if (NetworkIdentity.CanPublishMarkup)
                _ = PublishAsync(note, text, position);
            else
                ActionLog.Log("Notes", "note stays on this scope only - observer or not connected");
        }
    }

    async Task PublishAsync(LocalNote note, string text, Coordinate position)
    {
        try
        {
            var created = await _api.CreateNoteAsync(text, position).ConfigureAwait(false);

            AsdMapLayer.RunOnUiThread(() =>
            {
                note.Id = created.Id;

                // Ours, so it must never be injected back as somebody else's - the author filter on
                // the fetch does that, and this covers the window before the next fetch.
                _dismissed.Add(created.Id);
            });
        }
        catch (Exception ex)
        {
            ActionLog.Log("Notes", $"could not share a note: {ex.Message}");
        }
    }

    // Everyone else's notes, put onto every scope that does not have them yet.
    void Inject(List<Control> controls)
    {
        var wanted = _remote
            .Where(annotation => annotation.Kind == "note")
            .Where(annotation => annotation.Author?.Cid != NetworkIdentity.Current?.Cid)
            .Where(annotation => !_dismissed.Contains(annotation.Id))
            .Where(annotation => annotation.Points.Count > 0 && !string.IsNullOrWhiteSpace(annotation.Body))
            .ToList();

        // Notes the author has since withdrawn: they are gone from the server, so they go from here.
        var stale = _injected.Keys.Where(id => wanted.All(annotation => annotation.Id != id)).ToList();
        foreach (var id in stale)
            _injected.Remove(id);

        foreach (var annotation in wanted)
        {
            if (_injected.ContainsKey(annotation.Id))
                continue;

            var point = annotation.Points[0];

            // The author's callsign is part of the text rather than a separate field - vatSys's text
            // area has one string, and an unattributed note on a shared picture is worth much less
            // than one you can ask about.
            var instance = CreateTextArea(
                $"{annotation.Body} [{annotation.Author?.Callsign}]",
                new Coordinate(point.Lat, point.Lon));

            if (instance != null)
                _injected[annotation.Id] = instance;
        }

        foreach (var control in controls)
        {
            var current = ReadTextAreas(control);
            var missing = _injected.Values.Where(area => !current.Contains(area)).ToList();
            var dropped = current.Where(area => IsStaleInjection(area)).ToList();

            if (missing.Count == 0 && dropped.Count == 0)
                continue;

            WriteTextAreas(control, current.Except(dropped).Concat(missing).ToList());
        }

        bool IsStaleInjection(object area) =>
            !_injected.ContainsValue(area) && !_local.ContainsKey(area) && WasInjected(area);
    }

    // An instance we created but no longer track - a note whose author withdrew it. Distinguished
    // from the controller's own text areas by never having been in _local.
    readonly HashSet<object> _everInjected = new(ReferenceEqualityComparer.Instance);

    bool WasInjected(object area) => _everInjected.Contains(area);

    async Task RefreshRemoteAsync()
    {
        try
        {
            var annotations = await _api.GetAnnotationsAsync().ConfigureAwait(false);
            AsdMapLayer.RunOnUiThread(() => _remote = annotations);
        }
        catch (Exception ex)
        {
            ActionLog.Log("Notes", $"could not read shared notes: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // vatSys internals. ASDControlDX.TextAreas is a private ASDTextArea[], and both AddTextArea and
    // RemoveTextArea are private - but they work by building a List, calling ToArray and assigning
    // the field, which is a single reference swap. PaintTextAreas reads that field on the render
    // thread, so writing it any other way would tear; this does exactly what vatSys does.
    //
    // ASDTextArea itself is internal but has a public (string, Coordinate) constructor, so instances
    // can be made without touching anything private.
    // ---------------------------------------------------------------------------------------------

    static FieldInfo? _textAreasField;
    static Type? _textAreaType;
    static PropertyInfo? _textProperty;
    static PropertyInfo? _positionProperty;

    static bool Resolve(Type asdType)
    {
        if (_textAreasField != null)
            return _textAreaType != null;

        _textAreasField = asdType.GetField("TextAreas", BindingFlags.NonPublic | BindingFlags.Instance);
        _textAreaType = typeof(MMI).Assembly.GetType("vatsys.ASDTextArea");
        _textProperty = _textAreaType?.GetProperty("Text");
        _positionProperty = _textAreaType?.GetProperty("Position");

        return _textAreaType != null && _textAreasField != null;
    }

    static List<object> ReadTextAreas(Control control)
    {
        if (!Resolve(control.GetType()))
            return new List<object>();

        return (_textAreasField!.GetValue(control) as Array)?.Cast<object>().ToList()
               ?? new List<object>();
    }

    static void WriteTextAreas(Control control, List<object> areas)
    {
        if (!Resolve(control.GetType()) || _textAreaType == null)
            return;

        var array = Array.CreateInstance(_textAreaType, areas.Count);
        for (var i = 0; i < areas.Count; i++)
            array.SetValue(areas[i], i);

        _textAreasField!.SetValue(control, array);
        MMI.RequestRedraw(true, false, false);
    }

    object? CreateTextArea(string text, Coordinate position)
    {
        if (_textAreaType == null)
            return null;

        var instance = Activator.CreateInstance(_textAreaType, text, position);
        if (instance != null)
            _everInjected.Add(instance);

        return instance;
    }

    static string TextOf(object area) => _textProperty?.GetValue(area) as string ?? "";

    static Coordinate? PositionOf(object area) => _positionProperty?.GetValue(area) as Coordinate;

    static async Task Safely(Task work, string what)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ActionLog.Log("Notes", $"{what} failed: {ex.Message}");
        }
    }
}
