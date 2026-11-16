using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;
using vatsys;

namespace OzServerPlugin;

// The area to shade for a set of sector volumes: their outline merged into one shape, so a spot
// covered by several volumes is shaded once rather than darker for each.
//
// vatSys fills every infill of a map separately (ASDControlDX.PaintMap: one PathGeometry and one
// FillGeometry per infill), so overlapping volumes - a TMA's stacked steps, a sector's lower and
// upper parts, two neighbouring sectors that share airspace - each laid their translucent colour
// over the last. Only the fill is merged: borders are still drawn per volume, which is what shows
// the sector boundaries inside the shading.
public static class SectorFill
{
    // Decimal places of a degree kept by the union: about a centimetre.
    const int Precision = 7;

    public static List<List<Coordinate>> Merge(IReadOnlyList<List<Coordinate>> polygons)
    {
        if (polygons.Count < 2)
            return polygons.ToList();

        try
        {
            // Longitude as X and latitude as Y. Every input is wound the same way, so Positive
            // takes any spot covered at least once whichever way the dataset drew a volume.
            var paths = new PathsD();
            foreach (var polygon in polygons)
            {
                var path = new PathD(polygon.Select(c => new PointD(c.Longitude, c.Latitude)));
                if (Clipper.Area(path) < 0)
                    path = Clipper.ReversePath(path);
                paths.Add(path);
            }

            var merged = Clipper.BooleanOp(ClipType.Union, paths, null, FillRule.Positive, Precision);

            // Outer outlines only. A hole (negative area) cannot be shown: an infill is a single
            // closed figure. Its area is shaded with the surrounding shape.
            var result = new List<List<Coordinate>>();
            foreach (var path in merged)
            {
                if (path.Count < 3 || Clipper.Area(path) <= 0)
                    continue;

                var points = path.Select(p => new Coordinate(p.y, p.x)).ToList();
                points.Add(points[0]);
                result.Add(points);
            }

            return result.Count > 0 ? result : polygons.ToList();
        }
        catch (Exception ex)
        {
            ErrorThrottle.Report("sector-fill", $"Couldn't merge sector shading: {ex.Message}", ex);
            return polygons.ToList();
        }
    }
}
