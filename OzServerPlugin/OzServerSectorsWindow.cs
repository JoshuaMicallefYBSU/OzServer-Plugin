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
// sectors nobody's on ("Available") and sectors someone else currently is ("Controlled"), so a
// controller can browse to find who to ask. The arrow button below Requested Changes
// claims/releases/requests depending on what's selected in Owned/Available; Accept/Reject/Cancel
// act on whatever's selected in the Requested Changes tree.
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
    const string CollapsedPrefix = "> ";
    const string ExpandedPrefix = "v ";

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
    // Names from _controlledSnapshot as a set, so the Available filter is a hash lookup per sector
    // rather than a linear scan of the whole response per sector.
    readonly HashSet<string> _controlledNames = new(StringComparer.OrdinalIgnoreCase);
    List<OzServerControlledSectorDto> _controlledSnapshot = new();
    string? _controlledSignature;
    bool _hasControlledSnapshot;
    bool _controlledRefreshRunning;
    Task? _requestedRefreshTask;
    bool _requestedRefreshQueued;
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
    bool _applyRunning;

    // One menu for the window's lifetime, its items rebuilt per click, rather than a fresh
    // ContextMenuStrip each time. The renderer it is given is vatSys's own shared instance (see
    // VatSysContextMenu) - disposing a menu that holds it would take vatSys's built-in ComboField
    // menus down with it, so there must be nothing here that ever gets disposed.
    readonly ContextMenuStrip _nodeMenu = VatSysContextMenu.Create();

    readonly TableLayoutPanel _tableLayoutPanel1;
    readonly TextLabel _currentSectorsLabel;
    readonly TextLabel _requestedChangesLabel;
    readonly GenericButton _applyButton;
    readonly GenericButton _cancelButton;
    readonly GenericButton _arrowButton;
    readonly ToggleGenericButton _availableModeButton;
    readonly ToggleGenericButton _controlledModeButton;
    // Accept/Reject/Cancel are no longer buttons. Every one of them was only ever a second way to
    // invoke what the middle-click menu already offers on the row itself, and each needed its own
    // enable/disable rules kept in step with that menu's. Removing them gives the list the height
    // back and leaves exactly one place those actions live.
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

    public OzServerSectorsWindow(OzServerOwnershipTracker tracker)
    {
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
        _availSectorsView.NodeMouseClick += TreeView_NodeMouseClick;
        _availSectorsView.MouseUp += TreeView_MouseUp;
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
        _currSectorsView.NodeMouseClick += TreeView_NodeMouseClick;
        _currSectorsView.MouseUp += TreeView_MouseUp;
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
        _requestedChangesView.NodeMouseClick += TreeView_NodeMouseClick;
        _requestedChangesView.MouseUp += TreeView_MouseUp;
        _requestedChangesView.MouseWheel += RequestedChangesView_MouseWheel;
        _requestedChangesView.AfterSelect += (_, _) => UpdateRequestActionButtons();
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
            Margin = new Padding(3, 8, 3, 3),
            Name = "arrowButton",
            Size = new Size(80, 30),
            TabIndex = 16,
            Text = ArrowLeft,
        };
        _arrowButton.Click += ArrowButton_Click;

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
            _ = _tracker.RefreshFromServerIfIdleAsync();
            _ = RefreshRequestedChangesAsync();
            // Regardless of mode: Available filters against this too, so it has to stay current
            // even while Controlled isn't the list being shown.
            _ = RefreshControlledSnapshotAsync();
        };

        ConfigureCurrScrollbar();
        ConfigureAvailScrollbar();
        SyncOwnedFromTracker();
        PopulateRequestedChanges();

        _ = _tracker.RefreshFromServerIfIdleAsync();
        _ = RefreshControlledSnapshotAsync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (Visible)
        {
            _pollTimer.Start();
            // Every time the window is opened, not just the first time this session - a nudge for
            // OzServer's own record in case a while has passed since the tracker last refreshed.
            _ = _tracker.RefreshFromServerIfIdleAsync();
            _ = RefreshRequestedChangesAsync();
            _ = RefreshControlledSnapshotAsync();
        }
        else
        {
            _pollTimer.Stop();
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
        RefreshDropdownNodeText(e.Node);

        // _suspendScrollSync as well as _rebuildingTree: this fires from inside
        // ToggleNodeExpansion's BeginUpdate/EndUpdate block, where the control has not relaid out
        // yet, so GetPreferredHeight and GetScrollPos both still describe the tree as it was
        // *before* the expand. Reconfiguring the bar from that is what made expanding a category
        // jump. ToggleNodeExpansion does one sync afterwards instead, once layout has settled.
        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureCurrScrollbar();
        SyncScrollValue(_currSectorsView, _currScrollBar);
    }

    void AvailSectorsView_AfterExpandCollapse(object? sender, TreeViewEventArgs e)
    {
        RefreshDropdownNodeText(e.Node);

        // _suspendScrollSync as well as _rebuildingTree: this fires from inside
        // ToggleNodeExpansion's BeginUpdate/EndUpdate block, where the control has not relaid out
        // yet, so GetPreferredHeight and GetScrollPos both still describe the tree as it was
        // *before* the expand. Reconfiguring the bar from that is what made expanding a category
        // jump. ToggleNodeExpansion does one sync afterwards instead, once layout has settled.
        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureAvailScrollbar();
        SyncScrollValue(_availSectorsView, _availScrollBar);
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
        RefreshDropdownNodeText(e.Node);

        // _suspendScrollSync as well as _rebuildingTree: this fires from inside
        // ToggleNodeExpansion's BeginUpdate/EndUpdate block, where the control has not relaid out
        // yet, so GetPreferredHeight and GetScrollPos both still describe the tree as it was
        // *before* the expand. Reconfiguring the bar from that is what made expanding a category
        // jump. ToggleNodeExpansion does one sync afterwards instead, once layout has settled.
        if (_rebuildingTree || _suspendScrollSync)
            return;

        ConfigureRequestedScrollbar();
        SyncScrollValue(_requestedChangesView, _requestedScrollBar);
    }

    // A left click only selects a row. Expand/collapse is no longer a mouse button of its own -
    // it, and every action the window can perform on a row, is a command on the middle-click menu
    // (see ShowNodeContextMenu). Keeping expansion off left click is what stops a claimable primary
    // sector reflowing the list out from under the pointer at the moment it is being selected.
    void TreeView_BeforeMouseExpandCollapse(object? sender, TreeViewCancelEventArgs e)
    {
        // TreeView's native left-button double-click toggles a node even with ShowPlusMinus=false.
        // TreeViewCancelEventArgs does not distinguish that native toggle from a direct
        // TreeNode.Expand/Collapse call, so explicitly allow only the menu's own Expand/Collapse
        // command and the programmatic expansion-state restore performed during a rebuild.
        if (!_allowTreeToggle && !_rebuildingTree)
            e.Cancel = true;
    }

    void TreeView_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        var treeView = (TreeViewEx)sender!;
        treeView.SelectedNode = e.Node;

        // The category headers - the position types (Approach/Centre/Tower/...) in Owned and
        // Available, and Requested By/From Me - are pure grouping rows with nothing to claim,
        // release or accept, so a left click on one has no other job to do and opening the group is
        // the only thing it could sensibly mean. Everything else keeps expansion on the middle-click
        // menu: those rows are selectable targets first, and reflowing the list underneath the
        // pointer as one is selected is the behaviour this window was moved away from.
        if (IsCategoryNode(e.Node))
            ToggleNodeExpansion(treeView, e.Node);
    }

    static bool IsCategoryNode(TreeNode node) => ReferenceEquals(node.Tag, CategoryTag);

    // Middle click deliberately comes off MouseUp rather than NodeMouseClick. NodeMouseClick is
    // raised from the native tree control's NM_CLICK/NM_RCLICK notifications, and WinForms derives
    // its Button purely from which of those two arrived - so it only ever reports Left or Right and
    // a middle click would never reach the menu at all. MouseUp is a plain Control-level message and
    // sees all three buttons; the node is recovered by hit-testing the click point.
    void TreeView_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Middle)
            return;

        var treeView = (TreeViewEx)sender!;
        var node = treeView.GetNodeAt(e.Location);
        if (node == null)
            return;

        // Select first: every command the menu offers derives from the selection rather than being
        // handed the node separately - Accept's "one row or the whole category" split (see
        // GetRequestsToAccept) most of all - so the clicked row has to *be* the selection for the
        // menu to describe what it will actually do.
        treeView.SelectedNode = node;
        ShowNodeContextMenu(treeView, node, e.Location);
    }

    // The middle-click menu: every command that applies to a row, in one place, on whichever of the
    // three trees was clicked. Commands that don't apply to this row are shown greyed rather than
    // dropped, so the menu keeps one shape and one item order to aim at regardless of what was
    // clicked - a menu that grows and shrinks per row is far harder to use without looking.
    void ShowNodeContextMenu(TreeViewEx treeView, TreeNode node, Point location)
    {
        var menu = _nodeMenu;

        // Rebuilt rather than recreated. Items.Clear() does not dispose what it removes and each
        // rebuild allocates a fresh set, so the old ones are disposed explicitly - but the menu
        // itself never is, because it holds vatSys's shared renderer (see the _nodeMenu field).
        var previousItems = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var item in previousItems)
            item.Dispose();

        VatSysContextMenu.ApplyRenderer(menu);

        menu.Items.Add(VatSysContextMenu.CreateHeader(HeaderTextFor(node)));
        menu.Items.Add(new ToolStripSeparator());

        var expandCollapse = new ToolStripMenuItem("Expand/Collapse") { Enabled = node.Nodes.Count > 0 };
        expandCollapse.Click += (_, _) => ToggleNodeExpansion(treeView, node);
        menu.Items.Add(expandCollapse);
        menu.Items.Add(new ToolStripSeparator());

        // Request commands read the same state the Accept/Reject buttons do, so a row that the
        // buttons would refuse to act on greys out here for the identical reason.
        //
        // Gated on the clicked tree being Requested Changes, because both commands resolve what
        // they act on from _requestedChangesView's *selection*, not from the node clicked here (see
        // GetRequestsToAccept). Middle-clicking a row in Owned or Available leaves that selection
        // untouched, so without this an unrelated request left selected in the other panel would
        // show as Accept-able from a sector row - and accepting it would hand away a sector the
        // controller was not even looking at.
        var request = FindOwningRequest(node);
        var requestsActionable = ReferenceEquals(treeView, _requestedChangesView)
                                 && !_requestActionRunning
                                 && Network.IsConnected;

        var accept = new ToolStripMenuItem("Accept")
        {
            Enabled = requestsActionable && GetRequestsToAccept().Count > 0
        };
        accept.Click += (_, _) => _ = AcceptSelectedRequestsAsync();
        menu.Items.Add(accept);

        var reject = new ToolStripMenuItem("Reject")
        {
            Enabled = requestsActionable && request != null && CategoryNameOf(node) == RequestedFromMeName
        };
        reject.Click += (_, _) => _ = RejectSelectedRequestAsync();
        menu.Items.Add(reject);
        menu.Items.Add(new ToolStripSeparator());

        // Add/Remove are this menu's wording for the same staged move the arrow button makes, and
        // work from any of the three trees: a request row resolves to the sector it is about, so an
        // incoming request can be staged without first hunting that sector down in Available.
        //
        // Not gated on being connected: staging is local, and Apply is the only thing that needs the
        // network. Only an Apply already in flight blocks it, so the selection being committed can't
        // move underneath it.
        var sector = SectorForNode(node);
        var owned = sector != null && IsOwned(sector);
        var sectorActionable = sector != null && !_applyRunning;

        var add = new ToolStripMenuItem("Add") { Enabled = sectorActionable && !owned };
        add.Click += (_, _) => RunSectorAction(sector!, add: true);
        menu.Items.Add(add);

        var remove = new ToolStripMenuItem("Remove") { Enabled = sectorActionable && owned };
        remove.Click += (_, _) => RunSectorAction(sector!, add: false);
        menu.Items.Add(remove);

        menu.Show(treeView, location);
    }

    // What the menu is acting on, as its title row. Category headers carry the plain name in Name
    // (Text has the >/v prefix bolted on); sector rows carry it in Text, with the trailing "*" that
    // marks "bundles sub-sectors" trimmed off - it is list notation, not part of the sector's name.
    static string HeaderTextFor(TreeNode node) =>
        string.IsNullOrEmpty(node.Name) ? node.Text.TrimEnd('*') : node.Name;

    // The sector a row is about, whichever tree it came from: Owned and Available rows are tagged
    // with the sector itself, Requested Changes rows with the request, whose Sector is the subject.
    static SectorsVolumes.Sector? SectorForNode(TreeNode node) => node.Tag switch
    {
        SectorsVolumes.Sector sector => sector,
        SectorChangeRequest request => request.Sector,
        _ => FindOwningRequest(node)?.Sector
    };

    // Name comparison rather than Contains - the same footing vatsys.SectorsWindow's own
    // available-list filter uses (see PopulateAvailableList).
    bool IsOwned(SectorsVolumes.Sector sector) =>
        _sectorsSelected.Any(s => !s.IsDummy && s.Name == sector.Name);

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
            if (node.IsExpanded)
                node.Collapse();
            else
                node.Expand();
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

    // Every node - group header or leaf - reads dark blue at rest and light blue when selected,
    // with no other status-based colouring anywhere in the tree.
    void SectorsView_DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        var treeView = (TreeViewEx)sender!;
        var selected = (e.State & TreeNodeStates.Selected) != 0;

        // A staged row - moved across but not yet committed by Apply - is drawn in the profile's
        // own WindowWarning identity rather than a hardcoded yellow. That is exactly the role
        // ("warning indications in windows"), it is BrightYellow in the Australia profile, and it
        // follows the loaded profile the way every other colour in this window does. Selection still
        // wins over it, so a staged row the controller is pointing at still reads as selected.
        var color = selected
            ? Colours.GetColour(Colours.Identities.HighlightedText)
            : IsStagedNode(e.Node)
                ? Colours.GetColour(Colours.Identities.WindowWarning)
                : Colours.GetColour(Colours.Identities.InteractiveText);

        // DrawMode is OwnerDrawText, so the system has already filled the row with the control's
        // BackColor before this runs - an ordinary row has nothing to erase and only needs its text.
        //
        // Only a selected row does: the system paints its highlight block there, and this window
        // shows selection as coloured text on the normal background instead. Clearing every row
        // regardless meant allocating and disposing two GDI+ Regions per row per repaint (Clip's
        // getter allocates a fresh Region, it is not a borrowed reference) for no visible
        // difference on the vast majority of them.
        if (selected)
        {
            using var previousClip = e.Graphics.Clip;

            using (var clip = new Region(e.Bounds))
                e.Graphics.Clip = clip;

            e.Graphics.Clear(treeView.BackColor);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont, e.Node.Bounds, color);

            e.Graphics.Clip = previousClip;
            return;
        }

        TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont, e.Node.Bounds, color);
    }

    // Group headers (Approach/Centre/.../Requested By Me/...) get the >/v expand-collapse prefix
    // (see RefreshDropdownNodeText below) since ShowPlusMinus is off. Primary-position sectors
    // that bundle their own sub-sectors (e.g. AAE, TBD) instead get a trailing "*" - matching
    // vatsys.SectorsWindow's own convention for this case - and no dynamic refresh, since a plain
    // suffix doesn't need to track expand state the way the header prefix does.
    static void ApplySectorNodeText(TreeNode node, string baseText)
    {
        node.Text = node.Nodes.Count > 0 ? baseText + "*" : baseText;
        node.ToolTipText = node.Text;
    }

    static void RefreshDropdownNodeText(TreeNode node)
    {
        if (string.IsNullOrEmpty(node.Name))
            return;

        node.Text = (node.IsExpanded ? ExpandedPrefix : CollapsedPrefix) + node.Name;
        node.ToolTipText = node.Text;
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
        public string? SelectedKey;
        public int ScrollValue;
    }

    static TreeViewState CaptureTreeState(TreeViewEx view, ScrollBar scrollBar)
    {
        var state = new TreeViewState { ScrollValue = scrollBar.Value };
        var selected = view.SelectedNode;
        CaptureExpanded(view.Nodes, "", state, selected);
        return state;
    }

    // Descends only into branches that are open. A collapsed branch cannot contain an expanded node
    // by definition, so walking it was pure waste - and it is where nearly all the nodes live.
    // The selected node's key is picked up on the way past rather than rebuilt from scratch.
    static void CaptureExpanded(TreeNodeCollection nodes, string parentKey, TreeViewState state, TreeNode? selected)
    {
        foreach (TreeNode node in nodes)
        {
            var key = ChildKey(parentKey, node);

            if (ReferenceEquals(node, selected))
                state.SelectedKey = key;

            if (!node.IsExpanded)
                continue;

            state.ExpandedKeys.Add(key);
            CaptureExpanded(node.Nodes, key, state, selected);
        }
    }

    // Re-expands whatever was open before the rebuild and re-selects the same logical item if it
    // still exists post-refresh - so a poll tick (every 10s) can't collapse an open dropdown or
    // move the selection out from under the controller mid-action. Must run inside the caller's
    // BeginUpdate/EndUpdate.
    static void RestoreExpandedAndSelection(TreeViewEx view, TreeViewState state)
    {
        TreeNode? selected = null;

        void Walk(TreeNodeCollection nodes, string parentKey)
        {
            foreach (TreeNode node in nodes)
            {
                var key = ChildKey(parentKey, node);

                if (key == state.SelectedKey)
                    selected = node;

                // Nothing below a branch that was closed can have been open either, so there is no
                // reason to descend into it looking for one.
                if (!state.ExpandedKeys.Contains(key))
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
        // Guarded for the same reason as SyncScrollValue: this sets the tree's position itself on
        // the next line, and letting the bar's own Scroll handler do it too scrolled it twice.
        SetScrollBarValue(scrollBar, state.ScrollValue);
        view.SetScrollPosVert((state.ScrollValue + itemHeight - 1) / itemHeight);
    }

    // Maps the tree's current row offset back to a scrollbar value, the inverse of RestoreScroll's
    // value-to-row conversion: pos*h - h + 1 is the smallest value that rounds back to the same row.
    //
    // Clamped at zero, which the raw expression is not - at the top of the list (row 0) it evaluates
    // to 1 - ItemHeight, i.e. negative, and assigning that produced exactly the jump-to-nowhere seen
    // when collapsing a group while scrolled to the top.
    void SyncScrollValue(TreeViewEx view, ScrollBar scrollBar)
    {
        var itemHeight = Math.Max(view.ItemHeight, 1);
        var value = Math.Max(0, view.GetScrollPos().Y * itemHeight - itemHeight + 1);
        if (scrollBar.Value == value)
            return;

        SetScrollBarValue(scrollBar, value);
    }

    // The bar raises Scroll for a value assigned from code exactly as it does for a drag, and its
    // handler pushes that position straight back into the tree - so syncing the bar after an expand
    // scrolled the tree a second time and repainted it again. The flag makes the assignment
    // one-directional: bar follows tree here, tree follows bar only for real user scrolling.
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

    void CurrScrollBar_Scroll(object? sender, EventArgs e)
    {
        if (_syncingScrollBar)
            return;

        _currSectorsView.SetScrollPosVert((_currScrollBar.Value + _currSectorsView.ItemHeight - 1) / _currSectorsView.ItemHeight);
    }

    // What TreeViewEx.GetPreferredHeight() computes, without the cost of computing it.
    //
    // Its MeasureHeight walks *every* node in the tree - collapsed branches included, since it
    // recurses on Nodes.Count rather than on expansion - and reads TreeNode.Bounds for each one.
    // Bounds is not a managed value: it round-trips to the native control (TVM_GETITEMRECT) per
    // node, and returns an empty rectangle for anything not currently visible. So the result is
    // simply the visible rows' combined height, arrived at via one SendMessage for every node in
    // the dataset - several hundred of them here.
    //
    // This is called from ConfigureXScrollbar, which runs on every expand, every collapse and every
    // tree rebuild. That synchronous P/Invoke storm between the expand and the repaint is what made
    // opening a category look like the list was rebuilding rather than unfolding.
    //
    // Same answer, walked in managed code, and only descending into branches that are actually
    // open - a collapsed tree costs a handful of checks instead of hundreds of messages.
    static int VisibleContentHeight(TreeViewEx view) =>
        CountVisibleNodes(view.Nodes) * view.ItemHeight;

    static int CountVisibleNodes(TreeNodeCollection nodes)
    {
        var count = 0;
        foreach (TreeNode node in nodes)
        {
            count++;
            if (node.IsExpanded)
                count += CountVisibleNodes(node.Nodes);
        }

        return count;
    }

    void ConfigureCurrScrollbar()
    {
        _currScrollBar.PreferredHeight = VisibleContentHeight(_currSectorsView);
        _currScrollBar.ActualHeight = _currSectorsView.Height;
        _currScrollBar.Change = _currSectorsView.ItemHeight;
    }

    void AvailScrollBar_Scroll(object? sender, EventArgs e)
    {
        if (_syncingScrollBar)
            return;

        _availSectorsView.SetScrollPosVert((_availScrollBar.Value + _availSectorsView.ItemHeight - 1) / _availSectorsView.ItemHeight);
    }

    void ConfigureAvailScrollbar()
    {
        _availScrollBar.PreferredHeight = VisibleContentHeight(_availSectorsView);
        _availScrollBar.ActualHeight = _availSectorsView.Height;
        _availScrollBar.Change = _availSectorsView.ItemHeight;
    }

    void RequestedScrollBar_Scroll(object? sender, EventArgs e)
    {
        if (_syncingScrollBar)
            return;

        _requestedChangesView.SetScrollPosVert((_requestedScrollBar.Value + _requestedChangesView.ItemHeight - 1) / _requestedChangesView.ItemHeight);
    }

    void ConfigureRequestedScrollbar()
    {
        _requestedScrollBar.PreferredHeight = VisibleContentHeight(_requestedChangesView);
        _requestedScrollBar.ActualHeight = _requestedChangesView.Height;
        _requestedScrollBar.Change = _requestedChangesView.ItemHeight;
    }

    void UpdateArrowButton()
    {
        var ownedSelected = _currSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector;
        var availSelected = _availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector;

        _arrowButton.Text = ownedSelected ? ArrowRight : ArrowLeft;
        _arrowButton.Enabled = !_applyRunning && (ownedSelected || availSelected);
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
        _ = _tracker.RefreshFromServerIfIdleAsync();
        _ = RefreshRequestedChangesAsync();
        _ = RefreshControlledSnapshotAsync();
    }

    void RefreshStagedHighlight()
    {
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
        _ = RefreshControlledSnapshotAsync();
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
                _currSectorsView.Nodes.Clear();
                AddNodesGroupedByCategory(_currSectorsView, sectorNodes);

                // Every group starts collapsed, including the very first populate - this used to
                // ExpandAll() on first open, which meant the window came up fully unfolded and the
                // controller had to close everything by hand. Nodes are built collapsed, so there is
                // nothing to do here beyond restoring whatever they had opened themselves.
                RestoreExpandedAndSelection(_currSectorsView, state);
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

    // Recurses through a grouping sector's own sub-sectors (e.g. TBD > AAE > AAW/AAR) - always
    // shown regardless of whether those sub-sectors are also independent _sectorsSelected entries,
    // since a grouping sector owns them outright. Every level that ends up with children gets its
    // own "*" treatment (see ApplySectorNodeText), not just the outermost one.
    TreeNode BuildOwnedSectorNode(SectorsVolumes.Sector sector, int depth = 0)
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
                node.Nodes.Add(ReferenceEquals(child, sector)
                    ? new TreeNode(FormatSectorText(child)) { Tag = child, NodeFont = node.NodeFont, ToolTipText = FormatSectorText(child) }
                    : BuildOwnedSectorNode(child, depth + 1));
            }
        }

        ApplySectorNodeText(node, FormatSectorText(sector));
        return node;
    }

    // Available and Controlled are two genuinely different data sources, not just a filter over
    // the same one: Available is "nobody's live on this frequency right now" (checked locally,
    // synchronously, against the live VATSIM feed - see TryMatchAvailable), because that's a
    // meaningful thing to know even for a sector OzServer's database has never heard of. Controlled
    // is specifically "OzServer has an active ownership record for this, owned by someone else"
    // (see PopulateControlledListAsync) - a sector some stray callsign is logged into on VATSIM
    // but that was never actually claimed through here correctly does *not* show up as Controlled.
    void PopulateAvailableList()
    {
        if (_sectorListMode == SectorListMode.Controlled)
        {
            RenderControlledList();
            return;
        }

        // Once for the whole pass, not once per sector - see FindController.
        RefreshOnlineControllerIndex();

        var sectorNodes = new List<TreeNode>();
        foreach (var key in SectorsVolumes.Sectors.Where(s =>
                     s.CSECEligible && !_sectorsSelected.Any(ss => !ss.IsDummy && s.Name == ss.Name)))
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

        var sectorNodes = new List<TreeNode>();
        foreach (var dto in _controlledSnapshot)
        {
            // Not something this vatSys install's Sectors.xml even has a definition for - nothing
            // sensible to show or act on, so it's skipped rather than shown as a dead entry.
            var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Name == dto.Name);
            if (sector == null)
                continue;

            var text = string.IsNullOrEmpty(dto.Owner?.Callsign)
                ? FormatSectorText(sector)
                : $"{sector.Name} - {sector.FullName} ({dto.Owner!.Callsign})";
            sectorNodes.Add(new TreeNode(text) { Tag = sector, NodeFont = _availSectorsView.Font, ToolTipText = text });
        }

        ApplyAvailableSectorNodes(sectorNodes, ControlledModePrefix);
    }

    // The one fetch of "who owns what" that both lists read. It backs Controlled directly, and
    // Available filters against it (see IsOwnedByAnotherController) so a sector another controller
    // holds is never offered as claimable - which is what makes Apply's claim-or-request split
    // predictable instead of a surprise.
    //
    // Refreshed on the window's own poll regardless of which mode is showing, so switching to
    // Controlled has an answer ready rather than starting a request the controller waits on.
    async Task RefreshControlledSnapshotAsync()
    {
        if (!Network.IsConnected || _controlledRefreshRunning)
            return;

        _controlledRefreshRunning = true;
        try
        {
            var controlled = await _api.GetControlledSectorsAsync();

            if (IsDisposed || !IsHandleCreated)
                return;

            var signature = ControlledSignature(controlled);
            var firstAnswer = !_hasControlledSnapshot;

            _controlledSnapshot = controlled;
            _controlledNames.Clear();
            foreach (var dto in controlled)
                _controlledNames.Add(dto.Name);
            _hasControlledSnapshot = true;

            // Only re-render when the answer actually changed. This runs on every poll tick, and
            // rebuilding the node tree each time is what made switching modes stutter: the switch
            // drew from cache, then this landed a moment later and redrew the identical list on top
            // of it. Ownership rarely changes between ticks, so in the normal case this now does
            // nothing at all.
            if (firstAnswer || signature != _controlledSignature)
            {
                _controlledSignature = signature;

                // Both lists depend on this, not just Controlled - Available's claimable set
                // changes whenever somebody else's ownership does.
                PopulateAvailableList();
            }
        }
        catch (Exception ex)
        {
            // Deliberately fire-and-forget from the poll, so nothing escapes as an unobserved task
            // exception. The previous snapshot stays up rather than blanking the list on a blip.
            if (!IsDisposed && IsHandleCreated)
                Errors.Add(new Exception($"Couldn't refresh controlled sectors: {ex.Message}", ex), "OzServer");
        }
        finally
        {
            _controlledRefreshRunning = false;
        }
    }

    // Sector name plus owner, so a handover between two controllers counts as a change even though
    // the set of controlled sectors did not move. Ordered, because the endpoint makes no promise
    // about row order and an unstable fingerprint would defeat the whole point of comparing it.
    static string ControlledSignature(IEnumerable<OzServerControlledSectorDto> controlled) =>
        string.Join("|", controlled
            .Select(dto => dto.Name + ">" + (dto.Owner?.Callsign ?? ""))
            .OrderBy(v => v, StringComparer.Ordinal));

    // Whether OzServer records this sector as someone else's right now. Distinct from the live
    // VATSIM presence test in TryMatchAvailable, and the reason both are needed: a controller who
    // reached a sector by extending into it is not logged in under that sector's callsign at all, so
    // presence alone never sees them and the sector looked claimable when it was not.
    bool IsOwnedByAnotherController(SectorsVolumes.Sector sector) =>
        _controlledNames.Contains(sector.Name);

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

    // A single untagged row explaining why the list is empty. Untagged deliberately: SectorForNode
    // and the arrow button both key off the tag, so a placeholder can never be selected into
    // something claimable the way a real row can.
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

    // Recurses through a sector's own sub-sectors so a primary position nested at any depth (e.g.
    // AAE inside TBD, or AAW/AAR inside AAE) still gets checked against live VATSIM presence and
    // gets its own dropdown treatment. Returns null if this sector doesn't belong in Available at
    // all (already owned, or someone's live on it).
    TreeNode? BuildAvailableSectorNode(SectorsVolumes.Sector sector, int depth = 0)
    {
        // Three separate reasons a sector is not claimable, and all three have to be checked: it is
        // already this controller's, somebody is live on its frequency, or OzServer records it as
        // another controller's. The last one is what an extending controller looks like - they hold
        // it without ever being logged in under its callsign - and missing it made Available offer
        // sectors whose claim could only ever come back as a request.
        if (_sectorsSelected.Contains(sector) || IsOwnedByAnotherController(sector)
            || !TryMatchAvailable(sector, out var controller))
            return null;

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
                    var text = FormatSectorText(child, controller);
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

    // Whether nobody's currently live on this sector's frequency on VATSIM.
    bool TryMatchAvailable(SectorsVolumes.Sector sector, out NetworkATC? controller)
    {
        controller = FindController(sector);

        // Still mine on the network even if I've locally unpicked it and haven't hit Apply yet -
        // don't show it as something to browse/request either way.
        if (controller != null && controller.Callsign == Network.Callsign)
            return false;

        return controller == null;
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

        foreach (var atc in Network.GetOnlineATCs ?? new List<NetworkATC>())
        {
            if (atc.ValidATC && !string.IsNullOrEmpty(atc.Callsign))
                _onlineByCallsign[atc.Callsign] = atc;
        }
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

    static TreeNode CreateCategoryNode(TreeViewEx view, string name)
    {
        var node = new TreeNode(CollapsedPrefix + name)
        {
            Tag = CategoryTag,
            Name = name,
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
        var callsign = sector.Callsign;
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
            var result = await _tracker.CommitSectorChangesAsync(toClaim, toRelease);

            // Staged requests are sent after the claims and releases: a sector freed by one of those
            // releases might be exactly what somebody is being asked for, and asking first would
            // race it.
            foreach (var sector in stagedRequests)
            {
                try
                {
                    await _tracker.RequestAsync(sector);
                    result.Requested.Add(sector.Name);
                }
                catch (Exception ex)
                {
                    result.Failed.Add(sector.Name);
                    Errors.Add(new Exception($"Couldn't request {sector.Name}: {ex.Message}", ex), "OzServer");
                }
            }

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

        if (result.Requested.Count > 0)
        {
            var names = string.Join(", ", result.Requested.Distinct());
            parts.Add(result.Requested.Count == 1
                ? $"{names} is owned by another controller, so a request has been sent to them."
                : $"These are owned by other controllers, so requests have been sent: {names}");
        }

        // Reported rather than silently dropped: these are sub-sectors of something that *was*
        // claimed, so without saying so the controller sees a claim succeed and has no idea part of
        // the group stayed behind. They are not requested automatically - staging them is how you
        // ask for them.
        if (result.Skipped.Count > 0)
        {
            var names = string.Join(", ", result.Skipped.Distinct());
            parts.Add(result.Skipped.Count == 1
                ? $"{names} is already owned by another controller and was left with them. Move it across on its own to request it."
                : $"These are already owned by other controllers and were left with them: {names}. Move one across on its own to request it.");
        }

        if (parts.Count == 0)
            return;

        ShowNotice(string.Join(Environment.NewLine + Environment.NewLine, parts), "Sector changes applied");
    }

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
    void CancelButton_Click(object? sender, EventArgs e)
    {
        if (_applyRunning)
            return;

        _stagedNames.Clear();
        _stagedRequests.Clear();
        SyncOwnedFromTracker();
        PopulateLists();
        PopulateRequestedChanges();
        RefreshStagedHighlight();
        UpdateArrowButton();
    }

    void AvailSectorsView_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node != null)
            _currSectorsView.SelectedNode = null;
        UpdateArrowButton();
    }

    void CurrSectorsView_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node != null)
            _availSectorsView.SelectedNode = null;
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

    // Shared by the Accept button and the menu's Accept, so both act on the identical selection
    // rule (see GetRequestsToAccept) rather than drifting apart.
    async Task AcceptSelectedRequestsAsync()
    {
        try
        {
            var requests = GetRequestsToAccept();
            if (requests.Count > 0)
                await AcceptRequestsAsync(requests);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't accept that sector request: {ex.Message}", ex), "OzServer");
            UpdateRequestActionButtons();
        }
    }

    async Task RejectSelectedRequestAsync()
    {
        try
        {
            // FindOwningRequest, not a direct Tag test - a request that bundles sub-sectors
            // renders them as rows underneath it, and clicking one of those means that request.
            var request = FindOwningRequest(_requestedChangesView.SelectedNode);
            if (request != null && CategoryNameOf(_requestedChangesView.SelectedNode) == RequestedFromMeName)
                await RejectRequestAsync(request);
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

        PopulateLists();
        RefreshStagedHighlight();
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
    Task RefreshRequestedChangesAsync()
    {
        if (!Network.IsConnected)
            return Task.CompletedTask;

        _requestedRefreshQueued = true;
        return _requestedRefreshTask ??= RunRequestedChangesRefreshLoopAsync();
    }

    async Task RunRequestedChangesRefreshLoopAsync()
    {
        // Ensure RefreshRequestedChangesAsync assigns _requestedRefreshTask before this runner can
        // reach its finally block, even if the API fails/completes synchronously.
        await Task.Yield();

        try
        {
            do
            {
                _requestedRefreshQueued = false;
                await RefreshRequestedChangesOnceAsync();
            }
            while (_requestedRefreshQueued && Network.IsConnected);
        }
        catch (Exception ex)
        {
            // The timer/open paths deliberately do not await this task, so contain any unexpected
            // rendering/lifecycle failure here rather than leaking an unobserved exception.
            Errors.Add(new Exception($"Couldn't refresh sector requests: {ex.Message}", ex), "OzServer");
        }
        finally
        {
            _requestedRefreshTask = null;
            _requestedRefreshQueued = false;
        }
    }

    async Task RefreshRequestedChangesOnceAsync()
    {
        try
        {
            var response = await _api.GetMyRequestsAsync();

            if (IsDisposed || !IsHandleCreated)
                return;

            // A rejected request comes back in by_me exactly once per rejection, purely so this
            // controller can be told (see OzServerSectorOwnershipRequestDto.RejectedAt). It is not a
            // pending request and must not be rendered as one - it is already decided, and Cancel
            // would have nothing to act on.
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
        }
        catch (Exception ex)
        {
            if (!IsDisposed && IsHandleCreated)
                Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }

        if (IsDisposed || !IsHandleCreated)
            return;

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

            var sectorName = dto.Sector?.Name ?? "That sector";
            var by = string.IsNullOrEmpty(dto.TargetCallsign) ? "the controller" : dto.TargetCallsign;
            ShowNotice($"{by} denied your request for {sectorName}.", "Request denied");

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
        var byMeNode = CreateCategoryNode(_requestedChangesView, RequestedByMeName);
        AddRequestNodes(byMeNode, _requestsByMe, NoRequestsByMe, _stagedRequests);

        var fromMeNode = CreateCategoryNode(_requestedChangesView, RequestedFromMeName);
        AddRequestNodes(fromMeNode, _requestsFromMe, NoRequestsFromMe);

        var rootNodes = new[] { byMeNode, fromMeNode };
        var signature = "requested|" + TreeSignature(rootNodes);
        if (signature == _requestedTreeSignature)
        {
            UpdateRequestActionButtons();
            return;
        }

        var state = CaptureTreeState(_requestedChangesView, _requestedScrollBar);

        _rebuildingTree = true;
        try
        {
            _requestedChangesView.BeginUpdate();
            try
            {
                _requestedChangesView.Nodes.Clear();
                _requestedChangesView.Nodes.AddRange(rootNodes);
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
                var text = FormatSectorText(sector);
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
                    ? new TreeNode(FormatSectorText(child)) { NodeFont = node.NodeFont, ToolTipText = FormatSectorText(child) }
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
                    ? new TreeNode(FormatSectorText(child)) { NodeFont = node.NodeFont, ToolTipText = FormatSectorText(child) }
                    : BuildRequestDescendantNode(child, depth + 1));
            }
        }

        ApplySectorNodeText(node, FormatSectorText(sector));
        return node;
    }

    // Kept as the single "request state changed" notification point even though there are no
    // buttons left to update: the middle-click menu decides what it offers when it is opened, so
    // there is nothing to pre-enable, but plenty of callers still want to say "this changed".
    void UpdateRequestActionButtons()
    {
    }

    // What Accept acts on, entirely from the current selection:
    //   - the "Requested From Me" header  -> every incoming request, accepted as one batch
    //   - a request row (or any of the informational sub-sector rows under it) -> just that one
    //   - anything else (an outgoing request, the empty placeholder) -> nothing, Accept greyed out
    List<SectorChangeRequest> GetRequestsToAccept()
    {
        var selected = _requestedChangesView.SelectedNode;
        if (selected == null)
            return new List<SectorChangeRequest>();

        // Selecting the header is the "accept everything incoming" gesture the checkbox cascade
        // used to provide. Read off the rendered nodes rather than _requestsFromMe so it can only
        // ever act on what the controller is actually looking at - notably, the "No incoming
        // requests" placeholder carries no Tag and so contributes nothing.
        if (ReferenceEquals(selected.Tag, CategoryTag) && selected.Name == RequestedFromMeName)
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

        return top?.Name;
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

            await RefreshRequestedChangesAsync();
            RefreshAllListsAsync();
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
            await _api.RejectRequestAsync(request.Id);
            await RefreshRequestedChangesAsync();
            RefreshAllListsAsync();
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
            await _api.CancelRequestAsync(request.Id);
            await RefreshRequestedChangesAsync();
            RefreshAllListsAsync();
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
