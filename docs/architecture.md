# Architecture

Reference material for people working on the plugin. For what the plugin does and how to use it,
see the [README](../README.md).

- [Principles](#principles)
- [Source layout](#source-layout)
- [API endpoints](#api-endpoints)
- [Timings](#timings)
- [Self-update](#self-update)
- [Matching the vatSys look](#matching-the-vatsys-look)
- [vatSys behaviours the code depends on](#vatsys-behaviours-the-code-depends-on)

## Principles

**OzServer's record is the single source of truth for ownership.** Every action calls the relevant
endpoint and then re-reads the result. Nothing is assumed locally about what the server decided.

**Releasing is explicit.** A sector is never released because it disappeared from
`MMI.SectorsControlled`. Exactly five things release one:

1. a VSCS line going to Idle (`AfvSectorClaimer`)
2. an applied `>>` in the Sectors window
3. a conflict the controller declined
4. the position's own controller being online already when this one connects
   (`PrimaryPositionWatcher`)
5. a deliberate disconnect (`GracefulDisconnectReleaser`)

The reverse drift — owned on OzServer but missing from vatSys — is repaired, not released: the
tracker puts the sector back on the next poll and logs that it did.

**A session starts clean.** Everything the tracker learned about a session is discarded when it
disconnects, so a reconnect in the same vatSys run behaves exactly like a fresh start: the first
sync is a baseline, not a change to react to.

**Silence means a dropped connection.** A deliberate exit calls `POST /sectors/release-all`. A
crash sends nothing, and that silence is what makes the server hold the sectors for the resume
window.

**Four things can hurt a live session**, and the code around them is deliberately careful:

- vatSys renders on its own thread, and neither its render loop nor its paint path catches
  exceptions caused by plugin data. An exception there stops the scope drawing for good. See
  `AsdMapLayer`.
- The primary/group rules decide what is released to an arriving controller. A mistake does not
  raise an error; it hands someone's airspace away. `PrimaryPosition` is the one definition and
  every caller must agree with it.
- vatSys does not catch exceptions on its UI thread either: one escaping a posted delegate or an
  event handler ends the process. Marshal through a class's `RunOnUiThread` helper, which wraps
  the work in `UiThread.Guard` and reports a failure in the Errors window instead.
- Reflection into vatSys internals is checked by no compiler. Each site records why there was no
  public API for it.

## Source layout

All source is in [`OzServerPlugin/`](../OzServerPlugin).

### Entry point and infrastructure

| File | Role |
| --- | --- |
| `Plugin.cs` | MEF entry point; menu entries and the incoming-request indicators |
| `OzServerApiClient.cs` | HTTP client and DTOs for the OzServer API |
| `OzServerEventStream.cs` | The Server-Sent Events stream: change signals, each answered with a sync |
| `OzServerSettings.cs` | The OzServer API address (fixed - not user-configurable) |
| `OzServerMode.cs` | The session-wide *Disable OzServer* switch |
| `NetworkIdentity.cs` | The session's CID and callsign |
| `NetworkServer.cs` | Which network the session is on: live, SweatBox 1 or SweatBox 2 |
| `PluginUpdater.cs` | Self-update, staged to take effect at the next vatSys start |
| `ActionLog.cs` | The daily `ozserver_<date>.txt` log |
| `ClientLogForwarder.cs` | Forwards log lines to `POST /client-logs` |
| `ErrorThrottle.cs` | Stops a repeating failure flooding the vatSys Errors window |
| `ErrorReporter.cs` | Copies every error the plugin raises into the log, so it is forwarded to OzServer |
| `UiThread.cs` | Contains exceptions thrown by work the plugin runs on vatSys's UI thread |
| `MmiSectorWatch.cs` | Logs what removes a sector from `MMI.SectorsControlled` |

### Sector ownership

| File | Role |
| --- | --- |
| `OzServerOwnershipTracker.cs` | Owned sectors, kept in step with `MMI.SectorsControlled` and VSCS in both directions |
| `AfvSectorClaimer.cs` | Turns VSCS transmit state into sector claims and releases |
| `VscsTransmitMemory.cs` | Remembers which VSCS lines were transmitting, for a reconnect |
| `PrimaryPosition.cs` | Which sectors a position takes when its controller logs on |
| `PrimaryPositionWatcher.cs` | Gives a position back to its controller if they were online first |
| `GracefulDisconnectReleaser.cs` | Releases everything on a deliberate exit |
| `ObserverPositionMirror.cs` | Shows an observer the airspace of the controller they are watching |
| `SectorChangeRequest.cs` · `SectorDescription.cs` | A pending request as the window shows it; how a sector is written in notices |

### Windows

| File | Role |
| --- | --- |
| `OzServerSectorsWindow.cs` | The Owned / Planned Changes / Available–Controlled window |
| `SectorConflictPromptWindow.cs` | vatSys-styled Accept/Reject prompt |
| `SectorNoticeWindow.cs` | One-way notice with an OK button |
| `SectorMessageWindow.cs` | Shared chrome for the two dialogs above |
| `ToggleGenericButton.cs` | A `GenericButton` that can be held depressed |
| `NotificationSound.cs` | The incoming-request tone |

### Scope drawing

| File | Role |
| --- | --- |
| `AsdMapLayer.cs` | A map layer of the plugin's own, drawn by vatSys's renderer |
| `RequestedSectorOverlay.cs` | Shades sectors in a pending request |
| `PendingSectorGhosts.cs` | Draws the aircraft a staged or requested sector would bring |

### Flight data and tags

| File | Role |
| --- | --- |
| `FdrSync.cs` | Batches flight-data pushes for aircraft this controller holds |
| `SectorTagHandoff.cs` | Hands tags over with a sector, and accepts them on the receiving side |
| `LabelState.cs` | Carries cleared level, scratchpad and label data across a handover or reconnect |
| `TagResumeRecovery.cs` | Restores this controller's own tags after a reconnect |
| `FdrResumeActivation.cs` | Re-activates and positions other recent flights after a reconnect |
| `ServerFdrSnapshot.cs` | The most recent server-side flight records, shared by the resume paths |
| `SectorLocator.cs` | Which sector an aircraft is physically inside |
| `CoupleAssist.cs` | Couples an activated flight plan to its radar track by callsign |
| `FmpLevelFilter.cs` | Applies the 998–999 level filter on flow positions |

### ATIS

| File | Role |
| --- | --- |
| `AtisSync.cs` | Pushes vatSys's built-in ATIS |
| `BadVectorsAtisSync.cs` | Publishes [ATISPlugin](https://github.com/badvectors/ATISPlugin)'s broadcasts, read by reflection |

`AfvSectorClaimer` is adapted from [badvectors/VatpacPlugin](https://github.com/badvectors/VatpacPlugin)
(`Sectors.cs`). The `Extending ...` controller-info line that came with it is not included, so the
two plugins do not edit the same field.

## API endpoints

All under `{BaseUrl}/api/v1`. Every call carries `controller_cid`, `controller_callsign` and
`server`. The API itself is documented in the
[OzServer-API README](https://github.com/JoshuaMicallefYBSU/OzServer-API#api-reference).

| Endpoint | Used for |
| --- | --- |
| `GET /sectors/sync` | Owned, controlled-by-others and requests in one read; the ownership poll |
| `POST /sectors/{name}/claim` | Claim, optionally with an `exclude` list after a conflict |
| `POST /sectors/{name}/release` | Release |
| `POST /sectors/{name}/request` | Request from the current owner |
| `POST /sectors/commit` | Apply the Sectors window's staged changes in one transaction |
| `POST /sectors/release-all` | Give up everything on a deliberate disconnect |
| `POST /sectors/resume` | Recover sectors, and the tags to restore, after a dropped connection |
| `POST /sector-requests/{id}/accept` · `/reject` · `/cancel` | Single-request actions |
| `POST /sector-requests/accept-batch` · `/reject-batch` | Answer several at once |
| `POST /sector-requests/{id}/acknowledge-rejection` | Confirms a rejection was shown, which deletes it |
| `GET /events?server=` | Server-Sent Events: a signal to re-sync, never data |
| `POST /fdr/batch` | Flight-data upserts, keyed by callsign |
| `GET /fdr/sync` · `GET /fdr/{callsign}` | Server-side flight records, for label state and resume |
| `POST /atis` | The ATIS handover record, keyed by ICAO and server |
| `PUT /vatsim/atis/{icao}` | Publishes the broadcast itself for the public map; live network only |
| `POST /client-logs` | Forwarded log lines |
| `GET /plugin/release` | Which release the updater should install |

## Timings

| | |
| --- | --- |
| Ownership sync | every 3 s; 1 s while a claim or request is pending; 2 s while the Sectors window is open; immediately on an event |
| Flight-data flush | every 5 s |
| Log forwarding flush | every 5 s |
| ATIS refresh while on air | every 20 min |
| ATISPlugin rescan | every 5 s |
| Event stream reconnect | 2 s, backing off to 30 s |
| HTTP timeout | 20 s; 6 s for the ownership sync |
| Concurrent HTTP connections | 8 |
| Update check | 3 s after start, then every 6 h, with a 30 s timeout; retried after 2 min (up to 3 times) if neither source can be reached |

## Self-update

`PluginUpdater` keeps the installed DLL current without interrupting a session.

**Choosing a release.** The updater asks OzServer (`GET /plugin/release`), which serves a cached
copy of this repository's GitHub releases. If OzServer cannot answer — it is unreachable, is an
older build without the endpoint, or the session has OzServer disabled — the updater asks GitHub's
releases API directly. Prereleases count; drafts do not.

**Verifying it.** Whichever source answered:

1. the download URL must be under this repository's GitHub releases — a changed base URL or a
   compromised server can choose among published releases, but cannot supply a file
2. the downloaded bytes must match the release asset's SHA-256, where one is published
3. the file must be a managed assembly named `OzServerPlugin` with a version higher than the one
   running, read from its metadata without loading it

**Installing it.** vatSys holds the plugin DLL open for the whole session, so it cannot be
overwritten — but Windows allows a loaded file to be renamed:

1. the new DLL is downloaded beside the current one as `OzServerPlugin.dll.update`
2. the running DLL is renamed to `OzServerPlugin.dll.backup`
3. `.update` is moved into its place
4. the next vatSys start loads the new DLL and deletes the backup

Neither staging file ends in `.dll`, so vatSys never tries to load one. The backup is only deleted
once a later session has started, so a release that fails to load can be undone by renaming it
back.

Failures to reach either source are logged and retried at the next check. A successful install is
reported once, in the vatSys Errors window — the only notification surface a plugin has.

## Matching the vatSys look

Every window is a `BaseForm` and takes its palette from `Colours.GetColour`, so it follows the
loaded profile.

| Element | Colour identity |
| --- | --- |
| Window backgrounds | `WindowBackground` |
| Labels | `GenericText` |
| Text boxes, trees, the message pane | `InteractiveText` |
| Staged rows, flashing headings | `WindowWarning` |
| Buttons | `GenericButton`'s own defaults |

Rules for anything that touches styling:

- **Never set `BackColor` on a `GenericButton`.** It paints itself and applies its own hover and
  pressed states.
- **Do not set `Font` on vatSys controls.** They already default to the right face and size. Where
  a plain WinForms control needs a font, take it from `MMI.eurofont_winsml` or
  `MMI.eurofont_winverysml`.
- **Show windows with `ShowWithPlacement(owner)`**, which restores the last position and size,
  keyed on `Control.Name` — so every form needs a unique `Name`.
- **Use `SectorConflictPromptWindow` or `SectorNoticeWindow`, never `MessageBox`**, which ignores
  the profile.
- **Menus use vatSys's own renderer** (`ComboField.DefaultMenuRenderer`), so they match the
  built-in dropdowns.

## vatSys behaviours the code depends on

- **Plugins are discovered with MEF.** Implementing `IPlugin` is not enough; the class also needs
  `[Export(typeof(IPlugin))]`.
- **Only one copy of each assembly may be visible to vatSys.** The project references
  `vatSys.exe`, `VATSYSControls.dll`, `Newtonsoft.Json.dll` and `RossCarlson.Vatsim.Network.dll`
  with `Private=False`, and builds to a flat folder. A second copy beside the plugin makes MEF see
  two `IPlugin` types, and the plugin silently fails to load.
- **`SectorsVolumes.Sector` overrides `Equals` and `GetHashCode` but not `==`.** Two instances of
  the same sector compare unequal with `==`. Always use `.Equals`.
- **vatSys clears Transmit before it sets Receive** on a VSCS line, so Transmit → Receive is told
  apart from Transmit → Idle on `ReceiveChanged`, not `TransmitChanged`.
- **A newly online controller's sectors are stripped from everyone else's list**, and vatSys stops
  tracking tags pointed at them. `SectorTagHandoff` re-points those tags first so they stay
  tracked until the handover is accepted.

[vatsys-internals-audit.md](vatsys-internals-audit.md) records a detailed audit of the vatSys
internals the plugin relies on.
