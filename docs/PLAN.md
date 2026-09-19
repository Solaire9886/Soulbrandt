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

**Reprioritized 2026-09-06** (`docs/context.md` part 56, `docs/ARCHITECTURE.md`'s skeleton/animation entry): the list below is kept in its original order for reference, but item 2 (Havok collision parsing specifically, not necessarily full skeleton/animation) is now the actual next thing to pick up, ahead of finishing more of the graphics pipeline. Most of what's left open there (adapted-luminance eye adaptation, water's remaining accuracy, shadow movement jitter, DOF/lens flare/bloom) needs a real, gameplay-matched moving player camera to tune and verify against, not more capture/data work — so this project is shifting toward the game-logic stage (Havok collision + a real player/camera) next, since that's a genuine prerequisite for player movement regardless of sequencing and it unblocks that whole stuck class of graphics work as a side effect, rather than being a detour from it. One graphics item is still worth finishing first since it doesn't need a camera at all: the Nexus sun-ray/light-shaft quad family's edges read harder/sharper than RPCS3's soft falloff (scoped, not yet investigated).

**VFX interlude (2026-09-14/15) was a deliberate, bounded exception to this reprioritization, not a reversal of it.** Everything else left open in the graphics pipeline at the time needed the player camera this reprioritization is waiting on; VFX didn't — it was almost entirely untouched (no camera-region/object-effect/playback work existed yet) and was judged easy to slot in without blocking on Havok. That work (object/camera-region VFX preview, playback corrections, the 2026-09-15 map-shading material fix) now has a solid foundation, though VFX itself isn't finished — the remaining VFX gaps (native activation, Param66, motion84 turbulence, and others listed under "Known deferred work" in `docs/ARCHITECTURE.md`) need the same kind of intense investigative work the earlier graphics items did, without similarly being blocked on a player camera. With that foundation in place, Havok collision parsing is genuinely back up next, not still deferred behind more graphics work.

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
   animation ecosystem research" entry for the full trail. **No longer deferred behind
   "the graphical side being largely done" (2026-09-06) — Havok collision parsing
   specifically is the near-term priority**, since it's a genuine prerequisite for player
   movement/physics on its own terms and also closes the point-light `Collision`-anchor
   gap already found (see docs/ARCHITECTURE.md's `POINT_LIGHT_BANK` entry). Full
   skeleton/animation *playback* may still lag behind collision parsing itself — posing
   needs its own runtime built on top of the parsed data, the same "no game logic to plug
   into yet" reasoning still applying to `Enemy`/`Player` MSB placement below — but
   parsing the format is no longer being held back pending more graphics work. See the
   reprioritization note above and docs/context.md's part 56 for why.
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
   into any phase item here yet pending a priority decision. `menu`/`remo`/`msg`
   are the same story at smaller scale. **`sfx` parsing reached full corpus coverage
   (3,091/3,091 files) on 2026-09-06; map placement/performance were corrected 2026-09-14.**
   The old Part-anchor assumption was wrong: SFX's `UnkT00` indexes authored MSB regions.
   All 278 Nexus events now resolve; the old mesh-centre fallback stacked 180 separate
   candles (720 particle systems) at one point. Off-tree particle ownership and redundant
   constant-emitter updates are fixed; single-axis rotations restore two missing dry-ice
   variants. A disabled-by-default `MapSfxPreview` inspector control now offers bounded
   nearby previews (64 systems per map, two builds per refresh) with omission diagnostics.
   The follow-up VFX playback audit and implementation (external research archive, `VFX_PLAYBACK.md`, not tracked in this repo) corrects exclusive
   template2117 distance selection and template2023 constant emission schedules. CPU-scheduled
   MultiMesh billboards work in Compatibility, separating authored capacity from batch/interval.
   Map previews budget both systems and particles, account for coverage, and warm ambient
   instances on activation. Circle/square use provisional horizontal disk/plane distributions.
   **91001 remains unsupported:** Param66's count conversion is unresolved; its earlier
   population estimate was upstream of that conversion. No fixed-density fallback is used.
   **Still needed for native playback:** broader activation/StateMap execution, finite/dynamic
   schedules, Param66, container movement/parent-follow behavior, native distribution axes,
   turbulence, geometry/screen actions, depth-softening/output shading and multi-axis placement
   composition. See docs/ARCHITECTURE.md's VFX entry and docs/context.md's
   2026-09-14 investigation for evidence and limits. This is graphics/VFX
   work, not game logic, so it sits alongside the rendering pipeline generally rather
   than this game-logic-data cluster; grouped here only because it was scouted at the
   same time as `menu`/`remo`/`msg`. Separately, that same search once concluded real
   per-material shader calibration constants were **not** recoverable from any game file
   (symbol-stripped `.fpo` binaries) — **for water this was overturned 2026-09-05 (context.md
   part 50)**: the water `.mtd` (`DS_Water_Env.spx`) carries the full param set
   (`g_TileScale_0..2`, `g_TileBlend_0..2`, `g_WaterColor`, `g_Fresnel*`, `g_RefractBand`,
   `g_BumpMapSmoose`, `g_WaterFadeBegin`), and RPCS3 frame captures confirmed each param →
   shader-constant mapping. chr/parts specular cubemap values may be similarly recoverable via
   the same `.rrc` route and haven't been re-checked. One
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
     map, and (see below) the only source of per-instance lighting data too.
     **The `MSB1`-can't-read-DeS blocker described here previously is stale — DeS uses a
     wholly separate `MSBD` reader, not `MSB1`, and `MsbLoader.cs` (2026-07-24, see
     docs/ARCHITECTURE.md's "MSB map placement") already reads real `.msb` files end to end
     via `MSBD.Read()` with no patch needed.** `MSB1`'s DS1-specific `Treasure` bug is
     simply irrelevant to this game. Re-confirmed 2026-07-26: `MSBD.Read()` (which reads
     `Events` too, not just `Parts`) completes cleanly on every `.msb` spot-checked
     (`m01_00_00_00`, `m03_00_00_00`, `m03_01_00_00`, `m08_00_00_00`) with no assert
     failures anywhere in the file, not just up to the Parts section.
   - **`MSBD.Part.DrawGroups[4]` / `DispGroups[4]` - semantics for DeS still unknown; a
     first `MapAreaCuller` cut was built and reverted 2026-09-03 (`docs/context.md` parts 39-40).**
     Applying the DS1 rule (part visible when its `DrawGroups` intersect the active `Collision`'s
     `DispGroups`) deleted ~3/4 of `m02`. A `.msb` dump found **every MapPiece/Object has
     all-zero `DispGroups`** and `Collision.DispGroups` are sparse single bits - so the DS1
     convention gives 0 visible and the reverse gives a tiny slice. **Leading theory: DeS's
     older engine doesn't gate map pieces by draw group at all** (blocks stream whole), the
     system being for enemies/objects/lights only. Needs a frame-capture cross-check - a
     Boletaria `.rrc` via `rrc-draws`, which parts DeS actually draws vs. the full MSB part list
     - before anything culls visible geometry on it. `Part` also exposes `ShadowID`,
     `LodParamID`, `IsShadowSrc/Dest/Only` (all-zero in DeS data - likely mis-mapped field names
     upstream), and `UseDepthBiasFloat`, all unread.
   - **`MapPieces` (2026-07-24) and `Objects` (2026-08-17) are placed and lit; `Enemy`/
     `Player` are deliberately held off, not just unscoped.** See docs/ARCHITECTURE.md's
     "MSB map placement"/"`Objects` placement". User decision (2026-08-17): without any
     game-logic systems (AI, combat, animation state) built yet, placed enemy/player
     markers would be inert clutter rather than useful progress — keep MSB work on the
     graphical/model side (`Collision`/`Navmesh` are still reasonable next slices) until
     that changes.
   - **Per-part lighting (`LightID`) resolution belongs here, not as a
     separate system** — confirmed real and readable this session, not just a lead
     anymore: DeS's own `MSBD.PartsParam.LightID` byte field (`SoulsFormatsNEXT`
     already parses it) indexes into `LIGHT_BANK` rows in
     `param/drawparam/<map>_lightbank.param` - loose, `PARAM`/`PARAMDEF`-readable files
     (no BND unpacking needed), confirmed to hold exactly the two-color hemisphere-ambient/
     directional-light data the shader work below already assumes. (The sibling `FogID` →
     `FOG_BANK` is wired 2026-09-04 as `des_fog`, a colour distance fade — not RSX fog, which is
     dead; see context.md parts 36 and 45.) See docs/context.md's "Lightmap/drawparam system" parts 5-8 for
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
     row-0 fallback every lightmapped material currently uses. **`LightID` does meaningfully
     vary per-part within a single map, not one dominant value** — confirmed 2026-07-26 via a throwaway `MsbLoader` dump across
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
     separate task.** It matters less than it did now that the material families are all
     `unshaded` and run DeS's own output stage (exposure/tonemap/tone-correct in-shader, parts
     43-48) rather than leaning on Godot's lighting - but there is still no ambient/exposure
     `WorldEnvironment` anywhere (the glow-only bloom one added 2026-08-31 disables ambient and
     pins linear tonemap), so anything relying on Godot's ambient/reflection response is still
     provisional. The assembler is the natural place to build one: it's the
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
     (7) per-material fog fed by `FogID` - **done 2026-09-04 (part 45)**. Not RSX fog (that path is
     dead, part 36) - `FOG_BANK`'s *colour* is the target of a hand-rolled `mix()` distance fade in
     the HemEnv epilogue, `mix(colour, col·colA/100, saturate(ramp·degRotW/100))` before scattering,
     verified exact on m01/m02/m03/m06. New `des_fog` + `GetFogBankRow` + `ApplyFogBank`. Not yet
     user-tested. The rest of the outdoor atmosphere is `LIGHT_SCATTERING_BANK`'s in-scatter,
     **user-accepted as close but not claimed engine-accurate solved**. In-scatter is normalized
     **per-channel by `1/β`** (captured `c106 == 1/c104` on m01/m02/m03, part 46) - β is blue-heavy
     so the normalised in-scatter is near-neutral, matching RPCS3's grey haze; the old scalar
     `1/(lsBetaRay+lsBetaMie)` left it Rayleigh-blue. The per-channel wavelength weights are
     **measured, not fitted** - `c109` (Rayleigh) R:G:B = 1:1.30:1.87 and `c108` (Mie) = 1:1.69:3.50,
     stable across four outdoor areas; now `RAYLEIGH/MIE_WAVELENGTH_WEIGHTS` (in-scatter numerator only).
     Extinction is separate + bluer (`EXTINCTION_WAVELENGTH_WEIGHTS`, from captured `c104`, part 47) -
     absorption on top of scattering; drives `fex` + the `1/beta` divide so distant geometry sheds
     blue faster. See docs/context.md parts 35, 46, 47. `scatter_distance_scale` and the
     in-scatter magnitude are the one remaining co-fitted "to the reference ceiling" knob and need
     a real frame to re-fit. **Now actionable, not deferred (2026-09-04, part 43):** the m02
     washout was traced to a missing `× 0.6` vertex-colour factor + `des_tonemap` being a Reinhard
     curve when DeS's whole tone pipeline is linear (`saturate(color · E)`, both passes
     disassembled - no LUT). Both fixed. `scatter_distance_scale = 0.0035` was eye-fitted against
     that old Reinhard, so with `des_tonemap` now linear it needs re-deriving against m01 *and* m02
     together. `des_tone_correct` was checked (2026-09-04) and is **already engine-accurate** - the
     `DS_Fil_HDR_ColAdj` colour matrix is exactly `S(sat)·diag(contrast)·diag(brightness)` + pivot
     offset, which our `B → C-pivot → S → H` composition reproduces to measurement precision; the
     binding reaches the geometry (verified headless). Not recommended:
     re-attempting `Environment.Adjustment*` or a `grayKeyValue → TonemapExposure` mapping in any
     form, or adding an *ambient/reflection* `WorldEnvironment` before item (4). (A glow-only one
     for bloom was added 2026-08-31 with ambient/reflection disabled — see docs/ARCHITECTURE.md's
     "Bloom"; that is the bounded exception, not a reopening of this.)
   - **The shader library (`shader/ds_flver.shaderbnd`) is a new, largely untapped information
     source, opened 2026-08-28** - 1349 named shaders giving the complete lighting-model matrix
     (`HemDir3`/`HemEnv`/`HemEnvLerp` x point-light counts x shadow variants), a direct
     authored MTD->shader mapping, and dev-written Japanese descriptions on all 612 MTDs. See
     docs/ARCHITECTURE.md's "The shader library" section. ~~(a) add `shader` to
     `AssetExtractor.KnownCategories`~~, ~~(b) read the names as data~~ and ~~(c) disassemble
     the microcode~~ - **all done** (a/b 2026-08-28; c 2026-08-31 via `tools/RsxShaderMatch`:
     `.fpo` RSX-microcode decoder + fingerprint matcher + readable disassembler, 363 HIGH-
     confidence names across 423 captures, 195 distinct shaders identified). The disassembler
     drove the D5/D6/D7 shader-accuracy fixes and re-identified `HemEnvLerp` as a two-cubemap
     env-diffuse transition. See `docs/context.md` part 34. ~~(d) vertex-program matching
     (separate RSX ISA)~~ - **done 2026-08-31** (`--vertex`, part 35): own decoder for the
     co-issued vector+scalar ISA, matched on the input-attribute / constant-index / output-register
     sets; HIGH on 143 of 212 vertex captures, 28 distinct canonical shaders named. Confirmed
     `DS_Phn_*` and `DS_Gst_*` vertex programs are byte-identical (ghost effect is fragment-only).
     ~~(e) the `.rrc` RSX Capture route for the scattering constants~~ - **done 2026-08-31**
     (`RrcCapture.cs`/`RrcInterp.cs`, `rrc`/`rrc-draws`): reads a whole-frame `.rrc.gz`, replays
     the FIFO, names every draw's FP/VP against the library and dumps its real vertex constants.
     FP 99.6% HIGH-named on the Nexus frame. Drove the measured wavelength-weight fix above.
     **`fetch_fog_value` / RSX hardware fog: closed as a non-gap 2026-09-03** (`rrc-fog`, part 36) -
     eight frame captures prove DeS never issues `SET_FOG_PARAMS` and no shader reads `f[FOGC]`, so
     the *RSX-fog* `des_fog()` + its `fog_*` uniforms were **removed 2026-09-03** (correct - RSX fog is
     dead). **But DeS's own hand-rolled per-material distance fade toward the FOG_BANK *colour* was
     found and implemented 2026-09-04 (part 45)** - it's the `mix()` two instructions before
     `result·tc8 + tc9` in the standard HemEnv epilogue (not ~7 niche shaders), colour `= col·colA/100`
     and weight scale `= degRotW/100` verified exact on m01/m02/m03/m06. New `des_fog` + `GetFogBankRow`
     + `ApplyFogBank`. **Still open:** a magnitude re-fit of `scatter_distance_scale` / in-scatter
     strength against a real frame, folded into the screen-space pipeline work.
     ~~(f) systematic constant + render-state mining across all eight captures~~ - **done
     2026-09-03** (`rrc-mine`, part 37): `RrcInterp` now tracks the surface/viewport/mask
     state; every constant register is attributed to the VP microcode that references it,
     per-frame vs per-draw. The `.rrc` interpreter is now considered finished.
   - **Shadow / depth pipeline - `SHADOW_BANK`-driven. v1 built 2026-09-03 (`docs/context.md`
     parts 37-41); v2 = ONE STATIC ortho depth pass over the casters' bounds, 2048² atlas +
     Poisson PCF + real SHADOW_BANK numbers, 2026-09-04 (part 49), user-confirmed "about right".
     The cast is fixed (sun + geometry), so the projection never follows the camera; only the
     shader distance fade is camera-dynamic. Two camera-following cuts (mild-perspective, then
     texel-snapped ortho) were tried and reverted - both made shadows visibly slide as the view
     moved. **The true 4-split (below) waits for a player camera**: CSM cascades track the view
     frustum, so in the editor free-fly cam they'd sweep the same way. opengl3 compile check clean.
     Analysis: `rrc-shadow` part 37, `DS_*_Sdw`/`Csd` disasm + `ShadowBank.paramdef` part 49.**
     DeS uses **4-split *perspective* shadow maps** (the `ShadowBank` "PSM" fields
     `calibulateFar`/`persedDepthOffset`/`radFactor`; the captured cast matrices have a real
     W-row and the light basis re-warps around the camera each frame). 4 depth passes from
     the sun into a **2048x2048 Z24S8 atlas as a 2x2 grid of 1024x1024 tiles** (`c[467].zy` =
     `(2.0, 0.5)` = surface/tile ratio and clip->UV half-scale; `c[466].x` = `1/1024` texel).
     No separate depth pre-pass. Cast matrix per split = `c[0..3]` (shared light view, per-split
     frustum). Receive: **map pieces take one cascade matrix per draw in `c[112..115]`**
     (clip->atlas-UV, per-draw where a piece spans splits - confirmed in the Boletaria capture);
     **characters carry all 4 as column-major fragment inline constants and pick per pixel**
     (one-hot select by `near_i < viewDepth <= far_i`, from the `Csd` disasm). Filter = **one
     hardware `TXPR`** (2x2 bilinear). Fade = `saturate((fadeEnd - radialDist)/fadeDist)`,
     `fadeEnd = fadeBeginDist + fadeDist` - a **separate** distance pair from `beginDist`/`endDist`
     (the frustum range). Composite = `1 - (density - tint)*fade*inShadow` per channel (matches
     what we already had). All from part 49's disasm.
     **Decision (2026-09-03): implement custom, not via Godot light nodes** - Godot has no
     shadow-only light, the shadow term is only reachable in `light()` (runs after `fragment()`,
     where DeS composes `min(shadow, lightmap)`), and Godot PSSM != DeS's atlas.

     **Spike done 2026-09-03 - the mechanism works in 4.7 Compatibility.** A `SubViewport`
     (`UPDATE_ALWAYS`, `transparent_bg`) with an orthographic `Camera3D` renders casters through
     a `render_mode unshaded, depth_draw_always` material that writes `ALBEDO = vec3(-VERTEX.z /
     light_far)` (linear light-space distance, 0..1). `SubViewport.get_texture()` returns a
     usable `ViewportTexture` (RGBA8). A hand-built light matrix - `light_view =
     Transform3D(basis_from_sun_dir, -sun_dir*d).affine_inverse()`, `light_proj =
     Projection.create_orthogonal(-h, h, -h, h, near, far)` - round-trips: receiver computes
     `p_ls = light_view * world`, `uv = (light_proj * p_ls).xy * 0.5 + 0.5` (ortho, no w divide),
     `recv_d = -p_ls.z / light_far`, and `recv_d <= tex(shadow_map, uv).r + bias` classified
     5/5 test points correctly (tall box shadows the ground under it, open ground lit, box tops
     lit, sun-side surfaces lit). No CompositorEffect, no light nodes, no engine shadow system.

     **What's built (2026-09-03; `docs/context.md` parts 38-41 have the blow-by-blow):**
     - **`ShadowRenderer.cs`** - a `[Tool]` `Node` built by `FlverLoader.InstantiateMap`, owning
       one `SubViewport` (2048² - DeS's full shadow surface, used as one map; `transparent_bg`,
       `own_world_3d`, `UPDATE_ALWAYS`) + ortho `Camera3D` + a `MeshInstance3D` clone per lit
       caster (shared `Mesh` resource, one `shadow_depth.gdshader` `MaterialOverride`;
       sky/ghost/VFX + water excluded via `FlverLoader.CastsSunShadow`). **v2 (part 49) = ONE
       STATIC ortho depth pass.** The cast is fixed by the sun + the geometry, so the projection
       is computed once (merged caster world-AABB; radius `min(½·diag + 2, 200)` cap;
       `pullback = radius + volumeDepth + 5`; `far = 2·radius + volumeDepth + 10`), pushed once,
       and `ShadowRenderer` has **no `_Process`**. The only camera-dynamic part is the shader
       `fade`. User A/B: "about right". *Rejected first:* a
       mild-perspective follower (shadows swam), then a texel-snapped ortho follower (still slid -
       a moving cast is a moving cast). **v1** was an ortho box `clamp(endDist*0.75, 8, 120)`
       re-rendered past a `endDist*0.4` drift threshold (the snap the user saw).
     - **`SHADOW_BANK` row 0** (`DrawParamReader.GetShadowBankRow`, map baseline; per-part
       `ShadowID` rows exist but one region uses one): light **direction** from
       `lightDegRotX/Y` (its own, not the scattering sun), **fade** from `fadeBeginDist`/`fadeDist`
       (the only camera-dynamic part), far-range extrusion from `shadowVolumeDepth`, darkness from
       `densityRatio`/100 (partial - never fully black), tint from `colR/G/B`/255 (≈0), bias from
       `depthOffset` (~-0.01, small world-constant tiebreaker). `beginDist`/`endDist` (0→10 m01,
       0→40 m02 - the cascade split ranges) are **not** used by the static region; they return with
       the 4-split. A default-shaped row (`endDist ≥ 200`) → no shadow.
     - **`shadow_depth.gdshader`**: `unshaded, cull_front` (back faces only → lit surface sits
       ahead of its occluder, kills acne, mild contact light-leak), linear light-space distance
       **packed 16-bit across R,G** (unaffected by the projection type).
     - **`sun_shadow()`** (`hemisphere_ambient.gdshaderinc`) returns a **per-channel** term
       `1 - (density - tint)·fade·inShadow`; two mat4s (`shadow_light_clip` for the atlas UV -
       `.xy/.w` divide, `.w` is 1 under ortho but the code stays general for the 4-split
       perspective phase; `shadow_light_view` to rebuild the linear depth);
       **v2 rotated 12-tap Poisson-disk PCF** (per-fragment rotation hashed from `world_pos`)
       standing in for DeS's one hardware 2×2 `TXPR`; `fade` (`fadeBeginDist`/`fadeDist` vs
       `view_distance`) is the sole camera-dynamic term; out-of-atlas / faded /
       empty-texel → `vec3(1.0)`. Gate `min(shadowTerm, lightmap)` on the HemEnv env term,
       `×shadowTerm` on the HemDir3 directional term, hemisphere floor ungated. `view_distance`
       threaded through `lightmap_shade` (4 variants) + `terrain_blend`.
     - **Files**: `ShadowRenderer.cs`, `shadow_depth.gdshader`, `DrawParamReader.GetShadowBankRow`,
       `sun_shadow()` + uniforms in `hemisphere_ambient.gdshaderinc`, gate wiring in
       `lightmap_common.gdshaderinc` / `lightmap*.gdshader` / `terrain_blend.gdshader`,
       `AttachShadowRenderer` + `HasUniform` visibility in `FlverLoader.cs`. `archstone.gd`
       unchanged.
     - **Engine-accurate (v2)**: direction, `densityRatio`, tint, the distance fade,
       `shadowVolumeDepth`, `1 - (density - tint)·fade·inShadow` composite
       (all `ShadowBank.paramdef` + the `DS_*_Sdw`/`Csd` disasm, part 49). **Reductions**: one
       **static** ortho region, not the 4-split camera-relative **perspective** (PSM) atlas +
       cascade select. **The 4-split waits for a player camera** - DeS's cascades track the view
       frustum (look direction included), so in the editor free-fly cam they'd sweep like the two
       reverted follower cuts; a gameplay chase cam is what makes CSM read as stable.
       `beginDist`/`endDist` + `calibulateFar`/`persedDepthOffset`/`radFactor` (the PSM warp)
       unmodelled; whole-region 2048² ortho box → coarse texels on a large map (200 cap trades
       reach for texel size); Poisson-over-8-bit stands in for one `TXPR`; `cull_front` trades
       acne for light-leak; no alpha-scissor casters; `lightDegRotX/Y` → vector reading
       unverified; not wired into the "Load Folder" path (one SubViewport per model - needs a
       batched entry point). **User A/B (part 49): "about right now."**
     - **History**: whole-map capped-box first cut + its region-sizing/depth fixes (part 38); a
       `MapAreaCuller` draw-group cull tried and reverted (part 40, the DS1 rule deletes most of
       a DeS block - map pieces may not use draw groups at all); replaced by the above (part 41).
     - **Part 42/43 note:** the m02 (Boletaria) washout was *initially* attributed to missing
       cascaded shadows (wrong - m02 is 85% lightmapped), then to the env-cubemap decode path
       (also wrong - decode is byte-correct). **Part 43 read the actual cause out of the HemEnv
       fragment programs + the disassembled `ds_filter` post chain:** (1) HemEnv multiplies vertex
       colour by a fixed `0.6` we omitted; (2) DeS's whole tone pipeline is **linear** - geometry
       epilogue `saturate(color · E / 2)`, post `DS_Fil_HDR_ColAdj` `saturate(scene · 2) + bloom`
       then a colour matrix; the `/2`·`2` cancel so the net is `saturate(color · E)`. No Reinhard,
       no LUT anywhere - the old `des_tonemap` `x/(1+x)` was a wrong assumption. **Both fixed
       2026-09-04** (`lightmap_common.gdshaderinc` + `terrain_blend.gdshader` for (1);
       `output_stage.gdshaderinc` `des_tonemap` → `clamp(color · E, 0, 1)` for (2), after a first
       attempt with a stray `· 0.5` came out 2× too dark). Parts 44–48 then closed the rest:
       colour space verified (no output encode, `: source_color` a no-op - part-42 theory dead),
       `des_tone_correct` already the exact `DS_Fil_HDR_ColAdj` matrix, `des_fog` re-added for the
       real `FOG_BANK` colour term, sky-dome routing, per-channel `1/β` in-scatter + extinction
       weights, and `tone_adapted_lum = clamp(0.10, min, max)`. **User-confirmed "extremely
       accurate now".** Remaining: `scatter_distance_scale` / planar-depth re-fit, bloom -
       deferred behind the rest of the pipeline. Shadows resumed 2026-09-04 with the part 49 dig +
       v2; the 4-split *perspective* atlas is still the shadow endgame, on the v2 foundation.
   - **Two user-reported issues tracked as of 2026-08-28, both no longer active.** **(a)
     ~~shadowed geometry reads too dark to make out detail~~ - resolved as a byproduct of the
     atmosphere work: additive in-scatter now genuinely lifts shadowed surfaces, which is also
     what makes the part-19 pivot contrast form safe to keep (condensed atmosphere entry). Not
     separately confirmed by the user as its own item but no longer reported.** **(b) ~~placed
     props/`Objects` render black without a `WorldEnvironment`~~ - fixed 2026-08-28, see part 25.**
   - **The map-piece lighting-colour gap (was "top priority" 2026-08-28) — largely closed by
     parts 43–48 (2026-09-04), user-confirmed "extremely accurate now".** The cause was seven
     concrete output-stage bugs (missing `× 0.6`, Reinhard `des_tonemap`, missing `FOG_BANK` fade,
     sky-dome bypass, scalar in-scatter normalisation, extinction not blue enough, wrong
     adapted-luminance stand-in) — each read from the HemEnv fragment programs and the captured VP
     constants, not fitted. See docs/context.md parts 43–48. The `LIGHT_BANK` "diffuse hemisphere"
     pair (`colA_du`/`colA_dd`) was **not** the lead — wired and reverted three times. Residual
     minor inconsistencies (scatter magnitude, dynamic exposure, radial-vs-planar distance) are
     deferred behind shadows / bloom / the rest of the pipeline.
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
