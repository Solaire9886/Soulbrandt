# PLAN.md

Roadmap for this project — what we're building and in what order. `CLAUDE.md` covers how
to work in the repo today; `context.md` covers the history and lessons behind it; this
file covers where it's going.

## The mission

Soulbrandt is ultimately a **full recreation of Demon's Souls (2009, PS3) on a modern
engine (Godot)**, in the same spirit as Dusklight (Twilight Princess) or Daggerfall
Unity: the project itself will **never contain any proprietary FromSoftware code or
assets**. Everything playable comes from the user's own legally-owned extracted copy of
the game, read at runtime. This isn't a launch-time legal footnote — it's a constraint
that should shape design decisions throughout both phases below. If a feature only works
by bundling or redistributing original assets/data, it's the wrong design.

## Phase 1 — Asset importer bridge (current focus)

The near-term goal is a Godot pipeline that can read every asset type Demon's Souls
ships and turn it into native Godot resources, well enough that a full level with
correctly-textured, correctly-animated characters and geometry can actually be opened
and walked around in the editor. This phase is infrastructure, not gameplay.

**Done:**
- Static mesh + material import for both character/object models and map geometry
  (FLVER0 → `ImporterMesh`/`StandardMaterial3D`/`ImageTexture`, native
  `EditorSceneFormatImporter`, no intermediate file formats).
- Texture resolution for the chr/obj same-basename-TPF convention, the map
  shared-area-bucket convention, and cross-category reuse (a map piece using another
  category's texture set wholesale — see CLAUDE.md's "Texture resolution" section).
- Alpha/cutout and alpha-blend material wiring (foliage, hair).
- **The in-editor asset-mounting system** (the editor-side half of item 1 below): a
  "Mount..." action reads the user's raw extracted game copy directly — `.bnd`/`.dcx`
  containers and all — via `SoulsFormatsNEXT`'s own `DCX`/`BND3`/`BND4` readers, no
  external tool (WitchyBND is no longer needed for the standard workflow). Also runnable
  headlessly (`extract_cli.gd`), with per-category selection. `mounted` is a real,
  extractor-populated directory now, not a symlink. See CLAUDE.md's "Asset mounting"
  section for the full picture.

**Not started / next up, roughly in order:**
1. **The in-*game* half of asset mounting** — the editor-side piece above replaces the
   old `mounted` symlink stopgap for development, but a shipped/exported build still
   needs its own runtime loader (no editor, no pre-extraction step, reading a player's
   own copy at play time). `AssetExtractor.cs` has no editor-only dependency, so it's
   usable from a future in-game flow too, but that flow itself doesn't exist yet. Still
   core to the mission above, not a side task.
2. **Skeleton/animation import** — parsing Demon's Souls' Havok (`.hkx`) animation data
   and building `Skeleton3D`/`AnimationPlayer` from it. The long-stated long-term goal
   for the importer; nothing has been scoped here yet beyond "this is next."
3. **The known FLVER vertex-format gap** — a handful of FLVERs (originally just map
   pieces, now confirmed on at least one `obj` file too now that `obj` is importable at
   all) use a different vertex/UV buffer layout the importer doesn't handle yet (see
   CLAUDE.md's known-error baseline). Small in scope, not yet started.
4. **Whatever else turns out to be needed for a walkable level** — collision/navmesh
   data, lighting/GI data, sound, and any other formats a real level depends on haven't
   been scoped at all yet. Expect this list to grow as Phase 1 continues; don't treat it
   as complete.
5. **A future "map assembler"** — resolving which map-piece FLVERs actually belong in a
   given scene (not just "every loose file in the folder", see the "not every FLVER in a
   map folder is placed" finding in context.md), plus handling triggers/events/entity
   placement. Not started, not scoped. Two concrete things already known that this will
   need, found while investigating unrelated questions this session (see context.md's
   "map-piece naming investigation" entry for the full story):
   - **MSB (level layout) parsing is a hard prerequisite**, not an optional nicety — it's
     the only source of "what's actually placed, where, with what transform" for a given
     map. `SoulsFormatsNEXT`'s `MSB1` reader currently **cannot read Demon's Souls' own
     MSBs at all** - it assumes Dark Souls 1's `Treasure` event field layout
     (`MSB1/EventParam.cs`, `Treasure.ReadTypeData`'s trailing `AssertInt16(0)`), which
     doesn't match this game's real data and throws immediately, aborting the whole read
     before even reaching the Parts section. Needs the same kind of hand-patch already
     applied once to this vendored library for the FLVER0 UV-factor bug - fixing this is
     the actual first step of building this system, not a side detail.
   - **Cutscene/event data lives in `remo/scnAAxxxx.remobnd`** (`AA` = area number, e.g.
     `scn02xxxx` for Boletaria) - each a real, structured multi-cut sequence (camera
     `.sibcam` + Havok `.hkx` animation per cut, plus a `.tae` timed-event file).
     Relevant once "triggers and entities" scope starts, not now.
   - **Some map-piece FLVERs bundle small "decoration" sub-meshes authored at local
     origin instead of real world coordinates** (a weapon/prop mesh sitting at
     `(0,0,0)` instead of where it visibly belongs), alongside a `FLVER.Node` whose name
     matches the decoration (a weapon ID, an obj ID, a descriptive label) and whose
     `Translation` looks like the right placement - but `Mesh.NodeIndex` (the field that
     should bind a mesh to that node) is uniformly `0` everywhere checked, so it's *not*
     a live, importer-readable binding. Likely resolved via a per-decoration MSB Part
     entry instead - same underlying prerequisite as the two points above. See
     context.md for the specific files this was found in.

Phase 1 is "done enough to move on" once a real map area with animated characters can be
opened and explored correctly in the Godot editor — not when every FromSoft format is
supported.

## Phase 2 — Full game recreation (future, not yet scoped in detail)

Once the importer bridge is solid, the project moves from "asset pipeline" to "actual
game": assembling imported pieces into playable levels, and building the systems needed
to actually play Demon's Souls in Godot (movement, combat, AI, itemization, UI, save
data, etc.). None of this has been designed yet beyond the mission statement above —
this section exists so future sessions know this phase is coming and why the mounting
system in Phase 1 has to be built for it, not just for editor convenience. Expect this
section to grow into real sub-plans once Phase 1 is far enough along to start it.

## Standing constraint (applies to both phases, always)

No proprietary Demon's Souls code, assets, or extracted data get committed to this
repo, ever — not as test fixtures, not "just for now," not compressed/obfuscated. Every
mechanism for reading game data must assume it lives entirely outside the repo, on a
copy the user already owns.
