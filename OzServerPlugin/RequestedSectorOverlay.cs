using System;
using System.Collections.Generic;
using System.Linq;
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
// Two layers because a map has exactly one brush (see AsdMapLayer): the fill would swallow its own
// border at the same alpha. The border layer is created second so it paints over the shading -
// PaintMaps walks DisplayMaps.Maps in order and the Join preserves it.
public class RequestedSectorOverlay
{
    const int PoolSize = 64;

    const byte HighlightRed = 255;
    const byte HighlightGreen = 235;
    const byte HighlightBlue = 0;
    const byte FillAlpha = 70;
    const byte BorderAlpha = 255;
    const float BorderWidth = 2f;

    readonly OzServerOwnershipTracker _tracker;
    readonly AsdMapLayer _fill;
    readonly AsdMapLayer _border;

    // What is being asked for, and whether the controller is currently looking at it. Tracked
    // whether or not anything is on screen, so opening the window shows what is pending right now
    // rather than only what arrives afterwards.
    List<SectorsVolumes.Sector> _requested = new();
    bool _revealed;

    public RequestedSectorOverlay(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;

        _fill = new AsdMapLayer("OzServer Requested", "OzServerRequestedSector",
            HighlightRed, HighlightGreen, HighlightBlue, FillAlpha, PoolSize, lineWidth: null);

        _border = new AsdMapLayer("OzServer Requested Border", "OzServerRequestedSectorBorder",
            HighlightRed, HighlightGreen, HighlightBlue, BorderAlpha, PoolSize, BorderWidth);

        _tracker.IncomingRequestsChanged += (_, requests) => SetRequested(SectorsIn(requests));
        Network.Disconnected += (_, _) => Clear();
    }

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
        var polygons = sectors.SelectMany(Boundaries).ToList();

        // The same shapes drive both layers - the border is the outline of what the fill shades.
        _fill.SetPolygons(polygons);
        _border.SetPolygons(polygons);

        ActionLog.Log("Overlay", sectors.Count == 0
            ? $"highlight cleared ({_requested.Count} request(s) pending, revealed={_revealed})"
            : $"{sectors.Count} sector(s) highlighted: {string.Join(", ", sectors.Select(s => s.Name))}");
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
}
