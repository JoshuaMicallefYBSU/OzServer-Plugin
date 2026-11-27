using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// The example session every picture is taken from, and the windows opened in it.
//
// ML-BLA_CTR is working Benalla. Mungo has extended over Yarrowee, Gundagai and Armidale are
// staffed, and Huon is worked by a controller without the plugin. All of them are invented.
static class Scenarios
{
    const string Me = "ML-BLA_CTR";
    const int MyCid = 1000001;

    static readonly OzServerControlledSectorOwnerDto Mungo = new() { Cid = 1000002, Callsign = "ML-MUN_CTR" };
    static readonly OzServerControlledSectorOwnerDto Gundagai = new() { Cid = 1000003, Callsign = "ML-GUN_CTR" };
    static readonly OzServerControlledSectorOwnerDto Armidale = new() { Cid = 1000004, Callsign = "BN-ARL_CTR" };
    static readonly OzServerControlledSectorOwnerDto Huon = new() { Cid = 0, Callsign = "ML-HUO_CTR", External = true };

    static SectorsVolumes.Sector Sector(string name) => SectorsVolumes.Sectors.First(s => s.Name == name);

    static OzServerOwnershipTracker _tracker = null!;

    public static async Task RunAll()
    {
        Shots.SetOnline(new[]
        {
            ("ML-MUN_CTR", "Example Two"), ("ML-GUN_CTR", "Example Three"),
            ("BN-ARL_CTR", "Example Four"), ("ML-HUO_CTR", "Example Five"),
        });

        var owned = PrimaryPosition.CoveredBy(Sector("BLA"));
        if (!owned.Any(s => s.Name == "BLA"))
            owned.Insert(0, Sector("BLA"));
        Console.WriteLine("Owned: " + string.Join(",", owned.Select(s => s.Name)));

        var controlled = new Dictionary<string, OzServerControlledSectorOwnerDto>(StringComparer.OrdinalIgnoreCase);
        void Hold(string name, OzServerControlledSectorOwnerDto owner)
        {
            foreach (var sector in PrimaryPosition.CoveredBy(Sector(name)).Append(Sector(name)))
            {
                if (!owned.Any(o => o.Name == sector.Name) && !controlled.ContainsKey(sector.Name))
                    controlled[sector.Name] = owner;
            }
        }
        Hold("MUN", Mungo);
        Hold("YWE", Mungo);
        Hold("GUN", Gundagai);
        Hold("ARL", Armidale);
        Hold("HUO", Huon);
        Console.WriteLine("Controlled: " + string.Join(",", controlled.Select(p => $"{p.Key}>{p.Value.Callsign}")));

        Shots.Set(typeof(MMI), null, "sectorsControlled", owned.ToList());

        _tracker = new OzServerOwnershipTracker();
        var type = typeof(OzServerOwnershipTracker);
        Shots.Set(type, _tracker, "_owned", owned.ToList());
        Shots.Set(type, _tracker, "_controlled", controlled);
        Shots.Set(type, _tracker, "_hasBaseline", true);
        Shots.Set(type, _tracker, "PluginClients",
            new HashSet<string>(new[] { Me, Mungo.Callsign, Gundagai.Callsign, Armidale.Callsign }, StringComparer.OrdinalIgnoreCase));

        await Try("how-to menu entry", HowToMenuEntry);
        await Try("sectors window", SectorsWindow);
        await Try("prompts", Prompts);
        await Try("arrival list", ArrivalList);
        await Try("developer message", DeveloperMessage);
    }

    static async Task Try(string what, Func<Task> scenario)
    {
        try
        {
            await scenario();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{what}: {(ex as TargetInvocationException)?.InnerException ?? ex}");
        }
    }

    // ---- menu ---------------------------------------------------------------------------------

    // Not a picture: a check that the plugin puts "OzServer How-To" where it means to. The host
    // window is given a menu shaped like vatSys's Info menu, then one without it.
    static async Task HowToMenuEntry()
    {
        var menu = new MenuStrip { Name = "mainMenu" };
        var info = new ToolStripMenuItem("Info") { Name = "aISToolStripMenuItem" };
        info.DropDownItems.Add(new ToolStripMenuItem("About"));
        info.DropDownItems.Add(new ToolStripMenuItem("AIS Display"));
        info.DropDownItems.Add(new ToolStripSeparator { Name = "infoStripSeparator", Visible = false });
        menu.Items.Add(info);
        Shots.Host.Controls.Add(menu);
        Shots.Host.MainMenuStrip = menu;

        var plugin = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Plugin));
        Shots.Call(typeof(Plugin), plugin, "AddHowToButton");
        await Task.Delay(600);
        var last = info.DropDownItems.Cast<ToolStripItem>().Last();
        Console.WriteLine($"How-To entry: Info menu ends with \"{last.Text}\"; separator shown: {info.DropDownItems["infoStripSeparator"].Available}; entries: {info.DropDownItems.Count}");

        // A vatSys with no menu of that name: the entry goes under Tools through the plugin API.
        menu.Items.Remove(info);
        var before = (Shots.Get(typeof(MMI), null, "PluginMenuItems") as System.Collections.ICollection)?.Count ?? 0;
        Shots.Call(typeof(Plugin), plugin, "AddHowToButton");
        await Task.Delay(600);
        var after = (Shots.Get(typeof(MMI), null, "PluginMenuItems") as System.Collections.ICollection)?.Count ?? 0;
        var fallback = ((System.Collections.IEnumerable)Shots.Get(typeof(MMI), null, "PluginMenuItems")!).Cast<vatsys.Plugin.CustomToolStripMenuItem>().LastOrDefault();
        Console.WriteLine($"How-To entry without an Info menu: plugin menu items {before} -> {after}; category: {fallback?.Category}; text: \"{fallback?.Item.Text}\"");

        Shots.Host.MainMenuStrip = null;
        Shots.Host.Controls.Remove(menu);
        menu.Dispose();
    }

    // ---- Sectors window -----------------------------------------------------------------------

    static async Task SectorsWindow()
    {
        var window = new OzServerSectorsWindow(_tracker, new PendingSectorGhosts(_tracker), new RequestedSectorOverlay(_tracker));
        var type = typeof(OzServerSectorsWindow);
        window.Show(Shots.Host);
        Shots.SquareCorners(window);

        // The window asks OzServer every two seconds while it is open. Here that can only fail.
        ((System.Windows.Forms.Timer)Shots.Get(type, window, "_pollTimer")!).Stop();
        await Task.Delay(600);
        Open(window, "_currSectorsView", "Centre", "BLA");
        Open(window, "_availSectorsView", "Centre");
        await Task.Delay(300);
        Shots.Save(window, "sectors-window");

        // Controlled: who holds what.
        var modes = type.GetNestedType("SectorListMode", BindingFlags.NonPublic)!;
        Shots.Call(type, window, "SetSectorListMode", Enum.Parse(modes, "Controlled"));
        Press(window, "_controlledModeButton", true);
        Press(window, "_availableModeButton", false);
        await Task.Delay(400);
        Open(window, "_availSectorsView", "Centre");
        Open(window, "_availSectorsView", "Centre", "MUN");
        await Task.Delay(300);
        Shots.Save(window, "sectors-window-controlled");

        // A request staged for Yarrowee, which Mungo holds: not sent until Apply Changes.
        Shots.Call(type, window, "StageSectorChange", Sector("YWE"), true);
        await Task.Delay(400);
        Shots.Save(window, "sectors-window-request-staged");
        Shots.Call(type, window, "DiscardStagedChanges");

        // The same request once sent, waiting on Mungo.
        var yarrowee = Sector("YWE");
        ApplyRequests(new OzServerMyRequestsDto
        {
            ByMe =
            {
                new OzServerSectorOwnershipRequestDto
                {
                    Id = 501, SectorId = 1, GroupId = "example-1",
                    RequestingCid = MyCid, RequestingCallsign = Me,
                    TargetCid = Mungo.Cid, TargetCallsign = Mungo.Callsign,
                    Sector = new OzServerSectorDto { Id = 1, Name = yarrowee.Name, FullName = yarrowee.FullName },
                }
            }
        });
        await Task.Delay(400);
        Shots.Save(window, "sectors-window-request-sent");

        // And one arriving: the controller logging on to a sector Benalla covers asks for it.
        Shots.Call(type, window, "SetSectorListMode", Enum.Parse(modes, "Available"));
        Press(window, "_controlledModeButton", false);
        Press(window, "_availableModeButton", true);
        await Task.Delay(300);
        Open(window, "_availSectorsView", "Centre");
        var wanted = _tracker.Owned.FirstOrDefault(s => s.Name != "BLA" && s.Callsign != null && s.Callsign.EndsWith("_CTR", StringComparison.Ordinal))
                     ?? _tracker.Owned.First(s => s.Name != "BLA");
        ApplyRequests(new OzServerMyRequestsDto
        {
            FromMe =
            {
                new OzServerSectorOwnershipRequestDto
                {
                    Id = 502, SectorId = 2, GroupId = "example-2",
                    RequestingCid = 1000006, RequestingCallsign = wanted.Callsign ?? "ML-ELW_CTR",
                    TargetCid = MyCid, TargetCallsign = Me,
                    Sector = new OzServerSectorDto { Id = 2, Name = wanted.Name, FullName = wanted.FullName },
                }
            }
        });
        await Task.Delay(400);

        var requested = (TreeView)Shots.Get(type, window, "_requestedChangesView")!;
        var heading = requested.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text.Contains("Release Sector Jurisdiction"));
        if (heading?.Nodes.Count > 0)
            requested.SelectedNode = heading.Nodes[0];
        await Task.Delay(700);
        Shots.Save(window, "incoming-request");

        ApplyRequests(new OzServerMyRequestsDto());
        await Task.Delay(200);
        window.Hide();
    }

    // Opens a row the way a click on it does: the category, then the sector under it by name.
    static void Open(Form window, string view, string category, string? sector = null)
    {
        var tree = (TreeView)Shots.Get(window.GetType(), window, view)!;
        var node = tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text.Contains(category));
        if (node != null && sector != null)
            node = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => (n.Tag as SectorsVolumes.Sector)?.Name == sector);
        if (node == null || node.IsExpanded)
            return;
        Shots.Call(window.GetType(), window, "ToggleNodeExpansion", tree, node);
    }

    static void ApplyRequests(OzServerMyRequestsDto requests) =>
        Shots.Call(typeof(OzServerOwnershipTracker), _tracker, "ApplyRequests", requests);

    static void Press(Form window, string button, bool pressed)
    {
        var control = Shots.Get(window.GetType(), window, button)!;
        control.GetType().GetProperty("Pressed")?.SetValue(control, pressed);
        ((Control)control).Invalidate();
    }

    // ---- prompts and notices ------------------------------------------------------------------

    static async Task Prompts()
    {
        var type = typeof(OzServerOwnershipTracker);

        // Pressing transmit on Yarrowee, which Mungo holds.
        var yarrowee = Sector("YWE");
        var contested = PrimaryPosition.CoveredBy(yarrowee).Append(yarrowee)
            .GroupBy(s => s.Name).Select(g => g.First())
            .Where(s => _tracker.ControlledByOthers.ContainsKey(s.Name))
            .Select(s => new OzServerSectorConflictDto { Sector = s.Name, Owner = _tracker.ControlledByOthers[s.Name] })
            .ToList();
        await Shoot("request-prompt", () => Shots.Call(type, _tracker, "HandleConflictAsync", yarrowee, (IReadOnlyList<OzServerSectorConflictDto>)contested));

        // Taking Huon from a controller who is not on OzServer.
        var huon = Sector("HUO");
        var held = PrimaryPosition.CoveredBy(huon).Append(huon)
            .GroupBy(s => s.Name).Select(g => g.First())
            .Where(s => _tracker.ControlledByOthers.TryGetValue(s.Name, out var owner) && owner.External)
            .Select(s => new OzServerSectorConflictDto { Sector = s.Name, Owner = _tracker.ControlledByOthers[s.Name] })
            .ToList();
        await Shoot("take-sectors", () => _tracker.ConfirmTakeDroppedAsync(held, askAgain: true));

        // The same question for a controller whose connection dropped.
        var until = DateTime.UtcNow.AddMinutes(2);
        var dropped = new OzServerControlledSectorOwnerDto
        {
            Cid = Gundagai.Cid, Callsign = Gundagai.Callsign, Dropped = true,
            LastSeenAt = DateTime.UtcNow.AddMinutes(-1), ReleasedAt = until,
        };
        var gundagai = Sector("GUN");
        var heldDropped = new List<OzServerSectorConflictDto> { new() { Sector = gundagai.Name, Owner = dropped } };
        await Shoot("take-sectors-disconnected", () => _tracker.ConfirmTakeDroppedAsync(heldDropped, askAgain: true));

        // Logging on to Benalla while Gundagai is covering it.
        var position = _tracker.Owned
            .Select(s => new OzServerSectorConflictDto { Sector = s.Name, Owner = Gundagai })
            .ToList();
        await Shoot("own-position-prompt", () => Shots.Call(type, _tracker, "ConfirmOwnPositionRequestAsync", position));

        // A sector held by a controller without the plugin, when it is asked for rather than taken.
        var nl = Environment.NewLine;
        await Shoot("external-notice", () =>
        {
            var notice = new SectorNoticeWindow(
                $"This sector is controlled by {Huon.Callsign}, who is not using the OzServer plugin:" + nl + nl
                + SectorDescription.Describe(huon.Name) + nl + nl
                + $"OzServer can't request or hand over sectors with them. Coordinate with {Huon.Callsign} directly, and hand off tags manually.",
                "Controller not using OzServer");
            notice.Show(Shots.Host);
            return null;
        });

        // The update notice, for the version this build is.
        var running = Assembly.GetExecutingAssembly().GetName().Version!;
        await Shoot("update-window", () => Shots.Static(typeof(PluginUpdater), "AnnounceAsync", running, running));

        // The notice after an update that was applied as vatSys started, from the version before.
        var previous = new Version(running.Major, running.Minor, Math.Max(0, running.Build - 1));
        await Shoot("update-applied-window", () => Shots.Static(typeof(PluginUpdater), "AnnounceAppliedAsync", previous));
    }

    // Opens whatever `open` opens, saves each new window, and closes it again.
    static async Task Shoot(string name, Func<object?> open, int settle = 900)
    {
        var before = Application.OpenForms.Cast<Form>().ToList();
        try
        {
            open();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{name}: {(ex as TargetInvocationException)?.InnerException ?? ex}");
        }

        await Task.Delay(settle);

        var opened = Application.OpenForms.Cast<Form>().Where(f => !before.Contains(f) && f.Visible).ToList();
        if (opened.Count == 0)
            Console.Error.WriteLine($"{name}: no window opened");

        for (var i = 0; i < opened.Count; i++)
        {
            Shots.SquareCorners(opened[i]);
            await Task.Delay(150);
            Shots.Save(opened[i], i == 0 ? name : $"{name}-{i + 1}");
        }

        foreach (var form in opened)
            form.Close();
        await Task.Delay(300);
    }

    // ---- other windows ------------------------------------------------------------------------

    static async Task ArrivalList()
    {
        var now = DateTime.UtcNow;
        DateTime At(int minutes) => new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(minutes);

        var list = new OzServerArrivalListDto
        {
            Ades = "YSSY",
            Viewers = 3,
            ViewerCallsigns = { "ML-BLA_CTR", "ML-GUN_CTR", "SY_APP" },
            Fmp = new OzServerArrivalFmpDto { Position = "SY_FMP", Online = true, Callsign = "SY_FMP" },
            CanMove = false,
            Flights =
            {
                new() { Callsign = "QFA438", SeqIndex = -1, Eta = At(41), AircraftType = "B738", AircraftWake = "M" },
                new() { Callsign = "RXA6117", SeqIndex = -1, Eta = At(44), AircraftType = "SF34", AircraftWake = "M" },
                new() { Callsign = "VOZ855", SeqIndex = 5, Eta = At(33), AircraftType = "B738", AircraftWake = "M", GlobalOpData = "16R" },
                new() { Callsign = "QFA12", SeqIndex = 4, Eta = At(29), AircraftType = "A388", AircraftWake = "J", GlobalOpData = "16R" },
                new() { Callsign = "JST513", SeqIndex = 3, Eta = At(26), AircraftType = "A320", AircraftWake = "M", GlobalOpData = "16L" },
                new() { Callsign = "UAE412", SeqIndex = 2, Eta = At(22), AircraftType = "A388", AircraftWake = "J", GlobalOpData = "16R" },
                new() { Callsign = "QLK212D", SeqIndex = 1, Eta = At(19), AircraftType = "DH8D", AircraftWake = "M", GlobalOpData = "16L" },
                new() { Callsign = "QFA426", SeqIndex = 0, Eta = At(15), AircraftType = "B738", AircraftWake = "M", GlobalOpData = "16R" },
            }
        };

        // Each callsign is drawn in the colour of its track on this controller's scope, so the
        // flights are given one: mine, coming to me, being handed off, or somebody else's.
        Track("VOZ855", MMI.HMIStates.Announced);
        Track("QFA12", MMI.HMIStates.Jurisdiction);
        Track("JST513", MMI.HMIStates.Jurisdiction);
        Track("UAE412", MMI.HMIStates.HandoverOut);
        foreach (var other in new[] { "QFA438", "RXA6117", "QLK212D", "QFA426" })
            Track(other, MMI.HMIStates.NonJurisdiction);

        await Shoot("arrival-list", () =>
        {
            var window = new ArrivalListWindow("YSSY", new ArrivalListSync());
            window.Show(Shots.Host);
            window.ClientSize = new Size(window.ClientSize.Width, 330);
            window.Apply(list);
            return null;
        });
    }

    static void Track(string callsign, MMI.HMIStates state)
    {
        var fdr = (FDP2.FDR)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(FDP2.FDR));
        Shots.Set(typeof(FDP2.FDR), fdr, "callsign", callsign);
        ((List<FDP2.FDR>)Shots.Get(typeof(FDP2), null, "fdrs")!).Add(fdr);

        var track = (Track)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Track));
        track.State = state;
        var tracks = (System.Collections.Concurrent.ConcurrentDictionary<object, Track>)Shots.Get(typeof(MMI), null, "AircraftTracks")!;
        tracks.TryAdd(fdr, track);
    }

    static async Task DeveloperMessage()
    {
        await Shoot("developer-message", () =>
        {
            var window = new DeveloperMessageWindow(new OzServerDeveloperMessageDto
            {
                Id = 1,
                Title = "Server restart at 1200Z",
                Body = "OzServer will restart at 1200Z for an update. It takes about a minute, and your sectors and tags are kept while it does.",
                AllowReply = true,
                SentBy = "OzServer Dev Team",
                CreatedAt = DateTime.UtcNow,
            });
            window.Show(Shots.Host);
            return null;
        });
    }
}
