# vatSys internals audit

An engineering-reference audit of every point where OzServerPlugin touches vatSys, checked against
vatSys's actual decompiled source rather than against the plugin's own comments about it. The plugin
has no official API documentation beyond the thin `vatsys.Plugin` surface (`IPlugin`/`ILabelPlugin`/
`IStripPlugin`), so most of what it relies on is inferred behavior — some of it public API, a lot of it
reflection into private/internal members. This document exists so a future change (ours or vatSys's)
can be checked against ground truth instead of against inherited assumptions.

**This document is our own analysis and does not contain vatSys source code.** It cites specific
classes, members, and line numbers so the claims can be re-verified, but vatSys's decompiled source
itself is not vatSys's to redistribute and is not committed anywhere in this repo.

## Methodology

- vatSys version audited: **0.4.9305.35667** (file version of the installed `vatSys.exe`), decompiled
  2026-09-24.
- Decompiled with [ilspycmd](https://github.com/icsharpcode/ILSpy) 8.2.0.7535 in project mode
  (`ilspycmd -p -o <dir> "<vatSys install>\bin\vatSys.exe"`), and the same for `VATSYSControls.dll`
  and `RossCarlson.Vatsim.Network.dll`.
- Every plugin source file that touches `vatsys`, `vatsys.Plugin`, `VATSYSControls`, or
  `RossCarlson.Vatsim.Network` was read, and every specific factual claim in its comments (there are
  many — this codebase documents its own reverse-engineering as it goes) was checked line-by-line
  against the real decompiled member it depends on.
- To redo this after a vatSys update: decompile the new `vatSys.exe`/`VATSYSControls.dll` the same way,
  then re-check every entry under [Fragile internals registry](#fragile-internals-registry) below —
  that list is exactly the set of private/undocumented members this plugin's correctness depends on.

---

## Actionable findings (ranked by real-world impact)

These are the only items that look like actual bugs rather than confirmed-correct-but-fragile
internals. Items 1, 3, 4, and 6 have since been fixed (see each item); item 2 is still open.

### 1. `GracefulDisconnectReleaser`'s graceful-shutdown path is dead code in the common case

**Fixed.** `GracefulDisconnectReleaser` no longer hooks `MainForm.FormClosing` at all — it now checks
`vatsys.BaseForm.AppClosing` (public get) from its existing `Network.Disconnected` handler, which is
set by `mainForm_FormClosing` right before it calls `Network.Instance.Disconnect()` on a confirmed
exit. This also fixed a second bug the original `FormClosing` hook had, that this audit didn't
originally catch: because vatSys's own `mainForm_FormClosing` unconditionally sets `e.Cancel = true`
before showing its confirm prompt, and only undoes that by disconnecting and recursively closing on
Yes, a second `FormClosing` subscriber (this class) still ran even when the prompt was answered
**No** — and since `Network.IsConnected` was still `true` at that point (no disconnect ever
happened), the old code released the controller's sectors on a close they had just cancelled. Keying
off `Network.Disconnected`/`AppClosing` instead means this class simply never runs at all unless
`Network.Instance.Disconnect()` actually executed.

**Original finding:** vatSys's own `MainForm.mainForm_FormClosing` is wired in `MainForm`'s
constructor, so it is always the *first* `FormClosing` subscriber. The plugin's handler was only
attached later, via `Application.Idle`, so it always ran *second*. `mainForm_FormClosing` calls
`Network.Instance.Disconnect()` **synchronously** before it does anything else. `Disconnect()` runs
through to `FSDSession.Disconnect()`, which nulls the socket (so `Network.IsConnected` is already
`false`) and raises `Network.Disconnected` **synchronously**, nested inside that same call. By the
time the plugin's own `FormClosing` handler finally ran, `Network.IsConnected` was already `false`,
so its own guard (`if (!Network.IsConnected) return;`) bailed out immediately — the blocking
`ReleaseAllSectorsAsync().Wait(...)` call never executed. Net effect: closing vatSys normally while
connected did not release sectors through either path the class's own header comment documented; it
silently fell into the same 5-minute ungraceful-disconnect hold every time.

### 2. `AtisSync` misses the moment an ATIS actually goes live

**Open.** `AtisSync.cs` subscribes to `ATIS.Updated`. The network-visible ATIS commit happens in
`ATIS.BroadcastATIS()` (via `Network.Instance.UpdateATISInfo` + `SendNewATISMessages`), which
**never raises `ATIS.Updated`**. `ATISEditorWindow.saveButton_Click` also unconditionally sets
`broadcastButton.Checked = false` before calling `UpdateATIS`/`UpdateATISNoTTS`, which starts
`StopBroadcastingATIS()` on a background `Task.Run` — racing the synchronous `UpdateATIS` call and
the `Updated` event it raises on the UI thread.

Net effect: a station's **first** broadcast after opening is never pushed to OzServer —
`IsBroadcasting` is still `false` when `Updated` fires at Save time, and the subsequent manual
Broadcast toggle (the actual go-live) raises no event at all. It only catches up on the *next* letter
change. This directly undercuts the plugin's stated purpose of letting a controller taking over pull
back the last-broadcast ATIS.

### 3. `AfvSectorClaimer` and `GracefulDisconnectReleaser.OnDisconnected` don't marshal to the UI thread

**Fixed.** Both classes now route their vatSys event handlers through a `RunOnUiThread` helper
(the same `Application.OpenForms["MainForm"]`/`InvokeRequired`/`BeginInvoke` pattern already used by
`PrimaryPositionWatcher`, `ObserverPositionMirror`, and several other classes in this plugin) before
touching `MMI.SetControlledSectors` or `Application.OpenForms`/`Form` state:
`AfvSectorClaimer`'s `Network.Connected` handler, its `Audio.VSCSFrequenciesChanged` handler, and its
`System.Threading.Timer`-driven `RetryInit` all now run through it; `GracefulDisconnectReleaser`'s
single `Network.Disconnected` handler does too.

**Original finding:** `AfvSectorClaimer.cs` (its `Network.Connected`/`Audio.VSCSFrequenciesChanged`/
retry-timer handlers) and `GracefulDisconnectReleaser.cs`'s `OnDisconnected` called into
`MMI.SetControlledSectors`/`Application.OpenForms`/`Form.IsDisposed`/`Form.Visible` directly, without
marshaling. vatSys never installs a `SynchronizationContext` on its `FSDSession` (confirmed:
`SetSyncContext` is never called anywhere in `Network.cs`), and the socket model is fully async
(`BeginConnect`/`BeginReceive`), so `Network.Connected`/`Disconnected` genuinely fire on a
ThreadPool/socket-callback thread, not the UI thread — a real cross-thread WinForms access, not a
theoretical one.

### 4. `SectorLocator` mis-resolves sub-sectors for tracks without an established Mode C

**Fixed.** `SectorLocator.Resolve` now returns `null` instead of guessing when the track's altitude
is unresolved (`CorrectedAltitude == -1`) and more than one candidate remains after the existing
primary/sub-sector filter — i.e. exactly the case where two vertically-stacked sibling sub-sectors
both match because `IsInSector(point, -1)` ignores vertical bounds entirely and there is nothing left
to disambiguate them by. A real Mode C return re-asks and resolves it properly on a later update.

**Original finding:** `SectorLocator.cs` passed `RadarTrack.CorrectedAltitude` straight through as
the `level` argument to `SectorsVolumes.Sector.IsInSector`. `CorrectedAltitude` defaults to the
sentinel `-1` when altitude is unresolved, and `IsInSector` treats `level == -1` as "ignore vertical
bounds entirely" — so a track with no Mode C yet matched purely on lateral footprint against every
vertically stacked volume, undermining the "most specific sub-sector wins" logic used by `FdrSync`'s
`current_sector` and `SectorTagHandoff`'s geometry matching, and (when two sibling sub-sectors both
matched) resolving to whichever happened to come first in the candidate list. Low-frequency (only
affects tracks without an established Mode C — e.g. very early after acquisition), but silent and
unmentioned in `SectorLocator.cs`'s own comments.

### 5. Minor / cosmetic

- `Plugin.cs`'s comment attributes the request-window title-bar flash to "the ATIS window" — the real
  vatSys precedent for `BaseForm.FlashTitleBar` is `ChatWindow`, not `ATISWindow` (which never calls
  it). No functional effect, just a misleading pointer for a future reader.
- The debug menu's "Conflict (Yes/No)" preview (`Plugin.cs`) shows `SectorConflictPromptWindow`
  non-modally via `Show()`, but the real code path always shows it modally via `ShowDialog`. The
  preview doesn't reproduce the real window's modality, unlike the `SectorNoticeWindow` preview next to
  it, which does.
- `NotificationSound.cs`'s comment claims the sound is "loaded up front" — it's actually lazily loaded
  on the first real `PlayRequestArrived()` call, with no warm-up call anywhere in `Plugin.cs`. Trivial
  disk I/O, no real impact.
- The `OzServerPlugin.csproj` comment about a stray nested build-output DLL causing MEF to see "two
  distinct IPlugin types" describes the wrong mechanism: `Plugins.cs` already dedupes loaded assemblies
  by identity string before composition, so the real risk in that scenario is *staleness/ambiguity*
  (whichever duplicate the directory scan happens to load first), not a silent composition failure. The
  practical advice — keep the plugin output folder flat — is still correct.

### 6. `SectorTagHandoff.GiveAway` could silently drop an aircraft instead of handing it off

**Fixed.** `MMI.HandoffJurisdiction(fdr, to)` (`MMI.cs:2035`) only sends a real handoff PDU when
`to.Callsign` is already present in `Network.Instance.OnlineATC`. If it isn't yet, it takes a
completely different, undocumented branch: `Network.Instance.StopTracking(fdr)` (broadcasts a drop,
sets `ControllerTracking = null`) followed by a **local-only** `FDP2.AcceptJurisdiction(fdr, to)` —
which, since the aircraft is now untracked, resolves `fdr.State` to `STATE_UNCONTROLLED`. No PDU
ever reaches the new controller; the aircraft is just released to nobody. `GiveAway`'s own header
comment previously assumed `MMI.HandoffJurisdiction` always "travels over vatSys's own protocol,"
unaware of this fallback.

`Network.OnlineATC` is populated from a periodic `ATCPosition` broadcast (`Network.cs:2399`), not an
instant announcement on connect — this plugin already documents that exact lag elsewhere
(`OzServerOwnershipTracker.cs`, `PrimaryPositionWatcher.cs`: "VATSIM's own update cycle a few
seconds") for a different symptom of the same fact. The scenario most likely to hit this: a
controller logs on under a primary's callsign and OzServer reassigns that primary's sectors to them
immediately (see the README's "Primary inheritance"), but the *previous* holder's client can run
`GiveAway` before its own `OnlineATC` snapshot has caught up to the new controller's connection.

`SectorTagHandoff` now checks `Network.GetOnlineATCs` itself before ever calling
`MMI.HandoffJurisdiction`. If the target isn't visible yet, it still reassigns
`fdr.ControllingSector` immediately (protecting the aircraft from `MMI.SetControlledSectors`'s
own imminent old/new-sector cleanup, exactly as the existing reassignment already did) but leaves
the real handoff PDU to a new `RetryDeferredHandoffs` timer (2s interval, 60s window) that completes
it once the target actually appears online. The aircraft is never dropped from tracking while it
waits — traced through `MMI.SetTrackState`'s `STATE_CONTROLLED` branch (`MMI.cs:1086-1125`), the
only visible effect of deferring is the tag cosmetically fading to `NonJurisdiction` on the giving
controller's own scope after `PostJurisdictionTimeout` (20s) elapses, which reverses itself the
moment the deferred handoff completes.

---

## Subsystem-by-subsystem detail

### FDR / radar-track / sector-tag sync (`FdrSync`, `SectorTagHandoff`, `OzServerOwnershipTracker`, `PendingSectorGhosts`, `TagResumeRecovery`, `SectorChangeRequest`, `SectorLocator`, `SectorDescription`)

Confirmed correct, including several load-bearing internal-only facts:

- `FDP2.FDR.FDRStates` ordinals are exactly what `SectorTagHandoff.cs` assumes:
  `STATE_COORDINATED=5`, `STATE_HANDOVER_FIRST=7`, `STATE_CONTROLLED=8`, `STATE_HANDOVER=9`.
- `FDR.IsTrackedByMe` / `ESTed` match the plugin's gating logic verbatim.
- `MMI.AcceptJurisdiction`/`HandoffJurisdiction` branch exactly as documented (send `PDUHandoffAccept`,
  then `FDP2.AcceptJurisdiction`).
- `RDP.RadarTrack` sentinel defaults (`CorrectedAltitude=-1`, `GroundSpeed=-1.0`, `Heading=-1.0`,
  `VerticalSpeed=-9999.0`) match `FdrSync.FillPosition`'s guards.
- `MMI.SetTrackState`'s `STATE_CONTROLLED` branch is gated on `SectorsControlled.Contains(...)` with
  `IsTrackedByMe` preferring `Jurisdiction` over `GhostJurisdiction` exactly as `PendingSectorGhosts.cs`
  assumes, and `PostJurisdictionTimeout` really is 20000ms.
- `MMI.SetControlledSectors`'s old/new-sector diff cleanup and `MMI.HandoffToNone`'s
  `StopTracking`-only-if-`IsTrackedByMe` gating match the reasoning behind
  `OzServerOwnershipTracker`/`SectorTagHandoff`'s event ordering.
- `vatsys.Plugin.Plugins`' dispatch (`OnUpdateFDR`/`OnRadarTrackUpdate`) really is one `Task.Run` per
  update with a per-plugin `try`/`catch` inside the loop — confirms the concurrency and exception model
  every subsystem here assumes (see [Fragile internals registry](#fragile-internals-registry)).

Issues: see [#4](#4-sectorlocator-mis-resolves-sub-sectors-for-tracks-without-an-established-mode-c)
above (fixed).

One claim flagged unverified rather than confirmed: `TagResumeRecovery.cs`'s comment that
"`HandoffFirst` only queues the state change; accepting has nothing to resolve until that has been
through a process pass" doesn't clearly follow from the decompiled code — `FDP2.HandoffFirst` sets
`fdr.State` synchronously, and `FDP2.Process` only enqueues route/trajectory recalculation, never
touching `State`/`HandoffSector`. The surrounding `State > 5` logic is still correct; this specific
mechanism justification looks like an empirically-tuned workaround description rather than something
the decompiled code substantiates.

### Network / connection / position (`NetworkServer`, `NetworkIdentity`, `PrimaryPosition`, `PrimaryPositionWatcher`, `GracefulDisconnectReleaser`, `ObserverPositionMirror`, `AfvSectorClaimer`)

Confirmed correct:

- `Network.Connect` writes `callsign`/`controllerID`/`rating`/`facility` synchronously — no async delay
  before those fields are readable.
- `NetworkATC.IsRealATC` genuinely lags `Connected` by however long the `"Y"`-payload PDU round trip
  takes, confirming why the plugin keys `IsObserver` off `Rating`/`Facility` instead of `IsRealATC`.
- `Network.Disconnect()` never resets `callsign`/`controllerID`/`rating`/`facility`/`serverIp` — they
  hold their last value after disconnect, as `NetworkServer.Current`/`NetworkIdentity.Current` assume.
- Both the clean-logoff and error-triggered disconnect paths raise `Network.Disconnected` — confirms
  `GracefulDisconnectReleaser`'s central premise (the *reachability* of the release logic was the
  actual problem — see [#1](#1-gracefuldisconnectreleasers-graceful-shutdown-path-is-dead-code-in-the-common-case)).
- `MMI.SetControlledSectors` really does backfill a dummy sector via
  `SectorsVolumes.CreateDummySector` when the session's own sector is missing.
- RossCarlson's `NetworkFacility`/`NetworkRating` enum values line up exactly with how
  `ConnectWindow` populates its dropdowns, validating `NetworkIdentity.IsObserver`.

Issues: see [#1](#1-gracefuldisconnectreleasers-graceful-shutdown-path-is-dead-code-in-the-common-case)
and [#3](#3-afvsectorclaimer-and-gracefuldisconnectreleaserondisconnected-dont-marshal-to-the-ui-thread)
above (both fixed).

### Map drawing / rendering (`AsdMapLayer`, `MapDrawing`, `RequestedSectorOverlay`, `ProfileColours`, `DrawModeHotkey`)

This is the highest-risk area in the whole plugin — it works entirely through reflection into private
vatSys internals (`ASDControlDX.SetMapVisible`/`IsMapVisible`, `Colours.customColoursDX`,
`DisplayMaps.Maps`'s private setter) to draw a custom overlay layer on the radar scope, since vatSys
exposes no public "draw a custom map" API to plugins.

**Every single factual claim in `AsdMapLayer.cs`'s detailed internals comment checked out exactly**
against the decompiled `ASDControlDX.cs`/`DisplayMaps.cs`/`Colours.cs`/`RenderParams.cs` — the render
loop's exception boundaries, the `Join` against `VisibleMaps`, `Map.Id`'s generation, the
`computedPolygonVectors` indexer hazard, the degenerate-polygon `BeginFigure`/`EndFigure` asymmetry, and
every reflected member's exact name and signature. No mismatches found. See the
[fragile internals registry](#fragile-internals-registry) for the full list of exactly what to re-check
first after any vatSys update, since this is where an update is most likely to silently break the
plugin.

One real gap, not a reflection break: `DrawModeHotkey.cs`'s "D is safe, nothing collides with it"
reasoning only covers the WinForms message pump. vatSys also runs `DirectInputCapture`, a separate
background loop polling raw keyboard state for user-configurable `InputMaps` bindings, entirely
independent of `WM_KEYDOWN`. A controller who has personally remapped "D" via Options' input-mapping UI
would get a silent double-action (the plugin's draw toggle plus whatever they bound) — not a crash, no
error, just an unexplained side effect. Only matters for controllers who've customized their input map.

### UI / menu integration (`Plugin.cs`'s menu logic, `OzServerSectorsWindow`, `OzServerSettingsWindow`, `SectorMessageWindow`, `SectorNoticeWindow`, `SectorConflictPromptWindow`, `ToggleGenericButton`)

Every specific claim about `MainForm.LoadPluginMenuItem`'s placement rules, the built-in Sectors menu
item's designer name (`sectorsToolStripMenuItem`), `MenuRenderer`'s Name/Tag/Checked-driven coloring and
flash logic, the `"[N]"` badge via `ShortcutKeyDisplayString`, `BaseForm.ShowWithPlacement`'s
`Control.Name`-keyed placement persistence, and `BaseForm.FlashTitleBar`'s existence/behavior — all
checked out exactly against `MainForm.cs`, `MenuRenderer.cs`, and `BaseForm.cs`. The only two issues are
the minor/cosmetic ones listed under [Actionable findings item 5](#5-minor--cosmetic).

### ATIS / audio / shared notes (`AtisSync`, `BadVectorsAtisSync`, `NotificationSound`, `SharedNotes`)

`BadVectorsAtisSync`'s frequency-format round trip (`Conversions.FSDFrequencyToString`/
`VSCSFrequencyToFSDFrequency`) and `SharedNotes`'s claim that it reads/writes vatSys's own native text
areas (`ASDControlDX.TextAreas`/`AddTextArea`/`RemoveTextArea`, all private, plus the `internal`
`vatsys.ASDTextArea` type, located via reflection) both checked out exactly — `SharedNotes`'s "vatSys
has no native sharing, only local UI" framing is accurate, not overstated. The one substantive miss is
[`AtisSync`'s missed-broadcast issue](#2-atissync-misses-the-moment-an-atis-actually-goes-live) above
(still open); `NotificationSound`'s "loaded up front" comment is inaccurate but harmless (see
[Actionable findings item 5](#5-minor--cosmetic)).

### Plugin loading / MEF (`Plugins.cs` internals, `PluginUpdater`, `ActionLog`, `ClientLogForwarder`, the `.csproj`'s `Private=False` reasoning)

Confirmed correct: vatSys scans `<dataset path>\Plugins` and `<program folder>\bin\Plugins` for `*.dll`
via `Assembly.LoadFrom` with **no shadow-copying** anywhere in `Plugins.cs`, so the file handle stays
open for the process's lifetime — confirming why `PluginUpdater` has to stage-and-rename rather than
overwrite in place. Composition is one-shot (`Plugins.Load()` runs once, no reload path). Every plugin
dispatch method (`OnUpdateFDR`, `OnRadarTrackUpdate`, `GetCustomLabelItem`, `GetCustomStripItem`,
`GetCustomTrackColour`) wraps each individual plugin's call in its own `try`/`catch`, so a throwing
plugin can't affect siblings. The `Private=False` reasoning for `vatSys.exe` and
`RossCarlson.Vatsim.Network.dll` is concretely correct — `IPlugin` and `NetworkRating`/`NetworkFacility`
really do cross the plugin/host boundary as shared types, so a second differently-versioned copy really
would be a distinct CLR type. The `Newtonsoft.Json.dll` reasoning is good hygiene but slightly
overstated — no vatSys API actually passes a `Newtonsoft` type across the plugin boundary in this
plugin's usage, so the risk there is assembly-version contention in one process, not a hard
type-identity coupling. The one real inaccuracy is the "two distinct IPlugin types" framing (see
[Actionable findings item 5](#5-minor--cosmetic)).

---

## Fragile internals registry

Everything below is **verified correct today** against vatSys 0.4.9305.35667, but depends on a private,
internal, or otherwise undocumented vatSys implementation detail with no public contract. Re-check this
list first — before anything else — after any vatSys update, since these are exactly the members a
vatSys refactor is most likely to change without notice.

**Map drawing (`AsdMapLayer.cs`) — highest risk, all reflection targets:**

- `ASDControlDX.SetMapVisible(DisplayMaps.Map, bool)` / `IsMapVisible(DisplayMaps.Map)` — public, but
  the only way in; no other public API adds a map to `VisibleMaps`.
- `ASDControlDX`'s private `GetRenderParams(bool)`, and the exact try/catch shape of
  `Render`/`RenderLoop`/`ComputeAllMapElements`/`PaintMaps` (which exception types are caught where).
- `ASDControlDX.PaintMap`'s `computedPolygonVectors[id]` indexer lookups (not `TryGetValue`) and the
  `BeginFigure`/`EndFigure` asymmetry for infills with zero points.
- `DisplayMaps.Map.Id`'s field initializer and private `GenerateId()`.
- `DisplayMaps.Maps`'s private `set_Maps` (plus the `<Maps>k__BackingField` fallback).
- `Colours.customColoursDX`, the private static `Dictionary<string, SharpDX.Color>` reflected into to
  register a custom map colour.
- `Colours.GetCustomColorDX`/`CustomColourExists` fallback behavior for an unregistered name.
- `DisplayMaps.Loaded`'s get-only semantics.
- `ASDWindow.ASD`/`MainForm.ASD` being private fields (why the plugin walks the control tree by type
  name instead).

**Network / disconnect:**

- `NetworkATC.IsRealATC`'s dependency on the private `validATC` field and its PDU-timing.
- `Network.Disconnect()` never clearing identity fields.
- `vatsys.BaseForm.AppClosing` (public get, internal set) being set `true` by `MainForm`'s
  `FormClosing` handler *before* it calls `Network.Instance.Disconnect()` on a confirmed exit —
  `GracefulDisconnectReleaser` now depends on this exact ordering to distinguish a deliberate close
  from a crash.
- vatSys's `FSDSession` never installing a `SynchronizationContext` — the entire plugin's
  UI-thread-marshaling strategy assumes `Network` events arrive off the UI thread. If a future vatSys
  build ever calls `SetSyncContext`, existing `RunOnUiThread`/`InvokeRequired` checks become harmless
  no-ops — but the *reverse* is what's currently relied on.
- `ConnectWindow`'s hardcoded `Name = "ConnectWindow"`, matched by string since the plugin can't
  reference the type directly.
- `MMI.SetControlledSectors`'s dummy-sector backfill via `SectorsVolumes.CreateDummySector`.

**FDR / sector sync:**

- `FDP2.FDR.FDRStates` numeric ordinals.
- `MMI.AcceptJurisdiction`/`HandoffJurisdiction`'s internal branching.
- `MMI.SetTrackState`'s per-state table and the 20s `PostJurisdictionTimeout`.
- `MMI.SetControlledSectors`'s old/new diff cleanup and `MMI.HandoffToNone`'s `StopTracking` gating.
- `RDP.RadarTrack`'s sentinel field defaults (undocumented as a contract, just field initializers).
- `SectorsVolumes.Sector.IsInSector`'s `level == -1` special case — `SectorLocator` now guards against
  the ambiguous case this creates (see
  [#4](#4-sectorlocator-mis-resolves-sub-sectors-for-tracks-without-an-established-mode-c)), but the
  underlying vatSys behavior is still exactly what it is; re-verify it after any update.
- `vatsys.Plugin.Plugins`' `Task.Run`-per-update, `try`/`catch`-per-plugin dispatch shape.
- `MMI.HandoffJurisdiction`'s undocumented branch on whether the target sector's callsign is present
  in `Network.Instance.OnlineATC` — real handoff PDU if present, `StopTracking` + local-only
  `FDP2.AcceptJurisdiction` (silently dropping the aircraft) if not. `SectorTagHandoff` now checks
  this itself first (see
  [#6](#6-sectortaghandoffgiveaway-could-silently-drop-an-aircraft-instead-of-handing-it-off)); a
  future vatSys update changing this branching would need that fix revisited.
- `Network.OnlineATC`'s population source (a periodic `ATCPosition` broadcast, not an instant
  connect-time announcement) and `Network.GetOnlineATCs`'s exact backing (`Instance?.OnlineATC.ToList()`
  — the same list `MMI.HandoffJurisdiction` itself checks, which is what makes checking it from the
  plugin a reliable predictor).
- `MMI.SetTrackState`'s `STATE_CONTROLLED`/`PostJurisdiction` branch and the 20s
  `PostJurisdictionTimeout` fade to `NonJurisdiction` — what makes a deferred handoff's only visible
  side effect cosmetic rather than destructive.

**UI / menu:**

- `MainForm`'s `sectorsToolStripMenuItem` designer field name (private control name — no public
  contract; falls back to matching on the `"Sectors..."` label text, itself fragile to a copy change).
- `MenuRenderer`'s hardcoded control-name literals and its `Tag`-driven badge/flash logic.
- `MMI.BaseFormPlacements`, the dictionary `BaseForm.ShowWithPlacement` uses, keyed by `Control.Name`.

**ATIS / notes:**

- `ASDControlDX.TextAreas`/`AddTextArea`/`RemoveTextArea` (private) and the `internal`
  `vatsys.ASDTextArea` type, located via reflection.
- `ATIS.Content` being replaced by reference (not mutated in place) inside `UpdateATIS`/
  `UpdateATISNoTTS` — relied on for safely reading it from an event handler.
- `Conversions.FSDFrequencyToString`/`VSCSFrequencyToFSDFrequency`'s exact numeric formulas.

**Plugin loading:**

- The exact scanned folders (`<dataset path>\Plugins`, `<program folder>\bin\Plugins`) — private to
  `Plugins.cs`'s constructor, no public contract.
- The one-`try`/`catch`-per-plugin-per-call isolation granularity in `Plugins.cs`'s four dispatch
  methods.
- `Assembly.LoadFrom` without shadow-copying (no `AppDomainSetup` anywhere) — the whole basis for
  `PluginUpdater`'s stage-and-rename design. If a future vatSys version shadow-copies plugin assemblies,
  the stage/rename dance becomes unnecessary but stays harmless (`PluginUpdater` already checks the
  installed path is a real file and degrades safely).
