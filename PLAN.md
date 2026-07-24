# PLAN.md

Roadmap for this project — what we're building and in what order. `CLAUDE.md` covers how
to work in the repo today; `context.md` covers the history and lessons behind it; this
file covers where it's going.

## The mission

Soulbrandt is ultimately a **full recreation of Demon's Souls (2009, PS3) on a modern
engine (Godot)**, in the spirit of asset-required fan recreations like Dusklight (Twilight
Princess) or Daggerfall Unity: the project itself will **never contain any proprietary
FromSoftware code or assets**. Everything playable comes from the user's own legally-owned
copy of the game, read at runtime. This isn't a launch-time legal footnote — it's a
constraint that should shape design decisions throughout both phases below. If a feature
only works by bundling or redistributing original assets/data, it's the wrong design.

## Phase 1 — Asset importer bridge (current focus)

The near-term goal is a Godot pipeline that can read every asset type Demon's Souls
ships and turn it into native Godot resources, well enough that a full level with
correctly-textured, correctly-animated characters and geometry can actually be opened
and walked around in the editor. This phase is infrastructure, not gameplay.

**Done:**
- Static mesh + material import for both character/object models and map geometry
  (FLVER0 → `ArrayMesh`/`StandardMaterial3D`/`ImageTexture`, no intermediate file
  formats). As of 2026-07-24, driven by a manual loader (`FlverModelBuilder.cs` +
  `FlverLoader.cs`, triggered from the Archstone toolbar's "Load Model(s)..."/"Load
  Folder..."), not a Godot `EditorSceneFormatImporter` — the res:// reimport pipeline
  was dropped entirely after it turned out to be the source of a real hang/crash cycle
  Godot itself gives plugins no way to control; see CLAUDE.md's "Architecture" section
  and context.md's reimport-concurrency entry for the full story. This also means the
  loader logic now has zero `EditorPlugin`/`EditorInterface` dependency, which is a real
  (if partial) step toward item 1 below, not just an unrelated refactor.
- Texture resolution for the chr/obj same-basename-TPF convention, the map
  shared-area-bucket convention, and cross-category reuse (a map piece using another
  category's texture set wholesale — see CLAUDE.md's "Texture resolution" section).
- Alpha/cutout and alpha-blend material wiring (foliage, hair).
- **The in-editor asset-mounting system** (the editor-side half of item 1 below): a
  "Mount..." action reads the user's raw game copy directly — `.bnd`/`.dcx`
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
   data, sound, and any other formats a real level depends on haven't been scoped at all
   yet. Expect this list to grow as Phase 1 continues; don't treat it as complete. One
   concrete addition found while investigating unrelated `obj/` texture questions: **a
   Havok-driven destructible-debris system** (`map/breakobj/*.breakobj`, an undocumented
   FromSoft-specific format, magic header `OBJB`) that a cluster of dummy-only `obj/`
   FLVERs (e.g. `o6511`-`o6602`) appears to depend on for their actual visible geometry
   at runtime — see CLAUDE.md's "Known deferred work" and context.md's "Destructible-prop
   debris cluster" entry for the full investigation. Not started, not even confirmed yet
   beyond the working theory. Lighting/fog/environment work (`LightID`/`FogID`,
   `WorldEnvironment`) used to be listed here too — that's been folded into item 5 below,
   since it turned out to share the same MSB-parsing prerequisite as the rest of that
   item, not a standalone gap.
5. **A future "map assembler"** — resolving which map-piece FLVERs actually belong in a
   given scene (not just "every loose file in the folder", see the "not every FLVER in a
   map folder is placed" finding in context.md), plus handling triggers/events/entity
   placement. Not started, not scoped. Several concrete things already known that this
   will need, found while investigating unrelated questions across sessions (see
   context.md's "map-piece naming investigation" and "Lightmap/drawparam system" entries
   for the full stories):
   - **MSB (level layout) parsing is a hard prerequisite**, not an optional nicety — it's
     the only source of "what's actually placed, where, with what transform" for a given
     map, and (see below) the only source of per-instance lighting/fog data too.
     `SoulsFormatsNEXT`'s `MSB1` reader currently **cannot read Demon's Souls' own MSBs at
     all** - it assumes Dark Souls 1's `Treasure` event field layout
     (`MSB1/EventParam.cs`, `Treasure.ReadTypeData`'s trailing `AssertInt16(0)`), which
     doesn't match this game's real data and throws immediately, aborting the whole read
     before even reaching the Parts section. Needs the same kind of hand-patch already
     applied once to this vendored library for the FLVER0 UV-factor bug - fixing this is
     the actual first step of building this system, not a side detail.
   - **Per-part lighting/fog (`LightID`/`FogID`) resolution belongs here, not as a
     separate system** — confirmed real and readable this session, not just a lead
     anymore: DeS's own `MSBD.PartsParam.LightID`/`FogID` byte fields (`SoulsFormatsNEXT`
     already parses these, currently unused) index into `LIGHT_BANK`/`FOG_BANK` rows in
     `param/drawparam/<map>_lightbank.param`/`<map>_fogbank.param` - loose, `PARAM`/
     `PARAMDEF`-readable files (no BND unpacking needed), confirmed to hold exactly the
     two-color hemisphere-ambient/directional-light/fog-distance data the shader work
     below already assumes. See context.md's "Lightmap/drawparam system" parts 5-8 for
     the full trail, including the specific row shape and field names. **Why this can't
     be resolved at the importer's current per-file granularity**: a single `.flver`
     (e.g. a common wall/prop piece) can legitimately be placed multiple times across a
     map with different `LightID`s, so "bake the right ambient into the material once, at
     import time" (what the importer does for everything today) doesn't fit. The
     assembler needs to set each *placed instance's own* shader uniforms
     (`ambient_up`/`ambient_down`/`env_color`/`env_intensity`/`env_spc_color`/
     `env_spc_intensity`, already wired into `addons/archstone/hemisphere_ambient.gdshaderinc`
     and shared by `lightmap_common.gdshaderinc`/`terrain_blend.gdshader` - see CLAUDE.md's
     "Lightmaps" section) as a per-node material override at placement time via Godot's
     `set_shader_parameter`, rather than relying on the shared `default_lightbank.param`
     row-0 fallback every lightmapped material currently uses. `FogID` likely drives real
     per-map distance fog (`FOG_BANK`'s `fogBeginZ`/`fogEndZ`/color/intensity, already
     confirmed readable) via `WorldEnvironment`'s fog settings once that node exists (see
     below) - whether it meaningfully varies per-part within one map, or is effectively
     one dominant value per map in practice, isn't known yet; find out once real MSB data
     is actually being walked instead of assuming either way.
   - **A real `WorldEnvironment`/tonemap/exposure setup belongs here too, not as a
     separate task.** CLAUDE.md's water and lightmap sections both already note that
     brightness/contrast comparisons so far have been against the editor's own opaque
     default preview lighting, not a controlled baseline, because no `WorldEnvironment`
     exists anywhere in this project yet - every shader-tuning decision made before this
     exists is provisional. The assembler is the natural place to build one: it's the
     first point where a whole real map scene gets assembled (not a single loose test
     mesh loaded by a throwaway script), which is exactly when a scene-level
     `WorldEnvironment` node needs to exist anyway. Building it as part of the assembler,
     rather than as a one-off test-scene hack, avoids re-deriving it later.
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
     entry instead - same underlying prerequisite as the points above. See context.md for
     the specific files this was found in.

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

A preliminary (curiosity-driven, not yet acted on) investigation already found where
most of this phase's *data* actually lives, and confirmed none of it needs any DRM
circumvention — see context.md's "Non-visual game-logic data investigation" entry for
the full picture. Headline findings worth remembering when this phase actually starts:
stamina/combat/AI-behavior numbers are fully exposed via PARAM (with paramdefs shipped
in the game itself, unlike later titles that need the community `Paramdex` project), map
event scripting and full per-enemy AI logic are plain, unencrypted Lua source under
`script/` (same `.bnd`/`.dcx` reading `AssetExtractor.cs` already does, just not yet
extended to that category), and per-animation timed-event data (hit windows, the kind of
thing that would encode dodge-roll invincibility frames) lives in real, already-readable
`.tae` files bundled in each character's `.anibnd` — `SoulsFormatsNEXT`'s `TAE` reader
already has an explicit `TAEFormat.DES` case and parses these directly. The two real
gaps: TAE event *type IDs* have no friendly names without a template (same
template-needed relationship PARAMDEF has to PARAM — none sourced or built for DeS yet),
and precise i-frame/hyper-armor frame counts don't appear to be documented anywhere
publicly for this specific title (unlike Dark Souls 3, which the community has mapped
in detail) — filling that second gap will likely mean direct frame-by-frame observation
against the real game in RPCS3, not extraction.

## Standing constraints (apply to both phases, always)

No proprietary Demon's Souls code, assets, or extracted data get committed to this
repo, ever — not as test fixtures, not "just for now," not compressed/obfuscated. Every
mechanism for reading game data must assume it lives entirely outside the repo, on a
copy the user already owns.

Stability and resource safety in the extraction/import pipeline is a standing priority,
not a nice-to-have — see `CLAUDE.md`'s "Standing priority: stability and resource
safety" section for the concrete evidence and what it means for how importer/extractor
work gets done. The mission above only works if a regular user can eventually run this
machinery themselves, unsupervised, against their own full game copy — it has to be
built to hold up to that, not just to this dev machine.
