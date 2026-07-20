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
- **Missing terrain ground-blend layer (hard seams between grass/dirt/stone).** User
  noticed `m0002b1.flver` (Shrine of Storms island/stairs) had hard mesh-boundary seams
  between ground textures where the real game blends them smoothly. Root cause: a large
  family of FromSoft map materials carry a *second* texture set (`g_Diffuse_2` plus
  optionally `g_Specular_2`/`g_Bumpmap_2`) meant to be blended with the first via FLVER's
  `VertexColor` layout semantic (SoulsFormats' own doc comment: "data used for blending,
  alpha, etc.") — confirmed non-constant (varies 0.0-1.0, R=G=B) rather than dead data.
  The importer only ever read the first set, so every such mesh rendered as one flat
  texture. Fixed by branching `GetOrBuildMaterial` to build a `ShaderMaterial`
  (`addons/archstone/terrain_blend.gdshader`) instead of `StandardMaterial3D` when a
  material qualifies, mixing both layers by the FLVER vertex-color red channel.
  - **First attempt was wrong, corrected same session.** Initially gated on all three of
    `g_Diffuse_2`+`g_Specular_2`+`g_Bumpmap_2` being present together, based on sampling
    only `m0002b1.flver` where that held for every blend material found. User then tested
    Boletarian Palace (`m02_00_00_00`, pieces `m0100b0`-`m0500b0`) via screenshots
    comparing an emulator run against the Godot editor, and found ground patches
    rendering as flat dark rock instead of blending with grass, with hard edges — e.g.
    `m0100b0.flver` material 2 ("緑"/"green") and material 3 ("緑ディテール"/"green
    detail") both have `g_Diffuse_2`+`g_Bumpmap_2` (real grass/cliff texture pairs) but
    *no* specular anywhere, so the `g_Specular_2`-required gate wrongly excluded them,
    falling back to whichever texture happened to be the primary `g_Diffuse` (rock, on
    the "detail" variant) with the actual grass layer silently ignored in `g_Diffuse_2`.
  - **Corrected gate, confirmed across the whole dataset.** Full-dataset scan of all 963
    `g_Diffuse_2` materials in the game found the real discriminator is FromSoft's own MTD
    bracket tag: 912 have `[M]` or `[ML]` in the MTD filename (the blend-shader family
    marker) with bumpmap and specular each independently optional per layer (174 have
    neither, 110 have bumpmap only, 56 have specular only, 566 have both — the pattern the
    first attempt happened to sample). The other exactly 51 have no bracket tag at all and
    are 100% the unrelated "ghost"/dissolve character materials (`Cs_Ghost_*`,
    `Cs_ShadowMan`, etc. — MTDs like `Cs_Ghost_Param_Wander.mtd`), a clean, total split
    with zero overlap either direction. New gate: `MTD.Contains("[M]") ||
    MTD.Contains("[ML]")`, combined with requiring `g_Diffuse_2` itself present as a
    belt-and-suspenders check. Also fixed the shader's specular fallback hint from
    `hint_default_white` to `hint_default_black` — with specular now optional per layer,
    an absent texture must default to zero metallic contribution (matching
    `StandardMaterial3D`'s own default when `g_Specular` is absent), not full white/opaque
    metallic. All 913 blend-material meshes under the new gate were confirmed to still
    have the required ≥2 UV channels and vertex-color data before rebuilding.
  - **Verification status: resolved.** A subsequent full reimport did complete all the way
    through; the mid-batch crash flake and the one-off `m3008b1.flver`
    `NullReferenceException` seen previously turned out to just be the already-documented
    threaded-reimport flakiness, not a real regression from the gate fix — error count
    landed back on the same 16 known files.
- **Terrain blend used the wrong vertex-color channel (blending looked like "one texture
  always wins, no smooth transition").** After the gate fix above, the shader itself
  (`terrain_blend.gdshader`) was mixing `diffuse1`/`diffuse2` by `COLOR.r` (equivalently
  `.g`/`.b` — R=G=B always, per the confirmation two entries up). User reported the blend
  still didn't look smooth on real terrain pieces: one texture dominates a whole surface,
  transitions look abrupt rather than gradual. Root cause, confirmed with data rather than
  guessed: `VertexColor.R` is **not** the blend weight — it's a separate, mostly-1.0 signal
  (vertex-baked AO/shadow: near 1.0 almost everywhere, dipping only in localized spots like
  crevices/corners) that happens to also satisfy "non-constant, varies 0-1" (the check used
  to confirm it two entries up — a necessary but not sufficient test, since AO data is
  non-constant too). The real blend weight is `VertexColor.A`. Confirmed two independent
  ways with a scratch console tool (`SoulsFormatsNEXT`-referencing, not committed) reading
  raw FLVER0 vertex data directly, bypassing the importer entirely:
  1. **Game-wide statistics** across all 907 blend meshes in `mounted/map` (516k vertices):
     R and A are essentially uncorrelated (Pearson corr = 0.07). R's per-mesh mean is >0.9
     for 713/907 meshes (78.6%) — classic "no occlusion" baseline with rare dips, not a
     deliberately painted texture-mix weight. A has far higher variance (std 0.471 vs R's
     0.226) and 608/907 meshes have a mean in the 0.3-0.7 band, consistent with genuinely
     mixed painted regions.
  2. **Per-triangle transition coverage on the known test file** (`m0002b1.flver`, Shrine
     of Storms island/stairs — the file the original hard-seam bug report was against):
     for every one of its 12 blend materials, the fraction of triangles whose 3 vertices
     span a blend-value range >0.2 (i.e. an actual transition triangle, not flat) is
     dramatically higher using A than using R — e.g. 88.1% vs 3.5% on one material, 74.4%
     vs 66.7% on the narrowest gap, never the other way around across all 12 materials.
     Using R meant the shader's `mix()` picked essentially the same texture across nearly
     an entire surface, only dipping toward the other layer in the sparse AO-crevice spots
     — exactly the reported symptom.
  Fixed by changing `terrain_blend.gdshader`'s `float blend = COLOR.r` to `COLOR.a`. No
  importer C# change and no new/changed shader uniforms, so no reimport was needed —
  spot-checked by reimporting `m0002b1.flver` alone (clean, no new errors) rather than
  re-running the full ~1776-file baseline.
  - **Follow-up: R is not vertex AO — tried, visually disproven, reverted.** R's
    AO-shaped statistics (mostly ~1.0, non-constant, uncorrelated with A) were originally
    read as "vertex-baked AO/shadow, darkens near crevices" and wired in as
    `ALBEDO *= COLOR.r`. User reported patchy, sharp-edged light/dark borders on
    Boletarian Palace terrain (concretely reproducible on `m0201b0.flver`,
    `mounted/map/m02_00_00_00/`) persisting even with the sun disabled — confirming it was
    the multiply itself, not a lighting interaction — and confirmed absent from the real
    game. Direct inspection of that file's vertex data settled it: **R only ever takes
    exactly two discrete values per mesh — e.g. `1.000` or `0.549` (≈140/255), nothing in
    between** (checked via full frequency histogram, not just min/max). It's a binary
    per-vertex flag, not a shading gradient, so multiplying it into albedo produces a hard
    on/off split with no falloff wherever it flips — exactly the reported symptom. What R
    actually encodes is unknown (a material sub-variant switch, a wetness/puddle mask, a
    surface-type tag for footstep sounds — no way to tell from geometry data alone).
    Reverted `ALBEDO *= COLOR.r`; R is read (as part of the full `VertexColor` struct) but
    deliberately left unused in the shader again. Lesson: "matches an AO-like statistical
    shape" (skewed near 1, non-constant) is necessary but nowhere near sufficient to
    confirm a channel is a shading gradient — always check whether values are actually
    continuous before wiring something into a multiply, not just their range/skew.
  - Also verified while investigating: UV channel 1 (used for `diffuse2`'s own UV, distinct
    from UV channel 0's `diffuse1`) tiles at a wide range nearly matching UV channel 0's
    range (e.g. U -1.696..12.018 vs -1.678..11.991 on one mesh) — i.e. two independent but
    similar diffuse unwraps — while UV channel 2 stays confined to ~[0,1], matching the
    already-documented lightmap-UV description in CLAUDE.md's deferred-work section. This
    confirms the existing UV0→diffuse1/UV1→diffuse2/UV2→(future)lightmap assignment in
    `FlverSceneImporter.cs` is correct and was **not** part of this bug.
- **Water surfaces added (new material family, not a bug fix).** A material-landscape
  survey (prompted by the terrain-blend work above) found `g_Envmap` used on exactly 58
  materials across 45-56 distinct map files (58 material *instances*; 56 distinct file
  *paths* since the same basename recurs across areas — the two counts aren't the same
  thing), 100% of them the `DS_Water_Env`/`DS_Water_Env_Skin` MTD family (`g_BlendMode`
  `Water`/`WaterWave`) and nowhere else in the game's 8889 materials — confirmed via a
  scratch console tool reading raw FLVER0 material data directly. These materials carry
  only `g_Bumpmap`+`g_Envmap`, no diffuse texture at all, so before this they imported
  with no albedo (blank/undefined-looking). Investigated properly before writing a shader:
  - **`g_Envmap` is not a cubemap.** Decoded a real one (`m05_env_00.tga`, 128×128) to PNG
    with a scratch tool (`Headerizer` + `Pfim`, same path `DecodeTexture` already uses) and
    viewed it directly — a small flat "impression" image of a rocky scene, not 6 cubemap
    faces or an equirectangular panorama. Handled in-shader as a matcap-style lookup
    (reflected view-space normal's XY as UV) — the standard cheap PS3-era substitute for
    real reflections, and the only technique that makes sense for a single flat image like
    this. `g_Bumpmap` decoded to an ordinary tangent-space wave normal map, as expected.
  - **Real per-material tuning only exists in the actual `.mtd` file, not FLVER0's own
    data.** WitchyBND has, at some point since this project started, unpacked
    `mtd.mtdbnd.dcx` into `mounted/mtd/` (306 real, parseable `.mtd` files) — this predates
    the last few sessions' work but wasn't noticed until the material survey; the
    "Alpha/cutout materials" bug entry above is stale on this point (kept as-is, corrected
    inline, since it explains why that fix used a filename heuristic at the time).
    `SoulsFormatsNEXT` already has a working `MTD.cs` reader. Dumped full params for 3
    different water `.mtd` files and found real, materially-different per-material tuning
    (e.g. `g_TexScroll_0` of `0.01`/`0.03`/`-0.1`, `g_FresnelPow` of `3`/`4`/`7`,
    `g_WaterColor` differing per body of water) — confirmed this wasn't boilerplate/default
    data worth hardcoding once. `FlverSceneImporter.cs` now indexes every real `.mtd` by
    filename once at construction (`_mtdIndex`, eager field init like `_blendShader` —
    306 small files is cheap enough to not bother with lazy init) and reads the real file
    per water material for `water.gdshader`'s uniforms (tile scale/scroll/blend per bump
    layer, water tint, Fresnel curve), falling back to the shader's own generic-water
    uniform defaults if a material's `.mtd` can't be resolved.
  - **Gate is texture-based, not filename-based, and confirmed necessary to be so.**
    Some MTDs with "water" in the filename (e.g. `m99_water01[dsb]_alp.mtd`,
    `a05_water02[dn]_add.mtd`) are actually plain alpha-blended splash effects on the
    ordinary single-layer shader family — a filename-substring check would have wrongly
    special-cased them. `g_Envmap`'s presence is the actual, exclusive, confirmed signal.
  - **A real, generalizable Godot/C# gotcha found while wiring this up** — see the interop
    gotchas section below (`SetShaderParameter` + `source_color`-hinted `vec3` uniforms).
  - **Verification status: mechanically confirmed, not yet visually confirmed.** All 56
    water-bearing files reimport with no errors (only the pre-existing, unrelated
    `m9900.flver` crash showed up, from an incidental wider reimport after rebuilding the
    assembly). Confirmed via headless script that real, distinct per-material MTD data
    (different `water_color`/`fresnel_pow`/`tile_scale` per file) is actually reaching the
    shader, not just defaults. **Not yet looked at in the editor** — the shader's actual
    look (matcap reflection quality, wave animation, screen-space refraction) hasn't been
    checked against the real game the way terrain blending was.
  - **Follow-up: all water looked the same regardless of material (`g_WaterColor`'s alpha
    was silently discarded).** User confirmed most water looked good (Boletaria's moat,
    fairly accurate) but Valley of Defilement's swamp — the large piece past Leechmonger —
    looked like "the same regular water effect," "crudely stretched," not murky/opaque
    like real swamp mud. Investigated by comparing the two materials' real `.mtd` data
    directly rather than guessing: Boletaria (`a01_water[we].mtd`) has `g_ReflectBand=0.1`,
    `g_RefractBand=0.05`; Defilement (`a04_water[we].mtd`) has `g_ReflectBand=0` (**no**
    reflection at all) and `g_RefractBand=0.15`. Both were already confirmed reaching the
    shader correctly (headless check on the actual imported material, not just the source
    `.mtd`), ruling out an `_mtdIndex` resolution failure. Also ruled out a UV/world-scale
    mismatch theory (mesh too big → texture stretched): checked world-units-per-UV-unit on
    both meshes and it's nearly identical (~19-20) despite Defilement's mesh being roughly
    the same size as Boletaria's (Boletaria's is if anything *larger*, 1477×902 vs
    1187×1236) — so mesh size wasn't it either. Root cause: with `g_ReflectBand=0`,
    Defilement's water should show almost nothing *but* the tinted, distorted
    screen-refraction of whatever's behind it — but the shader had no way to make that
    refraction *opaque*, so it always looked like see-through water no matter the tint,
    and the refraction-UV distortion (`N.xy * refract_band`) visibly smearing a large
    expanse of background across a big flat swamp reads exactly as "crudely stretched."
    `g_WaterColor` is actually a Float4, not a Float3 — its 4th component had been read
    into `GetMtdColor3` (which only keeps the first 3) and never used. Checked it across
    all 17 water `.mtd` files game-wide: Defilement's alpha is `0.784` vs. Boletaria's
    `0.588` — *higher*, not lower, exactly the direction needed for "more opaque swamp,
    less opaque moat," confirming it's real opacity data, not noise. Added a `water_alpha`
    uniform (read via a small new `GetMtdFloat4Alpha` helper) and blend the refraction
    toward a flat, fully-opaque `water_color` by that amount before mixing in reflection —
    `vec3 surface = mix(refraction, water_color, water_alpha); ALBEDO = mix(surface,
    reflection, fresnel);`. Re-verified both files reimport clean and the new uniform
    reads back correctly per-material (0.784 vs 0.588) via the same headless-inspection
    method as before. **Still not yet visually confirmed** — this fix is a well-evidenced
    hypothesis from real per-material data, not something rendered and looked at.
  - **Follow-up 2: coloring fixed, but still "very reflective/watery" not muddy, and
    "none of the water has ever been see-through" (both before and after the alpha fix).**
    Checked whether unused real MTD params explained it before guessing at a fix:
    - `g_SpecularPower` is a **constant 100 across every single water material in the
      game** (checked all 17 real `[we].mtd` files) - not usable as a per-material
      roughness signal, ruling out the obvious "derive roughness from specular power"
      approach before it was tried.
    - The actual cause: `ROUGHNESS` was a fixed `0.05` constant for every water material,
      completely separate from the manual `reflect_band`-scaled matcap reflection term
      above it. Godot's own automatic PBR specular/Fresnel response runs regardless of that
      manual term, and real dielectric Fresnel reflectance climbs toward ~100% at grazing
      angles *regardless of roughness* - what a low roughness does is concentrate that into
      a sharp mirror-like glint instead of spreading/dimming it, which is what actually
      reads as "reflective" vs "matte" to the eye. So swamp water stayed glossy-mirror-like
      at grazing angles even with the manual reflection correctly zeroed. Fix: derive
      `ROUGHNESS`/`SPECULAR` from `g_ReflectBand` too (confirmed to only take `0` or `0.1`
      across every water material in the game, so a `clamp(reflect_band/0.1, 0, 1)` mix
      covers the full real range, not an arbitrary guess) - `0.6`/`0.05` (rough, dim) at
      `ReflectBand=0` vs `0.02`/`0.5` (glossy) at `0.1`.
    - For "never see-through": found `g_WaterFadeBegin` (real data, varies `0.3`-`1.0`
      game-wide, previously read nowhere) - name and value range strongly suggest a
      shallow/deep transition distance, the mechanism that would let a shoreline show the
      bottom while open water stays opaque, which nothing in the shader modeled at all
      (`water_alpha` was applied uniformly across the whole surface regardless of depth).
      Verified the exact Godot 4 API for this via web search rather than guessing
      (`INV_PROJECTION_MATRIX`, `hint_depth_texture`, `SCREEN_UV`, `VERTEX`'s view-space
      meaning) before writing it - see sources below. Added a `depth_tex` sampler,
      reconstructed the view-space position of whatever's behind each water pixel, and
      scaled `water_alpha` by `clamp(depth_diff / water_fade_begin, 0, 1)` so shallow water
      fades toward fully see-through and only reaches the real opacity once genuinely deep.
    - Re-verified: both files still reimport clean, `water_fade_begin`/`reflect_band`/
      `water_alpha` all read back correctly per-material. **Could not verify the shader
      actually compiles or renders correctly** - headless Godot here uses a dummy render
      driver with no real GPU, so GLSL is never actually compiled in this environment; the
      C#/data-pipeline side is confirmed, the shader math is not, until looked at in the
      editor. Sources used for the Godot 4 depth-reconstruction API:
      [Godot spatial shader reference](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/spatial_shader.html),
      [Godot Forum: view-space position from depth texture](https://forum.godotengine.org/t/shader-to-get-view-space-position-from-depth-texture-inv-projection-matrix/101389).
  - **Follow-up 3: two real bugs, confirmed with actual screenshots this time (first time
    this shader has been looked at instead of only data-checked).** User provided two
    screenshots. Boletaria's moat: nearly black and flat, needed the sun boosted to 3x and
    sky to 2x just to see it was water at all. Valley of Defilement: coloring/depth-fade
    correctly subtle now, but covered in chaotic diagonal streaks reading as "still very
    reflective."
    - **Streaking root cause:** `refract_band` (real range `0.05`-`0.8`) was being used
      directly as a `SCREEN_UV` offset for the refraction sample - but `SCREEN_UV` is 0-1,
      so even `0.15` is 15% of the whole screen. On a large, near-edge-on water surface,
      adjacent pixels sample wildly different, unrelated parts of the rendered scene,
      producing exactly this kind of smear. Fixed by scaling the offset down and hard-
      clamping it (`refraction_scale = 0.03`, clamped to ±0.03) - **this specific
      magnitude is an uncalibrated guess**, unlike everything else in this shader; the
      real MTD data has no visibility into what unit/space the original shader actually
      applied `g_RefractBand` in.
    - **Black/flat root cause (the more fundamental bug):** the whole hand-computed
      reflection+refraction+Fresnel result was written to `ALBEDO`. Godot's PBR model
      shifts energy from diffuse to specular as `ROUGHNESS` drops (physically correct -
      smooth surfaces have little diffuse response), and the previous round's fix had
      pushed `ROUGHNESS` down to `0.02` for reflective water. With no real sky/reflection
      probe set up in the scene for a near-mirror surface to actually reflect, and `ALBEDO`
      only visible via the (now nearly-zero) diffuse term, the computed color became
      almost entirely invisible regardless of scene light intensity - matching "had to
      crank the sun 3x and still barely see it" exactly. This was always going to be a
      structural mismatch: this shader was never meant to be a physically-based surface
      plugged into Godot's lighting model - it's a self-contained, hand-tuned final color,
      the same way the original PS3 `DS_Water_Env.spx` almost certainly worked internally
      (its own Fresnel/reflect/refract math baked straight to output, not routed through a
      generic BRDF). Fixed by writing the result to `EMISSION` instead of `ALBEDO`
      (`ALBEDO` set to `vec3(0)`), which is always visible regardless of scene lighting or
      roughness - `ROUGHNESS`/`SPECULAR` (still `g_ReflectBand`-derived) now only add a
      small *extra* real specular sun-glint on top, not the primary reflective effect.
    - Both fixes are shader-only (no new/changed uniforms, no C# change) - live-reloads,
      no reimport needed. Sanity-checked both test scenes still load without error;
      **cannot verify the actual rendered look from here** (still no real GPU in this
      headless environment) - waiting on a fresh look/screenshot.
  - **Follow-up 4: streaking diagnosis corrected by the user, wave tiling fixed; brightness
    still open, waiting on real data instead of a third guess.** User corrected the
    "refraction sampling garbage" theory from follow-up 3 directly: the streaks are
    real reflections *of the scrolling wave/bump pattern itself* - i.e. the wave tiling
    really is too large-scale ("stretched"), confirming the very first (later abandoned)
    hypothesis from when water was first implemented. Fixed by adding a flat
    `wave_detail_scale` multiplier (`12.0`) applied to the whole bump-sample coordinate
    (tiling and scroll together, so animation speed stays proportional to the new denser
    tile size) - **this constant is an uncalibrated guess**, same caveat as
    `refraction_scale`.
    - Separately, user provided two real DeS gameplay screenshots of the actual Boletaria
      moat (character on the bridge looking down, sun/lighting as the genuine game
      renders it, not our editor's sun) to use as a brightness reference, and confirmed
      the moat is still "way too dark" even under ambient-only lighting in the editor
      (specifically testing with the sun off, since PS3 Demon's Souls had no global
      dynamic sun - lighting there is more likely close to what our `EMISSION`-driven,
      lighting-independent approach should already produce regardless of scene light).
      Sampled the actual reference screenshots directly (`magick ... -resize 1x1!`) rather
      than eyeballing: the moat reads at roughly 10-11% sRGB brightness in a decently-lit
      strip (darker, ~2-4%, in shadow) - dim but not black. **Did not guess at a fix here** -
      have no current-Godot screenshot to sample against for a real before/after
      comparison, and two guesses already turned out wrong this session (the AO-as-
      shading-multiply idea, and the "refraction sampling garbage" streaking theory) - a
      third blind guess on an even less certain question (exact linear-light magnitude
      of a multi-term shader blend) isn't worth it when one more screenshot would give an
      actual number to hit instead.
  - **Follow-up 5: Defilement confirmed close (17% Godot vs 18-22% real, sampled directly
    from screenshots both times), Boletaria still dark - and the project has no
    WorldEnvironment/tonemap configuration anywhere, which is very likely why.** User
    corrected the streaking diagnosis again: it *is* real reflection of the scrolling wave
    pattern (matching follow-up 4's fix), just now reading as "intense and squashed" at
    `wave_detail_scale=12` - may need dialing back, not yet done since it wasn't the
    question asked this round. Dumped and compared the full real `.mtd` data for both
    materials side by side (not just the subset checked before): Boletaria's
    `g_FresnelColor` is pure white `(1,1,1)` vs Defilement's dim teal `(0.196,0.353,
    0.329)`, `g_FresnelPow` is a steeper `4` vs `3`, alpha is lower (`0.588` vs `0.784`,
    meaning more weight on refraction, less on the flat tint). These are real,
    already-correctly-used differences, but hand-calculating the shader's own math with
    them suggests Boletaria should land around ~35-40% linear-ish brightness, nowhere
    near the observed ~7% sRGB - meaning the per-material data isn't the (whole) story.
    Searched the project for any `WorldEnvironment`/tonemap/exposure configuration
    (`.tscn` files, `.tres` resources, `project.godot` rendering settings, `.godot/`
    auto-generated defaults) and **found none at all** - the user's sun/sky adjustments
    are almost certainly the 3D editor viewport's own built-in ad-hoc preview toggles
    (View > Environment), not a real configured environment resource, meaning there is no
    controlled tonemap/exposure baseline anywhere and the two materials are being judged
    under whatever the editor's default preview happens to do - which especially punishes
    Boletaria's screen-refraction-heavy math (41% of its "surface" weight, vs
    Defilement's dominant flat murky tint) since refraction inherits however dim the
    *rest* of the ambient-only scene happens to render. This directly extends the same
    root cause the user already suspected for Defilement's fog/effects ("more accurate
    once we get proper environments set up") to Boletaria too, just manifesting as
    tonemap/exposure crush instead of fog occlusion. Not fixed - this isn't a shader bug
    to patch, it's a missing testing prerequisite; offered to build a minimal, clearly-
    scoped test `WorldEnvironment` (not the real Phase 2 environment system) so future
    material comparisons have a controlled, reproducible baseline instead of the editor's
    opaque default preview.
  - **Noticed in passing, not yet acted on:** `a06_lava[we].mtd` exists — lava uses this
    same `DS_Water_Env`-family shader (`g_Envmap`-gated), so it's currently importing
    through `water.gdshader` (a reflective/refractive liquid look), which is almost
    certainly wrong for lava (should be opaque, glowing/emissive). Not fixed — flagging
    for whenever lava specifically comes up, since it wasn't part of what was reported.
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

## Environment specifics (this machine)

- No .NET 8 runtime is installed system-wide (Arch dropped the package) — Godot 4.7's
  own `GodotTools.dll` (the "build solutions" / project-generation feature) hardcodes a
  `net8.0` target with `rollForward: LatestMinor`, which does **not** cross major
  versions, so it can't fall back to the installed 9/10 runtimes. `GodotPlugins.dll`
  (the main C# script host used at editor runtime) uses `rollForward: LatestMajor`
  instead and *can* fall back — which is why the editor itself could still run C# before
  the runtime was fixed, while `--build-solutions` specifically hung.
- Fix: installed net8 via Microsoft's `dotnet-install.sh` into `~/.dotnet-godot`, then
  symlinked the system SDK and other runtimes into that *same* root
  (`~/.dotnet-godot/sdk/10.0.110`, `~/.dotnet-godot/shared/Microsoft.NETCore.App/9.0.18`
  and `10.0.10`) so `DOTNET_ROOT=~/.dotnet-godot` sees everything at once. Pointing
  `DOTNET_ROOT` at an isolated net8-only install instead hides the system SDK entirely
  and produces a *different* failure ("`.NET Sdk not found`") — don't do that, merge via
  symlinks into one root instead.
- **Superseded 2026-07-20:** the "Arch dropped the package" premise above was wrong —
  `dotnet-sdk-8.0` is in Arch's official `extra` repo and installs cleanly
  (`pacman -S dotnet-sdk-8.0`), landing in `/usr/share/dotnet` alongside the 9.0/10.0
  SDKs Arch's plain `dotnet-sdk` package pulls in, no merging needed. Confirmed by
  installing it and running both `dotnet build Soulbrandt.csproj` and
  `godot-mono --headless --editor --path . --import` with `DOTNET_ROOT` unset and `PATH`
  restricted to system dirs only — build succeeded, import ran through the full
  `update_scripts_classes`/editor-layout sequence with none of the old failure modes
  (no hang, no ".NET Sdk not found"). `~/.dotnet-godot` and every `DOTNET_ROOT=` prefix
  are removed from CLAUDE.md/CONTRIBUTING.md as of this note; if this regresses on a
  future Arch update, the merged-root workaround above is the fallback to rebuild.
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
