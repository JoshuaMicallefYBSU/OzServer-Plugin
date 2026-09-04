using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;
using ScrollBar = VATSYSControls.ScrollBar;

namespace OzServerPlugin;

// The Owned/Available lists started as a drop-in replica of vatsys.SectorsWindow (the built-in
// "Sector Configuration Window") - see vatsys.SectorsWindow via the decompiled reference project.
// They've since diverged: sectors are grouped under Flow/Centre/Approach/Tower/Other headers (by
// Callsign suffix, matching the real Sectors.xml), and the right-hand list can toggle between
// every sector this controller doesn't already hold ("Available", annotated with who's on one if
// anyone - see ShouldListAsAvailable) and specifically the ones OzServer says someone else actively
// owns ("Controlled"), so a controller can browse to find who to ask. The arrow button, above
// Accept/Reject and spanning
// the same width as the Requested Changes list, claims/releases/requests depending on what's
// selected in Owned/Available; Accept/Reject act on whatever's selected in the Requested Changes
// tree and are disabled until that selection means something. There is no right-click menu - a
// left click on any row with children (a category header, or a primary sector that bundles its
// own sub-sectors) both selects and expands/collapses it.
//
// This window is a view, not the source of truth: _sectorsSelected always mirrors _tracker.Owned
// (an OzServerOwnershipTracker shared across the whole plugin, constructed once in Plugin and
// passed in here), refreshed whenever the tracker's OwnedChanged fires. Claim/release/accept all
// go through the tracker (OzServerApiClient underneath, app/Http/Controllers/
// SectorOwnershipController.php on the backend) rather than this window's own copy of the API
// client - the tracker is also what reacts to MMI.SectorsControlled changing (a login, the
// built-in Sectors window, VSCS/AFV transmit via AfvSectorClaimer, ...) independent of whether this
// window happens to be open, so Owned catches up immediately rather than only on this window's own
// next poll or the next time it's opened. Requested Changes is refreshed from GET /sector-requests
// on open and every 10s after that, since an incoming "Requested From Me" entry is something
// another controller creates server-side with no local signal to react to otherwise.
public class OzServerSectorsWindow : BaseForm
{
    const string NoRequestsFromMe = "No incoming requests";
    const string NoRequestsByMe = "No outgoing requests";
    // Signature prefixes, so a mode switch always counts as a changed tree even when the two modes
    // happen to render the same rows - see ApplyAvailableTree.
    const string AvailableModePrefix = "available|";
    const string ControlledModePrefix = "controlled|";
    const string NothingAvailable = "No sectors available to claim";
    const string NothingControlled = "No sectors controlled by anyone else";
    const string ControlledUnavailable = "Not connected - can't list controlled sectors";
    const string LoadingControlled = "Loading controlled sectors...";
    const string RequestedByMeName = "Requested By Me";
    const string RequestedFromMeName = "Requested From Me";
    // Plain ASCII, not a Unicode arrow glyph - vatSys's own UI font (Terminus, via
    // MMI.eurofont_*, which is what GenericButton defaults to) isn't guaranteed to include one,
    // and the rest of vatsys's own UI (see vatsys.SectorsWindow) sticks to "<<"/">>"/"<<>>" for
    // this exact left/right-move meaning.
    const string ArrowLeft = "<<"; // points at Owned - claim/request an Available/Controlled selection
    const string ArrowRight = ">>"; // points at Available/Controlled - release an Owned selection
    // Shown, disabled, when neither list has a selection - exactly what vatsys.SectorsWindow's own
    // addButton does (see its UpdateChangeButtons: "<<" with Available selected, ">>" with
    // Controlled selected, otherwise "<<>>" and disabled).
    const string ArrowIdle = "<<>>";
    const string CollapsedPrefix = "> ";
    const string ExpandedPrefix = "v ";
    // Same width as CollapsedPrefix/ExpandedPrefix, so a leaf row's own text still lines up under a
    // sibling that does have the arrow, instead of starting two characters further left - see
    // ApplySectorNodeText and LeafText.
    const string BlankPrefix = "  ";

    // Marks a TreeNode as a group header (Approach/Centre/.../Requested By Me/...) rather than a
    // selectable leaf, regardless of which tree it's in or what leaf-Tag type that tree otherwise
    // uses (SectorsVolumes.Sector for Owned/Available, SectorChangeRequest for Requested Changes).
    static readonly object CategoryTag = new();

    enum SectorCategory { Flow, Centre, Approach, Tower, Other }

    enum SectorListMode { Available, Controlled }

    List<SectorsVolumes.Sector> _sectorsSelected = new();
    readonly List<SectorChangeRequest> _requestsFromMe = new();
    readonly List<SectorChangeRequest> _requestsByMe = new();
    // Rejections already shown to this controller, by request id - see ReportRejections.
    readonly HashSet<int> _reportedRejections = new();
    // Drives the Requested From Me heading's flash. A plain on/off toggle on a timer rather than
    // anything cleverer: vatSys flashes by repainting on a timer too (see MenuRenderer's own), and
    // the heading is a single row, so invalidating it is cheap.
    bool _fromMeHasPending;
    bool _flashOn;
    readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 500 };
    SectorListMode _sectorListMode = SectorListMode.Available;
    readonly OzServerApiClient _api = new();
    readonly OzServerOwnershipTracker _tracker;
    readonly System.Windows.Forms.Timer _pollTimer;
    bool _hasOwnedSnapshot;
    // RestoreExpandedAndSelection calls Expand while a Populate* method is rebuilding a tree.
    // TreeView raises AfterExpand for those programmatic restores too, so suppress the scrollbar
    // side effects until the rebuild has put the original state back in full.
    bool _rebuildingTree;
    // Set while an expand/collapse is being applied inside a BeginUpdate block - see the
    // AfterExpandCollapse handlers for why the scrollbar must not be touched until it ends.
    bool _suspendScrollSync;
    // See SetScrollBarValue - stops a code-assigned scrollbar value bouncing back into the tree.
    bool _syncingScrollBar;
    // Which nodes are currently expanded, tracked in managed memory.
    //
    // TreeNode.IsExpanded is not a managed flag: WinForms answers it with a TVM_GETITEM round trip
    // to the native control, per call. CountVisibleNodes and CaptureExpanded both walk the tree
    // asking it of every node, and CountVisibleNodes runs on every single expand (via
    // Configure*Scrollbar) - so opening a category with a hundred sectors under it fired a hundred
    // SendMessages immediately after the expand. That was the pause between clicking and the list
    // opening.
    //
    // Maintained from the AfterExpand/AfterCollapse handlers, which cost one such call each - once
    // per event rather than once per node.
    readonly HashSet<TreeNode> _expandedNodes = new();
    // Last content height each scrollbar was configured for - see the Configure*Scrollbar methods.
    int _currContentHeight = -1;
    int _availContentHeight = -1;
    int _requestedContentHeight = -1;
    bool _allowTreeToggle;
    // Avoid clearing and recreating an unchanged tree. Owned is refreshed every ten seconds even
    // when it has not changed, Available also reacts to every network-controller change, and
    // Requested Changes is polled, so an unconditional rebuild makes the rows visibly twitch.
    string? _ownedTreeSignature;
    string? _availableTreeSignature;
    string? _requestedTreeSignature;
    // Last GET /sectors/controlled result - "OzServer says someone other than me owns this". Held
    // as a snapshot so both lists render from it with no await, and refreshed on the same poll as
    // everything else rather than only when Controlled happens to be showing.
    // Online controllers by callsign, rebuilt once per populate - see RefreshOnlineControllerIndex.
    readonly Dictionary<string, NetworkATC> _onlineByCallsign = new(StringComparer.OrdinalIgnoreCase);
    // Memoizes ShouldListAsAvailable/ResolveDisplayController for the lifetime of one
    // PopulateAvailableList pass - see the Clear() calls there. Keyed by name, not the Sector
    // instance, for the same reason every other Owned/Available comparison in this window is.
    readonly Dictionary<string, bool> _availabilityCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, NetworkATC?> _displayControllerCache = new(StringComparer.OrdinalIgnoreCase);
    // One canonical TreeNode built per sector per populate pass, cloned (TreeNode.Clone() is a
    // plain managed deep copy - Text/Name/Tag/NodeFont/ToolTipText and every descendant node, no
    // native calls) wherever else that same sector needs to appear: its own top-level row and again
    // under every other primary that covers it (Available - see PopulateAvailableList), or under
    // more than one owned primary at once (Owned, when two held primaries' groupings overlap).
    // Rebuilding the whole recursive subtree from scratch for every placement - re-walking
    // SectorGroupings, re-formatting text, re-running IsCoveredByOwned/FindController - was the
    // actual cost behind a category with several such primaries feeling slow to redraw on expand.
    // Cleared at the top of the matching Populate* method; see BuildOwnedSectorNode/
    // BuildAvailableSectorNode.
    readonly Dictionary<string, TreeNode> _ownedNodeCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TreeNode?> _availableNodeCache = new(StringComparer.OrdinalIgnoreCase);
    // Names from _controlledSnapshot as a set, so the Available filter is a hash lookup per sector
    // rather than a linear scan of the whole response per sector.
    readonly HashSet<string> _controlledNames = new(StringComparer.OrdinalIgnoreCase);
    List<OzServerControlledSectorDto> _controlledSnapshot = new();
    string? _controlledSignature;
    bool _hasControlledSnapshot;
    bool _requestActionRunning;
    // True from the first staged move until Apply or Cancel resolves it. While set, the Owned list
    // is the controller's working selection rather than a view of _tracker.Owned, so background
    // refreshes must not overwrite it - see SyncOwnedFromTracker.
    // Exactly the sectors the controller has moved and not yet committed, by name.
    //
    // Deliberately an explicit set rather than "staged list differs from _tracker.Owned": those two
    // can already differ for reasons the controller had nothing to do with - the list is seeded from
    // MMI.SectorsControlled before the tracker's first response, and MMI and OzServer can hold
    // different views of a sector at any time. Deriving the highlight from that comparison painted
    // every one of those rows yellow the instant anything was staged, instead of only the row that
    // was actually moved.
    readonly HashSet<string> _stagedNames = new(StringComparer.OrdinalIgnoreCase);
    // Sectors staged to be *requested* rather than claimed - ones another controller currently owns.
    // They are not added to _sectorsSelected, because staging one does not make it this controller's
    // even provisionally: it shows under Requested By Me until Apply actually sends the request.
    readonly List<SectorsVolumes.Sector> _stagedRequests = new();
    // Shows the aircraft a staged request would bring with it. The window owns the staged set, so
    // it is the only place that can say when it changes.
    readonly PendingSectorGhosts _ghosts;
    bool _applyRunning;

    readonly TableLayoutPanel _tableLayoutPanel1;
    readonly TextLabel _currentSectorsLabel;
    readonly TextLabel _requestedChangesLabel;
    readonly GenericButton _applyButton;
    readonly GenericButton _cancelButton;
    readonly GenericButton _arrowButton;
    readonly ToggleGenericButton _availableModeButton;
    readonly ToggleGenericButton _controlledModeButton;
    // Act on whatever's selected in _requestedChangesView - see UpdateRequestActionButtons for the
    // enable rules (the same two rules the old right-click menu used to gate its own Accept/Reject
    // on).
    readonly GenericButton _acceptButton;
    readonly GenericButton _rejectButton;
    readonly FlowLayoutPanel _requestActionsPanel;
    readonly FlowLayoutPanel _sectorListModePanel;
    readonly FlowLayoutPanel _currSectorsFlowPanel;
    readonly FlowLayoutPanel _addRemoveLayoutPanel;
    readonly FlowLayoutPanel _requestedListRow;
    readonly InsetPanel _currInsetPanel;
    readonly InsetPanel _availInsetPanel;
    readonly InsetPanel _requestedInsetPanel;
    readonly ScrollBar _currScrollBar;
    readonly ScrollBar _availScrollBar;
    readonly ScrollBar _requestedScrollBar;
    readonly TreeViewEx _availSectorsView;
    readonly TreeViewEx _currSectorsView;
    readonly TreeViewEx _requestedChangesView;
    readonly FlowLayoutPanel _requestedChangesPanel;

    public OzServerSectorsWindow(OzServerOwnershipTracker tracker, PendingSectorGhosts ghosts)
    {
        _ghosts = ghosts;
        _tracker = tracker;
        Text = "OzServer - Sector Configuration Window";
        Name = nameof(OzServerSectorsWindow);
        KeyPreview = true;
        MiddleClickClose = false;
        HasCloseButton = true;
        HideOnClose = true;
        Resizeable = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(930, 466);
        BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

        _availSectorsView = new TreeViewEx
        {
            BorderStyle = BorderStyle.None,
            DrawMode = TreeViewDrawMode.OwnerDrawText,
            HideSelection = false,
            Location = new Point(2, 2),
            Name = "availSectorsView",
            ShowLines = false,
            ShowNodeToolTips = true,
            ShowPlusMinus = false,
            ShowRootLines = false,
            Size = new Size(265, 324),
            TabIndex = 4,
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText)
        };
        _availSectorsView.DrawNode += SectorsView_DrawNode;
        _availSectorsView.BeforeSelect += TreeView_BeforeSelect;
        _availSectorsView.NodeMouseClick += TreeView_NodeMouseClick;
        _availSectorsView.MouseWheel += AvailSectorsView_MouseWheel;
        _availSectorsView.AfterSelect += AvailSectorsView_AfterSelect;
        _availSectorsView.BeforeCollapse += TreeView_BeforeMouseExpandCollapse;
        _availSectorsView.BeforeExpand += TreeView_BeforeMouseExpandCollapse;
        _availSectorsView.AfterCollapse += AvailSectorsView_AfterExpandCollapse;
        _availSectorsView.AfterExpand += AvailSectorsView_AfterExpandCollapse;

        _currSectorsView = new TreeViewEx
        {
            BorderStyle = BorderStyle.None,
            DrawMode = TreeViewDrawMode.OwnerDrawText,
            HideSelection = false,
            Location = new Point(2, 2),
            Name = "currSectorsView",
            ShowLines = false,
            ShowNodeToolTips = true,
            ShowPlusMinus = false,
            ShowRootLines = false,
            Size = new Size(265, 324),
            TabIndex = 5,
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText)
        };
        _currSectorsView.DrawNode += SectorsView_DrawNode;
        _currSectorsView.BeforeSelect += TreeView_BeforeSelect;
        _currSectorsView.NodeMouseClick += TreeView_NodeMouseClick;
        _currSectorsView.MouseWheel += CurrSectorsView_MouseWheel;
        _currSectorsView.AfterSelect += CurrSectorsView_AfterSelect;
        _currSectorsView.BeforeCollapse += TreeView_BeforeMouseExpandCollapse;
        _currSectorsView.BeforeExpand += TreeView_BeforeMouseExpandCollapse;
        _currSectorsView.AfterCollapse += CurrSectorsView_AfterExpandCollapse;
        _currSectorsView.AfterExpand += CurrSectorsView_AfterExpandCollapse;

        _requestedChangesView = new TreeViewEx
        {
            BorderStyle = BorderStyle.None,
            // No CheckBoxes. Accepting is driven purely by what's selected (see
            // GetRequestsToAccept): a request row accepts that request, and the "Requested From Me"
            // header accepts every incoming request at once - which is all the checkboxes were ever
            // for. They also could not be limited to the rows they applied to: TreeView's CheckBoxes
            // is all-or-nothing, so category headers and outgoing "Requested By Me" rows grew a
            // checkbox that did nothing at all when ticked.
            DrawMode = TreeViewDrawMode.OwnerDrawText,
            HideSelection = false,
            Location = new Point(2, 2),
            Name = "requestedChangesView",
            ShowLines = false,
            ShowNodeToolTips = true,
            ShowPlusMinus = false,
            ShowRootLines = false,
            Size = new Size(265, 255),
            TabIndex = 21,
            BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
            ForeColor = Colours.GetColour(Colours.Identities.InteractiveText)
        };
        _requestedChangesView.DrawNode += SectorsView_DrawNode;
        _requestedChangesView.BeforeSelect += TreeView_BeforeSelect;
        _requestedChangesView.NodeMouseClick += TreeView_NodeMouseClick;
        _requestedChangesView.MouseWheel += RequestedChangesView_MouseWheel;
        _requestedChangesView.AfterSelect += RequestedChangesView_AfterSelect;
        _requestedChangesView.BeforeCollapse += TreeView_BeforeMouseExpandCollapse;
        _requestedChangesView.BeforeExpand += TreeView_BeforeMouseExpandCollapse;
        _requestedChangesView.AfterCollapse += RequestedChangesView_AfterExpandCollapse;
        _requestedChangesView.AfterExpand += RequestedChangesView_AfterExpandCollapse;

        _availInsetPanel = new InsetPanel
        {
            Location = new Point(3, 3),
            Margin = new Padding(3, 3, 1, 3),
            Name = "availInsetPanel",
            Size = new Size(270, 331),
            TabIndex = 0
        };
        _availInsetPanel.Controls.Add(_availSectorsView);

        _currInsetPanel = new InsetPanel
        {
            Location = new Point(3, 3),
            Margin = new Padding(3, 3, 1, 3),
            Name = "currInsetPanel",
            Size = new Size(270, 331),
            TabIndex = 0
        };
        _currInsetPanel.Controls.Add(_currSectorsView);

        _requestedInsetPanel = new InsetPanel
        {
            Location = new Point(3, 3),
            Margin = new Padding(3, 3, 1, 3),
            Name = "requestedInsetPanel",
            Size = new Size(270, 262),
            TabIndex = 0
        };
        _requestedInsetPanel.Controls.Add(_requestedChangesView);

        _availScrollBar = new ScrollBar
        {
            ActualHeight = 10,
            Change = 1,
            Location = new Point(275, 3),
            Margin = new Padding(1, 3, 3, 1),
            MinimumSize = new Size(0, 5),
            Name = "availScrollBar",
            Orientation = ScrollOrientation.VerticalScroll,
            PreferredHeight = 10,
            Size = new Size(20, 331),
            TabIndex = 1,
            Text = "availScrollBar",
            Value = 0,
            BackColor = Colours.GetColour(Colours.Identities.WindowButtonSelected),
            ForeColor = Colours.GetColour(Colours.Identities.WindowBackground)
        };
        _availScrollBar.Scroll += AvailScrollBar_Scroll;
        _availScrollBar.Scrolling += AvailScrollBar_Scroll;
        _availScrollBar.MouseWheel += AvailSectorsView_MouseWheel;

        _currScrollBar = new ScrollBar
        {
            ActualHeight = 10,
            Change = 1,
            Location = new Point(275, 3),
            Margin = new Padding(1, 3, 3, 1),
            MinimumSize = new Size(0, 5),
            Name = "currScrollBar",
            Orientation = ScrollOrientation.VerticalScroll,
            PreferredHeight = 10,
            Size = new Size(20, 331),
            TabIndex = 1,
            Text = "currScrollBar",
            Value = 0,
            BackColor = Colours.GetColour(Colours.Identities.WindowButtonSelected),
            ForeColor = Colours.GetColour(Colours.Identities.WindowBackground)
        };
        _currScrollBar.Scroll += CurrScrollBar_Scroll;
        _currScrollBar.Scrolling += CurrScrollBar_Scroll;
        _currScrollBar.MouseWheel += CurrSectorsView_MouseWheel;

        _requestedScrollBar = new ScrollBar
        {
            ActualHeight = 10,
            Change = 1,
            Location = new Point(275, 3),
            Margin = new Padding(1, 3, 3, 1),
            MinimumSize = new Size(0, 5),
            Name = "requestedScrollBar",
            Orientation = ScrollOrientation.VerticalScroll,
            PreferredHeight = 10,
            Size = new Size(20, 262),
            TabIndex = 1,
            Text = "requestedScrollBar",
            Value = 0,
            BackColor = Colours.GetColour(Colours.Identities.WindowButtonSelected),
            ForeColor = Colours.GetColour(Colours.Identities.WindowBackground)
        };
        _requestedScrollBar.Scroll += RequestedScrollBar_Scroll;
        _requestedScrollBar.Scrolling += RequestedScrollBar_Scroll;
        _requestedScrollBar.MouseWheel += RequestedChangesView_MouseWheel;

        _addRemoveLayoutPanel = new FlowLayoutPanel
        {
            Location = new Point(628, 20),
            Name = "addRemoveLayoutPanel",
            Size = new Size(298, 337),
            TabIndex = 15
        };
        _addRemoveLayoutPanel.Controls.Add(_availInsetPanel);
        _addRemoveLayoutPanel.Controls.Add(_availScrollBar);

        _currSectorsFlowPanel = new FlowLayoutPanel
        {
            Location = new Point(3, 20),
            Name = "currSectorsFlowPanel",
            Size = new Size(298, 337),
            TabIndex = 12
        };
        _currSectorsFlowPanel.Controls.Add(_currInsetPanel);
        _currSectorsFlowPanel.Controls.Add(_currScrollBar);

        _requestedListRow = new FlowLayoutPanel
        {
            Margin = new Padding(0),
            Name = "requestedListRow",
            Size = new Size(298, 268),
            TabIndex = 20
        };
        _requestedListRow.Controls.Add(_requestedInsetPanel);
        _requestedListRow.Controls.Add(_requestedScrollBar);

        _arrowButton = new GenericButton
        {
            Anchor = AnchorStyles.None,
            Enabled = false,
            Margin = new Padding(0, 8, 0, 3),
            Name = "arrowButton",
            // As wide as the Requested Changes list above it (298, see _requestedListRow) rather
            // than vatsys.SectorsWindow's own 90x28 addButton - sitting between that list and
            // Accept/Reject below, it reads as a divider spanning the column, not a small button
            // floating in the middle of it.
            Size = new Size(298, 28),
            TabIndex = 16,
            Text = ArrowIdle,
        };
        _arrowButton.Click += ArrowButton_Click;

        _acceptButton = CreateRequestActionButton("Accept");
        _acceptButton.Size = new Size(145, 30);
        _acceptButton.TabIndex = 23;
        _acceptButton.Click += (_, _) => _ = AcceptSelectedRequestsAsync(_requestedChangesView.SelectedNode);

        _rejectButton = CreateRequestActionButton("Reject");
        _rejectButton.Size = new Size(145, 30);
        _rejectButton.TabIndex = 24;
        _rejectButton.Click += (_, _) => _ = RejectSelectedRequestAsync(_requestedChangesView.SelectedNode);

        _requestActionsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 0),
            Name = "requestActionsPanel",
            TabIndex = 25
        };
        _requestActionsPanel.Controls.Add(_acceptButton);
        _requestActionsPanel.Controls.Add(_rejectButton);

        _requestedChangesPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Location = new Point(310, 20),
            Margin = new Padding(0),
            Name = "requestedChangesPanel",
            TabIndex = 22
        };
        _requestedChangesPanel.Controls.Add(_requestedListRow);
        _requestedChangesPanel.Controls.Add(_arrowButton);
        _requestedChangesPanel.Controls.Add(_requestActionsPanel);

        _currentSectorsLabel = new TextLabel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            ForeColor = Colours.GetColour(Colours.Identities.GenericText),
            HasBorder = false,
            InteractiveText = false,
            Location = new Point(76, 0),
            Name = "currentSectorsLabel",
            Size = new Size(80, 17),
            TabIndex = 1,
            Text = "Owned",
            TextAlign = ContentAlignment.MiddleCenter
        };

        _requestedChangesLabel = new TextLabel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            ForeColor = Colours.GetColour(Colours.Identities.GenericText),
            HasBorder = false,
            InteractiveText = false,
            Location = new Point(325, 0),
            Name = "requestedChangesLabel",
            Size = new Size(184, 17),
            TabIndex = 19,
            Text = "Requested Changes",
            TextAlign = ContentAlignment.MiddleCenter
        };

        _availableModeButton = new ToggleGenericButton
        {
            Margin = new Padding(2),
            Name = "availableModeButton",
            Pressed = true,
            Size = new Size(120, 30),
            TabIndex = 17,
            Text = "Available",
        };
        _availableModeButton.Click += (_, _) => SetSectorListMode(SectorListMode.Available);

        _controlledModeButton = new ToggleGenericButton
        {
            Margin = new Padding(2),
            Name = "controlledModeButton",
            Pressed = false,
            Size = new Size(120, 30),
            TabIndex = 18,
            Text = "Controlled",
        };
        _controlledModeButton.Click += (_, _) => SetSectorListMode(SectorListMode.Controlled);

        _sectorListModePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Location = new Point(628, 0),
            Margin = new Padding(0),
            Name = "sectorListModePanel"
        };
        _sectorListModePanel.Controls.Add(_availableModeButton);
        _sectorListModePanel.Controls.Add(_controlledModeButton);

        _tableLayoutPanel1 = new TableLayoutPanel
        {
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 2,
            Location = new Point(6, 3),
            Name = "tableLayoutPanel1",
            Size = new Size(920, 420),
            TabIndex = 1
        };
        _tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle());
        _tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle());
        _tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle());
        _tableLayoutPanel1.RowStyles.Add(new RowStyle());
        _tableLayoutPanel1.RowStyles.Add(new RowStyle());
        _tableLayoutPanel1.Controls.Add(_currentSectorsLabel, 0, 0);
        _tableLayoutPanel1.Controls.Add(_currSectorsFlowPanel, 0, 1);
        _tableLayoutPanel1.Controls.Add(_requestedChangesLabel, 1, 0);
        _tableLayoutPanel1.Controls.Add(_requestedChangesPanel, 1, 1);
        _tableLayoutPanel1.Controls.Add(_sectorListModePanel, 2, 0);
        _tableLayoutPanel1.Controls.Add(_addRemoveLayoutPanel, 2, 1);

        _applyButton = new GenericButton
        {
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(754, 430),
            Name = "applyButton",
            Size = new Size(80, 30),
            TabIndex = 2,
            Text = "Apply",
        };
        _applyButton.Click += ApplyButton_Click;

        _cancelButton = new GenericButton
        {
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(840, 430),
            Name = "cancelButton",
            Size = new Size(80, 30),
            TabIndex = 3,
            Text = "Cancel",
        };
        _cancelButton.Click += CancelButton_Click;

        Controls.Add(_cancelButton);
        Controls.Add(_applyButton);
        Controls.Add(_tableLayoutPanel1);

        // The tracker (not MMI.SectorsControlledChanged directly - see the class comment) is what
        // Owned actually follows, so it stays in sync with a change made through the tracker while
        // this window isn't even open, not just changes made while it's visible. Marshalled through
        // RunOnUiThread since OwnedChanged can fire off the UI thread, and can fire before this
        // window has a handle to post to at all - see that method.
        _tracker.OwnedChanged += (_, _) => RunOnUiThread(SyncOwnedFromTracker);
        // Requests now arrive with the same sync that carries ownership, so this window no longer
        // polls for them separately - see RefreshRequestedChangesAsync.
        _tracker.RequestsChanged += (_, requests) => RunOnUiThread(() => ApplyRequests(requests));
        // ControlledByOthers changes whenever anyone else's ownership moves, including APP
        // sectors being taken out from under an ENR grouping. Rebuild from completed syncs only;
        // reading immediately after starting an async refresh just reuses the previous snapshot.
        _tracker.Refreshed += (_, _) => RunOnUiThread(RefreshControlledSnapshot);
        Network.OnlineATCChanged += (_, _) => RefreshAvailableList();

        // Requested Changes has no local signal for an incoming request another controller just
        // created server-side, and Controlled mode is now server-owned data too (see
        // GetControlledSectorsAsync) with the same problem - so both are polled, only while this
        // window is actually visible (see OnVisibleChanged), not for the plugin's whole lifetime.
        // Owned itself doesn't need polling any more - the tracker keeps it current on its own,
        // independent of this window - but a nudge here still catches up faster than waiting on
        // whatever last triggered the tracker's own refresh.
        // 2s, not 10s. This timer only runs while the window is actually visible (see
        // OnVisibleChanged), and it is the only thing that surfaces another controller's actions -
        // a sector being claimed, released, requested or handed over. At ten seconds those took
        // long enough to appear that the lists looked wrong rather than merely behind. Each tick is
        // three GETs that all no-op cheaply when nothing changed: the tracker's refresh drops out if
        // one is already running, and both the requests and controlled snapshots are compared before
        // anything is rebuilt.
        _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _pollTimer.Tick += (_, _) =>
        {
            // One sync per tick, and only if one is not already in flight. It carries owned,
            // controlled and requests together, so asking separately for any of them here would be
            // the same round trip twice.
            _ = _tracker.RefreshFromServerIfIdleAsync();
        };

        _flashTimer.Tick += (_, _) =>
        {
            if (!_fromMeHasPending)
            {
                if (!_flashOn)
                    return;

                _flashOn = false;
                _requestedChangesView.Invalidate();
                return;
            }

            _flashOn = !_flashOn;
            _requestedChangesView.Invalidate();
        };
        _flashTimer.Start();

        ConfigureCurrScrollbar();
        ConfigureAvailScrollbar();
        ConfigureRequestedScrollbar();
        SyncOwnedFromTracker();
        PopulateRequestedChanges();

        _ = _tracker.RefreshFromServerIfIdleAsync();
        RefreshControlledSnapshot();
    }

    // Looking at the window is what acknowledges the flash - the same way vatSys stops flashing its
    // own windows once they have focus. Set from Plugin when an incoming request arrives.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        FlashTitleBar = false;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (Visible)
        {
            FlashTitleBar = false;
            _pollTimer.Start();
            // Every time the window is opened, not just the first time this session - a nudge for
            // OzServer's own record in case a while has passed since the tracker last refreshed.
            _ = _tracker.RefreshFromServerIfIdleAsync();
            _ = RefreshRequestedChangesAsync();
            RefreshControlledSnapshot();
        }
        else
        {
            _pollTimer.Stop();

            // Closing is a cancel. The window hides rather than closes (HideOnClose), so this is
            // the only place the X button can be caught - there is no Closed event to hook - and it
            // covers every other route to hidden as well.
            DiscardStagedChanges();
        }
    }

    // Every background signal this window reacts to - the tracker's OwnedChanged, Network's
    // OnlineATCChanged - can arrive off the UI thread, and can arrive before this window has ever
    // been shown, i.e. before its handle exists. BeginInvoke on a handleless control throws, and
    // because the tracker raises OwnedChanged outside its own try/catch that exception used to
    // escape RefreshFromServerAsync entirely rather than being reported anywhere. Nothing is lost
    // by skipping: OnVisibleChanged refreshes again on show, and the constructor populates
    // directly.
    void RunOnUiThread(MethodInvoker action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch (ObjectDisposedException)
        {
            // Window went away between the check above and the post - nothing left to update.
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed in that same gap - same story.
        }
    }

    // Owned always mirrors _tracker.Owned - see the class comment for why this window never
    // maintains its own copy of ownership state.
    void SyncOwnedFromTracker()
    {
        // The whole point of staging: while the controller has an uncommitted selection, the Owned
        // list is theirs, not a view of the server. A poll tick, an OwnedChanged from someone
        // accepting a request, or the tracker's own reconcile would otherwise land mid-edit and
        // silently throw away everything they had picked - which is what made this list look like it
        // was refreshing at random. Available still tracks the staged list, so it stays consistent.
        if (HasStagedEdits)
        {
            UpdateApplyCancelButtons();
            return;
        }

        // Before the tracker's first response, Owned is empty because nothing has been *asked* yet,
        // not because nothing is owned (see OzServerOwnershipTracker.HasBaseline). Rendering that
        // empty list put every sector the controller was actually holding into Available for the
        // length of one round trip on first open - the sector visibly "jumped" out of Owned and
        // back. vatSys's own SectorsWindow.LoadSectors seeds from MMI.SectorsControlled, which is
        // already correct locally at that point (a login, a VSCS transmit and the built-in window
        // all write it), so seed from the same place and let the first real refresh take over.
        var owned = _tracker.HasBaseline
            ? _tracker.Owned.ToList()
            : MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();

        var changed = !_hasOwnedSnapshot
                      || owned.Count != _sectorsSelected.Count
                      || owned.Any(sector => !_sectorsSelected.Any(existing => existing.Equals(sector)));

        _hasOwnedSnapshot = true;
        _sectorsSelected = owned;
        if (changed)
            PopulateLists();

        UpdateApplyCancelButtons();
    }

    void CurrSectorsView_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta > 0)
            _currScrollBar.Value -= _currSectorsView.ItemHeight;
        else if (e.Delta < 0)
            _currScrollBar.Value += _currSectorsView.ItemHeight;
    }

    void CurrSectorsView_AfterExpandCollapse(object? sender, TreeViewEventArgs e)
    {
        // One IsExpanded call per event keeps the managed set current, so the walks that run per
        // node never have to ask the native control at all - see _expandedNodes.
        if (e.Node.IsExpanded)
            _expandedNodes.Add(e.Node);
        else
            _expandedNodes.Remove(e.Node);

        RefreshDropdownNodeText(e.Node);

        // _suspendScrollSync as well as _rebuildingTree: this fires from inside
        // ToggleNodeExpansion's BeginUpdate/EndUpdate block, where the control has not relaid out
        // yet, so GetPreferredHeight and GetScrollPos both still describe the tree as it was
        // *before* the expand. Reconfiguring the bar from that is what made expanding a category
        // jump. ToggleNodeExpansion does one sync afterwards instead, once layout has settled.
        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureCurrScrollbar();
    }

    void AvailSectorsView_AfterExpandCollapse(object? sender, TreeViewEventArgs e)
    {
        // One IsExpanded call per event keeps the managed set current, so the walks that run per
        // node never have to ask the native control at all - see _expandedNodes.
        if (e.Node.IsExpanded)
            _expandedNodes.Add(e.Node);
        else
            _expandedNodes.Remove(e.Node);

        RefreshDropdownNodeText(e.Node);

        // _suspendScrollSync as well as _rebuildingTree: this fires from inside
        // ToggleNodeExpansion's BeginUpdate/EndUpdate block, where the control has not relaid out
        // yet, so GetPreferredHeight and GetScrollPos both still describe the tree as it was
        // *before* the expand. Reconfiguring the bar from that is what made expanding a category
        // jump. ToggleNodeExpansion does one sync afterwards instead, once layout has settled.
        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureAvailScrollbar();
    }

    void AvailSectorsView_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta > 0)
            _availScrollBar.Value -= _availSectorsView.ItemHeight;
        else if (e.Delta < 0)
            _availScrollBar.Value += _availSectorsView.ItemHeight;
    }

    void RequestedChangesView_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta > 0)
            _requestedScrollBar.Value -= _requestedChangesView.ItemHeight;
        else if (e.Delta < 0)
            _requestedScrollBar.Value += _requestedChangesView.ItemHeight;
    }

    void RequestedChangesView_AfterExpandCollapse(object? sender, TreeViewEventArgs e)
    {
        // One IsExpanded call per event keeps the managed set current, so the walks that run per
        // node never have to ask the native control at all - see _expandedNodes.
        if (e.Node.IsExpanded)
            _expandedNodes.Add(e.Node);
        else
            _expandedNodes.Remove(e.Node);

        RefreshDropdownNodeText(e.Node);

        // _suspendScrollSync as well as _rebuildingTree: this fires from inside
        // ToggleNodeExpansion's BeginUpdate/EndUpdate block, where the control has not relaid out
        // yet, so GetPreferredHeight and GetScrollPos both still describe the tree as it was
        // *before* the expand. Reconfiguring the bar from that is what made expanding a category
        // jump. ToggleNodeExpansion does one sync afterwards instead, once layout has settled.
        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureRequestedScrollbar();
    }

    // TreeView's native left-button double-click toggles a node even with ShowPlusMinus=false.
    // TreeViewCancelEventArgs does not distinguish that native toggle from a direct
    // TreeNode.Expand/Collapse call, so explicitly allow only ToggleNodeExpansion's own call and
    // the programmatic expansion-state restore performed during a rebuild.
    void TreeView_BeforeMouseExpandCollapse(object? sender, TreeViewCancelEventArgs e)
    {
        if (!_allowTreeToggle && !_rebuildingTree)
            e.Cancel = true;
    }

    void TreeView_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        var treeView = (TreeViewEx)sender!;

        // Non-selectable rows are filtered by TreeView_BeforeSelect; assigning here anyway would
        // simply be cancelled, so it is only done for rows that can actually take it.
        treeView.SelectedNode = e.Node;

        // Any row with children - a category header (Approach/Centre/Tower/..., Requested By/From
        // Me), or a primary sector that bundles its own sub-sectors (see ApplySectorNodeText) -
        // toggles open/closed on the same click that selects it. There is no right-click menu to
        // fall back on for expanding a primary any more, so the one click has to do both. A heading
        // with no Name is one of the Requested ones - not collapsible, so a left click on it only
        // selects (which is still meaningful: selecting "Requested From Me" is the
        // accept-everything-incoming gesture).
        if (!string.IsNullOrEmpty(e.Node.Name))
            ToggleNodeExpansion(treeView, e.Node);
    }

    static bool IsCategoryNode(TreeNode node) => ReferenceEquals(node.Tag, CategoryTag);

    // Only rows that stand for something - a sector, or a request - can be highlighted. The group
    // headings (Flow/Centre/Approach/..., Requested By/From Me) and the informational placeholder
    // rows are labels, and highlighting a label suggests it can be acted on when it cannot.
    //
    // Cancelling here rather than only in the click handler covers every route into a selection:
    // keyboard navigation, and the selection restore that runs after a rebuild.
    static void TreeView_BeforeSelect(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node?.Tag is not SectorsVolumes.Sector && e.Node?.Tag is not SectorChangeRequest)
            e.Cancel = true;
    }

    // The Requested From Me heading, on the lit half of its flash cycle, while requests are waiting.
    bool IsFlashingHeading(TreeNode node) =>
        _fromMeHasPending && _flashOn && IsCategoryNode(node) && node.Text == RequestedFromMeName;

    // Removes the row for one sector, and the group heading with it if that leaves it empty - which
    // is what a rebuild would have produced. Only ever touches top-level group children: a sector
    // nested under a grouping sector is shown as part of that group's own subtree, and pulling it
    // out from under its parent would misrepresent what the parent covers.
    // Returns false when the row is not a plain top-level group child - a sector nested inside a
    // grouping sector's subtree, say. Those cannot simply be pulled out: the subtree shows what the
    // parent covers, so removing one row from it would misrepresent the parent. The caller falls
    // back to a full rebuild, which works the nesting out properly.
    //
    // Reporting this rather than silently doing nothing matters: the first version returned quietly
    // when it found no match, which left the sector showing in Available *and* Owned at once - so a
    // move looked like it had not happened.
    static bool RemoveSectorRow(TreeViewEx view, string sectorName, Action<TreeNode> forget)
    {
        foreach (TreeNode group in view.Nodes)
        {
            for (var i = group.Nodes.Count - 1; i >= 0; i--)
            {
                var candidate = group.Nodes[i];

                if (candidate.Tag is not SectorsVolumes.Sector sector || sector.Name != sectorName)
                    continue;

                // Has its own children, so it is a grouping sector whose subtree other rows sit in.
                // Rebuilding is the only way to work out what should remain.
                if (candidate.Nodes.Count > 0)
                    return false;

                forget(candidate);
                group.Nodes.RemoveAt(i);

                if (group.Nodes.Count == 0)
                {
                    forget(group);
                    group.Remove();
                }

                return true;
            }
        }

        return false;
    }

    // Whether this row is part of an uncommitted change - a sector staged into Owned that OzServer
    // does not yet record as this controller's, or one staged out that it still does. Both
    // directions matter: the first shows in Owned, the second reappears in Available, and neither is
    // true until Apply.
    //
    // Derived rather than stored, so it can never disagree with what Apply will actually send: the
    // commit computes its claim/release lists from exactly this comparison.
    bool IsStagedNode(TreeNode node)
    {
        return node.Tag is SectorsVolumes.Sector sector
               && !sector.IsDummy
               && _stagedNames.Contains(sector.Name);
    }

    // A sector of this controller's that somebody has asked for. Marked in its own colour so opening
    // the window answers "which of mine do they want" at a glance, rather than leaving the controller
    // to read the request pane and cross-reference it against Owned by hand.
    //
    // vatSys gives plugins no way to draw on the ASD - IPlugin/ILabelPlugin/IStripPlugin cover
    // labels, strips and track colours, and nothing else - so a sector cannot be outlined on the map
    // itself. This is the closest thing to a visible highlight that is actually available.
    // Whether this row, or anything nested under it, has a request waiting on it. Used to hold the
    // arrow back until the request is answered - see UpdateArrowButton for why descendants count.
    bool HasOutstandingRequest(TreeNode node)
    {
        if (IsRequestedFromMeNode(node))
            return true;

        foreach (TreeNode child in node.Nodes)
        {
            if (HasOutstandingRequest(child))
                return true;
        }

        return false;
    }

    bool IsRequestedFromMeNode(TreeNode node)
    {
        if (node.Tag is not SectorsVolumes.Sector sector || sector.IsDummy)
            return false;

        lock (_requestsFromMe)
        {
            foreach (var request in _requestsFromMe)
            {
                if (request.Sector.Equals(sector))
                    return true;
            }
        }

        return false;
    }

    void ToggleNodeExpansion(TreeViewEx treeView, TreeNode node)
    {
        if (node.Nodes.Count == 0)
            return;

        // TreeView otherwise promotes a selected descendant to its parent when that parent is
        // collapsed. Besides violating the left-click-only selection model, that is dangerous in
        // Requested Changes: one selected request could silently become the category-wide
        // "accept all" selection. Clear a selection that is about to be hidden instead.
        var selectionCleared = treeView.SelectedNode != null
                               && !ReferenceEquals(treeView.SelectedNode, node)
                               && IsDescendantOf(treeView.SelectedNode, node);
        if (selectionCleared)
            treeView.SelectedNode = null;

        // Deliberately NOT wrapped in BeginUpdate/EndUpdate. Suspending redraw only pays off when
        // several mutations are being batched - this is a single Expand/Collapse, and EndUpdate
        // re-enables WM_SETREDRAW by invalidating the whole control, so wrapping it threw away the
        // native incremental expand (which repaints just the rows that moved) and repainted the
        // entire list instead. That full repaint is what read as the list redrawing rather than
        // opening.
        _allowTreeToggle = true;
        _suspendScrollSync = true;
        try
        {
            var expanding = !node.IsExpanded;

            // Marker updated to its target state *before* the expand, not from inside the
            // AfterExpand notification it raises. The control then paints the whole change once,
            // with the final text already in place, instead of painting the new rows and then being
            // dirtied again half way through - which is what the flicker was.
            SetDropdownNodeText(node, expanding);

            if (expanding)
                node.Expand();
            else
                node.Collapse();
        }
        finally
        {
            _allowTreeToggle = false;
            _suspendScrollSync = false;
        }

        // One sync for the whole toggle rather than one per node event, and after the expand rather
        // than from inside AfterExpand, where the control has not finished relaying out.
        SyncScrollbarFor(treeView);

        if (selectionCleared)
        {
            UpdateArrowButton();
            UpdateRequestActionButtons();
        }
    }

    static bool IsDescendantOf(TreeNode node, TreeNode ancestor)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    // Same shape as vatsys.SectorsWindow.SectorsView_DrawNode - save the clip, clip, clear, draw the
    // text, restore - and reads the tree's own ForeColor/BackColor rather than naming identities, so
    // a tree keeps whatever colours it was given. What it draws for selection and for this window's
    // own flagged rows is described inline below.
    void SectorsView_DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        var treeView = (TreeViewEx)sender!;
        var selected = (e.State & TreeNodeStates.Selected) != 0;
        var flagged = IsStagedNode(e.Node) || IsFlashingHeading(e.Node);

        // Selection is a filled bar, which is how vatSys marks a highlighted row everywhere else -
        // WindowButtonSelected (DarkBlue in this profile) behind WindowBackground text, the same
        // inversion its own menus use. It used to recolour the text to HighlightedText (CyanBlue)
        // and leave the background alone, which read as a different kind of thing entirely.
        //
        // A staged row keeps its WindowWarning text even while selected: BrightYellow stays legible
        // on that fill, and losing the staged marker just because the row is highlighted would hide
        // the one thing the controller most needs to see.
        var background = selected
            ? Colours.GetColour(Colours.Identities.WindowButtonSelected)
            : treeView.BackColor;

        // Owned sectors with an incoming request are drawn as ordinary rows. They used to take
        // WindowEmergency, which is the red this profile uses for genuine emergencies - far too
        // loud for "somebody would like this sector", and it made a normal working list look like
        // something was wrong. The request is already surfaced where it belongs: the Requested From
        // Me heading flashes, the Settings header flashes, and the arrow goes unavailable for that
        // sector until it is answered (see UpdateArrowButton).
        var foreground = flagged
            ? Colours.GetColour(Colours.Identities.WindowWarning)
            : selected
                ? Colours.GetColour(Colours.Identities.WindowBackground)
                : treeView.ForeColor;

        // The bar spans the full width of the control, not just the label - e.Bounds under
        // OwnerDrawText is only the text. Unselected rows still clip to e.Bounds, exactly as
        // vatsys.SectorsWindow's own DrawNode does.
        var fill = selected
            ? new Rectangle(0, e.Bounds.Top, treeView.ClientSize.Width, e.Bounds.Height)
            : e.Bounds;

        // Graphics.Clip's getter allocates a fresh Region rather than handing back a borrowed one,
        // so the saved original needs disposing just as much as the replacement does.
        using var previousClip = e.Graphics.Clip;

        using (var clip = new Region(fill))
            e.Graphics.Clip = clip;

        e.Graphics.Clear(background);
        TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont, e.Node.Bounds, foreground);

        e.Graphics.Clip = previousClip;
    }

    // Group headers (Approach/Centre/.../Requested By Me/...) and primary-position sectors that
    // bundle their own sub-sectors (e.g. AAE, TBD) both get the same >/v expand-collapse prefix
    // (see SetDropdownNodeText) since ShowPlusMinus is off - there is no right-click menu left to
    // fall back on for expanding a primary, so it needs the same click affordance a header has.
    // baseText becomes the node's Name, which is what both SetDropdownNodeText and
    // TreeView_NodeMouseClick's toggle-on-click key off; a leaf sector keeps Name empty so it reads
    // as plain text and does not toggle.
    static void ApplySectorNodeText(TreeNode node, string baseText)
    {
        if (node.Nodes.Count > 0)
        {
            node.Name = baseText;
            SetDropdownNodeText(node, node.IsExpanded);
            return;
        }

        node.Name = string.Empty;
        node.Text = LeafText(baseText);
        node.ToolTipText = node.Text;
    }

    // A leaf row's text, padded to reserve the space a sibling's >/v prefix would occupy - see
    // BlankPrefix. Used by ApplySectorNodeText and everywhere else a leaf is built directly (the
    // self-referencing "primary that is also its own sub-sector" case - see BuildOwnedSectorNode -
    // and a not-yet-applied staged request row).
    static string LeafText(string baseText) => BlankPrefix + baseText;

    static void RefreshDropdownNodeText(TreeNode node) => SetDropdownNodeText(node, node.IsExpanded);

    // Writes the >/v prefix for a given expansion state, and only when it would actually change.
    //
    // Both assignments are TVM_SETITEM round trips that dirty the node, so doing them
    // unconditionally repainted rows that already read correctly. That mattered most from inside
    // AfterExpand: the control had just painted the newly revealed rows, and rewriting the node's
    // text there dirtied it again mid-operation and forced a second paint pass over the list. That
    // double draw is what the expand flicker was.
    //
    // vatsys.SectorsWindow has no such marker at all - ShowPlusMinus is false and nothing annotates
    // the text - so none of this is inherited behaviour to preserve. It is ours, and it has to be
    // cheap enough not to cost a repaint.
    static void SetDropdownNodeText(TreeNode node, bool expanded)
    {
        if (string.IsNullOrEmpty(node.Name))
            return;

        var text = (expanded ? ExpandedPrefix : CollapsedPrefix) + node.Name;
        if (node.Text == text)
            return;

        node.Text = text;
        node.ToolTipText = text;
    }

    static void RefreshDropdownNodeTextRecursive(TreeNode node)
    {
        RefreshDropdownNodeText(node);
        foreach (TreeNode child in node.Nodes)
            RefreshDropdownNodeTextRecursive(child);
    }

    // A logical identity for one path that survives a full Nodes.Clear()+rebuild. The whole path is
    // required because some grouping data contains a same-named sector both as a claimable parent
    // and as its own child; a global "sector:TBD" key could restore a parent selection onto that
    // child instead. Text is only the fallback for informational request descendants/placeholders.
    // One node's own identity, without its ancestors. The full path key is assembled top-down as
    // the tree is walked (see CaptureExpanded/RestoreExpandedAndSelection), because building it
    // per-node from the node upwards was the single most expensive thing this window did: it
    // allocated a Stack and a string per segment and then joined them, for every node, on three
    // separate full walks of every rebuild (capture, signature, restore). With a few hundred
    // sectors that is thousands of allocations per refresh - and refreshes happen on every poll.
    //
    // The path still matters for correctness: some grouping data contains the same sector both as a
    // claimable parent and as its own child, so a bare "sector:TBD" could restore a parent's
    // expansion onto that child. Assembling it downwards costs one concatenation per node instead.
    static string NodeSegment(TreeNode node) => node.Tag switch
    {
        SectorsVolumes.Sector sector => "sector:" + sector.Name,
        SectorChangeRequest request => "request:" + request.Id,
        _ when ReferenceEquals(node.Tag, CategoryTag) => "category:" + node.Name,
        _ => "text:" + node.Text
    };

    static string ChildKey(string parentKey, TreeNode node) =>
        parentKey.Length == 0 ? NodeSegment(node) : parentKey + "" + NodeSegment(node);

    sealed class TreeViewState
    {
        public readonly HashSet<string> ExpandedKeys = new();
        // Every key present before the rebuild, expanded or not. Capture only records what was
        // *open*, which makes a group the controller deliberately closed indistinguishable from one
        // that has never been seen - and those two want opposite treatment on a rebuild.
        public readonly HashSet<string> KnownKeys = new();
        public string? SelectedKey;
        public int ScrollValue;
    }

    TreeViewState CaptureTreeState(TreeViewEx view, ScrollBar scrollBar)
    {
        var state = new TreeViewState { ScrollValue = scrollBar.Value };
        var selected = view.SelectedNode;
        CaptureExpanded(view.Nodes, "", state, selected);
        return state;
    }

    // Descends only into branches that are open. A collapsed branch cannot contain an expanded node
    // by definition, so walking it was pure waste - and it is where nearly all the nodes live.
    // The selected node's key is picked up on the way past rather than rebuilt from scratch.
    void CaptureExpanded(TreeNodeCollection nodes, string parentKey, TreeViewState state, TreeNode? selected)
    {
        foreach (TreeNode node in nodes)
        {
            var key = ChildKey(parentKey, node);
            state.KnownKeys.Add(key);

            if (ReferenceEquals(node, selected))
                state.SelectedKey = key;

            if (!_expandedNodes.Contains(node))
                continue;

            state.ExpandedKeys.Add(key);
            CaptureExpanded(node.Nodes, key, state, selected);
        }
    }

    // Re-expands whatever was open before the rebuild and re-selects the same logical item if it
    // still exists post-refresh - so a poll tick (every 10s) can't collapse an open dropdown or
    // move the selection out from under the controller mid-action. Must run inside the caller's
    // BeginUpdate/EndUpdate.
    static void RestoreExpandedAndSelection(TreeViewEx view, TreeViewState state) =>
        RestoreExpandedAndSelection(view, state, expandNewGroups: false);

    // expandNewGroups opens any top-level group the previous state had never seen. Used by Owned, so
    // a sector staged into a category that wasn't on screen a moment ago is visible immediately
    // rather than hidden behind a dropdown the controller has to find and open. A group they
    // deliberately closed stays closed - it is in KnownKeys, so it isn't "new".
    static void RestoreExpandedAndSelection(TreeViewEx view, TreeViewState state, bool expandNewGroups)
    {
        TreeNode? selected = null;

        void Walk(TreeNodeCollection nodes, string parentKey)
        {
            foreach (TreeNode node in nodes)
            {
                var key = ChildKey(parentKey, node);

                if (key == state.SelectedKey)
                    selected = node;

                var isNewGroup = expandNewGroups
                                 && parentKey.Length == 0
                                 && node.Nodes.Count > 0
                                 && !state.KnownKeys.Contains(key);

                // Nothing below a branch that was closed can have been open either, so there is no
                // reason to descend into it looking for one.
                if (!isNewGroup && !state.ExpandedKeys.Contains(key))
                    continue;

                node.Expand();
                RefreshDropdownNodeText(node);
                Walk(node.Nodes, key);
            }
        }

        Walk(view.Nodes, "");

        if (selected != null)
            view.SelectedNode = selected;
    }

    // Restores the scroll position captured by CaptureTreeState - call after the tree's own
    // Configure*Scrollbar() has already run against the rebuilt content, so PreferredHeight/
    // ActualHeight reflect the new node structure before the old offset is reapplied to it.
    void RestoreScroll(TreeViewEx view, ScrollBar scrollBar, TreeViewState state)
    {
        var itemHeight = Math.Max(view.ItemHeight, 1);
        // Guarded so the bar's own Scroll handler doesn't also push this position into the tree -
        // the next line does that itself, and letting both run scrolled it twice.
        SetScrollBarValue(scrollBar, state.ScrollValue);
        view.SetScrollPosVert((state.ScrollValue + itemHeight - 1) / itemHeight);
    }

    // Maps the tree's current row offset back to a scrollbar value, the inverse of RestoreScroll's
    // value-to-row conversion: pos*h - h + 1 is the smallest value that rounds back to the same row.
    // Clamped at zero, which the raw expression is not - at row 0 it evaluates to 1 - ItemHeight.
    void SyncScrollValue(TreeViewEx view, ScrollBar scrollBar)
    {
        var itemHeight = Math.Max(view.ItemHeight, 1);
        var value = Math.Max(0, view.GetScrollPos().Y * itemHeight - itemHeight + 1);
        if (scrollBar.Value != value)
            SetScrollBarValue(scrollBar, value);
    }

    // The bar raises Scroll for a value assigned from code exactly as it does for a drag, and that
    // handler scrolls the tree - so syncing the bar after an expand would scroll it a second time.
    // One-directional: bar follows tree here, tree follows bar only for real user scrolling.
    void SetScrollBarValue(ScrollBar scrollBar, int value)
    {
        _syncingScrollBar = true;
        try
        {
            scrollBar.Value = value;
        }
        finally
        {
            _syncingScrollBar = false;
        }
    }

    void SyncScrollbarFor(TreeViewEx view)
    {
        if (ReferenceEquals(view, _currSectorsView))
        {
            ConfigureCurrScrollbar();
            SyncScrollValue(_currSectorsView, _currScrollBar);
        }
        else if (ReferenceEquals(view, _availSectorsView))
        {
            ConfigureAvailScrollbar();
            SyncScrollValue(_availSectorsView, _availScrollBar);
        }
        else
        {
            ConfigureRequestedScrollbar();
            SyncScrollValue(_requestedChangesView, _requestedScrollBar);
        }
    }

    // What TreeViewEx.GetPreferredHeight() computes, without the cost of computing it.
    //
    // Its MeasureHeight walks *every* node in the tree - collapsed branches included, since it
    // recurses on Nodes.Count rather than on expansion - and reads TreeNode.Bounds for each one.
    // Bounds is not a managed value: it round-trips to the native control (TVM_GETITEMRECT) per
    // node, and returns an empty rectangle for anything not currently visible. So the result is
    // simply the visible rows' combined height, arrived at via one SendMessage for every node in
    // the dataset - several hundred of them, on every expand and every rebuild.
    //
    // Same answer, walked in managed code, and only descending into branches that are actually
    // open - a collapsed tree costs a handful of checks instead of hundreds of messages.
    int VisibleContentHeight(TreeViewEx view) =>
        CountVisibleNodes(view.Nodes) * Math.Max(view.ItemHeight, 1);

    int CountVisibleNodes(TreeNodeCollection nodes)
    {
        var count = 0;
        foreach (TreeNode node in nodes)
        {
            count++;
            if (_expandedNodes.Contains(node))
                count += CountVisibleNodes(node.Nodes);
        }

        return count;
    }

    // Drops a discarded subtree from the expansion set. Called before a rebuild clears the nodes,
    // because the set holds references and those nodes are about to cease to exist. Purely managed -
    // enumerating TreeNodeCollection touches no native state.
    void ForgetNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            _expandedNodes.Remove(node);
            ForgetNodes(node.Nodes);
        }
    }

    // The tree's own Height is never touched. An earlier version sized each tree to its full content
    // and scrolled it by moving it inside its panel, to dodge the repaint TreeViewEx causes when it
    // strips WS_VSCROLL during WM_NCCALCSIZE. That resizing re-entered the same handler: changing a
    // window's style from inside a frame calculation makes Windows send another WM_NCCALCSIZE, and
    // it recursed until the stack was gone - an uncatchable 0xC00000FD inside comctl32 that took
    // vatSys down the moment the window opened. The flicker it was chasing is cosmetic; this is not.
    void ConfigureCurrScrollbar()
    {
        // Only touched when the content height actually moved. Each of these setters repaints the
        // scrollbar, and this runs on every expand, collapse and rebuild - so re-asserting values
        // that had not changed was a visible flicker beside the list for no reason at all.
        var content = VisibleContentHeight(_currSectorsView);
        if (content == _currContentHeight && _currScrollBar.ActualHeight == _currSectorsView.Height)
            return;

        _currContentHeight = content;
        _currScrollBar.PreferredHeight = content;
        _currScrollBar.ActualHeight = _currSectorsView.Height;
        _currScrollBar.Change = Math.Max(_currSectorsView.ItemHeight, 1);
    }

    void ConfigureAvailScrollbar()
    {
        // Only touched when the content height actually moved. Each of these setters repaints the
        // scrollbar, and this runs on every expand, collapse and rebuild - so re-asserting values
        // that had not changed was a visible flicker beside the list for no reason at all.
        var content = VisibleContentHeight(_availSectorsView);
        if (content == _availContentHeight && _availScrollBar.ActualHeight == _availSectorsView.Height)
            return;

        _availContentHeight = content;
        _availScrollBar.PreferredHeight = content;
        _availScrollBar.ActualHeight = _availSectorsView.Height;
        _availScrollBar.Change = Math.Max(_availSectorsView.ItemHeight, 1);
    }

    void ConfigureRequestedScrollbar()
    {
        // Only touched when the content height actually moved. Each of these setters repaints the
        // scrollbar, and this runs on every expand, collapse and rebuild - so re-asserting values
        // that had not changed was a visible flicker beside the list for no reason at all.
        var content = VisibleContentHeight(_requestedChangesView);
        if (content == _requestedContentHeight && _requestedScrollBar.ActualHeight == _requestedChangesView.Height)
            return;

        _requestedContentHeight = content;
        _requestedScrollBar.PreferredHeight = content;
        _requestedScrollBar.ActualHeight = _requestedChangesView.Height;
        _requestedScrollBar.Change = Math.Max(_requestedChangesView.ItemHeight, 1);
    }

    void CurrScrollBar_Scroll(object? sender, EventArgs e)
    {
        if (_syncingScrollBar)
            return;

        _currSectorsView.SetScrollPosVert((_currScrollBar.Value + _currSectorsView.ItemHeight - 1) / _currSectorsView.ItemHeight);
    }

    void AvailScrollBar_Scroll(object? sender, EventArgs e)
    {
        if (_syncingScrollBar)
            return;

        _availSectorsView.SetScrollPosVert((_availScrollBar.Value + _availSectorsView.ItemHeight - 1) / _availSectorsView.ItemHeight);
    }

    void RequestedScrollBar_Scroll(object? sender, EventArgs e)
    {
        if (_syncingScrollBar)
            return;

        _requestedChangesView.SetScrollPosVert((_requestedScrollBar.Value + _requestedChangesView.ItemHeight - 1) / _requestedChangesView.ItemHeight);
    }

    // An observer may look at every list in this window but act on none of it: the backend
    // refuses a claim from a session that is not real ATC, so any control offered here would
    // only ever produce a refusal. Hidden rather than disabled, for the same reason the arrow
    // is hidden on an incoming request - a greyed control reads as "not right now", and for an
    // observer the answer is never.
    //
    // Read from the connection's own Position/Rating - see NetworkIdentity.IsObserver. The previous
    // test on Network.Me.IsRealATC would have blanked a real controller's buttons for the first
    // seconds of every session, since that flag reads false until the network publishes the record.
    static bool IsObserver => NetworkIdentity.IsObserver;

    void UpdateArrowButton()
    {
        var ownedNode = _currSectorsView.SelectedNode;
        var ownedSelected = ownedNode?.Tag is SectorsVolumes.Sector;
        var availSelected = _availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector;

        // A sector somebody has an outstanding request on is an Accept-or-Reject decision before it
        // is anything else, so it cannot be moved until that is answered. Releasing it out from
        // under the request would answer it by side effect: the sector leaves, and whoever asked is
        // left holding a request against airspace this controller no longer has.
        //
        // Descendants count. Releasing a group releases the sub-sectors it covers, so moving a
        // parent would otherwise carry a requested child out with it and never answer the request.
        var blockedByRequest = ownedSelected && ownedNode != null && HasOutstandingRequest(ownedNode);

        // Deliberately reads only the Owned and Available trees. A row in Requested is a request in
        // flight, not a sector sitting somewhere it can be moved out of - Accept, Reject, Add and
        // Remove there are all decisions about that request, which is what the right-click menu is
        // for. Letting the arrow act on it broke the flow: the button means "move between these two
        // lists", and Requested is neither of them.
        _arrowButton.Text = ownedSelected ? ArrowRight : availSelected ? ArrowLeft : ArrowIdle;

        // Disabled rather than hidden for the request case, unlike the Requested-tree case below:
        // there the arrow is the wrong control entirely, whereas here it is the right control and
        // will work as soon as the request is answered - which is what a greyed-out button means.
        _arrowButton.Enabled = !_applyRunning && (ownedSelected || availSelected) && !blockedByRequest;

        // An incoming request is an Accept-or-Reject decision, not a list move. The arrow acts on
        // whatever is selected over in Owned/Available, and that selection stays highlighted while a
        // request is being read - so leaving the arrow available invited pressing it and staging a
        // release of an entirely unrelated sector.
        //
        // Hidden rather than disabled: a greyed-out button still reads as "this is the control for
        // what I am looking at, just not right now", which is the opposite of true here. Accept and
        // Reject are the only actions an incoming request has.
        _arrowButton.Visible = !IsObserver
                               && CategoryNameOf(_requestedChangesView.SelectedNode) != RequestedFromMeName;
    }

    // Compares on exactly the footing ApplyButton_Click actually applies: non-dummy sectors, as a
    // set. SequenceEqual against the raw MMI.SectorsControlled was both order-sensitive and
    // dummy-sensitive, while _sectorsSelected arrives in OzServer's response order and never
    // contains dummies (they're vatSys's own backfill for uncontrolled airspace, filtered out at
    // every other site in this codebase) - so it reported "unsaved changes" essentially always,
    // leaving Apply and Cancel permanently lit whether or not anything actually differed.
    // Whether anything is waiting to be committed. Reads the moved-sector set rather than comparing
    // the staged list against the server, because that comparison also goes true when the *server*
    // changes underneath (a request of mine accepted, a primary taking a sector back) - which is not
    // something the controller staged, and must not light up Apply or lock the list.
    bool HasStagedEdits => _stagedNames.Count > 0 || _stagedRequests.Count > 0;

    void UpdateApplyCancelButtons()
    {
        // Compared against OzServer's record, not MMI.SectorsControlled. Ownership is what Apply
        // actually commits, and MMI is downstream of it (ReconcileMmiWithOwned writes MMI once the
        // commit's refresh lands) - so diffing against MMI reported "unsaved changes" for any
        // difference the two happened to have for unrelated reasons, such as a dummy backfill or a
        // sector held on the network but not yet recorded on OzServer.
        //
        // Nothing to commit and nothing to discard while an Apply is still in flight.
        var pending = HasStagedEdits && !_applyRunning;

        _applyButton.Visible = !IsObserver;
        _cancelButton.Visible = !IsObserver;
        _applyButton.Enabled = pending;
        _cancelButton.Enabled = pending;
    }

    // Staged-ness is drawn per node (see IsStagedNode) but is not part of any tree's signature, so
    // clearing it changes no row's text or structure and the rebuild is correctly skipped - which
    // would leave every row still painted yellow after a successful Apply. Repaint explicitly
    // instead of forcing a rebuild nothing else needs.
    // Pulls every source the three lists read, at once. Called straight after any action this
    // controller takes so the result is on screen immediately rather than on the next poll tick -
    // ownership (Owned/Available), the requests list, and the controlled snapshot that Available
    // filters against all change together when a claim, release, request or accept lands.
    void RefreshAllListsAsync()
    {
        // A single sync covers all three views - see OzServerOwnershipTracker.RefreshFromServerCoreAsync.
        // Queueing rather than dropping: this runs straight after an action of this controller's, so
        // it has to reflect what that action just did rather than give up because a poll was mid-flight.
        _ = RefreshAllListsAfterSyncAsync();
    }

    async Task RefreshAllListsAfterSyncAsync()
    {
        try
        {
            await _tracker.RefreshFromServerAsync();
            RunOnUiThread(RefreshControlledSnapshot);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't refresh sector lists from OzServer: {ex.Message}", ex), "OzServer");
        }
    }

    void RefreshStagedHighlight()
    {
        // Every staging change comes through here, so this is the one place the ghost preview has
        // to be told - including Cancel and closing the window, which clear the staged set.
        _ghosts.SetStaged(_stagedRequests);

        _currSectorsView.Invalidate();
        _availSectorsView.Invalidate();
    }

    void SetSectorListMode(SectorListMode mode)
    {
        if (_sectorListMode == mode)
            return;

        _sectorListMode = mode;
        _availableModeButton.Pressed = mode == SectorListMode.Available;
        _controlledModeButton.Pressed = mode == SectorListMode.Controlled;
        _availableModeButton.Invalidate();
        _controlledModeButton.Invalidate();

        // A mode switch is a genuinely different data set, so the old selection deliberately isn't
        // carried across it - PopulateAvailableList's own state capture/restore (see NodeSegment) would
        // otherwise try to find something that plainly no longer applies.
        _availSectorsView.SelectedNode = null;

        // Draws from the cached snapshot immediately (RenderControlledList), so the press is never
        // waiting on the network, and only asks for a fresher one behind it.
        PopulateAvailableList();
        RefreshControlledSnapshot();
        UpdateArrowButton();
    }

    void RefreshAvailableList()
    {
        // IsDisposed before InvokeRequired: reading InvokeRequired on a disposed control can throw
        // ObjectDisposedException, and this is reached from Network.OnlineATCChanged, which keeps
        // firing regardless of what has happened to this window.
        if (IsDisposed || !IsHandleCreated)
            return;

        if (InvokeRequired)
        {
            RunOnUiThread(RefreshAvailableList);
            return;
        }

        // OnlineATCChanged affects the locally-derived Available list only. Controlled comes from
        // OzServer and already has its own poll; refreshing it for every VATSIM connect/disconnect
        // just creates duplicate requests and increases the chance of out-of-order responses.
        if (_sectorListMode != SectorListMode.Available)
            return;

        PopulateAvailableList();
    }

    // vatsys.SectorsWindow's own LoadSectors - "_sectorsSelected = MMI.SectorsControlled" - has no
    // equivalent here any more and is deliberately not kept. Owned follows OzServer's ownership
    // record, not MMI, and Cancel goes back to that record rather than to MMI (see
    // CancelButton_Click). MMI is only consulted as the pre-baseline seed, in SyncOwnedFromTracker.

    void PopulateLists()
    {
        // IsDisposed first - see RefreshAvailableList.
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            RunOnUiThread(PopulateLists);
            return;
        }

        PopulateOwnedList();
        PopulateAvailableList();
    }

    // Every Populate* method captures the current expand/selection/scroll state before rebuilding
    // and restores it afterwards (see CaptureTreeState/RestoreExpandedAndSelection/RestoreScroll),
    // so a background refresh only ever changes the underlying data, never what the controller is
    // currently looking at - a poll tick or an OnlineATCChanged event can't collapse an open
    // dropdown or scroll the list back to the top out from under them.
    void PopulateOwnedList()
    {
        // See _ownedNodeCache - cleared per pass, not per call site.
        _ownedNodeCache.Clear();

        var sectorNodes = new List<TreeNode>();
        foreach (var key in _sectorsSelected)
        {
            // A grouping sector (e.g. AAE) owns AAW/AAR outright - if one of those is independently
            // in _sectorsSelected too it still belongs nested under its group, not at top level.
            // !other.Equals(key), not other != key: Sector overrides Equals/GetHashCode but not the
            // == operator, so two instances of the same real sector reached by different lookups
            // compare unequal under != (see AfvSectorClaimer.CheckActive, which documents the same
            // trap). One TryGetValue rather than ContainsKey plus an indexer, while here.
            var isSubordinate = _sectorsSelected.Any(other =>
                !other.Equals(key)
                && SectorsVolumes.SectorGroupings.TryGetValue(other, out var covered)
                && covered.Contains(key));
            if (isSubordinate)
                continue;

            sectorNodes.Add(BuildOwnedSectorNode(key));
        }

        sectorNodes = OrderSectorNodes(sectorNodes);
        var signature = "owned|" + TreeSignature(sectorNodes);
        if (signature == _ownedTreeSignature)
            return;

        var state = CaptureTreeState(_currSectorsView, _currScrollBar);

        _rebuildingTree = true;
        try
        {
            _currSectorsView.BeginUpdate();
            try
            {
                // Drop the outgoing nodes from the expansion set before they are discarded.
                ForgetNodes(_currSectorsView.Nodes);
                _currSectorsView.Nodes.Clear();
                AddNodesGroupedByCategory(_currSectorsView, sectorNodes);

                // Every group starts collapsed, including the very first populate - this used to
                // ExpandAll() on first open, which meant the window came up fully unfolded and the
                // controller had to close everything by hand. Nodes are built collapsed, so there is
                // nothing to do here beyond restoring whatever they had opened themselves.
                RestoreExpandedAndSelection(_currSectorsView, state, expandNewGroups: true);
            }
            finally
            {
                _currSectorsView.EndUpdate();
            }

            ConfigureCurrScrollbar();
            RestoreScroll(_currSectorsView, _currScrollBar, state);
            _ownedTreeSignature = signature;
            UpdateArrowButton();
        }
        finally
        {
            _rebuildingTree = false;
        }
    }

    // Cached and cloned per sector name for the lifetime of one PopulateOwnedList pass (see
    // _ownedNodeCache) - two held primaries whose groupings overlap on a shared sub-sector would
    // otherwise have that sub-sector's whole subtree rebuilt from scratch a second time. The
    // recursive call below goes through this wrapper, not the Core method directly, so a deeply
    // nested repeat benefits too.
    TreeNode BuildOwnedSectorNode(SectorsVolumes.Sector sector, int depth = 0)
    {
        if (_ownedNodeCache.TryGetValue(sector.Name, out var cached))
            return (TreeNode)cached.Clone();

        var node = BuildOwnedSectorNodeCore(sector, depth);
        _ownedNodeCache[sector.Name] = node;
        return (TreeNode)node.Clone();
    }

    // Recurses through a grouping sector's own sub-sectors (e.g. TBD > AAE > AAW/AAR) - always
    // shown regardless of whether those sub-sectors are also independent _sectorsSelected entries,
    // since a grouping sector owns them outright. Every level that ends up with children gets its
    // own >/v treatment (see ApplySectorNodeText), not just the outermost one.
    TreeNode BuildOwnedSectorNodeCore(SectorsVolumes.Sector sector, int depth)
    {
        var node = new TreeNode { Tag = sector, NodeFont = _currSectorsView.Font };

        // A sector with both its own Volumes and ResponsibleSectors (e.g. TBD, which is directly
        // controllable *and* bundles AUG/AAE) lists itself inside its own SectorGroupings entry -
        // recursing into that would rebuild this exact node forever and stack-overflow the whole
        // process (uncatchable - that's the clr.dll crash). Render the self-entry as a plain leaf
        // instead, same as vatsys.SectorsWindow's own "p == s" case. depth is a hard backstop
        // against any other cyclical grouping the real dataset might contain.
        // TryGetValue, not an indexer: SubSectors being non-empty is not a guarantee that this
        // sector has a SectorGroupings entry - PopulateOwnedList already probes with ContainsKey
        // before indexing, so the dataset evidently can disagree. Indexing directly turns that into
        // a KeyNotFoundException in the middle of a tree rebuild.
        if (sector.SubSectors.Count > 0 && depth < 8 && SectorsVolumes.SectorGroupings.TryGetValue(sector, out var children))
        {
            foreach (var child in children)
            {
                // A sub-sector OzServer says belongs to someone else is not ours to draw, even
                // though its parent grouping is ours. Claiming a group does cover its sub-sectors,
                // but any one of them can be handed away individually afterwards - and this tree is
                // built from the local dataset's groupings, which know nothing about that. The
                // symptom was a transferred sector still sitting under Owned while the Controlled
                // pane simultaneously, and correctly, showed its new owner: vatSys had already
                // taken the airspace away, so this window was the only thing still disagreeing.
                //
                // OwnerOf returns a synthetic "me" for anything in Owned, so the IsMine test is
                // what stops our own sub-sectors being filtered out here. A child nobody owns
                // returns null and is still drawn, which is correct - claiming the group covers it.
                // The self-reference case (a grouping sector listing itself, see above) is exempt,
                // since that node is the sector already being drawn.
                if (!ReferenceEquals(child, sector) && !_tracker.IsMine(child) && _tracker.OwnerOf(child) != null)
                    continue;

                node.Nodes.Add(ReferenceEquals(child, sector)
                    ? new TreeNode(LeafText(FormatSectorText(child))) { Tag = child, NodeFont = node.NodeFont, ToolTipText = LeafText(FormatSectorText(child)) }
                    : BuildOwnedSectorNode(child, depth + 1));
            }
        }

        ApplySectorNodeText(node, FormatSectorText(sector));
        return node;
    }

    // Available lists every CSEC-eligible sector this controller doesn't already hold - the same
    // basic rule vatsys.SectorsWindow.PopulateLists uses for its own single list (CSECEligible,
    // not already in sectorsSelected) - annotated with who's currently on it, if anyone, purely for
    // information (see ResolveDisplayController). It does NOT hide a sector because someone else is
    // on it or OzServer already has an ownership record for them: that used to gate inclusion
    // entirely (see ShouldListAsAvailable's predecessor, IsClaimable), which meant a sector nobody
    // had "cleanly" claimed yet was invisible here even though vatsys's own window would show it -
    // the controller had no way to even see it existed in order to request it. Staging one anyway
    // still does the right thing on Apply: IsOwnedByAnotherController is what StageSectorChange
    // reads to turn that into a request instead of a claim, and the server has its own final say
    // regardless of what this list chose to show.
    //
    // Controlled is a different data source entirely: "OzServer has an active ownership record for
    // this, owned by someone else" (see RenderControlledList) - a sector some stray callsign is
    // logged into on VATSIM but that was never actually claimed through here correctly does *not*
    // show up as Controlled, and Controlled is unaffected by any of the above.
    void PopulateAvailableList()
    {
        if (_sectorListMode == SectorListMode.Controlled)
        {
            RenderControlledList();
            return;
        }

        // Once for the whole pass, not once per sector - see FindController.
        RefreshOnlineControllerIndex();

        // See _availabilityCache/_displayControllerCache/_availableNodeCache - cleared per pass,
        // not per call site.
        _availabilityCache.Clear();
        _displayControllerCache.Clear();
        _availableNodeCache.Clear();

        var candidates = SectorsVolumes.Sectors.Where(s => s.CSECEligible && ShouldListAsAvailable(s)).ToList();

        // Every candidate gets its own top-level row, in alphabetical order within its category
        // (see OrderSectorNodes) - matching vatsys.SectorsWindow's own list exactly: a sub-sector is
        // itself independently CSECEligible, so it is not suppressed here just because it also shows
        // up nested under its primary's dropdown (see BuildAvailableSectorNode). Both are correct at
        // once - "every sector, alphabetically" and "a primary also shows what it covers" are not in
        // tension, they are just two different rows for the same sector.
        var sectorNodes = new List<TreeNode>();
        foreach (var key in candidates)
        {
            var node = BuildAvailableSectorNode(key);
            if (node != null)
                sectorNodes.Add(node);
        }

        ApplyAvailableSectorNodes(sectorNodes, AvailableModePrefix);
    }

    // Renders the Controlled list straight from the cached snapshot, with no await in the way.
    // Pressing Controlled used to issue a request and draw nothing until it came back; now the last
    // known answer is on screen immediately and the refresh below only ever corrects it.
    //
    // GET /sectors/controlled is already flattened server-side - claiming a grouping sector (e.g.
    // TBD) creates one sector_ownerships row per covered sector, so the response already has a
    // separate TBD/AUG/AAE/... entry, no client-side recursion needed the way Owned/Available's own
    // tree-building does.
    void RenderControlledList()
    {
        if (!_hasControlledSnapshot)
        {
            ShowAvailablePlaceholder(ControlledModePrefix,
                Network.IsConnected ? LoadingControlled : ControlledUnavailable);
            return;
        }

        // GET /sectors/controlled is flattened server-side: claiming a grouping sector creates one
        // ownership row per covered sector, so the response lists INL, ARL, BUR, CNK ... as peers.
        // Rendering that verbatim dropped the grouping entirely - every sub-sector of a held primary
        // appeared as its own top-level row, which is why ARL showed up ungrouped. The structure is
        // rebuilt here from the local dataset so a held primary shows as one row with its
        // sub-sectors nested under it, and either the group or an individual sector can be picked.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var controlled = new List<SectorsVolumes.Sector>();

        foreach (var dto in _controlledSnapshot)
        {
            // Not something this vatSys install's Sectors.xml even has a definition for - nothing
            // sensible to show or act on, so it's skipped rather than shown as a dead entry.
            var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == dto.Name);
            if (sector == null)
                continue;

            controlled.Add(sector);
            if (!string.IsNullOrEmpty(dto.Owner?.Callsign))
                owners[sector.Name] = dto.Owner!.Callsign;
        }

        var sectorNodes = new List<TreeNode>();
        foreach (var sector in controlled)
        {
            // Same subordination rule PopulateOwnedList applies: a sector covered by another
            // controlled sector belongs nested under it, not at top level.
            var isSubordinate = controlled.Any(other =>
                !other.Equals(sector)
                && SectorsVolumes.SectorGroupings.TryGetValue(other, out var covered)
                && covered.Contains(sector));

            if (!isSubordinate)
                sectorNodes.Add(BuildControlledSectorNode(sector, owners));
        }

        ApplyAvailableSectorNodes(sectorNodes, ControlledModePrefix);
    }

    // Mirrors BuildOwnedSectorNode's nesting, but labelled with who holds each sector. Recursion is
    // bounded and self-references are rendered as leaves for exactly the reason documented there: a
    // sector that lists itself inside its own grouping would otherwise rebuild forever and take the
    // process down with an uncatchable stack overflow.
    TreeNode BuildControlledSectorNode(SectorsVolumes.Sector sector, Dictionary<string, string> owners, int depth = 0)
    {
        var node = new TreeNode { Tag = sector, NodeFont = _availSectorsView.Font };

        if (sector.SubSectors.Count > 0 && depth < 8 && SectorsVolumes.SectorGroupings.TryGetValue(sector, out var children))
        {
            foreach (var child in children)
            {
                // A sub-sector this controller owns is not "controlled by someone else", even when
                // the group above it belongs to someone else. Exactly the same trap as
                // BuildOwnedSectorNode: this tree is built from the local dataset's groupings, which
                // know nothing about a sub-sector having been handed over individually. The symptom
                // was taking a sub-sector off another controller and watching it sit in Controlled -
                // correctly gone from their side, but never showing up as ours.
                //
                // Only our own are filtered. A sub-sector held by a third controller still belongs
                // in this tree: every node here is labelled with its actual holder, so the nesting
                // reads correctly rather than misleading.
                if (!ReferenceEquals(child, sector) && _tracker.IsMine(child))
                    continue;

                node.Nodes.Add(ReferenceEquals(child, sector)
                    ? new TreeNode(LeafText(ControlledSectorText(child, owners))) { Tag = child, NodeFont = node.NodeFont, ToolTipText = LeafText(ControlledSectorText(child, owners)) }
                    : BuildControlledSectorNode(child, owners, depth + 1));
            }
        }

        ApplySectorNodeText(node, ControlledSectorText(sector, owners));
        return node;
    }

    static string ControlledSectorText(SectorsVolumes.Sector sector, Dictionary<string, string> owners) =>
        owners.TryGetValue(sector.Name, out var callsign)
            ? $"{sector.Name} - {sector.FullName} ({callsign})"
            : FormatSectorText(sector);

    // The one fetch of "who owns what" that both lists read. It backs Controlled directly, and
    // Available filters against it (see IsOwnedByAnotherController) so a sector another controller
    // holds is never offered as claimable - which is what makes Apply's claim-or-request split
    // predictable instead of a surprise.
    //
    // Refreshed on the window's own poll regardless of which mode is showing, so switching to
    // Controlled has an answer ready rather than starting a request the controller waits on.
    // Sector name plus owner, so a handover between two controllers counts as a change even though
    // the set of controlled sectors did not move. Ordered, because nothing guarantees a stable
    // order and an unstable fingerprint would defeat the point of comparing it at all.
    static string ControlledSignature(IEnumerable<OzServerControlledSectorDto> controlled) =>
        string.Join("|", controlled
            .Select(dto => dto.Name + ">" + (dto.Owner?.Callsign ?? ""))
            .OrderBy(v => v, StringComparer.Ordinal));

    // Rebuilds the Controlled snapshot from the tracker's own last refresh instead of issuing a
    // second GET /sectors/controlled.
    //
    // The tracker already fetches that endpoint for its own ownership picture,
    // and this window was fetching it again on the same 2s tick - so the heaviest of the three
    // queries was being pulled twice per tick, per client. Nothing here needs to be async any more:
    // the data is already in hand by the time the tracker has refreshed.
    void RefreshControlledSnapshot()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        var controlled = _tracker.ControlledByOthers
            .Select(pair => new OzServerControlledSectorDto
            {
                Name = pair.Key,
                FullName = SectorsVolumes.Sectors.FirstOrDefault(x => x.Name == pair.Key)?.FullName ?? pair.Key,
                Owner = pair.Value,
            })
            .ToList();

        var signature = ControlledSignature(controlled);
        var firstAnswer = !_hasControlledSnapshot;

        _controlledSnapshot = controlled;
        _controlledNames.Clear();
        foreach (var dto in controlled)
            _controlledNames.Add(dto.Name);
        _hasControlledSnapshot = true;

        // Only re-render when the answer actually changed. This runs on every poll tick, and
        // rebuilding the node tree each time is what made switching modes stutter: the switch drew
        // from cache, then this landed a moment later and redrew the identical list on top of it.
        // Ownership rarely changes between ticks, so in the normal case this does nothing at all.
        if (!firstAnswer && signature == _controlledSignature)
            return;

        _controlledSignature = signature;

        // Both lists depend on this, not just Controlled - Available's claimable set changes
        // whenever somebody else's ownership does.
        PopulateAvailableList();
    }

    // Whether OzServer records this sector as someone else's right now. Distinct from live VATSIM
    // presence (see ResolveDisplayController/FindController) - a controller who reached a sector by
    // extending into it is not logged in under that sector's callsign at all, so presence alone
    // never sees them. This is what StageSectorChange reads to decide claim vs. request.
    bool IsOwnedByAnotherController(SectorsVolumes.Sector sector) =>
        _controlledNames.Contains(sector.Name);

    // Whether something already in Owned covers this sector - that is, it is held through a
    // grouping sector rather than in its own right.
    //
    // Available used to test Owned for an exact name match only, which left a grouping sector's
    // covered sectors listed as claimable after the group itself was taken. Owned was already
    // drawing them nested under their parent, and PopulateOwnedList's subordination rule stopped
    // them from getting a row of their own, so staging one out of Available changed precisely
    // nothing on screen - the row stayed put and the move looked broken.
    //
    // Brisbane Approach is where this shows up: BAN declares
    // ResponsibleSectors="BAS,BDN,BDS,SHN", so holding BAN silently made all four unmovable.
    bool IsCoveredByOwned(SectorsVolumes.Sector sector) =>
        _sectorsSelected.Any(owned =>
            !owned.Equals(sector)
            && SectorsVolumes.SectorGroupings.TryGetValue(owned, out var covered)
            && covered.Contains(sector));

    // Whether a sector belongs in the Available list at all - matching vatsys.SectorsWindow's own
    // rule for its one list (CSECEligible, not already sectorsSelected), not the narrower
    // "claimable right now" test this used to be (nobody live on it, no OzServer ownership record
    // for anyone else). That narrower test hid a sector the moment anyone else was on it, which
    // meant a controller could not even see it existed here in order to request it - the sector was
    // simply invisible until whoever had it left. What actually happens when one is staged and
    // Apply'd is unaffected: StageSectorChange still reads IsOwnedByAnotherController itself to
    // decide claim vs. request, independently of whether this list chose to show the sector.
    //
    // Only the two "this is already effectively mine" cases still exclude a sector - not "someone
    // else has it": PopulateAvailableList works out its nesting from this same set, so a sector this
    // considers a candidate that BuildAvailableSectorNode then rejects would vanish from the list
    // altogether.
    //
    // Name comparison for the Owned test, not Contains: it is the footing every other
    // Owned/Available decision in this window uses (see StageSectorChange), and the two used to
    // disagree here - the list filtered by name while the node builder filtered by Sector.Equals,
    // which is callsign-based.
    //
    // Memoized per populate pass (see _availabilityCache) - called once for every sector in
    // SectorsVolumes.Sectors by the top-level candidate filter, and again by
    // BuildAvailableSectorNode's own entry check for that same sector. The answer never changes
    // mid-pass (_sectorsSelected is fixed for the duration), so recomputing IsCoveredByOwned's scan
    // on every one of those calls was pure waste.
    bool ShouldListAsAvailable(SectorsVolumes.Sector sector)
    {
        if (_availabilityCache.TryGetValue(sector.Name, out var cached))
            return cached;

        var result = !_sectorsSelected.Any(owned => !owned.IsDummy && owned.Name == sector.Name)
                     && !IsCoveredByOwned(sector);
        _availabilityCache[sector.Name] = result;
        return result;
    }

    void ApplyAvailableSectorNodes(List<TreeNode> sectorNodes, string modePrefix)
    {
        // An empty result is a real answer, not a missing one, and rendering it as a bare empty box
        // made the Available/Controlled toggle look like it had done nothing at all - the single
        // most common reading of Controlled, where usually nobody else holds anything.
        if (sectorNodes.Count == 0)
        {
            ShowAvailablePlaceholder(modePrefix, modePrefix == ControlledModePrefix
                ? NothingControlled
                : NothingAvailable);
            return;
        }

        sectorNodes = OrderSectorNodes(sectorNodes);
        ApplyAvailableTree(modePrefix + TreeSignature(sectorNodes),
            view => AddNodesGroupedByCategory(view, sectorNodes));
    }

    // A single untagged row explaining why the list is empty. Untagged deliberately: the arrow
    // button keys off the tag, so a placeholder can never be selected into something claimable the
    // way a real row can.
    void ShowAvailablePlaceholder(string modePrefix, string text) =>
        ApplyAvailableTree(modePrefix + "placeholder|" + text,
            view => view.Nodes.Add(new TreeNode(text) { NodeFont = view.Font, ToolTipText = text }));

    void ApplyAvailableTree(string signature, Action<TreeViewEx> build)
    {
        if (signature == _availableTreeSignature)
            return;

        var state = CaptureTreeState(_availSectorsView, _availScrollBar);

        _rebuildingTree = true;
        try
        {
            _availSectorsView.BeginUpdate();
            try
            {
                // Drop the outgoing nodes from the expansion set before they are discarded.
                ForgetNodes(_availSectorsView.Nodes);
                _availSectorsView.Nodes.Clear();
                build(_availSectorsView);
                RestoreExpandedAndSelection(_availSectorsView, state);
            }
            finally
            {
                _availSectorsView.EndUpdate();
            }

            ConfigureAvailScrollbar();
            RestoreScroll(_availSectorsView, _availScrollBar, state);
            _availableTreeSignature = signature;
            UpdateArrowButton();
        }
        finally
        {
            _rebuildingTree = false;
        }
    }

    // Cached and cloned per sector name for the lifetime of one PopulateAvailableList pass (see
    // _availableNodeCache) - a sub-sector now deliberately gets built more than once per pass (its
    // own top-level row, and again wherever a different primary nests it - see
    // PopulateAvailableList), which used to mean re-walking that sector's own SectorGroupings and
    // re-running ShouldListAsAvailable/ResolveDisplayController on every one of those repeats. The
    // recursive call below goes through this wrapper, not the Core method directly, so a deeply
    // nested repeat benefits too. null is cached like any other result - a sector that doesn't
    // belong in Available at all is just as stable for the pass as one that does.
    TreeNode? BuildAvailableSectorNode(SectorsVolumes.Sector sector, int depth = 0)
    {
        if (_availableNodeCache.TryGetValue(sector.Name, out var cached))
            return (TreeNode?)cached?.Clone();

        var node = BuildAvailableSectorNodeCore(sector, depth);
        _availableNodeCache[sector.Name] = node;
        return (TreeNode?)node?.Clone();
    }

    // Recurses through a sector's own sub-sectors so a primary position nested at any depth (e.g.
    // AAE inside TBD, or AAW/AAR inside AAE) still gets its own dropdown treatment and its own
    // "who's on it" annotation. Returns null only if this sector doesn't belong in Available at all
    // - already this controller's own, in one form or another (see ShouldListAsAvailable); being
    // occupied or owned by someone else is no longer a reason to hide it, only to say so.
    TreeNode? BuildAvailableSectorNodeCore(SectorsVolumes.Sector sector, int depth)
    {
        if (!ShouldListAsAvailable(sector))
            return null;

        var controller = ResolveDisplayController(sector);
        var node = new TreeNode { Tag = sector, NodeFont = _availSectorsView.Font };

        // See BuildOwnedSectorNode for why the self-reference case (e.g. TBD listing itself in its
        // own SectorGroupings) has to be a leaf, not a recursive call - it would otherwise loop
        // forever and crash the whole process with a stack overflow.
        // See BuildOwnedSectorNode for why this is TryGetValue rather than an indexer.
        if (sector.SubSectors.Count > 0 && depth < 8 && SectorsVolumes.SectorGroupings.TryGetValue(sector, out var children))
        {
            foreach (var child in children)
            {
                if (ReferenceEquals(child, sector))
                {
                    var text = LeafText(FormatSectorText(child, controller));
                    node.Nodes.Add(new TreeNode(text) { Tag = child, NodeFont = node.NodeFont, ToolTipText = text });
                    continue;
                }

                var childNode = BuildAvailableSectorNode(child, depth + 1);
                if (childNode != null)
                    node.Nodes.Add(childNode);
            }
        }

        ApplySectorNodeText(node, FormatSectorText(sector, controller));
        return node;
    }

    // Who to annotate an Available row with, if anyone - purely informational now that presence no
    // longer gates inclusion (see ShouldListAsAvailable). Never this controller's own callsign: this
    // list only shows a sector at all when ShouldListAsAvailable has already excluded anything of
    // theirs, so seeing themselves here means the network hasn't caught up with a local unstage yet,
    // and displaying "(me)" on a row about to disappear on its own is more confusing than showing
    // nothing.
    //
    // Memoized per populate pass (see _displayControllerCache), same reasoning as
    // ShouldListAsAvailable.
    NetworkATC? ResolveDisplayController(SectorsVolumes.Sector sector)
    {
        if (_displayControllerCache.TryGetValue(sector.Name, out var cached))
            return cached;

        var controller = FindController(sector);
        var result = controller != null && controller.Callsign == Network.Callsign ? null : controller;
        _displayControllerCache[sector.Name] = result;
        return result;
    }

    // Reads the per-populate snapshot, never Network.GetOnlineATCs directly.
    //
    // That property is not a cached list: it copies vatSys's live collection into a new List on
    // every single call. This used to be called once per sector *and* once per sub-sector while
    // building the Available tree, so one refresh allocated and linearly scanned that whole list
    // several hundred times. Indexed once per populate instead - see RefreshOnlineControllerIndex.
    NetworkATC? FindController(SectorsVolumes.Sector sector) =>
        sector.Callsign != null && _onlineByCallsign.TryGetValue(sector.Callsign, out var atc) ? atc : null;

    // Rebuilt at the start of each populate so every node built in that pass sees one consistent
    // view of who is online, rather than re-reading a list that can change underneath the walk.
    void RefreshOnlineControllerIndex()
    {
        _onlineByCallsign.Clear();

        // PrimaryPosition.OnlineRealAtcs, not a second predicate of its own.
        //
        // This used to filter on NetworkATC.ValidATC while the primary-inheritance side filtered on
        // IsRealATC, and those are not the same question. ValidATC is a flag vatSys sets when the
        // entry simply parses as a recognised ATC position; IsRealATC additionally requires a real
        // frequency - specifically that frequencies[0] is not 99998, VATSIM's observer sentinel.
        //
        // So an observer sitting on a sector's callsign counted as "someone is on it" here and hid
        // the sector from Available, while PrimaryPosition happily handed that same sector to a
        // primary logging on. One sector, two answers. IsRealATC is the correct one - an observer is
        // not controlling anything - and it is what AfvSectorClaimer has always used.
        foreach (var atc in PrimaryPosition.OnlineRealAtcs())
            _onlineByCallsign[atc.Callsign] = atc;
    }

    static string FormatSectorText(SectorsVolumes.Sector sector, NetworkATC? controller = null)
    {
        if (controller == null)
            return $"{sector.Name} - {sector.FullName}";

        var name = string.IsNullOrEmpty(controller.RealName) ? controller.Callsign : controller.RealName;
        return $"{sector.Name} - {sector.FullName} ({name})";
    }

    static List<TreeNode> OrderSectorNodes(IEnumerable<TreeNode> sectorNodes) => sectorNodes
        .OrderBy(node => GetSectorCategory((SectorsVolumes.Sector)node.Tag))
        .ThenBy(node => ((SectorsVolumes.Sector)node.Tag).Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(node => node.Text, StringComparer.OrdinalIgnoreCase)
        .ToList();

    static void AddNodesGroupedByCategory(TreeViewEx view, IEnumerable<TreeNode> orderedSectorNodes)
    {
        foreach (var group in orderedSectorNodes.GroupBy(node => GetSectorCategory((SectorsVolumes.Sector)node.Tag)))
        {
            var categoryNode = AddCategoryNode(view, CategoryName(group.Key));
            foreach (var node in group)
                categoryNode.Nodes.Add(node);
        }
    }

    // Stable structural fingerprint for a would-be tree. Tag identity is included as well as text:
    // two otherwise identical request rows with different server IDs must still trigger a rebuild,
    // while child boundaries make differently nested sectors distinct.
    static string TreeSignature(IEnumerable<TreeNode> nodes)
    {
        var builder = new StringBuilder();

        static void AppendValue(StringBuilder into, string value) =>
            into.Append(value.Length).Append(':').Append(value);

        static void AppendNode(StringBuilder into, TreeNode node)
        {
            // Own segment, not the full path: this walk already encodes structure through the
            // child counts below, so re-deriving each node's ancestry here was redundant work.
            AppendValue(into, NodeSegment(node));
            AppendValue(into, node.Text);
            into.Append('[').Append(node.Nodes.Count).Append(']');
            foreach (TreeNode child in node.Nodes)
                AppendNode(into, child);
        }

        foreach (var node in nodes)
            AppendNode(builder, node);

        return builder.ToString();
    }

    // Bold category-header fonts, cached against the view font they were derived from. A Font is an
    // unmanaged GDI handle, and AddCategoryNode runs for every category in all three trees on every
    // rebuild - a poll tick every 10s, every OnlineATCChanged, every claim or release - so
    // allocating one per header and never disposing it churned handles for the plugin's whole
    // lifetime. Keyed rather than a single static because the three views could in principle report
    // different fonts; in practice they all inherit the form's, so this holds one entry.
    // UI-thread-only, so no synchronisation.
    static readonly Dictionary<Font, Font> CategoryFonts = new();

    static Font GetCategoryFont(Font source)
    {
        if (!CategoryFonts.TryGetValue(source, out var bold))
        {
            bold = new Font(source, FontStyle.Bold);
            CategoryFonts[source] = bold;
        }

        return bold;
    }

    static TreeNode CreateCategoryNode(TreeViewEx view, string name) => CreateCategoryNode(view, name, collapsible: true);

    // collapsible:false makes a plain heading rather than a dropdown - no >/v prefix, and
    // RefreshDropdownNodeText leaves it alone because that keys off Name. Used for Requested By/From
    // Me, which are labels for the two halves of one short list rather than groups worth folding
    // away: there are only ever two of them, and hiding either hides the thing the controller opened
    // the window to act on.
    static TreeNode CreateCategoryNode(TreeViewEx view, string name, bool collapsible)
    {
        var node = new TreeNode(collapsible ? CollapsedPrefix + name : name)
        {
            Tag = CategoryTag,
            Name = collapsible ? name : string.Empty,
            NodeFont = GetCategoryFont(view.Font)
        };
        node.ToolTipText = node.Text;
        return node;
    }

    static TreeNode AddCategoryNode(TreeViewEx view, string name)
    {
        var node = CreateCategoryNode(view, name);
        view.Nodes.Add(node);
        return node;
    }

    static SectorCategory GetSectorCategory(SectorsVolumes.Sector sector)
    {
        // Guarded because this runs for every node of every rebuild: the Australia profile gives all
        // 306 sectors a Callsign, but nothing in the format requires one, and a single null here
        // would throw inside a tree rebuild rather than anywhere obviously connected to the dataset.
        var callsign = sector.Callsign;
        if (string.IsNullOrEmpty(callsign))
            return SectorCategory.Other;

        if (callsign.EndsWith("_FMP", StringComparison.OrdinalIgnoreCase))
            return SectorCategory.Flow;
        if (callsign.EndsWith("_CTR", StringComparison.OrdinalIgnoreCase) || callsign.EndsWith("_FSS", StringComparison.OrdinalIgnoreCase))
            return SectorCategory.Centre;
        if (callsign.EndsWith("_APP", StringComparison.OrdinalIgnoreCase) || callsign.EndsWith("_DEP", StringComparison.OrdinalIgnoreCase))
            return SectorCategory.Approach;
        if (callsign.EndsWith("_TWR", StringComparison.OrdinalIgnoreCase)
            || callsign.EndsWith("_GND", StringComparison.OrdinalIgnoreCase)
            || callsign.EndsWith("_DEL", StringComparison.OrdinalIgnoreCase))
            return SectorCategory.Tower;
        return SectorCategory.Other;
    }

    static string CategoryName(SectorCategory category) => category switch
    {
        SectorCategory.Flow => "Flow",
        SectorCategory.Centre => "Centre",
        SectorCategory.Approach => "Approach",
        SectorCategory.Tower => "Tower",
        _ => "Other"
    };

    // Commits the staged selection. Everything unpicked is released, everything newly picked is
    // claimed, and anything another controller already owns becomes a request - see
    // OzServerOwnershipTracker.CommitSectorChangesAsync. vatSys itself is only activated as a
    // consequence of that: the single refresh at the end of the commit is what pushes the result
    // into MMI.SectorsControlled and the VSCS panel, through ReconcileMmiWithOwned.
    //
    // async void, so nothing may escape: an exception leaving here is unhandled on the UI thread
    // and takes vatSys down with it rather than surfacing in the error log.
    async void ApplyButton_Click(object? sender, EventArgs e)
    {
        if (_applyRunning || !HasStagedEdits)
            return;

        // Scoped to the sectors the controller actually moved, not to every difference between the
        // list and the server. Those two are not the same set: the Owned list is seeded from
        // MMI.SectorsControlled before the tracker's first response, and MMI and OzServer can
        // legitimately disagree at any moment - committing that difference would claim or release
        // sectors nobody touched. This also keeps one invariant worth relying on: what Apply acts on
        // is exactly what is drawn yellow (see IsStagedNode).
        var owned = _tracker.Owned.Where(s => !s.IsDummy).ToList();
        var staged = _sectorsSelected.Where(s => !s.IsDummy).ToList();

        var toClaim = staged
            .Where(s => _stagedNames.Contains(s.Name) && !owned.Any(o => o.Name == s.Name))
            .ToList();
        var toRelease = owned
            .Where(o => _stagedNames.Contains(o.Name) && !staged.Any(s => s.Name == o.Name))
            .ToList();

        // Snapshotted before the await: the staged list is cleared on completion, and the poll can
        // repopulate the lists while this is in flight.
        var stagedRequests = _stagedRequests.ToList();

        _applyRunning = true;
        UpdateArrowButton();
        UpdateApplyCancelButtons();
        try
        {
            // Requests go in the same call as the claims and releases. The server still applies them
            // in order - releases, then claims, then requests - because a sector freed by one of
            // those releases may be exactly what somebody is being asked for, and asking first would
            // race it.
            var result = await _tracker.CommitSectorChangesAsync(toClaim, toRelease, stagedRequests);

            if (IsDisposed || !IsHandleCreated)
                return;

            // Cleared only after the commit, so a poll landing mid-Apply still can't overwrite what
            // is being committed. The refresh inside the commit has already re-derived Owned, so the
            // resync below adopts the real result - including a claim that turned into a request and
            // therefore did not move.
            _stagedNames.Clear();
            _stagedRequests.Clear();
            SyncOwnedFromTracker();
            // Pulls the just-sent requests back as real ones, so they stop being yellow and become
            // ordinary pending rows under Requested By Me - and refreshes what is claimable, since
            // this Apply just changed it.
            RefreshAllListsAsync();
            RefreshStagedHighlight();
            ReportCommitResult(result);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't apply those sector changes: {ex.Message}", ex), "OzServer");
        }
        finally
        {
            _applyRunning = false;
            UpdateArrowButton();
            UpdateApplyCancelButtons();
        }
    }

    // Requests are the one outcome worth saying out loud: nothing moves on screen for them, so
    // without this an Apply that turned into a request looks like an Apply that did nothing.
    void ReportCommitResult(SectorCommitResult result)
    {
        var parts = new List<string>();

        // Lead / list, like every other popup, with each sector written out in full and the
        // controller holding it named (SectorDescription). These used to run bare three-letter
        // codes together inline - "STR, ARA" - which named airspace the controller then had to go
        // and look up before they could act on it.
        if (result.Requested.Count > 0)
        {
            var described = result.Requested.Distinct().Select(DescribeWithCurrentOwner).ToList();
            parts.Add((described.Count == 1
                          ? "This sector is owned by another controller, so a request has been sent to them:"
                          : "These sectors are owned by other controllers, so requests have been sent:")
                      + Environment.NewLine + Environment.NewLine
                      + string.Join(Environment.NewLine, described));
        }

        // Reported rather than silently dropped: these are sub-sectors of something that *was*
        // claimed, so without saying so the controller sees a claim succeed and has no idea part of
        // the group stayed behind. They are not requested automatically - staging them is how you
        // ask for them.
        if (result.Skipped.Count > 0)
        {
            var described = result.Skipped.Distinct().Select(DescribeWithCurrentOwner).ToList();
            parts.Add((described.Count == 1
                          ? "This sector is already owned by another controller and was left with them. Move it across on its own to request it:"
                          : "These sectors are already owned by other controllers and were left with them. Move one across on its own to request it:")
                      + Environment.NewLine + Environment.NewLine
                      + string.Join(Environment.NewLine, described));
        }

        if (parts.Count == 0)
            return;

        ShowNotice(string.Join(Environment.NewLine + Environment.NewLine, parts), "Sector changes applied");
    }

    // "STR - Sturt (held by BN-TRT_CTR)" for a sector someone else has, "STR - Sturt" when nobody
    // does. The tracker is the same snapshot the Controlled list is built from, so the holder named
    // here is the one the window is already showing.
    string DescribeWithCurrentOwner(string name) =>
        SectorDescription.DescribeWithOwner(
            name,
            _tracker.ControlledByOthers.TryGetValue(name, out var owner) ? owner.Callsign : null);

    static void ShowNotice(string message, string caption)
    {
        var notice = new SectorNoticeWindow(message, caption);
        if (Application.OpenForms["MainForm"] is Form mainForm)
            notice.Show(mainForm);
        else
            notice.Show();

        notice.BringToFront();
    }

    // Throws the staged selection away and goes back to what OzServer says is actually owned.
    void CancelButton_Click(object? sender, EventArgs e) => DiscardStagedChanges();

    // Throws away everything staged but not applied, putting the lists back to what OzServer
    // actually says. Shared by Cancel and by closing the window, which mean the same thing: staged
    // changes are a sentence the controller has not finished, and nothing about them survives
    // walking away from it. Leaving them queued meant the window reopened - possibly much later,
    // after the sectors involved had changed hands - still holding moves the controller had
    // abandoned, one Apply away from committing them by accident.
    void DiscardStagedChanges()
    {
        // An Apply already in flight is not staged any more - it has been committed and is being
        // carried out. Clearing underneath it would only desynchronise the lists from the commit
        // that is still running; ReportCommitResult repopulates from the result either way.
        if (_applyRunning)
            return;

        // Visible also goes false while the form is being torn down, and the rebuild below touches
        // every tree and scrollbar in the window. Nothing needs discarding at that point anyway.
        if (IsDisposed || Disposing)
            return;

        _stagedNames.Clear();
        _stagedRequests.Clear();
        SyncOwnedFromTracker();
        PopulateLists();
        PopulateRequestedChanges();
        RefreshStagedHighlight();
        UpdateArrowButton();
    }

    // Exactly one row in the window is selected at any time. Owned and Available already cleared
    // each other; Requested is now part of the same exclusion. Without it a highlight left behind in
    // Owned sat there while the controller worked in Requested, and the arrow - which reads the
    // Owned/Available selection - appeared to be offering an action on the request being read while
    // actually pointing at an unrelated sector.
    //
    // Every one of these is guarded on e.Node != null for the same reason: clearing a selection
    // raises AfterSelect again with a null node, and acting on that would immediately undo the
    // selection the controller just made.
    void AvailSectorsView_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node != null)
        {
            _currSectorsView.SelectedNode = null;
            _requestedChangesView.SelectedNode = null;
        }

        UpdateArrowButton();
        UpdateRequestActionButtons();
    }

    void CurrSectorsView_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node != null)
        {
            _availSectorsView.SelectedNode = null;
            _requestedChangesView.SelectedNode = null;
        }

        UpdateArrowButton();
        UpdateRequestActionButtons();
    }

    void RequestedChangesView_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node != null)
        {
            _currSectorsView.SelectedNode = null;
            _availSectorsView.SelectedNode = null;
        }

        UpdateRequestActionButtons();
        // The arrow hides while an incoming request is selected, so it has to re-evaluate on a
        // selection change in this tree too, not only in Owned/Available.
        UpdateArrowButton();
    }

    void ArrowButton_Click(object? sender, EventArgs e)
    {
        if (_currSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector ownedSector)
            RunSectorAction(ownedSector, add: false);
        else if (_availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector availSector)
            RunSectorAction(availSector, add: true);
    }

    // The one path both the arrow button and the menu's Add/Remove take. Purely local now - no
    // network call, nothing awaited, nothing to latch against - because the only thing either
    // gesture does is move a row between the two lists. Apply is what talks to OzServer.
    //
    // Controlled mode needs no special case any more either: staging a sector someone else owns and
    // pressing Apply produces a request, because that is what CommitSectorChangesAsync does with a
    // claim the server rejects as already-owned.
    void RunSectorAction(SectorsVolumes.Sector sector, bool add)
    {
        if (_applyRunning)
            return;

        StageSectorChange(sector, add);
        UpdateArrowButton();
    }

    // Called from the Accept button's own click handler (see GetRequestsToAccept for the selection
    // rule it acts on).
    async Task AcceptSelectedRequestsAsync(TreeNode? node)
    {
        try
        {
            var requests = GetRequestsToAccept(node);
            if (requests.Count > 0)
                await AcceptRequestsAsync(requests);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't accept that sector request: {ex.Message}", ex), "OzServer");
            UpdateRequestActionButtons();
        }
    }

    // Reject is the one button covering both directions: on an incoming request (Requested From
    // Me) it declines it, on this controller's own outgoing one (Requested By Me) it deletes it -
    // there is no separate Cancel any more, since "reject my own request" and "cancel my own
    // request" are the same gesture from the controller's side.
    async Task RejectSelectedRequestAsync(TreeNode? node)
    {
        try
        {
            // FindOwningRequest, not a direct Tag test - a request that bundles sub-sectors
            // renders them as rows underneath it, and clicking one of those means that request.
            var request = FindOwningRequest(node);
            if (request == null)
                return;

            var category = CategoryNameOf(node);
            if (category == RequestedFromMeName)
                await RejectRequestAsync(request);
            else if (category == RequestedByMeName)
                await CancelRequestAsync(request);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't reject that sector request: {ex.Message}", ex), "OzServer");
            UpdateRequestActionButtons();
        }
    }

    // Claims through the tracker (a claim covers the sector's full responsible_sectors chain
    // server-side - see SectorOwnershipController::claim), which re-derives Owned from the server
    // and raises OwnedChanged - see the class comment for why this window never touches
    // _sectorsSelected directly.
    // Moves the row between Owned and Available locally and stops there. Nothing is claimed,
    // released or requested, and nothing is activated in vatSys, until Apply - which is what makes
    // the two lists a selection the controller builds up and then commits, rather than a pair of
    // buttons that each fire a live ownership change at OzServer the moment they are clicked.
    //
    // The staged list deliberately diverges from _tracker.Owned while this is set, and
    // SyncOwnedFromTracker leaves it alone until Apply or Cancel resolves it - a poll landing
    // mid-edit used to overwrite whatever the controller had picked.
    void StageSectorChange(SectorsVolumes.Sector sector, bool owned)
    {
        // Taking a sector somebody else holds is a request, not a claim, and the window should say
        // so the moment it is staged rather than pretending it is already in Owned and only
        // revealing the truth after Apply. It goes to Requested By Me instead, still yellow because
        // nothing has been sent yet, and the selection is dropped: the row has moved to a different
        // list and leaving the old one selected points the arrow button at nothing.
        if (owned && IsOwnedByAnotherController(sector))
        {
            if (!_stagedRequests.Any(r => r.Name == sector.Name))
            {
                _stagedRequests.Add(sector);
                _stagedNames.Add(sector.Name);
            }

            _availSectorsView.SelectedNode = null;
            _currSectorsView.SelectedNode = null;

            PopulateLists();
            PopulateRequestedChanges();
            RefreshStagedHighlight();
            UpdateArrowButton();
            UpdateApplyCancelButtons();
            return;
        }

        // Moving a staged request back out again just withdraws it - nothing was ever sent.
        if (!owned && _stagedRequests.Any(r => r.Name == sector.Name))
        {
            _stagedRequests.RemoveAll(r => r.Name == sector.Name);
            _stagedNames.Remove(sector.Name);

            PopulateLists();
            PopulateRequestedChanges();
            RefreshStagedHighlight();
            UpdateArrowButton();
            UpdateApplyCancelButtons();
            return;
        }

        // Name comparison, not Contains: it is the footing every other Owned/Available decision in
        // this window uses, and the one vatSys's own SectorsWindow uses too (see PopulateLists).
        var alreadyOwned = _sectorsSelected.Any(s => !s.IsDummy && s.Name == sector.Name);
        if (owned == alreadyOwned)
            return;

        var updated = _sectorsSelected.Where(s => s.Name != sector.Name).ToList();
        if (owned)
            updated.Add(sector);

        _sectorsSelected = updated;

        // Only this sector's staged-ness changes. Moving it back to whatever OzServer already
        // records drops it out of the set again rather than leaving it marked - otherwise a move
        // and an undo would leave nothing to commit but still keep SyncOwnedFromTracker locked out
        // for the rest of the session, silently freezing the Owned list against the server.
        var ownedOnServer = _tracker.Owned.Any(s => !s.IsDummy && s.Name == sector.Name);
        if (owned == ownedOnServer)
            _stagedNames.Remove(sector.Name);
        else
            _stagedNames.Add(sector.Name);

        PopulateOwnedList();

        if (owned)
        {
            // Available -> Owned, the move that has to feel immediate.
            //
            // Rebuilding Available means clearing and re-adding around 266 nodes, and every one of
            // those is a separate insert into the native tree control - that teardown is what read
            // as the list reloading rather than the row simply moving. Taking the one row out
            // directly is a single operation, and the result on screen is identical.
            //
            // The signature is dropped so the next refresh still does a full, authoritative rebuild:
            // this is a shortcut to the same picture, not a replacement for deriving it properly.
            if (RemoveSectorRow(_availSectorsView, sector.Name, n => _expandedNodes.Remove(n)))
            {
                _availableTreeSignature = null;
                ConfigureAvailScrollbar();
            }
            else
            {
                // Nested, or already gone - rebuild so Available genuinely reflects the move rather
                // than keeping a row that is now staged into Owned.
                PopulateAvailableList();
            }
        }
        else
        {
            // Owned -> Available has to put the row back in its correct group and sort position,
            // which is exactly what a rebuild works out. Far rarer, and not the direction anyone is
            // waiting on.
            PopulateAvailableList();
        }

        // Nothing stays selected after a move. Removing a row makes the native tree promote the next
        // one, so the controller ends up with a different sector highlighted than the one they acted
        // on - and the arrow button then points at that one, ready to move it too. Clearing is the
        // honest end state: the action is finished, and nothing is chosen.
        _availSectorsView.SelectedNode = null;
        _currSectorsView.SelectedNode = null;

        RefreshStagedHighlight();
        UpdateArrowButton();
        UpdateApplyCancelButtons();
    }

    static GenericButton CreateRequestActionButton(string text) => new()
    {
        Enabled = false,
        Margin = new Padding(2),
        Size = new Size(100, 30),
        Text = text,
    };

    // Pulls the canonical list from GET /sector-requests and re-renders from it - called on open,
    // every 10s while visible (see the poll timer in the constructor), and after every action below
    // succeeds, rather than trying to patch _requestsByMe/_requestsFromMe by hand from each
    // response's own shape.
    // Asks the tracker to refresh, which is what actually fetches the requests now - they arrive on
    // its RequestsChanged event and are applied by ApplyRequests below.
    //
    // This used to own its own GET /sector-requests, complete with a queue-and-coalesce loop to stop
    // overlapping polls stacking up. All of that moved: the tracker fetches owned, controlled and
    // requests in a single sync call (SectorOwnershipController::sync), and its _refreshGate already
    // serialises exactly the way this loop was reimplementing.
    Task RefreshRequestedChangesAsync()
    {
        if (!Network.IsConnected)
            return Task.CompletedTask;

        return _tracker.RefreshFromServerAsync();
    }

    // Applies a requests payload from the tracker. Runs on the UI thread - the tracker raises this
    // from wherever its refresh continuation resumes, which is not the UI thread.
    void ApplyRequests(OzServerMyRequestsDto response)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        // A rejected request comes back in by_me exactly once per rejection, purely so this
        // controller can be told (see OzServerSectorOwnershipRequestDto.RejectedAt). It is not a
        // pending request and must not be rendered as one - it is already decided, and Cancel would
        // have nothing to act on.
        var rejected = response.ByMe.Where(dto => dto.RejectedAt != null).ToList();

        // Map both halves before changing either field so a malformed response cannot leave a
        // half-new/half-old Requested Changes snapshot on screen.
        var byMe = response.ByMe
            .Where(dto => dto.RejectedAt == null)
            .Select(dto => MapRequest(dto, dto.TargetCallsign))
            .OfType<SectorChangeRequest>()
            .ToList();
        var fromMe = response.FromMe
            .Select(dto => MapRequest(dto, dto.RequestingCallsign))
            .OfType<SectorChangeRequest>()
            .ToList();

        _requestsByMe.Clear();
        _requestsByMe.AddRange(byMe);

        _requestsFromMe.Clear();
        _requestsFromMe.AddRange(fromMe);

        ReportRejections(rejected);
        PopulateRequestedChanges();
    }

    // Tells the controller their request was turned down, then acknowledges it so the server can
    // drop it. Without the acknowledgement the rejection keeps coming back in every poll - which is
    // deliberate, and is what makes this survive the request being rejected while vatSys was closed:
    // it is collected the next time this window polls, not lost.
    //
    // _reportedRejections guards the gap between showing it and the acknowledgement landing, since
    // the poll interval is shorter than a round trip can take and the same rejection would otherwise
    // pop a second dialog.
    void ReportRejections(List<OzServerSectorOwnershipRequestDto> rejected)
    {
        foreach (var dto in rejected)
        {
            if (!_reportedRejections.Add(dto.Id))
                continue;

            var by = string.IsNullOrEmpty(dto.TargetCallsign) ? "the controller" : dto.TargetCallsign;

            // The sector goes on its own line below the lead rather than inline after it. Written
            // in full it carries its own position callsign, and next to the callsign of whoever
            // denied the request that reads as two owners of one sector - on separate lines it
            // reads as what it is, who said no and what they said no to.
            var message = dto.Sector?.Name is { } name
                ? $"{by} denied your request for:"
                  + Environment.NewLine + Environment.NewLine
                  + SectorDescription.Describe(name)
                : $"{by} denied your request.";

            ShowNotice(message, "Request denied");

            _ = AcknowledgeRejectionAsync(dto.Id);
        }
    }

    async Task AcknowledgeRejectionAsync(int requestId)
    {
        try
        {
            await _api.AcknowledgeRejectionAsync(requestId);
        }
        catch (Exception ex)
        {
            // Left in _reportedRejections either way: the controller has already been told, and
            // re-telling them on the next poll would be worse than the row lingering until
            // PruneRejectedSectorRequestsJob sweeps it up server-side.
            Errors.Add(new Exception($"Couldn't acknowledge that rejection: {ex.Message}", ex), "OzServer");
        }
    }

    // otherControllerCallsign is target_callsign for a "by me" row (who I'm requesting from) or
    // requesting_callsign for a "from me" row (who's requesting from me) - the caller already knows
    // which, so it's simpler to pass it in than re-derive "which side is me" from CIDs here.
    // Returns null if the sector isn't one this vatSys install's Sectors.xml even knows about,
    // rather than showing a row with no local sector to act on.
    static SectorChangeRequest? MapRequest(OzServerSectorOwnershipRequestDto dto, string otherControllerCallsign)
    {
        var sector = dto.Sector == null ? null : SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == dto.Sector.Name);
        return sector == null ? null : new SectorChangeRequest(dto.Id, sector, otherControllerCallsign);
    }

    void PopulateRequestedChanges()
    {
        var byMeNode = CreateCategoryNode(_requestedChangesView, RequestedByMeName, collapsible: false);
        AddRequestNodes(byMeNode, _requestsByMe, NoRequestsByMe, _stagedRequests);

        var fromMeNode = CreateCategoryNode(_requestedChangesView, RequestedFromMeName, collapsible: false);
        // Marked so DrawNode can flash it - the last step of the chain that starts at the Settings
        // menu: something is waiting, and this is the half of the list it is waiting in.
        _fromMeHasPending = _requestsFromMe.Count > 0;
        AddRequestNodes(fromMeNode, _requestsFromMe, NoRequestsFromMe);

        var rootNodes = new[] { byMeNode, fromMeNode };
        var signature = "requested|" + TreeSignature(rootNodes);
        if (signature == _requestedTreeSignature)
        {
            UpdateRequestActionButtons();

            // The arrow depends on the request list too, not just on what is selected - a request
            // arriving for the sector already highlighted in Owned has to take the arrow away, and
            // answering it has to give the arrow back, neither of which involves a selection
            // change. Refreshed on this path as well as below: an unchanged signature still means
            // this ran, and the arrow can be stale for reasons the signature does not cover.
            UpdateArrowButton();
            return;
        }

        var state = CaptureTreeState(_requestedChangesView, _requestedScrollBar);

        _rebuildingTree = true;
        try
        {
            _requestedChangesView.BeginUpdate();
            try
            {
                // Drop the outgoing nodes from the expansion set before they are discarded.
                ForgetNodes(_requestedChangesView.Nodes);
                _requestedChangesView.Nodes.Clear();
                _requestedChangesView.Nodes.AddRange(rootNodes);

                // Always open: they are headings, not dropdowns, so their contents are simply the
                // list. Expanded during the rebuild, where _rebuildingTree already permits it.
                foreach (var root in rootNodes)
                    root.Expand();
                RestoreExpandedAndSelection(_requestedChangesView, state);
            }
            finally
            {
                _requestedChangesView.EndUpdate();
            }

            ConfigureRequestedScrollbar();
            RestoreScroll(_requestedChangesView, _requestedScrollBar, state);
            _requestedTreeSignature = signature;
        }
        finally
        {
            _rebuildingTree = false;
        }

        UpdateRequestActionButtons();
        UpdateArrowButton();
    }

    void AddRequestNodes(TreeNode parent, List<SectorChangeRequest> requests, string emptyText,
        List<SectorsVolumes.Sector>? staged = null)
    {
        // Staged rows first: they are the ones the controller just acted on, and they are tagged
        // with the sector so IsStagedNode paints them yellow. Once Apply sends them the server
        // returns them as real requests and they render in the ordinary colour instead.
        if (staged != null)
        {
            foreach (var sector in staged.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var text = LeafText(FormatSectorText(sector));
                parent.Nodes.Add(new TreeNode(text)
                {
                    Tag = sector,
                    NodeFont = _requestedChangesView.Font,
                    ToolTipText = text
                });
            }
        }

        if (requests.Count == 0)
        {
            if (staged == null || staged.Count == 0)
                parent.Nodes.Add(new TreeNode(emptyText) { NodeFont = _requestedChangesView.Font });

            return;
        }

        foreach (var request in requests
                     .OrderBy(r => r.Sector.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Controller, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Id))
            parent.Nodes.Add(BuildRequestNode(request));
    }

    // Mirrors BuildOwnedSectorNode's own nesting - a request against a primary sector that bundles
    // its own sub-sectors (e.g. WOL) shows them nested underneath, same as Owned/Available already
    // do, rather than as one flat line. Only the node this returns carries Tag = request - that's
    // what GetRequestsToAccept/UpdateRequestActionButtons resolve a selection to (directly, or via
    // FindOwningRequest for a click on one of the nested informational rows below it).
    TreeNode BuildRequestNode(SectorChangeRequest request)
    {
        var text = $"{request.Sector.Name} - {request.Sector.FullName} ({request.Controller})";
        var node = new TreeNode { Tag = request, NodeFont = _requestedChangesView.Font };

        // See BuildOwnedSectorNode for why this is TryGetValue rather than an indexer.
        if (request.Sector.SubSectors.Count > 0 && SectorsVolumes.SectorGroupings.TryGetValue(request.Sector, out var children))
        {
            foreach (var child in children)
            {
                node.Nodes.Add(ReferenceEquals(child, request.Sector)
                    ? new TreeNode(LeafText(FormatSectorText(child))) { NodeFont = node.NodeFont, ToolTipText = LeafText(FormatSectorText(child)) }
                    : BuildRequestDescendantNode(child));
            }
        }

        ApplySectorNodeText(node, text);
        return node;
    }

    // Same recursion (and same self-reference/depth guard) as BuildOwnedSectorNode, but never
    // carries a Tag - these exist purely to show what a primary's request also covers, not as
    // their own checkable/actionable request rows.
    TreeNode BuildRequestDescendantNode(SectorsVolumes.Sector sector, int depth = 0)
    {
        var node = new TreeNode { NodeFont = _requestedChangesView.Font };

        // See BuildOwnedSectorNode for why this is TryGetValue rather than an indexer.
        if (sector.SubSectors.Count > 0 && depth < 8 && SectorsVolumes.SectorGroupings.TryGetValue(sector, out var children))
        {
            foreach (var child in children)
            {
                node.Nodes.Add(ReferenceEquals(child, sector)
                    ? new TreeNode(LeafText(FormatSectorText(child))) { NodeFont = node.NodeFont, ToolTipText = LeafText(FormatSectorText(child)) }
                    : BuildRequestDescendantNode(child, depth + 1));
            }
        }

        ApplySectorNodeText(node, FormatSectorText(sector));
        return node;
    }

    // The single "request state changed" notification point - called after every rebuild of
    // Requested Changes, every selection change in it, and every Accept/Reject/connectivity change,
    // so the two buttons can never show enabled for something they would actually refuse to act on.
    // Accept only ever means something for an incoming request; Reject covers both directions (see
    // RejectSelectedRequestAsync) so it enables for either category, as long as a real request - not
    // a placeholder row or a not-yet-applied staged one - is what's actually selected.
    void UpdateRequestActionButtons()
    {
        var selected = _requestedChangesView.SelectedNode;
        var requestsActionable = !_requestActionRunning && Network.IsConnected;
        var category = CategoryNameOf(selected);

        _acceptButton.Visible = !IsObserver;
        _rejectButton.Visible = !IsObserver;
        _acceptButton.Enabled = requestsActionable && GetRequestsToAccept(selected).Count > 0;
        _rejectButton.Enabled = requestsActionable
                                 && FindOwningRequest(selected) != null
                                 && (category == RequestedFromMeName || category == RequestedByMeName);
    }

    // What Accept acts on, entirely from the current selection:
    //   - the "Requested From Me" header  -> every incoming request, accepted as one batch
    //   - a request row (or any of the informational sub-sector rows under it) -> just that one
    //   - anything else (an outgoing request, the empty placeholder) -> nothing, Accept greyed out
    // Takes the node explicitly rather than reading the selection, because the headings this can
    // act on are no longer selectable - right-clicking "Requested From Me" is now the only way to
    // reach the accept-everything gesture, and a right click does not move the selection.
    static List<SectorChangeRequest> GetRequestsToAccept(TreeNode? selected)
    {
        if (selected == null)
            return new List<SectorChangeRequest>();

        // Selecting the header is the "accept everything incoming" gesture the checkbox cascade
        // used to provide. Read off the rendered nodes rather than _requestsFromMe so it can only
        // ever act on what the controller is actually looking at - notably, the "No incoming
        // requests" placeholder carries no Tag and so contributes nothing.
        if (ReferenceEquals(selected.Tag, CategoryTag) && selected.Text == RequestedFromMeName)
            return selected.Nodes.Cast<TreeNode>()
                .Select(n => n.Tag as SectorChangeRequest)
                .Where(r => r != null)
                .Select(r => r!)
                .ToList();

        if (CategoryNameOf(selected) != RequestedFromMeName)
            return new List<SectorChangeRequest>();

        var request = FindOwningRequest(selected);
        return request == null ? new List<SectorChangeRequest>() : new List<SectorChangeRequest> { request };
    }

    // The request a node belongs to: the node itself when it carries the Tag, otherwise the nearest
    // ancestor that does. A request against a primary renders its covered sub-sectors as untagged
    // rows underneath (see BuildRequestDescendantNode), and clicking one of those plainly means
    // "this request" rather than nothing at all.
    static SectorChangeRequest? FindOwningRequest(TreeNode? node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current.Tag is SectorChangeRequest request)
                return request;
        }

        return null;
    }

    // Which of the two category headers a node sits under, at any depth - the top-level ancestor's
    // Name. Replaces the old Parent?.Name test, which only ever worked for a request row sitting
    // directly beneath its header and reported nothing for the rows nested below it.
    static string? CategoryNameOf(TreeNode? node)
    {
        var top = node;
        while (top?.Parent != null)
            top = top.Parent;

        // Text, not Name: the Requested headings are plain (non-collapsible) headers and carry no
        // Name, and Text is exactly the heading for those. A collapsible category's Text has the
        // >/v prefix, so its Name is still preferred where it has one.
        return top == null ? null : string.IsNullOrEmpty(top.Name) ? top.Text : top.Name;
    }

    // Accepts one or several incoming requests as a single batch - one when a request row is
    // selected, all of them when the "Requested From Me" header is (see
    // OzServerOwnershipTracker.AcceptRequestsBatchAsync) rather than one call per request - firing
    // separate accepts back-to-back for a multi-select could leave a request row behind even though
    // its sector's authority had already moved on, since each one's own claim/refresh cascade could
    // still be in flight when the next one landed.
    async Task AcceptRequestsAsync(List<SectorChangeRequest> requests)
    {
        // Same offline guard RejectRequestAsync/CancelRequestAsync already had, and for a concrete
        // reason: without it, an Accept while disconnected disabled all three buttons and then took
        // a path that never re-enabled them. The tracker returns an empty result list when offline,
        // so no failure is reported, and RefreshRequestedChangesAsync bails out before reaching
        // PopulateRequestedChanges - which is what would have called UpdateRequestActionButtons.
        // The buttons stayed greyed out until the next successful poll after reconnecting.
        if (!Network.IsConnected || _requestActionRunning)
            return;

        _requestActionRunning = true;
        UpdateRequestActionButtons();
        try
        {
            // Accepting means ownership just transferred away from me - the tracker re-derives
            // Owned from the server rather than this window guessing locally what happened.
            var results = await _tracker.AcceptRequestsBatchAsync(requests.Select(r => r.Id));
            UpdateArrowButton();

            var failed = results.Where(r => !r.Accepted).ToList();
            if (failed.Count > 0)
            {
                var summary = string.Join("; ", failed.Select(f => $"{f.Sector ?? $"#{f.RequestId}"}: {f.Message}"));
                Errors.Add(new Exception($"Couldn't accept: {summary}"), "OzServer");
            }

            // AcceptRequestsBatchAsync already applied the state its own response carried, so the
            // lists are current by the time it returns - only the Controlled view needs re-deriving
            // from it.
            RefreshControlledSnapshot();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }
        finally
        {
            _requestActionRunning = false;
            UpdateRequestActionButtons();
        }
    }

    async Task RejectRequestAsync(SectorChangeRequest request)
    {
        if (!Network.IsConnected || _requestActionRunning)
            return;

        _requestActionRunning = true;
        UpdateRequestActionButtons();
        try
        {
            // The response carries the resulting state, so there is no follow-up GET: the list
            // updates on the first reply rather than after a second round trip.
            var result = await _api.RejectRequestAsync(request.Id);
            ActionLog.Log("Ownership", $"Rejected request #{request.Id} for {request.Sector.Name} from {request.Controller}");
            await _tracker.ApplyActionResultAsync(result);
            RefreshControlledSnapshot();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }
        finally
        {
            _requestActionRunning = false;
            UpdateRequestActionButtons();
        }
    }

    async Task CancelRequestAsync(SectorChangeRequest request)
    {
        if (!Network.IsConnected || _requestActionRunning)
            return;

        _requestActionRunning = true;
        UpdateRequestActionButtons();
        try
        {
            // The response carries the resulting state, so there is no follow-up GET: the list
            // updates on the first reply rather than after a second round trip.
            var result = await _api.CancelRequestAsync(request.Id);
            ActionLog.Log("Ownership", $"Cancelled request #{request.Id} for {request.Sector.Name}");
            await _tracker.ApplyActionResultAsync(result);
            RefreshControlledSnapshot();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }
        finally
        {
            _requestActionRunning = false;
            UpdateRequestActionButtons();
        }
    }

}
