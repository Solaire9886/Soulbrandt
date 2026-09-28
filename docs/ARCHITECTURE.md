# Architecture

Reference for how Soulbrandt works today: what it is, how to build and verify it, its
conventions, the reconstructed rendering pipeline, and the known gaps. `docs/PLAN.md` is the
roadmap. `docs/context.md` is the investigation history: how each fact here was established,
the wrong turns, environment quirks and dead ends. `AGENTS.md` is the agent pointer file.

Detailed research write-ups (executable behaviour specifications, VFX audits) live in an
external research archive that is not tracked in this repository; they are cited by document
name. Ask the maintainer for one if needed.

## What this is

A Godot 4.7 (.NET/C#) project that reads **Demon's Souls (PS3)** data (FLVER0 meshes, TPF
textures, MTD materials, MSB maps, draw parameters, FFX effects) directly into Godot, as the
first phase of a full recreation (`docs/PLAN.md`). No game assets are bundled; `mounted/`
(gitignored) holds files extracted from the user's own copy (see "Asset mounting").

The repository (`github.com/Solaire9886/Soulbrandt`) is public under **GPLv3**, required
because it links `SoulsFormatsNEXT` (GPLv3, no linking exception). `README.md` carries the
disclaimer (no assets, no piracy support, no decryption methods); `CONTRIBUTING.md` covers
contributions. The C# assembly and csproj are both `Soulbrandt`.

## Clean-room rule for native engine behaviour

Some engine behaviour (how the game interprets its data) exists only in the game executable.
Since 2026-09-24 it may be studied under a clean-room split:

- **Research side, outside this repository.** A contributor may read and disassemble an
  executable they extracted themselves from their own copy with RPCS3's built-in decryption
  (`EBOOT.BIN` → `EBOOT.elf`). Read-only: the ELF and the original are never modified, and
  patched code is never run. Results go to the external research archive as behavioural
  specifications (formulas, constants, state rules, selection logic), labelled observed or
  inferred, with the ELF's SHA-256 recorded.
- **Repository side.** Code is written from those specifications, never transcribed from
  decompiled output. The repository never contains the executable, disassembly listings,
  decompiled code, function addresses or keys, and documents no decryption method. Citing a
  specification document by name is fine.
- **Captures and data still verify.** Where a specification can be checked against shipped
  data (a corpus census) or an RPCS3 frame capture, check it.

Soulbrandt still only reads formats from the user's own copy at run time and ships no game
code, assets or decryption.

## Build & verify

- **.NET:** Godot's C# tooling requires a `net8.0` SDK (no cross-major roll-forward). Install
  a .NET 8 SDK alongside any newer one; no `DOTNET_ROOT` override is needed.
- **Build** after any change under `addons/archstone/`: `dotnet build Soulbrandt.csproj`.
  Do not use `godot-mono --headless --build-solutions`; it hangs.
- **Clone** with `git clone --recurse-submodules` (or `git submodule update --init`).
- **No reimport step and no unit test suite.** `.flver` is not a Godot resource type; models
  load only through `FlverLoader` (see "Code layout").
- **Headless checks:** throwaway `SceneTree` scripts, e.g. `godot-mono --headless --path . -s
  check.gd`, where the script calls
  `load("res://addons/archstone/scripts/FlverLoader.cs").new().Instantiate("res://mounted/...")`
  and inspects the node tree. Keep them in a scratch location and delete them afterwards.
  Redirect output to a file: a script that errors before `quit()` runs forever and a piped
  `grep` shows nothing.
- **`--headless` never compiles GLSL.** Its dummy driver accepts invalid shaders. Godot's own
  shader parser does run at load (so `get_shader_uniform_list()` catches language errors),
  but GLSL compile errors need a real driver: `xvfb-run -a godot-mono --path . -s check.gd
  --rendering-driver opengl3 --display-driver x11`; errors print as `SHADER ERROR`. This runs
  Mesa llvmpipe and is heavy: **never run it while the GUI editor or another Godot process is
  open against this project** (a concurrent run once OOM-killed the editor).
- **Shader-language limits worth knowing:** built-ins (`ALBEDO`, varyings) can only be
  assigned inside `vertex()`/`fragment()`, never from a helper; some built-ins
  (`MODEL_MATRIX`, `FRONT_FACING`, `CAMERA_VISIBLE_LAYERS`) are not readable from helpers and
  are passed in or read through a macro; `CUSTOM0` is readable only in `vertex()`; uniform
  defaults must be literal constants.

## Code layout

| File | Role |
|---|---|
| `scripts/archstone.gd` | Editor plugin: the Archstone toolbar menu (mount, import, load, reload, debug overlay). |
| `scripts/AssetExtractor.cs`, `extract_cli.gd` | Unpacks the user's game copy into `mounted/` (editor action or headless CLI). |
| `scripts/FlverModelBuilder.cs` | FLVER0 → `ImporterMesh` with resolved materials and textures. No Godot import system involvement. |
| `scripts/FlverLoader.cs` | Session mesh cache; `Instantiate`, `InstantiateWithDefaultDrawParams`, `InstantiateMap`; per-placement draw-parameter binding. |
| `scripts/MsbLoader.cs` | MSB parsing only (parts, point lights, SFX events, region boxes, tone IDs). No scene nodes. |
| `scripts/DrawParamReader.cs` | Draw-parameter bank rows and env cubemap names. |
| `scripts/ShaderLibrary.cs` | Reads the shader library's names as data (material → shader family/features). |
| `scripts/ShadowRenderer.cs` | Static sun-shadow depth pass per loaded map. |
| `scripts/MapCollision.cs` | MSB collision and object parts as static bodies from their Havok shapes. |
| `scripts/PostProcessPipeline.cs` | Exposure adaptation and bloom (the game's post chain). |
| `scripts/SfxLoader.cs`, `SfxPreview*.cs`, `SfxBatchParticles.cs` | FFX effect reading and billboard playback. |
| `scripts/MapSfxPreview*.cs` | Per-map effect controller (MSB events, object and camera-region sources). |
| `scripts/DebugOverlay.gd` | Diagnostic text overlay for the editor viewport or a game. |
| `shaders/` | Material families, the shared output stage, post-process stages, effect shaders. |
| `tools/RsxShaderMatch/` | Standalone console tool: shader-binary decoding, RPCS3 capture reading. Excluded from the Godot build. |

`AssetExtractor`, `FlverModelBuilder`, `FlverLoader` and `MsbLoader` have no `EditorPlugin`
dependency; only `archstone.gd` is editor-only.

**Why there is no `EditorSceneFormatImporter`.** Registering one for `.flver` made every
filesystem scan push the whole corpus through Godot's threaded reimport queue, which caused a
hang/crash cycle that a plugin cannot control (no concurrency hook exists, and the worker-pool
setting is ignored in the editor). Since 2026-07-24 nothing routes `.flver` through Godot's
import system; extraction never triggers a filesystem scan.

**Loading from the editor.** "Load Model(s)..." and "Load Folder..." (`EditorFileDialog`,
rooted at `mounted/`) call `InstantiateWithDefaultDrawParams` per file, which binds row 0 of
each `default_*` bank so a standalone model is lit. "Load Map..." (rooted at
`mounted/map/mapstudio`) calls `InstantiateMap` per `.msb`. "Reload Loaded Models" calls
`FlverLoader.EvictAll()`, which also clears the builder's texture caches. Every added node
gets `.Owner` set recursively (`_set_owner_recursive`), or a scene save drops it.

## Asset mounting

"Mount..." validates and remembers a raw game root (`.../PS3_GAME/USRDIR`) in
`user://archstone_mount.cfg`. "Import" (full or chosen categories) runs `AssetExtractor`,
which unpacks `.bnd`/`.dcx` containers in-process with `SoulsFormatsNEXT`'s `DCX`/`BND3`/
`BND4` readers (DeS compression is `DCX_EDGE`) into loose files under `res://mounted`.
Extraction is idempotent (mtime skip) and limited to `AssetExtractor.KnownCategories`:
`chr`, `map`, `obj`, `parts`, `mtd`, `param`, `paramdef`, `shader`, `sfx`. "Clear Mounted
Assets..." deletes `res://mounted`. Headless:
`godot-mono --headless --path . -s addons/archstone/scripts/extract_cli.gd -- --raw-root=<path>
[--categories=chr,map]`.

BND entry names carry a `data/DVDROOT/...` prefix that is stripped. Entries without it
(`shader`) and `sfx` entries (whose internal paths collide between banks) go through
`FallbackEntryOutputPath`, which mirrors the container's own location and names a folder after
it; a trailing `.dcx` is dropped so a container's plain and `.dcx` copies land together.

Not extracted: `script/` (plain-text Lua and `.esd` state machines), `menu/`, `remo/`
(cutscenes), `msg/`, `sound/`, `movie/`, `other/`. `SoulsFormatsNEXT` reads most of them.

## FLVER import

### Coordinate and mesh conventions

These are correct as implemented; do not "fix" them.

- **X is negated** on positions and normals: FLVER space is the mirror image of Godot's. A
  mirrored import is internally consistent (winding and lighting still look right), which is
  why it went unnoticed until a map was recognised as its sibling's mirror image.
- **Triangle winding is swapped** because of that negation (mirroring flips apparent winding;
  Godot's front face is clockwise).
- **Cubemap lookups negate X** (`CUBE_MIRROR`, and in `water.gdshader`): the faces are loaded
  as shipped, in FLVER space. Without it lightmap-less HemEnv surfaces, which take the full env
  term, sample the opposite side (m02's tree cards in `m0202b0` rendered near-black).
- **No UV V-flip.** `Image.CreateFromData` uses the decoded texture's row order.
- **UV scale is always 1024** for DeS. The fork patches `SoulsFormatsNEXT`'s header-version
  heuristic, which picks 2048 for some files; re-check after any upstream sync.
- **Rigid mesh-to-node binding is per vertex.** A static mesh's `BoneIndices` is a small
  palette of `FLVER.Node`s and each vertex's first bone index selects its node; `BuildMesh`
  composes each node's transform through its parents and applies it per vertex in FLVER space
  (`GetRigidNodeTransforms`). Without this, decorations sit at the origin. Skinned meshes
  (`UseBoneWeights`) are left in bind pose.
- **Missing vertex channels are real.** Some meshes' layouts omit normal/UV/colour (e.g.
  `m9999b0`, `m9900`, `o9996`); the builder substitutes defaults, and requests
  `Triangulate`'s flip check only when normals exist.
- **`ImporterMesh` never renders on its own.** `FlverLoader.Instantiate` converts it with
  `GetMesh()` into an `ArrayMesh` in a `MeshInstance3D`. `ImporterMeshInstance3D` outside the
  import pipeline is invisible.
- **Off-tree nodes have no global transform.** `Node3D.GetGlobalTransform()` returns identity
  when not in the tree. Placements are built off-tree, so anything needing a world position
  during the build composes local transforms by hand.
- Geometry is built as arrays and handed over in one `SurfaceTool.CreateFromArrays()` call;
  `GenerateTangents()` runs afterwards. **Bump Y is negated:** DeS bump maps put X along +U and
  Y along +V (the fragment program's Y axis is the FLVER tangent, X is `cross(N, T)·w`;
  measured on every bumped m02 piece), while Godot's generated `BINORMAL` points along −V.
  `hemisphere_pixel_normal` subtracts the Y term. The lightmap UV of blend materials rides in
  `Custom0` (`RgFloat`, passed through `ImporterMesh.AddSurface`'s `flags`).
- **Zero-mesh objects are real:** 62 of 1068 `obj/` FLVERs have no mesh (57 dummy-only
  attach markers, 5 empty stubs). Destructible-prop debris (`o6511`–`o6602`) pairs a
  dummy-only FLVER with Havok data; the visible debris likely comes from the unparsed
  `map/breakobj/*.breakobj` (magic `OBJB`).
- Not every FLVER in a map folder is placed; only MSB parts say what is (the `l1`-suffixed
  Boletarian Palace pieces are unplaced variants).

### Texture resolution

`FlverModelBuilder.ResolveTexture` tries an ordered chain of candidate directories
(`CandidateDirs`), because DeS uses different conventions per category and even per
material:

1. **The model's own container** (`OwnModelDir`): same-basename `.tpf` beside a chr `.flver`;
   `tex/` beside an obj/parts `sib/` folder.
2. **The texture's own reference path** (`RefPathDir`): the `.../tex/` segment of the
   reference string, relative to the mounted root. Covers map-area buckets
   (`mounted/map/m03/` holds ~19 shared `.tpf` files) and cross-category reuse.
3. **A sibling slot's map-area folder** (`SiblingMapAreaDir`): recovers copy-pasted stale
   references (the six Nexus archstones share one `m01` texture labelled correctly only on
   `g_Lightmap`).
4. **A map prefix in the file name** (`MapPrefixDir`, `^(m\d\d)_`).
5. **Another obj container holding the same texture** (`SiblingObjTextureDir`): a lazily
   built reverse index over `mounted/obj/`, ties broken toward the nearest obj ID.

Each directory's `.tpf` files are merged (`_dirTextureCache`); decoded textures are cached by
entry identity (`_decodedTextureCache`), and both are cleared together once decoded memory
passes 25% of available memory (`MaybeEvictDecodedTextures`). Lookups are case-insensitive.
Three shipped `.tpf` files are corrupt or empty (`o3104`, `o0050`, `o7999`) and are skipped
with a warning. Untyped texture entries get their slot from the MTD's bracket tag
(`InferMissingParamNames`, verified on 83 materials). Pfim decodes DXT; its buffer holds the
whole mip chain, so the base level is sliced out before `Image.CreateFromData`.

**TPF format 10** (uncompressed ARGB8888): the TPF bytes are in RSX swizzled order, which the
fork's `Headerizer` already undoes, but its DDS header declares 24-bit RGB, so Pfim misreads the
32-bit payload. `DecodeArgbImage` reads it directly as A,R,G,B. Format 10 holds the env cubemaps
of m02/m04/m06/m08/m99, a few 2D map textures (`m08_0808`, `m99_9700`) and the SFX bump texture 22.

**Cubemaps** (`DecodeCubemap`): Pfim decodes only face 0, so each face is re-wrapped as its own
DDS. Format-10 faces bypass Pfim (it misreports them as `Rgb24` and rotates the channels into a
rainbow) and go through `DecodeArgbImage` per face.
`Cubemap.get_layer_data()` reads back blank under Compatibility; verify cubemaps by rendering.

### Material data

Every material's `.mtd` is read once (`ResolveMtdShading`). Fields in use:

- **`g_BlendMode`** (`DesBlendMode`): 0 opaque, 1 alpha test, 2 alpha blend, 3 water,
  4 additive, 5 subtractive; 7 (two thunder materials) is treated as alpha blend. Replaced a
  filename heuristic that was wrong on 26% of materials.
- **`g_LightingType`**: 0 unlit, 1 `HemDir3` (hemisphere + three directional lights),
  3 `HemEnv` (hemisphere + environment cubemaps). Selects the shader path (below).
- **`g_DiffuseMapColor` × `g_DiffuseMapColorPower`** → `diffuse_tint`. Power is a
  **multiplier**, not an exponent (`M_Sky_light` is "sky, bright" at 1.5; `S_DiffuseX2` is
  "diffuse ×2"). Usually 0.6 or 0.5 on map materials: this is the 0.6 seen on the vertex
  colour in captured HemEnv programs.
- **`g_SpecularMapColor` × `g_SpecularMapColorPower`** → `specular_tint` on the HemEnv env
  specular term; on water it is the sun-glint weight.
- **`g_SpecularPower`**: HemDir3 Phong exponent.
- **`g_TexScroll_0`/`_1`**: UV scroll per second (diffuse/normal/specular only; the lightmap UV
  stays fixed).
- **`g_EnvSpcSlotNo`**: which `envSpc_k` cubemap of the light row a material uses (0 when
  absent, inferred).
- **`FLVER0.Mesh.CullBackfaces`**: double-sided meshes. `StandardMaterial3D` sets `CullMode`;
  the lightmap family uses `cull_disabled` sibling shaders; `vfx_scroll` takes a
  `cull_back_faces` uniform. The material cache is keyed by `(materialIndex, CullBackfaces)`.
- Every MTD also has a Japanese `Description` from the developers (`Mul` multi-texture, `Lit`
  light texture, `α抜き` alpha cutout, `半透明` semi-transparent); read it before inferring
  anything about a material family.

**Routing** (`GetOrBuildMaterial`, `ClassifyMaterial`) uses the MTD's authored shader path
resolved through `ShaderLibrary` (`Mul` = two-layer blend, `Lit` = lightmap), falling back to
texture-slot heuristics only if the `.mtd` cannot be read:

| Material | Shader |
|---|---|
| has `g_Envmap` (unique to water, incl. `a06_lava`, which authors `DS_Water_Env`) | `water.gdshader` |
| `Mul` two-layer blend | `terrain_blend.gdshader` |
| `g_LightingType` 1 or 3 | `lightmap.gdshader` family (`_alpha`, `_add`, `_sub`, `_double_sided`, `_alpha_double_sided`), `lighting_model` uniform = HemDir3 or HemEnv |
| type 0, opaque, MTD name contains "sky" | `sky.gdshader` |
| type 0 with UV scroll or alpha/additive blend | `vfx_scroll`/`vfx_scroll_add` |
| other type 0 (opaque, alpha test, subtractive) | `StandardMaterial3D`, unshaded (no fog/scattering/exposure) |

Blend mode is a compile-time `render_mode` in Godot, hence one file per blend/cull variant.
All of these shaders are `unshaded`: they compute a finished colour as DeS's material programs
do. A lit Godot shader in a scene without light nodes is lit by the editor's preview sun and
sky, which silently skewed every early comparison.

## MSB placement

`FlverLoader.InstantiateMap(msbPath)` builds a map root holding a `WorldEnvironment`, the
`PostProcessPipeline`, every `MapPiece` and `Object` placement, a `ShadowRenderer`, the map's
collision (`Collision`), the objects' collision (`ObjectCollision`) and a disabled
`MapSfxPreview`.

- **Map pieces** resolve `ModelName` case-insensitively in `map/<block>/`. Most carry an
  identity transform (their vertices are in world space); reused ones carry a real one.
- **Objects** resolve to `obj/<id>/sib/<id>.flver` (true for all 777 obj folders).
- **Transform:** position `(−x, y, z)`; rotation mirrored to `(α, −β, −γ)` and composed
  **Y, then Z, then X** (`RotationOrder = EulerOrder.Yzx`). Every multi-axis object in eight
  RPCS3 captures matches this to 1e-6; Godot's default YXZ missed by up to 1.9. MSB regions
  use the same convention.
- **Collisions** resolve to `map/<block>/<ModelName>.hkx` (extension case varies) and become
  `StaticBody3D`s with one `ConcavePolygonShape3D` per surface type, placed with the same
  transform code as map pieces (m08's parts carry real transforms). Parts reference only the
  `h`-prefixed files; the `l`-prefixed ones (both `b` and `B` spellings) are never placed. See
  "Havok collision".
- **Object collision** resolves to `obj/<id>/hkx/<id>.hkx` (552 of the 777 objects have one)
  and becomes one `StaticBody3D` per object placement, with the object's transform. Every body
  is static, including the ones the file marks dynamic or keyframed; `<id>_1.hkx` (the broken
  state of a breakable object: tens of dynamic debris bodies) is not placed.
- **Not placed:** `Enemy`/`Player` (deliberately, until game logic exists), `Navmesh`
  (`SoulsFormatsNEXT` `NVM` reads it; unwired),
  `DummyObject`/`DummyEnemy`, `ConnectCollision`.
- **Known data oddities:** some objects are event- or cutscene-gated (MSB lists everything
  that could appear; the gate is event scripting); `o1450_0001`–`_0003` sit at the origin in
  the data itself (`InitAnimID = −1`, likely inactive stubs); reused bodies such as `o0500`
  render in bind pose because `InitAnimID` needs animation playback.
- **`DrawGroups`/`DispGroups` are not used.** In DeS, map pieces and objects carry group
  membership in `DrawGroups` (`DispGroups` all zero); collisions carry their own group as one
  `DispGroups` bit and the groups visible from them in `DrawGroups`. Map SFX are created only
  while their event part's `DrawGroups` meet the active group mask (per map block, observed);
  that the active mask is the current collision's `DrawGroups` is inferred. Applying the DS1
  rule against `DispGroups` once deleted most of m02. Needs a player on collision first.
- `IsShadowSrc/Dest/Only` are zero on every part (likely mis-named fields upstream);
  `Collision.EnvLightMapSpotIndex` is zero everywhere.

**Events.** Region-based, with `MsbLoader.EventRegion` shared by both kinds:

- **SFX** (`ReadSfxPlacements`): the event's common region index when nonnegative, else the
  type-specific `UnkT00`. 1,691 of 1,693 events resolve; unresolved ones are skipped, never
  placed at a part's centre (that once stacked 180 Nexus candles on one point).
- **Point lights** (`ReadPointLights`): `PointLightID` is the region index, `UnkT04` the
  `POINT_LIGHT_BANK` row (−1 = no light). Confirmed in captures: m02's bridge campfire renders
  exactly at region 125 with row 0.
- Other events (`Sound`, `Wind` for particles, `Treasure`, `Generator`, `Message`) are unread.

**Per-part draw parameter IDs** (`MsbPlacement`): `LightID`, `FogID`, `ScatterID` are bound
per placement; `ToneMapID`/`ToneCorrectID` feed frame-global passes (see "Draw parameters").
`255` means unset.

## Havok collision

The fork's `HKX` class (`SoulsFormats/Formats/HKX/`) reads Havok packfiles: header, sections,
local/global/virtual fixups, and objects by class name. DeS ships Havok 5.5.0 and 5.1.0 files
(and a few 4.x in `chr`/`obj`/`m99`), big-endian with 4-byte pointers, **with no type
descriptions** (`__types__` is empty; the section order also differs between versions), so
object layouts are fixed per class in code. Member offsets follow soulstruct-havok's 5.5.0
definitions (GPL-3.0-or-later) and hold for the 5.1.0 files too.

`HKX.ReadCollisionShapes` follows each `hkpRigidBody` (transform at +224; identity in every
retail map file) through its shape tree and returns each leaf shape in its own space with its
accumulated transform. Havok's convex radius (+16 in every convex shape) is added to box,
sphere, capsule and cylinder sizes.

- `CustomParamStorageExtendedMeshShape` (FromSoftware's subclass, most map files) and
  `hkpStorageExtendedMeshShape`: `hkVector4` vertices, four 16-bit indices per triangle; the
  surface type is `materialArray[i].materialNameData`. The fourth index word is uninitialised
  exporter memory (0, else fill patterns such as `0xCDCD`, `0xDDDD`, `0xFFFF`; 27,361 distinct
  values) and is not read.
- `hkpStorageMeshShape` (5.1.0 files): float-triple vertices, three indices per triangle.
- `hkpSimpleMeshShape` (m99, some objects): vertices at +24, 16-byte triangles at +36.
- `hkpBoxShape`: half extents at +32 (m03_03's is flat).
- `hkpSphereShape`: the convex radius is the sphere's.
- `hkpCapsuleShape`: hemisphere centres at +32 and +48; the convex radius is the capsule's.
- `hkpCylinderShape`: core radius at +20, end points at +32 and +48 (their w lanes hold the full
  radius); the solid extends one convex radius past both.
- `hkpConvexVerticesShape`: vertices in blocks of four at +64 (x, y, z lanes), count at +76.
  The face planes at +80 are not read: many hulls store one face as several coplanar planes.
  Returned as points, without the convex radius.
- Wrappers: `hkpMoppBvTreeShape` (child at +52), `hkpConvexTranslateShape` (child at +24,
  translation at +32) and `hkpConvexTransformShape` (child at +24, `hkTransform` at +32).
- Not read: Havok 4.x files (29 object files, none placed in a retail map; 2 m99 map files).

`hkTransform` stores its rotation as three columns, then the translation. Against the FLVER
vertices of objects whose shapes are rotated 30–180°, reading the columns as rows fits worse
(`o1616` 88% of vertices inside its box against 30%, `o6472` 53% against 14%). Every 5.x map and
object file reads with no skipped shape.

The storage-mesh and `hkpMoppBvTreeShape` offsets follow soulstruct-havok; soulstruct has no
32-bit layouts for the other shapes, so theirs were read from the files. Mesh triangles are
wound clockwise from the front, as stored ((c − a) × (b − a) points out; floors point down
under the other order), which is also Godot's convention. The MOPP tree is ignored (Godot
builds its own).

`MapCollision` negates X like the FLVERs. Meshes swap each triangle's winding and become one
`ConcavePolygonShape3D` per surface type, each `CollisionShape3D` carrying `surface_type`
metadata. The other shapes become `BoxShape3D`, `SphereShape3D`, `CapsuleShape3D`,
`CylinderShape3D` (both along the axis point to point) and `ConvexPolygonShape3D` (Godot builds
the hull from the points), each rotation mirrored as S·R·S with S = diag(−1, 1, 1). Downward
ray casts hit floors' front faces; the m03 hull is hit from outside only. Object bodies' bounds
centres agree with their FLVERs for 98% of placements on m02 and m04 (the rest have no visible
mesh), and primitive-only bodies enclose 60–100% of their rendered vertices on m02, m04 and m05
(collision often covers part of an object, such as `o4421`'s top slab). The editor draws the
shapes through the `CollisionShape3D` gizmo (View → Gizmos).

**Surface types** (DS1's scheme: the last two digits are the base surface, +100/+200/+300 are
variants of it). No DeS param names them; the names below come from the diffuse texture of the
render geometry within 1 m of each collision triangle, over 15 main blocks:

| Type | Nearest render textures | Reading |
|---|---|---|
| 1, 2 | walls, pillars, arches, blocks | stone |
| 3 | cliffs, mud ground | soil, rock |
| 4 (104, 204) | `etc_wood`, trees, wooden walkways | wood |
| 5 | cloth, trees, ground | uncertain (DS1: grass) |
| 9 (109, 209) | metal, chains, iron objects | metal |
| 10 | `m01_sand*` | sand |
| 14 | m05 wooden floors, walls and roofs | wood (m05) |
| 16 | `m06_bone*` | bone |
| 21 | water meshes, pool floors | water |
| 22, 23 | m05 mud, garbage | mud, swamp |
| 27 | `m06_ara_maguma`, iron stone | lava |

Types 0, 6–8, 11, 12, 15, 18, 19, 25, 26, 40 and 90–92 match no clear texture. Some are
mostly away from any render geometry, i.e. invisible surfaces: 40 (2 of 62 triangles near
geometry), 7 (603 of 4,306), 0 (20,269 of 33,786).

## Draw parameters

`param/drawparam/<mXX>_<bank>.param` against the shipped paramdefs, read by
`DrawParamReader`. The paramdefs' Japanese `Description`/`DisplayName` fields document the
fields; `colA`-style fields are percent scales (`/100`). A missing map bank falls back to
`default_<bank>.param`.

| Bank | Indexed by | Consumer |
|---|---|---|
| `LIGHT_BANK` | `Part.LightID` | hemisphere up/down, env cubemap names and colours, three directional lights and the specular colour (HemDir3) |
| `FOG_BANK` | `Part.FogID` | per-material distance fade (`degRotW` is the strength; `degRotZ` unused) |
| `LIGHT_SCATTERING_BANK` | `Part.ScatterID` | per-vertex Hoffman–Preetham scattering |
| `TONE_MAP_BANK` | collision `ToneMapID` | exposure adaptation and bloom (frame-global) |
| `TONE_CORRECT_BANK` | collision `ToneCorrectID` | final colour matrix (frame-global) |
| `SHADOW_BANK` | `Part.ShadowID` (row 0 used) | sun shadow direction, density, tint, fade |
| `POINT_LIGHT_BANK` | light event `UnkT04` | point-light colour and falloff |
| `DOF_BANK`, `LENS_FLARE_BANK` | — | read, not implemented |
| `ENV_LIGHT_TEX_BANK` | — | `isUse = 0` everywhere: unused by the game |

**Env cubemaps:** every map ships `map/<mXX>/<mXX>_9999.tpf` with `EnvDif_<mXX>_<NNN>` and
`EnvSpc_<mXX>_<NNN>` cubemaps; `LIGHT_BANK`'s `envDif`/`envSpc_0..3` are the `NNN` (930 of 930
referenced rows resolve). Unresolved names bind a 1×1 white cubemap.

**The frame's tone rows** are inferred to follow the collision the player stands on
(collisions carry their own IDs, which differ from the map pieces' on several blocks); without
a player, the block's most common collision rows are used
(`MsbLoader.ReadDominantCollisionToneIds`).

**Not read:** `LIGHT_BANK`'s diffuse hemisphere pair (`colA_du`/`colA_dd`). It was wired and
reverted three times (every variant flattened the Nexus or washed it to beige); do not re-add
without new evidence.

## Rendering pipeline

The reconstruction comes from the disassembled shader library, RPCS3 frame captures, and
executable specifications (external `ELF_ENGINE_ACCURACY_RESEARCH.md` sections 12–16).
**Standing method:** when data confirmed from the game renders wrong here, look for what the
engine does that Soulbrandt does not; do not revert the finding or fit a constant.

### Per material (the game's fragment program)

1. Diffuse at UV0 × `diffuse_tint` × vertex colour. Two-layer materials blend by vertex
   colour alpha, and vertex RGB multiplies both the blended diffuse and specular.
2. Normal: reconstructed per pixel from a two-channel bump map when present, else the vertex
   normal. Flipped on back faces of double-sided meshes.
3. **Hemisphere** `mix(down, up, (N.y + 1)/2)`, ungated.
4. **HemEnv:** EnvDif cubemap by the normal, gated by `min(shadow, lightmap)`; EnvSpc cubemap
   by the reflection vector, weighted by the RGB specular sample, vertex colour,
   `specular_tint` and the same gate. No directional lights.
5. **HemDir3:** three directional lights (`max(−N·L, 0)`), plus three Phong lobes over the
   same directions weighted by the single specular colour and `g_SpecularPower`. No env term.
   (`spec_light_direction` is unused by this variant.)
6. **Point lights** (`PntS`/`PntSS`/`PntSSSS`, 1–4 lights): Lambert × linear falloff
   `saturate((dwindleEnd − d)/(dwindleEnd − dwindleBegin))`, not shadowed.
7. **Fog:** `mix(c, FOG_BANK colour, saturate(ramp) · degRotW/100)`, weight unclamped after
   the multiply; ramp `(clip.w − fogBeginZ)/(fogEndZ − fogBeginZ)` on planar depth, per vertex.
8. **Scattering:** `c · T + S` with per-vertex transmittance `T` and in-scatter `S`.
9. **Exposure:** `saturate(c · E / 2)` into the RGBA8 scene buffer.

### Per frame (`ds_filter.shaderbnd`)

Luminance, adaptation, bright-pass, blur and bloom, then `DS_Fil_HDR_ColAdj`:
`M · (saturate(buffer · 2) + bloom · bloomMul/100) + offset`, with `M`/offset from
`TONE_CORRECT_BANK` (brightness, pivot contrast, Rec.601 saturation, hue). **Everything is
linear:** no tone curve, no LUT. The geometry pass's `/2` and the composite's `·2` cancel, so
the displayed value is `saturate(c · E)` through the matrix. Then DoF and camera motion blur.

### What Soulbrandt implements

| Stage | Status |
|---|---|
| diffuse, tint, vertex colour, two-layer blend | matches |
| per-pixel normal, hemisphere | matches |
| HemEnv env diffuse/specular, `g_EnvSpcSlotNo` | matches |
| HemDir3 directional and three-lobe specular | matches |
| gate `min(shadow, lightmap)` | shadow term is the static single-map stand-in (see "Sun shadows") |
| point lights | MSB lights on objects only, nearest four per placement at load; the player's light and per-frame selection are missing |
| fog, scattering | engine-exact constants, per vertex |
| exposure, adaptation, tone correction, bloom | matches the game's chain (see "Frame post-process") |
| `HemEnvLerp` (cross-fade between two light rows' cubemaps) | not implemented; a runtime transition |
| DoF, motion blur | not implemented |

### Output stage (`output_stage.gdshaderinc`)

Each entry-point shader calls `DES_ATMOSPHERE_VERTEX(world_position)` in `vertex()` and
`des_output(colour)` in `fragment()`: fog → `c·T + S` → exposure → tone matrix.

- **Scattering constants** (`FlverLoader.ApplyScatterBank`) are computed as the engine does:
  Rayleigh (∝ λ⁻⁴) and Mie (∝ K(λ)/λ²) coefficients for 650/570/475 nm scaled by
  `lsBetaRay`/`lsBetaMie`, reflectance, in-scatter, sun colour, `blendCoef` and `distanceMul`
  as percent scales, phase vector `(1 − g², 1 + g, 2g)`. This reproduces the captured `c104`
  of m01 and m02 exactly. Extinction is `exp2(−c104 · d · distanceMul · log2(e)²)` on the
  radial distance; the engine's Henyey–Greenstein denominator is `(1 + g) − 2g·cosθ`; the
  in-scatter is divided per channel by the extinction.
- **Light directions:** `LIGHT_BANK`, `LIGHT_SCATTERING_BANK` and `SHADOW_BANK` all build the
  travel direction `(cos X sin Y, −sin X, cos X cos Y)`; `FlverLoader.SunDirection` returns
  the mirrored toward-light vector.
- **Exposure and tone** are frame-global: `des_exposure_map` (the adapted `E`) and the
  `des_tone_*` rows are global uniforms declared in `project.godot`. Without a map a fixed
  fallback `E = 0.18/0.101` binds.
- **Buffer encoding:** for `PostProcessPipeline`'s measurement camera (visual layer 21,
  `DES_MEASURE_LAYER`) materials write the game's buffer value `saturate(c·E/2)`; for the
  main view, the displayed value. `des_encode` takes twice the buffer value; additive and
  subtractive draws use `des_encode_additive`, which applies only the matrix's linear part
  (the game applies `M` once to the blended buffer).
- `FlverLoader.ApplyDrawParams` binds light, fog and scatter rows per placement as a
  per-surface override (`Duplicate()` of the cached base material), gated by which uniforms
  the shader declares (`HasUniform`), so water gets the output stage and its glint direction but
  no other light uniforms.
- Compatibility renders RGBA8 without an HDR target, so exposure has to happen in the
  material shader, exactly where DeS does it. There is no colour-space conversion anywhere:
  `: source_color` is a no-op on fetch under Compatibility, there is no output sRGB encode,
  and DeS's HemEnv textures are all fetched without gamma conversion.

### Lighting details

- **Lightmaps** (`g_Lightmap`) are on 63% of materials, always on the last UV channel (UV2,
  or `Custom0` for blend materials). A lit material without a lightmap binds white, so its
  env term is gated by the shadow alone (the engine's behaviour).
- **Objects and characters** use the same family: `g_LightingType` 1 → HemDir3, 3 → HemEnv.
- **Point lights** (`FlverLoader.ResolvePointLights`, `BindPointLights`): each map's light
  events are resolved once per load; each object placement binds the four nearest (measured
  from its mesh's AABB centre) as uniform arrays. **MSB lights never reach map pieces**: in all
  eight captures every map-piece draw uses the one-light variant with only the player's own
  light (a bank row no light event names), even beside a lit campfire, while objects near it
  take up to four MSB lights. Map geometry's torch light is baked into its lightmaps.
  `FlverLoader.PointLightsOnMapPieces` (default off) extends them to map pieces as a
  deliberate departure. The PC Remaster mod achieves torch light on geometry by converting
  host map pieces into objects and adding hand-placed regions.
- **`env_intensity`** runs 1.5–5×. The one reported overexposure, m01's `m0000B0` (the
  Old One's arena floor, row 3, 4.5×), predates the adapted exposure and the exact atmosphere,
  and the area is meant to read bright. No capture covers it: the game shows it only in the
  ending. A potential issue, probably a false report. A clamp was tried and reverted.

### Sun shadows (`ShadowRenderer`)

The game uses **four perspective shadow-map splits** in a 2048² Z24S8 atlas of 2×2 1024²
tiles, with the PSM fields `calibulateFar`/`persedDepthOffset`/`radFactor`; map pieces get
one cascade matrix per draw (`c112..c115`), characters select per pixel; one hardware 2×2
compare; composite `1 − (density − tint) · g_ShadowPowMul · fade · inShadow` per channel, with
`fade = saturate((fadeBeginDist + fadeDist − d)/fadeDist)` on radial distance.
`g_ShadowPowMul` is the MTD's (1 when absent). Only lightmap-less MTDs carry it: 0.8 on
ordinary map and character materials, 0.4 hair, 0.5 body and face, 0 on `c[dn]`; lightmapped
materials take the bank's density unscaled (confirmed in every capture, including a tinted row).

Soulbrandt's stand-in (v2) is **one static orthographic depth pass** per map, from
`SHADOW_BANK` row 0's direction, over the lit casters' bounds (radius `min(½·diagonal + 2,
200)`), into a 2048² `SubViewport`. Casters are sibling `MeshInstance3D`s sharing each mesh
with `shadow_depth.gdshader` (`cull_front`, linear light-space depth packed 16-bit across
R,G); alpha-tested surfaces take `shadow_depth_alpha.gdshader`, which also discards below the
material's alpha threshold (still `cull_front`: casting both faces self-shadowed closed
alpha-tested meshes). Its effect is minor at this resolution (190 texels in m04, 33 in m05;
m02's cutouts lie outside the region). Receivers (`sun_shadow` in
`hemisphere_ambient.gdshaderinc`) use a rotated 12-tap Poisson PCF and the engine's composite,
density, tint, `g_ShadowPowMul` (`shadow_pow_mul`) and distance fade. Uniforms are bound once;
only the fade depends on the camera. Exact: direction, density, tint, `g_ShadowPowMul`, fade,
volume depth, composite. Missing: the splits and PSM warp (which follow the camera, so they
wait for a gameplay camera; camera-following versions made shadows slide), per-draw cascade
selection, the "Load Folder" path, and a load target with a non-identity transform (the caster
clones live in their own world). At about 0.2 m per texel it cannot resolve
a prop's self-shadowing, which the game's splits do at millimetre scale near the camera.

### Water (`water.gdshader`)

Reconstructed from `DS_Water_Env` (107 instructions), `DS_Water`, the water `.mtd` and six
captured water draws. Three bump octaves at `UV · g_TileScale_i.x + g_TexScroll_0 ·
g_TileScale_i.y · TIME`, summed by `g_TileBlend_i` (not renormalised), Z reconstructed and
biased by `g_BumpMapSmoose`. The wave normal is read as the world normal `(x, z, y)` (flat water,
no tangent frame). With `a` the vertex alpha and `C` the vertex RGB (both passed
through unchanged by `DS_Water`):

- body = `mix(refraction, g_WaterColor.rgb, a · g_WaterColor.a)`; the refraction tap is offset
  by `wave.xy · g_RefractBand · a` and decoded from the scene buffer to scene radiance;
- Fresnel `g_FresnelScale · (bias + (1 − bias) · (1 − N·V)^g_FresnelPow)`;
- reflection = `g_Envmap` cubemap (tiny, e.g. 32 px) `· g_FresnelColor` + a sun glint
  `pow(R·L, 100) · g_SpecularMapColor · Power`, where `L` is `LIGHT_BANK` directional light 0,
  not the scattering sun; there is no screen-space reflection tap (`g_ReflectBand` is unused);
- surface = `mix(body, reflection, F) · C`, then `des_output`;
- final = `mix(body, surface, min(a, g_WaterFadeBegin) / g_WaterFadeBegin)`.

The shoreline is authored in the vertex colour: m02's river banks carry RGB 0.5 (interior 1.0),
and shallow pools carry alpha 0.1–0.3, which shows the bed through the water.
`DS_Water_Reflect`/`DS_Water_Mask` are unused; `DS_Water_Env_Skin` is the Leechmonger arena
only. The engine's refraction guard tests the scene buffer's alpha; ours tests depth.

### Sky and unlit materials

- **Sky domes** (`sky.gdshader`) run the full `des_output`: the captured sky draws carry the
  frame's own scattering constants, fog from their own `FOG_BANK` row, and a diffuse
  multiplier of 1. Without the scattering the sky rendered up to 1.6× too bright.
- **Unlit blended/scrolling map meshes** (light shafts, fog sheets, clouds) use `vfx_scroll`,
  the lit epilogue without lighting (the game's `DS_Phn_Dif________________Non`).
- **Effect sprites** (`sfx_preview*`) write the game's buffer value per sprite program: Type0
  applies exposure (`saturate((…)·E/2)`), Type1–3 do not.
- `StandardMaterial3D` remains only for unlit opaque/alpha-test/subtractive materials. Its
  texture-driven `Emission` renders wrong without an exposure environment; do not use it for
  glow.

### Frame post-process (`PostProcessPipeline`)

Reproduces the game's `ds_filter` chain with the captured constants, resolutions and formats
(external `ELF_ENGINE_ACCURACY_RESEARCH.md` 13):

1. Scene buffer `saturate(c·E/2)`, copied to 320×180 with one bilinear tap.
2. Luminance: 64×64 of `log(dot(rgb, Rec.709) · 2/E + 0.0001)` over 3×3 bilinear taps, two
   4×4 reductions, `exp` of the mean.
3. Adaptation: `E += (grayKey/(clamp(L, minAdaptedLum, maxAdapredLum) + 0.001) − E) ·
   (1 − 0.98^(30 · adaptSpeed · dt))`.
4. Bloom: bright-pass `max(c − bloomBegin/100, 0)/(1 − bloomBegin/100)`, 13-tap 5×5
   Gaussian, 2:1 copy to 160×90, the Gaussian again, then a 15-tap σ = 3 Gaussian vertically
   and horizontally at twice unit weight; RGBA8 targets clip at 1.
5. Composite as above.

Under Compatibility: a second camera (`MeasureCamera`, synced to the editor or game camera)
renders the scene at 360 lines into `SceneBuffer` (doubling map draw calls); stages are nested
`SubViewport`s (a child renders before its parent, so the chain completes in one frame);
luminance targets are float16; `E` lives in a never-cleared 1×1 float target updated by alpha
blending; the bloom is added over the main view by a full-screen quad
(`bloom_composite.gdshader`). The map's `WorldEnvironment` only pins a black background, no
ambient, no reflections and a linear tonemap. Verified on a captured Nexus buffer: `L` within
0.4% of the game's, the bloom intermediate equal to the game's within one 8-bit level; live
m02 `E` 1.7812 against the captured 1.7823. Not reproduced: DoF, motion blur, tone-row
interpolation when moving between rows.

### Comparing against captures

A capture holds the game camera (map pieces at the origin carry world translation `−eye`; view
rows `c4..c6`; vertical FOV `2·atan(c5.y/c1.y)`, 43°) and, through RPCS3's colour-buffer
write-back, the game's own render targets. Rendering Soulbrandt at a captured camera and
comparing against the captured buffer is the most direct accuracy check available; the method
and results are in `docs/context.md`.

## Shader library and capture tooling

`shader/ds_flver.shaderbnd` holds 1349 named programs (134 `.vpo`, 1215 `.fpo`);
`ds_filter.shaderbnd` holds the 31 post-process programs. Names are fixed-width, `_`-padded:

`DS_<family>_<textures>_<shadow>_<lightingModel>.fpo`

| Field | Values |
|---|---|
| family | `Phn`, `Gst` (translucent phantom variant), `Dbg`, `Water`, `Ghost` |
| textures | `Dif`, `DifSpc`, `DifSpcBmp`, `+Mul`, `+Lit` |
| shadow | none, `Sdw` (shadow map), `Csd` (cascaded) |
| lighting model | `HemDir3`, `HemEnv`, `HemEnvLerp`, each × {none, `PntS`, `PntSS`, `PntSSSS`}; plus `Non` and bare point-light variants |

Vertex programs: `DS_<family>_<layout>_<texgen>_<pass>.vpo`, layout `PIN`/`PIWN`/`PINT`/
`PINTT`, pass `Non`/`Sdw`/`Dep`/`DepAlp`. `DS_Phn_*` and `DS_Gst_*` vertex programs are
identical. Parse names by feature substring, not by splitting on `_`. An MTD's `ShaderPath`
fixes the family and textures; the runtime picks the shadow and lighting variants, so the
lighting model cannot be derived from the shader path (`MTD.LightingType` is the authored
signal). `ShaderLibrary.ResolveMaterialShader` reads this as data (612 of 612 MTDs resolve).

The binaries are symbol-stripped RSX microcode: no parameter names, and per-draw constants are
supplied by the CPU. `tools/RsxShaderMatch` (see its `README.md`) decodes and disassembles
`.fpo`/`.vpo` files, names RPCS3 shader-log dumps against the library, and reads RPCS3 frame
captures (`~/.config/rpcs3/captures/*.rrc.gz`):

- `rrc-draws`: every draw's named programs and vertex constants.
- `rrc-mine`: constant registers per shader group, per-frame vs per-draw, and fragment inline
  constants (RSX fragment constants are patched into the microcode per draw).
- `rrc-shadow`, `rrc-tex`, `rrc-fog`: shadow atlas matrices, texture-unit state, fog state
  (DeS never uses RSX fixed-function fog).

Fixed vertex registers: `c103` fog, `c104..c111` scattering, `c112..c115` shadow cascade,
`c120..c122` texgen, `c466 = (1/1024, log2 e, log2 e, 1)`, `c467` shadow atlas remap.

## Effects (VFX)

`SfxLoader` reads `.ffx` files from `mounted/sfx/<bank>/` (map bank first, then
`commoneffects`, then `main`, which holds some m08 placements) through the fork's DeS mode of
`FFXDLSE` (3,091 of 3,091 files parse).
`SfxPreview` builds billboard layers from fingerprint-verified templates (2117 LOD selection,
2023 schedules, 2121/2123/2101 containers, 2020 child-effect emission); `SfxBatchParticles`
schedules births on the CPU and draws each layer as one `MultiMesh`, because
`GPUParticles3D.EmitParticle` is unsupported under Compatibility.

Native behaviour implemented from the external specifications (`ELF_ENGINE_ACCURACY_RESEARCH.md`
sections 2–11, `OPEN_ENGINE_ACCURACY_PROBLEMS.md` section E, `VFX_PLAYBACK.md`):

- **Placement:** SFX region selection and Y–Z–X region rotation (see "MSB placement");
  action35 translation `(L,U,F)` mirrored to `(−L, U, F)`, rotation `Ry(−B)·Rx(−A)·Rz(−C)`,
  parent × local.
- **Parameters:** Param66 is a parent-context reference to Param38's runtime slot; 91001
  (Nexus stair mist) builds.
- **Emitters:** square (29) and circle (30) in the local XY plane, circle radius `R·u`;
  birth cone about local +Z with polar angle `bias(concentration) · breadth`; the box (32)
  turns the cone onto each face's normal; `emissionType` 0 instance frame, 3 world axes, 1/2
  world axes with +Z turned to ±Y (world axes inferred). Sphere (31) direction is unrecovered.
- **Motion84:** stepped explicit Euler with gravity, wind as velocity advection, linear drag
  and periodic velocity rotation; cadence reuses `ZeroWaitPreviewHz` and
  `PreviewWindAcceleration` (native cadence and live wind unrecovered). Action55 keeps a
  closed-form preview with an inferred gravity sign.
- **Type2 sprites** (`DS_Sfx_SimpleSpriteType2`) fade by depth intersection over a slab of
  thickness `(width + height)/2`.
- **Action46 `MoveCamera`** (container motion): the container leaves its parent and rides the
  camera, its local pose read in native camera space (+Z forward, inferred), following position
  and rotation. Camera-attached effects are always in the map preview's range. Used by the m05
  rain (95000) and m08 mist (98100); m05's rain sits 13 m ahead of the view.
- **Emitter and gravity sequences:** speed and size-multiplier sequences of emitters 28–32 are
  sampled per emission on the instance clock (time since the 2023 instance's startup); an
  action55 gravity sequence is evaluated on the same clock, as motion84's is (inferred for
  action55), through its integrals. Size ranges of 28–31 stay omitted, as on the constant path.
- **Action20 `AssignBillboard`** (template2101 geometry): one sprite at the container's pose for
  the container's life, played as a one-particle layer with the cluster's size, colour, frame
  and shader-type rules (the host builds the same render descriptor). Scale Y precedes scale X
  (the archived Lua's order). Fires use it as their far-distance impostor.
- **Action43 `SetPostEffect`** (template2101 geometry), type 1: the bump distortion of
  `DS_Sfx_DistortionType1`/`Type5` (`sfx_distortion.gdshader`, constants read from captured
  draws). A quad of `±0.5 × (scaleX, scaleY)`, its normal turned toward the camera position with
  X kept horizontal when `pointToCamera` is set (captured draws), else in the container's XY
  plane, samples
  the scene at its screen position plus `bump.xy · strength · max(1 − r, 0) · 0.01` (bump
  scrolled by args 12/13), times the colour and optional mask, alpha `colour.a · 2^r`, alpha
  blended with depth test and no depth write. It draws before every other transparent surface:
  Godot's screen copy holds only opaque geometry, and `colour.a` is 1 on most fog gates, so drawn
  later it would erase the fog behind it. Used over map fires, torches and fog gates.
- **Template2023 startup children** (arg 16) start at the instance's pose, its placement
  (arg 3) included; fog gates lift their distortion to the fog emitter's height this way.
- **Action104** (camera-distance fade) is parsed and not applied: it writes only the effect
  instance's faded colour word, which billboard clusters never read.
- **Blending:** transparency 0 and 4 additive, 2 alpha (verified against capture draws).
- **Lifetimes:** finite 2023 schedules play once; lifetimes above 10,000 s are unlimited;
  LOD band crossings let outgoing particles expire instead of restarting.

**Map preview** (`MapSfxPreview`, disabled until **Preview Enabled** is set): nearby MSB
events, plus opt-in reviewed object/dummy effects (archstone sword glow 99100, wagon 1400,
wisp 93000) and camera-region effects (Latria 94200, Stonefang 96000). Budget: 64 systems and
8192 allocated particles per map, two builds per 0.25 s refresh, priority by apparent size.
Effects leaving range are parked (up to 128), not freed: in the editor, adding or removing any
node makes the Scene dock re-walk the whole scene. Prepared effect descriptions and appearance
samples are shared between placements.

**Not implemented:** native activation and event-script control (map Lua enables effects by
entity ID), display-group gating of map SFX, container translation (action1), action46's position-only mode, native action34
axes, model primitives (actions 3/61), lights (action24), action43's radial-wave type and
nonzero shapes, action20's Y-axis mode, moving child-effect emitters (curved template2020
radius or rotation), templates 2104/2115/2022/2024, native motion cadence, map wind, and the
sprites' per-vertex fog and scattering factors. Coverage: 129 of the 135 distinct MSB-placed
effects that exist build at least one layer (three referenced IDs exist in no bank).

## Debug overlay

**Archstone → Debug Overlay...** opens a window of section checkboxes (saved in editor
metadata) and a viewport selector. `DebugOverlay.gd` draws monospace text over the chosen
editor viewport: frame timing, render counters, camera pose (Godot and native), each
`MapSfxPreview`'s budget, the nearest placed part, and a frustum-culled render log
(`instances_cull_convex`, not occlusion-tested). Frame, render and camera lines update every
frame; scene walks every 0.5 s. Under a `CanvasLayer` it runs in a game too.

## Standing priority: stability and resource safety

The target user is a non-technical player pointing this at their own full game copy, so hangs,
crashes and runaway CPU, memory or disk use in extraction or loading are bugs to root-cause,
not flakes to re-run. Prefer failing safely and detectably (bounded memory and time, resumable
progress) over silent retries or corrupted intermediate state. A confirmed external cause may
stay documented as a known flake; a new one may not until it has been investigated. The
2026-07-24 removal of the import pipeline eliminated the known instances; the principle
applies to `AssetExtractor` and `FlverLoader`, which still do real I/O and decoding.

## Known gaps and deferred work

**Rendering**
- Four-split perspective shadows (waits for a gameplay camera).
- The player's own point light (a bank row about 4 m ahead of the camera) and per-frame
  light selection.
- `HemEnvLerp`, DoF, motion blur, lens flare.
- The ghost/dissolve families: `Ps_Wander_Ghost` (Wandering Ghost, `DS_Ghost_Tod/Skin`) and
  `Cs_ShadowMan` (Shadow Man, `DS_Ghost_ParamTod/ParamSkin`), each with its own `g_Ghost*`
  parameters, currently render through the ordinary path. Not to be confused with `DS_Gst_*`.
- Light-shaft quads (`A05_vollight` etc.) read harder at their edges than in RPCS3.
- Possible `env_intensity` overexposure on bright lightmaps; unconfirmed (see "Lighting
  details").
- Character/parts armor: vanilla uses HemDir3 (no cubemap). The PC Remaster mod's reflective
  armor re-tags those MTDs to HemEnv with re-tuned specular; it is an enhancement, not a
  restoration.

**Data and systems**
- Havok beyond static collision: skeletons, animation (the wavelet-compressed format), object
  simulation (motion types, mass, constraints, ragdolls, breakable debris) and Havok 4.x files.
  `Grimrukh/soulstruct-havok` is the DeS-specific reference; the unmerged 2018
  `SoulsFormatsNEXT` branch is a rougher one.
- Enemy/player placement, navmesh wiring, event scripts, `.breakobj` debris, cutscenes
  (`remo/scnAAxxxx.remobnd`: camera, Havok animation and `.tae` per cut).
- Runtime-placed geometry: Nexus captures 094137/094221 draw a colonnade at a transform no
  m01 MSB part has; its source is untraced.
- Draw-group visibility semantics; LOD selection (loading every FLVER in a folder overlaps
  variants).
- A runtime (non-editor) loader for exported builds.

**`SoulsFormatsNEXT` fork** (submodule; `origin` is the fork, `upstream` is `soulsmods`):
local commits are the FLVER0 UV-scale fix, the `net8.0` retarget, the DeS `FFXDLSE` mode and
the read-only Havok packfile reader (`Formats/HKX/`, new files only).
Re-check the UV fix after any upstream sync.
