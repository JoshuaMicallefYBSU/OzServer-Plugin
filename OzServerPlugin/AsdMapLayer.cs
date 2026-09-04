using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// A layer of our own on the radar picture, drawn by vatSys's renderer rather than by us.
//
// Everything here is forced by what ASDControlDX.Render actually does, and none of it is guessable,
// which is why it lives in one place instead of being written out per feature:
//
//   var visible = DisplayMaps.Maps.Join(renderParams.VisibleMaps, m => m.Id, id => id, ...);
//   ComputeAllMapElements(renderParams, visible);      // outside any try/catch
//   try {
//       PaintMaps(visible);
//   } catch (SharpDXException) { ... }
//
// 1. A map is drawn only if its Id is in RenderParams.VisibleMaps. Map.Id is initonly and
//    VisibleMaps is get-only, which is why simply adding a layer to DisplayMaps.Maps draws nothing.
//    The way through is ASDControlDX.SetMapVisible(map, true) - public, and it reads VisibleMaps,
//    adds the Id and pushes it back through SetRenderParams. Map's public constructor already
//    assigns a unique Id from DisplayMaps.GenerateId(), so no Id has to be forged.
//
// 2. Colour is per map. SelectMapBrush is called once per map and reused for that map's infills and
//    its lines alike, so one layer is exactly one colour - two colours means two layers. A
//    non-empty CustomColourName recolours the shared custom brush from Colours.GetCustomColorDX,
//    which is registered here rather than in a profile file.
//
// 3. Render runs on its own thread (RenderLoop), which catches only OperationCanceledException;
//    the paint block catches only SharpDXException, and the compute call sits outside even that. So
//    an exception this code provokes escapes the render loop and stops the scope drawing for good.
//    Thread safety is therefore a correctness requirement - see MutationSafety.
public sealed class AsdMapLayer
{
    // Every layer is reasserted on one timer rather than each keeping its own: a scope opened after
    // a layer was created has never been told about it, and reloading maps or a position replaces
    // DisplayMaps.Maps wholesale and drops every layer at once.
    static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(2);
    static readonly List<AsdMapLayer> Layers = new();
    static readonly object LayersLock = new();
    static System.Threading.Timer? _reassertTimer;

    // Three references to one Coordinate: a triangle of zero area, which paints nothing. Unused
    // slots cannot simply be emptied - ComputeMapElements skips an empty point list, so no entry is
    // written to the vector table, but PaintMap then calls EndFigure unconditionally on a figure it
    // never began, and the SharpDXException that follows abandons the rest of the frame's maps.
    static readonly List<Coordinate> Hidden = Degenerate();

    readonly string _mapName;
    readonly string _colourName;
    readonly byte _red;
    readonly byte _green;
    readonly byte _blue;
    readonly byte _alpha;
    readonly int _poolSize;
    readonly float? _lineWidth;
    readonly object _lock = new();

    List<List<Coordinate>> _polygons = new();
    DisplayMaps.Map? _map;
    DisplayMaps.Map.BasePolygon[]? _pool;

    // lineWidth null draws filled shapes, otherwise strokes them at that width. Both are the same
    // pipeline - Infill and Line are both BasePolygon, which is where Points lives.
    public AsdMapLayer(string mapName, string colourName,
                       byte red, byte green, byte blue, byte alpha,
                       int poolSize, float? lineWidth)
    {
        _mapName = mapName;
        _colourName = colourName;
        _red = red;
        _green = green;
        _blue = blue;
        _alpha = alpha;
        _poolSize = poolSize;
        _lineWidth = lineWidth;

        lock (LayersLock)
        {
            Layers.Add(this);
            _reassertTimer ??= new System.Threading.Timer(
                _ => RunOnUiThread(ReassertAll), null, ReassertInterval, ReassertInterval);
        }
    }

    // The shapes this layer draws, in lat/lon. Anything beyond the pool size is dropped rather than
    // growing the pool - see MutationSafety for why the pool is fixed.
    public void SetPolygons(IEnumerable<List<Coordinate>> polygons)
    {
        lock (_lock)
            _polygons = polygons.Take(_poolSize).ToList();

        RunOnUiThread(ApplyNow);
    }

    // The fast path, and it has to be fast: freehand drawing calls this on every captured point, so
    // tens of times a second during a drag. Only the points are swapped and a repaint asked for.
    //
    // Creating the map and asserting its visibility are deliberately left to the timer - the first
    // walks every loaded map, the second walks every open form and does reflection per scope, and
    // neither changes between one point of a stroke and the next. The exception is the very first
    // call, which has to build the layer before there is anything to put points into.
    void ApplyNow()
    {
        if (!DisplayMaps.Loaded)
            return;

        try
        {
            if (_map == null)
            {
                EnsureMap();
                EnsureVisible();
            }

            ApplyPolygons();
            MMI.RequestRedraw(true, false, false);
        }
        catch (Exception ex)
        {
            ActionLog.Log("MapLayer", $"{_mapName}: {ex.Message}");
        }
    }

    public void Clear() => SetPolygons(new List<List<Coordinate>>());

    static void ReassertAll()
    {
        List<AsdMapLayer> layers;
        lock (LayersLock)
            layers = Layers.ToList();

        foreach (var layer in layers)
            layer.Reassert();
    }

    void Reassert()
    {
        if (!DisplayMaps.Loaded)
            return;

        try
        {
            EnsureMap();
            ApplyPolygons();
            EnsureVisible();
            MMI.RequestRedraw(true, false, false);
        }
        catch (Exception ex)
        {
            // Swallowed deliberately. This is decoration; if the internals it leans on ever move,
            // the layer should stop drawing, not take the plugin - or the scope - with it.
            ActionLog.Log("MapLayer", $"{_mapName}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // MutationSafety
    //
    // Render runs on its own thread and neither it nor RenderLoop catches the exceptions this code
    // could cause, so a torn read does not drop a frame - it kills the scope permanently. Two
    // hazards, both avoided the same way: never mutate a collection the renderer might be walking,
    // swap a fresh one in instead. A reference assignment is atomic, so the renderer sees either the
    // old collection or the new one, each internally consistent.
    //
    // The first is enumeration. DisplayMaps.Maps is walked twice per frame - the Join is lazy, so
    // both ComputeAllMapElements and PaintMaps enumerate it - and List throws if it is modified
    // while enumerated. So maps are published by building a new list and swapping it in, never by
    // DisplayMaps.Maps.Add.
    //
    // The second is the vector table. ComputeAllMapElements clears it and refills it keyed by
    // BasePolygon.Id; PaintMap then looks each one up with the indexer, not TryGetValue. A polygon
    // appearing between compute and paint is a KeyNotFoundException. That is why the pool is fixed
    // and allocated once: the set of Ids never changes, so every key paint asks for was written by
    // the compute that preceded it. Only Points references are swapped, and paint never reads Points
    // - it works purely from the table.
    // ---------------------------------------------------------------------------------------------

    void EnsureMap()
    {
        // Contains is a read, and concurrent reads of a List are safe - only mutation during
        // enumeration throws. This catches a map reload having dropped our layer.
        if (_map != null && DisplayMaps.Maps.Contains(_map))
            return;

        RegisterColour(_colourName, _red, _green, _blue, _alpha);

        var map = new DisplayMaps.Map
        {
            Name = _mapName,
            Category = DisplayMaps.MapCategories.ASD,
            Type = DisplayMaps.MapTypes.Filled,
            Pattern = DisplayMaps.Map.Patterns.Solid,
            CustomColourName = _colourName,
            Priority = DisplayMaps.Map.PRIORITY_HIGHEST
        };

        var pool = new DisplayMaps.Map.BasePolygon[_poolSize];

        for (var i = 0; i < _poolSize; i++)
        {
            if (_lineWidth is { } width)
            {
                // Patterns.Solid is the one branch that strokes with no StrokeStyle at all - a
                // plain DrawLine per segment. Every other pattern applies a dash style.
                var line = new DisplayMaps.Map.Line
                {
                    Name = _mapName,
                    Pattern = DisplayMaps.Map.Patterns.Solid,
                    Width = width,
                    Points = Hidden
                };

                map.Lines.Add(line);
                pool[i] = line;
            }
            else
            {
                var infill = new DisplayMaps.Map.Infill
                {
                    Name = _mapName,
                    Type = DisplayMaps.Map.InfillTypes.Normal,
                    Pattern = DisplayMaps.Map.Patterns.Solid,
                    Points = Hidden
                };

                map.Infills.Add(infill);
                pool[i] = infill;
            }
        }

        _map = map;
        _pool = pool;

        // The swap described in MutationSafety. Any earlier copy of ours is filtered out by name
        // rather than by reference, so a half-applied state cannot leave a duplicate behind.
        var replacement = DisplayMaps.Maps.Where(existing => existing.Name != _mapName).ToList();
        replacement.Add(map);
        SetMaps(replacement);
    }

    void ApplyPolygons()
    {
        var pool = _pool;
        if (pool == null)
            return;

        List<List<Coordinate>> polygons;
        lock (_lock)
            polygons = _polygons;

        // Reference swaps only, never Points.Clear() or AddRange - see MutationSafety.
        for (var i = 0; i < pool.Length; i++)
            pool[i].Points = i < polygons.Count ? polygons[i] : Hidden;
    }

    void EnsureVisible()
    {
        var map = _map;
        if (map == null)
            return;

        foreach (var asd in AsdControls())
        {
            var type = asd.GetType();
            var setVisible = type.GetMethod("SetMapVisible", new[] { typeof(DisplayMaps.Map), typeof(bool) });
            if (setVisible == null)
                continue;

            // Checked first because SetMapVisible ends in SetRenderParams, which forces a full
            // redraw of that scope. Called every couple of seconds unconditionally, that would be a
            // permanent and pointless load on every open display.
            var isVisible = type.GetMethod("IsMapVisible", new[] { typeof(DisplayMaps.Map) });
            if (isVisible?.Invoke(asd, new object[] { map }) is true)
                continue;

            setVisible.Invoke(asd, new object[] { map, true });
        }
    }

    // Registers a colour in the Colours custom table, so CustomColourName resolves. Done by
    // reflection rather than by referencing SharpDX: the plugin has no SharpDX reference and adding
    // one would mean shipping a second copy of an assembly vatSys already loads, which is the thing
    // every Private=False in the csproj exists to prevent. The value is constructed through the
    // dictionary's own value type, so it is whatever SharpDX.Color vatSys is using. An unregistered
    // name is not fatal - GetCustomColorDX returns AliceBlue rather than throwing.
    static void RegisterColour(string name, byte red, byte green, byte blue, byte alpha)
    {
        if (Colours.CustomColourExists(name))
            return;

        var field = typeof(Colours).GetField(
            "customColoursDX", BindingFlags.NonPublic | BindingFlags.Static);

        var table = field?.GetValue(null);
        if (table == null)
            return;

        var colourType = field!.FieldType.GetGenericArguments()[1];
        var colour = Activator.CreateInstance(colourType, red, green, blue, alpha);

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

        typeof(DisplayMaps).GetField("<Maps>k__BackingField", Flags)?.SetValue(null, maps);
    }

    // The scopes, found by walking the control tree rather than through the private ASDWindow.ASD
    // and MainForm.ASD fields - one walk covers the main display, every popped-out ASD window and
    // any opened later, without naming a field per host.
    public static IEnumerable<Control> AsdControls() =>
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

    static List<Coordinate> Degenerate()
    {
        var origin = new Coordinate(0.0, 0.0);
        return new List<Coordinate> { origin, origin, origin };
    }

    public static void RunOnUiThread(Action action)
    {
        if (Application.OpenForms["MainForm"] is Control mainForm && mainForm.InvokeRequired)
            mainForm.BeginInvoke(action);
        else
            action();
    }
}
