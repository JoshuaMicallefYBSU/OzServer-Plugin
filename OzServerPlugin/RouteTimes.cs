using System;
using System.Collections.Generic;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// The route times OzServer keeps for a flight (OzServerRoutePointDto.Ato, Est and Hold, pushed by
// FdrSync.FillRoute), put back onto a tag that has just become this controller's: by reconnect,
// sector change or pickup, through LabelStateRestore.Apply.
//
// vatSys shares estimates between the controllers connected at the time, but a controller who
// connects later, or whose vatSys missed them, starts with none of it - and with no times over
// waypoints passed before they could see the aircraft.
//
// Only filled in where vatSys has nothing, as with the scratchpad: a time vatSys already holds was
// worked out or shared here, and is at least as new as the record.
public static class RouteTimesRestore
{
    // Returns what was written, for the log, or null if nothing was.
    public static string? Apply(FDP2.FDR fdr, IList<OzServerRoutePointDto> stored)
    {
        List<FDP2.FDR.ExtractedRoute.Segment> segments;
        try
        {
            segments = fdr.ParsedRoute.ToList();
        }
        catch (Exception)
        {
            return null; // being re-parsed; the record is read again on the next pickup
        }

        var matched = Match(segments, stored);
        if (matched.Count == 0)
            return null;

        var applied = new List<string>();

        // Passed points first, in route order, through vatSys's own position report (without the
        // messages it would send for one made here): it moves the route on and works the estimates
        // ahead out from the time over.
        foreach (var (point, segment) in matched)
        {
            if (point.Ato is not { } ato || FdrSync.RouteTime(segment.ATO) != null)
                continue;
            FDP2.PositionReport(fdr, segment, ato, false);
            applied.Add($"over {point.Name} {ato:HHmm}");
        }

        // A hold sets the estimates at its fix and the next waypoint itself (FDP2.Hold), from this
        // vatSys's own estimate for the fix: those two are not then set again below.
        var done = new HashSet<FDP2.FDR.ExtractedRoute.Segment>();
        foreach (var (point, segment) in matched)
        {
            if (point.Hold is not { } seconds || seconds <= 0 || segment.IsPETO || FdrSync.RouteTime(segment.ATO) != null)
                continue;
            if (FDP2.Hold(fdr, segment, TimeSpan.FromSeconds(seconds), out var exit))
            {
                done.Add(segment);
                if (exit != null)
                    done.Add(exit);
                applied.Add($"hold at {point.Name} {seconds / 60} min");
            }
        }

        foreach (var (point, segment) in matched)
        {
            if (point.Est is not { } est || done.Contains(segment) || segment.IsPETO || FdrSync.RouteTime(segment.ATO) != null)
                continue;
            FDP2.SetPETO(fdr, segment, est);
            applied.Add($"estimate {point.Name} {est:HHmm}");
        }

        return applied.Count > 0 ? string.Join(", ", applied) : null;
    }

    // Each stored point that carries a time, with the segment of this vatSys's route it is: the
    // same points FillRoute sends (waypoints and crossings with a position, no trial route), paired
    // in order by name, so a waypoint the route passes twice pairs with the right one. A point this
    // route no longer has - it was changed since - is left out.
    static List<(OzServerRoutePointDto Point, FDP2.FDR.ExtractedRoute.Segment Segment)> Match(
        List<FDP2.FDR.ExtractedRoute.Segment> segments, IList<OzServerRoutePointDto> stored)
    {
        var local = segments
            .Where(s => s?.Intersection?.LatLong != null && s.Type != FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.TRIAL)
            .ToList();

        var pairs = new List<(OzServerRoutePointDto, FDP2.FDR.ExtractedRoute.Segment)>();
        var from = 0;
        foreach (var point in stored)
        {
            if (point == null || string.IsNullOrEmpty(point.Name))
                continue;
            var index = local.FindIndex(from, s => string.Equals(s.Intersection.Name, point.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                continue;
            from = index + 1;
            if (point.Ato != null || point.Est != null || point.Hold != null)
                pairs.Add((point, local[index]));
        }

        return pairs;
    }
}
