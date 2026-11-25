# Contributing

## Reporting a problem

Open an [issue](https://github.com/JoshuaMicallefYBSU/OzServer-Plugin/issues/new/choose) and fill
in the form.

**Attach the log** — `Documents\vatSys Files\ozserver_<yyyymmdd>.txt`. Nearly every bug in this
plugin has been found in it.

If the problem involves two controllers — ownership, requests, a handover — say what the *other*
controller was holding and doing. Most of this plugin is two clients agreeing with each other, so
a one-sided report is often half the story.

## Making a change

Read [docs/architecture.md](docs/architecture.md) first. It covers the source layout, the rules
the code is built around, and the vatSys behaviours it depends on.

Things about this codebase that are not obvious:

- **There is no CI build.** Compiling needs a vatSys installation, and its assemblies are not ours
  to redistribute to a build runner. Build and test against a real install, and say in the pull
  request what you exercised.
- **Some of this can freeze a controller's radar.** vatSys does not catch exceptions on its render
  path. Anything touching map layers, text areas or FDR state needs the care described in
  `AsdMapLayer`.
- **Some of this can take airspace off a controller mid-session.** The primary/group rules decide
  what is released to an arriving controller, and getting them wrong produces no error.
  `PrimaryPosition` is the single definition.
- **An unhandled exception on the UI thread closes vatSys.** Post work there through the
  `RunOnUiThread` helpers, which contain it (`UiThread.Guard`).
- **Reflection into vatSys is checked by nothing.** No compiler error tells you when an internal
  moves; only testing does.

### Building

```bash
dotnet build OzServerPlugin/OzServerPlugin.csproj -c Release
```

By default the DLL is written to `OzServerPlugin/bin/Release/`. To build straight into a vatSys
profile, set `OutputPath` in a git-ignored `OzServerPlugin/OzServerPlugin.csproj.user`, or pass
`-p:OutputPath=...` on the command line.

A local build is only replaced by the updater when a *higher* version is released. If your build's
`<Version>` is behind the latest release, the updater will replace it at the next check; the
previous file is kept beside it as `.backup`.

### Comments

Explain *why*, not *what*. Comments are for constraints and decisions that are not visible in the
code — a vatSys behaviour being worked around, an ordering that matters, a consequence of getting
it wrong. Write them as facts about the code as it is now, keep them short, and update them when
the reasoning changes.

## Publishing a release

Every installed copy updates itself from this repository's GitHub releases, and it trusts the
**assembly's** version, not the tag. A release must therefore be:

1. **Versioned.** Raise `<Version>` in `OzServerPlugin.csproj`, add it to `CHANGELOG.md`, commit
   and push, and run `.\Release.ps1` from that commit. The script refuses a tree with uncommitted
   changes, a commit that is on no remote branch and a tag that already exists, and checks the
   DLL's name and version.
2. **Tagged to match.** Use `v0.1.9` for version `0.1.9`, titled `OzServer Plugin v0.1.9`. The
   `gh release create` command the script prints names the commit with `--target`; without it
   GitHub tags the tip of the default branch instead.
3. **Published with both files attached**, zip first:
   - `OzServerPlugin-v<version>.zip` (DLL and `LICENSE.txt` at the root), which the
     [vatSys Launcher](https://github.com/badvectors/vatSysLauncher) installs
   - `OzServerPlugin.dll`, which the self-updater installs
4. **A normal release, marked latest.** The launcher's listing comes from VATPAC
   (`https://vatpac.org/api/Plugins`), which follows GitHub's *latest* release, and a pre-release
   or a draft is never "latest".

The OzServer API picks a new release up within about five minutes, and each client installs it at
its next check (as vatSys opens, then every five minutes until an update is installed) and shows
the controller a window offering to restart vatSys for them. It takes effect the next time that controller starts vatSys.

Things to know:

- **Publishing announces it.** `.github/workflows/discord-release-notify.yml` posts the release's
  name and description to Discord through the `CHANGES_WEBHOOK` secret when a release or prerelease
  is published. Drafts are not announced, and editing a published release does not post again.
- **Prereleases still reach every installed copy.** The self-updater offers them like any other
  release; only the launcher ignores them. Only a draft is held back, so keep a release as a draft
  until it is ready.
- **Never rename `OzServerPlugin.dll`, or the repository.** The DLL name is the launcher's
  `DllName`, by which it finds existing installs, and the repository name is the launcher's install
  folder (`Plugins\OzServer-Plugin`). Both are also built into the self-updater.
- **The launcher compares FileVersions.** It offers an update when the installed DLL's FileVersion
  is below the version VATPAC lists, which comes from the tag. Keep the tag and `<Version>` equal.
- **A tag ahead of the assembly reaches nobody.** If `v0.1.9` carries a DLL still stamped `0.1.8`,
  every client downloads it, sees it is not newer, and discards it.
- **To pause a rollout, return the release to draft.** Nothing else stops it. Setting
  `PLUGIN_RELEASE_MAX_VERSION` on the API to the last good version only narrows it: a client that
  gets no answer from OzServer — the API is restarting or unreachable, or the session has OzServer
  disabled — asks GitHub directly and installs the newest published release. Clients that already
  installed it are not downgraded; publish a fixed, higher version for them.
- **Write access to this repository is the trust boundary.** Whatever is published here runs
  inside every controller's vatSys. Keep release rights restricted.
