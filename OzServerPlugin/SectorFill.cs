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
        if (polygons.Count == 0)
            return polygons.ToList();

        try
        {
            // Each volume is resolved on its own first, and the results are then merged.
            //
            // A volume built from several boundaries (BIK is BINDOOK_A and BINDOOK_B) reaches here
            // as one point list holding every ring end to end, and vatSys reverses rings as it
            // joins them (SectorsVolumes.LoadVolumes), so the rings of one volume can be wound in
            // opposite directions. NonZero takes a ring whichever way it runs. Merging the raw
            // lists in one pass cannot: a ring wound against the rest of its volume is dropped, or
            // cancels the same airspace in a neighbouring volume, and that area is left unshaded.
            //
            // Longitude as X and latitude as Y. Where the volumes run across the antimeridian (the
            // Pacific oceanic sectors) longitudes are taken east, 0 to 360, for the merge: as
            // stored, -180 to 180, such a boundary doubles back across the whole map.
            var acrossAntimeridian = polygons.Any(p => p.Any(c => c.Longitude > 90))
                                     && polygons.Any(p => p.Any(c => c.Longitude < -90));
            double East(double longitude) => acrossAntimeridian && longitude < 0 ? longitude + 360 : longitude;

            var paths = new PathsD();
            foreach (var polygon in polygons)
            {
                var path = new PathD(polygon.Select(c => new PointD(East(c.Longitude), c.Latitude)));
                paths.AddRange(Clipper.BooleanOp(ClipType.Union, new PathsD { path }, null, FillRule.NonZero, Precision));
            }

            // Every outline from the first pass is wound the same way, so nothing cancels here.
            var merged = Clipper.BooleanOp(ClipType.Union, paths, null, FillRule.Positive, Precision);

            // Outer outlines only. A hole (negative area) cannot be shown: an infill is a single
            // closed figure. Its area is shaded with the surrounding shape.
            var result = new List<List<Coordinate>>();
            foreach (var path in merged)
            {
                if (path.Count < 3 || Clipper.Area(path) <= 0)
                    continue;

                var points = path.Select(p => new Coordinate(p.y, p.x > 180 ? p.x - 360 : p.x)).ToList();
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
