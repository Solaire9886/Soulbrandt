# PLAN.md

Roadmap for this project — what we're building and in what order. `docs/ARCHITECTURE.md`
covers how the project works today; `docs/context.md` covers the history and lessons behind
it; this file covers where it's going.

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
  Godot itself gives plugins no way to control; see docs/ARCHITECTURE.md's "Architecture" section
  and docs/context.md's reimport-concurrency entry for the full story. This also means the
  loader logic now has zero `EditorPlugin`/`EditorInterface` dependency, which is a real
  (if partial) step toward item 1 below, not just an unrelated refactor.
- Texture resolution for the chr/obj same-basename-TPF convention, the map
  shared-area-bucket convention, and cross-category reuse (a map piece using another
  category's texture set wholesale — see docs/ARCHITECTURE.md's "Texture resolution" section).
- Alpha/cutout and alpha-blend material wiring (foliage, hair).
- **The FLVER0 vertex-layout crash** (`m9999b0`/`m9900`/`o9996` — originally found on map
  pieces, later confirmed on that one `obj` file too) — some meshes' own `BufferLayout`
  genuinely omits Normal/UV/VertexColor data entirely; fixed by falling back to sane
  defaults instead of indexing unconditionally, plus a related crash one level deeper in
  a winding-flip check that assumed Normal data always existed. Verified corpus-wide
  (zero errors across the full mounted corpus) before this file's 2026-07-24 pivot away
  from the reimport pipeline — see `docs/context.md`'s "FLVER0 vertex-layout crash fixed"
  entry and `docs/ARCHITECTURE.md`'s "Architecture" section for the surviving fallback
  logic.
- **The in-editor asset-mounting system** (the editor-side half of item 1 below): a
  "Mount..." action reads the user's raw game copy directly — `.bnd`/`.dcx`
  containers and all — via `SoulsFormatsNEXT`'s own `DCX`/`BND3`/`BND4` readers, no
  external tool (WitchyBND is no longer needed for the standard workflow). Also runnable
  headlessly (`extract_cli.gd`), with per-category selection. `mounted` is a real,
  extractor-populated directory now, not a symlink. See docs/ARCHITECTURE.md's "Asset mounting"
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
   for the importer. As of 2026-08-18, no longer purely unscoped: a concrete two-source
   approach exists — an unmerged 2018 branch on our own `SoulsFormatsNEXT` upstream
   already parses Havok's packfile container format with a DeS-specific variant, extended
   with hand-coded skeleton/animation classes using a real, GPL-licensed, DeS-specific
   Python Havok animation parser (`Grimrukh/soulstruct-havok`) as the field-layout/
   algorithm reference — most notably for DeS's wavelet-compression scheme, which is
   *not* the same as later titles' spline-compression. See docs/context.md's "Havok/
   animation ecosystem research" entry for the full trail. **Deliberately still deferred,
   by explicit decision, not just unstarted:** even with parsing solved, this project has
   no skeleton/animation runtime at all yet — same "no game logic to plug into" reasoning
   already applied to `Enemy`/`Player` MSB placement below. Revisit once the graphical
   side of the importer is largely done.
3. **Whatever else turns out to be needed for a walkable level** — sound and most other
   formats a real level depends on haven't been scoped at all yet. Collision/navmesh is a
   partial exception, found 2026-08-18: `SoulsFormatsNEXT/NVM.cs` already reads DeS
   navmesh data end to end and is completely unwired (see docs/ARCHITECTURE.md's "Known
   deferred work") — a much smaller wiring task than assumed, not a missing format. HKX
   collision-mesh reading also has two known reference implementations now (same
   docs/context.md entry as above) rather than being an unknown-shape problem, though
   nothing's wired on our side yet either way. Expect this list to grow as Phase 1
   continues; don't treat it as complete.
   Another real, scoped-but-unstarted addition, found 2026-08-17 while searching for
   shader calibration data (see docs/ARCHITECTURE.md's "Known deferred work" and
   docs/context.md's "Lightmap/drawparam system, part 13"): a whole `script/` category
   (real AI/dialogue Lua + FromSoft's `.esd` state-machine format, both already readable
   via unused `SoulsFormatsNEXT` classes) that this project has never extracted or
   touched — a genuinely separate future system (game logic, not rendering), not folded
   into any phase item here yet pending a priority decision. `menu`/`sfx`/`remo`/`msg`
   are the same story at smaller scale. Separately, that same search confirmed real
   per-material shader calibration constants (water's tile/scroll/tint tuning, chr/parts
   specular cubemap values) are **not** recoverable from any game file — the real
   compiled shader binaries exist and were opened directly, but ship with no
   parameter-name table — so visual-reference calibration against real screenshots stays
   the only path for those, not a blocked-on-more-file-access item. One
   concrete addition found while investigating unrelated `obj/` texture questions: **a
   Havok-driven destructible-debris system** (`map/breakobj/*.breakobj`, an undocumented
   FromSoft-specific format, magic header `OBJB`) that a cluster of dummy-only `obj/`
   FLVERs (e.g. `o6511`-`o6602`) appears to depend on for their actual visible geometry
   at runtime — see docs/ARCHITECTURE.md's "Known deferred work" and docs/context.md's "Destructible-prop
   debris cluster" entry for the full investigation. Not started, not even confirmed yet
   beyond the working theory. Lighting/fog/environment work (`LightID`/`FogID`,
   `WorldEnvironment`) used to be listed here too — that's been folded into item 4 below,
   since it turned out to share the same MSB-parsing prerequisite as the rest of that
   item, not a standalone gap.
4. **A future "map assembler"** — resolving which map-piece FLVERs actually belong in a
   given scene (not just "every loose file in the folder", see the "not every FLVER in a
   map folder is placed" finding in docs/context.md), plus handling triggers/events/entity
   placement. Not started, not scoped. Several concrete things already known that this
   will need, found while investigating unrelated questions across sessions (see
   docs/context.md's "map-piece naming investigation" and "Lightmap/drawparam system" entries
   for the full stories):
   - **MSB (level layout) parsing is a hard prerequisite**, not an optional nicety — it's
     the only source of "what's actually placed, where, with what transform" for a given
     map, and (see below) the only source of per-instance lighting/fog data too.
     **The `MSB1`-can't-read-DeS blocker described here previously is stale — DeS uses a
     wholly separate `MSBD` reader, not `MSB1`, and `MsbLoader.cs` (2026-07-24, see
     docs/ARCHITECTURE.md's "MSB map placement") already reads real `.msb` files end to end
     via `MSBD.Read()` with no patch needed.** `MSB1`'s DS1-specific `Treasure` bug is
     simply irrelevant to this game. Re-confirmed 2026-07-26: `MSBD.Read()` (which reads
     `Events` too, not just `Parts`) completes cleanly on every `.msb` spot-checked
     (`m01_00_00_00`, `m03_00_00_00`, `m03_01_00_00`, `m08_00_00_00`) with no assert
     failures anywhere in the file, not just up to the Parts section.
   - **`MapPieces` (2026-07-24) and `Objects` (2026-08-17) are placed and lit; `Enemy`/
     `Player` are deliberately held off, not just unscoped.** See docs/ARCHITECTURE.md's
     "MSB map placement"/"`Objects` placement". User decision (2026-08-17): without any
     game-logic systems (AI, combat, animation state) built yet, placed enemy/player
     markers would be inert clutter rather than useful progress — keep MSB work on the
     graphical/model side (`Collision`/`Navmesh` are still reasonable next slices) until
     that changes.
   - **Per-part lighting/fog (`LightID`/`FogID`) resolution belongs here, not as a
     separate system** — confirmed real and readable this session, not just a lead
     anymore: DeS's own `MSBD.PartsParam.LightID`/`FogID` byte fields (`SoulsFormatsNEXT`
     already parses these, currently unused) index into `LIGHT_BANK`/`FOG_BANK` rows in
     `param/drawparam/<map>_lightbank.param`/`<map>_fogbank.param` - loose, `PARAM`/
     `PARAMDEF`-readable files (no BND unpacking needed), confirmed to hold exactly the
     two-color hemisphere-ambient/directional-light/fog-distance data the shader work
     below already assumes. See docs/context.md's "Lightmap/drawparam system" parts 5-8 for
     the full trail, including the specific row shape and field names. **Why this can't
     be resolved at the importer's current per-file granularity**: a single `.flver`
     (e.g. a common wall/prop piece) can legitimately be placed multiple times across a
     map with different `LightID`s, so "bake the right ambient into the material once, at
     import time" (what the importer does for everything today) doesn't fit. The
     assembler needs to set each *placed instance's own* shader uniforms
     (`ambient_up`/`ambient_down`/`env_color`/`env_intensity`/`env_spc_color`/
     `env_spc_intensity`, already wired into `addons/archstone/shaders/hemisphere_ambient.gdshaderinc`
     and shared by `lightmap_common.gdshaderinc`/`terrain_blend.gdshader` - see docs/ARCHITECTURE.md's
     "Lightmaps" section) as a per-node material override at placement time via Godot's
     `set_shader_parameter`, rather than relying on the shared `default_lightbank.param`
     row-0 fallback every lightmapped material currently uses. `FogID` likely drives real
     per-map distance fog (`FOG_BANK`'s `fogBeginZ`/`fogEndZ`/color/intensity, already
     confirmed readable) via `WorldEnvironment`'s fog settings once that node exists (see
     below). **`LightID`/`FogID` do meaningfully vary per-part within a single map, not
     one dominant value** — confirmed 2026-07-26 via a throwaway `MsbLoader` dump across
     4 real maps: `m01_00_00_00` (Nexus) alone has `MapPieces` spanning `LightID`
     `{0,1,3,11,255}`, `Objects` spanning `{0,1,2,4,6,11,58}`, and `Collisions` a visibly
     disjoint range `{0,58-63}` from either — collision geometry apparently draws from its
     own row block. `255` shows up only on `MapPieces`/never on `Objects`/`Collisions` in
     the maps checked, and is the likely "unset, use default" sentinel (matching this
     format's usual `0xFF`-as-unset convention) rather than a real row index — worth
     confirming once actual per-row resolution is wired, not assumed now. Per-map variety
     also confirmed real, not just per-part: `m03_00_00_00` (a near-empty shell — 4
     `MapPieces`, likely a stub the game's real Boletaria sub-blocks route through) has a
     single flat `LightID={0}`, while its populated sibling `m03_01_00_00` spans `{0,1,2}`
     on `MapPieces` and `{0,5,6,7}` on `Objects`/`Collisions`.
   - **A real `WorldEnvironment`/tonemap/exposure setup belongs here too, not as a
     separate task.** docs/ARCHITECTURE.md's water and lightmap sections both already note that
     brightness/contrast comparisons so far have been against the editor's own opaque
     default preview lighting, not a controlled baseline, because no `WorldEnvironment`
     exists anywhere in this project yet - every shader-tuning decision made before this
     exists is provisional. The assembler is the natural place to build one: it's the
     first point where a whole real map scene gets assembled (not a single loose test
     mesh loaded by a throwaway script), which is exactly when a scene-level
     `WorldEnvironment` node needs to exist anyway. Building it as part of the assembler,
     rather than as a one-off test-scene hack, avoids re-deriving it later. Confirmed
     2026-07-26 to block more than tuning: `StandardMaterial3D.Emission` (texture-driven)
     was found to render genuinely wrong - not just uncalibrated - without this, ruling
     out an Emission-based fix for the Nexus's glowing-rune/sky-dome materials until it
     exists. See docs/ARCHITECTURE.md's "Known deferred work" and docs/context.md's "Nexus
     VFX gaps investigated" entry.
   - **`LightID`/`FogID` resolution shipped 2026-08-17 (`DrawParamReader.cs`,
     `FlverLoader.InstantiateMap()`'s `ApplyLightBank`), but the env term's exact shape is
     still a stopgap, not the real formula.** `colR/G/B_u`/`_d` and the `envDif`/`envSpc`
     equivalents are 0-255 color channels; `colA_u`/`colA_d`/`envDif_colA`/`envSpc_colA` are
     confirmed via the real `lightbank.paramdef` field metadata (`min=0`, `max=1000`,
     `default=100`) to be a percent-style intensity scale (`/100`), not a raw multiplier or an
     alpha channel - fixed after an initial raw-value guess blew every `colA=100` row (the
     common case) out to solid white. **Still open:** real Nexus `env_intensity`/
     `env_spc_intensity` values range 1.5x-5x, and the lightmap-scaled env/env_spc terms are
     left unclamped, which overexposes a minority of bright-lightmap surfaces - confirmed via
     `m0000B0` (the Old One/ending-area ground piece, `LightID=3`, `envDif_colA=450` -> `4.5x`
     - geometry gated behind beating the game, not present in the regular explorable Nexus hub,
     so not recapturable via casual RPCS3 play sessions; see docs/context.md's RPCS3 shader
     capture entries): its bright open-ground lightmap texels blow past white while shadowed
     ruins/crevice texels stay correct, since the env term scales with the lightmap sample
     itself (see the `HemEnvDifSpc` DSR-source entry above/docs/context.md's "Lightmap/drawparam
     system, part 4"). A `[0,1]` clamp (`hemisphere_env_term()`) was tried and reverted the
     same day (2026-08-17) - user-confirmed it visibly dulled most *other* map pieces
     (m02/Boletaria included) that weren't overexposed to begin with, and the overexposure on
     the few affected pieces is a more useful marker of what still needs the real fix than a
     global clamp masking it. The real DSR source's `CalcEnvIBL(...)` is a function call, not a
     bare multiply, and almost certainly shapes/normalizes this properly. Revisit once the rest
     of MSB parsing (or at least the lighting-data half) is otherwise done - this is real
     shader-research, not more data-wiring. **`ToneMapBank`/`ToneCorrectBank` (2026-08-18) turned
     out to be a real, adjacent per-map dataset** (`MSBD.Part.ToneMapID`/`ToneCorrectID`, same
     shape as `LightID`/`FogID`) - reading is wired and kept, but a `WorldEnvironment` built from
     its real values made things visibly worse (most maps dark, some areas pure black), not
     better - see docs/ARCHITECTURE.md's "Known deferred work" and docs/context.md's
     "third-party investigation brief" entry. Confirms the env-term formula gap above is a real
     understanding gap, not just missing calibration data - more real data alone didn't fix it.
     **Resolved 2026-08-27** (see docs/context.md's dig entry and docs/ARCHITECTURE.md's
     Known-deferred-work): the regression was a compound (mild whole-frame `Adjustment`
     darkening plus the `StandardMaterial3D` population's ambient collapsing to near-black with
     no light nodes in-scene), and the fix order is now clear - give that population a real
     `LIGHT_BANK`-driven directional-light shader (`g_LightingType=1`/`HemDirDifSpcx3`) before
     ever adding a `WorldEnvironment` again. The ranked next steps below reflect this.
   - **Ranked next steps for this lighting thread, established 2026-08-27** (each independently
     testable, ordered so nothing depends on a later item - see docs/context.md's dig entry for
     the full evidence): ~~(1) resolve `LIGHT_BANK`'s `envDif`/`envSpc_0..3` cubemap IDs to real
     `TPF` names, alongside `g_EnvSpcSlotNo`~~ - **done 2026-08-27** (data-only, no consumer);
     **(2) ~~the hemisphere-diffuse terms (`colA_du`/`colA_dd`)~~ - wired and reverted three times
     across parts 15 and 32** (flat, flat-with-tonemap, lightmap-gated); the last fixed the
     contrast but left the scene a uniform beige. The transfer function was a necessary condition,
     not the missing piece. **Not the lead any more** - do not re-add without genuinely new
     information. See docs/context.md's parts 15/32/33;
     ~~(3) per-pixel normals in the lightmap/blend shading math~~ - **done 2026-08-27**,
     landed together with the env cubemaps as the scoping check required (neither delivers
     alone); ~~(4) an **in-shader** DeS transfer function~~ - **done 2026-08-27**
     (`output_stage.gdshaderinc`), and it turned out to be engine-accurate rather than a Godot
     workaround: DeS ends every material shader with its own exposure step, so in-shader is
     where it belongs anyway. Landed together with per-material fog and per-channel tone
     correction as one "output stage" unit, since all three are per-material in the real engine
     and splitting them would have meant testing a half-built transfer function. (This was
     expected to unblock (2); it did not - see (2) above.) ~~(5) route
     `g_LightingType=1` materials onto a real `HemDir3` shader instead of
     `StandardMaterial3D`+Godot PBR, carrying `LIGHT_BANK`'s three directional lights~~ -
     **done 2026-08-28**: both lighting models now share one shader family via a
     `lighting_model` uniform (75 m01 surfaces routed, all binding real directional lights), so
     the population that rendered black without a `WorldEnvironment` no longer needs one.
     ~~Still open here: `g_LightingType=3` materials with no lightmap~~ - **also done
     2026-08-28**, and that was the population actually responsible for the black props (60 of
     m01's meshes, against only 4 that are `g_LightingType=1`). The unattenuated-`env_intensity`
     worry was wrong on arithmetic *and* on accuracy - see docs/context.md's "part 25". **The directional trio belongs here and only here**: bytecode shows the map/lightmap
     (HemEnv) family uses zero directional lights, so adding them to the lightmap shader would be
     inventing light the engine never applied. Note the family split is approximate - only 83% of
     lightmap/blend materials are HemEnv - so this needs a real per-material `g_LightingType`
     gate rather than keying off which builder a material routed to; (6) only then, a block-space post-process
     pass (tonemap + tone-correct as separately-toggleable stages, since a `CompositorEffect`
     needs Forward+/Mobile and this project's real-driver check runs Compatibility/`opengl3` - a
     `hint_screen_texture` shader on a `CanvasLayer` is the viable surface) - note this is now
     only worth it for the genuinely screen-space stages (bloom, DOF, motion blur), since
     tonemap and tone-correct already run per-material where the engine runs them;
     ~~(7) per-material fog fed by `FogID`~~ - **done 2026-08-27**. The atmosphere is
     **user-accepted as close ("about the closest we can get it for now", 2026-08-28) but not
     claimed engine-accurate solved** - see docs/context.md's parts 20-33. `FOG_BANK`'s lerp
     turned out to be the wrong dominant term (real in only 7/183 captured fragment programs) and
     was dropped from map materials; `LIGHT_SCATTERING_BANK`'s in-scatter is normalized by the
     **scalar** `1/(lsBetaRay + lsBetaMie)` (the "no divide" ruling of parts 29-30 was reopened -
     the bytecode has no DIV but applies the phase functions raw, so the divide is CPU-folded into
     `c108`/`c109`; scalar not per-channel, so hue is untouched); the wavelength weights are
     lambda^-1.5, fit to the reference ceiling; `scatter_distance_scale` is `3.5e-3`, set so the
     haze approaches its ceiling by the back of the visible room. Residual gap (map-piece lighting
     colour) is deferred until this item's screen-space stages exist. Not recommended:
     re-attempting `Environment.Adjustment*` or a `grayKeyValue → TonemapExposure` mapping in any
     form, or adding a `WorldEnvironment` before item (4).
   - **The shader library (`shader/ds_flver.shaderbnd`) is a new, largely untapped information
     source, opened 2026-08-28** - 1349 named shaders giving the complete lighting-model matrix
     (`HemDir3`/`HemEnv`/`HemEnvLerp` x point-light counts x shadow variants), a direct
     authored MTD->shader mapping, and dev-written Japanese descriptions on all 612 MTDs. See
     docs/ARCHITECTURE.md's "The shader library" section. ~~(a) add `shader` to
     `AssetExtractor.KnownCategories`~~ and ~~(b) read the names as data~~ - **both done
     2026-08-28** (`mounted/shader/`, `ShaderLibrary.cs`, 612/612 MTDs resolving). **Still open:
     (c) disassemble the microcode.** The `.fpo`/`.vpo` are stripped of symbols but the RSX ISA
     is implemented in RPCS3's own decompiler, so matching compiled shaders against the
     shaderlog captures would resolve every anonymous `FragmentProgramNNN` to its real name -
     turning the capture corpus into a named reference, and giving access to the 1215 shipped
     fragment programs against the 228 ever captured. Parsing, not reverse engineering.
   - **Two user-reported issues tracked as of 2026-08-28, both no longer active.** **(a)
     ~~shadowed geometry reads too dark to make out detail~~ - resolved as a byproduct of the
     atmosphere work: additive in-scatter now genuinely lifts shadowed surfaces, which is also
     what makes the part-19 pivot contrast form safe to keep (see docs/context.md's part 33). Not
     separately confirmed by the user as its own item but no longer reported.** **(b) ~~placed
     props/`Objects` render black without a `WorldEnvironment`~~ - fixed 2026-08-28, see part 25.**
   - **Deferred (was "current top priority", 2026-08-28): the map's lighting/shading colour is
     still slightly off vs RPCS3's warmer map-piece tone.** Not a contrast problem - the
     diffuse-hemi contrast crush is gone with that term, and the pivot black point is covered by
     the accurate haze at player range (part 33). The `LIGHT_BANK` "diffuse hemisphere" pair
     (`colA_du`/`colA_dd`) has been wired and
     reverted three times (parts 15/32) and is **not** the lead - it carries `m01`'s warm tint but
     every attempt flattened the scene. Per the user (part 33), the map-piece lighting colour may
     now be *more* accurate than the fog, and the fog is the easier of the two to revisit; both
     are parked until the screen-space stages of this item exist and there is a full pipeline to
     judge against. See docs/context.md's parts 32/33 and `.local-notes.md`.
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
     entry instead - same underlying prerequisite as the points above. See docs/context.md for
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
circumvention — see docs/context.md's "Non-visual game-logic data investigation" entry for
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
not a nice-to-have — see `docs/ARCHITECTURE.md`'s "Standing priority: stability and resource
safety" section for the concrete evidence and what it means for how importer/extractor
work gets done. The mission above only works if a regular user can eventually run this
machinery themselves, unsupervised, against their own full game copy — it has to be
built to hold up to that, not just to this dev machine.
