using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Freehand drawing on the radar picture (issue #9).
//
// vatSys has no drawing feature of any kind - there is no ink, sketch or annotation tool anywhere in
// it - so all of this is ours. What is not ours is the rendering: strokes go into an AsdMapLayer and
// vatSys's own renderer draws them, for all the reasons in that class.
//
// Strokes are held in lat/lon, never in pixels. The controller draws at one zoom, centre and screen
// size and everyone else is looking at a different one, so a pixel path means nothing anywhere but
// the scope it was drawn on - and it would slide off the map the moment its author panned. Points
// are converted as they are captured, so a stroke is anchored to the ground from the outset.
//
// Drawing is modal, and has to be. The ASD already uses every mouse button for tracks, clickspots
// and panning, so a drawing tool that was always live would break normal working; the toggle is
// also the "optional, user-disableable" the issue asks for. Off is the default and nothing below
// touches the mouse until it is switched on.
public class MapDrawing
{
    // Enough for a working session of markup. A stroke is one slot, so this is a limit on strokes
    // rather than on their length - see AsdMapLayer for why the pool cannot simply grow.
    const int PoolSize = 256;

    // vatSys's own map colours are muted so the traffic picture stays dominant. Drawing is a
    // deliberate mark by a person, so it sits above them - but at 2px and not fully saturated, so
    // it reads as annotation over the map rather than as another map feature.
    //
    // Overridable per profile from Colours.xml as <OzServerColour id="Drawing"> - see
    // ProfileColours, including why it cannot be an ordinary <Colour> entry.
    const string ColourName = "OzServerDrawing";
    const string ColourId = "Drawing";
    static readonly ProfileColours.Rgba DefaultColour = new(0, 220, 255, 220);
    const float StrokeWidth = 2f;

    // Other controllers' markup is a separate layer because a map has exactly one brush, and it is
    // deliberately a different colour: on a shared picture it matters whether a mark is yours or
    // somebody else's, and dimmer keeps your own work legible over theirs.
    const string RemoteColourName = "OzServerDrawingRemote";
    const string RemoteColourId = "DrawingRemote";
    static readonly ProfileColours.Rgba DefaultRemoteColour = new(255, 140, 0, 190);

    // Freehand input samples as fast as the mouse reports, which is far finer than anything visible
    // at map scale and turns a single drag into thousands of points. Anything closer than this to
    // the previous point is dropped: it thins the stroke at capture time, so the cost is never paid
    // by the renderer, the API or every other controller's scope.
    const int MinimumPointSpacingPixels = 4;

    // How near a middle click has to land, in pixels, to count as hitting a stroke. Generous,
    // because a freehand line is thin and nobody aims at a 2px target on a busy scope.
    const int RemoveHitRadiusPixels = 12;

    readonly List<List<Coordinate>> _strokes = new();
    readonly List<string> _strokeIds = new();

    // Everyone else's strokes, with the ids needed to tell them apart when one is middle-clicked.
    List<RemoteStroke> _remote = new();
    readonly HashSet<string> _dismissedRemote = new(StringComparer.OrdinalIgnoreCase);

    readonly AsdMapLayer _layer;
    readonly AsdMapLayer _remoteLayer;
    // Its own client, the same way OzServerOwnershipTracker keeps one - the class is a thin
    // wrapper over a shared static HttpClient, so an instance costs nothing.
    readonly OzServerApiClient _api = new();
    readonly List<Control> _hooked = new();

    List<Coordinate>? _current;
    System.Drawing.Point _lastCapturedAt;
    bool _enabled;

    sealed class RemoteStroke
    {
        public RemoteStroke(string id, List<Coordinate> points)
        {
            Id = id;
            Points = points;
        }

        public string Id { get; }
        public List<Coordinate> Points { get; }
    }

    public MapDrawing()
    {
        var colour = ProfileColours.Resolve(ColourId, DefaultColour);
        _layer = new AsdMapLayer("OzServer Drawing", ColourName,
            colour.Red, colour.Green, colour.Blue, colour.Alpha, PoolSize, StrokeWidth);

        var remote = ProfileColours.Resolve(RemoteColourId, DefaultRemoteColour);
        _remoteLayer = new AsdMapLayer("OzServer Drawing Remote", RemoteColourName,
            remote.Red, remote.Green, remote.Blue, remote.Alpha, PoolSize, StrokeWidth);

        // Push, not poll: the backend signals "annotations" whenever anyone changes anything, and
        // the plugin re-reads. Drawing is the one feature here where a delay is obvious - somebody
        // is drawing while you watch.
        OzServerEventStream.Shared.EventReceived += name =>
        {
            if (name == "annotations")
                _ = RefreshRemoteAsync();
        };

        // Someone else's markup outlives our own session, so what already exists has to be fetched
        // rather than waited for - the next signal might be minutes away.
        Network.Connected += (_, _) => _ = RefreshRemoteAsync();

        // Ours goes with us. The server expires annotations once their author stops being seen, but
        // that is a grace window, and a clean sign-off should not leave markup on everyone's scope
        // for minutes afterwards.
        Network.Disconnected += (_, _) =>
        {
            _strokes.Clear();
            _strokeIds.Clear();
            AsdMapLayer.RunOnUiThread(Redraw);
            _remoteLayer.Clear();
        };
    }

    public bool Enabled => _enabled;

    // Raised so the menu item's check mark follows the state however it was changed - the D key and
    // the menu are two ways into the same toggle, and either has to update the other.
    public event EventHandler? EnabledChanged;

    // Shared with notes - see NetworkIdentity.CanPublishMarkup for the rule and why it reads
    // Position/Rating rather than IsRealATC.
    static bool CanPublish => NetworkIdentity.CanPublishMarkup;

    public void Toggle() => SetEnabled(!_enabled);

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;

        if (enabled)
            Hook();
        else
            Unhook();

        // A stroke in progress when drawing is switched off is abandoned rather than committed:
        // the controller has said stop, and half a line they were still drawing is not something
        // to leave on the map.
        _current = null;

        // Said out loud at the moment the tool is switched on, rather than letting every stroke
        // quietly fail to publish. A drawing that stays local looks identical to one that shared,
        // so the only thing distinguishing them is being told.
        ActionLog.Log("Drawing", enabled
            ? CanPublish
                ? "draw mode on"
                : NetworkIdentity.IsObserver
                    ? "draw mode on - observer session, drawing stays on this scope only"
                    : "draw mode on - not connected, drawing stays on this scope only"
            : "draw mode off");

        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    // Removes the most recent stroke. Freehand drawing is imprecise by nature and the alternative to
    // an undo is clearing everything and starting again.
    public void UndoLast()
    {
        if (_strokes.Count == 0)
            return;

        var last = _strokes.Count - 1;
        _strokes.RemoveAt(last);

        // The id list is kept in step by index, and a stroke still in flight has none yet - so a
        // quick undo removes it locally and the server copy is dropped when its id arrives.
        var id = last < _strokeIds.Count ? _strokeIds[last] : null;
        if (last < _strokeIds.Count)
            _strokeIds.RemoveAt(last);

        Redraw();

        if (!string.IsNullOrEmpty(id))
            _ = SafelyAsync(_api.DeleteAnnotationAsync(id!), "undo");
    }

    // Everyone else's strokes. Ours are filtered out by author rather than by id, so a stroke this
    // session created still reads as ours after it round-trips - drawing it twice, once per layer,
    // would show it in both colours.
    async Task RefreshRemoteAsync()
    {
        try
        {
            var mine = NetworkIdentity.Current?.Cid;
            var annotations = await _api.GetAnnotationsAsync().ConfigureAwait(false);

            var strokes = annotations
                .Where(annotation => annotation.Kind == "stroke")
                .Where(annotation => annotation.Author?.Cid != mine)
                // Hidden on this scope by a middle click. Dropped here rather than when clicked so
                // the next refresh cannot quietly put it back.
                .Where(annotation => !_dismissedRemote.Contains(annotation.Id))
                .Select(annotation => new RemoteStroke(
                    annotation.Id,
                    annotation.Points.Select(point => new Coordinate(point.Lat, point.Lon)).ToList()))
                .Where(stroke => stroke.Points.Count >= 2)
                .ToList();

            // Kept so a middle click can tell which stroke it hit and whose it is.
            AsdMapLayer.RunOnUiThread(() =>
            {
                _remote = strokes;
                _remoteLayer.SetPolygons(strokes.Select(stroke => stroke.Points));
            });
        }
        catch (Exception ex)
        {
            ActionLog.Log("Drawing", $"could not read shared drawing: {ex.Message}");
        }
    }

    // Markup is decoration: a failed publish must never surface as an error dialog over someone's
    // scope, and there is nothing for the controller to do about it either way.
    static async Task SafelyAsync(Task work, string what)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ActionLog.Log("Drawing", $"{what} failed: {ex.Message}");
        }
    }

    // Subscribed on every open scope. ASDControlDX derives from a Control, so its mouse events are
    // ordinary public events - no reflection needed to listen, only to convert what comes back.
    //
    // Re-hooked on each enable rather than once at startup: a scope opened later would otherwise
    // never be drawable, and unsubscribing on disable is what guarantees this costs nothing at all
    // while the tool is off.
    void Hook()
    {
        Unhook();

        foreach (var asd in AsdMapLayer.AsdControls())
        {
            asd.MouseDown += OnMouseDown;
            asd.MouseMove += OnMouseMove;
            asd.MouseUp += OnMouseUp;
            _hooked.Add(asd);
        }
    }

    void Unhook()
    {
        foreach (var asd in _hooked)
        {
            asd.MouseDown -= OnMouseDown;
            asd.MouseMove -= OnMouseMove;
            asd.MouseUp -= OnMouseUp;
        }

        _hooked.Clear();
    }

    void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_enabled || sender is not Control asd)
            return;

        // Middle click removes whatever it landed on, matching the gesture vatSys already uses for
        // its text areas - and meaning the same two things it does there:
        //
        //   your own stroke   -> deleted for everyone. You are withdrawing your own mark.
        //   somebody else's   -> hidden for you alone. Theirs to withdraw, not yours.
        //
        // Right stays with vatSys entirely.
        if (e.Button == MouseButtons.Middle)
        {
            RemoveAt(asd, e.Location);
            return;
        }

        // Left draws. Right is left alone, so the controller is never stuck in a mode they cannot
        // work around without leaving it.
        if (e.Button != MouseButtons.Left)
            return;

        if (ToCoordinate(asd, e.Location) is not { } start)
            return;

        _current = new List<Coordinate> { start };
        _lastCapturedAt = e.Location;
    }

    // Hit-tests in screen space rather than in lat/lon: "near the line" means near as it looks, and
    // a fixed distance on the ground is a different distance on screen at every zoom.
    void RemoveAt(Control asd, System.Drawing.Point location)
    {
        var best = double.MaxValue;
        var localIndex = -1;
        RemoteStroke? remote = null;

        for (var i = 0; i < _strokes.Count; i++)
        {
            var distance = DistanceTo(asd, _strokes[i], location);
            if (distance < best)
            {
                best = distance;
                localIndex = i;
                remote = null;
            }
        }

        // Checked after our own, and only takes the hit on a strictly closer match, so where marks
        // overlap the controller's own is the one that goes - it is the one they can actually undo.
        foreach (var stroke in _remote)
        {
            var distance = DistanceTo(asd, stroke.Points, location);
            if (distance < best)
            {
                best = distance;
                remote = stroke;
                localIndex = -1;
            }
        }

        if (best > RemoveHitRadiusPixels)
            return;

        if (remote != null)
        {
            _dismissedRemote.Add(remote.Id);
            _remote = _remote.Where(stroke => stroke.Id != remote.Id).ToList();
            _remoteLayer.SetPolygons(_remote.Select(stroke => stroke.Points));

            ActionLog.Log("Drawing", "hid another controller's stroke on this scope only");
            return;
        }

        if (localIndex < 0)
            return;

        var id = localIndex < _strokeIds.Count ? _strokeIds[localIndex] : null;
        _strokes.RemoveAt(localIndex);
        if (localIndex < _strokeIds.Count)
            _strokeIds.RemoveAt(localIndex);

        Redraw();

        if (!string.IsNullOrEmpty(id))
            _ = SafelyAsync(_api.DeleteAnnotationAsync(id!), "remove stroke");

        ActionLog.Log("Drawing", "removed own stroke for everyone");
    }

    // Distance from the click to the nearest segment of a stroke. Segments, not points: a long
    // straight line has its endpoints far from the middle of it, and clicking the middle of a line
    // is the obvious way to mean that line.
    static double DistanceTo(Control asd, List<Coordinate> stroke, System.Drawing.Point location)
    {
        var best = double.MaxValue;
        System.Drawing.PointF? previous = null;

        foreach (var coordinate in stroke)
        {
            if (ToScreen(asd, coordinate) is not { } point)
                continue;

            if (previous is { } from)
                best = Math.Min(best, DistanceToSegment(location, from, point));

            previous = point;
        }

        return best;
    }

    static double DistanceToSegment(System.Drawing.Point point, System.Drawing.PointF from, System.Drawing.PointF to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        var lengthSquared = dx * dx + dy * dy;

        // A zero-length segment is just a point - the projection below would divide by zero.
        if (lengthSquared <= double.Epsilon)
            return Math.Sqrt(Math.Pow(point.X - from.X, 2) + Math.Pow(point.Y - from.Y, 2));

        // Projected onto the segment and clamped to it, so a click level with a segment but well
        // past its end is measured to the end rather than to the infinite line through it.
        var t = Math.Max(0, Math.Min(1, ((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared));
        var nearestX = from.X + t * dx;
        var nearestY = from.Y + t * dy;

        return Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Y - nearestY, 2));
    }

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_enabled || _current == null || sender is not Control asd)
            return;

        // Thinned in screen space, not in lat/lon: what matters is how far apart the points look,
        // and a fixed distance on the ground is a different distance on screen at every zoom.
        var dx = e.Location.X - _lastCapturedAt.X;
        var dy = e.Location.Y - _lastCapturedAt.Y;
        if (dx * dx + dy * dy < MinimumPointSpacingPixels * MinimumPointSpacingPixels)
            return;

        if (ToCoordinate(asd, e.Location) is not { } point)
            return;

        _lastCapturedAt = e.Location;

        // A new list each time rather than adding to the one the renderer may be walking. The live
        // stroke is published on every point so the line appears as it is drawn, and by then the
        // renderer is already reading it - see AsdMapLayer's MutationSafety.
        _current = new List<Coordinate>(_current) { point };
        Redraw();
    }

    void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_enabled || e.Button != MouseButtons.Left || _current == null)
            return;

        var finished = _current;
        _current = null;

        // Two points is the shortest thing that is still a line; a single click is discarded rather
        // than stored as a stroke nobody can see.
        if (finished.Count >= 2)
        {
            _strokes.Add(finished);
            _ = PublishAsync(finished);
        }

        Redraw();
    }

    // Published when the stroke is finished rather than as it is drawn: a stroke in progress is not
    // yet a statement, and streaming every point would be a round trip per few pixels of mouse
    // movement for something the author might still abandon.
    async Task PublishAsync(List<Coordinate> stroke)
    {
        if (!CanPublish)
            return;

        try
        {
            var created = await _api.CreateStrokeAsync(stroke).ConfigureAwait(false);

            // Matched back to its stroke by identity, not by index. Undo can remove earlier strokes
            // while this call is in flight, so the position it had when it was sent may not be the
            // position it has now - and an id written to the wrong index would delete the wrong
            // stroke later.
            AsdMapLayer.RunOnUiThread(() =>
            {
                var index = _strokes.IndexOf(stroke);
                if (index < 0)
                {
                    // Undone before the server answered - drop the server's copy rather than leave
                    // it on everyone else's scope with nothing local pointing at it.
                    _ = SafelyAsync(_api.DeleteAnnotationAsync(created.Id), "orphan cleanup");
                    return;
                }

                while (_strokeIds.Count < index)
                    _strokeIds.Add("");

                if (_strokeIds.Count == index)
                    _strokeIds.Add(created.Id);
                else
                    _strokeIds[index] = created.Id;
            });
        }
        catch (Exception ex)
        {
            ActionLog.Log("Drawing", $"could not share a stroke: {ex.Message}");
        }
    }

    void Redraw()
    {
        // The stroke being drawn is rendered alongside the finished ones, so the line follows the
        // cursor instead of appearing only once the button comes up.
        var polygons = _strokes.ToList();
        if (_current is { Count: >= 2 })
            polygons.Add(_current);

        _layer.SetPolygons(polygons);
    }

    // Screen point to lat/lon, through the ASD's own projection so a stroke lands exactly where it
    // was drawn at any zoom, rotation or projection setting.
    //
    // ConvertScreenToLL is public but takes a SharpDX.Vector2 and a RenderParams, and RenderParams
    // comes from a private GetRenderParams - so both are reached by reflection. The Vector2 is built
    // through the parameter's own type rather than by referencing SharpDX, for the same reason the
    // layer colours are: the plugin must not ship a second copy of an assembly vatSys already loads.
    // Resolved once. This runs on every captured point of every stroke, and a GetMethod per point
    // is a lookup by string tens of times a second for an answer that cannot change - the ASD type
    // is fixed for the life of the process.
    static MethodInfo? _getRenderParams;
    static MethodInfo? _convertScreenToLL;
    static Type? _vectorType;
    static Type? _resolvedFor;

    static bool ResolveConversion(Type asdType)
    {
        if (_resolvedFor == asdType)
            return _convertScreenToLL != null;

        _resolvedFor = asdType;

        _getRenderParams = asdType.GetMethod("GetRenderParams",
            BindingFlags.NonPublic | BindingFlags.Instance);

        _convertScreenToLL = asdType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "ConvertScreenToLL"
                                      && method.GetParameters().Length == 2);

        _vectorType = _convertScreenToLL?.GetParameters()[0].ParameterType;

        return _convertScreenToLL != null;
    }

    // The other direction, for hit-testing. This overload is public and takes no RenderParams - it
    // reads the control's current one itself - so it is the cheaper of the two and needs nothing
    // cached but the method and the two fields of the vector it returns.
    static MethodInfo? _convertLLToScreen;
    static FieldInfo? _vectorX;
    static FieldInfo? _vectorY;

    static System.Drawing.PointF? ToScreen(Control asd, Coordinate coordinate)
    {
        try
        {
            if (_convertLLToScreen == null)
            {
                _convertLLToScreen = asd.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "ConvertLLToScreen"
                                              && method.GetParameters().Length == 1);

                _vectorX = _convertLLToScreen?.ReturnType.GetField("X");
                _vectorY = _convertLLToScreen?.ReturnType.GetField("Y");
            }

            if (_vectorX == null || _vectorY == null)
                return null;

            var vector = _convertLLToScreen!.Invoke(asd, new object[] { coordinate });
            if (vector == null)
                return null;

            return new System.Drawing.PointF(
                Convert.ToSingle(_vectorX.GetValue(vector)),
                Convert.ToSingle(_vectorY.GetValue(vector)));
        }
        catch
        {
            // Hit-testing is best-effort: a stroke that cannot be projected simply cannot be hit.
            return null;
        }
    }

    static Coordinate? ToCoordinate(Control asd, System.Drawing.Point location)
    {
        try
        {
            if (!ResolveConversion(asd.GetType()) || _vectorType == null)
                return null;

            // Fetched per point rather than cached: it carries the current centre, zoom and screen
            // size, so a cached one would map every point after a pan or zoom to the wrong place.
            var renderParams = _getRenderParams?.Invoke(asd, new object[] { false });
            if (renderParams == null)
                return null;

            var vector = Activator.CreateInstance(_vectorType, (float)location.X, (float)location.Y);

            return _convertScreenToLL!.Invoke(asd, new[] { vector, renderParams }) as Coordinate;
        }
        catch (Exception ex)
        {
            ActionLog.Log("Drawing", $"could not resolve a screen point: {ex.Message}");
            return null;
        }
    }
}
