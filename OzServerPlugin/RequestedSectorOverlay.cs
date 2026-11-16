using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using vatsys;

namespace OzServerPlugin;

// Shades, on the controller's own scope, the sectors in a pending sector request - a transparent
// fill with a solid border of the same colour, so adjoining sectors still read as separate:
//
//   Incoming ("Requested From Me") - another controller is asking for this controller's sectors.
//                                    Yellow.
//   Outgoing ("Requested By Me")   - this controller is asking for, or has staged, sectors.
//                                    Light green.
//   Releasing                      - sectors staged out of Owned in the Sectors window, which
//                                    Apply will give back. Soft red.
//
// OzServerSectorsWindow draws a swatch of each colour from IncomingColour, OutgoingColour and
// ReleasingColour (SectorsView_DrawNode, the Owned heading), so the legend cannot drift from the
// highlight.
//
// Shown only while the Sectors window is open (SetRevealed). The highlight answers "which airspace
// is this", and painting the scope before the controller has gone to look would interrupt someone
// working traffic. The arrival itself is announced by the Settings header's flash, its badge and
// NotificationSound.
//
// Two layers per direction because a map has one brush (see AsdMapLayer), and a border at the
// fill's alpha would not show. Each border layer is created after its fill so it paints over the
// shading: PaintMaps walks DisplayMaps.Maps in order.
public class RequestedSectorOverlay
{
    const int PoolSize = 64;

    const byte IncomingRed = 255, IncomingGreen = 235, IncomingBlue = 0; // yellow
    const byte OutgoingRed = 144, OutgoingGreen = 238, OutgoingBlue = 144; // light green
    // Red for "going away", softened to the lightness of the other two so the three read as one
    // family, and kept clear of vatSys's own saturated alert reds.
    const byte ReleasingRed = 255, ReleasingGreen = 120, ReleasingBlue = 110; // soft red
    const byte FillAlpha = 70;
    const byte BorderAlpha = 255;
    const float BorderWidth = 2f;

    public static Color IncomingColour { get; } = Color.FromArgb(IncomingRed, IncomingGreen, IncomingBlue);
    public static Color OutgoingColour { get; } = Color.FromArgb(OutgoingRed, OutgoingGreen, OutgoingBlue);
    public static Color ReleasingColour { get; } = Color.FromArgb(ReleasingRed, ReleasingGreen, ReleasingBlue);

    readonly OzServerOwnershipTracker _tracker;
    readonly AsdMapLayer _incomingFill;
    readonly AsdMapLayer _incomingBorder;
    readonly AsdMapLayer _outgoingFill;
    readonly AsdMapLayer _outgoingBorder;
    readonly AsdMapLayer _releasingFill;
    readonly AsdMapLayer _releasingBorder;

    // Tracked whether or not anything is on screen, so opening the window shows what is already
    // pending.
    List<SectorsVolumes.Sector> _incoming = new();
    // Outgoing is two lists because a sector is pending in two ways: staged in the Sectors window
    // but not yet applied (SetStaged), which the tracker cannot see, and sent and awaiting an
    // answer (SetOutgoing). ApplyNow unions them, so a request does not flicker as it moves from
    // one list to the other.
    List<SectorsVolumes.Sector> _stagedOutgoing = new();
    List<SectorsVolumes.Sector> _confirmedOutgoing = new();
    // Staged releases, expanded to everything each gives back (see SetStaged).
    List<SectorsVolumes.Sector> _stagedReleases = new();
    bool _revealed;
    // The last line ApplyNow logged. RequestsChanged fires on every sync, so a line is written
    // only when it differs.
    string? _lastLogged;

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

        _releasingFill = new AsdMapLayer("OzServer Releasing", "OzServerReleasingSector",
            ReleasingRed, ReleasingGreen, ReleasingBlue, FillAlpha, PoolSize, lineWidth: null);
        _releasingBorder = new AsdMapLayer("OzServer Releasing Border", "OzServerReleasingSectorBorder",
            ReleasingRed, ReleasingGreen, ReleasingBlue, BorderAlpha, PoolSize, BorderWidth);

        _tracker.IncomingRequestsChanged += (_, requests) => SetIncoming(SectorsIn(requests));
        // Unlike IncomingRequestsChanged, RequestsChanged fires on every sync whether or not the
        // set changed. ApplyNow only recomputes a handful of polygons, so it is not deduplicated.
        _tracker.RequestsChanged += (_, requests) => SetOutgoing(requests.ByMe);
        Network.Disconnected += (_, _) => Clear();
    }

    public void SetIncoming(IReadOnlyList<SectorsVolumes.Sector> sectors)
    {
        if (OzServerMode.Disabled)
            return;

        // Narrowed to what would move. The backend transfers only the sectors the accepting
        // controller holds among those the named sector covers (transferRequest in the API), so a
        // covered sector another controller holds is not shaded.
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
        if (OzServerMode.Disabled)
            return;

        // The same narrowing as SetIncoming, from the other side: a request moves only what its
        // target holds among the covered sectors, so each is narrowed against its own TargetCid.
        _confirmedOutgoing = requests
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

    // Staged changes that have not been applied, each narrowed as its applied counterpart is:
    //
    //   stagedClaims   - expanded through the whole covered group with no ownership narrowing,
    //                    which is what a claim asks for (claimGroup in the API).
    //   stagedRequests - narrowed to the covered sectors held by the named sector's current owner.
    //                    Nothing has been sent, so there is no TargetCid; that owner stands in.
    //   stagedReleases - expanded through the covered group and narrowed to what this controller
    //                    owns, which is what a release gives back (releaseGroup in the API).
    //
    // Called from OzServerSectorsWindow.RefreshStagedHighlight, which every staging change goes
    // through.
    public void SetStaged(IReadOnlyList<SectorsVolumes.Sector> stagedClaims, IReadOnlyList<SectorsVolumes.Sector> stagedRequests,
        IReadOnlyList<SectorsVolumes.Sector> stagedReleases)
    {
        if (OzServerMode.Disabled)
            return;

        var claimed = stagedClaims.SelectMany(PrimaryPosition.CoveredBy);

        var requested = stagedRequests.SelectMany(named =>
        {
            var owner = _tracker.OwnerOf(named)?.Cid;
            return PrimaryPosition.CoveredBy(named).Where(covered => _tracker.OwnerOf(covered)?.Cid == owner);
        });

        _stagedOutgoing = claimed.Concat(requested)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        _stagedReleases = stagedReleases
            .SelectMany(PrimaryPosition.CoveredBy)
            .Where(_tracker.IsMine)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        Apply();
    }

    // Driven by the Sectors window's visibility, so the shading comes off the scope when the
    // window closes.
    public void SetRevealed(bool revealed)
    {
        if (OzServerMode.Disabled)
            return;

        if (_revealed == revealed)
            return;

        _revealed = revealed;
        Apply();
    }

    public void Clear()
    {
        _incoming = new List<SectorsVolumes.Sector>();
        _stagedOutgoing = new List<SectorsVolumes.Sector>();
        _confirmedOutgoing = new List<SectorsVolumes.Sector>();
        _stagedReleases = new List<SectorsVolumes.Sector>();
        Apply();
    }

    // Marshalled because the tracker raises IncomingRequestsChanged and RequestsChanged on a pool
    // thread, while SetStaged and SetRevealed come from the UI. Running every pass on one thread
    // stops a stale pool-thread pass landing after a newer UI one and restoring a highlight the
    // controller has just closed.
    void Apply() => AsdMapLayer.RunOnUiThread(ApplyNow);

    string? _lastShapesKey;

    void ApplyNow()
    {
        var incoming = _revealed ? _incoming : new List<SectorsVolumes.Sector>();
        var outgoing = _revealed
            ? _stagedOutgoing.Concat(_confirmedOutgoing)
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList()
            : new List<SectorsVolumes.Sector>();
        var releasing = _revealed ? _stagedReleases : new List<SectorsVolumes.Sector>();
        // The same sectors as last time need no new shapes. This runs after every sync, and the
        // merge is the most expensive thing it does - on the UI thread.
        var shapesKey = string.Join(",", incoming.Select(s => s.Name)) + "|" + string.Join(",", outgoing.Select(s => s.Name))
                        + "|" + string.Join(",", releasing.Select(s => s.Name));
        if (shapesKey != _lastShapesKey)
        {
            _lastShapesKey = shapesKey;

            var incomingPolygons = incoming.SelectMany(Boundaries).ToList();
            var outgoingPolygons = outgoing.SelectMany(Boundaries).ToList();
            var releasingPolygons = releasing.SelectMany(Boundaries).ToList();

            // The border outlines every volume, so the sector boundaries show inside the shading;
            // the fill is their merged outline, so overlapping volumes are shaded once (see
            // SectorFill).
            _incomingFill.SetPolygons(SectorFill.Merge(incomingPolygons));
            _incomingBorder.SetPolygons(incomingPolygons);
            _outgoingFill.SetPolygons(SectorFill.Merge(outgoingPolygons));
            _outgoingBorder.SetPolygons(outgoingPolygons);
            _releasingFill.SetPolygons(SectorFill.Merge(releasingPolygons));
            _releasingBorder.SetPolygons(releasingPolygons);
        }

        var message = incoming.Count == 0 && outgoing.Count == 0 && releasing.Count == 0
            ? $"highlight cleared ({_incoming.Count} incoming, {_stagedOutgoing.Count + _confirmedOutgoing.Count} outgoing, {_stagedReleases.Count} releasing pending, revealed={_revealed})"
            : $"{incoming.Count} incoming, {outgoing.Count} outgoing, {releasing.Count} releasing sector(s) highlighted"
              + (incoming.Count > 0 ? $" - from me: {string.Join(", ", incoming.Select(s => s.Name))}" : "")
              + (outgoing.Count > 0 ? $" - by me: {string.Join(", ", outgoing.Select(s => s.Name))}" : "")
              + (releasing.Count > 0 ? $" - releasing: {string.Join(", ", releasing.Select(s => s.Name))}" : "");

        if (message == _lastLogged)
            return;

        _lastLogged = message;
        ActionLog.Log("Overlay", message);
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

    // One polygon per volume: a sector can be several disjoint volumes, and merging them would
    // produce a shape that does not exist.
    static IEnumerable<List<Coordinate>> Boundaries(SectorsVolumes.Sector sector)
    {
        foreach (var volume in sector.Volumes)
        {
            if (volume.Boundary == null || volume.Boundary.Count < 3)
                continue;

            var points = volume.Boundary.ToList();

            // Closed explicitly: the dataset's boundaries do not reliably repeat their first point,
            // and PaintMap strokes a line as its consecutive segments without closing it, which
            // would leave the border open along one edge.
            if (!points[0].Equals(points[points.Count - 1]))
                points.Add(points[0]);

            yield return points;
        }
    }
}
