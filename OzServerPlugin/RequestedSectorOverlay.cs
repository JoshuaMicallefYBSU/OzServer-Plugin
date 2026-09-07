using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Fills, on the controller's own scope, the sectors involved in a pending sector request in either
// direction - shaded transparent with a solid border of the matching colour, so the airspace can be
// seen rather than only named, and two requested sectors that share a boundary still read as two:
//
//   Incoming ("Requested From Me")  - somebody is asking this controller for one of their own
//                                     sectors. Yellow - the colour this overlay has always used.
//   Outgoing ("Requested By Me")    - this controller is asking somebody else for one of theirs.
//                                     Orange - new, so the two directions read as distinct at a
//                                     glance rather than "some airspace is involved, which way is
//                                     anyone's guess". OzServerSectorsWindow's own Requested By/
//                                     From Me headings carry a small swatch of each colour (see
//                                     SectorsView_DrawNode), reading straight off IncomingColour/
//                                     OutgoingColour below so the legend and the highlight it
//                                     describes can never drift apart.
//
// Shown while the sector management window is open and taken off the scope when it closes, not the
// moment a request lands - see SetRevealed. The highlight answers "which airspace is this", a
// question only being asked once the controller has gone to look at the request; painting the map
// before then interrupts someone working traffic with something they have not asked about. The
// arrival itself is announced by the Settings header's flash, its badge and NotificationSound.
//
// Two layers per direction because a map has exactly one brush (see AsdMapLayer): the fill would
// swallow its own border at the same alpha. Each border layer is created after its fill so it
// paints over the shading - PaintMaps walks DisplayMaps.Maps in order and the Join preserves it.
public class RequestedSectorOverlay
{
    const int PoolSize = 64;

    const byte IncomingRed = 255, IncomingGreen = 235, IncomingBlue = 0; // yellow - unchanged from before this had a second direction
    const byte OutgoingRed = 255, OutgoingGreen = 140, OutgoingBlue = 0; // orange
    const byte FillAlpha = 70;
    const byte BorderAlpha = 255;
    const float BorderWidth = 2f;

    public static Color IncomingColour { get; } = Color.FromArgb(IncomingRed, IncomingGreen, IncomingBlue);
    public static Color OutgoingColour { get; } = Color.FromArgb(OutgoingRed, OutgoingGreen, OutgoingBlue);

    readonly OzServerOwnershipTracker _tracker;
    readonly AsdMapLayer _incomingFill;
    readonly AsdMapLayer _incomingBorder;
    readonly AsdMapLayer _outgoingFill;
    readonly AsdMapLayer _outgoingBorder;

    // What is being asked for in each direction, and whether the controller is currently looking at
    // it. Tracked whether or not anything is on screen, so opening the window shows what is pending
    // right now rather than only what arrives afterwards.
    List<SectorsVolumes.Sector> _incoming = new();
    List<SectorsVolumes.Sector> _outgoing = new();
    bool _revealed;

    public RequestedSectorOverlay(OzServerOwnershipTracker tracker)
    {
        _tracker = tracker;

        _incomingFill = new AsdMapLayer("OzServer Requested", "OzServerRequestedSector",
            IncomingRed, IncomingGreen, IncomingBlue, FillAlpha, PoolSize, lineWidth: null);
        _incomingBorder = new AsdMapLayer("OzServer Requested Border", "OzServerRequestedSectorBorder",
            IncomingRed, IncomingGreen, IncomingBlue, BorderAlpha, PoolSize, BorderWidth);

        _outgoingFill = new AsdMapLayer("OzServer Requesting", "OzServerRequestingSector",
            OutgoingRed, OutgoingGreen, OutgoingBlue, FillAlpha, PoolSize, lineWidth: null);
        _outgoingBorder = new AsdMapLayer("OzServer Requesting Border", "OzServerRequestingSectorBorder",
            OutgoingRed, OutgoingGreen, OutgoingBlue, BorderAlpha, PoolSize, BorderWidth);

        _tracker.IncomingRequestsChanged += (_, requests) => SetIncoming(SectorsIn(requests));
        // RequestsChanged carries both directions and fires on every sync (not only when the set
        // actually changes, unlike IncomingRequestsChanged) - fine here, since Apply just
        // recomputes the same handful of polygons each time rather than needing its own dedup.
        _tracker.RequestsChanged += (_, requests) => SetOutgoing(requests.ByMe);
        Network.Disconnected += (_, _) => Clear();
    }

    public void SetIncoming(IReadOnlyList<SectorsVolumes.Sector> sectors)
    {
        // Shaded to what would actually move, not the whole responsible-sectors group the named
        // sector expands through. That WAS the whole group once - accepting a request for BLA handed
        // over ELW and the Melbourne Approach sectors right along with it, because the backend's own
        // transfer did the same undiscriminating expansion, and shading BLA alone would have shown
        // only a fraction of the airspace actually at risk.
        //
        // It no longer works that way (see transferRequest/covered's own "only what the accepting
        // controller actually holds" fix): a sector someone else is already working - staffed,
        // running this plugin or not - stays with them, and only what THIS controller actually owns
        // among the covered set would ever move. Still shading the old, wider group here kept this
        // overlay telling the controller a request covered airspace that had not been at risk since
        // that fix landed - MAE and MAV highlighted, and looking taken, for a BLA request while a
        // controller was sitting on them the entire time. IsMine narrows it to what is actually
        // being asked for, the same filter the backend itself now applies.
        _incoming = sectors
            .SelectMany(PrimaryPosition.CoveredBy)
            .Where(_tracker.IsMine)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        Apply();
    }

    public void SetOutgoing(IReadOnlyList<OzServerSectorOwnershipRequestDto> requests)
    {
        // Mirrors SetIncoming's own "what would actually move" narrowing, from the other side: a
        // request only ever moves what its own target currently holds among the covered set (see
        // the same transferRequest/covered fix referenced above), so each request is narrowed
        // against its own TargetCid rather than the whole group it names - a covered sub-sector a
        // *different* controller happens to hold is never at risk from this request and should not
        // paint as if it were.
        _outgoing = requests
            .Where(request => request.RejectedAt == null && request.Sector != null)
            .SelectMany(request =>
            {
                var named = SectorsVolumes.Sectors.FirstOrDefault(s =>
                    string.Equals(s.Name, request.Sector!.Name, StringComparison.OrdinalIgnoreCase));

                return named == null
                    ? Enumerable.Empty<SectorsVolumes.Sector>()
                    : PrimaryPosition.CoveredBy(named)
                        .Where(covered => _tracker.OwnerOf(covered)?.Cid == request.TargetCid);
            })
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

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
        _incoming = new List<SectorsVolumes.Sector>();
        _outgoing = new List<SectorsVolumes.Sector>();
        Apply();
    }

    void Apply()
    {
        var incoming = _revealed ? _incoming : new List<SectorsVolumes.Sector>();
        var outgoing = _revealed ? _outgoing : new List<SectorsVolumes.Sector>();
        var incomingPolygons = incoming.SelectMany(Boundaries).ToList();
        var outgoingPolygons = outgoing.SelectMany(Boundaries).ToList();

        // The same shapes drive each direction's pair of layers - the border is the outline of what
        // the fill shades.
        _incomingFill.SetPolygons(incomingPolygons);
        _incomingBorder.SetPolygons(incomingPolygons);
        _outgoingFill.SetPolygons(outgoingPolygons);
        _outgoingBorder.SetPolygons(outgoingPolygons);

        ActionLog.Log("Overlay",
            incoming.Count == 0 && outgoing.Count == 0
                ? $"highlight cleared ({_incoming.Count} incoming, {_outgoing.Count} outgoing pending, revealed={_revealed})"
                : $"{incoming.Count} incoming, {outgoing.Count} outgoing sector(s) highlighted"
                  + (incoming.Count > 0 ? $" - from me: {string.Join(", ", incoming.Select(s => s.Name))}" : "")
                  + (outgoing.Count > 0 ? $" - by me: {string.Join(", ", outgoing.Select(s => s.Name))}" : ""));
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
