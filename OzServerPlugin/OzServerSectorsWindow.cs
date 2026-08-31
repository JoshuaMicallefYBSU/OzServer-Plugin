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
    SectorListMode _sectorListMode = SectorListMode.Available;
    readonly OzServerApiClient _api = new();
    readonly OzServerOwnershipTracker _tracker;
    readonly System.Windows.Forms.Timer _pollTimer;
    bool _ownedFirstPopulate = true;
    bool _hasOwnedSnapshot;
    // RestoreExpandedAndSelection calls Expand while a Populate* method is rebuilding a tree.
    // TreeView raises AfterExpand for those programmatic restores too, so suppress the scrollbar
    // side effects until the rebuild has put the original state back in full.
    bool _rebuildingTree;
    bool _allowTreeToggle;
    // Avoid clearing and recreating an unchanged tree. Owned is refreshed every ten seconds even
    // when it has not changed, Available also reacts to every network-controller change, and
    // Requested Changes is polled, so an unconditional rebuild makes the rows visibly twitch.
    string? _ownedTreeSignature;
    string? _availableTreeSignature;
    string? _requestedTreeSignature;
    int _controlledRefreshVersion;
    bool _controlledRefreshRunning;
    bool _controlledRefreshQueued;
    Task? _requestedRefreshTask;
    bool _requestedRefreshQueued;
    bool _requestActionRunning;
    bool _sectorActionRunning;

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
    readonly GenericButton _acceptButton;
    readonly GenericButton _rejectButton;
    readonly GenericButton _cancelRequestButton;
    readonly FlowLayoutPanel _sectorListModePanel;
    readonly FlowLayoutPanel _currSectorsFlowPanel;
    readonly FlowLayoutPanel _addRemoveLayoutPanel;
    readonly FlowLayoutPanel _requestedListRow;
    readonly FlowLayoutPanel _requestActionsPanel;
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
            Size = new Size(265, 217),
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
            Size = new Size(270, 224),
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
            Size = new Size(20, 224),
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
            Size = new Size(298, 230),
            TabIndex = 20
        };
        _requestedListRow.Controls.Add(_requestedInsetPanel);
        _requestedListRow.Controls.Add(_requestedScrollBar);

        _acceptButton = CreateRequestActionButton("Accept");
        // Guarded for the same reason as ArrowButton_Click - these lambdas are async void.
        _acceptButton.Click += async (_, _) => await AcceptSelectedRequestsAsync();

        _rejectButton = CreateRequestActionButton("Reject");
        _rejectButton.Click += async (_, _) => await RejectSelectedRequestAsync();

        _cancelRequestButton = CreateRequestActionButton("Cancel");
        _cancelRequestButton.Click += async (_, _) =>
        {
            try
            {
                var request = FindOwningRequest(_requestedChangesView.SelectedNode);
                if (request != null && CategoryNameOf(_requestedChangesView.SelectedNode) == RequestedByMeName)
                    await CancelRequestAsync(request);
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Couldn't cancel that sector request: {ex.Message}", ex), "OzServer");
                UpdateRequestActionButtons();
            }
        };

        _requestActionsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
            Name = "requestActionsPanel"
        };
        _requestActionsPanel.Controls.Add(_acceptButton);
        _requestActionsPanel.Controls.Add(_rejectButton);
        _requestActionsPanel.Controls.Add(_cancelRequestButton);

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
        _requestedChangesPanel.Controls.Add(_requestActionsPanel);
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
        _pollTimer = new System.Windows.Forms.Timer { Interval = 10000 };
        _pollTimer.Tick += (_, _) =>
        {
            _ = _tracker.RefreshFromServerIfIdleAsync();
            _ = RefreshRequestedChangesAsync();
            if (_sectorListMode == SectorListMode.Controlled)
                PopulateAvailableList();
        };

        ConfigureCurrScrollbar();
        ConfigureAvailScrollbar();
        SyncOwnedFromTracker();
        PopulateRequestedChanges();

        _ = _tracker.RefreshFromServerIfIdleAsync();
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
            if (_sectorListMode == SectorListMode.Controlled)
                PopulateAvailableList();
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

        if (_rebuildingTree)
            return;

        ConfigureCurrScrollbar();
        _currScrollBar.Value = _currSectorsView.GetScrollPos().Y * _currSectorsView.ItemHeight - _currSectorsView.ItemHeight + 1;
    }

    void AvailSectorsView_AfterExpandCollapse(object? sender, TreeViewEventArgs e)
    {
        RefreshDropdownNodeText(e.Node);

        if (_rebuildingTree)
            return;

        ConfigureAvailScrollbar();
        _availScrollBar.Value = _availSectorsView.GetScrollPos().Y * _availSectorsView.ItemHeight - _availSectorsView.ItemHeight + 1;
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

        if (_rebuildingTree)
            return;

        ConfigureRequestedScrollbar();
        _requestedScrollBar.Value = _requestedChangesView.GetScrollPos().Y * _requestedChangesView.ItemHeight - _requestedChangesView.ItemHeight + 1;
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

        // Add/Remove are this menu's wording for the claim/release the arrow button performs, and
        // work from any of the three trees: a request row resolves to the sector it is about, so an
        // incoming request can be acted on without first hunting that sector down in Available.
        var sector = SectorForNode(node);
        var owned = sector != null && IsOwned(sector);
        var sectorActionable = sector != null && !_sectorActionRunning && Network.IsConnected;

        var add = new ToolStripMenuItem("Add") { Enabled = sectorActionable && !owned };
        add.Click += (_, _) => _ = RunSectorActionAsync(sector!, add: true);
        menu.Items.Add(add);

        var remove = new ToolStripMenuItem("Remove") { Enabled = sectorActionable && owned };
        remove.Click += (_, _) => _ = RunSectorActionAsync(sector!, add: false);
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

        treeView.BeginUpdate();
        try
        {
            _allowTreeToggle = true;
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
            }
        }
        finally
        {
            treeView.EndUpdate();
        }

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
    static void SectorsView_DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        var treeView = (TreeViewEx)sender!;

        var color = (e.State & TreeNodeStates.Selected) != 0
            ? Colours.GetColour(Colours.Identities.HighlightedText)
            : Colours.GetColour(Colours.Identities.InteractiveText);

        // Both of these are unmanaged GDI+ region handles, and this runs once per node per repaint
        // across three trees - neither can be left to the finalizer. Graphics.Clip's *getter*
        // allocates a fresh Region every call (it is not a borrowed reference), so the saved
        // original needs disposing just as much as the replacement does. The setter copies the
        // region it's given, which is what makes it safe to dispose the replacement immediately.
        using var previousClip = e.Graphics.Clip;

        using (var clip = new Region(e.Bounds))
            e.Graphics.Clip = clip;

        e.Graphics.Clear(treeView.BackColor);
        TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont, e.Node.Bounds, color);

        e.Graphics.Clip = previousClip;
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
    static string NodeKey(TreeNode node)
    {
        var segments = new Stack<string>();
        for (var current = node; current != null; current = current.Parent)
        {
            segments.Push(current.Tag switch
            {
                SectorsVolumes.Sector sector => "sector:" + sector.Name,
                SectorChangeRequest request => "request:" + request.Id,
                _ when ReferenceEquals(current.Tag, CategoryTag) => "category:" + current.Name,
                _ => "text:" + current.Text
            });
        }

        return string.Join("\u001f", segments);
    }

    sealed class TreeViewState
    {
        public readonly HashSet<string> ExpandedKeys = new();
        public string? SelectedKey;
        public int ScrollValue;
    }

    static TreeViewState CaptureTreeState(TreeViewEx view, ScrollBar scrollBar)
    {
        var state = new TreeViewState { ScrollValue = scrollBar.Value };
        CaptureExpanded(view.Nodes, state.ExpandedKeys);
        state.SelectedKey = view.SelectedNode == null ? null : NodeKey(view.SelectedNode);
        return state;
    }

    static void CaptureExpanded(TreeNodeCollection nodes, HashSet<string> expandedInto)
    {
        foreach (TreeNode node in nodes)
        {
            var key = NodeKey(node);
            if (node.IsExpanded)
                expandedInto.Add(key);

            CaptureExpanded(node.Nodes, expandedInto);
        }
    }

    // Re-expands whatever was open before the rebuild and re-selects the same logical item if it
    // still exists post-refresh - so a poll tick (every 10s) can't collapse an open dropdown or
    // move the selection out from under the controller mid-action. Must run inside the caller's
    // BeginUpdate/EndUpdate.
    static void RestoreExpandedAndSelection(TreeViewEx view, TreeViewState state)
    {
        TreeNode? selected = null;

        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                var key = NodeKey(node);
                if (state.ExpandedKeys.Contains(key))
                {
                    node.Expand();
                    RefreshDropdownNodeText(node);
                }
                if (key == state.SelectedKey)
                    selected = node;
                Walk(node.Nodes);
            }
        }

        Walk(view.Nodes);

        if (selected != null)
            view.SelectedNode = selected;
    }

    // Restores the scroll position captured by CaptureTreeState - call after the tree's own
    // Configure*Scrollbar() has already run against the rebuilt content, so PreferredHeight/
    // ActualHeight reflect the new node structure before the old offset is reapplied to it.
    static void RestoreScroll(TreeViewEx view, ScrollBar scrollBar, TreeViewState state)
    {
        var itemHeight = Math.Max(view.ItemHeight, 1);
        scrollBar.Value = state.ScrollValue;
        view.SetScrollPosVert((state.ScrollValue + itemHeight - 1) / itemHeight);
    }

    void CurrScrollBar_Scroll(object? sender, EventArgs e)
    {
        _currSectorsView.SetScrollPosVert((_currScrollBar.Value + _currSectorsView.ItemHeight - 1) / _currSectorsView.ItemHeight);
    }

    void ConfigureCurrScrollbar()
    {
        _currScrollBar.PreferredHeight = _currSectorsView.GetPreferredHeight();
        _currScrollBar.ActualHeight = _currSectorsView.Height;
        _currScrollBar.Change = _currSectorsView.ItemHeight;
    }

    void AvailScrollBar_Scroll(object? sender, EventArgs e)
    {
        _availSectorsView.SetScrollPosVert((_availScrollBar.Value + _availSectorsView.ItemHeight - 1) / _availSectorsView.ItemHeight);
    }

    void ConfigureAvailScrollbar()
    {
        _availScrollBar.PreferredHeight = _availSectorsView.GetPreferredHeight();
        _availScrollBar.ActualHeight = _availSectorsView.Height;
        _availScrollBar.Change = _availSectorsView.ItemHeight;
    }

    void RequestedScrollBar_Scroll(object? sender, EventArgs e)
    {
        _requestedChangesView.SetScrollPosVert((_requestedScrollBar.Value + _requestedChangesView.ItemHeight - 1) / _requestedChangesView.ItemHeight);
    }

    void ConfigureRequestedScrollbar()
    {
        _requestedScrollBar.PreferredHeight = _requestedChangesView.GetPreferredHeight();
        _requestedScrollBar.ActualHeight = _requestedChangesView.Height;
        _requestedScrollBar.Change = _requestedChangesView.ItemHeight;
    }

    void UpdateArrowButton()
    {
        var ownedSelected = _currSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector;
        var availSelected = _availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector;

        _arrowButton.Text = ownedSelected ? ArrowRight : ArrowLeft;
        _arrowButton.Enabled = !_sectorActionRunning && (ownedSelected || availSelected);
    }

    // Compares on exactly the footing ApplyButton_Click actually applies: non-dummy sectors, as a
    // set. SequenceEqual against the raw MMI.SectorsControlled was both order-sensitive and
    // dummy-sensitive, while _sectorsSelected arrives in OzServer's response order and never
    // contains dummies (they're vatSys's own backfill for uncontrolled airspace, filtered out at
    // every other site in this codebase) - so it reported "unsaved changes" essentially always,
    // leaving Apply and Cancel permanently lit whether or not anything actually differed.
    void UpdateApplyCancelButtons()
    {
        var applied = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        var selected = _sectorsSelected.Where(s => !s.IsDummy).ToList();

        // Sector.Equals is callsign-based; == is not overloaded, so it must not be used here (the
        // same trap AfvSectorClaimer.CheckActive documents).
        var upToDate = selected.Count == applied.Count
                       && selected.All(s => applied.Any(a => a.Equals(s)));

        _applyButton.Enabled = !upToDate;
        _cancelButton.Enabled = !upToDate;
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
        // carried across it - PopulateAvailableList's own state capture/restore (see NodeKey) would
        // otherwise try to find something that plainly no longer applies.
        _availSectorsView.SelectedNode = null;
        PopulateAvailableList();
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

    void LoadSectors()
    {
        // Dummies filtered out: they are vatSys's own placeholder infill for airspace nobody
        // controls, never something this window can claim, release or request. Taking
        // MMI.SectorsControlled wholesale put them straight into _sectorsSelected, where they
        // showed up as rows in Owned and skewed the Apply/Cancel comparison above.
        _sectorsSelected = MMI.SectorsControlled.Where(s => !s.IsDummy).ToList();
        PopulateLists();
        UpdateApplyCancelButtons();
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
        var firstPopulate = _ownedFirstPopulate;

        _rebuildingTree = true;
        try
        {
            _currSectorsView.BeginUpdate();
            try
            {
                _currSectorsView.Nodes.Clear();
                AddNodesGroupedByCategory(_currSectorsView, sectorNodes);

                // Only the very first populate forces everything open (Owned is usually short
                // enough to show at a glance). Later rebuilds restore the controller's state.
                if (firstPopulate)
                {
                    _currSectorsView.ExpandAll();
                    foreach (TreeNode node in _currSectorsView.Nodes)
                        RefreshDropdownNodeTextRecursive(node);
                }

                RestoreExpandedAndSelection(_currSectorsView, state);
            }
            finally
            {
                _currSectorsView.EndUpdate();
            }

            ConfigureCurrScrollbar();
            RestoreScroll(_currSectorsView, _currScrollBar, state);
            _ownedFirstPopulate = false;
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
            QueueControlledListRefresh();
            return;
        }

        // Invalidates a Controlled response still in flight if the mode changed while it awaited.
        ++_controlledRefreshVersion;

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

    void QueueControlledListRefresh()
    {
        if (_controlledRefreshRunning)
        {
            _controlledRefreshQueued = true;
            return;
        }

        _controlledRefreshRunning = true;
        _ = RunControlledListRefreshLoopAsync();
    }

    async Task RunControlledListRefreshLoopAsync()
    {
        try
        {
            do
            {
                _controlledRefreshQueued = false;
                var refreshVersion = _controlledRefreshVersion;
                await PopulateControlledListAsync(refreshVersion);
            }
            while (_controlledRefreshQueued && _sectorListMode == SectorListMode.Controlled);
        }
        catch (Exception ex)
        {
            // This loop is deliberately fire-and-forget. Do not let a rendering/lifecycle failure
            // escape as an unobserved task exception or leave future Controlled refreshes wedged.
            Errors.Add(new Exception($"Couldn't refresh the Controlled sector list: {ex.Message}", ex), "OzServer");
        }
        finally
        {
            _controlledRefreshRunning = false;
            _controlledRefreshQueued = false;
        }
    }

    // GET /sectors/controlled is already flattened server-side - claiming a grouping sector (e.g.
    // TBD) creates one sector_ownerships row per covered sector, so the response already has a
    // separate TBD/AUG/AAE/... entry, no client-side recursion needed the way Owned/Available's
    // own tree-building does.
    async Task PopulateControlledListAsync(int refreshVersion)
    {
        // Say so rather than leaving whatever Available last rendered sitting under a Controlled
        // button that now looks pressed for no reason.
        if (!Network.IsConnected)
        {
            ShowAvailablePlaceholder(ControlledModePrefix, ControlledUnavailable);
            return;
        }

        List<OzServerControlledSectorDto> controlled;
        try
        {
            controlled = await _api.GetControlledSectorsAsync();
        }
        catch (Exception ex)
        {
            if (!IsDisposed && IsHandleCreated)
                Errors.Add(new Exception(ex.Message, ex), "OzServer");

            // Keep the last successful Controlled list on screen - rendering a transient failure
            // as an empty response makes every row vanish and jump back on the next poll. But only
            // if there *is* one: on the first switch into Controlled the tree still holds Available's
            // rows, and leaving those up would show other people's sectors as claimable.
            if (_availableTreeSignature?.StartsWith(ControlledModePrefix, StringComparison.Ordinal) != true)
                ShowAvailablePlaceholder(ControlledModePrefix, ControlledUnavailable);

            return;
        }

        // The toggle may have flipped back to Available while this request was in flight.
        if (IsDisposed || !IsHandleCreated || !Network.IsConnected
            || _sectorListMode != SectorListMode.Controlled || refreshVersion != _controlledRefreshVersion)
            return;

        var sectorNodes = new List<TreeNode>();
        foreach (var dto in controlled)
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
        if (_sectorsSelected.Contains(sector) || !TryMatchAvailable(sector, out var controller))
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

    static NetworkATC? FindController(SectorsVolumes.Sector sector) =>
        (Network.GetOnlineATCs ?? new List<NetworkATC>())
        .FirstOrDefault(a => a.ValidATC && a.Callsign == sector.Callsign);

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
            AppendValue(into, NodeKey(node));
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

    void ApplyButton_Click(object? sender, EventArgs e)
    {
        MMI.SetControlledSectors(_sectorsSelected.Where(s => !s.IsDummy).ToList());
        UpdateArrowButton();
    }

    void CancelButton_Click(object? sender, EventArgs e)
    {
        LoadSectors();
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

    // async void, so nothing may escape: an exception leaving this method is unhandled on the UI
    // thread and takes vatSys down with it, rather than surfacing in the error log. The tracker
    // handles the API calls themselves, but not everything downstream of them - conflict handling
    // in particular marshals a modal dialog, and used to be raised from inside a catch clause where
    // the sibling catch could not see it (see OzServerOwnershipTracker.ClaimAsync).
    async void ArrowButton_Click(object? sender, EventArgs e)
    {
        if (_currSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector ownedSector)
            await RunSectorActionAsync(ownedSector, add: false);
        else if (_availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector availSector)
            await RunSectorActionAsync(availSector, add: true);
    }

    // The one path both the arrow button and the menu's Add/Remove take, so a single
    // _sectorActionRunning latch covers both - the two entry points can't leave overlapping claims
    // in flight on the same sector, which is exactly the re-entrancy
    // OzServerOwnershipTracker's own class comment describes as a real bug rather than a
    // theoretical one. Catches everything: ArrowButton_Click is async void, where an escaping
    // exception is unhandled on the UI thread and takes vatSys down with it.
    async Task RunSectorActionAsync(SectorsVolumes.Sector sector, bool add)
    {
        if (_sectorActionRunning)
            return;

        _sectorActionRunning = true;
        UpdateArrowButton();
        try
        {
            if (!add)
                await ReleaseSectorAsync(sector);
            else if (_sectorListMode == SectorListMode.Available)
                await ClaimSectorAsync(sector);
            else
                // Controlled lists what someone else already holds, so Add can only mean "ask for
                // it" there - the same split the arrow button's <</>> has always made.
                await RequestSectorAsync(sector);
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't complete that sector change: {ex.Message}", ex), "OzServer");
        }
        finally
        {
            _sectorActionRunning = false;
            UpdateArrowButton();
        }
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
    async Task ClaimSectorAsync(SectorsVolumes.Sector sector)
    {
        _arrowButton.Enabled = false;
        ShowOwnedOptimistically(sector, owned: true);
        await _tracker.ClaimAsync(sector);
        UpdateArrowButton();
    }

    async Task ReleaseSectorAsync(SectorsVolumes.Sector sector)
    {
        _arrowButton.Enabled = false;
        ShowOwnedOptimistically(sector, owned: false);
        await _tracker.ReleaseAsync(sector);
        UpdateArrowButton();
    }

    // Moves the row between Owned and Available immediately, before the server has been asked.
    // A claim is two sequential round trips before anything moves on screen - POST /claim, then the
    // GET that re-derives Owned (see OzServerOwnershipTracker.ClaimAsync) - which read as the window
    // having ignored the click. This is presentation only: _sectorsSelected is overwritten wholesale
    // by the next SyncOwnedFromTracker, so the server still decides what is actually owned, and a
    // claim that fails or that the server answers differently (a claim covers the whole
    // responsible_sectors chain, so the real result is usually *more* than guessed here) corrects
    // itself on that refresh rather than being left as a lie on screen.
    //
    // Skipped while disconnected: ClaimAsync/ReleaseAsync both return without calling the server at
    // all in that state, so there would be no refresh afterwards to correct the guess.
    void ShowOwnedOptimistically(SectorsVolumes.Sector sector, bool owned)
    {
        if (!Network.IsConnected)
            return;

        // Name comparison, not Contains: it is the footing every other Owned/Available decision in
        // this window uses, and the one vatSys's own SectorsWindow uses too (see PopulateLists).
        var alreadyOwned = _sectorsSelected.Any(s => !s.IsDummy && s.Name == sector.Name);
        if (owned == alreadyOwned)
            return;

        var updated = _sectorsSelected.Where(s => s.Name != sector.Name).ToList();
        if (owned)
            updated.Add(sector);

        _sectorsSelected = updated;
        PopulateLists();
    }

    async Task RequestSectorAsync(SectorsVolumes.Sector sector)
    {
        _arrowButton.Enabled = false;
        try
        {
            await _tracker.RequestAsync(sector);
            await RefreshRequestedChangesAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }
        finally
        {
            UpdateArrowButton();
        }
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

            // Map both halves before changing either field so a malformed response cannot leave a
            // half-new/half-old Requested Changes snapshot on screen.
            var byMe = response.ByMe
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
        AddRequestNodes(byMeNode, _requestsByMe, NoRequestsByMe);

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

    void AddRequestNodes(TreeNode parent, List<SectorChangeRequest> requests, string emptyText)
    {
        if (requests.Count == 0)
        {
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

    void UpdateRequestActionButtons()
    {
        if (_requestActionRunning || !Network.IsConnected)
        {
            SetRequestButtonsEnabled(false);
            return;
        }

        var selected = _requestedChangesView.SelectedNode;
        var category = CategoryNameOf(selected);
        var request = FindOwningRequest(selected);

        _acceptButton.Enabled = GetRequestsToAccept().Count > 0;
        _rejectButton.Enabled = request != null && category == RequestedFromMeName;
        _cancelRequestButton.Enabled = request != null && category == RequestedByMeName;
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

    void SetRequestButtonsEnabled(bool enabled)
    {
        _acceptButton.Enabled = enabled;
        _rejectButton.Enabled = enabled;
        _cancelRequestButton.Enabled = enabled;
    }
}
