# context.md

Session history, lessons learned, and ruled-out dead ends for this project. `docs/ARCHITECTURE.md`
covers current architecture and how to work in the repo; this file covers *why* things
are the way they are and what's already been tried and rejected, so it doesn't get
re-litigated. Read both — docs/ARCHITECTURE.md for the current state of truth, this for the journey.

## The arc so far

Started as a proof-of-concept console app (`desflver_test/`, since deleted) that used
WitchyBND + the vendored SoulsFormatsNEXT library to write `.obj`/`.mtl`/`.png` for
Godot's generic Wavefront OBJ importer to pick up — fought constantly (path leakage,
raw DDS bytes, a Godot OBJ/PNG-import race bug) before being replaced entirely by a
native `EditorSceneFormatImporter` (itself later replaced by the current architecture,
see below). Two facts from this era still matter: **no Godot build has C# support except
the official godotengine.org `.NET` build** — confirmed the hard way on Redot (none at
all) and Steam's Godot (also none) — and **a forked/patched vendored submodule's local
commits are a live risk until actually pushed to a real remote**, not just a tidiness
concern: `SoulsFormatsNEXT`'s two local patches sat as uncommitted working-tree edits in
a plain clone for a while, meaning any `git pull`/`checkout` in that directory would have
silently wiped them with no recovery path, before being committed to a real fork and
pulled back in as a proper submodule. The project was also renamed `Boletaria` →
`Soulbrandt` along the way and made public under GPLv3 (required by `SoulsFormatsNEXT`'s
own license, not an arbitrary choice).

## Real bugs found and fixed (root causes, not symptoms)

Each of these produced a plausible-looking but wrong intermediate theory before the real
cause was confirmed — noted so the same wrong turn isn't re-taken.

- **Mip-chain slicing.** Pfim's decoded `Data` buffer contains the *entire* mip chain
  concatenated (base level first, ~4/3 the size of `width*height*4`), not just the base
  level. Must slice to `width*height*4` bytes before handing to
  `Image.CreateFromData`. Confirmed via `pfImage.MipMaps[]`, which exposes exact
  per-level offsets — trust that over guessing from total buffer length.
- **Empty imported scenes (two separate bugs, same symptom) — both rules now standing
  conventions, see docs/ARCHITECTURE.md's Architecture section.** A returned
  `Node3D`+`MeshInstance3D`+`ArrayMesh` loaded with 0 children, no error: (1) Godot's
  LOD/shadow-mesh post-processing only touches `ImporterMesh`/`ImporterMeshInstance3D`,
  not the runtime `ArrayMesh`/`MeshInstance3D`; (2) `PackedScene.pack()` only serializes
  descendants whose `.Owner` is set to the packed root — a plain `AddChild()` is
  invisible to it. Diagnostic trick worth keeping: `ResourceSaver.save(packed,
  "res://debug.tscn")` dumps the resource as human-readable text, much faster than
  guessing from binary `.scn` byte size.
- **Triangle winding and UV V-flip — both from the same source, both already standing
  conventions (docs/ARCHITECTURE.md).** Godot uses clockwise front-face winding (opposite
  Blender/OBJ's CCW) and needs no V-flip on UVs; `Program.cs` (the old `desflver_test/`
  OBJ-export tool, since deleted) applied a winding swap and a `1 - v.UVs[0].Y` flip to
  satisfy Blender/OBJ conventions that don't apply to the native path — porting either
  into the importer was wrong and produced inside-out geometry / misaligned textures.
  Confirmed for winding by comparing face normals against FLVER's own stored vertex
  normals across 16k+ triangles (99%+ match unswapped); confirmed for UVs by drawing a
  wireframe onto the raw decoded texture and visually matching a shield's ring/boss
  pattern with no flip.
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
  sibling; map piece FLVERs do not. Real textures instead live in a shared per-map-area
  folder (e.g. `mounted/map/m03/`). Superseded by `CandidateDirs` — see the
  "Texture-resolution redundancy... unified" entry below for the current architecture.
- **Race condition in the area-texture cache — moot now, structurally.** Symptom was
  nondeterministic textures across repeated reimports: Godot's bulk reimport dispatched
  across multiple worker threads against the same importer instance, and a plain
  `Dictionary` isn't thread-safe. Fixed then with `ConcurrentDictionary.GetOrAdd`; can't
  recur today regardless, since `FlverLoader` is single-threaded and no multi-threaded
  dispatch exists anymore (docs/ARCHITECTURE.md). Worth remembering the diagnostic
  signature though: inconsistent, non-reproducible-the-same-way symptoms are the tell of
  a threading bug, not a logic bug (which fails the same way every time).
- **Case-sensitive texture name lookup.** A material referenced
  `m03_01_wall_cliff_00` (lowercase) while the actual TPF entry was named
  `m03_01_Wall_Cliff_00` (mixed case) — the game engine treats these as equivalent, a
  default C# `Dictionary` does not. Both texture-name dictionaries now use
  `StringComparer.OrdinalIgnoreCase`.
- **Alpha/cutout materials.** FLVER0's parsed `Material` exposes no blend-mode flag at
  all (verified by reading the actual `SoulsFormats.FLVER0.Material` source — just
  `Name`/`MTD`/`Textures`/`Layouts`), so the MTD *path string* was the only available
  signal at the time (`.mtd` files themselves weren't extracted yet). `_Edge` →
  `AlphaScissor` (hard cutout, confirmed against grass — a clean near-binary alpha mask
  matching the sprite silhouette exactly), `_Alp` → `Alpha` blend (soft transparency,
  confirmed against a hair material). Same naming convention used across FromSoft's other
  Souls titles, not a Demon's-Souls-only guess. (`g_BlendMode`, the real MTD enum that
  could eventually replace this filename heuristic, is tracked as its own item in
  docs/ARCHITECTURE.md's Known Deferred Work — not duplicated here.)
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
    (UV0→diffuse1, UV1→diffuse2, UV2 reserved for the future lightmap, per docs/ARCHITECTURE.md's
    deferred-work section) was double-checked and confirmed correct, unrelated to
    this bug.
- **Water surfaces added (new material family, not a bug fix) — now fully described as
  current behavior in docs/ARCHITECTURE.md's water section; kept here only for what
  isn't duplicated there.** `g_Envmap` uniquely gates this family across all 8889
  materials (confirmed via direct scan, not a filename heuristic — some "water"-named
  MTDs are actually plain alpha-blended splash effects on the ordinary shader). One real
  bug worth remembering: `g_WaterColor`'s alpha (`water_alpha`) was initially silently
  discarded, which is what actually separates murky/opaque water from clear reflective
  water — it's a Float4, not Float3. Visual-accuracy tuning is deliberately paused, not
  abandoned, pending real `WorldEnvironment`/lighting work (see docs/PLAN.md).
  - **A real, generalizable Godot/C# gotcha found while wiring this up** — see the
    interop gotchas section below (`SetShaderParameter` + `source_color`-hinted
    `vec3` uniforms).
- **Cross-category texture reuse (a corpse pile rendering fully white, `m4020b0.flver` —
  early version, superseded by `CandidateDirs`, see the "Texture-resolution
  redundancy... unified" entry below for the current architecture).** Root cause: the
  material referenced a *character* model's own texture set (`c2000`) reused wholesale
  for a decorative map prop — neither the same-basename-sibling nor map-area-bucket
  rules covered this. Confirmed via a game-wide scan of all 8792 `g_Diffuse` refs as a
  real, recurring pattern (4+ map files reuse `chr`, ~14 reuse `obj`/`parts`, ~1183
  flagged mismatches sampled and confirmed genuinely orphaned/debug data, not an importer
  bug). Fixed then by `GetForeignCategoryTextures`, since replaced. Along the way,
  confirmed two real container-layout facts still true today: `obj`/`parts` containers
  keep a real nested `tex/` subfolder (unlike `chr`'s flat layout), and a folder can hold
  more than one real `.tpf` side by side where the entry needed isn't in the one matching
  the folder's own name — both are why `CandidateDirs`/`GetMergedTextures` merge every
  `.tpf` in a resolved directory rather than opening one guessed file.
- **Map-piece naming investigation (curiosity-driven, not a bug report - findings scoped
  for the future "map assembler" work, see docs/PLAN.md).** User noticed `m2020b0l1`,
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
    docs/PLAN.md's map-assembler entry for the exact bug). Worked around it for this one
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
    to place these correctly. See docs/PLAN.md's map-assembler entry.
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
  - **A silent reimport hang also hit during this work's own verification — same
    threaded-reimport-host flakiness class documented elsewhere in this file, moot now
    that no reimport queue exists to hang.** 49+ minutes of zero output on an ordinary map
    file, all threads sleeping; killed and re-ran clean. Recovery approach (kill and
    re-run) was the correct call at the time but doesn't apply to anything in the current
    architecture.

- **Non-visual game-logic data investigation (curiosity-driven, not a bug report —
  headline findings already condensed into docs/PLAN.md's Phase 2 section; kept here for
  what isn't duplicated there).** Confirmed directly against the real mounted dump, not
  assumed from general Souls-series knowledge, that none of Phase 2's eventual
  gameplay-logic needs would require any DRM circumvention: `EBOOT.BIN` (Sony's encrypted
  SELF container) is correctly off-limits, but `script/` (plain unencrypted Lua, same
  approach as DS1), `param`/`paramdef` (DeS ships its own paramdefs, unlike later titles
  needing the community `Paramdex` project), and per-character `.tae`/`.esd` files are
  all real, already-parseable, disc-native data. One naming trap worth remembering: DeS's
  poise-like mechanic is internally called **Super Armor**, not Poise — there's no
  DS1-style "Poise stat reduces hitstun" field in any table checked, so porting fan
  terminology directly would be wrong. Two real gaps remain: TAE event `Type` IDs have no
  friendly name without an external template (unbuilt for DeS), and precise i-frame/
  hyper-armor frame counts aren't publicly documented anywhere for this specific title
  (unlike Dark Souls 3) — likely needs direct frame-by-frame observation against the real
  game in RPCS3, not extraction.

- **Texture-resolution redundancy found and unified (`GetAreaTextures` deleted, not a bug fix on its own but the audit that led to real fixes below).** User asked directly whether the accumulated per-case texture-resolution methods (area-bucket, foreign-category, and whatever obj/parts-specific fix was about to be added next) were actually clean and non-redundant, or "smaller solutions taped together." Checked rather than assumed: scanned all 26,656 real texture refs across the mounted corpus and found `GetAreaTextures` never once succeeded where `GetForeignCategoryTextures` didn't already resolve the same reference identically, plus 123 cases where only the latter succeeded — the two methods weren't complementary, one was strictly subsumed by the other. Replaced both (plus the about-to-be-added obj/parts sib/tex fix) with a single ordered `CandidateDirs` chain and one shared `_dirTextureCache`/`GetMergedTextures` primitive — see docs/ARCHITECTURE.md's "Texture resolution" section for the resulting architecture. Real fixes that came out of building it properly instead of stacking one more method on top:
  - **obj/parts sib/tex split** (universal to all 777 obj + 309 parts models, not an edge case): their containers put the `.flver` in its own `sib/` folder with `tex/` as a sibling, unlike chr's flat same-folder layout — confirmed via directory scan, not assumed from chr's convention. This alone had been silently leaving every obj/parts model with no own-container texture lookup at all.
  - **Nexus archstones (`o1000`-`o1050`) share one body texture via a copy-pasted, never-updated ref** — all six only have their `g_Lightmap` slot honestly labeled; `g_Diffuse`/`g_Specular`/`g_Bumpmap` still point at the `o1000` template's own folder. Fixed by `SiblingMapAreaDir`: if any other slot on the same material resolves under `mounted/map`, try that folder for this slot too.
  - **`o3120`/`o3129` (Boletaria wall pieces) mislabel every ref on the material, including the lightmap, under their own obj category** — recoverable only because the texture filenames themselves still start with the real `m03_` map-area prefix. Fixed by `MapPrefixDir` (regex `^(m\d\d)_` against the filename, independent of whatever the reference path's category segment claims).
  - **Zero-byte `.tpf` crash** (`o3104`, `o0050`, `o7999`) — surfaced only once `CandidateDirs` became the first code path to actually open these obj models' own `tex/` folders. `System.IO.EndOfStreamException` inside `TPF.Read` aborted the *entire* model's import, not just the one bad texture. Confirmed as real shipped data (genuinely 0-byte files), not an extraction bug. Fixed with a per-file try/catch in `LoadDirTextures` (`GD.PushWarning` + skip).
  - **Missing FLVER0 `ParamName`** — a separate concern from location, not folded into `CandidateDirs`: some texture entries have a real path and a real texture but no slot type at all (`SoulsFormats` leaves `ParamName` null when `typeOffset == 0`). Recovered via `InferMissingParamNames`, positionally matching untyped entries against `Dif`/`Spc`/`Bmp`/`Lit`/`Dcl` tokens in the MTD's own bracket tag. Validated 100% (83/83 real affected materials, 44 files across every category including the `c9983`/`c9981` Ghost/Wanderer phantom-gear template) by scanning the corpus before writing the fix, not after.
  - Deliberately **not** generalized into a fifth `CandidateDirs` rule: `o6510_1` (a destructible-prop variant) needs a bottle/vase texture (`m02_obj_00.tga`) that's byte-identical across 6 *other* obj models' own folders, not in its own container and not in any map bucket. Unlike rules 3-4 above, there's no cheap signal for *which* sibling obj folders to search — flagged as needing more real examples before it's worth the scan-cost/collision-risk tradeoff of a general "search other obj folders" rule.
- **`obj/` "empty scene" investigation (mostly confirmed-correct-as-is, not a bug — see docs/ARCHITECTURE.md's "Known deferred work" for the stable conclusions).** User flagged large contiguous `obj/` ID ranges opening as scenes with zero nodes at all. Scanned all 1068 mounted `obj/` FLVER0 files directly (SoulsFormats, bypassing the importer) rather than guessing from a couple of samples: 1006 have real meshes, 62 don't. Of those 62, 57 are genuine dummy-only attach/anchor markers (0 meshes, 1-2 `Dummy` points, confirmed correct as imported — nothing for any resolution rule to recover) and 5 are 288-byte bare stubs with nothing at all (allocated-but-never-built IDs). One sub-cluster initially miscategorized as "empty" (`o6760`/`o6761`/`o6770`/`o6771`/`o6780`/`o6781`) turned out to be real: tiny 4-16 vert alpha-blended spiderweb decal quads (`map\M[D]_Alp.mtd`, `m06_spiderweb_00.tga`, resolved correctly from the model's own `tex/` folder) — confirmed via direct headless `load()` producing a correct `Mesh` node, `StandardMaterial3D`, `Alpha` transparency, and a resolved `albedo_texture`. The user's original "blank" observation on this specific sub-cluster predated the sib/tex + `CandidateDirs` + `ParamName`-inference fixes above landing and being reimported — not a separate bug.
- **FLVER0 vertex-layout crash fixed — two separate crash sites sharing one root cause,
  not one bug. Closed and confirmed corpus-wide (zero errors across the full ~3411-file
  mounted corpus); the surviving fallback logic is documented as current behavior in
  docs/ARCHITECTURE.md's Architecture section and docs/PLAN.md's Done list.** Root cause:
  some FLVER0 meshes' own `BufferLayout` genuinely has no member for Normal/UV/
  VertexColor at all (`m9999b0.flver`/`m9900.flver`/`o9996.flver`) — not a parser bug.
  Needed two separate fixes, not one: the importer's own vertex-array loop indexed
  `v.Normals[0]`/`v.Colors[0]`/`v.UVs[0]` unconditionally (guarded on `.Count > 0` now,
  falling back to sane defaults), and one level deeper inside `SoulsFormatsNEXT` itself,
  `Mesh.Triangulate`'s `doCheckFlip` path crashed on the same missing-Normal-data meshes
  (the library exposes `doCheckFlip` as a caller-side opt-out for exactly this, so gating
  it at the call site was the correct fix, not passing `false` unconditionally — which
  would have silently broken winding-correctness on strip meshes that legitimately need
  the check).
- **Lightmap system added (new material capability, not a bug fix) — now fully described
  as current behavior in docs/ARCHITECTURE.md's Lightmaps section; kept here for what
  isn't duplicated there.** Confirmed via a manual probe (decoding a real diffuse/lightmap
  pair and viewing the composite) that the lightmap is a genuine per-mesh UV atlas bake,
  not noise or a misdecode, before committing to any shader architecture — the initial
  composite read very dark, which turned out to be the same missing-`WorldEnvironment`
  gap already found for water, not a math error. A corpus survey (7394/11646 materials
  have a lightmap) determined the shader split: three thin files instead of one
  runtime-switched shader, since `blend_mix`/`blend_add` are compile-time `render_mode`
  keywords in Godot, not a per-material property. Packing the lightmap's UV into
  `Mesh.ArrayType.Custom0` needed a real Godot API gap worked around via reflection —
  `SurfaceTool.CreateFromArrays` has no explicit array-format parameter in 4.7's C#
  binding; the only place to declare `Custom0`'s component format turned out to be
  `ImporterMesh.AddSurface`'s existing but previously-unused `flags` parameter.
- **Lightmap system, part 2 — nearly everything rendered white after the user's own
  reimport+in-editor test; two real shader-compile bugs (now fixed and documented as
  standing restrictions in docs/ARCHITECTURE.md) plus a much larger, now-moot
  import-cache corruption issue.** `--headless` never actually compiles GLSL, so two real
  `SHADER ERROR`s (assigning built-ins from a helper function; reading `CUSTOM0` outside
  `vertex()`) were invisible until a real Xvfb+opengl3 driver check caught them — both
  restrictions are now standing conventions, see docs/ARCHITECTURE.md's Lightmaps
  section. Separately, a resource-contention crash (several Xvfb+opengl3 processes
  running alongside the GUI editor, no OOM this time, just contention — a second data
  point for the same "never run concurrent Godot processes" rule already in
  docs/ARCHITECTURE.md) corrupted the import cache far beyond the shader bugs' own blast
  radius: 1395/3411 `.flver.import` files pointed at cache artifacts that were never
  actually written, since Godot trusted an `.import` file's recorded timestamps rather
  than checking whether its target actually existed — moot now, structurally, since no
  reimport queue exists to corrupt. One still-generally-useful lesson: a background
  `godot-mono --import` launched via a trailing shell `&` inside a single tool call
  reported "complete" almost instantly, while the actual process kept running detached
  and unmonitored — verify via `ps aux` directly, don't trust a quick tool-call return as
  proof a long background process finished.
- **Lightmap system, part 3 — darkness + wrong contrast root-caused and resolved (2026-07-22): missing sky in the scene's `WorldEnvironment`, not a shader/color-space bug.** Confirmed not an sRGB/gamma mismatch (an isolated Xvfb+opengl3 pixel-readback test found zero decode difference across `source_color`-hinted vs unhinted `ShaderMaterial` samplers and `StandardMaterial3D` for a runtime-built `ImageTexture`) and not a double-lighting/`ALBEDO`-vs-`IRRADIANCE` architecture issue either (never got far enough to need testing - see below). User added a real `WorldEnvironment` node to the test scene (previously only using the editor's own top-bar preview tweaks, which don't reflect actual scene lighting) and found removing the sky entirely, using flat ambient light instead, fixed ~75% of the darkness/contrast gap immediately - screenshot comparison (`Screenshot_20260722_003748.png` vs the real game's `Screenshot_20260722_004720.png`, both Boletarian Palace's main gate) is now close: same silhouette, stone tone, overcast mood. Remaining open items: geometry past a certain distance goes pure black with no sun/sky (ambient alone doesn't seem to reach it); brightness/color/contrast still needs finer manual tuning against reference footage. Both are environment/lighting-setup work, not importer code.
- **Lightmap system, part 4 — contrast/desaturation/artifact fix attempted three ways this session (2026-07-22), all reverted; root architecture identified but blocked on unparseable per-map data.** Follow-up to part 3: even with a real `WorldEnvironment` (sky removed, flat ambient) closing most of the gap, straight `diffuse * lightmap` still crushed shadows to pure black and blew out highlights — no ambient/tonemap setting could fix this since ambient is one linear multiplier over `ALBEDO` and can't lift a dark majority without blowing out a bright minority (confirmed via a real per-vertex-UV probe, not a blind texture-grid sample: large valid-UV surfaces on real Nexus meshes still sampled mean ~0.03-0.13 on the lightmap, 60-95% of texels under 0.05-0.20).
  - **Real DeS MTD data** (read via `SoulsFormats.MTD.Read()` on the lightmapped Nexus/Boletaria materials) confirmed these are tagged `g_LightingType = HemEnvDifSpc` (hemisphere + environment + diffuse + specular). `g_DiffuseMapColorPower`/`g_SpecularMapColorPower` (0.6/1.5) exist as real fields but are confirmed unused by the DSR shader source below — legacy from an older pipeline revision, not wired into anything. `g_DiffuseMapColor`/`g_SpecularMapColor` are neutral `(1,1,1)`, not a tint source.
  - **Community ground truth**: found `AltimorTASDK/dsr-shader-mods` on GitHub — decompiled HLSL source for Dark Souls Remastered (shares FromSoft's "FRPG" engine lineage with DeS; DS1/DSR has a far larger reverse-engineering community than DeS does), specifically `FRPG_FS_HemEnv.fx`, the exact shader family DeS's `HemEnvDifSpc` lighting type names. Caveat: DSR is a 2018 PBR rewrite, not a byte-exact match for the 2009 DeS shader, but it's the only concrete source-level ground truth found. Key finding: the real shader **never multiplies the lightmap against the base diffuse/ambient result**. It only scales a secondary environment/IBL reflection term (`envLightComponent = CalcEnvIBL(...) * lightmapColor.rgb`), while a separate, always-on two-color hemisphere-ambient term (`Mtl.DiffuseColor * CalcHemAmbient(Mtl.Normal)`, blending two colors by vertex-normal Y) is added afterward, completely untouched by the lightmap. The raw lightmap sample is also gamma-corrected before use (`pow(lightMapVal.rgb, gFC_DebugPointLightParams.z)`). The two hemisphere colors (`gFC_HemAmbCol_u`/`_d`) are external runtime constant registers (`FC_REG(c98)`/`c99`) — real per-map lighting data supplied at draw time, **not stored in FLVER0/MTD/TPF**, and not parseable by anything in this project currently. Same category of gap as `.breakobj` above.
  - **Attempt 1 (gamma + flat neutral floor)**: `lm_shaped = mix(vec3(lightmap_floor), vec3(1.0), pow(lm, gamma))`, `ALBEDO = diffuse * lm_shaped`. Fixed the crush/blowout convincingly (user-confirmed against RPCS3 reference screenshots for Nexus and Boletaria). Regression: user reported maps now looked "dull" — a per-channel probe confirmed both diffuse and lightmap textures are genuinely warm-toned (R>G>B) on real data, and the flat gray floor was diluting that real baked color specifically in shadow regions, not just adding brightness.
  - **Attempt 2 (hue-preserving reciprocal boost)**: `boost = lightmap_floor / luminance(lm)`, `lm_shaped = lm * boost`, scaling the lightmap's own color vector up by magnitude while preserving its hue instead of mixing toward gray. Regression: unbounded division by near-zero luminance (the lightmap's own near-black texels, per the probe stats above) blew up DXT block-compression quantization noise into large, visibly blocky purple/green/magenta patches (screenshot: `test.png`) — confirmed the mechanism, not guessed, since the artifacts are literally block-shaped (matches DXT's 4×4 block granularity) and concentrated exactly where luminance is smallest.
  - **Attempt 2b (same boost, clamped to 6x)**: bounding the multiplier didn't help — user reported "not much of a visible change at all." Root cause: since the lightmap's mean is ~0.03-0.13, nearly every texel's unclamped boost was already far past 6x, so nearly every texel was hitting the clamp ceiling anyway; clamping a value that's already saturated everywhere doesn't change the picture.
  - **Attempt 3 (additive floor tied to diffuse color, not lightmap color)**: `ALBEDO = diffuse * (lm + lightmap_floor)` — matching the real shader's actual structure (`DiffuseColor * HemAmbient`, added, not derived from the lightmap). Structurally can't blow up regardless of how close to zero `lm` gets, since there's no division left at all, and the floor's hue now comes from the real (non-noisy) diffuse texture instead of the lightmap. Verified compile-clean under a real (opengl3/x11) rendering driver. Regression: user still reported "obvious patches of green and purple/magenta," though "much better than before." **Since this is the one attempt with no division/reciprocal anywhere in the math, the persistence of color patches through a structurally different formula is itself a finding**: it points away from the compositing formula as the artifact's cause and toward the raw lightmap texture decode itself (DXT block-compression noise inherent to the near-black texels, exposed by *any* approach that brightens shadows at all, not specifically by a reciprocal/division) — not yet confirmed, not investigated this session.
  - **Reverted to the original straight `diffuse * lightmap` multiply** (both `lightmap_common.gdshaderinc`'s `lightmap_shade()` and `terrain_blend.gdshader`'s inline compositing) — none of the three attempts shipped. The lightmap system's plumbing (UV routing, `Custom0` packing, alpha/add shader variants) is untouched; only the compositing formula was touched and reverted.
  - Real fix is blocked on the same kind of gap as `.breakobj`: no currently-parseable DeS file format carries per-map hemisphere-ambient color data. Next avenues, not yet attempted: (1) probe the raw decoded lightmap texture itself for a decode bug or confirm the noise is genuinely inherent to the source DXT data, independent of any compositing math; (2) pursue the RPCS3 real-shader-dump path (`showDebugTab=true` → `shaderlog/`) for byte-exact 2009 DeS shader instructions instead of DSR's approximation; (3) treat this as blocked on the bigger WorldEnvironment/GI-level ambient system already scoped in `docs/PLAN.md`, since a real per-normal hemisphere ambient term is itself a lighting-system feature, not a material-shader one.
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
- **Lightmap/drawparam system, parts 6-8 — `LIGHT_BANK`/`FOG_BANK` confirmed against real
  data and wired into the shader; now fully described as current behavior in
  docs/ARCHITECTURE.md's Lightmaps section, kept here for what isn't duplicated there
  (2026-07-22).** Row IDs/names line up 1:1 between `LIGHT_BANK` and `FOG_BANK`
  (confirmed, not inferred) — real evidence `LightID`/`FogID` share one row-ID space
  picking a matched light+fog preset per map situation. **Known, accepted risk carried
  into the shipped fix:** with `default_lightbank.param` row 0's `ambient_up ==
  ambient_down`, the hemisphere blend collapses to a near-flat floor for this
  global-fallback row, structurally close to an earlier reverted flat-floor attempt —
  likely still shows the same purple/green block-noise artifact (part 5/9's still-open
  question), since that artifact traces to the raw lightmap texture decode, not the
  compositing formula; kept anyway as real forward progress on the data/formula side, per
  user decision.
  - **Real in-editor testing found a distinct, worse regression on Boletaria (large
    hard-edged dark patches, no gradient) — traced and fixed, now noted as resolved in
    docs/ARCHITECTURE.md's Known Deferred Work.** Every affected surface used
    `terrain_blend.gdshader` specifically, which hadn't gotten the hemisphere/env formula
    yet (still on the old plain multiply) — porting the same formula there (sharing the
    math via `hemisphere_ambient.gdshaderinc`) fixed it, user-confirmed. Caught only by
    actually testing in the real editor, the same category of gap headless verification
    structurally can't cover.
- **Lightmap/drawparam system, part 9 — part 5's "PS3 block-swizzle, never undone" suspect for the DXT1 block-noise artifact tested directly and disproven (2026-07-23).** An external comparison against Soulstruct (`github.com/Grimrukh/soulstruct` + `soulstruct-blender`, a public Blender-addon FLVER/TPF importer with mature Demon's Souls support) found its PS3 deswizzle (`deswizzle_dds_bytes_ps3`) is format-generic — driven by DXGI block size, explicitly including `BC1_UNORM`/DXT1 — unlike this project's `Headerizer.ReadPS3Images`, which only deswizzles a short list of uncompressed formats (`R8G8B8A8_UNORM`, format 9/16/26) and skips every compressed format including DXT1. Read strongly as corroboration of part 5's suspicion.
  - **Implemented and built**: changed `ReadPS3Images` to call `DrSwizzler.Deswizzler.PS3Deswizzle` unconditionally for every PS3 texture rather than gating on format. Confirmed via `ilspycmd`-decompiling the `DrSwizzler` NuGet package directly (no source available otherwise) that `PS3Deswizzle`'s own block-size derivation (`IsPixelFormatCompressed` → block size 4 for any BCn DXGI format, 1 otherwise) already handles compressed formats correctly in isolation — the gate really was just in this project's calling code, not a downstream library limitation.
  - **Disproven by direct before/after evidence, not just re-inspection.** Dumped the Attribute Nexus archstone `o1000`'s resolved `g_Lightmap` texture to PNG by calling `FlverSceneImporter._import_scene()` directly (bypassing Godot's import cache entirely, so no reimport/cache-invalidation cycle needed for the comparison) — once with the original code, once with the format-generic deswizzle fix, same file, same run session. **Result was the opposite of the hypothesis**: the pre-fix texture is a coherent, recognizable image; the post-fix (deswizzled) texture is the *same* hard-edged checkerboard/block-scrambled mess the artifact reports describe. Applying PS3 deswizzle to this texture doesn't fix corruption, it *causes* it.
  - **Root cause of the wrong lead: a pre-existing doc comment in the exact function touched was overlooked before making the change.** `Headerizer.Headerize`'s own XML doc already says: *"By default, we'll assume no swizzling, PC type. Bear in mind Demon's Souls and Dark Souls 1 do NOT use PS3 swizzling and should be assigned 'PC'!"* — this project had already empirically settled, at some earlier point not otherwise documented in this file, that DeS mostly doesn't need PS3 deswizzling at all, and the original narrow uncompressed-only condition was deliberate tuning for this specific game, not an oversight matching Soulstruct's more generic implementation. Soulstruct's own approach may be correct for the games/textures it primarily targets (or may have the same issue for DeS specifically and nobody's checked against real screenshots) — it doesn't transfer here regardless.
  - **Reverted.** `SoulsFormatsNEXT` submodule working tree confirmed clean (`git status` empty, back to `d1de279`) after `git stash`/rebuild-compare/`git stash pop`/revert-edit/rebuild. Part 5's block-noise artifact remains **not** root-caused — this was the only concrete lead anyone had toward it, and it's now ruled out rather than confirmed. Next avenues are the two part-5 already named (decompile Pfim's real read order, or reverse-engineer the true PS3 compressed-block-swizzle pattern from scratch) — both bigger in scope than this session attempted.
  - **Process lesson**: read every doc comment/existing guard on the exact function being touched before trusting an external comparison's applicability, even a well-evidenced one — the contradicting comment was sitting in the same function this session edited, and would have caught the wrong direction before any build/test cycle was spent on it.
- **Lightmap/drawparam system, part 10 — the purple/green/magenta block-noise artifact (parts 4/5/9) closed as not-a-bug, confirmed against the real game (2026-07-26).** After part 9 ruled out the PS3 deswizzle lead without finding a replacement one, the user separately played real Demon's Souls and examined the Nexus geometry directly in-game: the same faint green/magenta artifacts are visible there too, at the same subtle intensity now seen in the editor after the hemisphere/env formula fix. Since the unmodified original game shows the same texel-level noise, it isn't an importer-introduced decode bug at all — either an intentional stylistic choice (aids blending) or an original-game imperfection too minor to matter, either way nothing to root-cause further. No longer listed as open work in docs/ARCHITECTURE.md.
- **Nexus VFX gaps investigated (2026-07-26): two real, separate root causes found for map-piece "glow"/"animation" materials looking flatter than the real game, both scoped as future work rather than fixed this session.**
  - **`m3051b0`'s sun-ray quads (the Nexus's large center statue) render with correct alpha but no animation and no soft edge falloff, unlike the real game's "lively, moving" rays.** Root-caused, not guessed: the material (`A05_vollight[Dn]_Add.mtd`, same "vollight"/"vollight_dust" family confirmed present under `area02`/`area03`/`area05`/`area99` too, not Nexus-only) carries real nonzero `g_TexScrollType=1`/`g_TexScroll_0=(0.05,-0.02)` data and its own description literally translates to "no light (diffuse only) / additive / scroll" — genuine authored UV-scroll animation, currently only wired for water materials (`BuildWaterMaterial`'s `tex_scroll_0/1/2` uniforms) and silently dropped for everything else, including this one (`BuildStandardMaterial` has no scroll path at all). The "soft/fuzzy edge" half is a separate, likely-unfixable-without-more-infrastructure gap: additive-blended bright edges normally get that look from bloom, and this project has no `WorldEnvironment`/glow anywhere (see the emission finding below, which hit the exact same missing-calibration wall). Not implemented — scoped as "generalize UV-scroll to non-water materials" alongside the map-assembler's planned `WorldEnvironment` work, not a standalone fix.
  - **`m3030b0`'s magic-square runes (`MagicS_00`-`03`) and `m9501b0`'s sky dome (`M_Sky_light[Dn].mtd`) both stay dim/dark in low light despite real `g_DiffuseMapColorPower` values (4 and 1.5) meant to make them glow — tried an Emission-based fix, reverted after real rendering proved it fundamentally broken in this project's current state, not tunable.** First confirmed the runes have *no* `g_Lightmap` at all (`A01[D]_Alp.mtd`, a plain diffuse+alpha material) — ruling out the user's own initial theory that lightmap-driven shadow darkening was responsible. Then found `ResolveMtdShading()`'s existing tint computation (`pow(g_DiffuseMapColor, power)`) is a mathematical no-op for this exact case: `g_DiffuseMapColor` is pure white here (confirmed via direct MTD dump), and `white^power == white` for any power, so the "real exponent, not ignored" comment already in that code doesn't actually reach render for the majority-white-tint case the comment itself says covers 568/584 materials.
    - **Attempted fix**: fed the diffuse texture into `StandardMaterial3D.Emission` (gated on `power > 1.0`) so these materials self-illuminate independent of scene light. First version scaled `EmissionEnergyMultiplier` by the raw `power` value — user reported pure white output, no gold. Sampling the real texture directly showed why: its brightest texels already hit `R=1.0, G=1.0, B≈0.8` before any multiplier, so `×4`/`×1.5` clipped every channel.
    - **Capped the multiplier to 1.0 instead (texture's own native brightness, no boost) — user reported zero visible change, still pure white.** Rendered it for real (`xvfb-run -a godot-mono --rendering-driver opengl3 --display-driver x11`, editor confirmed closed first) rather than continuing to reason from texture math alone. A flat 2D dump of the raw texture (bypassing all 3D rendering) confirmed the source art is genuinely, cleanly gold — the "correct" ground truth was never in question. But the actual 3D render of the same texture through `Emission` came out grayscale (`R=G=B`), not just bright — real bug, not perception. **Reproduced with zero FLVER involvement**: a synthetic 4×4 solid-red (`1.0, 0.2, 0.2`) `ImageTexture` fed into `EmissionTexture` with `EmissionEnergyMultiplier=1.0` rendered as flat white; sweeping energy from 0→1 showed the falloff isn't a sane linear multiply at all (clips/desaturates well before 1.0). Setting `Emission` to a plain color directly (no texture) rendered correctly, isolating the bug to texture-driven emission specifically.
    - **Root cause: this project has no `WorldEnvironment`/`CameraAttributes` anywhere** (already the reason water/lightmap visual tuning is paused, see the "diffuse * lightmap" entry in docs/ARCHITECTURE.md) — without one, exposure/tonemap runs on Godot's own uncalibrated internal default, and `Emission` rendering apparently needs that calibration far more than `Albedo` does to produce sane output. No constant tunable from `FlverModelBuilder` fixes this — exposure control lives on the camera/environment, which this addon doesn't create (the editor's preview camera is Godot's own, untouched by this plugin).
    - **Reverted.** `ResolveMtdShading()` back to its original `(Roughness, Tint)` two-tuple, `BuildStandardMaterial` back to no Emission path — verified via `git diff` against the pre-session baseline that only the explanatory comment survives, no behavioral change. Not tunable without inventing another placeholder number, the exact category of fix this project's docs already warn against (see the lightmap "three shader-only approximations... reverted" entry above) — genuinely blocked on the same `WorldEnvironment` gap already scoped for the map-assembler phase (docs/PLAN.md), not a smaller fixable issue.
- **MSB-parsing next-step scouting (2026-07-26): confirmed the `MSB1`-can't-read-DeS blocker in `docs/PLAN.md` was stale, and confirmed `LightID`/`FogID` genuinely vary per-part rather than being a near-constant per map.** Before picking a next slice of map-assembler work, checked whether the "needs an `MSB1` hand-patch first" framing still held now that `MsbLoader`/`MSBD.Read()` has been shipping real map-piece placement since 2026-07-24 — it didn't: DeS uses `MSBD` (its own reader), not `MSB1`; the `Treasure.ReadTypeData` assert bug lives only in `MSB1/EventParam.cs`, DS1-specific code this project never calls. `docs/PLAN.md` and `docs/ARCHITECTURE.md` both had this framed as still-blocking; corrected in both. Then added a temp `MsbLoader.DebugDumpPartsSummary(msbPath)` static method (removed after, standard temp-debug-method pattern) to check two things a headless dump can answer directly: (1) does `MSBD.Read()` — which parses `Events` too, not just `Parts` — actually complete cleanly end to end on real files, and (2) does `LightID`/`FogID` (confirmed on the common `Part` base via `ReadEntityData`, not just `MapPiece` — every part type carries them) meaningfully vary within one map, an open question `docs/PLAN.md` had explicitly flagged as unknown. Ran it against `m01_00_00_00` (Nexus), `m03_00_00_00`, `m03_01_00_00`, and `m08_00_00_00`: all four read cleanly with zero assert failures anywhere in the file. `LightID` varies real and wide within a single populated map — Nexus's `MapPieces` alone span `{0,1,3,11,255}`, `Objects` span `{0,1,2,4,6,11,58}`, `Collisions` a visibly disjoint `{0,58-63}` (collision geometry apparently pulls from its own row block, not the same range as visual geometry) — while `m03_00_00_00` (a near-empty 4-`MapPiece` shell, likely a stub the real Boletaria sub-blocks route through rather than a populated map on its own) sits flat at `LightID=0` throughout, confirming per-map variety is real too, not just per-part. `255` only ever showed up on `MapPieces`, never `Objects`/`Collisions`, in every map checked — a plausible "unset, use default" sentinel given this format's usual `0xFF`-as-unset convention, but not confirmed; check this once real row-resolution is wired rather than assuming. No code changes kept from this pass — investigative only, folded into `docs/PLAN.md`'s Phase 1 item 4 and `docs/ARCHITECTURE.md`'s lightmap "Known deferred work" entry.
- **The old `EditorSceneFormatImporter` pipeline (deleted 2026-07-24, see the pivot entry below) absorbed several rounds of real stability work before being replaced outright rather than repaired — compressed to a summary here since none of the mechanism it describes still exists.** Confirmed problems along the way, in order: an `o0103.flver` hang reproducible only under Godot's threaded bulk-reimport dispatch, never in a direct call (pointed at the importer's per-instance texture caches accumulating unboundedly across a whole reimport run); a per-process-restart batching mitigation for that same growth that needed a second fix after an early version wiped the whole corpus's cache up front and silently defeated its own restart on the very first test; a texture-cache self-eviction fix (`MaybeEvictDecodedTextures`, budget-based off `GC.GetGCMemoryInfo()` so it scales to weaker hardware automatically — still present in `FlverModelBuilder.cs` today, this part of the fix outlived the importer it was written for) that did bound peak memory, verified with both the real budget and an artificially tiny test budget; and finally a `SemaphoreSlim` concurrency gate that, after confirming directly against Godot's own engine source that no config-level or per-plugin lever exists to control reimport concurrency at all (`EditorSceneFormatImporter` has no `_can_import_threaded()` hook; `threading/worker_pool/max_threads` is hardcoded-ignored for the editor process), still didn't fix the underlying hang → `systemd-coredump` CPU spike → crash cycle. That last dead end — Godot gives a plugin no control over its own reimport concurrency — is what actually motivated the architecture pivot below, not another patch attempt.
- **Next avenue, prototyped in concept via a real reference implementation (2026-07-23), that led directly to the pivot below.** Bypassing `res://`'s import system entirely — confirmed viable, not just theorized, by reading a peer developer's own project (thanks Nox). Their pattern has **no `EditorSceneFormatImporter`/`EditorImportPlugin` anywhere at all** — a plain `@tool extends Node3D` with a manual button click, calling straight into ordinary `RefCounted` classes for parsing/caching, confirmed to be the same code path their real shipped game uses at runtime, not just an editor convenience. Their per-model cache is a plain in-memory `Dictionary` (never needs `ConcurrentDictionary` — nothing forces concurrent access once Godot's reimport queue is out of the picture) with no on-disk cache of parsed assets at all, just a re-parse per load. Confirmed the architecture direction was real and workable, not speculative — see the pivot entry below for what actually got built from it.
- **Destructible-prop debris cluster (`o6511`-`o6602`) — investigated, root-caused as genuinely out of importer scope for now, not fixed.** Distinct from the dummy-only markers above: after a full reimport and editor restart, user confirmed this specific cluster was *still* blank, ruling out the stale-import explanation that resolved the spiderweb case. Verified directly against the raw, unmounted PS3 files rather than trusting the mounted output alone: `obj/o6511.objbnd` and its `.objbnd.dcx` sibling (both exist on disc, decompress to byte-identical inner content) — the shipped game data for this object really does contain 0 triangles, confirmed at the source, not an extraction bug. Went looking for where the real geometry might come from instead and found `map/breakobj/*.breakobj` in the raw root: 20 files (one per map area), magic header `OBJB`, a completely undocumented FromSoft-specific format with no reader anywhere in `SoulsFormatsNEXT`. Working theory (not yet confirmed, needs the format reverse-engineered to verify): these dummy-only IDs are physics-only fracture anchors, and the actual visible debris at runtime is composed by the Havok destruction system from a small shared library of generic rubble/plank/pottery chunk meshes, driven by whatever `.breakobj` packs (placement, per-piece physics, which shared chunk to use) rather than stored per-object-ID — which would mean no per-file resolution rule, however clever, could ever recover a mesh that was never stored per-object in the first place. Scoped as real future work (see docs/PLAN.md), not attempted further this session. (Chronologically predates the pivot below by about a day; kept here rather than reordered by topic.)
- **The `res://`-bypass pivot from the "Next avenue" entry above was actually implemented, hit two more real bugs on the way, and is now the shipped architecture — user-confirmed working, fast, and the best state this plugin has ever been in (2026-07-24).** User approved prototyping it after seeing the sketch; four real problems surfaced during actual implementation, each found by testing against real data rather than assumed fixed once the code compiled.
  - **Split `FlverSceneImporter.cs` into `FlverModelBuilder.cs` (all the parsing/mesh/material/texture-resolution logic, zero Godot-import dependency) and `FlverLoader.cs` (a thin manual cache/loader, driven only by explicit user action).** Mechanical move at first — every method, every doc comment, moved verbatim — verified with a real `dotnet build` and a direct `FlverLoader.Instantiate()` smoke test against real `c0100`/`o1000` data before touching anything else.
  - **First real bug hit immediately, unrelated to the pivot: a hard engine crash on `m2011b1.flver`, `Index p_index = 57343 is out of bounds (count = 1312)` in `core/templates/local_vector.h`, signal 4.** Surfaced because opening the editor after any C# rebuild triggers a full corpus reimport-queue reconcile (a pre-existing Godot behavior, not something this session's code caused). Isolated properly, not just assumed pre-existing: reverted to the pre-split `FlverSceneImporter.cs`, rebuilt, reproduced the *identical* crash on the *identical* file, confirming it predates this session's changes entirely and isn't one of the three already-documented FLVER0 vertex-layout crashers (`m9999b0`/`m9900`/`o9996`). **Not root-caused, not chased further this session** — flagged to the user and left as a known open issue; it's now moot for the reimport-queue path specifically (that path no longer exists after the pivot below), but would still be worth investigating if `m2011b1.flver` is ever loaded through `FlverLoader` and something similar reproduces there.
  - **Second bug: a prototype custom `Tree`-based dock (`mounted_browser_dock.gd`) for browsing/selecting `.flver` files never worked reliably in the live editor, and the root cause was never fully pinned down.** First symptom (nothing under the arrows) traced to a real, fixable issue — the dock's tree was built once at `_ready()`, before extraction had populated `mounted/`, and never refreshed; added a "Refresh" button and a real empty-state message, verified by directly simulating the Tree's `item_collapsed` handler against the user's actual 134-folder `mounted/chr/` corpus (confirmed correct: all 134 rows populated). Despite that fix — plus a full plugin toggle-off/on and editor restart — the user's live arrows still failed to expand at all, described precisely as "pressing the arrow just makes that arrow disappear, everything else remaining unchanged." That specific symptom is consistent with the handler running and finding zero children, but a direct function-call simulation of the same operation against the same real data succeeded every time — meaning the discrepancy was specifically between a programmatic call and Godot's real UI click→signal path for a `Tree` inside a re-dockable/floatable panel, never actually isolated. **Abandoned rather than root-caused** — the user suggested reusing the toolbar's existing `EditorFileDialog` pattern (already proven working in this exact file for "Mount...") instead of continuing to debug a custom `Tree` blind; `mounted_browser_dock.gd` was deleted outright rather than kept as inert/broken code. If a future custom in-editor `Tree` browser is ever attempted again, treat "arrows not expanding despite correct handler logic in isolation" as a known-unresolved failure mode, not a new mystery to re-diagnose from scratch.
  - **Third bug, and the one that actually mattered: extraction still triggered the exact crash-prone reimport cycle the whole pivot existed to avoid.** `archstone.gd`'s `_on_extract_complete` unconditionally called `EditorInterface.get_resource_filesystem().scan()` after every extraction, and `FlverSceneImporter` was still registered via `add_scene_format_importer_plugin` — so every Mount+Import still queued every newly-extracted `.flver` through Godot's threaded reimport dispatch, identically to before the pivot. User caught this directly ("running the mount + import still directly extracts all of the files into res://, meaning the full import cycle... we wanted to avoid"). Fixed by removing both: no more `EditorSceneFormatImporter` registration for `.flver` at all, and no more `.scan()` calls after extraction or Clear. `FlverSceneImporter.cs` itself was deleted (fully superseded, unreachable once unregistered — kept as dead code would only invite someone re-registering it without the context of why it was removed). Once nothing dispatched `FlverModelBuilder`'s methods across multiple threads anymore, `_dirTextureCache`/`_decodedTextureCache` were downgraded from `ConcurrentDictionary`/`Interlocked`/lock back to plain `Dictionary`/`long`, removing complexity that had no remaining justification rather than leaving it as harmless-but-misleading ceremony.
  - **Fourth bug, found by the user after everything else finally worked end-to-end: loaded models had correct node structure (root + mesh-instance child, correct surface/material data) but were completely invisible.** Root cause: `ImporterMesh`/`ImporterMeshInstance3D` are import-*pipeline*-only placeholder types with no rendering of their own — Godot's own scene-import post-processing (LOD/shadow generation, the same step docs/ARCHITECTURE.md's old "Mesh nodes must use ImporterMesh/ImporterMeshInstance3D" convention was actually describing) is what normally converts them into a real `ArrayMesh`/`MeshInstance3D` when saving an imported scene. `FlverLoader` never goes through that pipeline at all, by design, so nothing was ever performing that conversion — the docs/ARCHITECTURE.md convention that used to be correct (when a real importer pipeline existed) became actively wrong once the pipeline was removed, and nobody had re-derived it from first principles for the new architecture. Confirmed `ImporterMesh.GetMesh()` exists as exactly the bridge API needed (`m.has_method("get_mesh")` → true, verified headlessly before writing the fix) and is the same call Godot's own importer uses internally. Fixed in `FlverLoader.Instantiate()`: build via `FlverModelBuilder.BuildMesh()` (still returns `ImporterMesh`, a fine mesh-authoring API on its own), then `GetMesh()` → real `ArrayMesh`, wrapped in a real `MeshInstance3D`. Verified against real `c0100` data: `MeshInstance3D`/`ArrayMesh`, 4 surfaces, materials intact. **User-confirmed after this fix**: loading works, and is "blazingly fast... probably the fastest and most efficient this plugin has been since it was a basic single-model obj converter stub" — i.e., not just "works," but a real, felt improvement over the old reimport-queue-driven workflow, consistent with the architectural argument for the pivot in the first place (no queue dispatch/thread-pool overhead, no cache-artifact bookkeeping, straight from disk bytes to a rendered mesh on demand).
  - **Process lesson, same shape as several entries above in this file**: two of the four bugs (the extraction-still-triggering-reimport issue and the invisible-mesh issue) were found only because the user actually exercised the feature end-to-end in the real editor after each round of "should be fixed now" — neither was caught by `dotnet build` succeeding, nor by a headless smoke test that only checked node/surface *structure* rather than whether the model would actually render. Structural correctness (right node types, right child counts, right surface data) is necessary but not sufficient evidence a Godot-facing feature works; headless verification here still can't catch "compiles and has the right data shape but is invisible for an unrelated rendering-pipeline reason," the same category `--headless`'s dummy rendering driver already can't catch for shader compile errors (see docs/ARCHITECTURE.md's "Build & verify").
- **Armor/character specular flatness — root-caused and fixed (2026-07-24), now fully
  described as current behavior in docs/ARCHITECTURE.md's roughness section.** Root
  cause: `Roughness` sat at `StandardMaterial3D`'s own default (`1.0`, fully rough) since
  only `Metallic`/`MetallicTexture` were ever set from `g_Specular` — metallic-but-fully-
  rough with no reflection probe anywhere in the project reads as exactly the flat,
  chalky look reported, and wasn't armor-specific, it was silently true for every
  non-lightmapped chr/parts material. Verified against real `c9990` data that the fix
  reads genuine per-material values, not one hardcoded number: the armor's
  `g_SpecularPower` converted to `roughness≈0.471`, while a different material on the
  same file independently came back `≈0.707` from its own MTD data.
- **Lightmap/drawparam system, part 10 — scouted (not implemented) whether DeS's
  unused/underused armor reflection system is real; confirmed as a genuine cubemap
  system, not a matcap-style trick like water's. Findings fully carried forward into
  docs/ARCHITECTURE.md's "Character/parts specular accuracy" deferred-work bullet, not
  duplicated here.** Scouting only, per the user's own framing — no code changed.
  - **Conclusion: real, extractable, structurally-legible data — a genuine reflective-cubemap upgrade for armor materials is possible, not just rumored.** Explicitly scouting only, per the user's own framing ("not necessarily looking to implement it, at least not yet") — no code changed this pass, findings folded into docs/ARCHITECTURE.md's "Known deferred work" (the character/parts specular-accuracy bullet). If picked up later, the concrete blockers are: (1) `AssetExtractor`'s category allowlist needs `other`/`param`/`paramdef` added (currently silently skipped, not a bug — the allowlist is deliberately scoped to what `FlverModelBuilder` reads today); (2) per-map `resNameId` resolution is unconfirmed beyond the default bank — needs either finding where per-map cubemap assets live or confirming the default set really is used everywhere; (3) actually wiring a per-instance slot needs the same MSB `LightID`/`FogID`-style per-part resolution already deferred for `LIGHT_BANK`/`FOG_BANK` — this isn't a new gap, it's the same one; (4) StandardMaterial3D has no cubemap slot, so armor materials with a real env cubemap would need to move to the `ShaderMaterial` path (a real architecture change for what's currently the "simple" material family) plus a real `WorldEnvironment`, same as water.
- **MSB map-piece placement — first slice implemented, transform correctness partially verified (2026-07-24).** Follow-up to the param/paramdef scoping discussion above: rather than reading more drawparam banks with nowhere to plug them in, picked MSB parsing itself as the next step, since almost every open item (the z-fighting/LOD "placed vs unused leftover" question, per-instance `LightID`/`FogID`) waits on it and — checked directly before committing to the plan, not assumed — `MSBD.PartsParam.Part` already exposes `ModelName`/`Position`/`Rotation`/`Scale` fully read, and `mapstudio/*.msb` files are loose and already sitting in `mounted/map/mapstudio/` (the `map` category was already extracted in full).
  - **New `addons/archstone/MsbLoader.cs`**, mirroring the `FlverModelBuilder`/`FlverLoader` split: pure parsing, no scene nodes. `ReadMapPieces(msbPath)` derives the sibling block folder from the `.msb`'s own path (`mapstudio/{name}.msb` → `map/{name}/`, confirmed 1:1 by directory listing), case-insensitively resolves each `MapPiece.ModelName` to a `.flver` in that folder (same case-insensitivity precedent as texture resolution), and `GD.PushWarning`s + skips (not aborts) any part whose model file isn't found — same skip-don't-abort precedent as corrupt/zero-byte `.tpf` handling. `FlverLoader.InstantiateMap(msbPath)` uses it plus the existing `Instantiate()`/mesh cache to build one `Node3D` per placement, and `archstone.gd` gained a 7th toolbar action, "Load Map..." (same `EditorFileDialog` pattern as the existing load actions, rooted at `mounted/map/mapstudio`, filtered to `*.msb`).
  - **Real finding, corpus-checked before assuming the transform math even mattered: most `MapPiece` parts carry an identity transform, but a real and substantial minority don't.** A standalone dump (bypassing Godot, same throwaway-console-app convention as the armor/cubemap investigations above) across all 42 mounted `.msb` files found `m01_00_00_00` (Boletaria) has only 1/61 `MapPiece`s with any non-identity `Position`/`Rotation`, while e.g. `m08_00_00_00` has 84/85 and `m99_99_10_00` has 85/85 — varies enormously by map, not uniformly zero everywhere. Consistent with map-piece geometry usually being pre-baked directly into the FLVER's own world-space vertex data (an MSB entry is then just "this piece exists in this block," not a real placement instruction) except where a piece is genuinely reused at multiple instances in the same block (confirmed on `m08_00_00_00`: `m0001B0` appears seven times, `_0000` through `_0006`, at seven distinct positions) — which does need a real per-instance transform. **This means even before rotation correctness is confirmed, the MapPiece-membership filter alone is real, working value**: it distinguishes which `.flver` files in a block folder are actually referenced by the map from whichever aren't, directly answering the "can't tell placed from unused leftover" question docs/ARCHITECTURE.md's z-fighting/LOD deferred-work bullet names.
  - **Coordinate handling: position/X-negation matches `FlverModelBuilder`'s own convention exactly and is structurally verified; rotation is a starting hypothesis, not yet visually confirmed.** `InstantiateMap` negates `Position.X` (mirrors `FlverModelBuilder.cs:98`'s vertex convention) and negates `RotationDegrees.Y`/`.Z` (compensating the same mirror) while leaving `.X` alone — a hypothesis, not derived from an existing confirmed rotation path anywhere else in this project. Headless checks against `m01_00_00_00` (61/61 `MapPiece`s resolved) and `m08_00_00_00` (85/85 resolved, 84 with a real transform, values matching the raw `MSBD` dump negated as expected) confirm the read→resolve→transform pipeline runs correctly end-to-end and produces sane-looking numbers — but this is the same category of "structurally correct, not yet proven to render right" gap the pivot's fourth bug (above) already burned a round on: only a real in-editor visual check against a known, asymmetric, heavily-instanced block (`m08_00_00_00` is a good candidate given 84/85 real transforms) can actually confirm the rotation sign, not headless numeric agreement with the raw data.
  - **Scoped to `MapPieces` only, everything else deliberately deferred**: `Objects`/`DummyObjects` (real per-instance transforms too, confirmed via the same dump, but resolve against `obj/`'s existing `CandidateDirs` rules rather than a map block folder — a distinct resolution path), `Enemy`/`Player`/`Collision`/`Navmesh` parts (need systems — skeletal/animation, physics, nav — that don't exist yet), and `LightID`/`FogID` → drawparam wiring (waits on this pass being confirmed correct first). `dotnet build` stays clean (no new warnings beyond the pre-existing CS8632 set).
- **Rigid mesh-to-node binding — the real cause of "decorations sitting at the world
  origin," root-caused and fixed via real user testing of the MSB slice (2026-07-24). Now
  fully described as current behavior in docs/ARCHITECTURE.md's coordinate-conventions
  section; kept here for what isn't duplicated there.** Found only because the user
  tested "Load Map..." in the real editor and reported it broken in a way headless checks
  couldn't have caught — gates, fountains, decor sitting at the world origin even when
  their parent piece was correctly placed. A user re-test after the first fix caught a
  real gap in it (a mesh can mix vertices bound to *different* palette entries, not just
  one per mesh — fixed by keying the transform lookup per-vertex instead of per-mesh)
  plus one unrelated, correctly-diagnosed-but-deferred observation: the Doran's Mausoleum
  windows rendering "inverted," hypothesized then as a material `CullMode`/authoring-
  convention issue rather than anything to do with this fix — confirmed exactly right the
  next day by the `m2304b0` `CullBackfaces` fix below.
  - **Process note worth keeping**: a headless verification script hung indefinitely for
    reasons never root-caused, unrelated to the fix itself — a reminder that headless
    `SceneTree` scripts can hang independent of anything under test, so add progress
    prints and a hard timeout by default rather than trusting a long wait as progress.
- **`m2304b0` window one-sidedness — root-caused and fixed (2026-07-25), and the previous entry's own guess (`material CullMode`) was exactly right.** User re-raised this specifically, having remembered the direction as "outside invisible, inside visible" (opposite of the prior entry's "visible from outside, invisible from inside" — the two reports disagree on which side is which, but that ambiguity turned out not to matter: the actual bug makes the mesh single-sided in whichever direction Godot's default happens to pick, not a direction chosen by any code in this project, so either phrasing describes the same underlying defect).
  - **Root cause, confirmed by reading `FLVER0.Mesh` directly**: it has a real `CullBackfaces` field ("whether triangles can be seen through from behind"), correctly parsed by `SoulsFormatsNEXT` off disk, but never once read anywhere in `FlverModelBuilder.cs` — grepped the whole file, zero hits. Every material this project builds therefore keeps Godot's own default (`StandardMaterial3D.CullMode = Back`, single-sided), regardless of what the source FLVER actually specifies.
  - **Confirmed on the actual file, not assumed**: a scratch console app (same convention as the armor/cubemap/rigid-node investigations above, referencing `SoulsFormatsNEXT` directly, no Godot involved) dumped every mesh's `CullBackfaces` in `m2304b0.flver` — 10 of 12 meshes are `true`, and exactly meshes 7 and 8 (60/90 verts, `M[D].mtd`, no lightmap tag — the window panes, matching the user's own description) are `false`. This is the double-sided case FromSoft authored on purpose; Godot's single-sided default was silently discarding it.
  - **Corpus-scanned before fixing, same discipline as the rigid-node fix above**: 3411 mounted FLVER0 files checked directly. 713 have at least one `CullBackfaces=false` mesh; 1312 such meshes total, split 973 `StandardMaterial3D` / 338 lightmap-`ShaderMaterial` / 1 water / 0 blend. Separately found 39 real files (mostly hair/parts, e.g. `HR_M_0009`, `BD_F_8010`) where meshes with different `CullBackfaces` values share the same `MaterialIndex` — a second latent bug, since `GetOrBuildMaterial`'s cache was keyed by `materialIndex` alone, meaning whichever mesh built the material first silently decided the cull mode for every other mesh sharing it.
  - **Fix**: `GetOrBuildMaterial`'s cache key changed from `int materialIndex` to `(int MaterialIndex, bool CullBackfaces)`, so the 39 real conflicting cases each get their own correctly-culled material instance instead of silently sharing one. After building `mat`, `if (!cullBackfaces && mat is StandardMaterial3D std) std.CullMode = BaseMaterial3D.CullModeEnum.Disabled` — one line, applied only to the plain (non-shader) material path.
  - **Deliberately scoped to `StandardMaterial3D` only, matching where the reported bug actually lives.** `ShaderMaterial` (lightmap/blend/water) has no per-instance cull property — Godot's `cull_back`/`cull_disabled` is a compile-time `render_mode` keyword baked into the `.gdshader` source, the same reason `blend_mix`/`blend_add` already needed three separate lightmap shader files rather than one runtime switch (see "Lightmaps" in docs/ARCHITECTURE.md). The 338 lightmap-path double-sided meshes still render single-sided; folded into docs/ARCHITECTURE.md's "Known deferred work" rather than attempted this pass, since `m2304b0`'s windows (`M[D].mtd`, no lightmap tag) are entirely on the `StandardMaterial3D` path and didn't need it.
  - **Verified two ways**: `dotnet build Soulbrandt.csproj` clean (no new warnings beyond the pre-existing CS8632 set), and a headless `FlverLoader.Instantiate()` load of `m2304b0.flver` confirming surfaces 7 and 8 — and only those two — now report `cull_mode == CULL_DISABLED` on their `StandardMaterial3D`, matching the raw-data dump exactly.
  - **Process note**: this had already been looked at once (the entry directly above) and even named the right suspect (`material CullMode`) without following through — the earlier pass stopped at "ruled out as caused by the rigid-node fix" and didn't go check whether `CullMode`/`CullBackfaces` was the actual cause of something else. Worth remembering: naming a plausible suspect and then not checking it isn't the same as ruling it out.
- **Post-`CullBackfaces`-fix sweep: user asked whether the same "unread flag/param" pattern existed anywhere else within reach - systematically checked, found six real candidates, three implemented (2026-07-25).** Method: cross-referenced every field on `FLVER0.Mesh`/`Material`/`Texture`/`FLVER.Node`/`FLVER.Vertex` and every distinct `.mtd` param name (612 files) against what `FlverModelBuilder.cs` actually reads, corpus-scanning each candidate the same way `CullBackfaces` itself was confirmed rather than trusting a name alone.
  - **Implemented, in order of confidence/impact:**
    1. **`g_SpecularPower`→roughness (the `c9990` armor fix, see the entry below this file's own history) had never been extended past `BuildStandardMaterial`.** `BuildLightmapMaterial`/`BuildBlendMaterial` (the `ShaderMaterial` family - **63% of all materials in the game**, every lightmapped/terrain-blend surface) never read the `.mtd` at all, so their shaders left `ROUGHNESS` at Godot's spatial-shader default (`1.0`, fully matte) - the exact same flat/non-reflective bug, just never fixed for the majority of world geometry. Confirmed 100% of sampled lightmap/blend MTDs (54/54, 18/18 distinct files) carry real `g_SpecularPower`, ranging 2-128 (wider spread than chr's 4-7 - some map surfaces, e.g. polished stone/wet floors, are meant to look noticeably shinier than others). Refactored the inline roughness block out of `BuildStandardMaterial` into a shared `ResolveMtdShading()` (one `MTD.Read()` per material instead of duplicating the read three times), reused by all three families.
    2. **`g_DiffuseMapColor`/`g_DiffuseMapColorPower` - a real per-material diffuse tint, read by nothing anywhere.** 568/584 sampled MTDs leave it white (a no-op), but 16 carry a genuine tint - correlated with real, identifiable assets: `Ps_Wander_Ghost.mtd` (near-black, `power=0.99`), `s_a03_thunder00[dn].mtd` (blue-ish, a lightning-effect material), `a03`/`a04_light_shaft[dn]_add.mtd` (warm tint, `power=5` - light-shaft/god-ray volumetrics), `a03_cloud[dn]_add.mtd`. Confirmed `g_DiffuseMapColorPower` is real per-material data, not a fixed authoring constant (0-7 game-wide, most common 0.5/0.6/1) - applied as a real exponent (`pow(color, power)`), not a plain multiply, since ignoring it would have visibly under/over-darkened exactly the tinted materials found. `StandardMaterial3D.AlbedoColor` needed no shader change at all - it already multiplies natively against `AlbedoTexture` in Godot's own material; the `ShaderMaterial` families got a new `diffuse_tint` uniform instead.
    3. **`g_BumpMapSmoose` on water materials (-0.5 to 1 game-wide) - the one real water `.mtd` param `BuildWaterMaterial` still didn't read**, despite that function already being the most thorough MTD-reader in the file. Applied as a wave-normal intensity scale in `water.gdshader`, explicitly marked as an uncalibrated guess (same category as the already-documented `wave_detail_scale`/`refraction_scale` constants in that same file) since there's no visibility into what the original shader actually did with it.
  - **Verified**: `dotnet build` clean (no new warnings), and - since `--headless` never actually compiles GLSL (see "Build & verify" in docs/ARCHITECTURE.md) - a real-driver check (`xvfb-run -a godot-mono --path . -s check.gd --rendering-driver opengl3 --display-driver x11`) loading `m2304b0.flver` (lightmap + blend + the still-fixed-from-earlier-today `StandardMaterial3D` windows) and `m9990b2.flver` (a real water-classified file, `mounted/map/m05_02_00_00/`, found by scanning for `g_Envmap`) - zero `SHADER ERROR` lines, all three touched shader families (`lightmap_common.gdshaderinc`+its three variants, `terrain_blend.gdshader`, `water.gdshader`) compiled and every surface resolved to the expected material type.
  - **Found and documented, not implemented this pass** (each needs more investigation or is a bigger standalone feature - see docs/ARCHITECTURE.md's "Known deferred work" for the write-up of each): `g_BlendMode`, a real 0-7 enum that does *not* correlate cleanly with the existing `_Edge`/`_Alp`/`_Add` filename-substring heuristic (e.g. `BlendMode=2` appears on 68 MTDs with no recognized tag at all, vs. 48 with `_Alp`) - could mean the current heuristic silently misses real transparency cases, unconfirmed; `g_LightingType` (0/1/3, matching the `Dif`/`DifSpc`/`HemEnvDifSpc` FRPG naming docs/ARCHITECTURE.md's character-specular entry already cites), which the new roughness/tint fix applies unconditionally without checking - gating it on this would be the safer long-term shape; and a real, explicitly-flagged `g_IsGhost` material family (10 MTDs, own dedicated `g_GhostTexColor`/`g_GhostEdgeColor`/`g_GhostTexScroll_0`/`_1` params) confirming the already-documented "ghost dissolve" materials are a real, distinct FromSoft convention that still needs its own shader.
  - **Checked and ruled out**: `FLVER.Node.Flags` (`NodeFlags.Disabled`/`DummyOwner`/`Mesh`/`Bone`) is `0` for every one of 51,923 nodes across the entire mounted corpus - not a real signal in DeS's FLVER0 data at all, nothing to gain there. `FLVER.Vertex.Tangents`/`Bitangent` are real authored per-vertex data (present in 61%/6% of material buffer layouts) currently discarded in favor of Godot's `SurfaceTool.GenerateTangents()` auto-generation - likely low visual impact for well-formed UV maps, not pursued without a concrete visual discrepancy to justify the extra plumbing. `Dummy.*` fields (hitboxes/particle attach points/`ReferenceID`) are gameplay markers, out of scope for a mesh importer with no debug-gizmo feature requested.
- **The lightmap/blend roughness fix immediately regressed most map surfaces into a washed-out, glassy sheen - user-confirmed via screenshot (Boletaria, hazy uniform sheen on walls/ground/towers), root-caused and fixed same day (2026-07-25).** User described it precisely: "hard to explain... definitely the type that comes up from specular materials and reflectivity" - and that framing pointed straight at the actual mechanism rather than needing much blind searching.
  - **Root cause: a pre-existing `metallic = spec`/`METALLIC = spec` line in `lightmap_common.gdshaderinc`/`terrain_blend.gdshader`, unrelated to anything changed in the roughness/tint work itself, that the roughness fix simply exposed for the first time.** This line predates this session entirely - the same "spec map jammed into the metallic slot" simplification already documented as a `ponytail:` ceiling on `BuildStandardMaterial`, inherited by the lightmap/blend shaders without re-checking whether the same trick still made sense there. It was invisible before today because `ROUGHNESS` defaulted to Godot's own `1.0` (fully rough) on this material family - a fully rough surface shows almost no visible metallic/reflection-probe response regardless of `METALLIC`'s value. The moment `ROUGHNESS` became a real, lower value (confirmed via corpus scan: Boletaria's wall/ground lightmap materials - `M_4Stone[DSB][L].mtd`/`M[DB][L].mtd`/`M[D][L].mtd`, 96/74/62 materials respectively in `m01` alone - uniformly carry `g_SpecularPower=4`, giving `roughness≈0.577`, not an unusually shiny value in isolation), the same pre-existing `METALLIC` assignment suddenly had a real, visible effect for the first time.
  - **Why it looks the way it does**: this project has zero `WorldEnvironment`/reflection probes anywhere (already-confirmed, see the water-tuning entries) - a nonzero `METALLIC` at a real (non-1.0) roughness tells Godot's PBR model to tint reflectance by `ALBEDO` and pull energy from diffuse into a specular/reflection response, but with nothing real in the scene to reflect, Godot substitutes its own fallback ambient - reading as exactly the flat, hazy, misplaced sheen the screenshot shows, uniformly across every lightmapped/blend surface (i.e. most of the visible map). `water.gdshader` already avoided this exact trap from the start (never sets `METALLIC` at all, specifically because of the same no-reflection-probe reasoning) - the lightmap/blend shaders' own pre-existing line just hadn't been checked against that same established precedent until this regression forced the comparison.
  - **Fix**: `metallic = 0.0` in `lightmap_common.gdshaderinc`'s `lightmap_shade()`, `METALLIC = 0.0` in `terrain_blend.gdshader`'s `fragment()`. `spec`'s real, intended contribution in both shaders is unchanged - it's still the multiplier on the existing hand-computed `env_spc` `EMISSION` term (the same water/hemisphere-ambient-family design of routing "shininess" through `EMISSION` instead of Godot's built-in metal pipeline, precisely because there's no real environment to feed that pipeline). `ROUGHNESS` itself was not the bug and is kept as computed - it's real Phong data, correctly converted, and isn't what caused the sheen.
  - **Verified**: real-driver (`xvfb-run -a godot-mono --path . -s check.gd --rendering-driver opengl3 --display-driver x11`) load of `m2304b0.flver`/`m9990b2.flver` - zero `SHADER ERROR` lines, all surfaces still resolve to the expected material types. Not yet re-confirmed by the user in the real editor against the reported screenshot - same "fix + verify build/compile, ask for a real visual re-check" pattern as the rest of this project's material work.
  - **Process note**: this is the second time in one day a fix validated narrowly on one case (chr/parts metal armor, or `m2304b0`'s specific windows) got extended to a much broader material family without re-deriving whether the same assumption still held there - the roughness *value* itself transferred fine, but a separate, already-existing simplification riding along with it (`METALLIC = spec`) didn't. Worth remembering when extending any fix across material families: check every simplification already baked into the destination shader/path, not just the one property being added.
- **`metallic = 0.0` did NOT fix the lightmap sheen regression, user-confirmed - whole lightmap/blend roughness attempt reverted outright rather than debugged further same-session (2026-07-25).** After the `METALLIC = spec` fix above shipped, the user re-tested in the real editor and reported the washed-out sheen was still there, unchanged - meaning the diagnosis above, while plausible and internally consistent (matching water.gdshader's own established "no reflection probe" precedent), was **not the actual mechanism**, or not the only one. Nothing further was root-caused this session: the remaining candidates (an actual `SPECULAR`-driven dielectric reflectance response even at `METALLIC=0`, Godot's own default/fallback ambient response scaling with `ROUGHNESS` regardless of metallic, a `g_LightingType`-dependent distinction never checked, some interaction with the hemisphere-ambient/env terms already in this shader, or something about the editor's own default preview environment specifically - the same confound already flagged as pausing water's own accuracy tuning) were named but not tested.
  - **Decision: revert the whole lightmap/blend roughness+metallic change outright, not comment it out.** User explicitly asked whether to comment out or remove entirely, framed as "come back to it once we understand DeS's engine/environment quirks better." Removed entirely (`git`-trackable, not a stale disabled block) because: (1) this project's own established convention for abandoned attempts is a clean revert, not commented-out code left in place (the PS3-deswizzle dead end and the three lightmap-formula guesses in "Lightmap system, part 4" were both fully reverted, never left commented); (2) a future correct implementation will almost certainly look different from this attempt anyway - it needs real `WorldEnvironment`/lighting groundwork, probably `g_LightingType` gating, and possibly the `EnvSpc` cubemap system, none of which this attempt touched - so keeping this specific broken code around wouldn't actually save meaningful future work; (3) the full reasoning trail lives here and in `docs/ARCHITECTURE.md`'s "Known deferred work" regardless, so nothing about *why* this was tried and reverted is lost by deleting the code itself.
  - **What was kept**: the `g_DiffuseMapColor`/`g_DiffuseMapColorPower` diffuse-tint read on `BuildLightmapMaterial`/`BuildBlendMaterial` (unrelated - never touches `METALLIC`/`ROUGHNESS`, no plausible connection to a specular-looking regression) and the original `BuildStandardMaterial` roughness fix (chr/parts, predates this session, independently user-confirmed back on 2026-07-24, not implicated in this report at all - the user's complaint was specifically about "surfaces in maps," not characters/armor).
  - **What was reverted**: `lightmap_common.gdshaderinc`'s `roughness` uniform and `metallic = 0.0` (back to `metallic = spec`); `ROUGHNESS = roughness` in all three `lightmap*.gdshader` variants; `terrain_blend.gdshader`'s `roughness` uniform and `METALLIC = 0.0`/`ROUGHNESS = roughness` (back to `METALLIC = spec`); the corresponding `mat.SetShaderParameter("roughness", ...)` calls in `FlverModelBuilder.BuildLightmapMaterial`/`BuildBlendMaterial`. Verified back to a clean state the same way the original fix was verified: `dotnet build` clean, real-driver (`--rendering-driver opengl3 --display-driver x11`) load of `m2304b0.flver`/`m9990b2.flver` with zero `SHADER ERROR` lines.
  - **Not yet re-confirmed by the user that the revert actually restores the pre-regression look** - same "fix, verify build/compile, ask for a real visual check" pattern as everywhere else in this project's material work.

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
  `--rendering-driver` shader-compile check in docs/ARCHITECTURE.md's "Build & verify").

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
  original narrow uncompressed-only condition. See docs/context.md's "Lightmap/drawparam
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

See docs/ARCHITECTURE.md's "Known deferred work" for the current list (animation/skeleton import,
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
fix. Revisit once real lighting/environment/ambience work happens (see `docs/PLAN.md`).
