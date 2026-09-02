using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// Fills, on the controller's own scope, the sectors somebody is currently asking them for - shaded
// transparent yellow with a solid yellow border, so the airspace being requested can be seen rather
// than only named, and two requested sectors that share a boundary still read as two.
//
// Shown while the sector management window is open and taken off the scope when it closes, not the
// moment a request lands - see SetRevealed. The highlight answers "which airspace is this", a
// question only being asked once the controller has gone to look at the request; painting the map
// yellow before then interrupts someone working traffic with something they have not asked about.
// The arrival itself is announced by the Settings header's flash, its badge and NotificationSound.
//
// Nothing here draws pixels. vatSys owns the render loop; this hands it maps and lets its existing
// renderer paint them. Every decision below is forced by what ASDControlDX actually does, which is
// worth writing down because most of it is not guessable and the obvious approaches all fail
// silently or crash the client.
//
// The render path, from ASDControlDX.Render:
//
//   var visible = DisplayMaps.Maps.Join(renderParams.VisibleMaps, m => m.Id, id => id, ...);
//   ComputeAllMapElements(renderParams, visible);      // outside any try/catch
//   try {
//       if (MMI.DynamicInfill) PaintMap(DisplayMaps.DynamicInfill);
//       PaintMap(DisplayMaps.DynamicRestrictedAreaInfills);
//       PaintMaps(visible);
//       PaintMap(DisplayMaps.DynamicRestrictedAreas);
//   } catch (SharpDXException) { ... }
//
// Four things follow, and they are the whole design:
//
// 1. A map is drawn only if its Id is in RenderParams.VisibleMaps. Map.Id is initonly and
//    VisibleMaps is get-only, which is why simply adding a layer to DisplayMaps.Maps draws nothing.
//    The way through is ASDControlDX.SetMapVisible(map, true) - a public method that reads the
//    current VisibleMaps, adds the Id and pushes it back through SetRenderParams. The public Map
//    constructor already assigns a unique Id from DisplayMaps.GenerateId(), so no Id has to be
//    forged; it just has to be made visible.
//
// 2. DisplayMaps.DynamicInfill, the surface this first tried, is gated on MMI.DynamicInfill, which
//    comes from the DynamicInfill attribute on the logical position in Positions.xml. Alice Springs
//    does not set it, so that branch never ran and nothing written there could ever appear.
//    UpdateDynamicInfill also assigns a brand new Map over the old one, discarding anything added
//    before it. Owning a map avoids both problems.
//
// 3. Colour is chosen per map by SelectMapBrush: a non-empty CustomColourName recolours the shared
//    custom brush from Colours.GetCustomColorDX(name), otherwise the brush comes from a switch on
//    Map.Type. An unregistered name silently returns AliceBlue rather than throwing. The lookup is
//    per map and happens immediately before that map is painted, so an owned map can be any colour
//    without disturbing any other layer - which sharing DynamicInfill could never have done.
//
//    That same per-map lookup is why the shading and the border are two maps rather than one.
//    PaintMap calls SelectMapBrush once and uses the result for both its infills and its lines, so
//    a border drawn in the fill map would be the same 27%-opacity yellow as the fill and invisible
//    against it. Two maps means two brushes: see Layers.
//
// 4. Render runs on its own thread (ASDControlDX.RenderLoop), which catches only
//    OperationCanceledException; the paint block catches only SharpDXException, and the compute call
//    sits outside even that. So any exception this code provokes escapes the render loop and stops
//    the scope drawing for good. That makes thread safety a correctness requirement, not a nicety,
//    and it is why nothing below is ever mutated in place - see MutationSafety.
public class RequestedSectorOverlay
{
    // ---------------------------------------------------------------------------------------------
    // Layers
    //
    // Two maps, because one map has exactly one brush (see 3 above). The fill carries the sector
    // shapes as infills at low alpha; the border carries the same shapes as lines at full alpha.
    //
    // They are appended fill-first. PaintMaps walks the join in DisplayMaps.Maps order - Join
    // preserves the outer sequence - so the border is painted over the shading rather than under it.
    // ---------------------------------------------------------------------------------------------

    const string FillMapName = "OzServer Requested";
    const string BorderMapName = "OzServer Requested Border";

    // Registered into the Colours custom table at startup, so this does not depend on the profile
    // defining colours for us. Alpha on the fill is what makes it read as a highlight over the map
    // underneath rather than a solid slab hiding it; the border is opaque so the edge stays crisp.
    const string FillColourName = "OzServerRequestedSector";
    const string BorderColourName = "OzServerRequestedSectorBorder";
    const byte HighlightRed = 255;
    const byte HighlightGreen = 235;
    const byte HighlightBlue = 0;
    const byte FillAlpha = 70;
    const byte BorderAlpha = 255;

    // Wide enough to survive the 1px antialiased map lines it sits among without being a slab.
    const float BorderWidth = 2f;

    // The pool is fixed for the reasons in MutationSafety: its size is the number of sector volumes
    // that can be highlighted at once, and it is far above any realistic request - a request is a
    // handful of sectors, and only the busiest have more than a few volumes each.
    const int PoolSize = 64;

    // Re-asserted rather than set once: a scope opened after this ran has never been told about the
    // maps, and reloading maps or a position replaces DisplayMaps.Maps wholesale and drops them.
    // Both are cheap to check and neither raises an event to hook.
    static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(2);

    // Three references to one Coordinate: a triangle of zero area, which paints nothing. Unused
    // slots cannot simply be emptied - ComputeMapElements skips an empty point list, so no entry is
    // written to the vector table, but PaintMap then calls EndFigure unconditionally on a figure it
    // never began, and the SharpDXException that follows abandons the rest of the frame's maps.
    static readonly List<Coordinate> Hidden = Degenerate();

    readonly OzServerOwnershipTracker _tracker;
    readonly System.Threading.Timer _reassertTimer;
    readonly object _lock = new();

    List<List<Coordinate>> _polygons = new();
    Layer? _fill;
    Layer? _border;

    // What is being asked for, and whether the controller is currently looking at it. The highlight
    // is the answer to "which airspace is this request", a question only asked while the sector
    // management window is open - shading the scope the moment a request lands puts unexplained
    // yellow over the map of a controller who is working traffic and has not looked yet.
    List<SectorsVolumes.Sector> _requested = new();
    bool _revealed;

    // A map and the polygons inside it that get reused. Infill and Line are both BasePolygon, which
    // is where Points lives, so one pool type drives both layers.
    sealed class Layer
    {
        public Layer(DisplayMaps.Map map, DisplayMaps.Map.BasePolygon[] pool)
        {
            Map = map;
            Pool = pool;
        }

        public DisplayMaps.Map Map { get; }

        public DisplayMaps.Map.BasePolygon[] Pool { get; }
    }

    public RequestedSectorOverlay(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;
        _tracker.IncomingRequestsChanged += (_, requests) => SetRequested(SectorsIn(requests));
        Network.Disconnected += (_, _) => Clear();

        _reassertTimer = new System.Threading.Timer(
            _ => RunOnUiThread(Reassert), null, ReassertInterval, ReassertInterval);
    }

    // The sectors currently being asked for. Tracked whether or not anything is on screen, so that
    // opening the window shows what is pending right now rather than only what arrives afterwards.
    public void SetRequested(IReadOnlyList<SectorsVolumes.Sector> sectors)
    {
        _requested = sectors.ToList();
        Apply();
    }

    // Driven by the sector management window's visibility. Both directions matter: closing the
    // window takes the shading off the scope, so it never outlives the reason it was drawn.
    public void SetRevealed(bool revealed)
    {
        if (_revealed == revealed)
            return;

        _revealed = revealed;
        Apply();
    }

    public void Clear()
    {
        _requested = new List<SectorsVolumes.Sector>();
        Apply();
    }

    void Apply()
    {
        var sectors = _revealed ? _requested : new List<SectorsVolumes.Sector>();

        lock (_lock)
            _polygons = sectors.SelectMany(Boundaries).Take(PoolSize).ToList();

        RunOnUiThread(Reassert);

        ActionLog.Log("Overlay", sectors.Count == 0
            ? $"highlight cleared ({_requested.Count} request(s) pending, revealed={_revealed})"
            : $"{sectors.Count} sector(s) highlighted: {string.Join(", ", sectors.Select(s => s.Name))}");
    }

    // Everything the overlay needs doing, in the order it has to happen, run on a timer as well as
    // on every change so a scope opened later still ends up showing the highlight.
    void Reassert()
    {
        if (!DisplayMaps.Loaded)
            return;

        try
        {
            EnsureMaps();
            ApplyPolygons();
            EnsureVisible();
            MMI.RequestRedraw(true, false, false);
        }
        catch (Exception ex)
        {
            // Swallowed deliberately. This is decoration; if the internals it leans on ever move,
            // the highlight should stop working, not take the plugin - or the scope - with it.
            ActionLog.Log("Overlay", $"could not apply highlight: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // MutationSafety
    //
    // Render runs on its own thread and neither it nor RenderLoop catches the exceptions this code
    // could cause, so a torn read does not drop a frame - it kills the scope permanently. Two
    // separate hazards, both avoided the same way, by never mutating a collection the renderer might
    // be walking and swapping a fresh one in instead. A reference assignment is atomic, so the
    // renderer sees either the old collection or the new one, each internally consistent.
    //
    // The first hazard is enumeration. DisplayMaps.Maps is walked twice per frame - the Join is
    // lazy, so both ComputeAllMapElements and PaintMaps enumerate it - and List throws if it is
    // modified while enumerated. So the maps are published by building a new list and swapping it
    // in, never by DisplayMaps.Maps.Add.
    //
    // The second is the vector table. ComputeAllMapElements clears it and refills it keyed by
    // BasePolygon.Id; PaintMap then looks each one up with the indexer, not TryGetValue. A polygon
    // that appears between compute and paint is a KeyNotFoundException. That is why each layer is a
    // fixed pool allocated once: the set of Ids never changes, so every key paint asks for was
    // written by the compute that preceded it, whatever happened in between. Only the Points
    // references are swapped, and paint never reads Points - it works purely from the table.
    // ---------------------------------------------------------------------------------------------

    void EnsureMaps()
    {
        // Contains is a read, and concurrent reads of a List are safe - it is only mutation during
        // enumeration that throws. This catches a map reload having dropped our layers.
        if (_fill != null && _border != null
            && DisplayMaps.Maps.Contains(_fill.Map)
            && DisplayMaps.Maps.Contains(_border.Map))
            return;

        RegisterColour(FillColourName, FillAlpha);
        RegisterColour(BorderColourName, BorderAlpha);

        var fill = BuildLayer(FillMapName, FillColourName, border: false);
        var border = BuildLayer(BorderMapName, BorderColourName, border: true);

        _fill = fill;
        _border = border;

        // The swap described in MutationSafety. Any earlier copy of ours is filtered out by name
        // rather than by reference, so a half-applied state cannot leave a duplicate behind.
        var replacement = DisplayMaps.Maps
            .Where(map => map.Name != FillMapName && map.Name != BorderMapName)
            .ToList();

        replacement.Add(fill.Map);
        replacement.Add(border.Map);

        SetMaps(replacement);
    }

    static Layer BuildLayer(string name, string colourName, bool border)
    {
        var map = new DisplayMaps.Map
        {
            Name = name,
            Category = DisplayMaps.MapCategories.ASD,
            Type = DisplayMaps.MapTypes.Filled,
            Pattern = DisplayMaps.Map.Patterns.Solid,
            CustomColourName = colourName,
            Priority = DisplayMaps.Map.PRIORITY_HIGHEST
        };

        var pool = new DisplayMaps.Map.BasePolygon[PoolSize];

        for (var i = 0; i < PoolSize; i++)
        {
            if (border)
            {
                // Patterns.Solid is the branch that strokes with no StrokeStyle at all - a plain
                // DrawLine per segment. Every other pattern applies a dash style.
                var line = new DisplayMaps.Map.Line
                {
                    Name = name,
                    Pattern = DisplayMaps.Map.Patterns.Solid,
                    Width = BorderWidth,
                    Points = Hidden
                };

                map.Lines.Add(line);
                pool[i] = line;
            }
            else
            {
                var infill = new DisplayMaps.Map.Infill
                {
                    Name = name,
                    Type = DisplayMaps.Map.InfillTypes.Normal,
                    Pattern = DisplayMaps.Map.Patterns.Solid,
                    Points = Hidden
                };

                map.Infills.Add(infill);
                pool[i] = infill;
            }
        }

        return new Layer(map, pool);
    }

    void ApplyPolygons()
    {
        List<List<Coordinate>> polygons;
        lock (_lock)
            polygons = _polygons;

        // The same shapes drive both layers - the border is the outline of what the fill shades.
        foreach (var layer in Layers())
        {
            // Reference swaps only, never Points.Clear() or AddRange - see MutationSafety.
            for (var i = 0; i < layer.Pool.Length; i++)
                layer.Pool[i].Points = i < polygons.Count ? polygons[i] : Hidden;
        }
    }

    void EnsureVisible()
    {
        var maps = Layers().Select(layer => layer.Map).ToList();
        if (maps.Count == 0)
            return;

        foreach (var asd in AsdControls())
        {
            var type = asd.GetType();
            var setVisible = type.GetMethod("SetMapVisible", new[] { typeof(DisplayMaps.Map), typeof(bool) });
            if (setVisible == null)
                continue;

            var isVisible = type.GetMethod("IsMapVisible", new[] { typeof(DisplayMaps.Map) });

            foreach (var map in maps)
            {
                // Checked first because SetMapVisible ends in SetRenderParams, which forces a full
                // redraw of that scope. Called every couple of seconds unconditionally, that would
                // be a permanent and pointless load on every open display.
                if (isVisible?.Invoke(asd, new object[] { map }) is true)
                    continue;

                setVisible.Invoke(asd, new object[] { map, true });
            }
        }
    }

    IEnumerable<Layer> Layers()
    {
        if (_fill != null)
            yield return _fill;

        if (_border != null)
            yield return _border;
    }

    // Registers a highlight colour in the Colours custom table, so CustomColourName resolves. Done
    // by reflection rather than by referencing SharpDX: the plugin has no SharpDX reference and
    // adding one would mean shipping a second copy of an assembly vatSys already loads, which is the
    // thing every Private=False in the csproj exists to prevent. The colour value is constructed
    // through the dictionary's own value type, so it is whatever SharpDX.Color vatSys is using.
    static void RegisterColour(string name, byte alpha)
    {
        if (Colours.CustomColourExists(name))
            return;

        var field = typeof(Colours).GetField(
            "customColoursDX", BindingFlags.NonPublic | BindingFlags.Static);

        var table = field?.GetValue(null);
        if (table == null)
            return;

        var colourType = field!.FieldType.GetGenericArguments()[1];
        var colour = Activator.CreateInstance(
            colourType, HighlightRed, HighlightGreen, HighlightBlue, alpha);

        field.FieldType.GetMethod("set_Item")?.Invoke(table, new[] { name, colour });
    }

    static void SetMaps(List<DisplayMaps.Map> maps)
    {
        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;

        var setter = typeof(DisplayMaps).GetMethod("set_Maps", Flags);
        if (setter != null)
        {
            setter.Invoke(null, new object[] { maps });
            return;
        }

        typeof(DisplayMaps)
            .GetField("<Maps>k__BackingField", Flags)
            ?.SetValue(null, maps);
    }

    // The scopes, found by walking the control tree rather than through the private ASDWindow.ASD
    // and MainForm.ASD fields - one walk covers the main display, every popped-out ASD window and
    // any opened later, without naming a field per host.
    static IEnumerable<Control> AsdControls() =>
        Application.OpenForms.Cast<Form>()
            .ToList()
            .SelectMany(Descendants)
            .Where(control => control.GetType().Name == "ASDControlDX");

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;

            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    List<SectorsVolumes.Sector> SectorsIn(IReadOnlyList<OzServerSectorOwnershipRequestDto> requests) =>
        requests
            .Where(request => request.RejectedAt == null && request.Sector != null)
            .Select(request => request.Sector!.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => SectorsVolumes.Sectors.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(sector => sector != null)
            .Select(sector => sector!)
            .ToList();

    // One polygon per volume. A sector is not necessarily a single polygon - it can be several
    // disjoint volumes - and merging them would produce a shape that does not exist.
    static IEnumerable<List<Coordinate>> Boundaries(SectorsVolumes.Sector sector)
    {
        foreach (var volume in sector.Volumes)
        {
            if (volume.Boundary == null || volume.Boundary.Count < 3)
                continue;

            var points = volume.Boundary.ToList();

            // Closed explicitly. The dataset's boundaries do not reliably repeat their first point,
            // and for the border that would leave the ring visibly open along one edge - PaintMap
            // strokes a line as its consecutive segments and closes nothing itself.
            if (!points[0].Equals(points[points.Count - 1]))
                points.Add(points[0]);

            yield return points;
        }
    }

    static List<Coordinate> Degenerate()
    {
        var origin = new Coordinate(0.0, 0.0);
        return new List<Coordinate> { origin, origin, origin };
    }

    static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
