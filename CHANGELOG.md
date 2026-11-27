# Changelog

Every release of the OzServer plugin, newest first. Versions follow `MAJOR.MINOR.PATCH`; each is
tagged `v<version>` on GitHub, and the plugin DLL's FileVersion is the same number. Entries up to
0.3.1 are the notes published with each GitHub release.

## [Unreleased]

### Changed

- **A release that is already out when you open vatSys is running by the time vatSys has opened.**
  It is downloaded, verified and started while vatSys is still loading, with no **OzServer update**
  window and no restart. The check normally adds a fraction of a second to startup; if it gets no
  answer within 3 seconds, or the download takes more than 10, vatSys opens on the version you have
  and the update is installed in the background as before. A release published while vatSys is open
  still waits for a restart, and still offers one. The first update to arrive this way is the one
  after this release: it is the installed version that does it.

## [0.3.4] - 2026-11-27

### Fixed

- **vatSys's memory growing over a long session.** Two things were growing for as long as vatSys
  stayed open:
  - The sector request shading sat on the scope all session, even with nothing to shade, and
    vatSys loses a little memory for every filled shape it paints whenever the scope is panned or
    zoomed. The shading is now on the scope only while a request is showing.
  - vatSys keeps every window and menu it has ever opened - each level, time and handoff menu
    included - after it is closed. OzServer now releases them once a minute.
- A flight plan vatSys had deleted could be kept in memory for the rest of the session.

### Added

- **Inbound flights are activated automatically.** An IFR flight plan that is still pre-active,
  airborne outside every OzServer-held sector, is activated once it is within 150 nm of a held
  sector that its route enters or passes within 50 nm of. vatSys gives it a code, which the pilot
  is not told (the controller issues it on first contact as usual); no CFL is set and nobody takes
  the tag. Only the controller holding the first such sector along the route activates it, and
  every other OzServer controller's copy follows. VFR flight plans, and flights already inside a
  held sector (such as an IFR departure from inside your airspace), are still activated by hand.
- The memory line the log records every 15 minutes is followed by four more: every window in
  vatSys by the window that owns it and by type, each named with the plugin it belongs to; how
  many flight plans, radar tracks, maps, loaded assemblies and threads vatSys is holding; the
  closed windows released; and how much work the plugin itself did - time in its update handlers
  and on vatSys's display, memory allocated, and how much it fetched from OzServer. A long session
  that slows down now shows what was growing, and whether the plugin was busy.

### Changed

- The memory lines carry on after **Disable OzServer**, so a disabled session can be compared
  with a normal one. They are written to the log file only.
- Less work in the background: the flight list fetched every few seconds no longer carries each
  flight's plan and route (once OzServer is updated to match), is fetched once rather than by two
  parts of the plugin separately, and is read without a second full copy of it. The list of
  controllers online is worked out once per report rather than once per sector per controller, and
  the Sectors window does the same while it is open.
- Less work on vatSys's display: while a sector request is pending, the aircraft it would move are
  re-marked at most ten times a second rather than on every radar and flight plan update, and a
  handover no longer sends its flight plan update from the display's own thread.

## [0.3.3] - 2026-11-25

### Changed

- **OzServer How-To** is now an entry in vatSys's **Info** menu, with the profile's other browser
  entries, instead of a button of its own in the menu bar.

## [0.3.2] - 2026-11-25

### Added

- **OzServer How-To** button in the vatSys menu bar. It opens the controller guide at
  [ozserver.org/how-to](https://ozserver.org/how-to) in vatSys's own browser window.
- Ready for the [vatSys Launcher](https://github.com/badvectors/vatSysLauncher). Each release ships
  `OzServerPlugin-v<version>.zip`, with `OzServerPlugin.dll` and `LICENSE.txt` at its root, and is
  published as a normal release so the launcher can see it.
- Duplicate-install check. vatSys silently uses only one of several installed copies. OzServer now
  names the copy it's running from and each one being ignored in the Errors window, so a stale
  manual install can't shadow a launcher install.
- `Release.ps1` builds and checks a release and writes it to `artifacts\`.
- MIT licence, assembly metadata (product, description, copyright, repository URL) and this
  changelog.

### Changed

- The extension limit's 150 hours counts only controller and instructor ratings (C1 to I3). SUP
  and ADM hours no longer count.
- **Your position is yours while you answer.** When part of a position is held by someone else,
  the rest is claimed straight away rather than after the question about the held part is
  answered.
- **Taking sectors takes only what you agreed to.** Agreeing to take a sector from a controller who
  disconnected, or one without the plugin, no longer also takes sectors another such controller
  holds under the same group.
- A change the Sectors window could not apply is now listed in the result, instead of the Apply
  appearing to have worked.
- Planned changes in the Sectors window are discarded when you disconnect.
- **SELCAL Debug** only transmits when you are connected as a valid ATC position with a frequency
  on transmit, as vatSys's own SELCAL window requires.

### Fixed

- **Sub-sectors released when their VSCS line was tidied away.** Removing a sub-sector's line from
  the VSCS panel (0.3.1's "fewer lines on the panel") was read as you setting that line to Idle:
  the sub-sector was dropped from your scope with its tags and released on OzServer. Taking over a
  group, or logging on, could lose part of it this way.
- **Tags dropped when you gave a sector away.** A tag you were still working from a sector you
  handed over, but which had already flown on into another of your sectors, or had no radar
  return, was dropped to nobody. It now stays with you.
- **A failed logon released your sectors.** Pressing Connect after a drop and being refused
  ("callsign in use", a wrong password) was treated as a deliberate disconnect, which gave up the
  sectors OzServer was holding for you.
- **Cleared levels arrived late, and old ones came back.** A new cleared level could take up to a
  minute to reach OzServer. A sector transfer, a reconnect or another controller's label could then
  put the previous level back on the tag. It now goes out within seconds, and before a handoff.
- **Cancelled flight plans coming back.** A plan cancelled before departure, or one whose pilot had
  disconnected at the gate, could be re-activated on every scope and then never cleared.
- **Arrival List drags landing one place too high** when the list refreshed while you were
  dragging. The landing time also shows as `HH:mm` whatever your Windows region.
- A tag you were already handing to another controller is no longer also handed to the new owner
  of its sector, and a handoff that had to wait is not sent if the sector has changed hands again.
- An aircraft being handed to you from a sector you have requested keeps flashing, instead of
  being drawn as one that would come with the sector.
- A drop reported while OzServer was briefly unreachable is sent once it is back, so the tag is
  not handed back to you on your next reconnect.
- After a reconnect whose resume could not reach OzServer, sectors you had been handed earlier are
  no longer given back to their position's controller.
- An agreement to take a disconnected controller's sector could expire before it was used, and the
  question was asked again.
- An observer watching a controller no longer has their scope redrawn, and an error added, every
  few seconds.
- Frequencies are reported to OzServer the same way whatever your Windows region. With a decimal
  comma they were unreadable, and a controller without the plugin could lose the sectors their
  frequency stood for.
- Two vatSys windows on one PC no longer lose each other's log lines, and an update one of them
  has installed is no longer downloaded and failed by the other every five minutes.
- A failure installing an update is reported at most three times in a session, not every five
  minutes.

### Security

- The updater checks the full address of a download, not only how it starts. An address written to
  begin with this repository's releases could be made to resolve somewhere else.
- Windows account names are removed from file paths in errors before they are sent to OzServer.

## [0.3.1] - 2026-11-24 (pre-release)

OzServer Plugin v0.3.1 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Taking over sectors

- **From a controller without the plugin.** Sectors worked by a controller who isn't using OzServer can now be taken once they've handed them to you or logged off. Transmitting on one, or moving it across in the Sectors window, asks whether to take it, and reminds you that coordination and tag handoffs with them have to be done manually. Before this, the only option was to wait until VATSIM stopped listing them, which could take several minutes.
- **From a controller who disconnected.** OzServer keeps a dropped controller's sectors for a few minutes in case they reconnect. You can now take them during that time: you're told whose they are and until when they're kept, then asked whether to take them.
- **Your own position comes first.** If you log on as a position that a controller without the plugin was still covering, it's yours straight away, along with everything under it.
- **Everything under an extended sector.** A controller without the plugin who extends onto a sector, such as MUN, now holds its subsectors too, unless somebody else is working them.

#### Extension limits

- **ENR extensions follow VATPAC ATS Policy 2.13.3.** You can work your own position plus four enroute extensions, or six once you have 150 hours at C1 and above. Adding a sector past the limit, by transmitting, from the Sectors window or by request, is refused with an explanation. Nothing you already hold is taken away, and a logon, resume or handover is never limited.

#### VSCS

- **Fewer lines on the panel.** Subsector lines you now work through their primary's frequency are removed from the VSCS panel. Lines you're transmitting on, your logon station's line, and a subsector someone else holds are left alone.
- **Automatic transmit stays in your tier.** An enroute controller is no longer put on to transmit on approach lines, and an approach controller isn't put on enroute lines. When enroute covers an approach unit's airspace after it logs off, you get the airspace but not its frequency.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.3.0] - 2026-11-23 (pre-release)

OzServer Plugin v0.3.0 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Controllers without the plugin

- **Their airspace now shows as held.** A controller connected without OzServer (for example `ML-TBD_CTR` on plain vatSys) used to look like they held nothing, so their sectors appeared free. OzServer now works out what they are covering from their position, their controller information ("Extending MUN, GUN") and their frequencies. Those sectors are listed under Controlled in the Sectors window, marked **no plugin**.
- **No requests that can't be answered.** Their sectors can't be claimed or requested, because there is no plugin on their end to answer. If you try, by staging one, transmitting on its frequency or claiming a group that includes one, a notice explains that you need to coordinate with that controller directly and hand off tags manually.
- **On the live map** their airspace is tinted light orange.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.9] - 2026-11-23 (pre-release)

OzServer Plugin v0.2.9 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Arrival Lists

- **Shared Arrival Lists.** The Arrival List (`A:YSSY List`, and the others) is now OzServer's own window in place of vatSys's. It shows every departed flight inbound, with its landing time, type and global ops, in the same order for every controller.
- **One sequence for everyone.** Anyone can drag flights to reorder the list, unless the airport's FMP (`SY_FMP` for YSSY) is online, in which case only the FMP can.
- **Who is watching.** Two badges above the list show how many controllers have it open (hover to see who) and whether the FMP is online.
- **Edit global ops from the list.** Any controller can edit an aircraft's global ops directly from the list, up to the FDR's 15 characters.
- If you disable OzServer from its menu, vatSys's own Arrival List comes back.

#### SELCAL

- **SELCAL32 codes.** vatSys's SELCAL window now accepts and transmits SELCAL32 codes, with the new tones T–Z and 1–9 as well as the original A–S. Everything else about the window works as before.
- **SELCAL Debug window.** A new **SELCAL Debug** entry in the Tools menu shows how a code is checked and which tone frequencies AFV will send, to help track down a SELCAL that does not go through.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.8] - 2026-11-22 (pre-release)

OzServer Plugin v0.2.8 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Eurocat UI

- **The Airspace button opens the OzServer Sectors window.** With the Eurocat UI plugin installed, clicking **Airspace** did nothing, because the plugin hides vatSys's built-in Sectors entry that the button looked for. The button now opens the OzServer Sectors window. If you disable OzServer from its menu, the button goes back to opening vatSys's own Sectors window.

## [0.2.7] - 2026-11-20 (pre-release)

OzServer Plugin v0.2.7 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Sectors window

- **Lists draw cleanly.** Opening or closing a dropdown, or a list refreshing, could leave a stray arrow or part of a label on the wrong row for a moment. That no longer happens.
- **Dropdowns respond straight away.** Opening or closing one, and each refresh of the lists, used to hold the window up for a moment while the list was redrawn. They are now close to instant.

#### Updates

- **The update notice can be closed.** It still has its single **Restart** button, which closes vatSys and opens it again for you, but the close button in its title bar is back. Closing the notice leaves the update to apply the next time you start vatSys.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.6] - 2026-11-20 (pre-release)

OzServer Plugin v0.2.6 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Updates

- **Restart from the update notice.** When an update has downloaded, the notice now has a single **Restart** button. It closes vatSys and opens it again for you, so you no longer have to reopen it by hand. If you are connected, vatSys still asks you to confirm before it exits.
- **The notice stays until you restart.** It can no longer be closed, but you can move it aside and keep working behind it.

You will first see the new notice with the update after this one: the notice is shown by the version you are running when an update arrives.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.5] - 2026-11-19 (pre-release)

OzServer Plugin v0.2.5 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Sectors window

- **One pair of buttons.** The separate Apply and Cancel buttons at the bottom are gone. The two buttons under Planned Changes now do the job of whatever you have selected:
  - **Apply Changes** and **Cancel**, for the changes you have planned.
  - **Cancel Request**, with a request selected under Request Sector Jurisdiction. It takes back a request you have not sent yet, or withdraws one you have.
  - **Accept** and **Reject**, with an incoming request selected under Release Sector Jurisdiction.
- **The arrow is always there.** It is greyed out when there is nothing for it to move, and with a request you have not sent yet selected, it takes that request back out before you apply.
- **No more duplicates in Available.** A sector another controller holds on OzServer is listed under Controlled only, and you request it from there.
- A notice along the bottom shows the plugin version and who to contact while OzServer is in testing.

#### Updates

- **Updates arrive within minutes.** The plugin now checks for a new version every 5 minutes instead of every 6 hours. When one has downloaded, a notice tells you; close and re-open vatSys when convenient to apply it. Your AFV frequencies, sectors and aircraft tags are kept for when you reconnect.

#### Fixed

- **A dropped tag got its old cleared level back.** When a tag is dropped, vatSys clears its cleared level, but the plugin then put the old level back on every scope. It now stays cleared.
- **Two controllers reporting the same flight.** A controller who had tracked a flight earlier could keep reporting it to OzServer alongside the controller whose sector it was in, with the two reports overwriting each other. Only the controller whose sector the flight is in reports it now.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.4] - 2026-11-19 (pre-release)

OzServer Plugin v0.2.4 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### New

- **OzServer Announcements.** The OzServer team can now send notices straight to your vatSys: a known problem, a release to install, or a question about something they have seen. They pop up with a sound, and when replies are allowed you can answer from the popup.
- **Quieter API restarts.** When the OzServer API restarts (every update to it does), it now tells the plugin first. Errors that only mean "OzServer isn't answering" are held back until it is back, usually within seconds, instead of opening the Errors window over your scope.

#### Fixed

- **Reconnecting lost your extension and tags.** If you disconnect while extending into another position and reconnect, you now keep its sectors and tags. Before, they were handed back to that position's controller if they were online, and the hand-back could take your own sector with it.
- **Taking over a position gave you nothing to transmit on.** When you gain another position's sectors, by request or in the Sectors window, its frequency is now added to your VSCS panel and put on transmit. On a full panel, an idle frequency makes way. Extensions you were working come back on transmit after a reconnect too.
- **Tags dropped when a pilot reconnects.** vatSys drops your tag the moment its pilot disconnects. If the pilot is back within 5 minutes, the plugin now takes the tag back for you, even if the aircraft was still in the sector before yours.
- **Losing infill when a position's controller logs on.** If you hold a sector and its own controller connects, the sector stays on your scope until they request it and you hand it over.
- **Logging on asked for your position automatically.** If someone else holds your position, you are now asked first: "Request [sector] from [controller]?", with **Request Sectors** or **No**.
- **Incoming sector requests were easy to miss.** A request now opens the Sectors window, and its sound plays in your headset rather than through your speakers.
- **Request colours on ground maps.** Request shading is now drawn only on the main scope and ASD windows, not on ground (ASMGCS) windows.
- **Popup buttons.** Buttons such as "Close vatSys" now fit on one line.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.3] - 2026-11-17 (pre-release)

OzServer Plugin v0.2.3 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Fixed

- **Tags lost straight after reconnecting.** Your tags were handed back on reconnect, then dropped again a few seconds later when another plugin briefly took your extended sectors out of vatSys. For the first 30 seconds after a tag is restored, losing it like this is no longer treated as you dropping it, and the tag is taken back automatically.
- **Sub-sectors you dropped came straight back.** A sub-sector you release from a group you keep now stays released. It is no longer reclaimed by a VSCS transmit press, a retry, or reconnecting within 5 minutes under the same callsign.
- **Logging on claimed the wrong position.** If a display position was open in vatSys before you connected, its sectors could be claimed instead of your logon position's. Claiming now starts from your logon position.
- **Taking over a position from the previous controller.** If you connect under the same callsign as a controller who left less than 2 minutes ago, you now take over the position's sectors and the tags inside them. Sectors they were only extending into are not handed over.
- **Tags given to the wrong controller.** When airspace changes hands other than by a handover (a disconnect, or a claim on a dropped controller's sector), its tags are now left with nobody, to be picked up by hand. Logging on also no longer picks up unowned tags in your airspace automatically. Only your own tags, and those of a position you take over, come back by themselves.
- **Flights coupled off their route stayed undeparted.** A flight coupled to its radar track while off its route now has its departure recorded, so the Depart menu item is no longer left showing.
- **Finished or cancelled flight plans came back.** A flight plan you finish or cancel is now removed from OzServer, so other controllers' clients no longer bring it back.
- **Sectors window:**
  - A released sub-sector no longer stays shown under its group.
  - A sub-sector you dropped from a group can now be found under Available and taken back.
  - Sectors that cover each other no longer nest inside each other over and over.
- Reconnecting quickly could let a claim land before your position handover arrived.

#### Changes

- **Requested Changes** has new headings: **Request Sector Jurisdiction**, **Transfer Sector Authority** and a new **Drop Sectors** list. Drop Sectors shows what Apply will release, and those sectors are shaded red on the scope while the Sectors window is open.
- **OzServer Sectors** in the Settings menu is now just **Sectors**, in place of vatSys's own Sectors... entry.
- **The OzServer Settings window has been removed.** The plugin always connects to the OzServer API.

#### Diagnostics

- The log now explains why an aircraft was not coupled to its flight plan, at most once every 5 minutes per aircraft.
- Tags lost straight after a reconnect are logged, so we can see how often it happens.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.2] - 2026-11-16 (pre-release)

OzServer Plugin v0.2.2 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Fixed

- **More freezing on slower PCs.** Following on from v0.2.1, the plugin does much less work on vatSys's display. The Sectors window no longer rebuilds its list of online controllers on every update from every controller - it is redrawn only while open, at most once a second. Aircraft positions are worked out at most once per report interval instead of on every radar update, and the sector request shading is only recalculated when the sectors in it change.
- **Activated flights on the ground went stale on OzServer.** vatSys doesn't couple an aircraft below 50 kt, so a flight that had been activated at the gate or while taxiing was never reported until its take-off roll. These flights are now reported from the aircraft's own position. Logging on does not take jurisdiction of aircraft that haven't departed yet.
- **Labels of flights tracked by a controller not using OzServer.** Their cleared level, scratchpad and global ops are now kept up to date on OzServer, instead of keeping whatever the last OzServer controller had set.
- **TMA (approach and departure) sectors handed aircraft over automatically.** When a TMA sector changes owner, its aircraft are no longer handed over automatically. They stay tracked by you until you hand each one over yourself, as with any other manual handoff.
- **Logging on asked another controller for a sector that was theirs.** A sector held by the controller logged on as that sector's own position is now left with them, instead of being requested from them.
- **Reconnecting could leave tags with the wrong controller.** Working out other controllers' positions now counts your own session, so a controller reconnecting no longer has their own sectors' tags treated as somebody else's.
- **Sector request shading could go missing** if it was set while vatSys was reloading its maps. It is now put back on the next redraw.

#### Updates

- **Checks for an update as soon as vatSys opens**, rather than two minutes later, so you hear about a new version before you connect. If the network isn't ready yet, it tries again shortly after.
- **A new update window.** When a new version has been downloaded you are told which version you are running and which was found. Updating is highly recommended, and the window's **Close vatSys** button closes vatSys so the new version loads when you reopen it.
- **The plugin version is shown in the Sectors window's title**, so you can tell at a glance which version you are running.

#### Diagnostics

- The log now records when vatSys's display stops responding for more than a second, and vatSys's memory use every 15 minutes. This helps track down any freezes that remain.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.1] - 2026-11-16 (pre-release)

OzServer Plugin v0.2.1 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### Fixed

- **vatSys freezing on slower PCs.** While a sector request was pending, the plugin redrew every map on every scope each time any aircraft updated, and the sector request shading redrew them every two seconds even when nothing had changed. In busy airspace this could freeze vatSys. The plugin now redraws the maps only when something on them has actually changed.

If you turned OzServer off because vatSys was freezing, restart vatSys once this version has installed and leave it on.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.2.0] - 2026-11-16 (pre-release)

OzServer Plugin v0.2.0 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### New

- **Everyone sees the same label.** On tags you aren't tracking you now see the tracking controller's cleared level, scratchpad and global ops, even if you connected after they set them or they have since logged off.
- **Off-track deviations are shared.** A deviation set by the tracking controller is saved on OzServer and shown to every controller, and comes with the tag when it changes hands.
- **The "C" stays yours.** It is saved on OzServer and comes back to you when you reconnect, and goes to whoever takes over your sector. Other controllers no longer see it, and it is not passed on in a handoff.
- **Controllers without the plugin.** OzServer now knows who is running the plugin. A tag handed to a controller without it is kept up to date by someone who has it, instead of sitting in handover.
- **Logging on picks up your traffic.** An aircraft nobody is working inside your sectors when you connect is taken straight into your jurisdiction, with no flashing handoff.
- **Route sector crossings.** The live map shows each route's sector crossings by their vatSys sector name, including the zones vatSys inserts into a route.

#### Fixed

- Logging on while the approach controller below you was online briefly took their sectors, and could take a tag they were working.
- A tag lost its cleared level on other controllers' scopes when its controller disconnected and reconnected.
- A tag you dropped could be picked straight back up when you, or the next controller, connected. A dropped tag now stays unowned until someone picks it up.
- Aircraft only passing through your airspace could be handed to you automatically. Tags now move automatically only with a sector transfer or your own reconnect.
- Overlapping sector volumes were shaded darker where they overlapped. Requested sectors are now shaded once, with the boundaries still drawn.
- An approach position could be listed under Centre in the Sectors window when an enroute controller's sector grouping covered it.
- A cleared level picked up from another controller could be lost on the next update.

#### Changed

- **Label colour.** Whether a cleared level shows as new or acknowledged is no longer carried between controllers; vatSys resets it whenever a tag comes under your jurisdiction.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.1.9] - 2026-11-16 (pre-release)

OzServer Plugin v0.1.9 prerelease.

If you already have the plugin installed it updates itself: this version is downloaded in the background and takes effect the next time you start vatSys.

#### New

- **Disable OzServer.** *Tools → Disable OzServer* takes OzServer out of your session and gives you plain vatSys back until the next restart.
- **Reconnecting brings more back.** As well as your sectors and tags, a reconnect now restores the VSCS lines you were transmitting on and the traffic picture, and tags come back with their cleared level, scratchpad and label data.
- **Label data follows a tag.** Cleared level, scratchpad and label data are carried across a sector handover.
- **Receive keeps a sector.** Only dropping a VSCS line to Idle releases it; Receive keeps the airspace and its aircraft.
- **Transmitting on someone else's sector asks first.** You are shown what they hold and asked whether to send a request.
- **Logging on to a covered position.** If someone is covering your position when you log on, your client sends them one request for all of it. Nothing moves until they accept.
- **Flow positions.** An FMP position gets the 998-999 level filter, and can take enroute or TMA airspace only as temporary relief, by request.
- **Couple assist.** An activated flight plan is coupled to the radar track with its callsign when vatSys's own check will not.
- **Untracked flights stay current.** An activated flight nobody is tracking is kept up to date on OzServer by the controller responsible for it.

#### Fixed

- Reconnecting without restarting vatSys could leave you with none of your position's sectors and no request sent for them.
- Applying a change that both gave up and gained sectors could claim the given-up sector straight back.
- Pressing Transmit on your own frequency did nothing once your position's sector had dropped off your list.
- A finished flight could reappear as a ghost seconds later - after a pilot disconnected, or after you finished it yourself.
- Couple assist repeatedly re-coupled a Mode C-only track squawking the wrong code.
- A slow connection could stop every tag being restored after a reconnect.
- Several faults that could close vatSys now show a line in the Errors window instead.

#### Changed

- **Updates.** The plugin asks OzServer which release to install and falls back to GitHub. Downloads still come only from this repository's releases and are checked against their published checksum.
- **Error reporting.** Errors the plugin shows you are forwarded to OzServer with your log, so the maintainers see them as they happen.
- **Removed.** Shared map drawing and notes are no longer part of the plugin.

Report problems on the [issue tracker](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) with your `Documents\vatSys Files\ozserver_<date>.txt` log attached.

## [0.1.8] - 2026-11-11 (pre-release)

OzServer Plugin v0.1.8 prerelease.

Tags come back after vatSys is closed and reopened. The plugin used to look for the tags OzServer handed back the instant it reconnected, before vatSys had loaded any flight plans, found none, and gave up - so restarting vatSys lost every tag you held. It now keeps trying for up to 90 seconds while the flight plans, radar tracks and your sectors load, leaves alone any tag another controller has picked up in the meantime, and logs what it is waiting on and why a tag did not come back.

## [0.1.7] - 2026-11-10 (pre-release)

OzServer Plugin v0.1.7 prerelease.

Reliability fixes: the live event stream connects again (it had been refused since server isolation, leaving the plugin on its fallback polls), SweatBox-1 and SweatBox-2 are recognised again after the Australia profile renamed them, and shared drawings and notes keep refreshing when the stream is down. A removed cleared level no longer comes back on the next controller's tag after a transfer, a primary taking its group back stops at its own position, the conflict prompt no longer blocks the vatSys window and withdraws itself after a minute, background failures are reported once per two minutes instead of on every attempt, an unchanged ATIS is re-sent every 20 minutes so it isn't aged out, and one hung ownership poll can no longer hold up the rest.

## [0.1.6] - 2026-09-29 (pre-release)

OzServer Plugin v0.1.6 prerelease.

Reliability fixes: resumed sectors (and the tags inside them) now survive a reconnect, shared notes and drawings no longer duplicate or linger after a disconnect, OzServer stays quiet on unsupported servers (LocalHost, unrecognised SweatBox) instead of raising errors, and several timing and threading edge cases are closed.

## [0.1.5] - 2026-09-02 (pre-release)

OzServer Plugin v0.1.5 prerelease.

Includes improved sector request notifications, requested-sector highlighting, clearer sector descriptions, and refined message dialogs.

## [0.1.4] - 2026-09-02 (pre-release)

FDR state saving and graceful reconnect recovery, allowing strips to be restored immediately when a controller disconnects and reconnects within five minutes.

## [0.1.3] - 2026-09-01 (pre-release)

Prerelease build of OzServer Plugin v0.1.3.

- Restores sectors and their still-owned tags safely after a short reconnect
- Prevents observer sessions from using the resume path to regain sectors
- Adds grouped sector-request prompts with batch accept/reject
- Adds observer position mirroring without allowing observer claims
- Stops automatically enabling VSCS transmit when ownership is gained
- Preserves the upstream sector-window fixes
- Uses the tokenless, no-datafeed-verification API client

The ZIP contains OzServerPlugin.dll at its root.

## [0.1.2] - 2026-09-01 (pre-release)

Prerelease build of OzServer Plugin v0.1.2.

- No shared token and no server-side VATSIM datafeed verification
- Uses vatSys's immediate local connection state and reported CID/callsign
- Event-driven sector and FDR synchronization with polling fallback
- More reliable default-sector claiming during connection startup
- Migrates the former ozserver.org default to api.ozserver.org

The ZIP contains OzServerPlugin.dll at its root.

## [0.1.1] - 2026-09-01 (pre-release)

> **Patched build:** The v0.1.1 download was rebuilt from the current `dev` branch to bundle the silent updater for new installations. The version number remains v0.1.1.

#### What's Changed
* Initial update for dev branch by @RealLeviticus in https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/pull/1
* Bundle the silent plugin updater in the v0.1.1 download

#### New Contributors
* @RealLeviticus made their first contribution in https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/pull/1

**Full Changelog**: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/commits/v0.1.1

[0.3.2]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.3.2
[0.3.1]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.3.1
[0.3.0]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.3.0
[0.2.9]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.9
[0.2.8]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.8
[0.2.7]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.7
[0.2.6]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.6
[0.2.5]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.5
[0.2.4]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.4
[0.2.3]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.3
[0.2.2]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.2
[0.2.1]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.1
[0.2.0]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.2.0
[0.1.9]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.9
[0.1.8]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.8
[0.1.7]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.7
[0.1.6]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.6
[0.1.5]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.5
[0.1.4]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.4
[0.1.3]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.3
[0.1.2]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.2
[0.1.1]: https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases/tag/v0.1.1
