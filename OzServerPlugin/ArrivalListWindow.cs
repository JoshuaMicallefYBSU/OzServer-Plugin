using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using vatsys;
using ScrollBar = VATSYSControls.ScrollBar;

namespace OzServerPlugin;

// The Arrival List ("A:YMML List"), remade to show OzServer's list in place of vatSys's own -
// ArrivalListSync opens one of these wherever vatSys would open its SequenceWindow.
//
// It looks and works like vatSys's: one strip per flight with its landing time, callsign, type and
// wake, and global ops; the flights not yet sequenced above the bar, the sequence below it from the
// top down. Dragging a strip moves it, above the bar to take it out of the sequence. Clicking the
// callsign opens the aircraft menu, clicking global ops edits them, as in vatSys.
//
// Everything shown comes from OzServer (routes/arrivals.ts), so every controller sees the same list.
// Above the list, two badges, each half its width: how many controllers have it open (who,
// on hover), whether the airport's FMP is online - while it is, only the FMP may reorder, and the
// strips do not drag for anyone else.
sealed class ArrivalListWindow : BaseForm
{
    const int BadgeHeight = 18;
    const int BadgeGap = 2;

    static readonly Color NeutralBack = Color.FromArgb(214, 214, 214);
    static readonly Color NeutralText = Color.FromArgb(40, 40, 40);
    static readonly Color OnlineBack = Color.FromArgb(0, 100, 0);
    static readonly Color OfflineBack = Color.FromArgb(140, 0, 0);

    readonly ArrivalListSync _sync;
    readonly InsetPanel _inset;
    readonly ArrivalLadder _ladder;
    readonly ScrollBar _scrollBar;
    readonly ArrivalBadge _connectedBadge;
    readonly ArrivalBadge _fmpBadge;
    // One tooltip for the three badges, shown by WinForms itself on hover: it stays up while the
    // mouse is anywhere over a badge, and its text is replaced only when it actually changes.
    readonly ToolTip _tooltip = new() { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 30000, ShowAlways = true };

    public string Airport { get; }

    public ArrivalListWindow(string airport, ArrivalListSync sync)
    {
        Airport = airport;
        _sync = sync;

        // Unique per airport: ShowWithPlacement keeps each list's position under its Name.
        Name = $"OzServerArrivalList{airport}Window";
        Text = $"A:{airport} List";
        KeyPreview = true;
        MiddleClickClose = false;
        HasCloseButton = true;
        HasMaximizeButton = false;
        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
        // vatSys's SequenceWindow, to the pixel, with the badge row added above the list.
        ClientSize = new Size(LockedClientWidth, 250 + BadgeHeight + BadgeGap);
        MinimumSize = new Size(0, 160);

        _connectedBadge = new ArrivalBadge();
        _fmpBadge = new ArrivalBadge();

        var listTop = 2 + BadgeHeight + BadgeGap;
        _inset = new InsetPanel
        {
            Location = new Point(2, listTop),
            Size = new Size(305, 246)
        };
        _ladder = new ArrivalLadder(this)
        {
            Location = new Point(3, 3),
            Size = new Size(297, 240),
            Padding = new Padding(3)
        };
        _inset.Controls.Add(_ladder);

        _scrollBar = new ScrollBar
        {
            ActualHeight = 10,
            Change = 1,
            Location = new Point(310, listTop),
            MinimumSize = new Size(0, 7),
            Orientation = ScrollOrientation.VerticalScroll,
            PreferredHeight = 10,
            Size = new Size(20, 246),
            Value = 0,
            BackColor = Colours.GetColour(Colours.Identities.WindowButtonSelected),
            ForeColor = Colours.GetColour(Colours.Identities.WindowBackground)
        };
        _scrollBar.Scroll += (_, _) => ScrollTo(_scrollBar.Value);
        _scrollBar.Scrolling += (_, _) => ScrollTo(_scrollBar.Value);

        Controls.Add(_connectedBadge);
        Controls.Add(_fmpBadge);
        Controls.Add(_scrollBar);
        Controls.Add(_inset);

        _ladder.WheelScrolled += Wheel;
        _ladder.ContentHeightChanged += (_, _) => ConfigureScrollBar();
        _ladder.Resize += (_, _) => ConfigureScrollBar();

        SetConnected(null);
        SetFmp(null);
        // Locked to vatSys's width - see OnNonClientAreaHitTest. No maximise for the same reason.
        LayoutContent();
        ConfigureScrollBar();
    }

    // Everything placed by hand from the client size rather than left to anchors: under BaseForm
    // the anchored list and scroll bar ran on past the bottom of the window, taking the scroll
    // bar's down arrow with them. The list and scroll bar fill the height below the badges, 4 px
    // short of the bottom as in vatSys's SequenceWindow; each badge is half the list's width.
    void LayoutContent()
    {
        var listTop = 2 + BadgeHeight + BadgeGap;
        var listHeight = Math.Max(0, ClientSize.Height - listTop - 4);
        _inset.Bounds = new Rectangle(2, listTop, LockedClientWidth - 27, listHeight);
        _scrollBar.Bounds = new Rectangle(LockedClientWidth - 22, listTop, 20, listHeight);

        var left = _inset.Left;
        var width = _inset.Width;
        var each = (width - BadgeGap) / 2;
        _connectedBadge.Bounds = new Rectangle(left, 2, each, BadgeHeight);
        _fmpBadge.Bounds = new Rectangle(left + each + BadgeGap, 2, width - each - BadgeGap, BadgeHeight);

        // The list fills the inset, as vatSys's does (3 px in, 297 x 240 in a 305 x 246 inset). Set
        // here rather than left to its anchors, which do not apply until the window has a handle.
        _ladder.Bounds = new Rectangle(3, 3, Math.Max(0, _inset.Width - 8), Math.Max(0, _inset.Height - 6));
    }

    // The list as OzServer last sent it. Null clears it (disconnected).
    public void Apply(OzServerArrivalListDto? list)
    {
        if (IsDisposed)
            return;

        if (list == null)
        {
            SetConnected(null);
            SetFmp(null);
            _ladder.SetFlights(Array.Empty<OzServerArrivalFlightDto>(), canMove: false);
            return;
        }

        SetConnected(list);
        SetFmp(list.Fmp);
        _ladder.SetFlights(list.Flights, list.CanMove);
    }

    // An FDR has changed in vatSys; a flight it has finished leaves the list at once.
    public void RemoveFinished()
    {
        if (!IsDisposed)
            _ladder.RemoveFinished();
    }

    // OzServer did not answer for this list, so it shows nothing it can vouch for, and says why
    // rather than looking like an airport with no traffic.
    public void ShowUnavailable()
    {
        if (IsDisposed)
            return;
        SetConnected(null);
        SetFmp(null);
        _ladder.SetFlights(Array.Empty<OzServerArrivalFlightDto>(), canMove: false);
    }

    void SetConnected(OzServerArrivalListDto? list)
    {
        if (list == null)
            Show(_connectedBadge, "- Connected", NeutralBack, NeutralText, "Not known while OzServer isn't answering");
        else
            Show(_connectedBadge, $"{list.Viewers} Connected", NeutralBack, NeutralText,
                list.ViewerCallsigns.Count > 0 ? string.Join("\n", list.ViewerCallsigns) : "Nobody has this list open");
    }

    void SetFmp(OzServerArrivalFmpDto? fmp)
    {
        if (fmp == null)
            Show(_fmpBadge, "FMP Unknown", NeutralBack, NeutralText, "Not known while OzServer isn't answering");
        else if (fmp.Online)
            Show(_fmpBadge, "FMP Online", OnlineBack, Color.White, $"{fmp.Callsign ?? fmp.Position} is online - only they can reorder this list");
        else
            Show(_fmpBadge, "FMP Offline", OfflineBack, Color.White, $"{fmp.Position} is offline - anyone can reorder this list");
    }

    void Show(ArrivalBadge badge, string text, Color back, Color fore, string tooltip)
    {
        badge.Set(text, back, fore);
        // Replaced only when it differs: setting it again, even to the same text, makes WinForms
        // hide and reshow a tooltip that is up.
        if (_tooltip.GetToolTip(badge) != tooltip)
            _tooltip.SetToolTip(badge, tooltip);
    }

    // vatSys's own Arrival List's width (a 332 px client area) plus about 15%, so a full
    // 15-character global ops field fits in the strip font; only the height can be dragged. vatSys's frame answers the resize edges with the standard hit-test codes, so the side
    // edges become plain border and the corners resize the height only.
    const int LockedClientWidth = 383;
    const int HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14;
    const int HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17, HtBorder = 18;

    protected override int OnNonClientAreaHitTest(Point point)
    {
        var hit = base.OnNonClientAreaHitTest(point);
        return hit switch
        {
            HtLeft or HtRight => HtBorder,
            HtTopLeft or HtTopRight => HtTop,
            HtBottomLeft or HtBottomRight => HtBottom,
            _ => hit
        };
    }

    // Anything else that changes the size (a saved placement, a snap) is put back to the width.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Normal && ClientSize.Width != LockedClientWidth)
            ClientSize = new Size(LockedClientWidth, ClientSize.Height);
        // Null while the constructor is still setting the size, before the controls exist.
        if (_inset != null)
        {
            LayoutContent();
            ConfigureScrollBar();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tooltip.Dispose();
        base.OnFormClosed(e);
    }

    // A strip dropped somewhere new: -1 above the bar, otherwise its place in the sequence.
    internal void RequestMove(string callsign, int seqIndex) => _ = _sync.MoveAsync(Airport, callsign, seqIndex);

    void ConfigureScrollBar()
    {
        _scrollBar.PreferredHeight = _ladder.ContentHeight;
        _scrollBar.ActualHeight = _ladder.ClientRectangle.Height;
        _scrollBar.Change = ArrivalLadder.RowPitch;
        var max = Math.Max(0, _ladder.ContentHeight - _ladder.ClientRectangle.Height);
        if (_ladder.ScrollOffset > max)
            ScrollTo(max);
        _scrollBar.Value = _ladder.ScrollOffset;
        _scrollBar.Invalidate();
    }

    void ScrollTo(int value)
    {
        _ladder.ScrollOffset = Math.Max(0, value);
        _ladder.Invalidate();
        _scrollBar.Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Wheel(e.Delta);
        base.OnMouseWheel(e);
    }

    void Wheel(int delta)
    {
        if (delta == 0)
            return;
        var max = Math.Max(0, _ladder.ContentHeight - _ladder.ClientRectangle.Height);
        var value = Math.Max(0, Math.Min(max, _ladder.ScrollOffset + (delta > 0 ? -ArrivalLadder.RowPitch : ArrivalLadder.RowPitch)));
        _scrollBar.Value = value;
        ScrollTo(value);
    }
}

// The strips. Drawn by hand, as vatSys's SequenceLadderDX is, in the same colours, fonts and sizes.
sealed class ArrivalLadder : Control
{
    // vatSys's SequenceLadderDX.FlightSize is 300 x 26, with a small gap between strips.
    const int RowHeight = 26;
    public const int RowPitch = RowHeight + 3;
    const int DragThreshold = 4;

    readonly ArrivalListWindow _window;
    List<OzServerArrivalFlightDto> _unsequenced = new();
    List<OzServerArrivalFlightDto> _sequenced = new();
    bool _canMove;

    // Pressed on a strip; a drag once the mouse has moved DragThreshold with the button down.
    OzServerArrivalFlightDto? _pressed;
    Point _pressPoint;
    bool _dragging;
    int _dragY;
    TextField? _opsEditor;

    public int ScrollOffset { get; set; }
    public int ContentHeight => (_unsequenced.Count + 1 + _sequenced.Count) * RowPitch + Padding.Vertical;
    public event EventHandler? ContentHeightChanged;

    public ArrivalLadder(ArrivalListWindow window)
    {
        _window = window;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
    }

    // The list as OzServer last sent it, before finished flights are taken out.
    List<OzServerArrivalFlightDto> _all = new();

    public void SetFlights(IEnumerable<OzServerArrivalFlightDto> flights, bool canMove)
    {
        _all = flights.ToList();
        _canMove = canMove;
        Rebuild();
    }

    // Takes out any flight whose FDR this client now holds as FINISHED - at once, rather than
    // waiting for OzServer's next answer to drop it.
    public void RemoveFinished()
    {
        if (_all.Any(f => IsFinished(f.Callsign)))
            Rebuild();
    }

    static bool IsFinished(string callsign) =>
        LocalFdr(callsign)?.State == FDP2.FDR.FDRStates.STATE_FINISHED;

    void Rebuild()
    {
        var list = _all.Where(f => !IsFinished(f.Callsign)).ToList();
        var height = ContentHeight;
        _unsequenced = list.Where(f => f.SeqIndex < 0).ToList();
        // Top of the sequence first, as listed.
        _sequenced = list.Where(f => f.SeqIndex >= 0).OrderByDescending(f => f.SeqIndex).ToList();

        // A strip being dragged that has gone from the list is let go. One still there is followed
        // to its new object: every answer from OzServer replaces them all, and Rows and Drop tell
        // the dragged strip from the rest by reference. Left on the old one, the strip would be
        // counted among the others, and a drag up the sequence would land one place too high.
        if (_pressed != null)
        {
            var pressedCallsign = _pressed.Callsign;
            var current = list.FirstOrDefault(f => SameCallsign(f.Callsign, pressedCallsign));
            if (current == null)
                EndDrag();
            else
                _pressed = current;
        }

        if (ContentHeight != height)
            ContentHeightChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    // Every row top to bottom: the unsequenced flights, the bar (null), the sequence.
    List<OzServerArrivalFlightDto?> Rows(OzServerArrivalFlightDto? leaveOut = null)
    {
        var rows = new List<OzServerArrivalFlightDto?>();
        rows.AddRange(_unsequenced.Where(f => f != leaveOut));
        rows.Add(null);
        rows.AddRange(_sequenced.Where(f => f != leaveOut));
        return rows;
    }

    Rectangle RowRect(int index) =>
        new(Padding.Left, Padding.Top + index * RowPitch - ScrollOffset, ClientSize.Width - Padding.Horizontal, RowHeight);

    // Column positions, from vatSys's PaintFlight: each field as wide as its longest text in its
    // font (vatSys multiplies MMI.FontWidth out; measured here, which needs nothing set up first).
    (Rectangle Time, Rectangle Callsign, Rectangle Type, Rectangle Ops) Columns(Rectangle row)
    {
        _widths ??= (Measure("00:00", MMI.eurofont_winsml), Measure("WWW7777", MMI.eurofont_sml), Measure("WWWW/W", MMI.eurofont_winsml));
        var (timeWidth, callsignWidth, typeWidth) = _widths.Value;
        var time = new Rectangle(row.Left + 4, row.Top, timeWidth, row.Height);
        var callsign = new Rectangle(time.Right + 4, row.Top, callsignWidth, row.Height);
        var type = new Rectangle(callsign.Right + 4, row.Top, typeWidth, row.Height);
        var ops = new Rectangle(type.Right + 5, row.Top, Math.Max(0, row.Right - 2 - (type.Right + 5)), row.Height);
        return (time, callsign, type, ops);
    }

    (int Time, int Callsign, int Type)? _widths;

    static int Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 1;

    // The text if it fits the width; otherwise as many leading characters as fit with an asterisk
    // for the rest, as vatSys's TruncateField marks a cut field.
    static string TruncateToFit(string text, Font font, int width)
    {
        if (Measure(text, font) <= width)
            return text;
        for (var length = text.Length - 1; length > 0; length--)
        {
            var cut = text.Substring(0, length) + "*";
            if (Measure(cut, font) <= width)
                return cut;
        }
        return "*";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var rows = Rows(_dragging ? _pressed : null);
        // While dragging, the strips open a gap where the dragged one would land.
        var gap = _dragging ? DropSlot(rows) : -1;
        for (int i = 0, slot = 0; i < rows.Count; i++, slot++)
        {
            if (slot == gap)
                slot++;
            var rect = RowRect(slot);
            if (rect.Bottom < 0 || rect.Top > ClientSize.Height)
                continue;
            if (rows[i] is { } flight)
                PaintFlight(g, flight, rect, dragged: false);
            else
                PaintBar(g, rect);
        }

        if (_dragging && _pressed != null)
        {
            var rect = RowRect(0);
            rect.Y = _dragY - RowHeight / 2;
            PaintFlight(g, _pressed, rect, dragged: true);
        }
    }

    static void PaintBar(Graphics g, Rectangle rect)
    {
        using var brush = new SolidBrush(Colours.GetColour(Colours.Identities.ListSeparator));
        g.FillRectangle(brush, rect);
    }

    void PaintFlight(Graphics g, OzServerArrivalFlightDto flight, Rectangle rect, bool dragged)
    {
        var fdr = LocalFdr(flight.Callsign);
        var callsignColour = TrackColour(fdr);
        var stripText = Colours.GetColour(Colours.Identities.StripText);

        using (var background = new SolidBrush(Colours.GetColour(Colours.Identities.StripBackground)))
            g.FillRectangle(background, rect);
        using (var border = new Pen(dragged ? callsignColour : stripText, 2))
            g.DrawRectangle(border, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);

        var (time, callsign, type, ops) = Columns(rect);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                      | TextFormatFlags.NoPadding;

        // OzServer's, or this client's own estimate while the record has none yet (pushed by a
        // plugin from before it sent one).
        if ((flight.Eta ?? (fdr != null ? FdrSync.LandingTime(fdr) : null)) is { } eta)
            // Invariant: ":" in a custom format is the culture's time separator, a "." on some.
            TextRenderer.DrawText(g, eta.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), MMI.eurofont_winsml, time, stripText, flags);

        var name = flight.Callsign.Length > 7 ? flight.Callsign.Substring(0, 7) : flight.Callsign;
        TextRenderer.DrawText(g, name, MMI.eurofont_sml, callsign, callsignColour, flags);

        if (!string.IsNullOrEmpty(flight.AircraftType))
        {
            var typeText = flight.AircraftType!.Length > 4 ? flight.AircraftType.Substring(0, 4) : flight.AircraftType;
            TextRenderer.DrawText(g, $"{typeText}/{flight.AircraftWake}", MMI.eurofont_winsml, type, stripText, flags);
        }

        // vatSys's own copy first: vatSys shares global ops across the network as they are typed,
        // while the OzServer record carries them only from a tracking controller on the plugin
        // (FdrSync's ClearanceKnown), so an untracked flight's record has none. The record's stands in
        // where this client has no value, such as after a reconnect.
        var globalOps = !string.IsNullOrEmpty(fdr?.GlobalOpData) ? fdr!.GlobalOpData : flight.GlobalOpData;
        if (!string.IsNullOrEmpty(globalOps) && (_opsEditor == null || !SameCallsign((string)_opsEditor.Tag, flight.Callsign)))
        {
            // All of it, as the flight strip shows it: in the strip font where that fits, otherwise
            // in vatSys's smaller window font, which takes the full 15 characters at this width -
            // spaces included, since a controller may pad a mark out to the end of the field. Only
            // if even that is too wide is it cut with an asterisk, as vatSys's TruncateField does.
            var opsFont = Measure(globalOps!, MMI.eurofont_winsml) <= ops.Width ? MMI.eurofont_winsml : MMI.eurofont_winverysml;
            var opsText = TruncateToFit(globalOps!, opsFont, ops.Width);
            TextRenderer.DrawText(g, opsText, opsFont, ops, stripText, flags);
        }
    }

    // vatSys's SelectBrush: the callsign in its track's jurisdiction colour, the strip text colour
    // without one.
    static Color TrackColour(FDP2.FDR? fdr)
    {
        var track = fdr != null ? MMI.FindTrack(fdr) : null;
        var identity = track?.State switch
        {
            MMI.HMIStates.NonJurisdiction => Colours.Identities.NonJurisdiction,
            MMI.HMIStates.Preactive => Colours.Identities.Preactive,
            MMI.HMIStates.PostJurisdiction => Colours.Identities.PostJurisdiction,
            MMI.HMIStates.Announced => Colours.Identities.Announced,
            MMI.HMIStates.HandoverIn => Colours.Identities.Handover,
            MMI.HMIStates.HandoverOut => Colours.Identities.Handover,
            MMI.HMIStates.Jurisdiction => Colours.Identities.Jurisdiction,
            MMI.HMIStates.GhostJurisdiction => Colours.Identities.GhostJurisdiction,
            _ => Colours.Identities.StripText
        };
        return Colours.GetColour(identity);
    }

    static FDP2.FDR? LocalFdr(string callsign)
    {
        try
        {
            return FDP2.GetFDRs.FirstOrDefault(f => SameCallsign(f.Callsign, callsign));
        }
        catch (InvalidOperationException)
        {
            // FDP2.GetFDRs is vatSys's live collection; changed mid-read, it is only this paint's.
            return null;
        }
    }

    static bool SameCallsign(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // Where the dragged strip would land: the slot it covers, among the rows without it.
    int DropSlot(List<OzServerArrivalFlightDto?> rows)
    {
        var slot = (int)Math.Round((_dragY + ScrollOffset - Padding.Top - RowHeight / 2.0) / RowPitch);
        return Math.Max(0, Math.Min(rows.Count, slot));
    }

    (OzServerArrivalFlightDto? Flight, Rectangle Rect) HitTest(Point point)
    {
        var rows = Rows();
        for (var i = 0; i < rows.Count; i++)
        {
            var rect = RowRect(i);
            if (rect.Contains(point))
                return (rows[i], rect);
        }
        return (null, Rectangle.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        CloseOpsEditor();
        var (flight, _) = HitTest(e.Location);
        _pressed = flight;
        _pressPoint = e.Location;
        _dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed == null || e.Button != MouseButtons.Left)
            return;

        if (!_dragging)
        {
            // Not while the FMP has the list: only they may move strips.
            if (!_canMove || Math.Abs(e.Y - _pressPoint.Y) < DragThreshold)
                return;
            _dragging = true;
            Capture = true;
            Cursor = Cursors.SizeNS;
        }

        _dragY = e.Y;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;

        if (_dragging && pressed != null)
        {
            Drop(pressed);
            EndDrag();
            return;
        }
        EndDrag();

        // A click rather than a drag, on the field it started on.
        var (flight, rect) = HitTest(e.Location);
        if (flight == null || flight != pressed)
            return;
        var (_, callsign, _, ops) = Columns(rect);
        if (callsign.Contains(e.Location))
            OpenAircraftMenu(flight, e.Location);
        else if (ops.Contains(e.Location))
            OpenOpsEditor(flight, ops);
    }

    void EndDrag()
    {
        _pressed = null;
        _dragging = false;
        Capture = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    // Works out the strip's new place, shows it at once, and sends it. OzServer's answer (or the
    // next refresh, if it refuses) settles it.
    void Drop(OzServerArrivalFlightDto flight)
    {
        var rows = Rows(flight);
        var slot = DropSlot(rows);
        var bar = rows.IndexOf(null);
        var sequenced = rows.Skip(bar + 1).ToList();

        int seqIndex;
        if (slot <= bar)
        {
            // Above the bar: out of the sequence. The unsequenced keep their landing-time order, so
            // a strip already there has nowhere else to go.
            if (flight.SeqIndex < 0)
                return;
            seqIndex = -1;
        }
        else
        {
            // Counted up from the bottom of the sequence, as OzServer numbers it.
            var fromTop = slot - bar - 1;
            seqIndex = sequenced.Count - fromTop;
            if (flight.SeqIndex == seqIndex)
                return;
        }

        // Shown straight away, in the place it was dropped.
        _unsequenced.Remove(flight);
        _sequenced.Remove(flight);
        if (seqIndex < 0)
            _unsequenced.Add(flight);
        else
            _sequenced.Insert(Math.Min(sequenced.Count - seqIndex, _sequenced.Count), flight);
        // Renumbered as OzServer will, so a second drag before its answer is worked out right.
        flight.SeqIndex = -1;
        for (var i = 0; i < _sequenced.Count; i++)
            _sequenced[i].SeqIndex = _sequenced.Count - 1 - i;
        Invalidate();

        _window.RequestMove(flight.Callsign, seqIndex);
    }

    void OpenAircraftMenu(OzServerArrivalFlightDto flight, Point location)
    {
        if (LocalFdr(flight.Callsign) is { } fdr && MMI.FindTrack(fdr) is { } track)
            MMI.OpenAircraftMenu(track, PointToScreen(location), false);
    }

    // The FDR's global ops limit, as vatSys's strip editor sets it (StripPanelDX, MaxLength 15).
    // vatSys's own Arrival List allowed 14 and cut the display at 11; this one matches the FDR.
    public const int GlobalOpsLength = 15;

    // Editable by every controller, as in vatSys's own Arrival List: vatSys checks no permission for
    // global ops, only that this is a valid ATC connection (FDP2.SetGlobalOps), and shares the edit
    // across the network itself. Written to vatSys's FDR, the way vatSys's list does it, trimmed
    // and upper-cased.
    void OpenOpsEditor(OzServerArrivalFlightDto flight, Rectangle ops)
    {
        // SetGlobalOps needs vatSys's own FDR. A flight on OzServer's list that this client has no
        // FDR for (out of range, or filed before it connected) cannot be edited from here.
        var fdr = LocalFdr(flight.Callsign);
        if (fdr == null)
            return;

        var editor = new TextField
        {
            Location = new Point(ops.Left, ops.Top + (ops.Height - 18) / 2),
            Width = Math.Max(Measure(new string('W', GlobalOpsLength), MMI.eurofont_winverysml) + 6, ops.Width),
            MaxLength = GlobalOpsLength,
            TakesReturn = true,
            Text = fdr.GlobalOpData ?? "",
            Font = MMI.eurofont_winverysml,
            CharacterCasing = CharacterCasing.Upper,
            Tag = flight.Callsign
        };
        editor.OnReturn += (_, _) =>
        {
            var text = (editor.Text ?? "").Trim().ToUpperInvariant();
            CloseOpsEditor();
            if (!Network.ValidATC)
            {
                // vatSys's SetGlobalOps would drop it without a word.
                OzErrors.Add(new Exception($"Couldn't set {flight.Callsign}'s global ops: not connected as a valid ATC position."), "OzServer");
                return;
            }
            try
            {
                FDP2.SetGlobalOps(fdr, text);
                flight.GlobalOpData = text;
                Invalidate();
            }
            catch (Exception ex)
            {
                OzErrors.Add(new Exception($"Couldn't set {flight.Callsign}'s global ops: {ex.Message}", ex), "OzServer");
            }
        };
        editor.Escape += (_, _) => CloseOpsEditor();
        editor.LostFocus += (_, _) => BeginInvoke(new Action(CloseOpsEditor));

        _opsEditor = editor;
        Controls.Add(editor);
        editor.BringToFront();
        editor.Focus();
        Invalidate();
    }

    void CloseOpsEditor()
    {
        var editor = _opsEditor;
        if (editor == null)
            return;
        _opsEditor = null;
        Controls.Remove(editor);
        editor.Dispose();
        Invalidate();
    }

    // The wheel reaches the control under focus, which is usually this one; the window owns the
    // scroll bar.
    public event Action<int>? WheelScrolled;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta != 0)
            WheelScrolled?.Invoke(e.Delta);
    }
}

// One badge above the list: a flat block of colour with its text centred, in vatSys's window font.
// A real control rather than something painted into the title bar, so the hover is WinForms' own
// tooltip and holds steady anywhere over it.
sealed class ArrivalBadge : Control
{
    public ArrivalBadge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = MMI.eurofont_winsml;
    }

    const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                  | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    // The text in the window font, or a smaller size of it when the badge is too narrow (a third of
    // a narrow list). Rebuilt only when the text or width changes.
    Font? _fitted;
    string? _fittedFor;

    public void Set(string text, Color back, Color fore)
    {
        if (Text == text && BackColor == back && ForeColor == fore)
            return;
        Text = text;
        BackColor = back;
        ForeColor = fore;
        Invalidate();
    }

    Font Fitted()
    {
        var key = $"{Text}|{ClientSize.Width}|{ClientSize.Height}";
        if (_fitted != null && _fittedFor == key)
            return _fitted;

        if (_fitted != null && !ReferenceEquals(_fitted, Font))
            _fitted.Dispose();
        _fitted = Font;
        for (var size = Font.Size; size >= 6f; size -= 0.5f)
        {
            var candidate = ReferenceEquals(_fitted, Font) && size == Font.Size ? Font : new Font(Font.FontFamily, size, Font.Style);
            var measured = TextRenderer.MeasureText(Text, candidate, Size.Empty, Flags);
            if (measured.Width <= ClientSize.Width - 4 && measured.Height <= ClientSize.Height)
            {
                _fitted = candidate;
                break;
            }
            if (!ReferenceEquals(candidate, Font))
                candidate.Dispose();
        }
        _fittedFor = key;
        return _fitted;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Fitted(), ClientRectangle, ForeColor, Flags | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _fitted != null && !ReferenceEquals(_fitted, Font))
            _fitted.Dispose();
        base.Dispose(disposing);
    }
}
