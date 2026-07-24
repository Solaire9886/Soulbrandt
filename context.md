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
- **FLVER0 vertex-layout crash fixed — two separate crash sites sharing one root cause, not one bug.** The long-standing known error (`ArgumentOutOfRangeException` on `m9999b0.flver`/`m9900.flver`/`o9996.flver`) turned out to need two fixes, discovered in sequence rather than both guessed up front:
  - **Root cause**: confirmed via `SoulsFormatsNEXT`'s `Vertex.Read` that some FLVER0 meshes' own `BufferLayout` genuinely has no member for Normal/UV/VertexColor at all — not a parser bug, and not per-vertex variation (`Mesh.Read` resolves one layout per mesh and reuses it for every vertex in the loop, so checking vertex 0 for a missing channel is provably representative of the whole mesh). All three confirmed-affected files have the "reserved/debug ID" look FromSoft games use for collision-only/tool geometry never meant to render.
  - **Fix 1 (the importer's own vertex-array loop, `FlverSceneImporter.cs`)**: `v.Normals[0]`/`v.Colors[0]`/`v.UVs[0]` were indexed unconditionally when building the position/normal/color/UV arrays. Guarded each on `.Count > 0`, falling back to `Vector3.Up`/`Colors.White`/`Vector2.Zero`. Verified against a full 3411-file mounted-corpus reimport: all 8 `m9999b0.flver` copies (one per affected map area) now import clean.
  - **Fix 2 (one level deeper, inside the `SoulsFormatsNEXT` submodule itself)**: that same full-corpus run still logged exactly 5 crashes — all 4 `m9900.flver` copies plus `o9996.flver` — at a *different* line than fix 1 touched: `Mesh.Triangulate`'s `doCheckFlip` path (`Mesh.cs:509`), which the importer called with `doCheckFlip: true` hardcoded. That flag only fires on triangle-strip meshes at a strip-restart marker, where it averages `v1.Normal`/`v2.Normal`/`v3.Normal` against a computed face normal to decide whether to flip winding — meaningless, and an empty-list crash, on a mesh with no Normal data. Not patched in the submodule (unlike the UV-scale-factor bug elsewhere in this file): the library already exposes `doCheckFlip` as a caller-side opt-out for exactly this ("Some ACFA map model faces will mess up using this, so an argument has been added to disable it" — the library author's own comment), so gating it at the `FlverSceneImporter.cs` call site (`vertices[0].Normals.Count > 0`) is the correct place, not a workaround. Passing `false` unconditionally instead would have "fixed" the crash too but silently broken winding-correctness on every strip mesh that legitimately needs the check — confirmed necessary, not just convenient, by reading what the flag actually guards before disabling it.
  - **How the fix was confirmed corpus-wide without a second full 3411-file reimport**: the fix-1-only full run already enumerated every remaining crash across the *entire* mounted corpus (not just the previously-known files) — 5 instances, all mapping to fix 2's exact failure mode. A scoped reimport of just those 5 files (touched, not a full cache invalidation) with fix 2 built in showed 0 errors. Since fix 2 only changes behavior when `Normals.Count == 0` (provably not the case for any file that wasn't already in that enumerated list), this is a valid proof of corpus-wide resolution, not an assumption — redoing the full 3411-file pass would have re-verified the same thing at ~10x the time/memory cost for zero new information.
- **Lightmap system added (new material capability, not a bug fix) — the `g_Lightmap` gap CLAUDE.md's "Known deferred work" had flagged since the terrain-blend fix above.** User asked to understand the mechanism and implementation routes before committing to one; investigated empirically rather than assuming the standard "multiply against a baked texture" convention held here too:
  - **Manual probe, not just a design discussion.** A throwaway `SceneTree` C# script (`ScratchLightmapProbe.cs`, deleted after use) decoded a real diffuse/lightmap pair via the exact same pipeline as `DecodeTexture` (Headerize → Pfim → BGRA swap), then a second pair, computing `diffuse * lightmap` and saving all three as PNGs for direct visual inspection. Findings: the lightmap texture on its own is visibly a real per-mesh UV atlas (distinct island-shaped blobs, each with its own internal gradient — not noise, not a misdecode); resolution is far lower than diffuse (128×128/512×512 vs 1024×1024, confirming it's genuinely unshared per-mesh, unlike diffuse's heavy cross-instance reuse); the straight-multiply composite's shadow/highlight shapes land exactly on the lightmap's own island boundaries, the signature of a real bake, not a coincidence. The composite read very dark (mean ~10/255) — traced to the same missing-`WorldEnvironment`/tonemap gap already documented for water above, not a math error, since the underlying lightmap data itself reaches full 255 brightness in real spots (confirmed via numeric min/max, not just eyeballing).
  - **Corpus survey before writing any shader code**, since the composite math alone didn't determine the architecture — the open question was how lightmap presence overlaps with the existing blend/water material families, which decides how much of Godot's `Custom0`-channel complexity is actually needed. A second throwaway script (`ScratchLightmapSurvey.cs`) parsed every mounted FLVER0's materials directly: 7394/11646 materials have a lightmap (63%); of those, 6529 are plain single-layer, 865 are also blend materials, and — cleanly — 0 are water (`g_Envmap` never co-occurs with `g_Lightmap`, so `water.gdshader` needed no changes at all). Also confirmed, with zero exceptions across every lightmapped mesh in the corpus, that the lightmap's UV channel is always the mesh's *last* one (2 total on non-blend+lightmap meshes, 3 on blend+lightmap) — the exact assumption CLAUDE.md's deferred-work note had made from UV-range analysis alone, now verified structurally too.
  - **A third check that changed the shader design**: whether lightmapped materials ever also need the `_Edge`/`_Alp`/`_Add` transparency handling `BuildStandardMaterial` already did for non-lightmapped materials. They do — 677 of the 6529 single-layer lightmapped materials carry one of these tags (e.g. `C_DullLeather[DSB][L]_Alp_Skin.mtd`, `M[DB][L]_Edge.mtd`). Since Godot's `blend_mix`/`blend_add` are compile-time `render_mode` keywords (not a per-material property the way `StandardMaterial3D.BlendMode` was), this couldn't collapse into one runtime-switched shader the way the water/blend families do — settled on three thin files (`lightmap.gdshader`/`lightmap_alpha.gdshader`/`lightmap_add.gdshader`) sharing one `#include "lightmap_common.gdshaderinc"` for the actual sampling logic. The opaque and `_Edge` cases *do* share a single file: writing `ALPHA_SCISSOR_THRESHOLD` (default `0.0`, i.e. never discards) keeps Godot's non-sorted opaque render path regardless of the runtime threshold value, unlike real `ALPHA` blending — confirmed from Godot's own shader semantics, not assumed.
  - **Implementation**: `terrain_blend.gdshader` gained a `lightmap` uniform (`hint_default_white`, so the 47/912 blend materials without one see a harmless no-op multiply) sampled at `CUSTOM0.rg` and multiplied into `ALBEDO` alongside the existing two-layer mix. `FlverSceneImporter.cs`'s per-mesh vertex loop now derives `needsUV2`/`needsLightmapCustom0` from a new shared `ClassifyMaterial` helper (factored out of `GetOrBuildMaterial`'s inline `isWater`/`isBlend` checks so both call sites can't drift out of sync) instead of inferring intent from the already-built `Material`/`Shader` instance, which couldn't distinguish "blend without a lightmap" from "blend with one." Packs FLVER's third UV channel into a `float[]` (2 floats/vertex, `u0,v0,u1,v1,...`) assigned to `Mesh.ArrayType.Custom0`.
  - **A real Godot API gap, resolved via reflection rather than guessing.** `SurfaceTool.CreateFromArrays` takes no explicit array-format parameter in 4.7's C# binding (confirmed via `System.Reflection` against `GodotSharp.dll` directly, not just the XML docs, which only documented the *read-back* format ambiguity for `Custom0`/etc, not how to *set* it) — the only place to declare `Custom0`'s component format (`Mesh.ArrayCustomFormat.RgFloat`, 2 floats/vertex, packed via `(ulong)RgFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift`) turned out to be `ImporterMesh.AddSurface`'s existing but previously-unused `flags` parameter.
  - **Verified empirically, the same discipline as the vertex-layout crash fix, not assumed from the code compiling.** A full corpus reimport (cache invalidated, same as any importer-code change) completed with zero errors — confirming the new shader paths don't crash on any of the ~3400 mounted files, not just the couple of test files touched. A second throwaway script then loaded one real blend+lightmap import result (`m0002b1.flver`) back via `PackedScene.Instantiate()` and read every terrain-blend surface's actual `Custom0` array via `Mesh.SurfaceGetArrays()`: every one came back as a real `PackedFloat32Array` (confirming the `flags` plumbing above actually worked, not silently ignored), correct length (`vertCount * 2`), and `[0,1]`-bounded non-zero values matching the raw FLVER UV2 data read directly from the same file for cross-check.
  - **Incidental finding, not yet used anywhere**: the MTD bracket tag on lightmapped materials consistently includes an `[L]` token (e.g. `A03_4Stone[DSB][L].mtd`) alongside the existing `[M]`/`[ML]`/`_Edge`/`_Alp`/`_Add` convention. Not wired in as a detection gate — `mat.Textures.Any(t => t.ParamName == "g_Lightmap")` is already fully reliable (including the `InferMissingParamNames` untyped-texture-recovery path, which already maps the bracket tag's `Lit` token to `g_Lightmap`) — but worth knowing if a cheaper filename-only signal is ever needed elsewhere.
- **Lightmap system, part 2 — nearly everything rendered white after the user's own reimport+in-editor test, traced to two real shader bugs plus a separate, much larger import-cache corruption issue.** User report: after reimporting maps/materials and viewing the entire Boletarian Palace map in the real editor, ~99% of textures showed as flat white (only unrelated non-lightmapped materials, e.g. billboard trees, looked normal). The verification done when the feature above was built (a full corpus reimport with zero errors, plus reading back `Custom0` mesh-array data) turned out to have a real blind spot, not a false "all clear":
  - **Root cause 1 — `--headless` never actually compiles GLSL.** It forces Godot's dummy rendering driver, which loads/saves `Shader`/`ShaderMaterial` resources and reports zero errors regardless of whether the shader source is even valid - confirmed directly (`RendererDummy`/`DummyShader` show up in the dummy driver's own class names). Every verification this session had been headless, so a real compile error in the brand-new shaders was structurally invisible to it. Forced a real compile by running under `xvfb-run -a godot-mono ... --rendering-driver opengl3 --display-driver x11` (Mesa llvmpipe software GL) - immediately surfaced two genuine `SHADER ERROR`s:
    - `lightmap_common.gdshaderinc`'s `lightmap_fragment()` helper assigned `ALBEDO`/`NORMAL_MAP`/`METALLIC` directly - `Unknown identifier in expression: 'ALBEDO'`. Godot's shader built-ins are only assignable directly inside `fragment()` itself, never from a helper function it calls, `#include`-d or not (a real GDShader restriction with no equivalent in plain GLSL, where output variables are ordinary globals). Fixed by changing the shared function to `lightmap_shade(uv, uv2, out vec3 albedo, out vec3 normal_map, out float metallic)` and assigning the actual built-ins in each of the three `fragment()` bodies from its `out` results.
    - `terrain_blend.gdshader`'s `texture(lightmap, CUSTOM0.rg)` inside `fragment()` - `Unknown identifier in expression: 'CUSTOM0'`. Unlike `UV`/`UV2`/`COLOR`, a mesh's `Custom0` channel is *not* automatically available as an interpolated fragment-stage input in Godot 4 - only inside `vertex()`. This directly contradicted what CLAUDE.md's original deferred-work note had assumed ("read back in-shader via `CUSTOM0.rg`") - that assumption was never actually verified against a real compiler before being written. Fixed by adding a `varying vec2 lightmap_uv;` set from `CUSTOM0.rg` in a new `vertex()` function, read from `fragment()` instead.
    - Re-ran the same Xvfb+opengl3 check after both fixes: zero `SHADER ERROR`s across `lightmap.gdshader`/`lightmap_alpha.gdshader`/`terrain_blend.gdshader`.
  - **A crash cascade during this investigation, caused by the verification method itself, not the code under test.** Running several back-to-back Xvfb+opengl3 Godot processes (real, if software, GPU rendering - CPU/memory-heavy) while the user separately had the real GUI editor open with the entire Boletarian Palace map loaded created enough resource contention that, per `coredumpctl`, a burst of unrelated processes crashed within ~90 seconds: KDE's `baloo_file_extractor` (SIGSEGV x2), `drkonqi` (SIGSEGV, crashed handling the baloo crash), this Claude Code session's own process (SIGABRT), and finally the user's real GUI Godot editor itself (SIGSEGV, 3.2GB core - consistent with dying mid-render of the full map). No kernel OOM-killer event this time (unlike the earlier documented GUI+headless-import incident), so this was raw contention, not a hard memory-exhaustion kill. Lesson folded into CLAUDE.md's shader-verification note: never run the real-driver Xvfb check concurrently with the GUI editor or another such check.
  - **Root cause 2, much larger in practice - the GUI editor crash above almost certainly interrupted it mid-write, corrupting the import cache far beyond the shader bugs' own blast radius.** After the crash, re-checking the exact file being tested (`m0002b1.flver`) showed `GD.Load` failing outright: `ERROR: Cannot open file 'res://.godot/imported/m0002b1.flver-....scn'`. Scanned every `mounted/**/*.flver.import` file's own recorded `dest_files` target against whether that file actually exists on disk: **1395 of 3411 (41%) were missing** - 1185 `map`, 121 `chr`, 89 `obj`. Root cause: Godot's incremental importer trusts an `.import` file's recorded timestamps, not whether its target cache artifact is actually present - if a reimport is interrupted (crash, or the already-documented `UnmanagedCallersOnly` threaded-import flake) partway through writing a file's target, the `.import` metadata can be left looking "up to date" for a file that was never actually written, and every later incremental `--import` silently skips it forever, no error, since nothing looks stale. This explains the user's original report far better than the shader bugs alone do: a huge fraction of the map's meshes had no valid cached geometry/material data at all, not just an incorrectly-composited lightmap.
    - **Fixed via the documented "Force a reimport" procedure** (`rm -f .godot/imported/*.flver-*` + touch every `.flver` + `--import`), run alone this time (no concurrent Xvfb/GUI process). Needed two passes: the first hit the known `UnmanagedCallersOnly` flake at 62% and died; simply re-running `--import` (not re-deleting the cache) picked up the remaining files and completed with zero real errors - exactly the recovery behavior CLAUDE.md already documented for that flake, now also exercised for a much larger interrupted-batch scenario, not just a fresh one.
    - **A real process-tracking mistake worth remembering**: the first reimport attempt was launched with a trailing shell `&` inside a single Bash tool call rather than the tool's own `run_in_background` mechanism. The tool call returned almost instantly (since backgrounding + the trailing `echo` both complete immediately) and was later reported "complete" - but that completion was only the trivial wrapper script, not the actual multi-minute `godot-mono --import` process, which kept running detached and unmonitored. Caught by checking `ps aux` directly rather than trusting the notification; the fix was `nohup ... & disown` plus a proper `run_in_background`-tracked polling loop (`while kill -0 <pid>; do sleep 5; done`) for subsequent attempts.
  - **Verified end-to-end, not just "no errors this time"**: after both fixes, re-checked `m0002b1.flver`'s materials via a cheap headless script - every `lightmap.gdshader` surface has a real, correctly-sized `diffuse`/`lightmap` texture bound (`diffuse1` correctly `NULL`, wrong uniform for that shader); every `terrain_blend.gdshader` surface has real `diffuse1`/`lightmap` textures bound (`diffuse` correctly `NULL`). A final single Xvfb+opengl3 render (GUI editor confirmed closed, only one heavy process this time) produced a screenshot with genuine texture/lightmap detail - visible stone texture and shading variation, not flat white. Still reads dark, matching the already-documented "no `WorldEnvironment`/tonemap configured" gap from the water-shader work, not a new bug.
- **Lightmap system, part 3 — darkness + wrong contrast root-caused and resolved (2026-07-22): missing sky in the scene's `WorldEnvironment`, not a shader/color-space bug.** Confirmed not an sRGB/gamma mismatch (an isolated Xvfb+opengl3 pixel-readback test found zero decode difference across `source_color`-hinted vs unhinted `ShaderMaterial` samplers and `StandardMaterial3D` for a runtime-built `ImageTexture`) and not a double-lighting/`ALBEDO`-vs-`IRRADIANCE` architecture issue either (never got far enough to need testing - see below). User added a real `WorldEnvironment` node to the test scene (previously only using the editor's own top-bar preview tweaks, which don't reflect actual scene lighting) and found removing the sky entirely, using flat ambient light instead, fixed ~75% of the darkness/contrast gap immediately - screenshot comparison (`Screenshot_20260722_003748.png` vs the real game's `Screenshot_20260722_004720.png`, both Boletarian Palace's main gate) is now close: same silhouette, stone tone, overcast mood. Remaining open items: geometry past a certain distance goes pure black with no sun/sky (ambient alone doesn't seem to reach it); brightness/color/contrast still needs finer manual tuning against reference footage. Both are environment/lighting-setup work, not importer code.
- **Lightmap system, part 4 — contrast/desaturation/artifact fix attempted three ways this session (2026-07-22), all reverted; root architecture identified but blocked on unparseable per-map data.** Follow-up to part 3: even with a real `WorldEnvironment` (sky removed, flat ambient) closing most of the gap, straight `diffuse * lightmap` still crushed shadows to pure black and blew out highlights — no ambient/tonemap setting could fix this since ambient is one linear multiplier over `ALBEDO` and can't lift a dark majority without blowing out a bright minority (confirmed via a real per-vertex-UV probe, not a blind texture-grid sample: large valid-UV surfaces on real Nexus meshes still sampled mean ~0.03-0.13 on the lightmap, 60-95% of texels under 0.05-0.20).
  - **Real DeS MTD data** (read via `SoulsFormats.MTD.Read()` on the lightmapped Nexus/Boletaria materials) confirmed these are tagged `g_LightingType = HemEnvDifSpc` (hemisphere + environment + diffuse + specular). `g_DiffuseMapColorPower`/`g_SpecularMapColorPower` (0.6/1.5) exist as real fields but are confirmed unused by the DSR shader source below — legacy from an older pipeline revision, not wired into anything. `g_DiffuseMapColor`/`g_SpecularMapColor` are neutral `(1,1,1)`, not a tint source.
  - **Community ground truth**: found `AltimorTASDK/dsr-shader-mods` on GitHub — decompiled HLSL source for Dark Souls Remastered (shares FromSoft's "FRPG" engine lineage with DeS; DS1/DSR has a far larger reverse-engineering community than DeS does), specifically `FRPG_FS_HemEnv.fx`, the exact shader family DeS's `HemEnvDifSpc` lighting type names. Caveat: DSR is a 2018 PBR rewrite, not a byte-exact match for the 2009 DeS shader, but it's the only concrete source-level ground truth found. Key finding: the real shader **never multiplies the lightmap against the base diffuse/ambient result**. It only scales a secondary environment/IBL reflection term (`envLightComponent = CalcEnvIBL(...) * lightmapColor.rgb`), while a separate, always-on two-color hemisphere-ambient term (`Mtl.DiffuseColor * CalcHemAmbient(Mtl.Normal)`, blending two colors by vertex-normal Y) is added afterward, completely untouched by the lightmap. The raw lightmap sample is also gamma-corrected before use (`pow(lightMapVal.rgb, gFC_DebugPointLightParams.z)`). The two hemisphere colors (`gFC_HemAmbCol_u`/`_d`) are external runtime constant registers (`FC_REG(c98)`/`c99`) — real per-map lighting data supplied at draw time, **not stored in FLVER0/MTD/TPF**, and not parseable by anything in this project currently. Same category of gap as `.breakobj` above.
  - **Attempt 1 (gamma + flat neutral floor)**: `lm_shaped = mix(vec3(lightmap_floor), vec3(1.0), pow(lm, gamma))`, `ALBEDO = diffuse * lm_shaped`. Fixed the crush/blowout convincingly (user-confirmed against RPCS3 reference screenshots for Nexus and Boletaria). Regression: user reported maps now looked "dull" — a per-channel probe confirmed both diffuse and lightmap textures are genuinely warm-toned (R>G>B) on real data, and the flat gray floor was diluting that real baked color specifically in shadow regions, not just adding brightness.
  - **Attempt 2 (hue-preserving reciprocal boost)**: `boost = lightmap_floor / luminance(lm)`, `lm_shaped = lm * boost`, scaling the lightmap's own color vector up by magnitude while preserving its hue instead of mixing toward gray. Regression: unbounded division by near-zero luminance (the lightmap's own near-black texels, per the probe stats above) blew up DXT block-compression quantization noise into large, visibly blocky purple/green/magenta patches (screenshot: `test.png`) — confirmed the mechanism, not guessed, since the artifacts are literally block-shaped (matches DXT's 4×4 block granularity) and concentrated exactly where luminance is smallest.
  - **Attempt 2b (same boost, clamped to 6x)**: bounding the multiplier didn't help — user reported "not much of a visible change at all." Root cause: since the lightmap's mean is ~0.03-0.13, nearly every texel's unclamped boost was already far past 6x, so nearly every texel was hitting the clamp ceiling anyway; clamping a value that's already saturated everywhere doesn't change the picture.
  - **Attempt 3 (additive floor tied to diffuse color, not lightmap color)**: `ALBEDO = diffuse * (lm + lightmap_floor)` — matching the real shader's actual structure (`DiffuseColor * HemAmbient`, added, not derived from the lightmap). Structurally can't blow up regardless of how close to zero `lm` gets, since there's no division left at all, and the floor's hue now comes from the real (non-noisy) diffuse texture instead of the lightmap. Verified compile-clean under a real (opengl3/x11) rendering driver. Regression: user still reported "obvious patches of green and purple/magenta," though "much better than before." **Since this is the one attempt with no division/reciprocal anywhere in the math, the persistence of color patches through a structurally different formula is itself a finding**: it points away from the compositing formula as the artifact's cause and toward the raw lightmap texture decode itself (DXT block-compression noise inherent to the near-black texels, exposed by *any* approach that brightens shadows at all, not specifically by a reciprocal/division) — not yet confirmed, not investigated this session.
  - **Reverted to the original straight `diffuse * lightmap` multiply** (both `lightmap_common.gdshaderinc`'s `lightmap_shade()` and `terrain_blend.gdshader`'s inline compositing) — none of the three attempts shipped. The lightmap system's plumbing (UV routing, `Custom0` packing, alpha/add shader variants) is untouched; only the compositing formula was touched and reverted.
  - Real fix is blocked on the same kind of gap as `.breakobj`: no currently-parseable DeS file format carries per-map hemisphere-ambient color data. Next avenues, not yet attempted: (1) probe the raw decoded lightmap texture itself for a decode bug or confirm the noise is genuinely inherent to the source DXT data, independent of any compositing math; (2) pursue the RPCS3 real-shader-dump path (`showDebugTab=true` → `shaderlog/`) for byte-exact 2009 DeS shader instructions instead of DSR's approximation; (3) treat this as blocked on the bigger WorldEnvironment/GI-level ambient system already scoped in `PLAN.md`, since a real per-normal hemisphere ambient term is itself a lighting-system feature, not a material-shader one.
- **Lightmap system, part 5 — followed part 4's avenue (1) (probe the raw decode directly); found real evidence the artifact is a block-read-alignment problem, not organic DXT quantization noise, and not fully root-caused (2026-07-22).** Prompted by the user's part-7 in-editor report ("less obvious [on Nexus] but still there" after the hemisphere/env fix).
  - **First test picked the wrong kind of asset entirely** - `o1000` (a Nexus archstone) is an `obj`-category model, and its `g_Lightmap` reference doesn't even resolve to a real texture via the real importer's own `ResolveTexture` (checked via reflection into the actual private method, not reimplemented by hand) - consistent with the user's observation that `obj` models aren't placed anywhere in the current test scene at all (no MSB parsing/placement exists yet). Not a bug: this asset is simply untested by any current in-editor view. Switched to `m3051b0.flver` (the Nexus's central statue, a real `map`-category piece, confirmed already-referenced in earlier notes) for a texture that's actually in view.
  - **Confirmed the artifact is real and present in the raw decoded texture, not shader-side**: decoded every `*_lit_*` texture in `mounted/map/m01` via the real `DecodeTexture` (reflection into the private method - exact pipeline, not a reimplementation), applied a diagnostic-only hue-preserving brightness boost (same mechanism part 4's Attempt 2 used, here purely for visualization) to each. `m01_lit_B0m9304`/`m01_lit_B0m9302` (91.7%/89.3% near-black) showed clear purple/magenta/green speckle noise once boosted; a zoomed crop with a 4-texel grid overlay confirmed the noise aligns to block boundaries (one fully solid green 4x4 block, hard edges matching the grid exactly) - the same block-granular shape part 4 already suspected, now directly visually confirmed for the first time.
  - **Ruled out ordinary DXT1 quantization/rounding error as the cause.** These `format=0` textures are BC1/DXT1 (`Headerizer.cs`'s format table). Found the single worst near-black chroma-divergent texel programmatically (not by eye), manually decoded its real compressed block from `texture.Bytes` per the BC1 spec (both the unambiguous 4-opaque-color interpretation and the color0≤color1 punch-through-alpha interpretation - they agreed with each other for this block, since color0>color1 here) - and the resulting achievable palette was confined to `[0,8]` per channel. **Pfim's actual decoded output for the same texel and its neighbors was `(19,19,19)`/`(25,28,25)`/`(14,9,14)`** - values a correct decode of *that block's own bytes* cannot produce under any interpolation. A real decode can't exceed its own two reference colors' range, so this isn't rounding/quantization error - Pfim is very likely reading different bytes than the block my (block-index × 8, plus `ReadPS3Images`'s confirmed 0x80-byte leading pad) arithmetic pointed at.
  - **Leading suspect, not confirmed: PS3 block-level swizzling for compressed formats, never undone.** `Headerizer.cs`'s `ReadPS3Images` only calls `DrSwizzler.Deswizzler.PS3Deswizzle` for a short explicit list of *uncompressed* formats (`R8G8B8A8_UNORM`, format 9/16/26) - DXT1 (format 0, what every one of these lightmaps uses) never triggers it. If PS3 also tile-swizzles compressed/BC texture data at the block level and this codebase's linear read order doesn't match it, the symptom would be exactly this: blocks effectively read out of their real order. Nearly invisible on busy diffuse textures (gross image structure - stone, statues, etc, per the Boletaria screenshot in part 7 - still reads correctly), glaringly obvious on near-uniform dark lightmap regions, where a swapped-in block from a brighter/different part of the same texture reads as a hard-edged, wrong-colored patch - matching every characteristic observed (block-granular, hard-edged, colored, concentrated at low luminance, worse on some textures than others).
  - **Not confirmed, and deliberately not chased further this session per user decision ("log it for now and move on").** Confirming the real mechanism would need either decompiling Pfim (no decompiler tooling installed, e.g. `ilspycmd`/`monodis` - checked, none available) to see its actual read order, or a real from-scratch reverse-engineering pass matching the true PS3 block-swizzle pattern - bigger in scope than a shader tweak, since it would mean changes to `SoulsFormatsNEXT` (the user's own fork/submodule), not just this project's importer/shader code. If revisited: start from `DrSwizzler`'s existing (working, for uncompressed formats) swizzle/deswizzle implementation as a reference for what pattern a compressed-format equivalent might need, rather than guessing from scratch.
- **Lightmap/drawparam system, part 5 — outside confirmation narrows the part-4 "blocked on unparseable data" conclusion to a specific, already-half-parseable path (2026-07-22).** A contact with DeS reverse-engineering knowledge, asked cold (no context given beyond the general question) whether this project accounts for drawparam colors, sun-angle-from-collision, and vertex color, confirmed independently: drawparams matter for both lightmap and blend-material shading, vertex color is "used way more" in DeS than DS1, and they're applied via per-part `LightID`/`FogID`-shaped fields in the MSB (their words, loosely recalled) referencing rows in the game's `.param` files.
  - **Searched for a literal `DrawParam` format across all of `SoulsFormatsNEXT` and `FORMATS.md`: doesn't exist.** The only string hits are `PartsDrawParamID` in `MSBE`/`MSBVI` (Elden Ring / Armored Core VI's MSB variants) — an unrelated ID field on much later games, not a DeS format.
  - **The graphics-config formats that do exist in the library are all explicitly DS2-and-later, confirmed via their own doc comments/version enums, not assumption:** `GPARAM.cs` ("A graphics config file used since DS2"), `BTL.cs` ("used in BB, DS3, and Sekiro"), `BTAB.cs` ("introduced in DS2"), `BTPB.cs` (`BTPBVersion` enum only has `DarkSouls2LE`/`BE`, `Bloodborne`, `DarkSouls3`, no DeS case at all). Cross-checked against the real mounted extraction: zero `.btl`/`.gparam`/`.fltparam` files exist anywhere in it — confirms DeS predates this whole format lineage rather than this project just failing to extract it.
  - **`MSBD` (DeS's own MSB reader, "extremely basic support... cannot be written" per `FORMATS.md`) already has the exact fields the contact half-remembered, already parseable by this project's fork, currently just unused:** `PartsParam.LightID` and `PartsParam.FogID` (`SoulsFormatsNEXT/SoulsFormats/Formats/MSB/MSBD/PartsParam.cs:231,236`, both single bytes, both marked `Unknown` in the library's own doc comments — nobody upstream has documented what table they index into either, matching the contact's own hazy recall), plus `EventParam.Light.PointLightID` (`EventParam.cs:288`, a separate fixed-point-light placement list).
  - Per the contact, the actual color/ambient values these IDs point at live in generic `.param` rows — not a dedicated format. `PARAM`/`PARAMDEF` are already supported generically by `SoulsFormatsNEXT` (see `FORMATS.md`'s PARAM section), but a param file carries no self-describing row layout; interpreting `LightID`/`FogID`'s target table needs the matching DeS paramdef, which hasn't been identified yet (not even the param's name is known).
  - **Scope, assessed but not yet acted on:** this data would directly resolve two already-documented open items — the part-4 hemisphere-ambient blocker above, and the still-unidentified vertex-color R/G/B signal from the terrain ground-blend investigation (this file, ~line 197-204: confirmed a binary two-value-per-mesh flag, disproven as AO, cause left unknown) — and would open two pieces of currently-nonexistent functionality (per-map fog via `FogID`, placed point lights via `EventParam.Light`). It would only partially help the separate Boletaria water/general-brightness gap (part 3/4 above), which is also blocked on the still-nonexistent `WorldEnvironment`/tonemap/exposure setup — necessary input, not a full fix by itself.
  - **Required plumbing, none of it started:** MSB isn't parsed anywhere in this project today — the importer only ever reads loose `chr`/`map`/`obj`/`parts`/`mtd` files per-mesh-file, with no per-map-placement concept at all; `AssetExtractor`'s category allowlist has no MSB/param/paramdef category; the specific paramdef/table `LightID`/`FogID` reference is still unidentified.
  - **Recommended next step, not started:** a small throwaway investigation — pull one map's MSB plus its param/paramdef, confirm what row `LightID`/`FogID` actually resolve to and that it holds real hemisphere-ambient-shaped color data — before committing to building MSB-parsing plumbing into the importer. Same "verify the premise before building on it" lesson as the DSR-shader-approximation caveat in part 4 above: a knowledgeable outside claim is a strong lead, not yet independently confirmed against real DeS data by this project.
- **Lightmap/drawparam system, part 6 — part 5's recommended next step done: `LIGHT_BANK`/`FOG_BANK` confirmed against real data, and a tier-1 shader fix shipped using it (2026-07-22).** A throwaway C# `SceneTree` script (`ScratchParamProbe.cs`, deleted after use, same convention as part 3's `ScratchLightmapProbe.cs`) read `paramdef/*.paramdef` directly (loose files, no BND unpacking needed — DeS ships them loose, unlike DS2+) into a `Dictionary`-backed list, then `PARAM.Read()` + `ApplyParamdefCarefully()` against `param/drawparam/m01_lightbank.param`/`m01_fogbank.param`. Both parsed cleanly (`ParamType` `LIGHT_BANK`/`FOG_BANK`, 64 rows each, no BND wrapping needed for these per-map bank files at all — confirmed by direct file listing, only the `a0X_drawparam.parambnd` variants are container-wrapped).
  - **Confirms the part-5 lead structurally, not just that the files parse.** Row IDs/names line up 1:1 between the two banks (row 0 "神殿部分"/temple-section, row 2 "縦穴"/vertical-shaft in both `LIGHT_BANK` and `FOG_BANK`) — real evidence `LightID`/`FogID` share one row-ID space picking a matched light+fog preset per map situation. `LIGHT_BANK`'s row shape is a real hemisphere-ambient rig, not inferred: three directional lights (`degRotX/Y_0..2` + `colR/G/B` + a `colA_x` "RGB multiplier %" field), a genuine **up/down ambient pair** (`colR/G/B/A_u`, `colR/G/B/A_d`) plus `du`/`dd` variants, a specular-sun set (`_s`), and `envDif`/`envSpc` color+multiplier fields — matching the DSR `FRPG_FS_HemEnv.fx` shape from part 4 far more concretely than the decompiled DS1R source alone did. `envSpc_0..3`'s values (`0,1,2,3`) don't look like colors or multipliers at all (too small, always sequential) — still unidentified, likely a mode/index selector, not wired into anything below.
  - **Shipped a tier-1 shader fix using this data, explicitly scoped as a floor-swap, not the real per-part answer.** `lightmap_common.gdshaderinc`'s `lightmap_shade()` no longer does a straight `diffuse * lightmap` multiply; it now does `albedo = diffuse * (hemi + env)` where `hemi` is `ambient_up`/`ambient_down` (uniform, `source_color`) blended by world-space `normal.y`, and `env = lightmap * env_color * env_intensity` — matching the DSR shape (hemisphere floor untouched by lightmap, lightmap only scales a secondary env term). Uniform defaults are `default_lightbank.param` row 0's real values (colA's "RGB multiplier %" divided by 100: `ambient_up_intensity`/`ambient_down_intensity` = 0.3, `env_intensity` = 2.0), not invented numbers — the global-fallback bank, since no per-part `LightID` resolution exists yet (MSB parsing still unstarted, same gap part 5 already named).
  - **Two real shader-compile errors found and fixed, same "only under a real driver" pattern as the ALBEDO/NORMAL_MAP restriction in part 3:** (1) a `varying` can't be assigned from a helper function either, only from `vertex()`/`fragment()` directly — `lightmap_world_normal()` now *returns* the value, each shader variant's own `vertex()` assigns it to the varying. (2) `MODEL_MATRIX`, unlike `NORMAL`, isn't visible for *reading* inside a non-entry function at all (`SHADER ERROR: Unknown identifier`) — worked around by passing both `NORMAL` and `MODEL_MATRIX` in as explicit parameters from `vertex()` instead of reading the built-ins directly inside the helper. Verified compile-clean afterward via `xvfb-run -a godot-mono --path . -s ScratchShaderCheck.cs --rendering-driver opengl3 --display-driver x11` (also deleted after use) against all three single-layer variants (`lightmap`/`lightmap_alpha`/`lightmap_add`). `terrain_blend.gdshader`'s own separate `* texture(lightmap, ...)` multiply was deliberately left untouched this pass — same fix applies there, scoped as a follow-up, not forgotten.
  - **Known, accepted risk: this is structurally close to part 4's reverted Attempt 3 (`diffuse * (lm + lightmap_floor)`), and will likely still show the same purple/green patch artifact.** With `default_lightbank.param` row 0's `ambient_up == ambient_down`, the normal-Y hemisphere blend collapses to a near-flat ~0.3 floor for this particular global-fallback row - functionally close to attempt 3's flat floor constant, just sourced from real data instead of invented, and `env` still directly scales the raw `lm` sample the same way attempt 3's floor term did. Part 4's own conclusion was that the artifact traces to the *raw lightmap texture decode* (DXT block-compression noise in near-black texels), not the compositing formula - any formula that brightens shadows re-exposes it. Explicitly kept anyway, per user decision, as a real step forward on the data/formula side (real per-map constants now exist and are wired in, MSB `LightID` can swap in a differentiated up/down row later without a formula rewrite) while the lightmap-decode-noise question is deliberately deferred to a separate investigation, not treated as blocking.
- **Lightmap/drawparam system, part 7 — real in-editor test narrows the part-6 "known risk" from a generic artifact to a path-specific one, and terrain_blend.gdshader gets formula parity (2026-07-22).** User tested the part-6 shader fix in the real GUI editor across two maps.
  - **Nexus: the expected outcome.** Reads "great," part-4/part-6's artifact still present but "not too obvious" - consistent with the known, accepted DXT-decode-noise risk, not a new problem.
  - **Boletarian Palace: a different, worse symptom - large hard-edged dark patches with no gradient/blending at all, on specific surfaces.** Screenshot (`Screenshot_20260722_211056.png`, the cliff/terrain around the main gate, looking back away from it) confirmed directly: the palace stonework itself (plain `lightmap.gdshader`, native `UV2`) looks clean, while the surrounding rock/cliff terrain (`terrain_blend.gdshader`, `Custom0`-packed lightmap UV) shows several sharp-edged near-black rectangular regions with no soft transition to their surroundings - visually distinct from part 4's small-scale purple/green block noise.
  - **User confirmed the pattern generalizes**: every affected surface on Boletaria uses the terrain-blend shader specifically, none use the plain single-layer path - even though, at the time of the report, only the single-layer family had gotten the part-6 hemisphere/env fix and `terrain_blend.gdshader` was still on the old straight multiply. Ruled out per-mesh lightmap-texture reuse as an explanation first: confirmed (part 3) that lightmap textures are baked per-mesh, never shared the way diffuse textures are, so "some pieces broken, hard edges at their boundary with unaffected neighbors" is architecturally expected regardless of cause - there's no shared texture to blend across that boundary in the first place.
  - **Working theory, not yet confirmed:** the correlation with `terrain_blend.gdshader` specifically (rather than a generic "lightmap texture problem") points at something particular to that path - its `Custom0`-based lightmap UV (more fragile than the single-layer path's native `UV2`, and the newer, less-exercised of the two), the fact it hadn't gotten the hemisphere/env fix yet at report time, or genuinely bad baked data isolated to those specific pieces. Not distinguished yet; would need decoding one affected piece's actual resolved lightmap texture directly (same manual-probe method as part 3/4) to tell "bad UV landing on the wrong texel" from "the baked texture itself has real black rectangles."
  - **Shipped formula parity as the immediate next step, per user direction ("go ahead with further improvements... before trying anything else").** The hemisphere-ambient/env math (uniforms, `hemisphere_world_normal()`, `hemisphere_ambient()`) was factored out of `lightmap_common.gdshaderinc` into a third shared file, `hemisphere_ambient.gdshaderinc`, now `#include`-d by both `lightmap_common.gdshaderinc` and `terrain_blend.gdshader` - avoids duplicating the same formula a second time now that two independent shader families need it. `terrain_blend.gdshader`'s `fragment()` changed from `ALBEDO = mix(diffuse1, diffuse2, blend) * texture(lightmap, lightmap_uv).rgb` to the same `diffuse_color * (hemisphere_ambient() + env)` shape as the single-layer path, `env = lightmap * env_color * env_intensity`; its `vertex()` now also sets `world_normal` via `hemisphere_world_normal(NORMAL, MODEL_MATRIX)` alongside its existing `lightmap_uv = CUSTOM0.rg`. Verified compile-clean under a real driver (`ScratchShaderCheck2.cs`, deleted after use) across all four shaders (`lightmap`/`lightmap_alpha`/`lightmap_add`/`terrain_blend`), with no GUI editor running concurrently (checked via `pgrep` first, per the part-2 concurrency lesson).
  - **User-confirmed fixed.** After reload, the terrain-blend formula parity fix resolved the hard-edged dark-block symptom on Boletaria - the root cause really was `terrain_blend.gdshader`'s stale plain multiply, not a separate texture/UV bug on that path. The still-open block-noise question (part 5 below) is a different, smaller-scale artifact, confirmed distinct from this one.
- **Lightmap/drawparam system, part 8 — `envSpc`, `LIGHT_BANK`'s second environment term, wired in (2026-07-22).** Per user direction ("touch #4 next"). `LIGHT_BANK` carries two separate environment color+intensity pairs - `envDif` (already used, part 6, folded into `ALBEDO` via `env`/`env_intensity`) and `envSpc`, which the DSR shader source (part 4) keeps as a distinct specular-response term rather than another diffuse multiplier. Added `env_spc_color`/`env_spc_intensity` uniforms to the shared `hemisphere_ambient.gdshaderinc` (same `default_lightbank.param` row-0-sourced defaults as the rest, coincidentally identical here to `envDif`'s: white, 200%). Written to `EMISSION` rather than folded into `ALBEDO`/`METALLIC` - same reasoning as `water.gdshader`'s own hand-computed reflection (no real reflection probe exists anywhere in this project), weighted by the material's own specular texture sample so it only shows where the surface is actually meant to be reflective. Required plumbing a new `emission` out-param through `lightmap_shade()` (signature now `(uv, uv2, out albedo, out normal_map, out metallic, out emission)`) and each of the three single-layer shader variants' `fragment()`; `terrain_blend.gdshader` computes its own blended specular sample once and reuses it for both `METALLIC` and the new `EMISSION` term. Verified compile-clean under a real driver across all four shaders (`ScratchShaderCheck3.cs`, deleted after use), no editor running concurrently.
- **Lightmap/drawparam system, part 9 — part 5's "PS3 block-swizzle, never undone" suspect for the DXT1 block-noise artifact tested directly and disproven (2026-07-23).** An external comparison against Soulstruct (`github.com/Grimrukh/soulstruct` + `soulstruct-blender`, a public Blender-addon FLVER/TPF importer with mature Demon's Souls support) found its PS3 deswizzle (`deswizzle_dds_bytes_ps3`) is format-generic — driven by DXGI block size, explicitly including `BC1_UNORM`/DXT1 — unlike this project's `Headerizer.ReadPS3Images`, which only deswizzles a short list of uncompressed formats (`R8G8B8A8_UNORM`, format 9/16/26) and skips every compressed format including DXT1. Read strongly as corroboration of part 5's suspicion.
  - **Implemented and built**: changed `ReadPS3Images` to call `DrSwizzler.Deswizzler.PS3Deswizzle` unconditionally for every PS3 texture rather than gating on format. Confirmed via `ilspycmd`-decompiling the `DrSwizzler` NuGet package directly (no source available otherwise) that `PS3Deswizzle`'s own block-size derivation (`IsPixelFormatCompressed` → block size 4 for any BCn DXGI format, 1 otherwise) already handles compressed formats correctly in isolation — the gate really was just in this project's calling code, not a downstream library limitation.
  - **Disproven by direct before/after evidence, not just re-inspection.** Dumped the Attribute Nexus archstone `o1000`'s resolved `g_Lightmap` texture to PNG by calling `FlverSceneImporter._import_scene()` directly (bypassing Godot's import cache entirely, so no reimport/cache-invalidation cycle needed for the comparison) — once with the original code, once with the format-generic deswizzle fix, same file, same run session. **Result was the opposite of the hypothesis**: the pre-fix texture is a coherent, recognizable image; the post-fix (deswizzled) texture is the *same* hard-edged checkerboard/block-scrambled mess the artifact reports describe. Applying PS3 deswizzle to this texture doesn't fix corruption, it *causes* it.
  - **Root cause of the wrong lead: a pre-existing doc comment in the exact function touched was overlooked before making the change.** `Headerizer.Headerize`'s own XML doc already says: *"By default, we'll assume no swizzling, PC type. Bear in mind Demon's Souls and Dark Souls 1 do NOT use PS3 swizzling and should be assigned 'PC'!"* — this project had already empirically settled, at some earlier point not otherwise documented in this file, that DeS mostly doesn't need PS3 deswizzling at all, and the original narrow uncompressed-only condition was deliberate tuning for this specific game, not an oversight matching Soulstruct's more generic implementation. Soulstruct's own approach may be correct for the games/textures it primarily targets (or may have the same issue for DeS specifically and nobody's checked against real screenshots) — it doesn't transfer here regardless.
  - **Reverted.** `SoulsFormatsNEXT` submodule working tree confirmed clean (`git status` empty, back to `d1de279`) after `git stash`/rebuild-compare/`git stash pop`/revert-edit/rebuild. Part 5's block-noise artifact remains **not** root-caused — this was the only concrete lead anyone had toward it, and it's now ruled out rather than confirmed. Next avenues are the two part-5 already named (decompile Pfim's real read order, or reverse-engineer the true PS3 compressed-block-swizzle pattern from scratch) — both bigger in scope than this session attempted.
  - **Process lesson**: read every doc comment/existing guard on the exact function being touched before trusting an external comparison's applicability, even a well-evidenced one — the contradicting comment was sitting in the same function this session edited, and would have caught the wrong direction before any build/test cycle was spent on it.
- **The old `EditorSceneFormatImporter` pipeline (deleted 2026-07-24, see the pivot entry below) absorbed several rounds of real stability work before being replaced outright rather than repaired — compressed to a summary here since none of the mechanism it describes still exists.** Confirmed problems along the way, in order: an `o0103.flver` hang reproducible only under Godot's threaded bulk-reimport dispatch, never in a direct call (pointed at the importer's per-instance texture caches accumulating unboundedly across a whole reimport run); a per-process-restart batching mitigation for that same growth that needed a second fix after an early version wiped the whole corpus's cache up front and silently defeated its own restart on the very first test; a texture-cache self-eviction fix (`MaybeEvictDecodedTextures`, budget-based off `GC.GetGCMemoryInfo()` so it scales to weaker hardware automatically — still present in `FlverModelBuilder.cs` today, this part of the fix outlived the importer it was written for) that did bound peak memory, verified with both the real budget and an artificially tiny test budget; and finally a `SemaphoreSlim` concurrency gate that, after confirming directly against Godot's own engine source that no config-level or per-plugin lever exists to control reimport concurrency at all (`EditorSceneFormatImporter` has no `_can_import_threaded()` hook; `threading/worker_pool/max_threads` is hardcoded-ignored for the editor process), still didn't fix the underlying hang → `systemd-coredump` CPU spike → crash cycle. That last dead end — Godot gives a plugin no control over its own reimport concurrency — is what actually motivated the architecture pivot below, not another patch attempt.
- **Next avenue, prototyped in concept via a real reference implementation (2026-07-23), that led directly to the pivot below.** Bypassing `res://`'s import system entirely — confirmed viable, not just theorized, by reading a peer developer's own project (thanks Nox). Their pattern has **no `EditorSceneFormatImporter`/`EditorImportPlugin` anywhere at all** — a plain `@tool extends Node3D` with a manual button click, calling straight into ordinary `RefCounted` classes for parsing/caching, confirmed to be the same code path their real shipped game uses at runtime, not just an editor convenience. Their per-model cache is a plain in-memory `Dictionary` (never needs `ConcurrentDictionary` — nothing forces concurrent access once Godot's reimport queue is out of the picture) with no on-disk cache of parsed assets at all, just a re-parse per load. Confirmed the architecture direction was real and workable, not speculative — see the pivot entry below for what actually got built from it.
- **Destructible-prop debris cluster (`o6511`-`o6602`) — investigated, root-caused as genuinely out of importer scope for now, not fixed.** Distinct from the dummy-only markers above: after a full reimport and editor restart, user confirmed this specific cluster was *still* blank, ruling out the stale-import explanation that resolved the spiderweb case. Verified directly against the raw, unmounted PS3 files rather than trusting the mounted output alone: `obj/o6511.objbnd` and its `.objbnd.dcx` sibling (both exist on disc, decompress to byte-identical inner content) — the shipped game data for this object really does contain 0 triangles, confirmed at the source, not an extraction bug. Went looking for where the real geometry might come from instead and found `map/breakobj/*.breakobj` in the raw root: 20 files (one per map area), magic header `OBJB`, a completely undocumented FromSoft-specific format with no reader anywhere in `SoulsFormatsNEXT`. Working theory (not yet confirmed, needs the format reverse-engineered to verify): these dummy-only IDs are physics-only fracture anchors, and the actual visible debris at runtime is composed by the Havok destruction system from a small shared library of generic rubble/plank/pottery chunk meshes, driven by whatever `.breakobj` packs (placement, per-piece physics, which shared chunk to use) rather than stored per-object-ID — which would mean no per-file resolution rule, however clever, could ever recover a mesh that was never stored per-object in the first place. Scoped as real future work (see PLAN.md), not attempted further this session. (Chronologically predates the pivot below by about a day; kept here rather than reordered by topic.)
- **The `res://`-bypass pivot from the "Next avenue" entry above was actually implemented, hit two more real bugs on the way, and is now the shipped architecture — user-confirmed working, fast, and the best state this plugin has ever been in (2026-07-24).** User approved prototyping it after seeing the sketch; four real problems surfaced during actual implementation, each found by testing against real data rather than assumed fixed once the code compiled.
  - **Split `FlverSceneImporter.cs` into `FlverModelBuilder.cs` (all the parsing/mesh/material/texture-resolution logic, zero Godot-import dependency) and `FlverLoader.cs` (a thin manual cache/loader, driven only by explicit user action).** Mechanical move at first — every method, every doc comment, moved verbatim — verified with a real `dotnet build` and a direct `FlverLoader.Instantiate()` smoke test against real `c0100`/`o1000` data before touching anything else.
  - **First real bug hit immediately, unrelated to the pivot: a hard engine crash on `m2011b1.flver`, `Index p_index = 57343 is out of bounds (count = 1312)` in `core/templates/local_vector.h`, signal 4.** Surfaced because opening the editor after any C# rebuild triggers a full corpus reimport-queue reconcile (a pre-existing Godot behavior, not something this session's code caused). Isolated properly, not just assumed pre-existing: reverted to the pre-split `FlverSceneImporter.cs`, rebuilt, reproduced the *identical* crash on the *identical* file, confirming it predates this session's changes entirely and isn't one of the three already-documented FLVER0 vertex-layout crashers (`m9999b0`/`m9900`/`o9996`). **Not root-caused, not chased further this session** — flagged to the user and left as a known open issue; it's now moot for the reimport-queue path specifically (that path no longer exists after the pivot below), but would still be worth investigating if `m2011b1.flver` is ever loaded through `FlverLoader` and something similar reproduces there.
  - **Second bug: a prototype custom `Tree`-based dock (`mounted_browser_dock.gd`) for browsing/selecting `.flver` files never worked reliably in the live editor, and the root cause was never fully pinned down.** First symptom (nothing under the arrows) traced to a real, fixable issue — the dock's tree was built once at `_ready()`, before extraction had populated `mounted/`, and never refreshed; added a "Refresh" button and a real empty-state message, verified by directly simulating the Tree's `item_collapsed` handler against the user's actual 134-folder `mounted/chr/` corpus (confirmed correct: all 134 rows populated). Despite that fix — plus a full plugin toggle-off/on and editor restart — the user's live arrows still failed to expand at all, described precisely as "pressing the arrow just makes that arrow disappear, everything else remaining unchanged." That specific symptom is consistent with the handler running and finding zero children, but a direct function-call simulation of the same operation against the same real data succeeded every time — meaning the discrepancy was specifically between a programmatic call and Godot's real UI click→signal path for a `Tree` inside a re-dockable/floatable panel, never actually isolated. **Abandoned rather than root-caused** — the user suggested reusing the toolbar's existing `EditorFileDialog` pattern (already proven working in this exact file for "Mount...") instead of continuing to debug a custom `Tree` blind; `mounted_browser_dock.gd` was deleted outright rather than kept as inert/broken code. If a future custom in-editor `Tree` browser is ever attempted again, treat "arrows not expanding despite correct handler logic in isolation" as a known-unresolved failure mode, not a new mystery to re-diagnose from scratch.
  - **Third bug, and the one that actually mattered: extraction still triggered the exact crash-prone reimport cycle the whole pivot existed to avoid.** `archstone.gd`'s `_on_extract_complete` unconditionally called `EditorInterface.get_resource_filesystem().scan()` after every extraction, and `FlverSceneImporter` was still registered via `add_scene_format_importer_plugin` — so every Mount+Import still queued every newly-extracted `.flver` through Godot's threaded reimport dispatch, identically to before the pivot. User caught this directly ("running the mount + import still directly extracts all of the files into res://, meaning the full import cycle... we wanted to avoid"). Fixed by removing both: no more `EditorSceneFormatImporter` registration for `.flver` at all, and no more `.scan()` calls after extraction or Clear. `FlverSceneImporter.cs` itself was deleted (fully superseded, unreachable once unregistered — kept as dead code would only invite someone re-registering it without the context of why it was removed). Once nothing dispatched `FlverModelBuilder`'s methods across multiple threads anymore, `_dirTextureCache`/`_decodedTextureCache` were downgraded from `ConcurrentDictionary`/`Interlocked`/lock back to plain `Dictionary`/`long`, removing complexity that had no remaining justification rather than leaving it as harmless-but-misleading ceremony.
  - **Fourth bug, found by the user after everything else finally worked end-to-end: loaded models had correct node structure (root + mesh-instance child, correct surface/material data) but were completely invisible.** Root cause: `ImporterMesh`/`ImporterMeshInstance3D` are import-*pipeline*-only placeholder types with no rendering of their own — Godot's own scene-import post-processing (LOD/shadow generation, the same step CLAUDE.md's old "Mesh nodes must use ImporterMesh/ImporterMeshInstance3D" convention was actually describing) is what normally converts them into a real `ArrayMesh`/`MeshInstance3D` when saving an imported scene. `FlverLoader` never goes through that pipeline at all, by design, so nothing was ever performing that conversion — the CLAUDE.md convention that used to be correct (when a real importer pipeline existed) became actively wrong once the pipeline was removed, and nobody had re-derived it from first principles for the new architecture. Confirmed `ImporterMesh.GetMesh()` exists as exactly the bridge API needed (`m.has_method("get_mesh")` → true, verified headlessly before writing the fix) and is the same call Godot's own importer uses internally. Fixed in `FlverLoader.Instantiate()`: build via `FlverModelBuilder.BuildMesh()` (still returns `ImporterMesh`, a fine mesh-authoring API on its own), then `GetMesh()` → real `ArrayMesh`, wrapped in a real `MeshInstance3D`. Verified against real `c0100` data: `MeshInstance3D`/`ArrayMesh`, 4 surfaces, materials intact. **User-confirmed after this fix**: loading works, and is "blazingly fast... probably the fastest and most efficient this plugin has been since it was a basic single-model obj converter stub" — i.e., not just "works," but a real, felt improvement over the old reimport-queue-driven workflow, consistent with the architectural argument for the pivot in the first place (no queue dispatch/thread-pool overhead, no cache-artifact bookkeeping, straight from disk bytes to a rendered mesh on demand).
  - **Process lesson, same shape as several entries above in this file**: two of the four bugs (the extraction-still-triggering-reimport issue and the invisible-mesh issue) were found only because the user actually exercised the feature end-to-end in the real editor after each round of "should be fixed now" — neither was caught by `dotnet build` succeeding, nor by a headless smoke test that only checked node/surface *structure* rather than whether the model would actually render. Structural correctness (right node types, right child counts, right surface data) is necessary but not sufficient evidence a Godot-facing feature works; headless verification here still can't catch "compiles and has the right data shape but is invisible for an unrelated rendering-pipeline reason," the same category `--headless`'s dummy rendering driver already can't catch for shader compile errors (see CLAUDE.md's "Build & verify").

## C#/Godot interop gotchas worth remembering

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
- Running the GUI editor and any other headless `godot-mono` process against the same
  project simultaneously causes contention/hangs. Only ever run one Godot process against
  this project at a time. **Confirmed as a real OOM incident, not just a hang, on
  2026-07-21**: running a full headless reimport (from the since-removed reimport
  pipeline) while the GUI editor was also open froze the whole system for 1-2 minutes,
  then the kernel OOM-killer killed the GUI editor process (`journalctl -p err`: `Out of
  memory: Killed process ... (godot.linuxbsd.) total-vm:93855004kB,
  anon-rss:14996300kB`) — the headless process itself survived and finished normally.
  Close the GUI editor before starting any other headless `godot-mono` run (e.g. the
  `--rendering-driver` shader-compile check in CLAUDE.md's "Build & verify").

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
- Re-registering an `EditorSceneFormatImporter` for `.flver` to try to fix a reimport-concurrency problem "properly" instead of avoiding the pipeline entirely — already tried at length (self-throttling, engine-source-level investigation of every available hook) and confirmed Godot exposes no lever for this at all; see "Real bugs found and fixed" above for the summary and why the fix ended up architectural instead.
- Applying `DrSwizzler`'s PS3 deswizzle unconditionally to every texture format
  (matching Soulstruct's format-generic approach, including DXT1/BC1) to fix the
  lightmap block-noise artifact — tested directly via a before/after visual dump on a
  real lightmap texture; makes it *worse*, not better. `Headerizer.Headerize`'s own doc
  comment already says DeS/DS1 don't use PS3 swizzling for most formats; only keep the
  original narrow uncompressed-only condition. See context.md's "Lightmap/drawparam
  system, part 9".
- A custom in-editor `Tree`-based file browser for selecting `.flver` files to load
  (`mounted_browser_dock.gd`, deleted 2026-07-24). The handler logic was verified
  correct in isolation (direct function-call simulation against real `mounted/chr/`
  data populated all rows correctly), but the real Tree's expand arrows never worked
  in the live editor — survived a plugin toggle and a full editor restart, symptom
  specifically "arrow disappears, nothing else happens." Root cause never isolated
  (see "Real bugs found and fixed"'s pivot entry for the full story); abandoned in
  favor of reusing the already-proven `EditorFileDialog` pattern instead of
  continuing to debug a from-scratch `Tree` blind. If a custom `Tree` browser is ever
  wanted again, budget time to actually diagnose the click→signal path in a real
  windowed session (not headless) before assuming the same approach will work this
  time.

## Deferred by design, not forgotten

See CLAUDE.md's "Known deferred work" for the current list (animation/skeleton import,
the metallic-channel ceiling, LOD z-fighting). The
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
