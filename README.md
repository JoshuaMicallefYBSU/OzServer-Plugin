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
| **Sector requests** | Ask another controller for a sector they own. Incoming requests can be multi-selected and accepted in one batch. |
| **Conflict handling** | Claiming a primary that bundles sub-sectors someone else already holds asks once, then claims everything that *isn't* contested rather than failing the whole thing. |
| **Request indicator** | A `[n SECTOR REQUEST]` button, in the profile's own warning colour, appears in the main menu bar when someone wants a sector from you — whether or not the Sectors window has ever been opened. |
| **Extending line** | Keeps an `Extending ...` line in your Controller Info in step with the extra VSCS lines you're transmitting on. APP/DEP and CTR positions only. |
| **Flight data** | Pushes FDR and radar-track updates to OzServer in 5-second batches, attributing datalink authority from each FDR's own tracking state rather than from your session. |

Ownership has exactly one source of truth: **OzServer's own record**. Every action calls the
relevant endpoint and then re-reads `/sectors/mine` rather than guessing locally what the result must
have been.

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

---

## Using it

### The Sectors window

Three panes, left to right:

| Pane | Shows |
| --- | --- |
| **Owned** | What OzServer says you currently own. Grouping sectors nest their covered sub-sectors underneath, with a `*` on any sector that bundles others. |
| **Requested Changes** | `Requested By Me` (outgoing) and `Requested From Me` (incoming). Tick several incoming rows — or the `Requested From Me` header itself — to accept them all at once. |
| **Available / Controlled** | Toggles between two genuinely different data sets, described below. |

Sectors are grouped under **Flow / Centre / Approach / Tower / Other**, derived from the callsign
suffix in your profile's `Sectors.xml`.

**Available vs Controlled** is not one list filtered two ways:

- **Available** — nobody is live on that frequency on VATSIM right now. Checked locally against the
  live feed, so it is meaningful even for a sector OzServer has never heard of.
- **Controlled** — OzServer holds an active ownership record for it, owned by someone else, with
  their callsign shown. A stray callsign somebody is logged into but never claimed through the
  plugin correctly does *not* appear here.

| Control | What it does |
| --- | --- |
| `<<` | Claims the selected Available sector, or requests the selected Controlled one from its owner. |
| `>>` | Releases the selected Owned sector. |
| **Accept** | Accepts the ticked incoming requests, or the single selected one if nothing is ticked. |
| **Reject** / **Cancel** | Rejects an incoming request; cancels one you sent. |
| **Apply** / **Cancel** | Pushes the current selection into `MMI.SectorsControlled`, or reloads it from there. Enabled only when the two actually differ. |

Requested Changes and Controlled are polled every 10 seconds while the window is open. Owned does not
need polling — the tracker keeps it current whether the window is open or not.

### Claiming by transmitting

Pressing Transmit on a VSCS line is the normal way to claim:

- A **bare sub-sector's** line (e.g. `SNO`) adds only that sector. Extending into SNO should never
  also hand you WOL and its siblings.
- A **primary's** line (e.g. `WOL`) adds it *and its whole group*, matching what "extend" means on
  the scope. Whether each sub-sector is actually claimable is then resolved once, on OzServer.

Releasing is never inferred from a sector merely disappearing from `MMI.SectorsControlled` — only an
explicit `>>` or a genuine conflict releases anything.

---

## Configuration

**Settings → OzServer Settings** sets the backend base URL. It defaults to `https://ozserver.org` and
is stored in `%AppData%\OzServerPlugin\settings.json` — not beside the DLL, since the vatSys
`Plugins` folder isn't guaranteed writable.

Only `https://` is accepted, with `http://` allowed for loopback addresses so a local dev server
still works. Every request carries the plugin token, so this is a security boundary rather than a
convenience — see [Security](#security). A URL hand-edited into `settings.json` is held to the same
rule and ignored (with an entry in the vatSys error log) if it fails.

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

---

## Matching the vatSys look

Every window is a `BaseForm` and takes its palette from `Colours.GetColour`, so it follows whatever
the loaded profile defines:

| Element | Identity |
| --- | --- |
| Window backgrounds | `WindowBackground` |
| Labels | `GenericText` |
| Text boxes, trees, the message pane | `InteractiveText` (matches `vatsys.TextField`) |
| Request indicator | `WindowWarning`, with black or white text chosen by luminance |
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
- **Use `SectorConflictPromptWindow`, not `MessageBox`.** An OS message box is the one thing on
  screen that ignores the profile entirely.

---

## Layout

| File | Role |
| --- | --- |
| `Plugin.cs` | MEF entry point; menu items and the request indicator |
| `OzServerOwnershipTracker.cs` | Source of truth for owned sectors; syncs both ways with MMI/VSCS |
| `OzServerApiClient.cs` | HTTP client and DTOs for the backend API |
| `OzServerSectorsWindow.cs` | The Owned / Available / Controlled / Requested Changes window |
| `OzServerSettingsWindow.cs` | Base-URL settings window |
| `SectorConflictPromptWindow.cs` | vatSys-styled Yes/No prompt, in place of `MessageBox` |
| `AfvSectorClaimer.cs` | Turns VSCS transmit state into `MMI.SectorsControlled` changes |
| `ControllerInfoUpdater.cs` | Maintains the `Extending ...` Controller Info line |
| `FdrSync.cs` | Batches FDR and radar-track pushes |
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
| `GET /sectors/mine` | The authoritative "what do I own" check |
| `GET /sectors/controlled` | Sectors owned by someone else, pre-flattened server-side |
| `GET /sector-requests` | Both directions of pending requests |
| `POST /sector-requests/accept-batch` | Accept several at once, processed sequentially |
| `POST /sector-requests/{id}/accept`, `/reject`, `/cancel` | Single-request actions |
| `POST /fdr`, `POST /fdr/batch` | Flight data upserts, keyed by callsign |
| `POST /atis` | ATIS upsert, keyed by ICAO; dropped 90 minutes after the last update |

### Timings

| | |
| --- | --- |
| Ownership + pending-request poll | 10s |
| Sectors window poll (requests, Controlled) | 10s, only while visible |
| FDR batch flush | 5s |
| HTTP request timeout | 20s |

---

## Notes for contributors

**There is no CI build.** Compiling requires a vatSys installation, and its assemblies aren't ours to
redistribute to a build runner. Build and test locally against a real install.

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
