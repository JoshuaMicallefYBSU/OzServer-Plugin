# OzServer Plugin

A [vatSys](https://virtualairtrafficsystem.com/) plugin that keeps sector ownership in sync with the
OzServer backend, so every controller sees the same picture of who owns what — and can hand sectors
between each other without leaving the scope.

Claim a sector by transmitting on it, extending into it, or logging on under its callsign. Ask
another controller for one they hold. Accept, reject, or cancel requests. Whatever happens on
OzServer flows back into vatSys and onto your VSCS panel automatically.

---

## What it does

| | |
| --- | --- |
| **Two-way ownership sync** | Anything that changes your controlled sectors — logging on under a position's callsign, the built-in Sectors window, a VSCS/AFV transmit press, a profile auto-set, another plugin — is claimed on OzServer. A sector gained or lost *on OzServer* is pushed back into `MMI.SectorsControlled` and its VSCS line switched to Transmit or Idle to match. |
| **Sector requests** | Ask another controller for a sector they own. Accepting the `Requested From Me` heading accepts every incoming request in one batch; accepting a row accepts just that one. A denial is reported back to whoever asked. |
| **Conflict handling** | Claiming a primary that bundles sub-sectors someone else already holds takes everything that *isn't* contested and leaves the rest with their owner, reporting which. It never turns those into requests behind your back — asking is something you do explicitly, by staging the sector you want. |
| **Primary inheritance** | Logging in under a position's own callsign takes that position's sectors regardless of who was holding them. Whoever had them is told, and gives them up. A primary is never kept from its own controller. |
| **Disconnect handling** | A deliberate exit (closing vatSys, or pressing Disconnect) releases your sectors immediately. A crash or dropped connection says nothing at all, and that silence is what holds them for 5 minutes so reconnecting picks up where you left off. |
| **Request indicator** | An incoming request flashes a trail inward — `Settings`, then `OzServer Sectors` once that menu is open, then the `Requested From Me` heading — plus the window's own title bar, the way the ATIS window does. |
| **Extending line** | Keeps an `Extending ...` line in your Controller Info in step with the extra VSCS lines you're transmitting on. APP/DEP and CTR positions only. |
| **Tag ownership** | An aircraft's tag follows the live geographic subsector it's physically in: sitting in one you own, on OzServer, gets it activated (if its flight plan hasn't been) and assumed (if nobody holds it) automatically; losing that subsector on OzServer hands off whatever you were tracking there. |
| **Flight data** | Pushes FDR and radar-track updates to OzServer in 5-second batches, only for flights you currently hold (`IsTrackedByMe`) and have activated, attributing datalink authority as your own session's identity. Each push also reports the real geographic subsector the aircraft is physically inside of right now, independent of who (if anyone) owns it. |

Ownership has exactly one source of truth: **OzServer's own record**. Every action calls the relevant
endpoint and then re-reads it rather than guessing locally what the result must have been.

---

## Installing

1. Download the latest build, or build it yourself (see [Building](#building)).
2. Copy `OzServerPlugin.dll` into your profile's plugin folder:
   `Documents\vatSys Files\Profiles\<your profile>\Plugins\`
3. Restart vatSys.

vatSys also loads plugins from `<install dir>\bin\Plugins`, which applies to every profile but needs
administrator rights to write to.

Both windows live under **Settings**, slotted in directly beneath the built-in *Sectors...* entry:

- **Settings → OzServer Sectors**
- **Settings → OzServer Settings**

### Staying up to date

Step 1 is only needed once. After that the plugin updates itself from this repository's
[GitHub releases](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/releases), silently and with
nothing to click. It checks two minutes after vatSys starts and every six hours afterwards.

A running session is never disturbed: **an update always takes effect at the next vatSys start**, not
the moment it is found. That isn't a policy choice, it's the only thing Windows permits. vatSys holds
`OzServerPlugin.dll` open for the whole session, so it cannot be overwritten or deleted while
running — which is exactly why installing a build by hand means closing vatSys first. It *can* be
renamed, though, so `PluginUpdater` stages the swap instead:

1. download the new DLL beside the current one, as `OzServerPlugin.dll.update`
2. verify it before it is allowed anywhere near the live name
3. rename the running DLL to `OzServerPlugin.dll.backup`, and move `.update` into its place
4. next start, vatSys loads the new one and the `.backup` is deleted

Neither staging file can be mistaken for a plugin — vatSys enumerates `*.dll`, and these end in
`.update`/`.backup` (short names `OZSERV~1.UPD` / `OZSERV~1.BAC`, so the 8.3 wildcard trap doesn't
apply either). A half-finished download is never something vatSys tries to load.

Step 2 is the one that matters. A download is only installed if it is a real managed assembly, named
`OzServerPlugin`, with a version genuinely higher than the running one — read via
`AssemblyName.GetAssemblyName`, which reads the metadata without loading the file, because whether it
can be trusted is not a question to answer by first running it. A truncated download or an error page
saved under a `.dll` name is discarded, not installed.

Nothing is deleted while it might still be needed. Step 4 only runs once the new version has
successfully loaded, so if a release ever ships something that won't load, the previous version is
still sitting there as `.backup` and **recovering is renaming that one file back**.

A successful install is the one thing that does say so, as a single line in the vatSys Errors window:

> `OzServer: An update (0.1.2) was detected and installed. It will be loaded at the next vatSys launch.`

Not an error, but that window is the only notification surface vatSys gives a plugin, and it behaves
well for the purpose — `ErrorWindow.AddError` shows it with `SW_SHOWNOACTIVATE`, so it appears above
the main form without taking focus off whatever the controller is doing. It's worth the one line: the
controller is now running a version that is no longer the one on disk, and restarting is what closes
that gap.

Failures, by contrast, are quiet by design: being offline, or the repo having no releases yet, goes to
the daily `ozserver_<date>.txt` log and nothing else. There is nothing a controller can do about it
mid-session, and the version they already have keeps working.

---

## Using it

### The Sectors window

Three panes, left to right:

| Pane | Shows |
| --- | --- |
| **Owned** | What OzServer says you currently own. Grouping sectors nest their covered sub-sectors underneath, with a `*` on any sector that bundles others. |
| **Requested Changes** | `Requested By Me` (outgoing) and `Requested From Me` (incoming). Both are plain headings rather than dropdowns — they never fold away. Select the `Requested From Me` heading to accept every incoming request at once, or a single row for just that one. |
| **Available / Controlled** | Toggles between two genuinely different data sets, described below. |

Sectors are grouped under **Flow / Centre / Approach / Tower / Other**, derived from the callsign
suffix in your profile's `Sectors.xml`.

**Available vs Controlled** is not one list filtered two ways:

- **Available** — claimable right now: nobody is live on that frequency on VATSIM *and* OzServer has
  no ownership record for it. Both checks are needed — a controller who reached a sector by
  *extending* into it is not logged in under that sector's callsign at all, so the live-frequency
  test alone never sees them and the sector looked claimable when it was not.
- **Controlled** — OzServer holds an active ownership record for it, owned by someone else, with
  their callsign shown. A stray callsign somebody is logged into but never claimed through the
  plugin correctly does *not* appear here.

Nothing is sent to OzServer until **Apply**. Moving a sector across only *stages* it: the row turns
yellow (the profile's `WindowWarning` identity) and stays that way until it is committed.

| Control | What it does |
| --- | --- |
| `<<` | Stages the selected Available sector into Owned. |
| `>>` | Stages the selected Owned sector out. |
| `<<>>` | Shown, disabled, when neither list has a selection — matching `vatsys.SectorsWindow`'s own button exactly. |
| **Apply** | Commits every staged move: claims what is free, releases what you unpicked, and sends a request for anything another controller owns. |
| **Cancel** | Throws the staged selection away and goes back to what OzServer says you own. |

The arrow button acts only on **Owned** and **Available**. A row in Requested Changes is a request in
flight, not a sector sitting somewhere it can be moved out of — everything you can do to one
(Accept, Reject, Add, Remove) is on its **middle-click menu**, which is also where Expand/Collapse
lives for every other row. Left click selects; left click on a category heading opens it.

Staging a sector another controller owns puts it straight under `Requested By Me` in yellow, rather
than pretending it is yours until Apply reveals otherwise.

While the window is open it polls once every 2 seconds. Owned does not need polling — the tracker
keeps it current whether the window is open or not.

### Claiming by transmitting

Pressing Transmit on a VSCS line is the normal way to claim:

- A **bare sub-sector's** line (e.g. `SNO`) adds only that sector. Extending into SNO should never
  also hand you WOL and its siblings.
- A **primary's** line (e.g. `WOL`) adds it *and its whole group*, matching what "extend" means on
  the scope. Whether each sub-sector is actually claimable is then resolved once, on OzServer.

Releasing is never inferred from a sector merely disappearing from `MMI.SectorsControlled`. There are
exactly four things that release: an applied `>>`, a genuine conflict, a position's own controller
logging on (`PrimaryPositionWatcher`), and a deliberate disconnect (`GracefulDisconnectReleaser`).

---

## Configuration

**Settings → OzServer Settings** sets the backend base URL. It defaults to `https://ozserver.org` and
is stored in `%AppData%\OzServerPlugin\settings.json` — not beside the DLL, since the vatSys
`Plugins` folder isn't guaranteed writable.

Only `https://` is accepted, with `http://` allowed for loopback addresses so a local dev server
still works. Every request carries the plugin token, so this is a security boundary rather than a
convenience — see [Security](#security). A URL hand-edited into `settings.json` is held to the same
rule and ignored (with an entry in the vatSys error log) if it fails.

**Note for backend maintainers:** since a flight is now only ever pushed to while a controller
actually holds it (see Tag ownership / Flight data above), a row nothing has pushed to in 10 minutes
is stale and safe to drop server-side — the same precedent as the existing 90-minute ATIS TTL. Not
implemented in this repo; there is no backend code here to implement it in.

Every `/fdr`, `/fdr/batch` push now also sends `current_sector` (nullable string) — the *name* of the
geographic subsector the aircraft is physically inside of, independent of `controlling_cid`/
`controlling_callsign`, which say who owns the tag. `UpdateFlightDataRecordRequest` and the flight
data table need a matching field before this is actually persisted or queryable; until then the
plugin sends it but the backend has nowhere to put it.

---

## Building

**Requirements**

- .NET SDK with `net472` targeting support, or Visual Studio 2022
- vatSys installed

The project references `vatSys.exe`, `VATSYSControls.dll` and vatSys's own `Newtonsoft.Json.dll`
directly, with `Private=False` so none of them are copied into the output. That is deliberate: a
second copy of `vatSys.exe` next to the plugin DLL makes MEF composition see two distinct `IPlugin`
types and the plugin silently fails to load.

**Steps**

1. Create `OzServerPlugin/Secrets.cs` from the template and fill in the token:

   ```bash
   cp OzServerPlugin/Secrets.cs.example OzServerPlugin/Secrets.cs
   ```

   It must match `PLUGIN_TOKEN` in the OzServer backend's `.env`. The file is gitignored. Leaving it
   empty still compiles — no `Authorization` header is sent, and the backend answers with its own
   *"Invalid or missing plugin token"* rather than something more confusing.

2. Build:

   ```bash
   dotnet build OzServerPlugin/OzServerPlugin.csproj -c Release
   ```

vatSys is located via `HKLM\SOFTWARE\WOW6432Node\Sawbe\vatSys@Path`. For a non-default install, pass
the path explicitly — the build fails early with a clear message if it can't find one:

```bash
dotnet build OzServerPlugin/OzServerPlugin.csproj -c Release -p:VatSysPath="D:\vatSys"
```

The project targets **x86** to match vatSys's own architecture. A mismatch means the plugin will not
load into the vatSys process at all.

---

## Security

The plugin authenticates with a **single shared bearer token compiled into the assembly**. Two things
follow from that, and both matter:

- **`PluginToken` is `const`, so the compiler inlines its value into the IL at every use site.**
  Gitignoring `Secrets.cs` protects the source file, not the build output. Never commit `bin/` or
  `obj/` — `.gitignore` covers both, and a compiled DLL gives the token up to `strings` in seconds.
- **The token proves "this is a plugin build", not "this is a legitimate controller."** Every
  controller running the plugin holds the same value, and the backend takes the CID and callsign each
  request supplies at face value once it checks out. Per-controller authentication (VATSIM Connect)
  would be the durable fix.

If the token is ever exposed, rotating `PLUGIN_TOKEN` in the backend `.env` is the only thing that
actually revokes it. Rewriting git history does not — anything already cloned, forked, or cached
keeps working.

**The updater executes what it downloads**, on the next vatSys start, so it is worth being explicit
about what does and does not guard that:

- Transport is HTTPS to `api.github.com` and GitHub's own asset host, with TLS 1.2 forced (4.7.2
  otherwise picks its protocol from an older default). The URL is a `const` — nothing the backend or
  a settings field says can redirect it.
- Only a real managed assembly named `OzServerPlugin`, versioned higher than the running one, is
  installed. That catches corruption and mistakes.
- It does **not** verify a signature or a publisher. Anyone who can publish a release to this
  repository can therefore run code inside every controller's vatSys. **Write access to the repo is
  the trust boundary** — treat it accordingly, and keep releases restricted to people who should have
  that. Signing the assembly and checking the key before install is the durable fix, in the same way
  VATSIM Connect is for the token above.

---

## Matching the vatSys look

Every window is a `BaseForm` and takes its palette from `Colours.GetColour`, so it follows whatever
the loaded profile defines:

| Element | Identity |
| --- | --- |
| Window backgrounds | `WindowBackground` |
| Labels | `GenericText` |
| Text boxes, trees, the message pane | `InteractiveText` (matches `vatsys.TextField`) |
| Staged rows, flashing headings, the request trail | `WindowWarning` (BrightYellow in the Australia profile) |
| Buttons | left entirely to `GenericButton`'s own defaults |

Four rules if you touch the styling:

- **Never set `BackColor` on a `GenericButton`.** It paints itself in `OnPaint`, filling with
  `BackColor` and drawing text in `ForeColor`, and its constructor already applies the right
  defaults. `WindowButtonSelected` and `WindowButtonDepressed` are hover and press states it applies
  itself.
- **Don't set `Font` on vatSys controls either.** `GenericButton` already defaults to Terminus 18px
  bold, `TextLabel` to 16px bold, and `BaseForm` to 16px bold, which controls inherit. Where a plain
  WinForms control genuinely needs a font — the URL box, the prompt's message pane — take it from
  `MMI.eurofont_winsml` / `MMI.eurofont_winverysml` rather than naming the face in a string literal.
- **Show windows with `ShowWithPlacement(owner)`, not `Show()`.** It restores the position and size
  the controller last left the window at, keyed on `Control.Name` — which is why each form sets a
  unique `Name`. An owner also keeps the window above the maximised main form.
- **Use `SectorConflictPromptWindow` / `SectorNoticeWindow`, not `MessageBox`.** An OS message box is
  the one thing on screen that ignores the profile entirely.
- **Menus borrow vatSys's own renderer.** `ComboField.DefaultMenuRenderer` is the instance `MainForm`
  builds and every built-in dropdown uses, so the middle-click menu is identical to a native one by
  construction rather than being a close repaint of it (`vatsys.MenuRenderer` is itself private).

---

## Layout

| File | Role |
| --- | --- |
| `Plugin.cs` | MEF entry point; menu items and the incoming-request flash trail |
| `PluginUpdater.cs` | Silent self-update from GitHub releases, staged so it lands at the next vatSys start |
| `OzServerOwnershipTracker.cs` | Source of truth for owned sectors; syncs both ways with MMI/VSCS |
| `OzServerApiClient.cs` | HTTP client and DTOs for the backend API |
| `OzServerSectorsWindow.cs` | The Owned / Available / Controlled / Requested Changes window |
| `OzServerSettingsWindow.cs` | Base-URL settings window |
| `SectorConflictPromptWindow.cs` | vatSys-styled Yes/No prompt, in place of `MessageBox` |
| `SectorMessageWindow.cs` | Shared chat-styled chrome for the two dialogs below it |
| `SectorNoticeWindow.cs` | One-way notice (OK only) — position relinquished, request denied |
| `PrimaryPosition.cs` | The one definition of which sectors a position takes when its controller logs on |
| `PrimaryPositionWatcher.cs` | Hands those sectors back when a position's own controller appears |
| `GracefulDisconnectReleaser.cs` | Releases everything on a deliberate exit, and stays silent on a crash |
| `VatSysContextMenu.cs` | Middle-click menu, drawn with vatSys's own `MenuRenderer` |
| `SectorChangeRequest.cs` | One pending request, as the window renders it |
| `AfvSectorClaimer.cs` | Turns VSCS transmit state into `MMI.SectorsControlled` changes |
| `ControllerInfoUpdater.cs` | Maintains the `Extending ...` Controller Info line |
| `FdrSync.cs` | Batches FDR and radar-track pushes, gated on currently holding and having activated the flight |
| `TagOwnershipSync.cs` | Activates/assumes a tag when it's sitting in a subsector you own; hands it off when OzServer says you no longer own that subsector |
| `SectorLocator.cs` | Resolves which sector, out of a given candidate list, an FDR is physically inside of right now |
| `AtisSync.cs` | Pushes vatSys's own built-in ATIS (`vatsys.ATIS`, slot 0) on change |
| `BadVectorsAtisSync.cs` | Same, but for [badvectors/ATISPlugin](https://github.com/badvectors/ATISPlugin)'s up-to-4 broadcasts, via reflection |
| `OzServerSettings.cs` | Base-URL persistence and validation |
| `ToggleGenericButton.cs` | A `GenericButton` that can be held visually depressed |

`AfvSectorClaimer` and `ControllerInfoUpdater` are ported from
[badvectors/VatpacPlugin](https://github.com/badvectors/VatpacPlugin) (`Sectors.cs` and
`Extending.cs`).

### Backend endpoints

All under `{BaseUrl}/api/v1`, with `controller_cid` and `controller_callsign` attached to every call.

| Endpoint | Used for |
| --- | --- |
| `POST /sectors/{name}/claim` | Claim, optionally with an `exclude` list after a conflict |
| `POST /sectors/{name}/release` | Release |
| `POST /sectors/{name}/request` | Request from the current owner |
| `GET /sectors/sync` | **The only GET the plugin makes.** Owned + controlled + requests in one response — the three below, which the poll used to fetch separately every tick |
| `POST /sectors/release-all` | Give up everything, on a *graceful* disconnect only; staying silent is what marks a disconnect ungraceful |
| `GET /sectors/mine` | The authoritative "what do I own" check — still routed, no longer called by the plugin |
| `GET /sectors/controlled` | Sectors owned by someone else, pre-flattened server-side — still routed, no longer called by the plugin |
| `GET /sector-requests` | Both directions of pending requests — still routed, no longer called by the plugin |
| `POST /sector-requests/accept-batch` | Accept several at once, processed sequentially |
| `POST /sector-requests/{id}/accept`, `/reject`, `/cancel` | Single-request actions |
| `POST /sector-requests/{id}/acknowledge-rejection` | Confirms a denial has been shown to the requester, which is what finally deletes it |
| `POST /fdr`, `POST /fdr/batch` | Flight data upserts, keyed by callsign |
| `POST /atis` | ATIS upsert, keyed by ICAO; dropped 90 minutes after the last update |

### Timings

| | |
| --- | --- |
| Ownership / controlled / requests sync | 10s (always), 2s while the Sectors window is visible |
| FDR batch flush | 5s |
| ATISPlugin rescan (looking for its slots) | 5s |
| HTTP request timeout | 20s |
| Max concurrent HTTP connections | 8 (raised from .NET's default of 2) |
| Update check | 2 min after start, then every 6h (30s timeout, its own client) |

All three views arrive in one `GET /sectors/sync`, so a poll tick is a single request rather than
three. A failed sync leaves all three as they were rather than half-updating them.

---

## Notes for contributors

**There is no CI build.** Compiling requires a vatSys installation, and its assemblies aren't ours to
redistribute to a build runner. Build and test locally against a real install.

**Publishing a release — bump `<Version>` and the tag together.** Everyone's copy updates itself from
GitHub releases (see [Staying up to date](#staying-up-to-date)), and it trusts the *assembly's* version,
not the tag. So a release must be:

1. `<Version>` raised in `OzServerPlugin.csproj`, and built from that
2. tagged to match (`v0.1.2` for `0.1.2` — a leading `v` is fine, it's stripped)
3. published with the built `OzServerPlugin.dll` attached as an asset under exactly that name

Tag a release `v0.1.2` but attach a DLL still stamped `0.1.1` and every client downloads it, sees it
isn't actually newer, discards it and logs why — then does the same six hours later, forever. It fails
safe rather than looping installs, but the release still reaches nobody. Drafts and prereleases are
ignored, so a release only goes out when it is actually published.

Building locally with the same `<Version>` as a published release is fine — it's only ever replaced by
a *higher* one. Leave `<Version>` behind the latest release, though, and the updater will helpfully
overwrite your own build with the released one at the next start; the `.backup` beside it is the
previous file if you want it back.

**The comments carry real history.** Several explain a specific bug a previous shape caused and why
the current one avoids it — the claim/release loop, the stack overflow from a self-referencing sector
grouping, the re-entrancy that let a request survive its own accept. Read the comment on a method
before changing it, and update it if the reasoning changes.

**Two vatSys behaviours worth knowing:**

- `SectorsVolumes.Sector` overrides `Equals`/`GetHashCode` (callsign-based) but **not** the `==`
  operator. Two instances of the same real sector reached through different lookups compare unequal
  under `==`. Always use `.Equals`.
- Plugins are discovered with MEF, so implementing `IPlugin` is not enough — the class also needs
  `[Export(typeof(IPlugin))]`, or it loads but is never instantiated.

---

## Licence

Not yet specified. Until a `LICENSE` file is added, no usage rights are granted by default.
