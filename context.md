# context.md

Session history, lessons learned, and ruled-out dead ends for this project. `CLAUDE.md`
covers current architecture and how to work in the repo; this file covers *why* things
are the way they are and what's already been tried and rejected, so it doesn't get
re-litigated. Read both — CLAUDE.md for the current state of truth, this for the journey.

## The arc so far

1. Proof of concept: WitchyBND to unpack game archives + a C# console app
   (`desflver_test/`) using the vendored SoulsFormatsNEXT library, writing
   `.obj`/`.mtl`/`.png` for Godot's *generic* Wavefront OBJ importer to pick up.
2. That relay fought back constantly: Windows-path leakage into `.mtl`, headerless/raw
   DDS bytes needing `Headerizer.Headerize()`, this editor build having no `dds` module
   compiled in (confirmed on both Redot and godot-mono — don't assume official/Steam
   builds have it by default, they don't here), and a real Godot bug (#81555-class) where
   OBJ-imported-as-Mesh races the referenced PNG import on first import and permanently
   caches a textureless material.
3. Replaced ImageSharp (v4+ requires a paid license) with a small stdlib-only PNG encoder
   (`desflver_test/PngWriter.cs`, `ZLibStream` + hand-rolled CRC32). Only matters to the
   `desflver_test` reference tool now — the native importer never touches disk for
   textures at all.
4. Decided a native `EditorSceneFormatImporter` was worth building instead of continuing
   to patch the OBJ relay — same extension point Godot's own glTF/Collada importers use,
   and unlike the simpler `EditorImportPlugin`, it has a natural home for skeleton/
   animation import later.
5. Migrated the project from Redot to `godot-mono` 4.7 for C# support (Redot's installed
   build had none at all). Confirmed Steam's Godot has no C# either — the official
   godotengine.org **.NET** build (same upstream release Arch's `godot-mono` package
   wraps) is the only route.
6. Built `addons/archstone/FlverSceneImporter.cs` and iterated through the real bugs
   listed below until static mesh + material import was solid for both character and
   map models.
7. User reorganized the project: renamed `Boletaria` → `Soulbrandt` (plugin `demonpipe`/
   `dpipe.gd` → `archstone`/`archstone.gd`), and moved all Demon's Souls content out of
   the repo entirely into a sibling directory simulating a real extracted game copy,
   which is exactly the scenario the eventual mounting system needs to support. Set up
   a symlink (`mounted`) as a stopgap so the existing importer keeps working unchanged.
8. Built the real in-editor asset-mounting system, replacing that symlink stopgap and
   the manual WitchyBND unpack step entirely - `addons/archstone/AssetExtractor.cs`
   reads `.bnd`/`.dcx` containers directly via `SoulsFormatsNEXT`'s own `DCX`/`BND3`/
   `BND4`, no external tool. See the "Asset mounting system built" entry below for the
   investigation and the real bugs it surfaced in cross-category texture resolution.
9. Cleaned up remaining leftovers and put the project under real version control:
   deleted `WitchyBND-3.0.1.0-linux-x64/` (dead weight, fully superseded by item 8) and
   `desflver_test/` (the original proof-of-concept console app from item 1 - everything
   it covered now lives in the native importer). Finished the `Boletaria` → `Soulbrandt`
   rename that item 7 only did at the project-organization level: the csproj file itself
   and `project.godot`'s `[dotnet] project/assembly_name` were still `Boletaria` until
   now. `git init`'d the project for the first time, forked `SoulsFormatsNEXT` to
   `github.com/Solaire9886/SoulsFormatsNEXT` and committed its two local patches there
   for real (they'd been sitting as uncommitted working-tree edits in a plain clone this
   whole time - a live risk, not just a tidiness concern, since any `git pull`/`checkout`
   in that directory would have silently wiped them with no recovery path), then pulled
   it back into this project as a proper git submodule pinned to that patched commit.
   Pushed the whole thing to `github.com/Solaire9886/Soulbrandt` - GPLv3 (required by
   `SoulsFormatsNEXT`'s own GPLv3 license with no linking exception, not an arbitrary
   choice), with an RPCS3-style disclaimer in `README.md` and a `CONTRIBUTING.md` for
   future contributors - and made the repo public.

## Real bugs found and fixed (root causes, not symptoms)

Each of these produced a plausible-looking but wrong intermediate theory before the real
cause was confirmed — noted so the same wrong turn isn't re-taken.

- **Mip-chain slicing.** Pfim's decoded `Data` buffer contains the *entire* mip chain
  concatenated (base level first, ~4/3 the size of `width*height*4`), not just the base
  level. Must slice to `width*height*4` bytes before handing to
  `Image.CreateFromData`. Confirmed via `pfImage.MipMaps[]`, which exposes exact
  per-level offsets — trust that over guessing from total buffer length.
- **Empty imported scenes (two separate bugs, same symptom).** A returned
  `Node3D`+`MeshInstance3D`+`ArrayMesh` produced a scene that loaded with 0 children —
  no error, just silently empty. Root causes, in order discovered:
  1. Godot's scene-import post-processing (LOD/shadow mesh gen, compression) only
     operates on the *import-time* `ImporterMesh`/`ImporterMeshInstance3D` types, not
     the runtime `ArrayMesh`/`MeshInstance3D`. Switching types alone didn't fix it —
  2. `PackedScene.pack()` only serializes descendants whose `.Owner` is explicitly set to
     the packed root. A plain `AddChild()` is invisible to it. This is the "editable
     children" ownership mechanism Godot always uses for scene saving, not something
     specific to custom importers.
  - Diagnostic method that actually worked: `ResourceSaver.save(packed, "res://debug.tscn")`
    to dump the resource as human-readable text and directly see the node tree — much
    faster than guessing from binary `.scn` byte size.
- **Triangle winding.** Godot's `SurfaceTool` docs state it plainly: Godot uses
  **clockwise** winding for front faces — the opposite of OpenGL/Blender/Wavefront OBJ's
  CCW convention. `Program.cs`'s OBJ export applies an `a,c,b` index swap specifically to
  satisfy Blender's CCW expectation; porting that same swap into the native importer was
  wrong there and produced inside-out geometry. Confirmed (not just reasoned) by computing
  face normals from both winding orders against FLVER's own stored vertex normals across
  all 16k+ triangles of a test model — the *un-swapped* order matched Godot's required
  convention 99%+ of the time.
- **UV V-flip.** Same shape of mistake as winding: `Program.cs`'s `1 - v.UVs[0].Y` flip
  exists to satisfy Wavefront OBJ's V convention. The native path feeds the same
  top-down decoded pixel byte order directly into `Image.CreateFromData`, so no flip is
  needed. Confirmed by drawing a UV wireframe directly onto the raw decoded texture using
  the *unflipped* V and visually matching it against the texture's actual layout (a
  shield's ring/boss pattern) — pixel-exact match with no flip.
- **FLVER0 `uvFactor` heuristic (in vendored SoulsFormatsNEXT, not our code).**
  `Mesh.cs`'s own `// NB hack` comment picks a UV fixed-point scale of 1024 or 2048 based
  on `header version >= 0x12`. This is wrong for Demon's Souls — confirmed empirically
  (UV wireframe overlays showing textures sampling the *wrong* atlas region, aligning
  perfectly only when forced to a factor of 512/1024 depending on which branch the
  heuristic picked) across two independently-affected models with different header
  versions (0x14, 0x15). Demon's Souls always needs `1024` regardless of version; the
  version-based branch likely targets a different FromSoft game. Patched directly in the
  vendored copy (`Formats/FLVER/FLVER0/Mesh.cs`, both `Read` and `Write`).
- **Map texture resolution.** Character/object models ship a same-basename `.tpf`
  sibling; map piece FLVERs do not — their per-subarea `.tpf` (matching the containing
  folder's name) is a 16-byte empty stub. Real textures live in a *different* sibling
  folder shared by the whole map area (e.g. `mounted/map/m03/`, holding ~19 numbered
  `.tpf` files), derived from the segment of each texture's own reference path
  immediately before `tex`. Confirmed via the same UV-wireframe-overlay technique.
- **Race condition in the area-texture cache.** Symptom was maddeningly nondeterministic
  — "50/50, some have textures, some don't, some mixed" across repeated reimports of the
  *same* files. Root cause: Godot's bulk reimport dispatches `_ImportScene` across
  multiple worker threads against the *same* importer instance, and the per-area merged
  texture dictionary was a plain `Dictionary` — not thread-safe, silently drops entries
  under concurrent build. Fixed with `ConcurrentDictionary.GetOrAdd`. The inconsistent,
  non-reproducible-in-the-same-way nature of the symptom was itself the tell that this
  was a threading bug rather than a logic bug (a logic bug fails the same way every time).
- **Case-sensitive texture name lookup.** A material referenced
  `m03_01_wall_cliff_00` (lowercase) while the actual TPF entry was named
  `m03_01_Wall_Cliff_00` (mixed case) — the game engine treats these as equivalent, a
  default C# `Dictionary` does not. Both texture-name dictionaries now use
  `StringComparer.OrdinalIgnoreCase`.
- **Alpha/cutout materials.** FLVER0's parsed `Material` exposes no blend-mode flag at
  all (verified by reading the actual `SoulsFormats.FLVER0.Material` source — just
  `Name`/`MTD`/`Textures`/`Layouts`), and at the time no `.mtd` files were present
  anywhere in the extracted data to parse authoritatively either, so the MTD *path
  string* was the only available signal for this fix. (Stale as of a later session:
  WitchyBND has since unpacked `mtd.mtdbnd.dcx` into `mounted/mtd/` — 306 real, parseable
  `.mtd` files exist now, and `SoulsFormatsNEXT` already has a working `MTD.cs` reader
  exposing a real `g_BlendMode` enum. Not yet used anywhere; this heuristic hasn't been
  revisited against it.) `_Edge` → `AlphaScissor` (hard cutout, confirmed against grass — a
  clean near-binary alpha mask matching the sprite silhouette exactly), `_Alp` → `Alpha`
  blend (soft transparency, confirmed against a hair material). This follows the same
  naming convention used across FromSoft's other Souls titles, not a Demon's-Souls-only
  guess.
- **Missing `_Add` (additive blend) MTD case.** User noticed a couple of map pieces with
  no visible alpha/blend at all: `m8003b1.flver` (light rays/fog in Shrine of Storms,
  `m03_01_00_00`) and `m3051b0.flver` (the Nexus's central statue, whose "sun rays" mesh
  had no blending). Both use MTDs ending in `_Add`
  (`A03_vollight11[Dn]_Add.mtd`, `A05_vollight[Dn]_Add.mtd`) — a third blend-mode suffix
  the `_Edge`/`_Alp` check didn't cover, so these fell through to the opaque default.
  Confirmed as a real, consistent FromSoft convention (not guessed) by scanning every
  mounted FLVER's materials: 22 materials across 15 distinct map files all use `_Add`,
  and every one of them is a volumetric-light/cloud/water-shimmer effect
  (`vollight`, `light_shaft`, `cloud`, `water02`) — never used on solid geometry. Fixed
  by adding an `_Add` branch in `GetOrBuildMaterial` (`Transparency = Alpha` +
  `BlendMode = Add`, the standard Godot recipe for additive glow effects). Verified: full
  reimport still lands on the same 16 known errors, and both named files reimport with no
  errors.
- **Global mirror on X (the big one — went unnoticed for a long time).** Every imported
  map and model was a left/right mirror image of the real thing. Invisible for so long
  because it's *self-consistent*: mirroring one axis also flips the apparent triangle
  winding, so the existing winding check (face normals vs. FLVER's stored vertex normals,
  99%+ match) and the UV-V check both still passed — a fully-mirrored-but-internally-
  consistent import looks exactly as "correct" as a real one to both of those checks. A
  bilaterally-symmetric human skeleton doesn't reveal it either (a mirrored person still
  looks like a normal person). What actually caught it: the user recognized
  `m03_01_00_00` (Shrine of Storms) as a literal mirror image of `m03_00_00_00`, then
  confirmed on `c9990` (default Fluted Knight) — shield/sword were on the swapped hands.
  Root cause confirmed two independent ways: (1) FK-resolving `c0000`/`c9990`'s own
  skeleton (`Node.ComputeLocalTransform`, `Scale*RotX*RotZ*RotY*Translation`) shows the
  bind-pose bone tree puts `L_*` bones at +X and `R_*` bones at -X in FLVER's raw
  coordinates, while `c9990`'s actual baked shield/sword mesh geometry sits on the
  opposite side from its matching `L_Shield`/`R_Shield` bone once imported unchanged; (2)
  the map-level mirror is a non-anatomical, purely geometric confirmation of the same
  thing. Fix (`FlverSceneImporter.cs`): negate X on both position and normal, and swap
  two triangle indices per face — the winding swap is required *because of* the new X
  negation (mirroring one axis flips the apparent winding that was previously
  "coincidentally" matching Godot's clockwise-front convention). Verified: reimporting
  all ~1776 FLVER files still produces exactly the same 16 known errors (no regression),
  and the user visually confirmed both the Shrine of Storms layout and c9990's
  shield/sword hands are now correct.
- **Missing terrain ground-blend layer (hard seams between grass/dirt/stone).** A
  family of map materials carry a *second* texture set (`g_Diffuse_2`, optionally
  `g_Specular_2`/`g_Bumpmap_2`) meant to blend with the first via FLVER's
  `VertexColor`; the importer only read the first set. Fixed via a `ShaderMaterial`
  (`terrain_blend.gdshader`), gated on FromSoft's own `[M]`/`[ML]` MTD bracket tag —
  **not** on `g_Specular_2`/`g_Bumpmap_2` presence, which is independently optional
  per layer and was tried first; it wrongly excluded real grass/cliff materials that
  simply lack a specular layer, confirmed via a 963-material game-wide scan (912 have
  the bracket tag with all four specular/bumpmap combinations present across them; the
  other 51 are unrelated "ghost"/dissolve character materials, a clean split with zero
  overlap). Also fixed the shader's specular fallback hint to `hint_default_black`
  (zero metallic contribution when absent, matching `StandardMaterial3D`'s own
  default) now that specular is confirmed optional per layer.
  - **Blend weight is `VertexColor.A`, not `.R`/`.G`/`.B`** (R=G=B always — confirmed
    non-constant, so "varies 0-1" alone isn't sufficient proof of which channel is the
    real blend weight). Confirmed via game-wide correlation stats (R and A
    essentially uncorrelated, R's mean is >0.9 on 78.6% of meshes — a "no occlusion"
    baseline, not a painted mix weight) and per-triangle transition coverage on a real
    test file (A shows dramatically more actual blend-transition triangles than R
    across all 12 materials checked). Fixed `terrain_blend.gdshader`'s
    `float blend = COLOR.r` → `COLOR.a`.
  - **R is not vertex AO — tried, visually disproven, reverted.** R's AO-like
    statistics (mostly ~1.0, non-constant) were wired in as `ALBEDO *= COLOR.r`, but
    produced sharp patchy light/dark borders even with the sun off. Direct inspection
    showed R only ever takes exactly two discrete values per mesh (e.g.
    `1.000`/`0.549`, nothing between) — a binary per-vertex flag, not a shading
    gradient. Reverted; what R actually encodes is still unknown. **Don't re-try
    "resembles an AO statistical shape" as confirmation** — check whether values are
    actually continuous before wiring anything into a multiply.
  - Verified: full reimport lands on the same known error set; UV channel assignment
    (UV0→diffuse1, UV1→diffuse2, UV2 reserved for the future lightmap, per CLAUDE.md's
    deferred-work section) was double-checked and confirmed correct, unrelated to
    this bug.
- **Water surfaces added (new material family, not a bug fix).** `g_Envmap` is used on
  exactly one MTD family (`DS_Water_Env`/`DS_Water_Env_Skin`) and nowhere else in the
  game's 8889 materials — confirmed via direct scan, so gating on the texture's
  presence (not a filename heuristic — some "water"-named MTDs are actually plain
  alpha-blended splash effects on the ordinary shader) is exact and sufficient. These
  materials carry only `g_Bumpmap`+`g_Envmap`, no diffuse. Added
  `addons/archstone/water.gdshader`:
  - `g_Envmap` is a small flat "impression" image (confirmed by decoding and viewing
    one), not a cubemap — handled as a matcap-style lookup (reflected view-space
    normal's XY as UV), the standard cheap PS3-era substitute for real reflections.
  - Real per-material tuning (tile scale/scroll, water tint, Fresnel curve) only
    exists in the actual `.mtd` file, not FLVER0's own data — confirmed materially
    different values across real files, not boilerplate. `FlverSceneImporter.cs`
    indexes every `.mtd` once at construction (`_mtdIndex`) and reads the real file
    per water material, falling back to the shader's generic defaults if
    unresolvable.
  - **`g_WaterColor` is a Float4, not Float3 — its alpha (`water_alpha`) was being
    silently discarded**, which is what actually separates murky/opaque water (e.g.
    Valley of Defilement's swamp) from clear reflective water (e.g. Boletaria's
    moat). Fixed by blending the refraction toward a flat, fully-opaque tint by that
    amount, further scaled by a depth-based shore/deep fade (`g_WaterFadeBegin` +
    `hint_depth_texture`/`INV_PROJECTION_MATRIX` reconstruction — Godot 4's
    depth-texture API confirmed via the
    [spatial shader reference](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/spatial_shader.html)
    and [this forum thread](https://forum.godotengine.org/t/shader-to-get-view-space-position-from-depth-texture-inv-projection-matrix/101389)
    rather than guessed) so shallow edges stay see-through even on otherwise-murky
    water.
  - **`ROUGHNESS`/`SPECULAR` derive from `g_ReflectBand`** (confirmed constant `0` or
    `0.1` game-wide; `g_SpecularPower` was checked and is a constant `100` everywhere,
    not usable as a per-material signal), adding a small extra real specular
    sun-glint on top of the manual reflection term.
  - **The whole reflection/refraction/Fresnel result is written to `EMISSION`, not
    `ALBEDO`** (`ALBEDO = vec3(0)`). Writing it to `ALBEDO` made it nearly invisible —
    Godot's PBR shifts energy from diffuse to specular as `ROUGHNESS` drops, and
    there's no real reflection probe in-scene for a low-roughness "mirror" to
    reflect. This is a self-contained, hand-computed color the same way the original
    PS3 shader almost certainly worked internally, not a physically-based surface
    meant to plug into Godot's lighting model.
  - **Two genuinely uncalibrated constants, unlike everything else here** (no real
    MTD data gives visibility into the units the original shader used): a
    refraction-sample `SCREEN_UV` offset scale (`refraction_scale = 0.03`, hard-
    clamped — `g_RefractBand`'s real range of `0.05`-`0.8` is far too large to use
    directly against a 0-1 `SCREEN_UV`) and a wave-tiling detail multiplier
    (`wave_detail_scale = 12.0` on the bump-sample coordinate — raw UV tiled at
    ~40-95 world units per repeat on large meshes, confirmed by the user as the
    cause of a "stretched, reflective-looking" wave pattern).
  - `a06_lava[we].mtd` also matches this `g_Envmap` gate and currently renders
    through this same shader — almost certainly wrong for lava (should be opaque/
    emissive, not a reflective liquid). Not fixed, noticed in passing.
  - **Visual accuracy tuning is deliberately paused, not abandoned or broken.**
    Coloring/depth-fade/tiling were confirmed reasonably close against real
    screenshots (Defilement's sampled brightness matched within a few percent);
    Boletaria's remaining gap traced to there being **no `WorldEnvironment`/tonemap/
    exposure configuration anywhere in this project** — checked directly, none
    exists — so all comparisons so far have been against the 3D editor's own
    uncontrolled default preview lighting, not a real baseline. See "Deferred by
    design, not forgotten" below; revisit once real lighting/environment work
    happens (`PLAN.md`).
  - **A real, generalizable Godot/C# gotcha found while wiring this up** — see the
    interop gotchas section below (`SetShaderParameter` + `source_color`-hinted
    `vec3` uniforms).
- **Cross-category texture reuse (a corpse pile rendering fully white, `m4020b0.flver`
  in Boletarian Palace).** User reported a specific pit-of-corpses map piece with no
  textures at all. Root cause: its material referenced `Model/chr/c2000/tex/...` - a
  *character* model's own texture set (`c2000`, an NPC), reused wholesale for a
  decorative map prop. Neither existing texture-resolution path handled this: not a
  same-basename sibling tpf (map pieces don't ship one), and not the map area-bucket
  convention either (`c2000` isn't a map-area folder). Confirmed via a game-wide scan of
  all 8792 `g_Diffuse` refs (not guessed) that this is a real, recurring pattern, not a
  one-off: at least 4 real map files reuse `chr` this way, ~14 more reuse `obj`/`parts`
  the same way (blocked at the time on `obj`/`parts` not being unpacked at all - see the
  "Asset mounting" entry below), and the remaining ~1183 flagged "map" mismatches
  sampled and confirmed to be genuinely orphaned/debug texture references with no
  corresponding data anywhere in the extracted archive at all (mostly `m99_*`/`m08`
  debug/test map slots) - out of scope, not an importer bug. Fixed by adding
  `GetForeignCategoryTextures` (`FlverSceneImporter.cs`): follows the reference path's
  own category straight to that model's real folder instead of assuming everything is a
  map-area bucket.
  - **First attempt assumed a uniform single-tpf-per-folder convention across every
    category - wrong, caught once `obj`/`parts` actually had real data to test against.**
    Built and shipped against `chr` only (the one category with real loose files at the
    time), assuming `mounted/<category>/<subfolder>/<basename>.tpf` flat, no subfolder.
    Once the "Asset mounting" system (below) made `obj`/`parts` real for the first time
    this session, headless verification (`m2210b0.flver`'s three weapon-shield
    materials, previously silently untextured) showed the assumption was wrong two
    separate ways:
    1. **Nested `tex/` subfolder, not flat.** `chr`'s chrbnd entries genuinely flatten
       the tpf straight into the model folder (`mounted/chr/c2000/c2000.tpf`, no `tex`
       folder in the real container entry name at all), but `obj`/`parts` containers
       keep a real `tex` subfolder matching the reference path literally
       (`mounted/parts/Weapon/WP_A_1503/tex/WP_A_1503.tpf`) - confirmed by directly
       inspecting the real extracted output, not assumed. Fixed by trying the nested
       layout first, falling back to the flattened one.
    2. **A folder can hold more than one real tpf, and the one matching the folder name
       isn't always the one with the needed entry.** Even after fixing the subfolder
       issue, the weapon materials *still* didn't resolve. Direct inspection of
       `WP_A_1503.tpf`'s actual internal entries showed `WP_A_1503_kiteshield` (no `_L`),
       while the material referenced `WP_A_1503_L_kiteshield` - which turned out to live
       in a *separate* `WP_A_1503_L.tpf` sitting right next to it in the same `tex/`
       folder. Fixed by merging every `*.tpf` found in the resolved folder into one
       lookup dictionary, rather than opening a single guessed-basename file - the exact
       same merge-everything pattern `BuildAreaTextures` already used for the map-area
       bucket case, which should have been the template from the start instead of
       assuming a single file.
  - **Verification status: resolved and reimport-clean.** Re-verified via the same
    headless surface/albedo inspection script used throughout this project:
    `m4020b0.flver` (chr case) and `m2210b0.flver`'s three weapon materials (parts case,
    the one that caught bug #2 above) all resolve correctly post-fix. Full reimport of
    the entire (now much larger, ~3400-file) mounted set lands on the same known
    vertex-layout bug class as before, no new failure modes introduced by this change.
- **Map-piece naming investigation (curiosity-driven, not a bug report - findings scoped
  for the future "map assembler" work, see PLAN.md).** User noticed `m2020b0l1`,
  `m2030b0l1`, `m2310b0l1` (Boletarian Palace, `m02_00_00_00`) render with large white
  patches, and asked what the `l1` suffix means more generally - is it a LOD system like
  the parts `_L` convention already decoded (see the "Cross-category texture reuse"
  entry above), an unused/cut asset, or geometry specific to the game's opening cutscene
  (the dragon landing on the bridge)?
  - **`_L` (parts/equipment) and `l1` (map pieces) are two separate, unrelated naming
    schemes - don't conflate them.** Confirmed by checking whether `l1` appears anywhere
    else in the ~1030-file map dataset: it doesn't. All 8 `l1` files exist only in
    `m02_00_00_00`, no `l2`/`l3` anywhere, no equivalent suffix on any other map. Whereas
    `_L` is a systematic, universal convention (642/1286 partsbnd files, every equipment
    category).
  - **Geometry comparison (base vs `l1`, same method as the `_L` investigation) mostly
    but not cleanly supports "reduced-detail variant":** 5 of 8 have the same bounding
    box with meaningfully fewer verts/tris (e.g. `m2300b0`: 16849/15434 tris → `l1`
    7706/4278). But `m2310b0l1` has *identical* vertex/triangle counts to its base, and
    `m2030b0l1` has *nearly double* the base's vertex count (11270 vs 5845) - which a
    simple "always-simpler distance LOD" story doesn't explain.
  - **The white patches: fully root-caused, unrelated to the LOD question.** All three
    reported files reference `m02_tower_wall_plain.tga` (+ its normal map) on their
    tower materials - every other texture they use is completely ordinary and resolves
    fine via the ordinary `m02` area-bucket path. Searched every `.tpf` in the entire
    extracted dataset: this texture doesn't exist anywhere. Genuinely orphaned source
    data (same category as `m02_01_00_10.tga`/`o2302` found earlier this session), not
    an importer bug, not fixable on our end.
  - **Tested "used in the opening cutscene" directly against real level-placement data -
    not supported.** `remo/scn020000.remobnd` (area 02) is confirmed real cutscene data:
    4 sequential cuts (`cut0005`/`0010`/`0020`/`0030`), each with its own `Camera.sibcam`
    + Havok `.hkx` animation - structurally exactly what a short opening cinematic looks
    like. But whether any of the 8 `l1` pieces are placed *anywhere at all* - for that
    cutscene or otherwise - is answered by the map's own `.msb` (level layout) file, and
    `SoulsFormatsNEXT`'s `MSB1` reader can't read Demon's Souls' MSBs at all (see
    PLAN.md's map-assembler entry for the exact bug). Worked around it for this one
    question with a crude but effective diagnostic instead of fixing the real reader
    (out of scope for a one-off question): raw ASCII-decoded the `.msb` file and counted
    literal occurrences of each piece's model-name string. Every ordinary placed piece
    (all 8 `l1` files' own base counterparts, plus 5 other random unrelated pieces - 13/13
    checked) scores **exactly 3** occurrences, a tight and consistent signature. All 8
    `l1` files score **zero** - not low, zero, with the identical method that correctly
    found every other piece. If they were used by the cutscene (even hidden-by-default,
    toggled by a script/event), the engine would still need a placement entry
    referencing that exact model name to put it in the world at all, the same as every
    other placed piece - so finding no reference at all is a real, if indirect, result:
    **more likely intended-but-cut than cutscene-specific**, though not provable further
    without actually fixing the MSB reader (which would give real Parts/Events data
    instead of a raw string count) or parsing the cutscene's own TAE/HKX animation data
    for explicit asset references.
  - **Bigger-picture lesson for the future map assembler, not just this one map:**
    confirmed directly, not assumed - **not every loose FLVER sitting in a map's folder
    is actually placed in that map.** A naive "import every `.flver` found under this
    folder" approach (which is exactly what bulk-reimporting the whole `mounted`
    directory today effectively does, harmlessly, since the current importer treats
    every file independently) would silently include orphaned/unused assets like these
    into a real assembled scene. Any future system that assembles a *complete, accurate*
    map instead of just bulk-converting loose files needs to consult the MSB's own Parts
    list to know what's actually placed, not just glob the directory.
- **Origin-offset decoration sub-meshes (map-assembler prerequisite, not a bug -
  investigation only, nothing changed).** User noticed a specific real in-game feature
  (a small archstone/fountain circle partway across the Boletarian Palace entrance
  bridge) is simply missing when importing `m2501b0.flver` whole, and traced it
  themselves to a sub-mesh that's *present* in the file but sitting at local origin
  instead of the piece's real coordinates - asked whether anything in the file explains
  where it's actually supposed to go. Confirmed real and general, not a one-off:
  `m2501b0` (mesh[11], 3046 verts, centered at ~(0,0.7,0)) has a `FLVER.Node` named
  `'fountain'` with `Translation` (17.5, 21.2, -152.5) - suspiciously exactly where that
  mesh should sit relative to its neighbors. Same shape of thing in `m2210b0` (the
  weapon-display piece from the cross-category texture fix above - its origin-clustered
  mesh sits next to a node literally named `'WP_A_1503'`), `m2601b0` (a node named
  `'o2428'`, same obj-ID convention as the `o2429`/`o2302` references found earlier), and
  `m4013b0` (11 separately-named nodes, each a distinct real position - individually
  labeled wood-debris/box-wreckage props, `小物_木材A00` etc).
  - **Checked whether this is a live, importer-readable binding - it isn't.**
    `Mesh.NodeIndex` is the one per-mesh field that could plausibly link a mesh to one
    of these nodes, but it's uniformly `0` on every mesh checked, including the three
    *different* origin-clustered meshes in `m4013b0` that would each need a *different*
    one of its 11 nodes. `UseBoneWeights` is also `false` on every mesh, ruling out
    per-vertex skinning too. So this isn't a quick current-importer fix - whatever
    actually resolves a decoration mesh to its node isn't stored in the FLVER in any
    form found so far.
  - Best-supported explanation, and a third independent confirmation of the same
    prerequisite noted elsewhere in this file: each decoration most likely gets its own
    placement entry in the map's MSB, referencing this shared FLVER plus a specific
    node/sub-mesh - meaning real map assembly needs MSB parsing, not just FLVER import,
    to place these correctly. See PLAN.md's map-assembler entry.
- **Asset mounting system built (new capability, not a bug fix) - WitchyBND is no longer
  needed.** Investigated whether `SoulsFormatsNEXT` (already vendored, GPL-3.0, already
  linked into `Boletaria.csproj`) could replace the manual "run WitchyBND yourself, then
  symlink the result" workflow entirely. Confirmed empirically (scratch code, this
  session): `DCX.Decompress()` correctly handles this game's real compression mode
  (`DCX_EDGE` - confirmed via raw header bytes: `DCP`/`EDGE`/`EgdT`; the library even has
  a literal `Type.DemonsSouls = Type.DCX_EDGE` alias, clearly written with this game in
  mind) and `BND3.Read()` lists real, human-readable entry paths and correct byte content
  against real `.chrbnd.dcx`/`.partsbnd.dcx` containers - no external name-dictionary
  needed, entries carry full paths already (that hashed-name problem is a later-game/BXF4
  issue, doesn't apply here). Built `addons/archstone/AssetExtractor.cs` (walks a raw
  extraction root, unpacks via `DCX`/`BND3`/`BND4`, writes loose files into `res://mounted`
  in the exact layout `FlverSceneImporter.cs` already expects - so the importer itself
  needed zero changes) plus an in-editor "Mount..." UI (`archstone.gd`) and a headless CLI
  entry point (`extract_cli.gd`, added specifically because there's no way to click
  through an actual editor GUI in this environment - also doubles as the intended
  mechanism for a future in-game/compiled-build extraction flow, not built yet). `mounted`
  is a real, gitignored directory now, not a symlink. Real BND entry names use a
  `DVDROOT` segment (not `Model`, which is what FLVER material texture *references* use
  instead - a different, easily-conflated embedded-path convention) - confirmed by
  reading raw entry names directly before trusting a resolution rule, not guessed;
  handled by dropping exactly one segment after `data`, whichever literal word it is,
  rather than hardcoding either name.
  - **A genuine environment hang hit during this work's own verification, distinct from
    the already-documented `UnmanagedCallersOnly` crash-and-exit flake.** A full reimport
    (triggered to verify no regressions after the bug fixes above) silently stalled -
    zero log output for 49+ minutes, all 29 process threads sleeping, CPU usage trending
    down not up - while sitting on an entirely ordinary map file (`m2016b1.flver`, a
    same-category terrain-blend piece with no cross-category references at all, ruled out
    as content-specific by inspecting its materials directly before assuming it was an
    environment flake rather than a real new bug). Killed and re-ran; Godot's reimport is
    resumable (confirmed: the retry only had ~21 files left to redo, meaning the killed
    run had already gotten through effectively the whole ~14k-file set before hanging) and
    completed cleanly on retry with the same known error set as before. Treat as the same
    general class of threaded-reimport-host flakiness already documented below, just a
    silent stall instead of a crash this time - re-running is still the correct recovery.

- **Non-visual game-logic data investigation (curiosity-driven, not a bug report -
  findings scoped for the future Phase 2 gameplay-recreation work, see PLAN.md).** User
  asked what, if anything, outside `PS3_GAME/USRDIR` could ever matter to the
  recreation (`PARAM.SFO`, `ICON0.PNG`, `TROPDIR`) - none of it does, it's all PS3
  platform/packaging metadata (boot info, XMB icon, PSN trophy definitions), not game
  content. Then asked the harder question: with `EBOOT.BIN` itself correctly ruled out
  as off-limits (it's Sony's encrypted SELF container - decrypting it is DRM
  circumvention, the same category this project's own README disclaimer already rules
  out), is any of the *gameplay logic* people would assume only exists in that compiled
  executable actually available another way? Investigated directly against the real
  mounted dump rather than guessing from general Souls-series knowledge.
  - **Full raw-root top-level layout, confirmed by listing it directly:** `chr`, `map`,
    `obj`, `parts`, `mtd` (the categories `AssetExtractor` already handles), plus
    `param`/`paramdef`, `script`, `msg`, `font`, `menu`, `facegen`, `item`, `sfx`,
    `sound`, `shader`, `remo`, `testdata`, `tropdir`, and `EBOOT.BIN`. `shader` (PS3 GPU
    microcode) is irrelevant regardless - this project already hand-writes its own Godot
    shaders (`terrain_blend.gdshader`/`water.gdshader`) from MTD parameters rather than
    using the original shader binaries at all.
  - **`script/` is plain, unencrypted Lua - same approach as DS1, confirmed not
    guessed.** 272 loose `.lua` files (map event scripts, `_eventboss` variants, a
    `global_event.lua`) plus `script/ai/out/` holding the shared goal-oriented AI
    library (`top_goal.lua`, `walk_around.lua`, `attack2.lua`/`attack3.lua`, etc.) and
    per-enemy-ID behavior files (`823000_battle.lua`, `510000_logic.lua`). Opened one
    directly: plain Shift-JIS-commented Lua source, human-readable as-is. Per-map
    `.luabnd` containers also exist (`m01.luabnd`) - read one with
    `SoulsFormats.BND3.Read()` (the exact same reader `AssetExtractor.cs` already uses):
    49 entries, real BND3 magic, bundling that map's own event scripts plus the entire
    shared AI library plus that map's specific enemy roster's behavior scripts. Each
    `.luabnd` also has a `.dcx` sibling (still fully open, `DCX`/`BND3` already handle
    it) and a `.sdat` sibling - checked that one's header too: `NPD`, Sony's
    encrypted/signed edata wrapper, the same DRM category as `EBOOT.BIN`. The plain and
    `.dcx` forms are the disc-native ones and already fully readable; `.sdat` is a
    redundant encrypted copy (likely for a different distribution channel) that should
    be skipped by extension if `script` is ever added to
    `AssetExtractor.KnownCategories`, not chased.
  - **`param`/`paramdef` confirmed readable via `SoulsFormats.PARAMDEF.Read()` against
    real files, and DeS ships its own paramdefs** - unlike later titles (DS2/DS3/Elden
    Ring), which need the community `Paramdex` project's reconstructed schemas, this
    game's own dump already has everything needed to parse its params. Checked real
    field names directly (not assumed from other games) across `AtkParam`,
    `BehaviorParam`, `NpcParam`, `EquipParamWeapon`, `SpEffectParam`, `NpcAtkParam`,
    `CharaInitParam`, `AiStandardInfo`, `EnemyStandardInfo`, `GameInfoParam`: stamina
    cost/regen/damage is fully exposed as literal fields everywhere you'd expect it
    (`AtkParam.stamina`, `NpcParam.stamina`/`staminaRecoverBaseVel`,
    `EquipParamWeapon.attackBaseStamina`, `SpEffectParam.changeStaminaRate`, etc). DeS's
    poise-like mechanic is internally called **Super Armor**, not Poise -
    `NpcParam.superArmorLimitDamage`/`superArmorLimitTime`,
    `EquipParamWeapon.attackSuperArmor`, `AtkParam.atkSuperArmor` - and there's no
    separate DS1-style "Poise stat reduces hitstun" field in any table checked; fan
    terminology and FromSoft's own internal naming diverge here. No literal
    invincibility/i-frame field found anywhere across all tables checked.
  - **Followed that gap into animation data - `.tae` (Time Act Event) files are real,
    unencrypted, and already parse.** `chr/c0000/c0000.anibnd` (real BND3, confirmed via
    magic bytes) bundles a `.tae` file per animation set (`a00.tae`, `a10.tae`, etc,
    alongside the raw `skeleton.hkx`) - these are exactly per-animation timed-event
    files, the category of thing that would encode dodge-roll invincibility windows and
    hit-cancel timing in later Souls games. `SoulsFormats.TAE.Read()` parses `a00.tae`
    directly with zero errors: 386 real animations, auto-detected as
    `TAEFormat.DES` - a Demon's-Souls-specific format variant the library already
    explicitly, distinctly supports (separate from `DS1`/`DESR`/`DS3`/`SOTFS`/`SDT`).
    Each event carries precise start/end timestamps in seconds and a numeric event
    `Type` (observed values include 0, 16, 128, 225, 229) - the raw structure is fully
    present and working today. What's missing: event `Type` IDs have no friendly name
    without an external template (`TAE.Template`, paired with the `EDD` format for
    names - the same relationship `PARAMDEF` has to `PARAM`) - none sourced or built for
    DeS specifically yet, so today this reads as numbered event slots, not yet
    "invincibility"/"hitbox"/"sound cue" by name.
  - **Per-character `.esd` files exist too** (`c0000.esd`/`.esd.dcx`/`.esd.dcx.sdat` -
    same plain/compressed/NPD-encrypted three-way pattern as `.luabnd` above). ESD
    (state machine format, `SoulsFormats.ESD` already exists) likely governs
    animation/behavior state transitions - not opened or tested this session, flagged
    for whenever this becomes relevant rather than left silently unknown.
  - **Web search for community-documented reference values found a real split, not a
    total gap.** Stamina regen has a solid, sourced formula (Fextralife: 45/s base,
    Eternal Warrior's Ring +12/s, specific stacking penalties for slow-roll/overburden/
    blocking) and roll-type/equip-burden tiers are well documented (≤50% burden = fast
    roll/max i-frames, above that a slower "fat roll" with fewer i-frames, no benefit
    below 30%) - both good enough to implement with real confidence. Precise i-frame
    counts and per-weapon Super Armor frame windows are **not** publicly documented
    anywhere found for Demon's Souls specifically - contrast with Dark Souls 3, which
    the community has mapped in detail - a genuine documentation gap for this one
    title, confirmed by multiple targeted searches turning up only qualitative
    descriptions ("invincible for roughly the first third of the roll"), not numbers.
  - **Bottom line:** none of the above needs any DRM circumvention. `script`/`param`
    are trivial extensions of the extraction system already built (add to
    `AssetExtractor.KnownCategories`, skip `*.sdat`); TAE reading needs an event-type
    naming template before its semantics are usable, and the specific numeric constants
    Fextralife etc. don't cover will likely need direct frame-by-frame observation
    against the real game in RPCS3 - but none of that is a wall, just remaining work.

- **Texture-resolution redundancy found and unified (`GetAreaTextures` deleted, not a bug fix on its own but the audit that led to real fixes below).** User asked directly whether the accumulated per-case texture-resolution methods (area-bucket, foreign-category, and whatever obj/parts-specific fix was about to be added next) were actually clean and non-redundant, or "smaller solutions taped together." Checked rather than assumed: scanned all 26,656 real texture refs across the mounted corpus and found `GetAreaTextures` never once succeeded where `GetForeignCategoryTextures` didn't already resolve the same reference identically, plus 123 cases where only the latter succeeded — the two methods weren't complementary, one was strictly subsumed by the other. Replaced both (plus the about-to-be-added obj/parts sib/tex fix) with a single ordered `CandidateDirs` chain and one shared `_dirTextureCache`/`GetMergedTextures` primitive — see CLAUDE.md's "Texture resolution" section for the resulting architecture. Real fixes that came out of building it properly instead of stacking one more method on top:
  - **obj/parts sib/tex split** (universal to all 777 obj + 309 parts models, not an edge case): their containers put the `.flver` in its own `sib/` folder with `tex/` as a sibling, unlike chr's flat same-folder layout — confirmed via directory scan, not assumed from chr's convention. This alone had been silently leaving every obj/parts model with no own-container texture lookup at all.
  - **Nexus archstones (`o1000`-`o1050`) share one body texture via a copy-pasted, never-updated ref** — all six only have their `g_Lightmap` slot honestly labeled; `g_Diffuse`/`g_Specular`/`g_Bumpmap` still point at the `o1000` template's own folder. Fixed by `SiblingMapAreaDir`: if any other slot on the same material resolves under `mounted/map`, try that folder for this slot too.
  - **`o3120`/`o3129` (Boletaria wall pieces) mislabel every ref on the material, including the lightmap, under their own obj category** — recoverable only because the texture filenames themselves still start with the real `m03_` map-area prefix. Fixed by `MapPrefixDir` (regex `^(m\d\d)_` against the filename, independent of whatever the reference path's category segment claims).
  - **Zero-byte `.tpf` crash** (`o3104`, `o0050`, `o7999`) — surfaced only once `CandidateDirs` became the first code path to actually open these obj models' own `tex/` folders. `System.IO.EndOfStreamException` inside `TPF.Read` aborted the *entire* model's import, not just the one bad texture. Confirmed as real shipped data (genuinely 0-byte files), not an extraction bug. Fixed with a per-file try/catch in `LoadDirTextures` (`GD.PushWarning` + skip).
  - **Missing FLVER0 `ParamName`** — a separate concern from location, not folded into `CandidateDirs`: some texture entries have a real path and a real texture but no slot type at all (`SoulsFormats` leaves `ParamName` null when `typeOffset == 0`). Recovered via `InferMissingParamNames`, positionally matching untyped entries against `Dif`/`Spc`/`Bmp`/`Lit`/`Dcl` tokens in the MTD's own bracket tag. Validated 100% (83/83 real affected materials, 44 files across every category including the `c9983`/`c9981` Ghost/Wanderer phantom-gear template) by scanning the corpus before writing the fix, not after.
  - Deliberately **not** generalized into a fifth `CandidateDirs` rule: `o6510_1` (a destructible-prop variant) needs a bottle/vase texture (`m02_obj_00.tga`) that's byte-identical across 6 *other* obj models' own folders, not in its own container and not in any map bucket. Unlike rules 3-4 above, there's no cheap signal for *which* sibling obj folders to search — flagged as needing more real examples before it's worth the scan-cost/collision-risk tradeoff of a general "search other obj folders" rule.
- **`obj/` "empty scene" investigation (mostly confirmed-correct-as-is, not a bug — see CLAUDE.md's "Known deferred work" for the stable conclusions).** User flagged large contiguous `obj/` ID ranges opening as scenes with zero nodes at all. Scanned all 1068 mounted `obj/` FLVER0 files directly (SoulsFormats, bypassing the importer) rather than guessing from a couple of samples: 1006 have real meshes, 62 don't. Of those 62, 57 are genuine dummy-only attach/anchor markers (0 meshes, 1-2 `Dummy` points, confirmed correct as imported — nothing for any resolution rule to recover) and 5 are 288-byte bare stubs with nothing at all (allocated-but-never-built IDs). One sub-cluster initially miscategorized as "empty" (`o6760`/`o6761`/`o6770`/`o6771`/`o6780`/`o6781`) turned out to be real: tiny 4-16 vert alpha-blended spiderweb decal quads (`map\M[D]_Alp.mtd`, `m06_spiderweb_00.tga`, resolved correctly from the model's own `tex/` folder) — confirmed via direct headless `load()` producing a correct `Mesh` node, `StandardMaterial3D`, `Alpha` transparency, and a resolved `albedo_texture`. The user's original "blank" observation on this specific sub-cluster predated the sib/tex + `CandidateDirs` + `ParamName`-inference fixes above landing and being reimported — not a separate bug.
- **Destructible-prop debris cluster (`o6511`-`o6602`) — investigated, root-caused as genuinely out of importer scope for now, not fixed.** Distinct from the dummy-only markers above: after a full reimport and editor restart, user confirmed this specific cluster was *still* blank, ruling out the stale-import explanation that resolved the spiderweb case. Verified directly against the raw, unmounted PS3 files rather than trusting the mounted output alone: `obj/o6511.objbnd` and its `.objbnd.dcx` sibling (both exist on disc, decompress to byte-identical inner content) — the shipped game data for this object really does contain 0 triangles, confirmed at the source, not an extraction bug. Went looking for where the real geometry might come from instead and found `map/breakobj/*.breakobj` in the raw root: 20 files (one per map area), magic header `OBJB`, a completely undocumented FromSoft-specific format with no reader anywhere in `SoulsFormatsNEXT`. Working theory (not yet confirmed, needs the format reverse-engineered to verify): these dummy-only IDs are physics-only fracture anchors, and the actual visible debris at runtime is composed by the Havok destruction system from a small shared library of generic rubble/plank/pottery chunk meshes, driven by whatever `.breakobj` packs (placement, per-piece physics, which shared chunk to use) rather than stored per-object-ID — which would mean no per-file resolution rule, however clever, could ever recover a mesh that was never stored per-object in the first place. Scoped as real future work (see PLAN.md), not attempted further this session.

## C#/Godot interop gotchas worth remembering

- `EditorSceneFormatImporter._ImportScene`'s exact signature
  (`string, uint, Godot.Collections.Dictionary`) matters — verified against
  `GodotSharpEditor.xml`'s doc comments, not guessed. A mismatched override wouldn't
  necessarily error loudly; it could just silently not be treated as an override.
- `ShaderMaterial.SetShaderParameter()` from C#, for a shader uniform hinted
  `: source_color` (e.g. `uniform vec3 water_color : source_color`), needs a `Color`, not
  a `Vector3` — passing `Vector3` doesn't throw, it silently no-ops (the parameter reads
  back `null` via `get_shader_parameter()`, as if never set, while every other
  non-color-hinted uniform set the same way works fine). Found by inspecting an imported
  water material headlessly and noticing exactly the color-hinted uniforms were null.
- `ProjectSettings.GlobalizePath()` is a safe no-op on paths that are already absolute
  filesystem paths (not `res://`), so it's fine to call it even when unsure whether the
  path Godot hands you is virtual or already global.
- `Image.CreateFromData`'s `useMipmaps` parameter means "this buffer already contains a
  full mip chain" — not "please generate mips for me." Pass `false` and call
  `image.GenerateMipmaps()` yourself, or the base-level-only buffer gets rejected/
  misread.
- `StandardMaterial3D.Metallic` defaults to `0.0` and *multiplies* against
  `MetallicTexture` — setting the texture alone has zero visible effect without also
  setting `Metallic = 1.0f`.
- `MetallicTextureChannel` defaults to reading only the texture's Red channel. A true
  full-RGB spec map's G/B data is silently dropped. Known, accepted ceiling — not
  chased further since there's no PBR-correct slot for a Blinn-Phong spec map anyway
  without a custom shader.
- A `CSharpScript.new()` call can transiently fail (`Invalid call. Nonexistent function
  'new' in base 'CSharpScript'`) for the *first* headless invocation immediately after a
  project directory rename — Godot's own editor-side "is the C# assembly ready" tracking
  seems to be keyed off the old path. A `dotnet build` from the new location plus one
  throwaway import pass resolves it; it isn't a real code regression when it happens
  exactly once right after a move.
- Godot's bulk reimport is genuinely multi-threaded against the *same* importer plugin
  instance. Any mutable state held at the importer-instance level (caches, anything not
  local to a single `_ImportScene` call) needs real thread-safety, not just "probably
  fine since it's just a cache."
- A bulk reimport has, once, died mid-batch with
  `Fatal error. Invalid Program: attempted to call a UnmanagedCallersOnly method from
  managed code.` Re-running recovered fully across a few attempts with no permanent
  damage or data loss (Godot's reimport is resumable — it just picks up whatever wasn't
  finished). Not root-caused. If it starts happening on *every* run rather than as an
  occasional flake, that's worth actually investigating (possibly related to the
  threaded-reimport-plus-Godot-object-construction-off-main-thread combination generally,
  not just this project's own cache).
- **"A full reimport completed" isn't proof a specific file reflects the current importer
  code.** Observed at least once: after implementing new resolution logic and running a
  full reimport, an individual file still needed a targeted second check (a direct
  `load()` + node/material inspection, the same method used throughout this project) to
  actually confirm it picked up the new code's output. Not root-caused beyond "on top of
  the already-documented mid-batch-abort flake" — when a specific file's behavior is in
  doubt, verify it directly rather than trusting that a completed bulk reimport covered
  it correctly.

## Environment specifics (this machine)

- **Net8 runtime: no special setup needed on Arch (superseded 2026-07-20).** Godot
  4.7's `GodotTools.dll` (the "build solutions" feature) hardcodes a `net8.0` target
  with no cross-major `rollForward`, but `dotnet-sdk-8.0` is in Arch's own `extra`
  repo and installs cleanly alongside newer SDKs (`pacman -S dotnet-sdk-8.0`), no
  `DOTNET_ROOT` override needed — confirmed by running both `dotnet build
  Soulbrandt.csproj` and a full headless import with `DOTNET_ROOT` unset. An earlier
  revision of this doc wrongly assumed Arch didn't package a net8 SDK at all and used
  a manually-merged `~/.dotnet-godot` root as a workaround; that premise was wrong
  and the workaround is obsolete. If this regresses on a future Arch update, merge a
  fresh net8 install into one root with the existing system SDKs via symlinks rather
  than pointing `DOTNET_ROOT` at an isolated net8-only install (see the dead-ends
  entry below on why isolating it breaks SDK discovery).
- `godot-mono --headless --build-solutions` hangs indefinitely in this environment even
  with the runtime fixed, for reasons never fully root-caused beyond "something in
  Godot's own in-process MSBuild invocation." Given up on making it work; `Boletaria.csproj`
  is hand-authored instead, using the exact `Godot.NET.Sdk/4.7.0` + `net8.0` +
  `PackageReference`/`ProjectReference` shape Godot would have generated, and it builds
  fine via plain `dotnet build`.
- Running the GUI editor and a headless `godot-mono --import` against the same project
  simultaneously causes contention/hangs. Only ever run one Godot process against this
  project at a time.

## Dead ends — don't re-try these

- Assuming Steam's Godot or "the latest official Godot" supports C# without switching
  builds — neither does; only the dedicated `.NET`/mono build does.
- Assuming an official/Steam Godot build has the `dds` module compiled in by default —
  checked empirically on two different builds, neither did. Moot now anyway since the
  native importer never relies on Godot's built-in DDS loader.
- Porting `Program.cs`'s OBJ-export winding swap or V-flip into the native importer "to
  be safe" — both are specifically wrong there (see above). If either bug's symptom
  reappears (inside-out geometry, misaligned textures), the fix is to *remove* a
  compensation, not add one back.
- Isolating `DOTNET_ROOT` to just the newly-installed net8 runtime — breaks SDK
  discovery. Always merge into one root instead of pointing at a narrow one.
- Treating an empty `~/godot/Boletaria/` directory (containing only a stray `.claude/`
  folder) as meaningful after the Soulbrandt rename — it was a harness cwd-recovery
  artifact from a stale path reference, not anything intentional. Already deleted; if
  something similar reappears after a future rename, it's the same harmless mechanism.

## Deferred by design, not forgotten

See CLAUDE.md's "Known deferred work" for the current list (animation/skeleton import,
the FLVER vertex-format crash, the metallic-channel ceiling, LOD z-fighting). The
in-editor asset-mounting UI (replacing the `mounted` symlink stopgap) is done - see
"Asset mounting system built" above. Its in-*game*/runtime counterpart (a shipped build
loading assets with no editor at all) is still not started, same as before.

Water shader visual-accuracy tuning (see the "Water surfaces added" bug entry and its
five follow-ups above) is explicitly paused by the user, not stuck or abandoned - the
data pipeline (real per-material MTD params reaching the shader correctly) and the shader
logic itself (coloring, depth fade, tiling) are confirmed working and reasonably close
where they've been checked against real screenshots; what's left is a genuine environment/
lighting gap (no `WorldEnvironment` anywhere in the project, so testing has been against
the editor's own uncontrolled default preview), not something more shader iteration can
fix. Revisit once real lighting/environment/ambience work happens (see `PLAN.md`).
