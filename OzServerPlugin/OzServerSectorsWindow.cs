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

// The OzServer counterpart to vatsys.SectorsWindow (the built-in "Sector Configuration Window"),
// which the Owned and Available lists are modelled on. Sectors are grouped under Flow/Centre/
// Approach/Tower/Other headers by callsign suffix. The right-hand list toggles between "Available"
// - every sector this controller does not hold, annotated with whoever is on it (see
// ShouldListAsAvailable) - and "Controlled" - the sectors OzServer records as someone else's. The
// arrow button stages a move between Owned and Available/Controlled, and nothing is sent until
// Apply (see StageSectorChange). Accept and Reject act on the selection in Requested Changes.
// There is no right-click menu: a left click on a row with children both selects it and expands
// or collapses it.
//
// The window is a view, not the source of truth. While nothing is staged, _sectorsSelected mirrors
// _tracker.Owned - the OzServerOwnershipTracker shared across the plugin - and is refreshed on its
// OwnedChanged. Ownership changes are made through the tracker, which also follows
// MMI.SectorsControlled (a login, the built-in Sectors window, a VSCS transmit via
// AfvSectorClaimer) whether or not this window is open. Requests and the controlled snapshot
// arrive with the tracker's sync, which this window triggers every 2s while visible: another
// controller's actions have no local signal.
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
    // Plain ASCII rather than an arrow glyph: vatSys's UI font (Terminus, the GenericButton
    // default) is not guaranteed to include one, and vatsys.SectorsWindow uses "<<"/">>"/"<<>>"
    // for the same meaning.
    const string ArrowLeft = "<<"; // points at Owned - claim/request an Available/Controlled selection
    const string ArrowRight = ">>"; // points at Available/Controlled - release an Owned selection
    // Shown, disabled, when neither list has a selection.
    const string ArrowIdle = "<<>>";
    const string CollapsedPrefix = "> ";
    const string ExpandedPrefix = "v ";
    // Same width as the two prefixes above, so a leaf row's text lines up under a sibling that has
    // the arrow - see LeafText.
    const string BlankPrefix = "  ";

    // Marks a TreeNode as a group header rather than a selectable leaf, whichever tree it is in
    // and whatever that tree's leaves carry (a Sector in Owned/Available, a SectorChangeRequest in
    // Requested Changes).
    static readonly object CategoryTag = new();

    enum SectorCategory { Flow, Centre, Approach, Tower, Other }

    enum SectorListMode { Available, Controlled }

    enum RequestListDirection { Outgoing, Incoming }

    List<SectorsVolumes.Sector> _sectorsSelected = new();
    readonly List<SectorChangeRequest> _requestsFromMe = new();
    readonly List<SectorChangeRequest> _requestsByMe = new();
    // Rejections already shown to this controller, by request id - see ReportRejections.
    readonly HashSet<int> _reportedRejections = new();
    // Drives the Requested From Me heading's flash: an on/off toggle repainted on a timer, as
    // vatSys's own flashing is.
    bool _fromMeHasPending;
    bool _flashOn;
    readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 500 };
    SectorListMode _sectorListMode = SectorListMode.Available;
    readonly OzServerApiClient _api = new();
    readonly OzServerOwnershipTracker _tracker;
    readonly System.Windows.Forms.Timer _pollTimer;
    bool _hasOwnedSnapshot;
    // RestoreExpandedAndSelection calls Expand while a Populate* method is rebuilding a tree, and
    // TreeView raises AfterExpand for those calls too. Suppresses the scrollbar side effects until
    // the rebuild has restored the state in full.
    bool _rebuildingTree;
    // Set while ToggleNodeExpansion is expanding or collapsing a node - see the
    // AfterExpandCollapse handlers.
    bool _suspendScrollSync;
    // See SetScrollBarValue.
    bool _syncingScrollBar;
    // Which nodes are expanded, tracked in managed memory. TreeNode.IsExpanded is a TVM_GETITEM
    // round trip to the native control per call, and CountVisibleNodes and CaptureExpanded would
    // ask it of every node - on every expand, in CountVisibleNodes' case. Maintained from the
    // AfterExpand/AfterCollapse handlers, at one such call per event.
    readonly HashSet<TreeNode> _expandedNodes = new();
    // Last content height each scrollbar was configured for - see ConfigureCurrScrollbar.
    int _currContentHeight = -1;
    int _availContentHeight = -1;
    int _requestedContentHeight = -1;
    bool _allowTreeToggle;
    // Signature of each tree as last built, so an unchanged tree is not cleared and recreated. The
    // lists are refreshed on every poll and every network-controller change, and an unconditional
    // rebuild makes the rows twitch.
    string? _ownedTreeSignature;
    string? _availableTreeSignature;
    string? _requestedTreeSignature;
    // Online controllers by callsign, rebuilt once per populate - see RefreshOnlineControllerIndex.
    readonly Dictionary<string, NetworkATC> _onlineByCallsign = new(StringComparer.OrdinalIgnoreCase);
    // Per-pass memos for ShouldListAsAvailable, ResolveDisplayController and
    // StaffedCoveredSectorsForDisplay, cleared at the top of PopulateAvailableList. Keyed by name,
    // like every other Owned/Available comparison in this window.
    readonly Dictionary<string, bool> _availabilityCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, NetworkATC?> _displayControllerCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _staffedCoveredCache = new(StringComparer.OrdinalIgnoreCase);
    // One canonical TreeNode per sector per populate pass, cloned wherever that sector appears
    // again: as its own top-level row and under each primary that covers it (Available), or under
    // two owned primaries whose groupings overlap (Owned). TreeNode.Clone is a managed deep copy
    // with no native calls, where rebuilding the subtree re-walks SectorGroupings for every
    // placement. Cleared at the top of the matching Populate* method.
    readonly Dictionary<string, TreeNode> _ownedNodeCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TreeNode?> _availableNodeCache = new(StringComparer.OrdinalIgnoreCase);
    // Names in _controlledSnapshot as a set - see IsOwnedByAnotherController.
    readonly HashSet<string> _controlledNames = new(StringComparer.OrdinalIgnoreCase);
    // The sectors OzServer records as someone else's, as of the tracker's last sync - see
    // RefreshControlledSnapshot. Held as a snapshot so the Controlled list renders with no await.
    List<OzServerControlledSectorDto> _controlledSnapshot = new();
    string? _controlledSignature;
    bool _hasControlledSnapshot;
    bool _requestActionRunning;
    // The sectors the controller has moved and not yet committed, by name. While any are staged
    // the Owned list is their working selection rather than a view of _tracker.Owned - see
    // SyncOwnedFromTracker.
    //
    // An explicit set rather than "the list differs from _tracker.Owned": those two differ for
    // reasons the controller had nothing to do with - the list is seeded from MMI.SectorsControlled
    // before the tracker's first response, and MMI and OzServer can disagree at any time - and
    // every such row would be highlighted, and committed, as if it had been moved.
    readonly HashSet<string> _stagedNames = new(StringComparer.OrdinalIgnoreCase);
    // Sectors staged to be requested rather than claimed - ones another controller owns. They are
    // not added to _sectorsSelected: they show under Requested By Me until Apply sends the request.
    readonly List<SectorsVolumes.Sector> _stagedRequests = new();
    // Shows the aircraft a staged request would bring with it - told in RefreshStagedHighlight.
    readonly PendingSectorGhosts _ghosts;
    // Shows, on the scope, the sectors a staged claim or request would bring - told in
    // RefreshStagedHighlight.
    readonly RequestedSectorOverlay _overlay;
    bool _applyRunning;

    readonly TableLayoutPanel _tableLayoutPanel1;
    readonly TextLabel _currentSectorsLabel;
    readonly TextLabel _requestedChangesLabel;
    readonly GenericButton _applyButton;
    readonly GenericButton _cancelButton;
    readonly GenericButton _arrowButton;
    readonly ToggleGenericButton _availableModeButton;
    readonly ToggleGenericButton _controlledModeButton;
    // Act on the selection in _requestedChangesView - see UpdateRequestActionButtons.
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

    // Suffixed with the connected server (see UpdateTitleForServer), so a SweatBox session cannot
    // be mistaken for the live network. Carries the running plugin version, so a controller (and
    // anyone reading their screenshot) can tell which build they are on.
    static readonly string BaseTitle = $"OzServer {PluginUpdater.RunningVersionDisplay} - Sector Configuration Window";

    void UpdateTitleForServer()
    {
        var label = NetworkServer.Current switch
        {
            NetworkServer.Live => "Live VATSIM",
            NetworkServer.SweatBox1 => "SweatBox 1",
            NetworkServer.SweatBox2 => "SweatBox 2",
            _ => null
        };
        Text = label == null ? BaseTitle : $"{BaseTitle} — {label}";
    }

    public OzServerSectorsWindow(OzServerOwnershipTracker tracker, PendingSectorGhosts ghosts, RequestedSectorOverlay overlay)
    {
        _ghosts = ghosts;
        _overlay = overlay;
        _tracker = tracker;
        Text = BaseTitle;
        Name = nameof(OzServerSectorsWindow);
        // The window is reused for the rest of the session (HideOnClose, below), so the title is
        // updated on every connect - marshalled, since Network.Connected can fire off the UI
        // thread - as well as once here for a connection that already exists.
        Network.Connected += (_, _) => RunOnUiThread(UpdateTitleForServer);
        UpdateTitleForServer();
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
            // No CheckBoxes: accepting is driven by the selection (see GetRequestsToAccept), and
            // TreeView's CheckBoxes is all-or-nothing, so headers and outgoing rows would carry a
            // checkbox that does nothing.
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
            // As wide as the Requested Changes list above it (see _requestedListRow), so it reads
            // as a divider between that list and Accept/Reject below.
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

        // Owned follows the tracker rather than MMI.SectorsControlledChanged - see the class
        // comment. The tracker's events can fire off the UI thread - see RunOnUiThread.
        _tracker.OwnedChanged += (_, _) => RunOnUiThread(SyncOwnedFromTracker);
        // Requests arrive with the same sync that carries ownership.
        _tracker.RequestsChanged += (_, requests) => RunOnUiThread(() => ApplyRequests(requests));
        // ControlledByOthers changes whenever anyone else's ownership moves. Rebuilt from completed
        // syncs only: reading it straight after starting a refresh would reuse the old snapshot.
        _tracker.Refreshed += (_, _) => RunOnUiThread(RefreshControlledSnapshot);
        Network.OnlineATCChanged += (_, _) => RefreshAvailableList();

        // Runs only while the window is visible (see OnVisibleChanged). Another controller's claim,
        // release, request or handover has no local signal, so this tick is what surfaces it - at
        // 2s, because much slower makes the lists look wrong rather than merely behind.
        _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _pollTimer.Tick += (_, _) =>
        {
            // One sync carries owned, controlled and requests together, and is skipped if one is
            // already in flight.
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

    // Looking at the window acknowledges the title-bar flash, which Plugin sets when an incoming
    // request arrives.
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
            // On every open, in case a while has passed since the tracker last refreshed.
            _ = _tracker.RefreshFromServerIfIdleAsync();
            _ = RefreshRequestedChangesAsync();
            RefreshControlledSnapshot();
            // Changes to who is online while hidden were not drawn - see RefreshAvailableList.
            if (_availableStale)
            {
                _availableStale = false;
                PopulateAvailableList();
            }
        }
        else
        {
            _pollTimer.Stop();

            // Closing is a cancel. The window hides rather than closes (HideOnClose), so there is
            // no Closed event; this catches the X button and every other route to hidden.
            DiscardStagedChanges();
        }
    }

    // The background signals this window reacts to - the tracker's events, Network's - can arrive
    // off the UI thread and before the window has a handle. BeginInvoke on a handleless control
    // throws, and the tracker raises OwnedChanged outside any try/catch, so the exception would
    // escape its refresh unreported. Skipping loses nothing: the constructor populates directly
    // and OnVisibleChanged refreshes on show.
    void RunOnUiThread(MethodInvoker action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            if (InvokeRequired)
                BeginInvoke(UiThread.Guard(() => action()));
            else
                action();
        }
        catch (ObjectDisposedException)
        {
            // The window went away between the check above and the post.
        }
        catch (InvalidOperationException)
        {
            // The handle was destroyed in the same gap.
        }
    }

    // Adopts _tracker.Owned as the Owned list, unless the controller has staged edits.
    void SyncOwnedFromTracker()
    {
        // While anything is staged, the Owned list is the controller's working selection, and a
        // poll tick or an OwnedChanged landing mid-edit must not overwrite it. Available is built
        // from the same list, so the two stay consistent.
        if (HasStagedEdits)
        {
            UpdateApplyCancelButtons();
            return;
        }

        // Before the tracker's first response, Owned is empty because nothing has been asked yet,
        // not because nothing is owned (see OzServerOwnershipTracker.HasBaseline), and rendering
        // it would put every held sector into Available for one round trip. Seeded from
        // MMI.SectorsControlled instead, as vatsys.SectorsWindow.LoadSectors does.
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
        // One IsExpanded call per event keeps the managed set current - see _expandedNodes.
        if (e.Node.IsExpanded)
            _expandedNodes.Add(e.Node);
        else
            _expandedNodes.Remove(e.Node);

        RefreshDropdownNodeText(e.Node);

        // _suspendScrollSync as well as _rebuildingTree: this is raised from inside
        // ToggleNodeExpansion's Expand/Collapse call, before the control has finished relaying
        // out. ToggleNodeExpansion syncs the scrollbar once afterwards instead.
        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureCurrScrollbar();
    }

    void AvailSectorsView_AfterExpandCollapse(object? sender, TreeViewEventArgs e)
    {
        // See CurrSectorsView_AfterExpandCollapse.
        if (e.Node.IsExpanded)
            _expandedNodes.Add(e.Node);
        else
            _expandedNodes.Remove(e.Node);

        RefreshDropdownNodeText(e.Node);

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
        // See CurrSectorsView_AfterExpandCollapse.
        if (e.Node.IsExpanded)
            _expandedNodes.Add(e.Node);
        else
            _expandedNodes.Remove(e.Node);

        RefreshDropdownNodeText(e.Node);

        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureRequestedScrollbar();
    }

    // TreeView's native double-click toggles a node even with ShowPlusMinus off, and
    // TreeViewCancelEventArgs cannot tell that from a direct Expand/Collapse call. Everything is
    // cancelled except ToggleNodeExpansion's own call and the expansion restore during a rebuild.
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

        // TreeView_BeforeSelect cancels this for rows that cannot be selected.
        treeView.SelectedNode = e.Node;

        // A row with a Name - a category header, or a primary sector with sub-sectors (see
        // ApplySectorNodeText) - toggles on the same click that selects it. Leaves and the
        // Requested By/From Me headings have no Name, so a click on one only selects - which for
        // Requested From Me is the accept-all gesture.
        if (!string.IsNullOrEmpty(e.Node.Name))
            ToggleNodeExpansion(treeView, e.Node);
    }

    static bool IsCategoryNode(TreeNode node) => ReferenceEquals(node.Tag, CategoryTag);

    // Only rows that stand for something can be selected: a sector, a request, or the Requested
    // From Me heading, which Accept and Reject handle as a batch. Other headings and placeholder
    // rows are labels, and a highlighted label suggests it can be acted on.
    //
    // Cancelled here rather than in the click handler, to cover keyboard navigation and the
    // selection restore after a rebuild as well.
    static void TreeView_BeforeSelect(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is { } node && IsIncomingRequestHeading(node))
            return;

        if (e.Node?.Tag is not SectorsVolumes.Sector && e.Node?.Tag is not SectorChangeRequest)
            e.Cancel = true;
    }

    static bool IsIncomingRequestHeading(TreeNode node) =>
        ReferenceEquals(node.Tag, CategoryTag) && node.Text == RequestedFromMeName;

    // The Requested From Me heading, on the lit half of its flash cycle, while requests are
    // waiting.
    bool IsFlashingHeading(TreeNode node) =>
        _fromMeHasPending && _flashOn && IsCategoryNode(node) && node.Text == RequestedFromMeName;

    // Removes one sector's row, and its group heading if that leaves the group empty, as a rebuild
    // would. Only a childless top-level group child is removed. Returns false for anything else -
    // a row nested in a grouping sector's subtree, which shows what the parent covers and cannot
    // be edited row by row - and the caller must then rebuild, or the sector is left showing in
    // both lists.
    static bool RemoveSectorRow(TreeViewEx view, string sectorName, Action<TreeNode> forget)
    {
        foreach (TreeNode group in view.Nodes)
        {
            for (var i = group.Nodes.Count - 1; i >= 0; i--)
            {
                var candidate = group.Nodes[i];

                if (candidate.Tag is not SectorsVolumes.Sector sector || sector.Name != sectorName)
                    continue;

                // A grouping sector with rows nested under it: only a rebuild can work out what
                // should remain.
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

    // Whether this row is part of an uncommitted change - see _stagedNames. That covers both
    // directions: a sector staged into Owned, and one staged out that reappears in Available.
    bool IsStagedNode(TreeNode node)
    {
        return node.Tag is SectorsVolumes.Sector sector
               && !sector.IsDummy
               && _stagedNames.Contains(sector.Name);
    }

    // Whether this row, or anything nested under it, has an incoming request waiting on it. Holds
    // the arrow back until the request is answered - see UpdateArrowButton.
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

        // TreeView promotes a selected descendant to its parent when that parent is collapsed,
        // which would silently change what the arrow, Accept or Reject acts on. A selection that
        // is about to be hidden is cleared instead.
        var selectionCleared = treeView.SelectedNode != null
                               && !ReferenceEquals(treeView.SelectedNode, node)
                               && IsDescendantOf(treeView.SelectedNode, node);
        if (selectionCleared)
            treeView.SelectedNode = null;

        // Deliberately not wrapped in BeginUpdate/EndUpdate: EndUpdate invalidates the whole
        // control, replacing the native incremental expand (which repaints only the rows that
        // moved) with a full repaint of the list.
        _allowTreeToggle = true;
        _suspendScrollSync = true;
        try
        {
            var expanding = !node.IsExpanded;

            // The marker is set to its target state before the expand rather than from AfterExpand,
            // so the control paints the change once with the final text in place - see
            // SetDropdownNodeText.
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

        // One sync for the whole toggle, after the expand rather than from inside AfterExpand,
        // where the control has not finished relaying out.
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

    // Same shape as vatsys.SectorsWindow.SectorsView_DrawNode - save the clip, clip, clear, draw
    // the text, restore - using the tree's own ForeColor/BackColor.
    void SectorsView_DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        var treeView = (TreeViewEx)sender!;
        var selected = (e.State & TreeNodeStates.Selected) != 0;
        var flagged = IsStagedNode(e.Node) || IsFlashingHeading(e.Node);

        // Selection is a filled bar - WindowButtonSelected behind WindowBackground text, the
        // inversion vatSys's own menus use. A staged row keeps its WindowWarning text while
        // selected, so the highlight never hides the staged marker.
        var background = selected
            ? Colours.GetColour(Colours.Identities.WindowButtonSelected)
            : treeView.BackColor;

        // Owned sectors with an incoming request are drawn as ordinary rows. The request is
        // surfaced by the flashing Requested From Me heading, and by the arrow being unavailable
        // for that sector until it is answered (see UpdateArrowButton).
        var foreground = flagged
            ? Colours.GetColour(Colours.Identities.WindowWarning)
            : selected
                ? Colours.GetColour(Colours.Identities.WindowBackground)
                : treeView.ForeColor;

        // The bar spans the full width of the control; e.Bounds under OwnerDrawText is only the
        // text. Unselected rows clip to e.Bounds, as vatsys.SectorsWindow's DrawNode does.
        var fill = selected
            ? new Rectangle(0, e.Bounds.Top, treeView.ClientSize.Width, e.Bounds.Height)
            : e.Bounds;

        // Graphics.Clip's getter allocates a fresh Region, so the saved original needs disposing
        // as much as the replacement does.
        using var previousClip = e.Graphics.Clip;

        using (var clip = new Region(fill))
            e.Graphics.Clip = clip;

        e.Graphics.Clear(background);
        TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont, e.Node.Bounds, foreground);

        e.Graphics.Clip = previousClip;

        // The Requested By/From Me headings each carry a swatch of the colour their half of the
        // list is shaded with on the scope, read from RequestedSectorOverlay so the legend cannot
        // drift from the highlight. Drawn after the clip is restored: an unselected row's clip is
        // only the text's bounding box, and a swatch placed past the text would be clipped away.
        if (IsCategoryNode(e.Node) && (e.Node.Text == RequestedByMeName || e.Node.Text == RequestedFromMeName))
        {
            var swatchColour = e.Node.Text == RequestedByMeName
                ? RequestedSectorOverlay.OutgoingColour
                : RequestedSectorOverlay.IncomingColour;

            var textWidth = TextRenderer.MeasureText(e.Graphics, e.Node.Text, e.Node.NodeFont).Width;
            const int swatchSize = 10;
            const int swatchGap = 8;
            var swatchRect = new Rectangle(
                e.Node.Bounds.Left + textWidth + swatchGap,
                e.Node.Bounds.Top + (e.Node.Bounds.Height - swatchSize) / 2,
                swatchSize, swatchSize);

            using var swatchBrush = new SolidBrush(swatchColour);
            e.Graphics.FillRectangle(swatchBrush, swatchRect);
            using var swatchPen = new Pen(Color.FromArgb(180, Color.Black));
            e.Graphics.DrawRectangle(swatchPen, swatchRect);
        }
    }

    // A sector that bundles sub-sectors (e.g. AAE, TBD) gets the same >/v prefix a category header
    // has, since ShowPlusMinus is off. baseText becomes the node's Name, which SetDropdownNodeText
    // and the toggle-on-click in TreeView_NodeMouseClick key off; a leaf keeps Name empty, so it
    // reads as plain text and does not toggle.
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

    // A leaf row's text, padded by the width of a sibling's >/v prefix - see BlankPrefix.
    static string LeafText(string baseText) => BlankPrefix + baseText;

    static void RefreshDropdownNodeText(TreeNode node) => SetDropdownNodeText(node, node.IsExpanded);

    // Writes the >/v prefix for an expansion state, and only when it would change. Both assignments
    // are TVM_SETITEM round trips that dirty the node; made unconditionally from AfterExpand, they
    // force a second paint pass over rows the control has just drawn, which shows as flicker.
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

    // One node's logical identity, without its ancestors, which survives a Nodes.Clear() and
    // rebuild. Text is only the fallback for untagged informational rows and placeholders.
    //
    // State is keyed on the full path, because some grouping data contains the same sector as both
    // a claimable parent and its own child: a bare "sector:TBD" could restore a parent's expansion
    // or selection onto that child. The path is assembled top-down as the tree is walked (see
    // ChildKey), at one concatenation per node - building it upwards from each node is far more
    // expensive, and this runs on every rebuild.
    static string NodeSegment(TreeNode node) => node.Tag switch
    {
        SectorsVolumes.Sector sector => "sector:" + sector.Name,
        SectorChangeRequest request => "request:" + request.Id,
        _ when ReferenceEquals(node.Tag, CategoryTag) => "category:" + (string.IsNullOrEmpty(node.Name) ? node.Text : node.Name),
        _ => "text:" + node.Text
    };

    static string ChildKey(string parentKey, TreeNode node) =>
        parentKey.Length == 0 ? NodeSegment(node) : parentKey + "" + NodeSegment(node);

    sealed class TreeViewState
    {
        public readonly HashSet<string> ExpandedKeys = new();
        // Every key present before the rebuild, expanded or not, so a group the controller closed
        // can be told apart from one that has never been shown - see expandNewGroups.
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

    // Descends only into open branches: a collapsed branch cannot contain an expanded node, and it
    // is where nearly all the nodes are. The selected node's key is picked up on the way past.
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

    // Re-expands what was open before the rebuild and re-selects the same logical item if it still
    // exists, so a background refresh cannot collapse an open dropdown or move the selection out
    // from under the controller. Must run inside the caller's BeginUpdate/EndUpdate.
    static void RestoreExpandedAndSelection(TreeViewEx view, TreeViewState state) =>
        RestoreExpandedAndSelection(view, state, expandNewGroups: false);

    // expandNewGroups also opens any top-level group the previous state had never seen. Used by
    // Owned, so a sector staged into a category that was not on screen is visible at once. A group
    // the controller closed stays closed: it is in KnownKeys.
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

                // Nothing below a closed branch can have been open, so it is not descended into.
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

    // Restores the scroll position captured by CaptureTreeState. Call after the tree's
    // Configure*Scrollbar has run against the rebuilt content, so PreferredHeight/ActualHeight
    // reflect the new node structure before the old offset is reapplied.
    void RestoreScroll(TreeViewEx view, ScrollBar scrollBar, TreeViewState state)
    {
        var itemHeight = Math.Max(view.ItemHeight, 1);
        // Through SetScrollBarValue, so the bar's Scroll handler does not also scroll the tree -
        // the next line does that.
        SetScrollBarValue(scrollBar, state.ScrollValue);
        view.SetScrollPosVert((state.ScrollValue + itemHeight - 1) / itemHeight);
    }

    // Maps the tree's row offset back to a scrollbar value, the inverse of RestoreScroll's
    // conversion: pos*h - h + 1 is the smallest value that rounds back to the same row. Clamped at
    // zero, since at row 0 the expression evaluates to 1 - ItemHeight.
    void SyncScrollValue(TreeViewEx view, ScrollBar scrollBar)
    {
        var itemHeight = Math.Max(view.ItemHeight, 1);
        var value = Math.Max(0, view.GetScrollPos().Y * itemHeight - itemHeight + 1);
        if (scrollBar.Value != value)
            SetScrollBarValue(scrollBar, value);
    }

    // The bar raises Scroll for a value assigned from code as it does for a drag, and that handler
    // scrolls the tree. The flag keeps this one-directional: the bar follows the tree here, and
    // the tree follows the bar only for user scrolling.
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

    // What TreeViewEx.GetPreferredHeight() computes, without its cost. That method walks every
    // node, collapsed branches included, reading TreeNode.Bounds - a TVM_GETITEMRECT round trip
    // per node, which returns an empty rectangle for a row that is not visible. Its result is
    // therefore the visible rows' combined height, counted here in managed code and descending
    // only into open branches.
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

    // Drops a subtree from the expansion set, which holds references to its nodes. Called before a
    // rebuild clears them.
    void ForgetNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            _expandedNodes.Remove(node);
            ForgetNodes(node.Nodes);
        }
    }

    // The tree's own Height is never changed, and a tree is never sized to its content. TreeViewEx
    // strips WS_VSCROLL during WM_NCCALCSIZE, and resizing the tree re-enters that handler: a style
    // change inside a frame calculation makes Windows send another WM_NCCALCSIZE, which recurses
    // until the stack is gone - an uncatchable 0xC00000FD inside comctl32 that takes vatSys down.
    void ConfigureCurrScrollbar()
    {
        // Skipped when nothing moved: each of these setters repaints the scrollbar, and this runs
        // on every expand, collapse and rebuild.
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
        // See ConfigureCurrScrollbar.
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
        // See ConfigureCurrScrollbar.
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

    // An observer may look at every list in this window but act on none: the backend refuses a
    // claim from a session that is not real ATC. The controls are hidden rather than disabled - a
    // greyed control reads as "not right now", and for an observer the answer is never.
    //
    // Read from the connection's own Position/Rating (see NetworkIdentity.IsObserver), not
    // Network.Me.IsRealATC, which reads false for the first seconds of a real controller's session.
    static bool IsObserver => NetworkIdentity.IsObserver;

    void UpdateArrowButton()
    {
        var ownedNode = _currSectorsView.SelectedNode;
        var ownedSelected = ownedNode?.Tag is SectorsVolumes.Sector;
        var availSelected = _availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector;

        // A sector with an outstanding request on it cannot be moved until the request is answered.
        // Releasing it would answer the request by side effect, leaving whoever asked holding a
        // request against airspace this controller no longer has. Descendants count, because
        // releasing a group releases the sub-sectors it covers.
        var blockedByRequest = ownedSelected && ownedNode != null && HasOutstandingRequest(ownedNode);

        // Reads only the Owned and Available trees. A row in Requested is a request in flight,
        // which Accept and Reject decide; the arrow only moves sectors between the lists.
        _arrowButton.Text = ownedSelected ? ArrowRight : availSelected ? ArrowLeft : ArrowIdle;

        // Disabled rather than hidden when blocked by a request: it is the right control, and
        // works again as soon as the request is answered.
        _arrowButton.Enabled = !_applyRunning && !_requestActionRunning
                               && (ownedSelected || availSelected) && !blockedByRequest;

        // Hidden while an incoming request is selected. That is an Accept-or-Reject decision, and a
        // greyed arrow would still read as the control for what is being looked at.
        _arrowButton.Visible = !IsObserver
                               && CategoryNameOf(_requestedChangesView.SelectedNode) != RequestedFromMeName;
    }

    // Whether anything is waiting to be committed. Reads the staged sets rather than comparing the
    // list against the server, because that comparison also goes true when the server changes
    // underneath (a request of this controller's accepted, a primary taking a sector back) - which
    // the controller did not stage, and which must not light up Apply or lock the list.
    bool HasStagedEdits => _stagedNames.Count > 0 || _stagedRequests.Count > 0;

    void UpdateApplyCancelButtons()
    {
        // Nothing to commit or discard while another sector or request action is in flight.
        var pending = HasStagedEdits && !_applyRunning && !_requestActionRunning;

        _applyButton.Visible = !IsObserver;
        _cancelButton.Visible = !IsObserver;
        _applyButton.Enabled = pending;
        _cancelButton.Enabled = pending;
    }

    // Refreshes every source the three lists read. Called straight after an action of this
    // controller's, so the result is on screen at once rather than on the next poll tick.
    void RefreshAllListsAsync()
    {
        // One sync covers all three views. RefreshFromServerAsync queues behind a running sync
        // rather than dropping, since this has to reflect what the action just did.
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

    // Staged-ness is drawn per node (see IsStagedNode) but is not part of any tree's signature, so
    // a change to it rebuilds nothing and the trees are repainted explicitly here. Every staging
    // change comes through this method - Cancel and closing the window included - so it is also
    // where the ghost preview and the scope overlay are told.
    void RefreshStagedHighlight()
    {
        _ghosts.SetStaged(_stagedRequests);

        // The same computation as ApplyButton_Click's toClaim - staged sectors not yet owned on
        // the server - so the overlay never promises more than Apply is about to do.
        var owned = _tracker.Owned.Where(s => !s.IsDummy);
        var stagedClaims = _sectorsSelected.Where(s => !s.IsDummy && _stagedNames.Contains(s.Name))
            .Where(s => !owned.Any(o => o.Name == s.Name))
            .ToList();
        _overlay.SetStaged(stagedClaims, _stagedRequests);

        _currSectorsView.Invalidate();
        _availSectorsView.Invalidate();
        _requestedChangesView.Invalidate();
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

        // The other mode is a different data set, so the selection is not carried across: the
        // state restore in PopulateAvailableList would otherwise look for a row that no longer
        // applies.
        _availSectorsView.SelectedNode = null;

        // Drawn from the cached snapshot (see RenderControlledList), so the press never waits on
        // the network.
        PopulateAvailableList();
        RefreshControlledSnapshot();
        UpdateArrowButton();
    }

    void RefreshAvailableList()
    {
        // IsDisposed before InvokeRequired, which can throw ObjectDisposedException on a disposed
        // control: Network.OnlineATCChanged keeps firing whatever has happened to this window.
        if (IsDisposed || !IsHandleCreated)
            return;

        if (InvokeRequired)
        {
            RunOnUiThread(RefreshAvailableList);
            return;
        }

        // OnlineATCChanged affects only the locally derived Available list. Controlled comes from
        // OzServer, with the tracker's sync.
        if (_sectorListMode != SectorListMode.Available)
            return;

        // OnlineATCChanged fires for every update from every controller online - several a second
        // on a busy night - and each rebuild clears and refills the whole tree on the UI thread.
        // Hidden, nothing is drawn: the list is rebuilt when the window opens. Shown, a burst is
        // folded into one rebuild a second. Rebuilding on every event, hidden or not, starved
        // slower PCs' UI thread (ML-BLA_CTR and ML-GUN_CTR, 16 Nov).
        if (!Visible)
        {
            _availableStale = true;
            return;
        }
        if (!_availableRebuildTimer.Enabled)
            _availableRebuildTimer.Start();
    }

    bool _availableStale;
    System.Windows.Forms.Timer? _availableRebuildTimerField;
    System.Windows.Forms.Timer _availableRebuildTimer
    {
        get
        {
            if (_availableRebuildTimerField == null)
            {
                _availableRebuildTimerField = new System.Windows.Forms.Timer { Interval = 1000 };
                _availableRebuildTimerField.Tick += (_, _) =>
                {
                    _availableRebuildTimerField.Stop();
                    if (IsDisposed || !Visible || _sectorListMode != SectorListMode.Available)
                    {
                        _availableStale = true;
                        return;
                    }
                    _availableStale = false;
                    PopulateAvailableList();
                };
            }
            return _availableRebuildTimerField;
        }
    }

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

    // Every Populate* method captures the expand/selection/scroll state before rebuilding and
    // restores it afterwards (see CaptureTreeState, RestoreExpandedAndSelection, RestoreScroll), so
    // a background refresh changes the data and never what the controller is looking at.
    void PopulateOwnedList()
    {
        _ownedNodeCache.Clear();

        var sectorNodes = new List<TreeNode>();
        foreach (var key in _sectorsSelected)
        {
            // A sector covered by another selected sector's grouping (e.g. AAW under AAE) is
            // nested under that group, not listed at top level. Equals rather than !=: Sector
            // overrides Equals/GetHashCode but not the == operator, so two instances of the same
            // sector compare unequal under != (see AfvSectorClaimer.CheckActive).
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
                ForgetNodes(_currSectorsView.Nodes);
                _currSectorsView.Nodes.Clear();
                AddNodesGroupedByCategory(_currSectorsView, sectorNodes);

                // Nodes are built collapsed. This reopens what the controller had open, and any
                // category that was not on screen before - see expandNewGroups.
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

    // Built once per sector per PopulateOwnedList pass and cloned for each placement - see
    // _ownedNodeCache. The Core method recurses back through this wrapper, so a nested repeat uses
    // the cache too.
    TreeNode BuildOwnedSectorNode(SectorsVolumes.Sector sector, int depth = 0)
    {
        if (_ownedNodeCache.TryGetValue(sector.Name, out var cached))
            return (TreeNode)cached.Clone();

        var node = BuildOwnedSectorNodeCore(sector, depth);
        _ownedNodeCache[sector.Name] = node;
        return (TreeNode)node.Clone();
    }

    // Recurses through a grouping sector's sub-sectors (e.g. TBD > AAE > AAW/AAR). Every level
    // that ends up with children gets the >/v prefix (see ApplySectorNodeText).
    TreeNode BuildOwnedSectorNodeCore(SectorsVolumes.Sector sector, int depth)
    {
        var node = new TreeNode { Tag = sector, NodeFont = _currSectorsView.Font };

        // A sector with both its own Volumes and ResponsibleSectors (e.g. TBD, which is directly
        // controllable and bundles AUG/AAE) lists itself in its own SectorGroupings entry.
        // Recursing into that never terminates, and the resulting stack overflow is uncatchable
        // and takes the process down - so the self-entry is rendered as a leaf, as
        // vatsys.SectorsWindow does. depth is a backstop against any other cycle in the dataset.
        //
        // TryGetValue rather than an indexer: a non-empty SubSectors does not guarantee a
        // SectorGroupings entry.
        if (sector.SubSectors.Count > 0 && depth < 8 && SectorsVolumes.SectorGroupings.TryGetValue(sector, out var children))
        {
            foreach (var child in children)
            {
                // The tree is built from the local dataset's groupings, which know nothing of
                // ownership, so a sub-sector that is not this controller's is skipped when:
                //   - someone else owns it: a group's members can be handed away individually; or
                //   - the group itself is owned: OzServer keeps one ownership record per
                //     sub-sector, so a child missing from Owned was released or never granted.
                // Under a group that is staged rather than owned, unowned children stay - they are
                // what Apply would claim along with it. The self-reference (see above) is exempt.
                if (!ReferenceEquals(child, sector) && !_tracker.IsMine(child)
                    && (_tracker.IsMine(sector) || _tracker.OwnerOf(child) != null))
                    continue;

                node.Nodes.Add(ReferenceEquals(child, sector)
                    ? new TreeNode(LeafText(FormatSectorText(child))) { Tag = child, NodeFont = node.NodeFont, ToolTipText = LeafText(FormatSectorText(child)) }
                    : BuildOwnedSectorNode(child, depth + 1));
            }
        }

        ApplySectorNodeText(node, FormatSectorText(sector));
        return node;
    }

    // Available lists every CSEC-eligible sector this controller does not already hold - the rule
    // vatsys.SectorsWindow.PopulateLists uses for its own list - annotated with whoever is on it
    // (see ResolveDisplayController). A sector is not hidden because someone else is on it or owns
    // it: the controller has to be able to see it to request it. Staging one becomes a request
    // rather than a claim (see StageSectorChange), and the server has the final say either way.
    //
    // Controlled is a different data source: the sectors OzServer records as owned by someone else
    // (see RenderControlledList). A callsign merely logged in on VATSIM does not make a sector
    // Controlled.
    void PopulateAvailableList()
    {
        if (_sectorListMode == SectorListMode.Controlled)
        {
            RenderControlledList();
            return;
        }

        // Once for the whole pass, not once per sector - see FindController.
        RefreshOnlineControllerIndex();

        _availabilityCache.Clear();
        _displayControllerCache.Clear();
        _staffedCoveredCache.Clear();
        _availableNodeCache.Clear();

        var candidates = SectorsVolumes.Sectors.Where(s => s.CSECEligible && ShouldListAsAvailable(s)).ToList();

        // Every candidate gets its own top-level row, as in vatsys.SectorsWindow's list: a
        // sub-sector is CSECEligible in its own right, so it is listed as well as nested under its
        // primary's dropdown (see BuildAvailableSectorNode).
        var sectorNodes = new List<TreeNode>();
        foreach (var key in candidates)
        {
            var node = BuildAvailableSectorNode(key);
            if (node != null)
                sectorNodes.Add(node);
        }

        ApplyAvailableSectorNodes(sectorNodes, AvailableModePrefix);
    }

    // Renders the Controlled list from the cached snapshot, with no await, so pressing Controlled
    // shows the last known answer at once.
    void RenderControlledList()
    {
        if (!_hasControlledSnapshot)
        {
            ShowAvailablePlaceholder(ControlledModePrefix,
                Network.IsConnected ? LoadingControlled : ControlledUnavailable);
            return;
        }

        // The controlled data is flattened server-side: claiming a grouping sector creates one
        // ownership row per covered sector, so a group and its members arrive as peers. The
        // structure is rebuilt here from the local dataset, so a held primary shows as one row
        // with its sub-sectors nested under it, and either the group or a single sector can be
        // picked.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var controlled = new List<SectorsVolumes.Sector>();

        foreach (var dto in _controlledSnapshot)
        {
            // Not defined in this vatSys install's Sectors.xml, so there is nothing to show or act
            // on.
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
            // The subordination rule PopulateOwnedList applies - a sector covered by another
            // controlled sector is nested under it - but only under a sector the same controller
            // holds. The grouping says what a sector could cover, not who has it.
            var isSubordinate = controlled.Any(other =>
                !other.Equals(sector)
                && SameOwner(other, sector, owners)
                && SectorsVolumes.SectorGroupings.TryGetValue(other, out var covered)
                && covered.Contains(sector));

            if (!isSubordinate)
                sectorNodes.Add(BuildControlledSectorNode(sector, owners));
        }

        ApplyAvailableSectorNodes(sectorNodes, ControlledModePrefix);
    }

    // Mirrors BuildOwnedSectorNode's nesting, labelled with who holds each sector. The depth bound
    // and the self-reference leaf guard against the stack overflow described in
    // BuildOwnedSectorNodeCore.
    TreeNode BuildControlledSectorNode(SectorsVolumes.Sector sector, Dictionary<string, string> owners, int depth = 0)
    {
        var node = new TreeNode { Tag = sector, NodeFont = _availSectorsView.Font };

        if (sector.SubSectors.Count > 0 && depth < 8 && SectorsVolumes.SectorGroupings.TryGetValue(sector, out var children))
        {
            foreach (var child in children)
            {
                // A child belongs under this parent only if OzServer records it as controlled by
                // someone else too. The grouping alone would show sectors that have since been
                // carved out of the parent.
                if (!ReferenceEquals(child, sector) && !_controlledNames.Contains(child.Name))
                    continue;

                // And only if it is the same controller - see RenderControlledList. A child held
                // by a different controller is a top-level row of its own.
                if (!ReferenceEquals(child, sector) && !SameOwner(sector, child, owners))
                    continue;

                node.Nodes.Add(ReferenceEquals(child, sector)
                    ? new TreeNode(LeafText(ControlledSectorText(child, owners))) { Tag = child, NodeFont = node.NodeFont, ToolTipText = LeafText(ControlledSectorText(child, owners)) }
                    : BuildControlledSectorNode(child, owners, depth + 1));
            }
        }

        ApplySectorNodeText(node, ControlledSectorText(sector, owners));
        return node;
    }

    static bool SameOwner(SectorsVolumes.Sector a, SectorsVolumes.Sector b, Dictionary<string, string> owners) =>
        owners.TryGetValue(a.Name, out var ownerA)
        && owners.TryGetValue(b.Name, out var ownerB)
        && string.Equals(ownerA, ownerB, StringComparison.OrdinalIgnoreCase);

    static string ControlledSectorText(SectorsVolumes.Sector sector, Dictionary<string, string> owners) =>
        owners.TryGetValue(sector.Name, out var callsign)
            ? $"{sector.Name} - {sector.FullName} ({callsign})"
            : FormatSectorText(sector);

    // Sector name plus owner, so a handover between two controllers counts as a change even though
    // the set of controlled sectors did not move. Ordered, because the source order is not
    // guaranteed to be stable.
    static string ControlledSignature(IEnumerable<OzServerControlledSectorDto> controlled) =>
        string.Join("|", controlled
            .Select(dto => dto.Name + ">" + (dto.Owner?.Callsign ?? ""))
            .OrderBy(v => v, StringComparer.Ordinal));

    // Rebuilds the controlled snapshot from the tracker's last sync rather than fetching it again.
    // It backs the Controlled list, and decides whether a staged move is a claim or a request (see
    // IsOwnedByAnotherController). Refreshed whichever mode is showing, so switching to Controlled
    // has an answer ready.
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

        // Re-rendered only when the answer changed: this runs after every sync, and ownership
        // rarely moves between them.
        if (!firstAnswer && signature == _controlledSignature)
            return;

        _controlledSignature = signature;

        // The snapshot above is kept current regardless; drawing it waits for the window to open.
        if (!Visible)
        {
            _availableStale = true;
            return;
        }

        PopulateAvailableList();
    }

    // Whether OzServer records this sector as someone else's. Distinct from live VATSIM presence
    // (see FindController): a controller who extended into a sector is not logged in under its
    // callsign, so presence never sees them. StageSectorChange reads this to decide claim or
    // request.
    bool IsOwnedByAnotherController(SectorsVolumes.Sector sector) =>
        _controlledNames.Contains(sector.Name);

    // Whether something already in Owned covers this sector - that is, it is held through a
    // grouping sector rather than in its own right. Such a sector is drawn nested under its parent
    // in Owned and gets no row of its own there (see PopulateOwnedList), so listing it in
    // Available would offer a move that changes nothing on screen. For example, BAN declares
    // ResponsibleSectors="BAS,BDN,BDS,SHN".
    bool IsCoveredByOwned(SectorsVolumes.Sector sector) =>
        _sectorsSelected.Any(owned =>
            !owned.Equals(sector)
            && SectorsVolumes.SectorGroupings.TryGetValue(owned, out var covered)
            && covered.Contains(sector));

    // Whether a sector belongs in the Available list at all. Only the two "already effectively
    // mine" cases exclude it - in Owned by name, or covered by something in Owned - and not
    // "someone else has it" (see PopulateAvailableList). The candidate filter and
    // BuildAvailableSectorNode both use this one test, so they cannot disagree and drop a sector
    // from the list altogether.
    //
    // The Owned test compares names rather than using Contains (Sector.Equals is callsign-based):
    // it is the footing every other Owned/Available decision in this window uses - see
    // StageSectorChange.
    //
    // Memoized per populate pass (see _availabilityCache): it is called for every sector by the
    // candidate filter and again by the node builder, and _sectorsSelected is fixed for the pass.
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
        // An empty result is a real answer, and a bare empty box makes the Available/Controlled
        // toggle look as if it did nothing - so it gets a placeholder row saying so.
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

    // A single row explaining why the list is empty. Deliberately untagged: selection and the arrow
    // button key off the tag, so a placeholder can never be acted on.
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

    // Built once per sector per PopulateAvailableList pass and cloned for each placement - its own
    // top-level row, and wherever a primary nests it (see _availableNodeCache). The Core method
    // recurses back through this wrapper, so a nested repeat uses the cache too. null is cached
    // like any other result.
    TreeNode? BuildAvailableSectorNode(SectorsVolumes.Sector sector, int depth = 0)
    {
        if (_availableNodeCache.TryGetValue(sector.Name, out var cached))
            return (TreeNode?)cached?.Clone();

        var node = BuildAvailableSectorNodeCore(sector, depth);
        _availableNodeCache[sector.Name] = node;
        return (TreeNode?)node?.Clone();
    }

    // Recurses through a sector's sub-sectors, so a primary nested at any depth (e.g. AAE inside
    // TBD, or AAW/AAR inside AAE) gets its own dropdown and its own "who's on it" annotation.
    // Returns null only for a sector that does not belong in Available - see ShouldListAsAvailable.
    TreeNode? BuildAvailableSectorNodeCore(SectorsVolumes.Sector sector, int depth)
    {
        if (!ShouldListAsAvailable(sector))
            return null;

        var controller = ResolveDisplayController(sector);
        var node = new TreeNode { Tag = sector, NodeFont = _availSectorsView.Font };
        var staffed = StaffedCoveredSectorsForDisplay(sector);

        // See BuildOwnedSectorNodeCore for the self-reference leaf, the depth bound and
        // TryGetValue.
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

                // A child that is staffed live is not part of this parent right now. It keeps its
                // own top-level row, but is not shown under a parent whose claim would exclude it.
                if (staffed.Contains(child.Name))
                    continue;

                var childNode = BuildAvailableSectorNode(child, depth + 1);
                if (childNode != null)
                    node.Nodes.Add(childNode);
            }
        }

        ApplySectorNodeText(node, FormatSectorText(sector, controller));
        return node;
    }

    // Who to annotate an Available row with, if anyone - informational only. Never this
    // controller's own callsign: ShouldListAsAvailable has already excluded their sectors, so
    // finding themselves here means the network has not caught up with a local change, and the
    // row is better left unannotated.
    //
    // Memoized per populate pass, as ShouldListAsAvailable is.
    NetworkATC? ResolveDisplayController(SectorsVolumes.Sector sector)
    {
        if (_displayControllerCache.TryGetValue(sector.Name, out var cached))
            return cached;

        var controller = FindController(sector);
        var result = controller != null && controller.Callsign == Network.Callsign ? null : controller;
        _displayControllerCache[sector.Name] = result;
        return result;
    }

    // Reads the per-populate index, never Network.GetOnlineATCs directly: that property copies
    // vatSys's live collection into a new List on every call, and this runs once per sector and
    // sub-sector - see RefreshOnlineControllerIndex.
    NetworkATC? FindController(SectorsVolumes.Sector sector) =>
        sector.Callsign != null && _onlineByCallsign.TryGetValue(sector.Callsign, out var atc) ? atc : null;

    HashSet<string> StaffedCoveredSectorsForDisplay(SectorsVolumes.Sector sector)
    {
        if (_staffedCoveredCache.TryGetValue(sector.Name, out var cached))
            return cached;

        var staffed = new HashSet<string>(
            PrimaryPosition.StaffedCoveredSectors(sector, Network.Me?.Callsign, _onlineByCallsign.Values),
            StringComparer.OrdinalIgnoreCase);
        _staffedCoveredCache[sector.Name] = staffed;
        return staffed;
    }

    // Rebuilt at the start of each populate, so every node built in that pass sees one consistent
    // view of who is online.
    void RefreshOnlineControllerIndex()
    {
        _onlineByCallsign.Clear();

        // PrimaryPosition.OnlineRealAtcs rather than a predicate of this window's own, so this
        // list and primary inheritance agree on who is online. It filters on IsRealATC, which -
        // unlike NetworkATC.ValidATC - excludes an observer sitting on a sector's callsign.
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

    // Structural fingerprint of a would-be tree. Tag identity is included as well as text, so two
    // otherwise identical request rows with different server ids still trigger a rebuild, and the
    // child counts make differently nested sectors distinct.
    static string TreeSignature(IEnumerable<TreeNode> nodes)
    {
        var builder = new StringBuilder();

        static void AppendValue(StringBuilder into, string value) =>
            into.Append(value.Length).Append(':').Append(value);

        static void AppendNode(StringBuilder into, TreeNode node)
        {
            // Own segment, not the full path: the child counts below already encode the structure.
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

    // Bold category-header fonts, cached against the view font they derive from. A Font holds a
    // GDI handle, and category nodes are created for every tree on every rebuild, so one per header
    // would churn handles for the plugin's lifetime. Keyed because the three views could report
    // different fonts; in practice they inherit the form's and this holds one entry. UI thread
    // only, so no synchronisation.
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

    // collapsible:false makes a plain heading rather than a dropdown: no >/v prefix and no Name, so
    // it neither toggles nor has its text rewritten. Used for Requested By/From Me, which label the
    // two halves of one short list - folding either away would hide what the controller opened the
    // window to act on.
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
        // Nothing in the Sectors.xml format requires a Callsign, and a null here would throw in
        // the middle of a tree rebuild.
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

    // Commits the staged selection: everything unpicked is released, everything newly picked is
    // claimed, and the staged requests are sent - see
    // OzServerOwnershipTracker.CommitSectorChangesAsync. vatSys follows from that: the refresh at
    // the end of the commit pushes the result into MMI.SectorsControlled and the VSCS panel,
    // through ReconcileMmiWithOwned.
    //
    // async void, so nothing may escape: an exception leaving here is unhandled on the UI thread
    // and takes vatSys down.
    async void ApplyButton_Click(object? sender, EventArgs e)
    {
        if (_applyRunning || _requestActionRunning)
            return;

        if (!HasStagedEdits)
            return;

        // Scoped to the sectors the controller moved, not to every difference between the list
        // and the server - see _stagedNames. Committing the whole difference would claim or
        // release sectors nobody touched. What Apply acts on is what is drawn as staged (see
        // IsStagedNode).
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
            // Requests go in the same call as the claims and releases, and are applied in that
            // order - releases, claims, then requests - because a sector freed by one of the
            // releases may be the one being asked for, and asking first would race it.
            var result = await _tracker.CommitSectorChangesAsync(toClaim, toRelease, stagedRequests);

            if (IsDisposed || !IsHandleCreated)
                return;

            // Cleared only after the commit, so a poll landing mid-Apply cannot overwrite what is
            // being committed. The refresh inside the commit has already re-derived Owned, so the
            // resync below adopts the real result, including a claim that did not go through.
            _stagedNames.Clear();
            _stagedRequests.Clear();
            SyncOwnedFromTracker();
            // Brings the requests just sent back as real ones, so they become ordinary pending
            // rows under Requested By Me.
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

    // Reports the outcomes that move nothing on screen - requests sent, and sub-sectors left with
    // another controller - which would otherwise look like an Apply that did nothing.
    void ReportCommitResult(SectorCommitResult result)
    {
        var parts = new List<string>();

        // A lead line then a list, with each sector written out in full and its holder named
        // (SectorDescription), rather than bare three-letter codes.
        if (result.Requested.Count > 0)
        {
            var described = result.Requested.Distinct().Select(DescribeWithCurrentOwner).ToList();
            parts.Add((described.Count == 1
                          ? "This sector is owned by another controller, so a request has been sent to them:"
                          : "These sectors are owned by other controllers, so requests have been sent:")
                      + Environment.NewLine + Environment.NewLine
                      + string.Join(Environment.NewLine, described));
        }

        // Sub-sectors of something that was claimed, left with their current owner. Reported so
        // the controller knows part of the group stayed behind; they are not requested
        // automatically - staging one is how it is asked for.
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
    // does. Read from the tracker data the Controlled list is built from, so the holder named is
    // the one the window shows.
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

    void CancelButton_Click(object? sender, EventArgs e) => DiscardStagedChanges();

    // Throws away everything staged but not applied and puts the lists back to what OzServer
    // records. Shared by Cancel and by closing the window: staged moves must not survive the window
    // being hidden, or it could reopen much later - after the sectors have changed hands - one
    // Apply away from committing moves the controller had abandoned.
    void DiscardStagedChanges()
    {
        // An Apply in flight is no longer staged, and clearing underneath it would desynchronise
        // the lists from the commit. Its completion resyncs them.
        if (_applyRunning)
            return;

        // Visible also goes false while the form is being torn down, and the rebuild below touches
        // every tree and scrollbar in the window.
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

    // At most one row in the window is selected at a time: a selection in any of the three trees
    // clears the other two. Otherwise a highlight left in Owned or Available would keep the arrow
    // pointed at an unrelated sector while the controller works in Requested.
    //
    // Each handler is guarded on e.Node != null: clearing a selection raises AfterSelect again with
    // a null node, and acting on that would undo the selection just made.
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
        // The arrow hides while an incoming request is selected, so it follows this tree's
        // selection too.
        UpdateArrowButton();
    }

    void ArrowButton_Click(object? sender, EventArgs e)
    {
        if (_currSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector ownedSector)
            RunSectorAction(ownedSector, add: false);
        else if (_availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector availSector)
            RunSectorAction(availSector, add: true);
    }

    // Purely local: the arrow only stages a move between the lists, and Apply is what talks to
    // OzServer. Controlled mode needs no special case - StageSectorChange stages a sector someone
    // else owns as a request.
    void RunSectorAction(SectorsVolumes.Sector sector, bool add)
    {
        if (_applyRunning || _requestActionRunning)
            return;

        StageSectorChange(sector, add);
        UpdateArrowButton();
    }

    // The Accept button's handler - see GetRequestsToAccept for the selection rule it acts on.
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

    // Reject covers both directions: it declines an incoming request (Requested From Me) and
    // cancels one of this controller's own (Requested By Me).
    async Task RejectSelectedRequestAsync(TreeNode? node)
    {
        try
        {
            // A heading means everything under it: every incoming request rejected, or every
            // outgoing one cancelled.
            if (IsRequestHeading(node))
            {
                await RejectAllUnderHeadingAsync(node!);
                return;
            }

            // FindOwningRequest rather than a direct Tag test: a request that bundles sub-sectors
            // renders them as rows underneath it, and one of those means that request.
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

    // Moves the row between Owned and Available locally and stops there. Nothing is claimed,
    // released or requested, and nothing is activated in vatSys, until Apply: the lists are a
    // selection the controller builds up and then commits. While anything is staged the list
    // diverges from _tracker.Owned, and SyncOwnedFromTracker leaves it alone until Apply or Cancel.
    void StageSectorChange(SectorsVolumes.Sector sector, bool owned)
    {
        // Taking a sector somebody else holds is a request, not a claim, and is shown as one from
        // the moment it is staged: under Requested By Me, marked as staged because nothing has
        // been sent. The selection is dropped, since the row has moved to a different list.
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

        // Moving a staged request back out withdraws it - nothing was sent.
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
        // this window uses, as does vatsys.SectorsWindow.PopulateLists.
        var alreadyOwned = _sectorsSelected.Any(s => !s.IsDummy && s.Name == sector.Name);
        if (owned == alreadyOwned)
            return;

        var updated = _sectorsSelected.Where(s => s.Name != sector.Name).ToList();
        if (owned)
            updated.Add(sector);

        // A release is never just the one row. The backend releases the named sector plus every
        // sector it is responsible for that this controller owns (releaseGroup), so unpicking GUN
        // also unpicks KAT, BIK and SAS, and everything under SAS in turn. They leave the list
        // with it, so the list shows what Apply will produce.
        //
        // The members are deliberately not added to _stagedNames, and a member staged on its own
        // earlier is dropped from it. That set is what Apply turns into releases, and naming a
        // member as well as its root would have the backend release the root first and then
        // refuse each member - no longer owned by then - reporting failures for a release that
        // worked.
        var carried = CoveredSectorsReleasedWith(sector);

        if (!owned)
        {
            updated = updated.Where(s => !carried.Any(c => c.Name == s.Name)).ToList();

            foreach (var member in carried)
                _stagedNames.Remove(member.Name);
        }
        else
        {
            // Putting the root back puts its group back with it: nothing has been sent, so what
            // OzServer records as this controller's is what was on screen before the release was
            // staged. A member the controller moved by hand is left alone.
            foreach (var member in carried)
            {
                if (_stagedNames.Contains(member.Name) || updated.Any(s => s.Name == member.Name))
                    continue;

                updated.Add(member);
            }
        }

        _sectorsSelected = updated;

        // Moving the sector back to what OzServer already records drops it from the set rather
        // than leaving it marked. Otherwise a move and its undo would leave nothing to commit yet
        // keep SyncOwnedFromTracker locked out, freezing the Owned list against the server.
        var ownedOnServer = _tracker.Owned.Any(s => !s.IsDummy && s.Name == sector.Name);
        if (owned == ownedOnServer)
            _stagedNames.Remove(sector.Name);
        else
            _stagedNames.Add(sector.Name);

        PopulateOwnedList();

        if (owned)
        {
            // Available -> Owned. Rebuilding Available clears and re-adds every node, each a
            // separate insert into the native tree, which reads as the list reloading. Removing
            // the one row gives the same picture in a single operation. The signature is dropped
            // so the next refresh still does a full, authoritative rebuild.
            if (RemoveSectorRow(_availSectorsView, sector.Name, n => _expandedNodes.Remove(n)))
            {
                _availableTreeSignature = null;
                ConfigureAvailScrollbar();
            }
            else
            {
                // Nested, or already gone: rebuild, so Available does not keep a row that is now
                // staged into Owned.
                PopulateAvailableList();
            }
        }
        else
        {
            // Owned -> Available has to put the row back in its group and sort position, which is
            // what a rebuild works out.
            PopulateAvailableList();
        }

        // Nothing stays selected after a move. Removing a row makes the native tree select the
        // next one, which would leave the arrow pointed at a sector the controller did not choose.
        _availSectorsView.SelectedNode = null;
        _currSectorsView.SelectedNode = null;

        RefreshStagedHighlight();
        UpdateArrowButton();
        UpdateApplyCancelButtons();
    }

    // What a release of this sector takes with it: everything its dataset entry is responsible
    // for, recursively - PrimaryPosition.CoveredBy walks the same ResponsibleSectors chain as the
    // backend's covered(), so this is the set releaseGroup deletes - restricted to what OzServer
    // records as this controller's. The sector itself is left out; the caller is moving that row.
    List<SectorsVolumes.Sector> CoveredSectorsReleasedWith(SectorsVolumes.Sector sector) =>
        PrimaryPosition.CoveredBy(sector)
            .Where(covered => covered.Name != sector.Name)
            .Where(covered => _tracker.Owned.Any(owned => !owned.IsDummy && owned.Name == covered.Name))
            .ToList();

    static GenericButton CreateRequestActionButton(string text) => new()
    {
        Enabled = false,
        Margin = new Padding(2),
        Size = new Size(100, 30),
        Text = text,
    };

    // Asks the tracker to refresh. The requests come with its sync, arrive on RequestsChanged and
    // are applied by ApplyRequests below; the tracker serialises overlapping refreshes.
    Task RefreshRequestedChangesAsync()
    {
        if (!Network.IsConnected)
            return Task.CompletedTask;

        return _tracker.RefreshFromServerAsync();
    }

    // Applies a requests payload from the tracker. Runs on the UI thread, marshalled by the
    // RequestsChanged subscription: the tracker raises the event from wherever its refresh resumes.
    void ApplyRequests(OzServerMyRequestsDto response)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        // A rejected request comes back in by_me so that this controller can be told (see
        // OzServerSectorOwnershipRequestDto.RejectedAt). It is already decided, and must not be
        // rendered as a pending request.
        var rejected = response.ByMe.Where(dto => dto.RejectedAt != null).ToList();

        // Both halves are mapped before either field changes, so a malformed response cannot
        // leave a half-new, half-old snapshot on screen.
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
    // drop it. Until acknowledged, the rejection comes back with every sync - deliberately, so one
    // made while vatSys was closed is collected the next time this window syncs.
    //
    // _reportedRejections covers the gap between showing it and the acknowledgement landing: the
    // poll interval is shorter than a round trip can take, and the same rejection would otherwise
    // raise a second dialog.
    void ReportRejections(List<OzServerSectorOwnershipRequestDto> rejected)
    {
        foreach (var dto in rejected)
        {
            if (!_reportedRejections.Add(dto.Id))
                continue;

            var by = string.IsNullOrEmpty(dto.TargetCallsign) ? "the controller" : dto.TargetCallsign;

            // The sector goes on its own line below the lead. Written in full it carries its own
            // position callsign, which inline beside the callsign of whoever denied the request
            // would read as two owners of one sector.
            var message = dto.Sector?.Name is { } name
                ? $"{by} denied your request for:"
                  + Environment.NewLine + Environment.NewLine
                  + SectorDescription.Describe(name)
                : $"{by} denied your request.";

            // A flow position cannot fall back on claiming the sector some other way, so say so -
            // see PrimaryPosition.MayOwn.
            var own = Network.Me?.Callsign;
            if (PrimaryPosition.IsFlowCallsign(own))
                message += Environment.NewLine + Environment.NewLine
                         + $"Unable to assume sector - position {own} is FMP. Relief cover is only by the controller's approval.";

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
            // Left in _reportedRejections either way: the controller has been told, and telling
            // them again on the next poll would be worse than the row lingering until the
            // backend's maintenance sweep removes it.
            Errors.Add(new Exception($"Couldn't acknowledge that rejection: {ex.Message}", ex), "OzServer");
        }
    }

    // otherControllerCallsign is target_callsign for a "by me" row and requesting_callsign for a
    // "from me" row; the caller knows which. Returns null for a sector this vatSys install's
    // Sectors.xml does not define, since there would be no local sector to act on.
    static SectorChangeRequest? MapRequest(OzServerSectorOwnershipRequestDto dto, string otherControllerCallsign)
    {
        var sector = dto.Sector == null ? null : SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == dto.Sector.Name);
        return sector == null ? null : new SectorChangeRequest(dto.Id, sector, otherControllerCallsign);
    }

    void PopulateRequestedChanges()
    {
        var byMeNode = CreateCategoryNode(_requestedChangesView, RequestedByMeName, collapsible: false);
        AddRequestNodes(byMeNode, _requestsByMe, NoRequestsByMe, RequestListDirection.Outgoing, _stagedRequests);

        var fromMeNode = CreateCategoryNode(_requestedChangesView, RequestedFromMeName, collapsible: false);
        // Lets DrawNode flash the heading while requests are waiting - see IsFlashingHeading.
        _fromMeHasPending = _requestsFromMe.Count > 0;
        AddRequestNodes(fromMeNode, _requestsFromMe, NoRequestsFromMe, RequestListDirection.Incoming);

        var rootNodes = new[] { byMeNode, fromMeNode };
        var signature = "requested|" + TreeSignature(rootNodes);
        if (signature == _requestedTreeSignature)
        {
            UpdateRequestActionButtons();

            // The arrow depends on the request list as well as the selection: a request arriving
            // for the sector selected in Owned takes the arrow away, and answering it gives it
            // back, with no selection change. Refreshed on this path too, since the arrow can be
            // stale for reasons the signature does not cover.
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
                ForgetNodes(_requestedChangesView.Nodes);
                _requestedChangesView.Nodes.Clear();
                _requestedChangesView.Nodes.AddRange(rootNodes);

                // Always open: they are headings, not dropdowns. Expanded during the rebuild, where
                // _rebuildingTree permits it (see TreeView_BeforeMouseExpandCollapse).
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
        TrySelectHighlightedRequest();
    }

    // Set by HighlightRequest and cleared once the request's row has been found and selected,
    // which may only be after the next poll brings the newly sent request back.
    string? _highlightRequestSector;
    DateTime _highlightRequestUntil;

    // Selects this controller's own outgoing request for sector in Requested By Me, so a request
    // raised from a VSCS transmit press opens the window on the thing just asked for.
    public void HighlightRequest(string sectorName)
    {
        _highlightRequestSector = sectorName;
        _highlightRequestUntil = DateTime.UtcNow.AddSeconds(15);
        TrySelectHighlightedRequest();
    }

    void TrySelectHighlightedRequest()
    {
        if (_highlightRequestSector == null)
            return;

        if (DateTime.UtcNow > _highlightRequestUntil)
        {
            _highlightRequestSector = null;
            return;
        }

        var byMe = _requestedChangesView.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text.StartsWith(RequestedByMeName, StringComparison.Ordinal))
                   ?? _requestedChangesView.Nodes.Cast<TreeNode>().FirstOrDefault();
        var node = byMe?.Nodes.Cast<TreeNode>().FirstOrDefault(n =>
            n.Tag is SectorChangeRequest request
            && string.Equals(request.Sector.Name, _highlightRequestSector, StringComparison.OrdinalIgnoreCase));
        if (node == null)
            return;

        _highlightRequestSector = null;
        _requestedChangesView.SelectedNode = node;
        node.EnsureVisible();
    }

    void AddRequestNodes(TreeNode parent, List<SectorChangeRequest> requests, string emptyText,
        RequestListDirection direction, List<SectorsVolumes.Sector>? staged = null)
    {
        // Staged rows first. They are tagged with the sector, not a request, so IsStagedNode marks
        // them; once Apply sends them, the server returns them as real requests.
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
            parent.Nodes.Add(BuildRequestNode(request, direction));
    }

    // Mirrors BuildOwnedSectorNode's nesting, but only for sectors that still belong to the
    // transfer (see RequestCoversSector): an incoming request can only give away sectors this
    // controller still holds, and an outgoing one can only receive sectors the target still holds.
    // The Sectors.xml grouping alone would include sectors since carved out of the group.
    //
    // Only the node this returns carries Tag = request, which is what a selection resolves to -
    // directly, or through FindOwningRequest for one of the informational rows nested below it.
    TreeNode BuildRequestNode(SectorChangeRequest request, RequestListDirection direction)
    {
        var text = $"{request.Sector.Name} - {request.Sector.FullName} ({request.Controller})";
        // A flow position only asks for someone else's airspace to cover it for a while (see
        // PrimaryPosition.MayOwn). Said on the request, so the controller deciding knows it is
        // relief rather than a handover for good.
        if (direction == RequestListDirection.Incoming && PrimaryPosition.IsFlowCallsign(request.Controller))
            text += " - FMP relief cover (temporary)";
        var node = new TreeNode { Tag = request, NodeFont = _requestedChangesView.Font };

        // See BuildOwnedSectorNodeCore for why this is TryGetValue rather than an indexer.
        if (request.Sector.SubSectors.Count > 0 && SectorsVolumes.SectorGroupings.TryGetValue(request.Sector, out var children))
        {
            foreach (var child in children)
            {
                if (ReferenceEquals(child, request.Sector))
                {
                    var selfText = LeafText(FormatSectorText(child));
                    node.Nodes.Add(new TreeNode(selfText) { NodeFont = node.NodeFont, ToolTipText = selfText });
                    continue;
                }

                var childNode = BuildRequestDescendantNode(child, request, direction);
                if (childNode != null)
                    node.Nodes.Add(childNode);
            }
        }

        ApplySectorNodeText(node, text);
        return node;
    }

    // The same recursion, self-reference leaf and depth bound as BuildOwnedSectorNodeCore, but
    // never tagged: these rows only show what a primary's request also covers, and are not
    // actionable themselves. Returns null when the branch no longer belongs to the transfer.
    TreeNode? BuildRequestDescendantNode(SectorsVolumes.Sector sector, SectorChangeRequest request,
        RequestListDirection direction, int depth = 0)
    {
        var node = new TreeNode { NodeFont = _requestedChangesView.Font };
        var includeSelf = RequestCoversSector(sector, request, direction);

        // See BuildOwnedSectorNodeCore for why this is TryGetValue rather than an indexer.
        if (sector.SubSectors.Count > 0 && depth < 8 && SectorsVolumes.SectorGroupings.TryGetValue(sector, out var children))
        {
            foreach (var child in children)
            {
                if (ReferenceEquals(child, sector))
                {
                    if (includeSelf)
                    {
                        var selfText = LeafText(FormatSectorText(child));
                        node.Nodes.Add(new TreeNode(selfText) { NodeFont = node.NodeFont, ToolTipText = selfText });
                    }

                    continue;
                }

                var childNode = BuildRequestDescendantNode(child, request, direction, depth + 1);
                if (childNode != null)
                    node.Nodes.Add(childNode);
            }
        }

        if (!includeSelf && node.Nodes.Count == 0)
            return null;

        ApplySectorNodeText(node, FormatSectorText(sector));
        return node;
    }

    bool RequestCoversSector(SectorsVolumes.Sector sector, SectorChangeRequest request, RequestListDirection direction)
    {
        if (direction == RequestListDirection.Incoming)
            return _tracker.IsMine(sector);

        return _tracker.ControlledByOthers.TryGetValue(sector.Name, out var owner)
               && string.Equals(owner.Callsign, request.Controller, StringComparison.OrdinalIgnoreCase);
    }

    // Called after every rebuild of Requested Changes, every selection change and every request
    // action, so the two buttons are never enabled for something they would refuse to act on.
    // Accept takes one incoming request or the incoming heading's whole batch. Reject covers both
    // directions (see RejectSelectedRequestAsync), provided a real request is selected - not a
    // placeholder row or a staged one not yet applied.
    void UpdateRequestActionButtons()
    {
        var selected = _requestedChangesView.SelectedNode;
        var requestsActionable = !_requestActionRunning && !_applyRunning && Network.IsConnected;
        var category = CategoryNameOf(selected);

        _acceptButton.Visible = !IsObserver;
        _rejectButton.Visible = !IsObserver;
        _acceptButton.Enabled = requestsActionable && GetRequestsToAccept(selected).Count > 0;
        _rejectButton.Enabled = requestsActionable
                                 && (IsRequestHeading(selected)
                                     ? RequestsUnderHeading(selected!).Count > 0
                                     : FindOwningRequest(selected) != null
                                       && (category == RequestedFromMeName || category == RequestedByMeName));
    }

    // The "Requested From Me" / "Requested By Me" headings themselves, as opposed to a row under
    // one.
    static bool IsRequestHeading(TreeNode? node) =>
        node != null && ReferenceEquals(node.Tag, CategoryTag)
        && (node.Text == RequestedFromMeName || node.Text == RequestedByMeName);

    // The requests already sent that are listed under a heading. Read off the rendered rows, like
    // GetRequestsToAccept, so it acts only on what is on screen: a placeholder row carries no
    // request, and a staged one is tagged with a Sector (Cancel withdraws those).
    static List<SectorChangeRequest> RequestsUnderHeading(TreeNode heading) =>
        heading.Nodes.Cast<TreeNode>()
            .Select(n => n.Tag as SectorChangeRequest)
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();

    // Reject on a heading. Incoming requests go as one batch, so a grouped request is not left
    // half answered. Outgoing ones have no batch call and are cancelled one by one; one failing
    // does not stop the rest.
    async Task RejectAllUnderHeadingAsync(TreeNode heading)
    {
        if (!Network.IsConnected || _requestActionRunning || _applyRunning)
            return;

        var requests = RequestsUnderHeading(heading);
        if (requests.Count == 0)
            return;

        var incoming = heading.Text == RequestedFromMeName;
        _requestActionRunning = true;
        UpdateRequestActionButtons();
        try
        {
            if (incoming)
            {
                var rejected = await _tracker.RejectRequestsBatchAsync(requests.Select(r => r.Id));
                ActionLog.Log("Ownership", $"Rejected all incoming requests: {rejected.Count} of {requests.Count}", new
                {
                    action = "reject_all_incoming",
                    request_ids = requests.Select(r => r.Id).ToList(),
                    rejected
                });
            }
            else
            {
                var failed = new List<string>();
                foreach (var request in requests)
                {
                    try
                    {
                        var result = await _api.CancelRequestAsync(request.Id);
                        await _tracker.ApplyActionResultAsync(result);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{request.Sector.Name} ({ex.Message})");
                    }
                }
                ActionLog.Log("Ownership", $"Cancelled all my requests: {requests.Count - failed.Count} of {requests.Count}", new
                {
                    action = "cancel_all_outgoing",
                    request_ids = requests.Select(r => r.Id).ToList(),
                    failed
                });
                if (failed.Count > 0)
                    Errors.Add(new Exception($"Couldn't cancel: {string.Join(", ", failed)}"), "OzServer");
            }

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

    // What Accept acts on, entirely from the current selection:
    //   - the "Requested From Me" header  -> every incoming request, accepted as one batch
    //   - a request row (or any of the informational sub-sector rows under it) -> just that one
    //   - anything else (an outgoing request, the empty placeholder) -> nothing, Accept greyed out
    static List<SectorChangeRequest> GetRequestsToAccept(TreeNode? selected)
    {
        if (selected == null)
            return new List<SectorChangeRequest>();

        // Read off the rendered nodes rather than _requestsFromMe, so it acts only on what the
        // controller is looking at. The "No incoming requests" placeholder carries no Tag and
        // contributes nothing.
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
    // rows underneath it (see BuildRequestDescendantNode).
    static SectorChangeRequest? FindOwningRequest(TreeNode? node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current.Tag is SectorChangeRequest request)
                return request;
        }

        return null;
    }

    // The name of the category header a node sits under, at any depth - taken from its top-level
    // ancestor.
    static string? CategoryNameOf(TreeNode? node)
    {
        var top = node;
        while (top?.Parent != null)
            top = top.Parent;

        // The Requested headings are not collapsible and carry no Name, so their Text is the
        // heading. A collapsible category's Text has the >/v prefix, so its Name is preferred.
        return top == null ? null : string.IsNullOrEmpty(top.Name) ? top.Text : top.Name;
    }

    // Accepts one or several incoming requests as a single batch (see
    // OzServerOwnershipTracker.AcceptRequestsBatchAsync) rather than one call per request: with
    // separate accepts fired back to back, each one's refresh can still be in flight when the next
    // lands, leaving a request row behind after its sector has moved on.
    async Task AcceptRequestsAsync(List<SectorChangeRequest> requests)
    {
        if (!Network.IsConnected || _requestActionRunning || _applyRunning)
            return;

        _requestActionRunning = true;
        UpdateRequestActionButtons();
        try
        {
            // Accepting transfers ownership away from this controller. The tracker re-derives
            // Owned from the server rather than this window guessing locally.
            var results = await _tracker.AcceptRequestsBatchAsync(requests.Select(r => r.Id));
            UpdateArrowButton();

            var failed = results.Where(r => !r.Accepted).ToList();
            if (failed.Count > 0)
            {
                var summary = string.Join("; ", failed.Select(f => $"{f.Sector ?? $"#{f.RequestId}"}: {f.Message}"));
                Errors.Add(new Exception($"Couldn't accept: {summary}"), "OzServer");
            }

            // AcceptRequestsBatchAsync has applied the state its response carried, so the lists are
            // current when it returns; only the controlled snapshot needs re-deriving.
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
        if (!Network.IsConnected || _requestActionRunning || _applyRunning)
            return;

        _requestActionRunning = true;
        UpdateRequestActionButtons();
        try
        {
            // The response carries the resulting state, so there is no follow-up GET.
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
        if (!Network.IsConnected || _requestActionRunning || _applyRunning)
            return;

        _requestActionRunning = true;
        UpdateRequestActionButtons();
        try
        {
            // The response carries the resulting state, so there is no follow-up GET.
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
