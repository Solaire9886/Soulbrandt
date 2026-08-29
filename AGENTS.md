# AGENTS.md

Operating instructions for any AI coding agent working in this repository; the substantive content lives in `docs/`.

## Where things live

- **`docs/ARCHITECTURE.md`** is the canonical reference for this project — what it is, current conventions, format quirks, known gotchas, the standing stability/resource-safety priority, and known deferred work. Read it before making non-trivial changes here. It's written for any contributor, not just Claude Code — there's nothing Claude-specific about it.
- **`docs/PLAN.md`** has the roadmap — why things are built this way and what's coming.
- **`docs/context.md`** has the session-by-session investigation history: the real bugs already found and fixed (with how they were confirmed, not just guessed), C#/Godot interop gotchas, this machine's environment quirks, and dead ends already tried and rejected. Optional, but worth checking before re-trying something that looks like an obvious fix.

## Build & verify, quick reference

- `dotnet build Soulbrandt.csproj` after any change under `addons/archstone/`. Do not use `godot-mono --headless --build-solutions` to build — it reliably hangs in this environment.
- There's no `res://` reimport step and no unit test suite. Verify with a throwaway headless GDScript check script (`godot-mono --headless --path . -s check.gd`, written to a scratch location and deleted when done — never committed). See `docs/ARCHITECTURE.md`'s "Build & verify" section for the full pattern, including why `--headless` never actually compiles GLSL and when a real-rendering-driver check (`xvfb-run -a godot-mono --path . -s check.gd --rendering-driver opengl3 --display-driver x11`) is needed instead to catch shader compile errors — and why that heavier check must never run concurrently with the GUI editor or another such check.
