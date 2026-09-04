## What this changes

<!-- What behaviour differs after this, from a controller's point of view. -->

## Why

<!-- The problem it solves. Link the issue if there is one. -->

## How it was tested

There is no CI build — vatSys's assemblies aren't ours to redistribute to a runner, so the compiler
is the only automated check and it runs on your machine. Say what you actually exercised:

- [ ] Builds against a real vatSys install
- [ ] Tested connected to the network
- [ ] Tested with a second controller (needed for anything touching ownership, requests or shared markup)
- [ ] Checked `Documents\vatSys Files\ozserver_YYYYMMDD.txt` for errors during the test

## Anything reviewers should look at closely

<!--
Worth calling out if this touches:
  - sector ownership or the primary/group rules, which can take airspace off a controller mid-session
  - anything reached from the render thread (see AsdMapLayer), where an exception stops the scope
    drawing permanently rather than dropping a frame
  - vatSys internals reached by reflection, which no compiler checks
-->

## Release

- [ ] Not a release
- [ ] Release — `<Version>` in `OzServerPlugin.csproj` bumped **and** the tag matches it

<!--
Everyone's copy updates itself from GitHub releases and trusts the assembly's version, not the tag.
A tag without a matching build ships an update nobody receives. See "Notes for contributors".
-->
