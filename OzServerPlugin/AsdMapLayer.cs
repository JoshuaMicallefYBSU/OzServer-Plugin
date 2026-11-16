using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// A map layer of the plugin's own on the radar picture, drawn by vatSys's renderer.
//
// The constraints below come from what ASDControlDX.Render does:
//
//   var visible = DisplayMaps.Maps.Join(renderParams.VisibleMaps, m => m.Id, id => id, ...);
//   ComputeAllMapElements(renderParams, visible);      // outside any try/catch
//   try {
//       PaintMaps(visible);
//   } catch (SharpDXException) { ... }
//
// 1. A map is drawn only if its Id is in RenderParams.VisibleMaps, so adding a layer to
//    DisplayMaps.Maps is not enough. ASDControlDX.SetMapVisible(map, true) adds the Id through
//    SetRenderParams; Map's public constructor assigns a unique Id.
//
// 2. Colour is per map: SelectMapBrush is called once per map and used for its infills and lines
//    alike, so two colours means two layers. A non-empty CustomColourName takes the brush colour
//    from Colours.GetCustomColorDX, which RegisterColour populates.
//
// 3. Render runs on its own thread and does not catch exceptions caused by map data, so a mistake
//    here stops the scope drawing permanently. See MutationSafety before changing how maps or
//    points are published.
public sealed class AsdMapLayer
{
    // All layers are reasserted on one timer: a scope opened after a layer was created has not
    // been told about it, and reloading maps or a position replaces DisplayMaps.Maps and drops
    // every layer.
    static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(2);
    static readonly List<AsdMapLayer> Layers = new();
    static readonly object LayersLock = new();
    static System.Threading.Timer? _reassertTimer;

    // Three references to one Coordinate: a zero-area triangle, which paints nothing. An unused
    // slot must not have an empty point list - ComputeMapElements skips it and writes no vector
    // table entry, but PaintMap still calls EndFigure on a figure it never began, and the resulting
    // SharpDXException abandons the rest of the frame's maps.
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

    // A null lineWidth draws filled shapes; otherwise they are stroked at that width. Infill and
    // Line are both BasePolygon, which is where Points lives.
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

    // The shapes this layer draws, in lat/lon. Any beyond the pool size are dropped: the pool is
    // fixed (see MutationSafety).
    //
    // The same shapes again change nothing and are dropped here: every apply ends in a background
    // redraw, which recomputes every map on every scope - see Reassert.
    public void SetPolygons(IEnumerable<List<Coordinate>> polygons)
    {
        var next = polygons.Take(_poolSize).ToList();
        lock (_lock)
        {
            if (SameShapes(_polygons, next))
                return;
            _polygons = next;
        }

        RunOnUiThread(ApplyNow);
    }

    static bool SameShapes(List<List<Coordinate>> a, List<List<Coordinate>> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Count != b[i].Count)
                return false;
            for (var j = 0; j < a[i].Count; j++)
            {
                if (a[i][j].Latitude != b[i][j].Latitude || a[i][j].Longitude != b[i][j].Longitude)
                    return false;
            }
        }
        return true;
    }

    // The fast path: swaps the points and requests a repaint. Creating the map and asserting its
    // visibility walk every loaded map and every open form, so they are left to the timer - except
    // on the first call, when the layer has to exist before it can take points.
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

        // A background redraw only when this pass changed something - the layer re-added after a
        // map reload, or made visible on a scope that did not show it. MMI.RequestRedraw(true)
        // makes every scope drop its cached map image and recompute every map, as on a pan or
        // zoom; asked for unconditionally, four layers every two seconds, it was steady load the
        // slower PCs could not carry.
        try
        {
            var changed = EnsureMap();
            if (changed)
                ApplyPolygons();
            changed |= EnsureVisible();
            if (changed)
                MMI.RequestRedraw(true, false, false);
        }
        catch (Exception ex)
        {
            // Swallowed deliberately. The layer is decoration: if the vatSys internals it relies on
            // change, it stops drawing rather than taking the plugin or the scope with it.
            ActionLog.Log("MapLayer", $"{_mapName}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // MutationSafety
    //
    // Render runs on its own thread (RenderLoop), which catches only OperationCanceledException.
    // The paint block catches only SharpDXException and the compute call is outside even that, so
    // an exception caused by this code does not drop a frame: it escapes the render loop and the
    // scope stops drawing permanently. A collection the renderer might be walking is therefore
    // never mutated; a new one is built and swapped in. A reference assignment is atomic, so the
    // renderer sees either the old collection or the new one, each internally consistent.
    //
    // 1. Enumeration. DisplayMaps.Maps is enumerated twice per frame (the Join is lazy, so both
    //    ComputeAllMapElements and PaintMaps walk it), and List throws if it is modified meanwhile.
    //    Maps are published by swapping in a new list, never with DisplayMaps.Maps.Add.
    //
    // 2. The vector table. ComputeAllMapElements clears it and refills it keyed by BasePolygon.Id,
    //    and PaintMap reads it with the indexer, not TryGetValue, so a polygon that appears between
    //    compute and paint is a KeyNotFoundException. The pool is therefore fixed and allocated
    //    once, so the set of Ids never changes. Only Points references are swapped, and paint does
    //    not read Points - it works from the table.
    // ---------------------------------------------------------------------------------------------

    // Returns whether the layer had to be (re)created.
    bool EnsureMap()
    {
        // Detects a map reload having dropped the layer. Contains only reads, and concurrent reads
        // of a List are safe.
        if (_map != null && DisplayMaps.Maps.Contains(_map))
            return false;

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
                // Patterns.Solid is the only pattern stroked without a StrokeStyle; every other one
                // applies a dash style.
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

        // The swap described in MutationSafety. An earlier copy of this layer is filtered out by
        // name rather than by reference, so a half-applied state cannot leave a duplicate.
        var replacement = DisplayMaps.Maps.Where(existing => existing.Name != _mapName).ToList();
        replacement.Add(map);
        SetMaps(replacement);
        return true;
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

    // Returns whether any scope had to be told to show the layer.
    bool EnsureVisible()
    {
        var changed = false;
        var map = _map;
        if (map == null)
            return false;

        foreach (var asd in AsdControls())
        {
            var type = asd.GetType();
            var setVisible = type.GetMethod("SetMapVisible", new[] { typeof(DisplayMaps.Map), typeof(bool) });
            if (setVisible == null)
                continue;

            // Checked first: SetMapVisible ends in SetRenderParams, which forces a full redraw of
            // that scope, and this runs every ReassertInterval.
            var isVisible = type.GetMethod("IsMapVisible", new[] { typeof(DisplayMaps.Map) });
            if (isVisible?.Invoke(asd, new object[] { map }) is true)
                continue;

            setVisible.Invoke(asd, new object[] { map, true });
            changed = true;
        }
        return changed;
    }

    // Registers a colour in the Colours custom table so CustomColourName resolves. Done by
    // reflection because the plugin has no SharpDX reference, and adding one would ship a second
    // copy of an assembly vatSys already loads. The value is built through the dictionary's own
    // value type, so it is the SharpDX.Color vatSys uses. An unregistered name is not fatal:
    // GetCustomColorDX returns AliceBlue rather than throwing.
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

    // Every scope, found by walking the control tree rather than through the private ASDWindow.ASD
    // and MainForm.ASD fields: one walk covers the main display and every popped-out ASD window.
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
            mainForm.BeginInvoke(UiThread.Guard(action));
        else
            UiThread.Guard(action)();
    }
}
