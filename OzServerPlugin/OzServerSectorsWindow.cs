using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
        _availSectorsView.MouseWheel += AvailSectorsView_MouseWheel;
        _availSectorsView.AfterSelect += SectorsView_AfterSelect;
        _availSectorsView.AfterSelect += AvailSectorsView_AfterSelect;
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
        _currSectorsView.MouseWheel += CurrSectorsView_MouseWheel;
        _currSectorsView.AfterSelect += CurrSectorsView_AfterSelect;
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
        _requestedChangesView.MouseWheel += RequestedChangesView_MouseWheel;
        _requestedChangesView.AfterSelect += SectorsView_AfterSelect;
        _requestedChangesView.AfterSelect += (_, _) => UpdateRequestActionButtons();
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
        _acceptButton.Click += async (_, _) =>
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
        };

        _rejectButton = CreateRequestActionButton("Reject");
        _rejectButton.Click += async (_, _) =>
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
        };

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
        _sectorsSelected = _tracker.Owned.ToList();
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
        ConfigureCurrScrollbar();
        _currScrollBar.Value = _currSectorsView.GetScrollPos().Y * _currSectorsView.ItemHeight - _currSectorsView.ItemHeight + 1;
    }

    void AvailSectorsView_AfterExpandCollapse(object? sender, TreeViewEventArgs e)
    {
        RefreshDropdownNodeText(e.Node);
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
        ConfigureRequestedScrollbar();
        _requestedScrollBar.Value = _requestedChangesView.GetScrollPos().Y * _requestedChangesView.ItemHeight - _requestedChangesView.ItemHeight + 1;
    }

    // Any node with children is a "dropdown" - a category header (Approach/Centre/...), or a
    // sector that's itself a primary position bundling further sub-sectors (e.g. AAE inside
    // Approach, or a sector like TBD that bundles AAE which itself bundles AAW/AAR). Dropdown
    // nodes are click-toggled (see TreeView_NodeMouseClick) rather than selection-driven, so
    // selecting a plain leaf just makes sure its ancestor chain stays visible.
    static void SectorsView_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node.Nodes.Count > 0)
            return;

        var treeView = (TreeViewEx)sender!;
        treeView.BeginUpdate();

        var topLevelAncestor = e.Node;
        while (topLevelAncestor.Parent != null)
            topLevelAncestor = topLevelAncestor.Parent;

        foreach (TreeNode node in treeView.Nodes)
        {
            if (node != topLevelAncestor)
                node.Collapse();
        }
        treeView.EndUpdate();
    }

    // Dropdown nodes don't select-to-expand (see SectorsView_AfterSelect above) - clicking one
    // toggles it directly, closing it if it's already open instead of always forcing it open.
    // Only collapses sibling nodes when toggling a top-level dropdown open, so expanding a
    // primary position nested inside a category doesn't disturb unrelated branches of the tree.
    static void TreeView_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Node.Nodes.Count == 0)
            return;

        var treeView = (TreeViewEx)sender!;
        treeView.BeginUpdate();

        if (e.Node.IsExpanded)
        {
            e.Node.Collapse();
        }
        else
        {
            if (e.Node.Parent == null)
            {
                foreach (TreeNode node in treeView.Nodes)
                {
                    if (node != e.Node)
                        node.Collapse();
                }
            }
            e.Node.Expand();
        }

        treeView.EndUpdate();
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

    // A logical identity for a node that survives a full Nodes.Clear()+rebuild - used to carry
    // expand/collapse state and selection across a data refresh instead of losing them every time
    // (a poll tick, MMI.SectorsControlledChanged, OnlineATCChanged, or any other action that calls
    // PopulateOwnedList/PopulateAvailableList/PopulateRequestedChanges would otherwise silently
    // collapse whatever the controller had open and drop their current selection).
    static string? NodeKey(TreeNode node) => node.Tag switch
    {
        SectorsVolumes.Sector sector => "sector:" + sector.Name,
        SectorChangeRequest request => "req:" + request.Id,
        _ when ReferenceEquals(node.Tag, CategoryTag) => "cat:" + node.Name,
        _ => null
    };

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
            if (key != null && node.IsExpanded)
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
                if (key != null)
                {
                    if (state.ExpandedKeys.Contains(key))
                    {
                        node.Expand();
                        RefreshDropdownNodeText(node);
                    }
                    if (key == state.SelectedKey)
                        selected = node;
                }
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
        _arrowButton.Enabled = ownedSelected || availSelected;
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
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            RunOnUiThread(RefreshAvailableList);
            return;
        }

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
        var state = CaptureTreeState(_currSectorsView, _currScrollBar);

        _currSectorsView.BeginUpdate();
        _currSectorsView.Nodes.Clear();

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

        AddNodesGroupedByCategory(_currSectorsView, sectorNodes);

        // Only the very first populate forces everything open (Owned is usually short enough that
        // seeing it all at a glance is worth it) - every refresh after that just restores whatever
        // the controller had open themselves, via the state captured above.
        if (_ownedFirstPopulate)
        {
            _currSectorsView.ExpandAll();
            foreach (TreeNode node in _currSectorsView.Nodes)
                RefreshDropdownNodeTextRecursive(node);
            _ownedFirstPopulate = false;
        }

        RestoreExpandedAndSelection(_currSectorsView, state);
        _currSectorsView.EndUpdate();

        ConfigureCurrScrollbar();
        RestoreScroll(_currSectorsView, _currScrollBar, state);
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
            _ = PopulateControlledListAsync();
            return;
        }

        var state = CaptureTreeState(_availSectorsView, _availScrollBar);

        _availSectorsView.BeginUpdate();
        _availSectorsView.Nodes.Clear();

        var sectorNodes = new List<TreeNode>();
        foreach (var key in SectorsVolumes.Sectors.Where(s =>
                     s.CSECEligible && !_sectorsSelected.Any(ss => !ss.IsDummy && s.Name == ss.Name)))
        {
            var node = BuildAvailableSectorNode(key);
            if (node != null)
                sectorNodes.Add(node);
        }

        AddNodesGroupedByCategory(_availSectorsView, sectorNodes);

        RestoreExpandedAndSelection(_availSectorsView, state);
        _availSectorsView.EndUpdate();

        ConfigureAvailScrollbar();
        RestoreScroll(_availSectorsView, _availScrollBar, state);
    }

    // GET /sectors/controlled is already flattened server-side - claiming a grouping sector (e.g.
    // TBD) creates one sector_ownerships row per covered sector, so the response already has a
    // separate TBD/AUG/AAE/... entry, no client-side recursion needed the way Owned/Available's
    // own tree-building does.
    async Task PopulateControlledListAsync()
    {
        if (!Network.IsConnected)
            return;

        List<OzServerControlledSectorDto> controlled;
        try
        {
            controlled = await _api.GetControlledSectorsAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
            controlled = new List<OzServerControlledSectorDto>();
        }

        // The toggle may have flipped back to Available while this request was in flight.
        if (_sectorListMode != SectorListMode.Controlled)
            return;

        var state = CaptureTreeState(_availSectorsView, _availScrollBar);

        _availSectorsView.BeginUpdate();
        _availSectorsView.Nodes.Clear();

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

        AddNodesGroupedByCategory(_availSectorsView, sectorNodes);

        RestoreExpandedAndSelection(_availSectorsView, state);
        _availSectorsView.EndUpdate();

        ConfigureAvailScrollbar();
        RestoreScroll(_availSectorsView, _availScrollBar, state);
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

    static void AddNodesGroupedByCategory(TreeViewEx view, IEnumerable<TreeNode> sectorNodes)
    {
        foreach (var group in sectorNodes
                     .GroupBy(node => GetSectorCategory((SectorsVolumes.Sector)node.Tag))
                     .OrderBy(g => g.Key))
        {
            var categoryNode = AddCategoryNode(view, CategoryName(group.Key));
            foreach (var node in group)
                categoryNode.Nodes.Add(node);
        }
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

    static TreeNode AddCategoryNode(TreeViewEx view, string name)
    {
        var node = new TreeNode(CollapsedPrefix + name)
        {
            Tag = CategoryTag,
            Name = name,
            NodeFont = GetCategoryFont(view.Font)
        };
        node.ToolTipText = node.Text;
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
        try
        {
            if (_currSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector ownedSector)
            {
                await ReleaseSectorAsync(ownedSector);
            }
            else if (_availSectorsView.SelectedNode?.Tag is SectorsVolumes.Sector availSector)
            {
                if (_sectorListMode == SectorListMode.Available)
                    await ClaimSectorAsync(availSector);
                else
                    await RequestSectorAsync(availSector);
            }
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception($"Couldn't complete that sector change: {ex.Message}", ex), "OzServer");
            UpdateArrowButton();
        }
    }

    // Claims through the tracker (a claim covers the sector's full responsible_sectors chain
    // server-side - see SectorOwnershipController::claim), which re-derives Owned from the server
    // and raises OwnedChanged - see the class comment for why this window never touches
    // _sectorsSelected directly.
    async Task ClaimSectorAsync(SectorsVolumes.Sector sector)
    {
        _arrowButton.Enabled = false;
        await _tracker.ClaimAsync(sector);
        UpdateArrowButton();
    }

    async Task ReleaseSectorAsync(SectorsVolumes.Sector sector)
    {
        _arrowButton.Enabled = false;
        await _tracker.ReleaseAsync(sector);
        UpdateArrowButton();
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
    async Task RefreshRequestedChangesAsync()
    {
        if (!Network.IsConnected)
            return;

        try
        {
            var response = await _api.GetMyRequestsAsync();

            _requestsByMe.Clear();
            _requestsByMe.AddRange(response.ByMe.Select(dto => MapRequest(dto, dto.TargetCallsign)).OfType<SectorChangeRequest>());

            _requestsFromMe.Clear();
            _requestsFromMe.AddRange(response.FromMe.Select(dto => MapRequest(dto, dto.RequestingCallsign)).OfType<SectorChangeRequest>());
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
        }

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
        var state = CaptureTreeState(_requestedChangesView, _requestedScrollBar);

        _requestedChangesView.BeginUpdate();
        _requestedChangesView.Nodes.Clear();

        var byMeNode = AddCategoryNode(_requestedChangesView, RequestedByMeName);
        AddRequestNodes(byMeNode, _requestsByMe, NoRequestsByMe);

        var fromMeNode = AddCategoryNode(_requestedChangesView, RequestedFromMeName);
        AddRequestNodes(fromMeNode, _requestsFromMe, NoRequestsFromMe);

        RestoreExpandedAndSelection(_requestedChangesView, state);
        _requestedChangesView.EndUpdate();

        ConfigureRequestedScrollbar();
        RestoreScroll(_requestedChangesView, _requestedScrollBar, state);
        UpdateRequestActionButtons();
    }

    void AddRequestNodes(TreeNode parent, List<SectorChangeRequest> requests, string emptyText)
    {
        if (requests.Count == 0)
        {
            parent.Nodes.Add(new TreeNode(emptyText) { NodeFont = _requestedChangesView.Font });
            return;
        }

        foreach (var request in requests)
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
        if (!Network.IsConnected)
            return;

        SetRequestButtonsEnabled(false);
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
            UpdateRequestActionButtons();
        }
    }

    async Task RejectRequestAsync(SectorChangeRequest request)
    {
        if (!Network.IsConnected)
            return;

        SetRequestButtonsEnabled(false);
        try
        {
            await _api.RejectRequestAsync(request.Id);
            await RefreshRequestedChangesAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
            UpdateRequestActionButtons();
        }
    }

    async Task CancelRequestAsync(SectorChangeRequest request)
    {
        if (!Network.IsConnected)
            return;

        SetRequestButtonsEnabled(false);
        try
        {
            await _api.CancelRequestAsync(request.Id);
            await RefreshRequestedChangesAsync();
        }
        catch (Exception ex)
        {
            Errors.Add(new Exception(ex.Message, ex), "OzServer");
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
