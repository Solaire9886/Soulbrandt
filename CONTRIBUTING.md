# Contributing to Soulbrandt

Thanks for taking a look. This is an early-stage hobby project (see `PLAN.md` for the
roadmap), so expect rough edges — that's not a reason to hold back contributions, just
context for what "done" looks like right now.

## Before anything else

Read `README.md`'s Disclaimer section. The short version: no proprietary game assets,
extracted files, ROMs, or decryption/DRM-bypass methods get posted here — in code,
issues, PRs, or discussion. Bug reports with in-game screenshots are fine (normal
practice for this kind of project); uploading or linking raw extracted game files
(`.flver`, `.tpf`, `.bnd`, `.dcx`, etc.) is not.

Then read `CLAUDE.md` (architecture, current conventions, known gotchas), `PLAN.md`
(where this is headed and why), and `context.md` (the real bugs already found and fixed,
and the dead ends already tried and rejected — worth checking before you re-try
something that looks like an obvious fix). These are the actual living documentation for
this project; this file is just the process wrapper around them.

## Setup

```
git clone --recurse-submodules git@github.com:Solaire9886/Soulbrandt.git
```
(or `git submodule update --init` after a plain clone — this project vendors a patched
fork of [SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT) as a submodule)

Build after any change under `addons/archstone/`:
```
dotnet build Soulbrandt.csproj
```

See `CLAUDE.md`'s "Build & verify" section for the full picture, including how to force
a reimport after changing importer code.

There's no unit test suite — verification is done with throwaway `SceneTree` GDScript
scripts run headlessly against your own mounted game copy. `CLAUDE.md` has an example.
Don't commit these scratch scripts.

## What to work on

Check `PLAN.md`'s roadmap and the "Known deferred work" section at the bottom of
`CLAUDE.md` for what's open — skeleton/animation import, the FLVER0 vertex-format crash
on a known subset of files, lightmap reading, and LOD selection are all real, scoped
gaps, not just ideas. If you want to tackle something not listed there, open an issue
first to talk it through before sinking time into it.

## Submitting changes

- Fork, branch, keep PRs focused on one thing.
- Commit messages should explain *why*, not just what — match the existing log's style.
- If your change touches something `CLAUDE.md` documents (a convention, a format
  quirk, a gotcha), update `CLAUDE.md` in the same PR. It goes stale fast otherwise.
- By submitting a PR you agree your contribution is licensed under this project's
  license (GPLv3, see `LICENSE`).

## Reporting bugs

Check `CLAUDE.md`'s known-issues notes first — some failures (like the FLVER0 vertex
crash on a specific vertex/UV layout) are expected on a known set of files. A report
matching that same exception type/line isn't new information; a *different* exception,
a new affected-file pattern, or a big jump in failure count is.
