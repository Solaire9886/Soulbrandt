# context.md

Investigation history: how the facts in `docs/ARCHITECTURE.md` were established, the wrong turns
taken on the way, lessons that still apply, this machine's environment quirks, and approaches
already tried and rejected. `docs/ARCHITECTURE.md` is the current state; read this before
re-trying something that looks like an obvious fix. Older entries are condensed by topic; part
numbers (e.g. "part 43") refer to the original session log and appear in older commit messages.
The last few sessions are kept in full at the end.

## Standing lessons

- **When data confirmed from the game renders wrong, ask what the engine does that Soulbrandt
  does not.** Do not revert the finding, reach for more data, or fit a constant. Every "the
  data looks wrong" case so far (the hemisphere pair, the scattering, the washout) was a
  missing or wrong stage on our side.
- **Measure, don't fit.** A constant that has to be re-fitted across a >100× range means the
  model around it is wrong. Fitting N knobs to fewer than N observations yields numbers that
  satisfy the metric and are wrong in every particular. The fitted scattering chain (parts
  20–47) was replaced wholesale by the engine's own constants (2026-09-25).
- **External corroboration is a lead, not a fact.** Read every doc comment and guard on the
  exact function before trusting an outside comparison (the Soulstruct PS3 deswizzle made
  textures worse). Verify a premise with a throwaway check before building on it.
- **Cost a risk before deferring on it.** A few lines of arithmetic showed the "ungated env
  term will blow out" worry behind the black-props deferral was wrong.
- **Extending a fix to a new family means auditing that family's own simplifications** (the
  roughness change exposed a hidden `metallic = spec`).
- **A later, individually correct change can silently undo an earlier fix** (the double-sided
  window regressed when routing moved its material to another shader path). Re-check a fix's
  scope when routing changes.
- **Name a suspect only after ruling it in or out.**
- **User testing in the real editor finds what headless checks cannot:** invisible
  `ImporterMesh`, broken file browsers, runtime VFX cost. Headless proves structure only.
- **A lit Godot shader in a scene without lights is lit by the editor's preview sun and sky.**
  Every hand-computed shader must be `unshaded`, or the editor becomes an uncontrolled input.
- **When a decoder reports an unexpected format, check the byte budget first** (a 32×32 face at
  4096 bytes is 4 bytes per pixel, whatever the decoder says).
- **Read the developers' Japanese text.** MTD `Description` and paramdef
  `Description`/`DisplayName` fields settled `g_DiffuseMapColorPower` (a multiplier), the bank
  field meanings and several material families.
- **RPCS3's decompiled GLSL misleads;** prefer the raw `.fpo` disassembly. Cubemap face
  selection expands to dot products that look like directional lights, and the decompiler
  never emits `TEXCUBE`/`reflect`/`discard` (grep `TEX2D`, `TEX3D`, `TEX2D_SHADOWPROJ`,
  `_kill`, `_fetch_constant`).
- **Captures are the ground truth for constants and images.** A frame capture carries every
  draw's vertex constants, the patched fragment constants, the game camera and (with
  colour-buffer write-back) the game's own render targets. Render at the captured camera and
  compare pixels.
- **Abandoned attempts are reverted, not commented out.**

## Import foundations (2026-07)

- **Origins.** A console app (`desflver_test`) exported OBJ/MTL/PNG through WitchyBND, then a
  native `EditorSceneFormatImporter`, then the current manual loader. Only the official
  godotengine.org .NET build supports C# (Redot and Steam's Godot do not). The project was
  renamed from `Boletaria`. The fork's local patches once sat uncommitted in a plain clone,
  where any checkout could have erased them; keep submodule patches committed and pushed.
- **Mirrored imports.** Every import was a left/right mirror, invisible because mirroring also
  flips winding and the checks kept passing. Caught when the user recognised `m03_01` as a
  mirror of its sibling, then `c9990`'s swapped hands; confirmed by skeleton bind-pose FK. Fix:
  negate X and swap winding. The OBJ exporter's V-flip and winding swap were Blender
  conventions and wrong for the native path.
- **Pfim** returns the whole mip chain; slice the base level. **UV scale** is always 1024 for
  DeS (fork patch). **Case-insensitive** texture lookups everywhere.
- **Invisible models and lost nodes.** `ImporterMeshInstance3D` never renders outside the import
  pipeline; `PackedScene.pack()` drops nodes without `.Owner`. `ResourceSaver.save(packed,
  "res://debug.tscn")` dumps a scene as text for inspection.
- **The reimport pipeline** (deleted 2026-07-24) absorbed a bulk-import hang on `o0103`
  (unbounded texture caches; `MaybeEvictDecodedTextures` survives from this), a batching fix,
  and a concurrency gate. Godot's engine source confirmed no plugin lever over reimport
  concurrency, so the architecture changed instead (prototyped from a peer developer's
  reference implementation). Of four bugs found during that change, two (a leftover
  filesystem `scan()` that re-triggered the queue, and invisible `ImporterMesh`) surfaced only
  in real editor use. An engine crash on `m2011b1.flver` seen then was never root-caused.
- **FLVER0 vertex-layout crash.** Some meshes' layouts genuinely omit normal/UV/colour. Two crash
  sites: the builder's vertex loop and `Triangulate`'s flip check (now gated on normals).
  Verified across ~3,411 files.
- **Rigid node binding.** Decorations at the world origin (`m2304b0`, `m2501b0`) were vertices
  bound to non-identity nodes. The first fix used one palette entry per mesh; a mesh can mix
  entries per vertex. 24% of `m02_00_00_00`'s files were affected.
- **Mounting** replaced a `mounted` symlink and WitchyBND (2026-07). BND entry names use a
  `DVDROOT` segment distinct from the `Model` segment texture references use.
- **Map-piece naming.** `l1`-suffixed Boletarian Palace pieces are unplaced variants (0 MSB
  references each). Some pieces reference `m02_tower_wall_plain.tga`, which exists nowhere in
  the data (white patches, not an importer bug).

## Textures and cubemaps

- **Resolution chain.** A 26,656-reference corpus scan showed the old per-case helpers
  overlapped (`GetAreaTextures` never succeeded where another did not), so they became one
  ordered `CandidateDirs` chain; `SiblingObjTextureDir` later covered textures duplicated only
  into other obj containers (`o6510_1`, the `m02_01` firewood/cart decor in the `o24xx` range).
  Rules 3–4 resolve ~90% of obj/parts references that rules 1–2 miss.
- **Lightmap block noise** (purple/green 4×4 speckle on m01 lightmaps) is present in the real
  game (the user checked unmodified DeS). The Soulstruct PS3 deswizzle tried against it made
  things worse.
- **Cubemaps** (part 17, part 24): Pfim decodes only face 0 (re-wrap each face); uncompressed
  ARGB8888 faces were misread as `Rgb24`, rotating channels into a rainbow on m02/m04/m06/m08/
  m99 (static speckle from EnvDif, swimming reflections from EnvSpc). Now read directly as
  A,R,G,B. Removing `GenerateMipmaps` from that path changed nothing (mip 0 is always sampled).
- **Env cubemap names** (part 14): `map/<mXX>/<mXX>_9999.tpf`, `NNN` from `LIGHT_BANK`;
  930/930. `EnvSpc_m02_003` is nearly uniform although slot 3 materials are the glossiest; the
  slot→sharpness reading was never settled.

## Materials

- **`g_BlendMode`** (part 14, 2026-08-27) replaced the `_Edge`/`_Alp`/`_Add` filename heuristic,
  which disagreed with the engine on 153/584 materials (case sensitivity, tag-less variants,
  `A03_Sky*` rendering opaque, four light shafts rendering additive). `lightmap_sub` covers
  the 139 `a04_blood` meshes.
- **`g_LightingType`** (2026-08-18): 0 unlit (sky domes, ghost, additive VFX), 1 HemDir3,
  3 HemEnv; `SoulsFormatsNEXT`'s own enum names match. Making type 0 unshaded fixed the washed
  sky.
- **Black props** (part 25): a `g_LightingType=3` population without lightmaps on
  `StandardMaterial3D`, lit by nothing. Routed to the shader family; lands ~2.3× brighter than
  a lightmapped surface, not blown out.
- **`g_DiffuseMapColorPower`** (part 51) was applied as an exponent, a no-op on the 488 MTDs
  with a white tint. The Japanese descriptions (`天球_明るい` 1.5, `天球_暗い` 0.6,
  `デヒューズ ２倍`) show it multiplies. The Nexus runes began to glow. A live capture later
  showed per-material 0.5/0.6 multiplies in the same frame, ruling it out as the cause of
  darker maps (part 56).
- **The 0.6 on vertex colour** (part 43, then 2026-09-15): captured HemEnv programs multiply
  the vertex colour by 0.6. It was first added as a fixed constant; the map-shading audit then
  showed it is the material's own `g_DiffuseMapColorPower`, already in `diffuse_tint`, so the
  constant was removed. The same audit added `specular_tint` to the env specular term and the
  vertex-RGB multiply on two-layer materials.
- **Roughness on the lightmap family** (2026-07-25) produced a glassy sheen through an
  inherited `metallic = spec`; `metallic = 0` did not fix it; reverted. Moot since the family
  became unshaded.
- **Double-sided windows** (`m2304b0`): `CullBackfaces` was parsed and never read (fixed for
  `StandardMaterial3D`, 2026-07-25); the 2026-08-28 routing change moved the windows' material
  onto the lightmap shader, which lacked the fix. Part 54 added `cull_disabled` sibling shaders
  and flips the normal on back faces; 457 meshes need them.
- **UV scroll** (part 14): `g_TexScroll_0` wired for every family; `vfx_scroll` exists because
  `StandardMaterial3D` has no UV animation.
- **Texture-driven `Emission`** desaturates toward white without an exposure environment
  (reproduced with a synthetic texture); glow came from the multiplier fix instead.
- **Ghost materials** are two enemy types with their own shader pairs (`Ps_Wander_Ghost`,
  `Cs_ShadowMan`), distinct from `DS_Gst_*`.

## Map lighting and the output stage (2026-07 to 2026-09-04)

Superseded in its details by the engine-exact work of 2026-09-25/26 (end of this file); kept
for the elimination record.

- **Hemisphere + env structure** (parts 4–13): the lightmap scales only an env term while a
  two-colour hemisphere ambient stays ungated. First inferred from Dark Souls Remastered's
  decompiled shaders, then confirmed from captured DeS programs, including the literal
  `min(shadow, lightmap)` gate. `LIGHT_BANK`/`FOG_BANK` identified from MSB `LightID`/`FogID`
  and wired; `colA`-style fields are percent scales.
- **Self-documenting banks** (part 14): the paramdefs' own descriptions named every field;
  `LIGHT_BANK` has seven terms; `FOG_BANK.degRotW` is the fog strength.
- **Hemisphere diffuse pair** (part 15): 5–30× the ambient pair; adding it collapsed Nexus
  contrast from 61× to 2.8× because nothing downstream compressed highlights. The user's
  correction became the first standing lesson. Wired and reverted three times in total.
- **No directional lights on map geometry** (part 16): 44 captured HemEnv programs carry the
  hemisphere and no directional terms; HemDir3 programs carry exactly 3 or 6. The waxy walls
  were per-pixel normals missing from the hemisphere and env terms, fixed with the env
  cubemaps (part 17).
- **Pipeline trace** (part 18) and **output stage** (part 19): every material program ends in an
  exposure step, so exposure belongs in the material shader under Compatibility's RGBA8 target.
  Making the family unshaded removed the editor preview sun as a hidden input. A useful trick
  from then: place check cameras at object positions (map-piece nodes sit at the origin).
- **Atmosphere, parts 20–33:** identified as Hoffman–Preetham scattering from
  `LIGHT_SCATTERING_BANK`'s own field names. About ten fitted iterations of wavelength weights,
  normalisation and `scatter_distance_scale` (2.5e-5 to 3.5e-3) followed before measurement.
- **Measured constants** (parts 35–37, 46–48): capture constants gave per-channel wavelength
  ratios, `c106 = 1/c104` (per-channel in-scatter normalisation), bluer extinction than
  scattering, and `E ≈ 1.78`. All of this was later replaced by computing the constants
  exactly (2026-09-25), which also showed part 35 had Rayleigh and Mie swapped.
- **The m02 washout** (parts 42–48): wrong turns were "m02 is mostly unlit" (a diagnostic bug;
  it is 85% lightmapped), "HemEnv has directional lights" (decompiler artefact), and "colour
  space mismatch". Part 44 settled colour space by probe: no output sRGB encode, and
  `: source_color` is a no-op; `rrc-tex` showed DeS fetches every HemEnv texture without gamma.
  The real causes were a linear tone pipeline (the Reinhard curve was an assumption; a first fix
  kept a stray `·0.5` and came out 2× too dark), the missing FOG_BANK colour fade (part 45;
  fixed the Old One's white fog), the sky bypassing the output stage, the in-scatter
  normalisation, and the exposure stand-in. `des_tone_correct` was already the exact
  `DS_Fil_HDR_ColAdj` matrix.
- **RSX fixed-function fog is never used** (part 36): no `SET_FOG_*` in eight frames, no program
  reads `f[FOGC]`. The FOG_BANK colour is instead the target of a hand-rolled `mix()` in every
  HemEnv epilogue (part 45).
- **Darker maps** (part 56) were the fixed exposure stand-in, which gave dim interiors no
  boost. Resolved by real adaptation (2026-09-25).
- **`WorldEnvironment` attempts** (2026-08-18): an ambient/tonemap environment built from the
  tone banks turned maps dark because `AmbientLightEnergy` replaced the editor's preview
  ambient for the then-`StandardMaterial3D` props. The glow-only bloom environment (part 34)
  was later replaced by `PostProcessPipeline`.

## Shadows (2026-09-03/04)

- **Characterisation** (part 37): 2048² Z24S8 atlas as 2×2 1024² tiles, four splits, map pieces
  one cascade matrix per draw in `c112..c115`, characters select per pixel; no depth pre-pass.
  A spike showed a `SubViewport` depth pass works under Compatibility.
- **v1** (part 38): a whole-map box. It went dark on bright maps from depth quantisation and
  front-face acne; fixed with a region cap, 16-bit packed depth and `cull_front`.
- **`SHADOW_BANK`** (parts 39, 49): its own light direction, density, tint, distance fade,
  begin/end split range, and PSM fields; the game's shadows are perspective, re-warped around
  the camera each frame.
- **Camera-following versions** (part 41; part 49's mild-perspective and texel-snapped
  follower cuts) made shadows swim or slide. The user's point: the cast is fixed by sun and
  geometry; only the fade depends on the camera. v2 is one static pass, "about right".
- **Draw-group culling** (part 40) was built and reverted: every map piece has all-zero
  `DispGroups`, and the DS1 rule deleted three quarters of m02.

## Water (2026-09-05)

- **Rebuild** (part 50): `DS_Water_Env` disassembly plus six captured water draws; every constant
  maps to a water `.mtd` parameter (`g_TileScale_i`, `g_TileBlend_i`, `g_WaterColor`,
  `g_Fresnel*`, `g_BumpMapSmoose`, `g_WaterFadeBegin`). The old "not recoverable" verdict
  predated the capture tooling. Removed invented factors (`wave_detail_scale = 12`, a 2D matcap
  env map, a refraction clamp).
- **Follow-ups:** scroll was 30× too fast (`g_TexScroll_0` is a velocity; do not normalise it);
  `g_BumpMapSmoose` is a Z bias, and the octaves are not renormalised; the glint weight is
  `g_SpecularMapColor · Power` exactly (`c39`), and multiplying in the sun intensity as well
  blew it out. Two conclusions here were wrong and were replaced on 2026-09-27 (below): the
  coastal darkening is not a depth-buffer band, and `f[TEX6]` is the vertex colour, not a
  per-vertex distance factor.
- `a03_water_in` is "glowing water" (`光る水マテリアル`); `A05_water00[We]_Skin` is the
  Leechmonger arena (`蛭デーモン`); lava deliberately uses the water shader (`溶岩の揺らぎ`).

## Point lights (2026-09-05 to 09-26)

- **Part 52** matched a captured light colour to `POINT_LIGHT_BANK` row 10 and inferred
  `row = PointLightID mod 64`. Part 55 placed lights at their anchor piece's centre, then its
  smallest sub-mesh; part 55 follow-up 2 found m08's lights anchored to collisions (no parser);
  part 60 found the receiving side's query point used an off-tree `GlobalTransform` (identity).
- **All superseded 2026-09-26:** `PointLightID` is a region index and `UnkT04` the row; row 10
  was the player's own light; MSB lights reach objects only (see the end of this file).
- **The PC Remaster mod** (part 55 follow-up 3): all draw-parameter banks are byte-identical to
  vanilla. Its reflective armor re-tags chr/parts MTDs from `g_LightingType` 1 to 3 with
  re-tuned specular; its torch light on geometry comes from converting 11 host map pieces into
  objects and adding hand-placed regions.

## Shader library and capture tooling (2026-08-28 to 09-03)

- **The library** (`shader/*.shaderbnd`, never extracted before): 1349 named programs with a
  fixed-width naming scheme; the material does not determine the lighting model; `HemEnvLerp`
  is a two-cubemap transition, and the "lerp toward a constant by EnvDif alpha" belongs to
  plain HemEnv. Extraction needed `FallbackEntryOutputPath`, because entries without a
  `DVDROOT` segment were silently dropped.
- **`RsxShaderMatch`** (parts 34–37): `.fpo`/`.vpo` decoding (fragment microcode needs a 16-bit
  halfword swap, vertex microcode does not), fingerprint matching of RPCS3 shader logs
  (`FragmentProgramN` numbering changes every session, so the JSON is keyed by fingerprint),
  a disassembler (it drove the RGB env-specular and three-lobe Phong fixes; the NV output scale
  `_x2` was once missed and made `reflect()` look non-standard), and the `.rrc` frame-capture
  reader. `DS_Phn_*` and `DS_Gst_*` vertex programs are identical.
- **Capture facts:** `c104..c111` are per-draw when several scatter rows are in view; fragment
  inline constants carry per-draw values (exposure, fog colour, point-light colour and
  position); captures from 2026-08-31 cover m01 (Nexus), m02 (Boletaria), Stonefang and the
  Shrine; 094438/094528 are m02, 205056 is m08.
- **RPCS3 capture practice** (user, 2026-08-27): the Vulkan backend is preferred (OpenGL mode is
  slow); its shader logs are `.spirv` files that are plain GLSL text. Pausing RPCS3 to catch a
  specific draw does not work (it lands on the post-process composite).
- **Remaining data categories** (scouted 2026-08-17 and part 53): `MSBD` has ten part types and
  seven event types; `DofBank` and `LensFlareBank` are straightforward and unbuilt; ~30
  gameplay paramdefs are unexamined; `EnvLightTexBank.isUse` and
  `Collision.EnvLightMapSpotIndex` are zero everywhere.

## Havok ecosystem (2026-08-18)

Verified by reading source, not READMEs: `SoulsFormatsNEXT`'s `NVM.cs` reads DeS navmesh and is
unused; the unmerged `old-kata-2018-dec-13` branch (commits `29cd70b`/`73fd675`) adds
`HKX.cs` (a packfile reader with an explicit DeS variant) and `Collision.cs` (raw collision
vertices and indices; rough, write unimplemented, no animation); `SoulsAssetPipeline`'s generic
Havok reader supports SDK 2015+ only; `Grimrukh/soulstruct-havok` has working DeS
(Havok 5.5.0) skeleton, animation (wavelet-compressed) and collision classes via the packfile
path. Its bundled Windows tools are for writing only.

## Effects (2026-09-06 to 09-15)

- **Parsing** (parts 57–58): the `.ffx` format differs from DS2's in the header, object header
  and `ParamList`; Param 31/32 are recursive effect/action references like DS2's 37/38; several
  type numbers mean something else in DeS (13, 38, 40, 41, 66), so each is DeS-conditional;
  effect envelopes come in versions 3 and 4; 22 effects use a three-child aggregate. From an
  external handoff (`DES_FFX_RESEARCH_DISCOVERIES.md`), verified on the corpus: 3,091/3,091.
- **Eager map-wide placement** (part 59) caused major editor cost and partial rendering; it
  was removed. `SfxPreview` needed deferred attachment for nodes entering the tree in bulk.
- **Region placement** (2026-09-14): `UnkT00` indexes MSB regions; 180 Nexus candles had been
  stacked on one piece centre (720 systems at one point). Freeing previews before tree entry
  leaked particle nodes (fixed). The map preview became opt-in and budgeted.
- **Playback semantics** (2026-09-14): template 2117 is an exclusive LOD selector (all branches
  were drawn together); capacity and batch count are bound separately (the fixed 12 particles
  inverted candle and mist balance); Compatibility cannot emit GPU particles manually, hence
  CPU-scheduled `MultiMesh`. Firefly 1010000 uses transparency 4 (additive; verified against a
  capture draw); action35's serialized order is left/up/front (the dry-ice split into side
  curtains came from treating the Lua wrapper's argument order as axes).
- **2026-09-15:** action55 gravity and preview wind let smoke and bonfires rise; zero-wait
  emitters use a bounded clock; camera-translation stutter (19 ms p95) came from re-parsing
  prepared effects per placement (now shared, 1.6 ms p95); the archstone sword glow is 99100,
  requested by map Lua on the sword entity, not an MSB event; a first Lua pass found
  script-only object and camera effects and the entity IDs they need; object/dummy and
  camera-region sources were added to the map preview.
- Detailed evidence for all of this is in the external archive (`VFX_PLAYBACK.md`,
  `ARCHSTONE_VFX.md`, `LUA_FIRST_PASS.md`, `OBJECT_VFX_PREVIEW.md`, `CAMERA_VFX_PREVIEW.md`).

## Open investigations

- Which bank row the per-frame light selection assigns to each draw, and the executable rule
  that keeps MSB lights off map pieces (captures show the behaviour only).
- The tone-row state writer (which collision's rows the frame uses).
- The Nexus colonnade drawn at a transform no m01 MSB part has.
- The sprites' per-vertex fog/scattering factors and the unlit program's `c1` (0.4 on Nexus
  draws).
- The env specular slot default for MTDs without `g_EnvSpcSlotNo`, and whether slots track
  sharpness.
- Draw-group semantics in DeS.
- The light-shaft quad edge softness.
- `env_intensity` overexposure on m01's `m0000B0` (the Old One's arena, seen only in the
  ending, so in no capture): likely a report from before the adapted exposure; the user
  considers it a false report (2026-09-27). Kept as a potential issue.

## C#/Godot interop gotchas

- `ShaderMaterial.SetShaderParameter()` from C#, for a shader uniform hinted
  `: source_color` (e.g. `uniform vec3 water_color : source_color`), needs a `Color`, not
  a `Vector3` — passing `Vector3` doesn't throw, it silently no-ops (the parameter reads
  back `null` via `get_shader_parameter()`, as if never set, while every other
  non-color-hinted uniform set the same way works fine). Found by inspecting an imported
  water material headlessly and noticing exactly the color-hinted uniforms were null.
- **A method whose signature uses a non-Variant type (a custom `record struct`, `PARAM.Row`, a
  nullable struct) is not exposed to GDScript at all** — calling it fails with `Invalid call.
  Nonexistent function '<name>' in base '<Class>.cs'`, not a marshalling error, so it reads like
  the method is missing. Since throwaway GDScript check scripts are this project's only
  verification path, a C# API meant to be *verified* has to return Variant-compatible types
  (`Godot.Collections.Dictionary`/`Array`, `string[]`, primitives). Found 2026-08-27 on
  `DrawParamReader.GetEnvCubemapNames`.
- **`ImageTextureLayered.get_layer_data()` (so `Cubemap`, `Texture2DArray`) returns blank images
  under the Compatibility renderer** — not null, not an error, just zeros, so it reads as "the
  texture is empty" rather than "readback is unsupported". Verified with a control built from
  known solid colours in pure GDScript. To check a layered texture's real contents, render with
  it and read the *viewport* image instead. Found 2026-08-27 while verifying env cubemaps.
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

- **Host moved from Arch Linux to Void Linux (glibc), 2026-08-21.** `godot-mono` and
  the .NET 8 SDK are installed from the official downloads (godotengine.org,
  dotnet.microsoft.com); Void packages neither. No `DOTNET_ROOT` override is needed. (An older
  manual merged root at `~/.dotnet-godot` is obsolete.)
- `godot-mono --headless --build-solutions` hangs indefinitely in this environment even
  with the runtime fixed, for reasons never fully root-caused beyond "something in
  Godot's own in-process MSBuild invocation." Given up on making it work; `Soulbrandt.csproj`
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
- **The opengl3 check under Xvfb/llvmpipe loads one full map per process.** Four maps in one
  process segfaulted (a resource ceiling of the software renderer, not a shader defect).
- **Telling a stalled process from a working one:** sample `utime + stime` in
  `/proc/<pid>/stat`; zero ticks per second is stalled. A command backgrounded with a trailing
  `&` can report completion while the real process keeps running; check `ps`.
- A CoreCLR SIGSEGV during a chunked corpus scan was traced to unrelated system load; the full
  run succeeded after a restart.
- **A/B renders that swap source files must refresh their timestamps (2026-09-25).**
  Restoring files with `tar x` (or anything that preserves mtimes) leaves them older than the
  built assembly, so the next incremental `dotnet build` reports success without recompiling
  and the run silently uses the other version's C#. `touch` the restored files or build with
  `--no-incremental`. This produced a false "Nexus renders black" regression during the
  map-piece atmosphere review.

## Dead ends — don't re-try these

**Tooling and environment**
- Steam's Godot or the standard official build for C#: only the dedicated .NET build has it.
  Neither build tested had the `dds` module either (moot: the loader decodes DDS itself).
- Isolating `DOTNET_ROOT` to the net8 runtime alone: breaks SDK discovery.
- Re-registering an `EditorSceneFormatImporter` for `.flver` to fix concurrency "properly":
  Godot exposes no lever for it.
- A custom `Tree`-based file browser (`mounted_browser_dock.gd`): the expand arrows never worked
  in the live editor although the handler logic was correct in isolation. If one is wanted
  again, diagnose the click→signal path in a real windowed session first.
- An empty `~/godot/Boletaria/` directory after the rename was a harness artefact.
- Live-pausing RPCS3 to catch one draw.

**Import and textures**
- Porting the old OBJ exporter's winding swap or V-flip: if inside-out geometry or misaligned
  textures reappear, remove a compensation, do not add one.
- `DrSwizzler`/Soulstruct's format-generic PS3 deswizzle on DXT textures: makes lightmaps worse.
  DeS does not swizzle its DXT formats (`Headerizer.Headerize`'s own comment). Format 10 is
  swizzled in the file, but `Headerizer` already deswizzles it: never deswizzle its output again
  (2026-09-27: doing so striped the env term on m02).
- Removing `GenerateMipmaps` from the uncompressed cubemap path: no effect.

**Lighting and output**
- `LIGHT_BANK`'s diffuse hemisphere pair (`colA_du`/`colA_dd`), flat or lightmap-gated: three
  attempts, each flattened or beige-washed the Nexus. Treating the pair and the directional
  trio as alternative primary lights is disproven (r = −0.15).
- Adding `LIGHT_BANK`'s directional lights to the map (HemEnv) family: the engine applies none.
- Clamping `env_intensity` to [0, 1]: dulled every other map piece.
- An ambient or tonemap `WorldEnvironment` (`Adjustment*`, `AmbientLight*`, a
  `grayKeyValue → TonemapExposure` mapping): the output stage runs in the material shaders.
- Texture-driven `StandardMaterial3D.Emission` for glow.
- Roughness/metallic on the lightmap family.
- A fixed or load-time-estimated adapted luminance: superseded by real adaptation.
- In-shader sRGB→linear on env cubemaps, or any colour-space conversion: DeS fetches without
  gamma and Compatibility applies none.
- Fitting scattering constants (`scatter_distance_scale`, wavelength weights, a scalar
  in-scatter divide): the constants are computed exactly now.
- A shadow projection that follows the camera (perspective or texel-snapped orthographic):
  shadows swim or slide. Only the fade follows the camera.
- Culling map pieces by `DrawGroups`/`DispGroups` with the DS1 rule: deletes most of m02.
- `row = PointLightID mod 64`, and positions from anchor pieces: the row is `UnkT04` and the
  position is the region.

**Effects**
- Eager map-wide effect instantiation: use the budgeted preview.
- Placing effects or lights at a part's mesh centre.
- Replacing Param66 with Param38 merely because a template assigns runtime slot 0 (resolved
  since: Param66 is a parent-context reference to that slot).
- Brightness or density multipliers to make effects look right: implement the missing native
  semantics instead.

## Recent sessions (kept in full)

### Map-piece atmosphere and light-direction review from the executable (2026-09-25)

First map-lighting pass done directly against the game executable (Claude, Ghidra plus
objdump's Cell/AltiVec mode, since Ghidra's 64-32addr language stops at Cell-only vector
loads). Findings and addresses are in the external research archive
(`ELF_ENGINE_ACCURACY_RESEARCH.md` section 12); implementation notes are in
docs/ARCHITECTURE.md's "Rendering pipeline".

- **Scattering.** The `LIGHT_SCATTERING_BANK` object precomputes Hoffman-Preetham Rayleigh
  and Mie coefficients for λ = 650/570/475 nm at construction; the per-row builder scales them
  by `lsBetaRay`/`lsBetaMie` into `c104`/`c108`/`c109` and packs the other fields. Computing
  them this way reproduced the captured m01 and m02 `c104` to every printed digit, and
  explained part 35's "c108 = 0 at the Shrine" (c108 is Rayleigh; m03 has `lsBetaRay` 0), so
  part 35's Rayleigh/Mie labels, and the shader weights built on them, were swapped. The
  vertex program (RPCS3 `VertexProgram110`) gave the rest: radial distance, `exp2` with
  `log2(e)²`, `(1 + cos²)` Rayleigh phase, HG with a `(1 + g)` denominator term.
- **Fog.** The builder packs `(fogBeginZ, fogEndZ − fogBeginZ, 0, degRotW/100)`; the upload
  inverts the range (captured `c103.y = 1/(end − begin)`), and the vertex program applies it
  to `clip.w`, i.e. planar depth. The fragment program multiplies `saturate(ramp)` by
  `degRotW/100` with no clamp afterwards.
- **Directions.** `LIGHT_BANK`, `LIGHT_SCATTERING_BANK` and `SHADOW_BANK` share one
  builder convention (travel direction `(cos X sin Y, −sin X, cos X cos Y)`); the HemDir3
  capture's `max(−N·c, 0)` confirms the uploaded vector is the travel direction. Our
  `SunDirection` was rotated 180° in azimuth for all three.
- **Env specular slot.** The env cubemap binder reads a per-material integer and binds
  `LIGHT_BANK` field `envSpc_<slot>`; `g_EnvSpcSlotNo` is now bound.
- **Checked and unchanged.** Point-light falloff and Lambert term match a captured HemEnvPntS
  program. `HemEnvLerp` is a two-row cubemap transition. Exposure and point-light selection
  are per-frame runtime state.

Verification: build 0 errors / 19 pre-existing warnings; opengl3 check 0 `SHADER ERROR`
across lightmap, terrain, water and sky shaders; the m01 row-0 binding printed
`scatter_extinction = (0.007627, 0.011513, 0.020976)` against the captured
`(0.00763, 0.01151, 0.02098)`. Before/after renders of Boletaria and the Nexus exterior show
more distance haze than the old fitted stage (Nexus frame mean 55.9 against 43.9). Not
compared with RPCS3 yet.

### Frame exposure adaptation and bloom from the post chain, captures and executable (2026-09-25)

The user asked for exposure and bloom in one pass, on the premise that the remaining
differences are gaps on our side. Implemented as `PostProcessPipeline` (see ARCHITECTURE.md's
"Frame post-process"). What made it tractable, and what it
turned up:

- **The chain is the DirectX SDK "HDRLighting" design** (same pass names), with FromSoftware
  changes: the adapted quantity is `E` itself, and the bloom runs at twice unit weight. All
  constants came from the `DS_Fil_*` disassembly plus the per-draw inline constants of the
  captures; the rate constant `-0.87438947` is `30·log2(0.98)`, and `CalcAdaptedLum`'s rate
  input equals `adaptSpeed × frame time` in every capture (0.0833 = 5/60 on m01, 0.0500 =
  3/60 on m02, with the jitter of a measured `dt`).
- **Render-target formats and filters** come from the capture registers the replay tool did not
  track yet (`SET_SURFACE_FORMAT` colour bits, `SET_TEXTURE_FILTER`, `SET_TEXTURE_IMAGE_RECT`):
  luminance targets are `F_X32` with nearest filtering, bloom targets `A8R8G8B8` (so the
  1.976-gain bloom passes clip), the 320×180 copy and the composite's bloom read bilinear.
- **RPCS3 captures hold the game's own images.** With colour-buffer write-back, the memory
  blocks attached to a draw contain its render-target textures, as they were at their first
  use as a texture that frame. That gave ground truth: our stages on the captured Nexus buffer
  give `L` 0.09979 vs the game's 0.10021, and bright-pass through the second Gaussian matches
  the game's 160×90 target to within one 8-bit level. The captured "bloom" texture is the
  pre-bloom Gaussian (first use: the vertical bloom pass reads it), which is why it first looked
  3.9× weaker than ours.
- **The game camera is in the capture too:** origin map pieces carry world translation `−eye`
  (five objects' captured translations land on their MSB positions to 0.00), `c4`-`c6` are the
  view rows, the clip-w row is the view direction, FOV `2·atan(c5.y/c1.y)` = 43°. The m02
  capture's framing reproduces exactly in Godot; the first m01 capture does not (our m01 load
  has no colonnade where the game draws one - a geometry question, not the camera).
- **Scene radiance is the real gap** (measured before the 2026-09-26 fixes). At identical cameras our m02 scene buffer is 3-5× darker
  on geometry and ~18% brighter in the sky than the game's; at the bright Nexus capture our
  log-average scene luminance is 0.030 vs the game's 0.1575 (the game's histogram centres on
  0.10-0.20, ours below 0.05). The previous "extremely accurate" A/B was by eye with a fixed
  `E`; the measured comparison says the lit geometry is far too dark and the light-shaft quads
  and sky too bright - consistent with the user's "haze looks brighter than the PS3".
- **Tone rows are frame-global.** The executable's tone-map row builder interpolates two rows
  by a factor; its caller reads the row pair and factor from a per-frame state object (with an
  override pair used for cutscenes, whose rows are named for event scenes) and passes the frame
  time on. The writer of that state was not traced. Collision parts carry their own
  ToneMapID/ToneCorrectID, different from the map pieces' on several blocks, so the stand-in
  uses the block's most common collision rows.
- **Godot facts established by scratch probes under `opengl3`:** `CAMERA_VISIBLE_LAYERS` works
  in Compatibility fragment shaders (and has to be read through a macro so it expands inside
  `fragment()`); a `global uniform sampler2D` accepts a `ViewportTexture`; `use_hdr_2d` gives
  an RGBA16F target with negatives intact; a never-cleared SubViewport with a `blend_mix`
  ColorRect accumulates across frames; nested SubViewports render children first with no
  frame lag. Float16 in the blended exposure target stalls `E` within ~0.5% of its target
  (m02: 1.7812 vs 1.7823).
- **Superseded:** part 48's fixed `clamp(0.10, min, max)` (the captured values it matched are
  the floor case of the real formula) and part 56's "park adaptation behind a player camera"
  (the editor camera exercises it; the capture data verified it). The glow-only
  `WorldEnvironment` bloom (part 34) is removed.


### Effect and unlit materials in the scene buffer (2026-09-26)

User report after the post-process landed: the Nexus is close, but at some angles the bloom above
the view is stronger and wider than the PS3's, and the Nexus ground fog and m03_01's sandstorm
become far more prominent, apparently through the post-processing. Traced to our side:

- **The measurement camera saw effects at twice the game's buffer value.** Only the lit
  families, water and sky wrote the buffer encoding; `vfx_scroll`, unshaded `StandardMaterial3D`
  and the SFX preview wrote their displayed value. The captures' own programs settle what the
  game writes (research 14): unlit map meshes use `DS_Phn_Dif________________Non`, the full
  fog → scattering → `E/2` epilogue; sprites halve their colour, and Type0 alone then applies
  `E/2`. The bright-pass subtracts its threshold before the blur, so a doubled fog layer blooms
  across its whole area where the game's does not, and the doubled luminance lowers `E` for the
  rest of the scene, which makes the effects stand out further.
- **The display side was also wrong for unlit map meshes:** no exposure, fog, scattering or tone
  correction. With `E` 1.15-1.77 on the Nexus and the m01 tone rows darkening by ~15%, the
  light-shaft and fog quads read brighter relative to the scene than the game's.
- **Additive draws took the tone offset twice.** The game applies `M` once to the blended
  buffer; an additive term blended onto an already-corrected colour must carry `M(b) − M(0)`.
- Fix: `des_encode`/`des_encode_additive` in `output_stage.gdshaderinc`; all unlit
  blended/additive map materials routed to `vfx_scroll` (with a `cull_back_faces` uniform so
  they keep two-sidedness); `apply_exposure` for Type0 sprites in `SfxPreview`. Not yet
  reproduced: the sprites' per-vertex fog/scattering factors; the remaining unlit
  opaque/alpha-test/subtractive `StandardMaterial3D` materials still write their display value.
- The captures also show `DS_Fil_Dof*` and `DS_Fil_CameraBlur*` passes on every Nexus frame, not
  reproduced; a candidate for remaining differences in the sky.


### Point-light positions and rows, and the effect distance fade (2026-09-26)

Two user reports from a PS3 comparison: the Nexus dry ice and similar map fog fade out when the
camera reaches the middle of the effect, which the game does not do; and the m02 bridge fountain
(`m2501B0`) is lit as if by a bright white light hovering over its centre.

- **Action104's fade never reaches billboard sprites.** The fade object (research 10.7) writes
  `base alpha × fade` into the effect instance's second colour word. The per-frame updater that the
  ordinary billboard-cluster path installs sends the instance's *first* colour word with its
  transform command; a different updater class, installed by another render path, sends the faded
  word. So the fade is not applied to Setup71 clusters (research 14.5), and `SfxPreview` now only
  notes an action104 instead of fading every layer.
- **Point lights: `PointLightID` is a region index, `UnkT04` is the bank row.** Every Light event
  in the corpus indexes an MSB region carrying its own name (m02 `焚き火　橋の上２番`, m04_01
  `fire_06`, m05_01 `炎_01` …), exactly as SFX events use `UnkT00`. Capture 094528 (m02) binds a
  second light to 191 draws at eye + `c23` = `(12.2, 21.1, −121.5)`, region 125's position to the
  printed digit, with colour/range `(1, 0.863, 0.588)`, 2–7 m = m02 row 0 = that event's
  `UnkT04`. The m08 capture 205056 has a brazier at m08 row 0 (`(2, 1.176, 0.392)`, 1–5 m),
  again its `UnkT04`. Row names follow: m01's "デモンズソウル" light is `UnkT04 1`, the blue
  1–15 m row; m08's mini torches carry −1 (no light).
- **Part 52's `row = PointLightID mod 64` was a coincidence.** Its one capture match was row 10,
  which every point-lit draw in every capture carries at ~4 m in front of the camera: the
  player's own light (m02 row 10, m08 row 10), not a map torch. The position heuristic from
  part 55 (anchor piece's smallest sub-mesh) put m2501B0's four campfire lights, resolved to
  white placeholder rows 60–63, on the fountain statue. That was the fountain glow.
- Fix: `MsbLoader.ReadPointLights` resolves the region (shared `EventRegion` with SFX) and keeps
  `UnkT04`; `FlverLoader` no longer instantiates anchor pieces. Checked by rendering the fountain
  under opengl3 before and after. Lights anchored to `Collision` parts (all of m08) now resolve
  too. Still not reproduced: the player's light and the engine's per-frame light selection.
- **Point lights skip map pieces (same day).** The user knew from the game (and the "PC Remaster"
  mod, which turned maps into objects to get torch light on them) that point lights light props,
  not map geometry. All eight RPCS3 captures agree. Classifying each point-lit draw by its
  world matrix (map pieces share the frame's identity rotation and `−eye` translation) and
  matching its fragment constants against the bank rows: every map-piece draw is
  `HemEnvPntS` carrying one light, the player's (m02 row 10, Nexus row 63, rows no light event
  names). MSB lights appear only on objects and characters: in 094528 the bridge campfire
  (region 125, m02 row 0) is on 393 object draws and the characters, while the bridge's own
  map-piece draws in its range carry only row 10; objects reach `PntSS`/`PntSSSS`, map pieces
  never pass `PntS`. The warm light on the bridge walls near the campfires is baked into the
  lightmaps. `FlverLoader` now binds MSB lights to objects only; `PointLightsOnMapPieces`
  (default off) restores map-piece lighting as a deliberate departure. The selection mechanism
  in the executable is not traced.

### Sky-dome scattering and part rotation order (2026-09-26)

User report after testing against RPCS3 on m02: exposure and bloom far stronger on the sky than
in the game (bright, sometimes colour-boosted), and some props apparently lit from the wrong
direction. Both came from data in the captures; no ELF work.

- **Sky-dome scattering.** In capture 094438 the four `DS_Phn_Dif________________Non` sky draws
  carry the frame's `LIGHT_SCATTERING_BANK` constants `c104..c111`, the same as the lit
  geometry, with FOG_BANK row 3 in `c103` (`50`, `1/50`, `0.3`) and diffuse multipliers
  `c1 = c2 = 1`. The `sky.gdshader` note that the sky's scattering constants were unknown and
  the dome "over-attenuates" dated from the fitted scattering, before part 46 and section 12 made
  it exact. The shader already computed the per-vertex terms and dropped them in `fragment()`;
  it now calls `des_output`. Checked by rendering the 094438 camera under opengl3 and comparing
  three sky regions with the game's final frame (extracted from the capture's colour-buffer
  write-back): before, 1.1–1.6× too bright, with the top clipping to near white
  (`221,250,216` vs `140,157,133`); after, 3–10% below the game (`134,152,125`). A brighter sky also
  raised the bright-pass input, which is why the bloom looked strongest there.
- **Part rotation order.** Matching every captured draw's world rows against MSB parts by
  position (all eight captures, all `m0*` MSBs): each object rotated about two or more axes fits
  `Ry(β)·Rz(γ)·Rx(α)`, the order section 9.5 of the research found for regions, to within 7e-7
  (26 objects in 094221, 094528, 094648, 094742 and 094931), while Godot's default YXZ misses by
  up to 1.9 (m03's `o3415_1001` `(131.3, −40.5, 76.8)`). The only exception, `o0300_0001`, fits
  neither order; it is presumably a physics-posed object. The previous "sign flip spot-checked
  but not proven" caveat only ever covered pure-Y rotations, where the order does not matter.
  `FlverLoader.InstantiatePlacement` now sets `RotationOrder = EulerOrder.Yzx` with the existing
  mirrored angles `(α, −β, −γ)`. A headless probe that mirrors the resulting Godot basis back
  reproduces the captured matrices of the three most-rotated m03 objects to within 5e-7.
  `MsbLoader.ReadRegionBoxes` used the same default order for the camera-region boxes and now
  uses Y-Z-X too. Mis-oriented debris and rock props read as wrongly lit, because the env cube,
  hemisphere and shadow all follow the rotated normals.

### Effect primitives, emitter curves and swizzled ARGB textures (2026-09-27)

Research in `~/godot/research-dump/ELF_ENGINE_ACCURACY_RESEARCH.md` section 19. Census of every
distinct MSB-placed effect per map (headless, `SfxLoader.InstantiateLayerPreview`): 121 of 138
built before, 129 after. Of the other nine, three are IDs that exist in no bank and six build
no layer.

- **Primitives.** 98 omissions were geometry primitives: action43 61, action20 25, action61 5,
  action3 4, action24 3. The archived Lua names them (`SetPostEffect`, `AssignBillboard`,
  `AssignRenderModel`, `AssignLight`); the compiled dispatch table is static data, so every
  handler resolves directly. Action20 is the fires' far-distance impostor (texture 16, a flame);
  its X/Y order follows the Lua and gives an upright flame on the square texture.
- **Distortion constants from captures.** `DS_Sfx_DistortionType1` draws in 172216, 094648,
  094742 and 205056 carry commoneffects 100/101/113's exact arguments (strength 5, colour
  alpha 40/255 and 20/255, scroll 0.02/0.05), which fixed the argument-to-constant map. Vertex
  buffers from `capture_draw_memory` gave the quad (±0.5, UV (0,1) at top-left); the method
  registers gave SRC_ALPHA/ONE_MINUS_SRC_ALPHA with depth test on, depth write off. A
  zero-strength render reproduces the background exactly, so the screen copy keeps the
  scene-buffer encoding. These need a scratch variant of RsxShaderMatch (patched FP disassembly,
  blend and vertex-array dumps); the repo tool is unchanged.
- **Format 10.** Bump 22 decoded as vertical stripes through Pfim. First misdiagnosed as a
  missing deswizzle (the raw TPF bytes are in RSX Morton order, and the GPU memory of m02's
  EnvDif cube in capture 094438 is byte-identical to them, format register without the LN
  flag), and a deswizzle was added to both loaders. That double-swizzled the cubes: the user saw
  dark bands on m02 terrain, and the render put hard stripes on the sacks and a dark wedge on a
  grass slope that the game shades smoothly. The fork's `Headerizer` output already equals the
  deswizzled raw bytes on every texel (cube and bump 22); its DDS header declares 24-bit RGB,
  which is what Pfim misread. Fix: read the `Headerizer` payload as A,R,G,B (as the cube path
  always did). Checks that did not find it: face-edge continuity of the deswizzled cube, Godot's
  cube face convention (probe: identical to D3D), the capture's world-space normal in the
  HemEnv VP, eight up-preserving cube orientations and a no-bump render; all were run on the
  wrong input. The lesson: compare against what the code actually decodes, not the file bytes.
- **Distortion orientation.** Seven captured draws of commoneffects 100/101/113 (arg2 = 1)
  measure 1.20 × 0.75 exactly and turn the quad normal toward the camera position, within 0.7
  degrees of the view ray even off-centre, X horizontal: not the view-plane billboard first
  implemented. 82511 (arg2 = 0) stays in its container's plane; most other fog gates set arg2 = 1.
  At strength 0 in the m02 gate hall the screen copy reproduces the scene within 7/255 (no shift).
- **Fog-gate faults (second report).** The m02 medium gates (82513, 82521, 82553) create their
  distortion as a template2023 startup child; it ignored the emitter's action35 lift (2 m), so a
  4.2 m quad sat half below the floor. On 82511 (o2221) a distortion tile drawn after the fog
  replaced it with the fog-free screen copy (alpha `colour.a · 2^r` >= 1): a dark square. The
  distortion now draws first (`RenderPriorityMin`).
- **Water shoreline.** The m02 river had no coastal fade. `DS_Water`'s output register 13 is
  `TEX6` (RPCS3's output table), written straight from the vertex colour; `DS_Water_Env` scales
  the refraction offset and the body mix by its alpha, multiplies the surface by its RGB, and
  fades to the body below `g_WaterFadeBegin` (captured `c46`/`c48` = 0.7 and 1/0.7, the MTD
  value). River bank vertices carry RGB 0.5 (all 163 of m1998's are on the mesh boundary). The
  depth-buffer band, the screen reflection tap and `bias + scale · pow` were stand-ins and are
  gone. The glint light `c22` (40°, 75°) is `LIGHT_BANK` directional light 0, not the scattering
  sun `c12`. The refraction tap is decoded (inverse tone matrix, then `/E`) as the engine
  decodes its scene buffer.
- **Stale items closed.** The action35 axis conflict noted on 2026-09-24 was fixed by
  `1dee186` (`PlacementTransform` builds `Ry(−B)·Rx(−A)·Rz(−C)`, the traced native order in
  mirrored space). The `m0000B0` overexposure is downgraded to a potential issue: the area is
  m01's ending arena, absent from every capture, and the report predates the exposure pipeline.
- **Bump orientation.** The earlier note "TBN roles swapped" was naming only: the HemEnv bump
  program puts Y along the FLVER tangent and X along `cross(N, T)·w`, and on all 114 bumped
  m02 pieces (320,813 triangles) those are the +V and +U UV gradients (mean dots 0.94, 0.95).
  The real fault was the sign: a probe shader comparing Godot's `BINORMAL` with the
  screen-derivative `dP/dv` gave −V on every pixel (`TANGENT` +U on every pixel), so all bump
  relief was lit upside down. Fixed in `hemisphere_pixel_normal`; the m02 parapet now catches
  the hemisphere light on its upper rims. Water takes its wave normal as world `(x, z, y)` as
  `DS_Water_Env` does, instead of through the tangent frame.
- **Emitter curves.** Five effects (99030 in every map; 13070, 95200, 95300, 96101) curved only
  emitter speed/size and action55 gravity. Emitter handlers evaluate their sequences when they
  run, so per emission; gravity follows motion84's cluster clock (section 10.5), inferred for
  action55. 99030's two template2020 children still skip: their curved radius and rotation move
  the child-effect frames.
- **Main bank.** m08 places 520/540/560/580, which exist only in `ds_sfxbnd_main`.

### Havok packfile reader and map collision (2026-09-27)

The user chose to build Havok support into the fork one content type at a time, collision
first, with the 2018 kata branch and soulstruct-havok (from the `io_soulstruct` Blender add-on
zip, `soulstruct-havok/src/soulstruct/havok/types/hk550`) as references.

- **Census of `mounted/`:** 7,625 `.hkx` files: map collision (2,918), object physics (846,
  `hkpPhysicsData`), character skeletons, animations and bindings (5,128), two in `Animation/`.
  Versions 5.5.0 (most), 5.1.0 (m02, m03, m07 and m99 collisions; some chr) and 4.x (a few
  chr/obj/m99). One chr file is little-endian.
- **No type descriptions.** An earlier assumption that the files describe their own layouts was
  wrong: `__types__` is empty in every file inspected, and 5.1.0 files put `__data__` before
  `__types__`. Layouts come from soulstruct-havok's 5.5.0 classes; the same offsets decode the
  5.1.0 files.
- **Which files are walkable:** every MSB collision part names an `h` file; `l` files (with both
  `b` and `B` spellings) are never referenced. `m07_02` and `m99_99_98_05` name files that are
  not on disk (unused blocks).
- **Checks.** Scratch Python prototype and the C# reader agree on all map `h`/`l` files: 6,759
  submeshes, 3,818,526 triangles, every index in range, every vertex finite (1.7 s for the
  whole set). Downward/upward ray casts (4,000 per map) hit front faces mostly from above on
  m01, m02, m03_01 and m05 (m02: 1,386 down, 441 up), which fixes the winding swap; m08's
  collision encloses the whole map, so every ray hits both ways there. Rendered overlays
  at the 094438 camera and an elevated m02 view line up with the parapet, battlements, stairs and
  ballistae; m08's apparent offset in an overlay was an uncollided roof hiding the rooms below.
  On m08, 14 of 23 `h`/`m` model pairs have identical local bounds, confirming the shared
  transform. Collision adds 0.15-0.36 s to a 6-7.5 s map load.
- **Follow-up (same day): convex shapes, the fourth index word, surface types.**
  - soulstruct-havok has no 32-bit layouts for `hkpBoxShape`, `hkpConvexVerticesShape` or
    `hkpConvexTranslateShape`; their offsets were read from the two retail map files and
    hold across the objects. The first hull fans used a tolerance scaled by world
    coordinates (4 cm at m03_01's 410 m) and merged adjacent faces; on-plane residuals are
    below 1e-4 and the nearest off-plane vertices 1e-3, so 0.5 mm is fixed. All map hulls
    then close; 50 of 966 object hulls still fan wrongly (the object step).
  - Winding: the first box and hull faces were wound counter-clockwise, the opposite of the
    storage meshes (whose (b − a) × (c − a) points down on 900,220 faces and up on 468,651).
    Flipped; ray probes on the m03_03 box (hit from above and below) and the m03_01 hull (hit
    from outside, missed from inside) confirm it.
  - The fourth index word of extended storage meshes is exporter garbage: after 0, the most
    common values are `0xCDCD`, `0x3F80` (the top of float 1.0), `0xFFFF`, `0xDDDD`; 27,361
    distinct values. soulstruct-havok's "face flags" reading of it is not supported.
  - Surface types: no DeS paramdef names map materials (DS1's `HitMtrlParam` has no
    counterpart). Matching each collision triangle to the diffuse texture of the nearest
    render triangle within 1 m, over 15 main blocks, reproduces DS1's scheme (table in
    ARCHITECTURE.md). Collision is now split per surface type with `surface_type` metadata.
- **Object collision (same day).** The reader now returns shapes rather than triangles
  (`ReadCollisionShapes`), and object placements get static bodies from `obj/<id>/hkx/<id>.hkx`.
  - Census of `obj/`: 846 files, 817 Havok 5.5 and 29 Havok 4.x (none placed in a retail MSB;
    checked by name against every retail `.msb`). 5.5 classes not read before:
    `hkpConvexTransformShape` (1,868), cylinder (31), sphere (28), capsule (12). Offsets read
    from the bytes: transform child +24, `hkTransform` +32; capsule ends +32/+48; cylinder core
    radius +20, ends +32/+48, whose w lanes equal core + convex radius (o0141: 0.7263 + 0.05).
  - `<id>_1.hkx` is the broken state of a breakable object: the base file holds one or a few
    fixed bodies (motion type 7), `_1` 10–90 box-inertia debris bodies (4, some 2). Base files
    also hold keyframed (6, 615 bodies) and dynamic (4, 507) bodies; all are placed static.
  - Hulls from planes, dropped: of 969 hulls, 254 failed an edge-pairing check, mostly because
    one face is stored as several coplanar planes (o2322: 8 vertices, 12 planes). The hull is now
    handed to Godot as points; the old fan code is gone.
  - Rotation storage: `hkTransform` columns, confirmed in file space against FLVER vertices
    for objects with rotations of 30–180° (o1616 88% inside its box against 30% for rows,
    o6472 53% against 14%, o5887 23% against 9%; others equal). Godot fits on m05 match the
    file-space ones (o5600 49% and 46%), so the S·R·S mirror is right.
  - Checks: all 3,764 map and object files read with no skipped shape; 10 degenerate hulls
    (under 4 points) only in `_1` files. Object bounds centres agree with the FLVERs for 369/374
    (m02) and 876/894 (m04) bodies; the rest have no visible mesh (invisible walls such as
    o2511) or cover part of the object (o4421's top slab). Object collision adds 12–62 ms per
    map. m02 map collision is unchanged (71,643 triangles, 1,380 downward hits); the m03_01 hull
    probes give the same hit points as before.
- **Not yet read:** object simulation (motion types, mass, constraints), Havok 4.x files and
  everything animation-side.
