# Contributing

## Reporting something

Open an issue and fill in the form. **Attach the log** — `Documents\vatSys Files\ozserver_YYYYMMDD.txt`.
Nearly every bug in this plugin has been found in it, and a report without one usually ends with us
asking for it anyway.

If the bug involves two controllers — ownership, requests, shared markup — say what the *other*
controller was holding and doing. Half of this plugin is two clients agreeing with each other, so a
one-sided report is often only half the story.

## Changing something

The [README](README.md) is the reference for how the thing actually works — read
[Layout](README.md#layout) before moving code around, and
[Notes for contributors](README.md#notes-for-contributors) before cutting a release.

A few things about this codebase that aren't obvious:

**There is no CI build.** Compiling needs a vatSys installation, and its assemblies aren't ours to
redistribute to a build runner. The compiler on your machine is the only automated check there is,
so test against a real install and say what you exercised in the PR.

**Some of this can freeze somebody's radar.** vatSys renders on its own thread, and neither its
render loop nor its paint block catches the exceptions plugin data can cause — an exception there
stops the scope drawing permanently rather than dropping a frame. Anything touching map layers,
text areas or the FDR state machine needs the care described in `AsdMapLayer`'s own comments.

**Some of this can take airspace off a controller mid-session.** The primary/group rules decide what
gets released to an arriving controller. Getting them wrong doesn't produce an error — it silently
hands someone else's sectors away. `PrimaryPosition` is the single definition; three callers depend
on it agreeing with itself exactly.

**Reflection into vatSys is checked by nothing.** Where the plugin reaches into vatSys internals it
does so because there is no public API for it, and the reasoning is written down at each site. A
compiler error is not available to tell you when one of those moves; only testing is.

## Comments

Explain *why*, not *what*. The code says what it does. The comments exist for the decisions that
took a while to work out and would otherwise be quietly undone — which is most of the hard-won
behaviour in here.
