# RsxShaderMatch

Research tool for **naming the anonymous RPCS3 shader-log captures** by matching
them against Demon's Souls' own named shader library.

Not part of the Godot project. Standalone console app:

```
dotnet run --project tools/RsxShaderMatch -- dump      <file.fpo|file.vpo>
dotnet run --project tools/RsxShaderMatch -- fp        <file.fpo>
dotnet run --project tools/RsxShaderMatch -- vp        <file.vpo>
dotnet run --project tools/RsxShaderMatch -- disasm    <file.fpo|file.vpo>
dotnet run --project tools/RsxShaderMatch -- sweep     <dir> [dir ...]
dotnet run --project tools/RsxShaderMatch -- match     mounted/shader ~/.cache/rpcs3/shaderlog [--vertex] [--only=ProgramN] [--json=out.json]
dotnet run --project tools/RsxShaderMatch -- coverage  mounted/shader ~/.cache/rpcs3/shaderlog [--vertex]
dotnet run --project tools/RsxShaderMatch -- verify    mounted/shader ~/.cache/rpcs3/shaderlog [--vertex]
dotnet run --project tools/RsxShaderMatch -- rrc       ~/.config/rpcs3/captures/<frame>.rrc.gz
dotnet run --project tools/RsxShaderMatch -- rrc-draws ~/.config/rpcs3/captures/<frame>.rrc.gz mounted/shader [--only=N] [--grep=substr]
dotnet run --project tools/RsxShaderMatch -- rrc-fog   ~/.config/rpcs3/captures/<frame>.rrc.gz
dotnet run --project tools/RsxShaderMatch -- rrc-mine  ~/.config/rpcs3/captures/<frame>.rrc.gz mounted/shader [--json=out.json]
dotnet run --project tools/RsxShaderMatch -- rrc-shadow ~/.config/rpcs3/captures/<frame>.rrc.gz mounted/shader
```

`--vertex` switches `match`/`verify`/`coverage` to the vertex-program pass (Stage 5).
For anything that iterates the whole library (a batch of `disasm`), `dotnet publish`
once and run the exe directly - `dotnet run` per file is ~1 s of startup each.

Outputs: `fragment-names.json` / `vertex-names.json` (capture -> library shader name +
confidence tier + candidates), and `disasm` (readable RSX assembly for any `.fpo`/`.vpo`,
captured or not).

## Why

Every shader investigation so far (`docs/context.md` parts 11-33) traced
**anonymous** programs - "FragmentProgram151 is a lightmapped HemEnv map piece"
was an inference that cost a whole session. Meanwhile `shader/ds_flver.shaderbnd`
holds all **1215 fragment + 134 vertex programs with their real dev names as
filenames** (`DS_Phn_DifSpcBmpMulLit____HemEnv.fpo` etc.).

If we can match each captured program to the library binary it came from, the
anonymous analysis gets retroactively named, and every future question
(`HemEnvLerp`, the `Pnt` point-light variants, which families get `FOG_BANK`)
becomes a lookup instead of a trace.

The captures cover the Nexus + Boletaria B0 only, but that is fine: the library
is the complete corpus. Captures are a calibration/label set, not the coverage.

## Inputs on this machine

| What | Where | Notes |
|---|---|---|
| Named library binaries | `mounted/shader/ds_flver/`, `ds_filter/`, ... | 1421 files, extracted 2026-08-28 |
| RPCS3 shader-log captures | `~/.cache/rpcs3/shaderlog/` | 423 `FragmentProgramN.spirv` + 212 `VertexProgramN.spirv`; despite the extension they are plain GLSL 450 text |
| RPCS3 RSX frame capture | `~/.config/rpcs3/captures/BLUS30443_20260817172216_capture.rrc.gz` | one frame, ~37 MB; carries raw microcode **and real CPU constant values** (`c104` etc.) - the source for part 33's unverified scattering constants, a later job |
| RPCS3 source (ISA reference) | `~/src/rpcs3/rpcs3/Emu/RSX/Program/` | `CgBinaryFragmentProgram.cpp` / `CgBinaryVertexProgram.cpp` are a working RSX FP/VP disassembler; `FragmentProgramDecompiler.cpp` is the full GLSL decompiler that produced the captures |

## Stage 1 - container format (done)

`CgProgram.cs` parses the `.fpo`/`.vpo` wrapper. **They are not raw Cg binaries** -
they are a ~0x30-byte FromSoft header (`magic` `0xFF0F0F11`/`0xFF0F0011`, then a
u32 offset to the payload) wrapping a **standard big-endian Sony `CgBinaryProgram`**
+ `CgBinaryFragmentProgram`/`CgBinaryVertexProgram` + raw RSX microcode. Full
layout in the `CgProgram.cs` doc comment.

`sweep mounted/shader` result: **1421/1421 parse, 0 failures.** For all 1260
fragment programs every invariant holds (`totalSize == fileLen - cgOffset`,
`ucodeSize % 16 == 0`, `instructionCount * 16 == ucodeSize`). Validated against the
known case: `DS_Fil_HDR.fpo` -> 7 instructions, ucode 112 B @ 0x70, matching
`docs/context.md` part 26's "five instructions" (5 shown in the capture + a
trailing MOV/END pair).

Findings worth keeping:

- **Fragment programs: `parameterCount == 0`** - the parameter/name table is fully
  stripped, as the docs already said.
- **Vertex programs keep the parameter table structurally** (161 entries on the big
  FLVER vpos, with types: 1047 = float3, 1048 = float4, plus embedded default-value
  floats) **but every name offset is 0** - names stripped just the same.
- The 4-u32 table at `.fpo` offset 0x20 (before the CgBinaryProgram) is not
  identified yet. Not needed so far.
- The `CgBinaryFragmentProgram` fields after `instructionCount` decode to
  suspicious values (`texCoords2D` = 0xFFFD on the HDR shader) - base offset or
  field widths need one more look. Not load-bearing.

## Stage 2 - RSX instruction decode (done)

`RsxFp.cs` decodes fragment-program microcode, ported from RPCS3's
`CgBinaryDisasm::TaskFP` + the bitfield unions in `ShaderParam.h` /
`RSXFragmentProgram.h`. Per instruction: 4x u32 big-endian words, 16-bit halfword
swap per word (`GetData(d) = d<<16 | d>>16`), then `OPDEST`/`SRC1` bitfields;
opcode = `dst.opcode | (src1.opcode_hi << 6)`; an instruction with any source
`reg_type == 2` is followed by a 128-bit inline literal (32-byte slot, not 16) -
that literal is exactly the "CPU-supplied per draw" constant slot the game patches
at runtime; walk ends at `OPDEST.end`.

No GLSL generation - just a fingerprint: ordered texture-unit run, distinct unit
set, inline-constant count, KIL present, FENCT/FENCB present, opcode histogram.

Validated: on all 21 `ds_filter` fragment programs the decoded slot count equals
the header `instructionCount`. `DS_Fil_HDR` decodes to 5 real instructions + 2
inline constants, matching `docs/context.md` part 26's independent
`CgBinaryDisasm` result.

## Stage 3 - fragment matcher (done)

`Capture.cs` reduces a shader-log `.spirv` to the same fingerprint by regex over
`fs_main()` (RPCS3's decompiler keeps RSX opcode names: `TEX2D(N,` -> unit N,
`_fetch_constant(N)`, `_kill()`, `// FENC`). Consecutive `TEXxD` calls on the same
unit are collapsed (one RSX `TEX` on a 1D or partial-mask sampler expands to
several GLSL lines).

`LibName.cs` parses the library filename into a feature vector (`Dif/Spc/Bmp/Mul/Lit`,
`Sdw`/`Csd`, `HemDir3`/`HemEnv`/`HemEnvLerp`, point-light count). `Score` in
`Program.cs` combines:

- ordered texture-unit run match (+90), or unit-set match (+48), or per-unit overlap
- **cube-sampler agreement** (+22 / -32): a `samplerCube` in the capture <=> `HemEnv`
  in the name. Cleanly separates `HemEnv` from `HemDir3`.
- **shadow-sampler agreement** (+22 / -32): a `sampler2DShadow` <=> `Sdw`/`Csd`.
- **exact inline-constant count** (+45; steep taper otherwise) - the point-light-count
  and `Sdw`-vs-`Csd` discriminator.
- FENC / KIL agreement, and material-texture count (name) vs 2D sampler count (capture).

Results against the earlier 257-capture shader-log: **HIGH 208, MED 21, LOW 28,
NONE 0**. The capture set has since grown to 423; `docs/context.md` part 34 has the
current run (363 HIGH). Written to `fragment-names.json` (regenerate with
`match ... --json=`). `verify` re-checks hand-verified anchors and exits non-zero on
regression:

| capture | -> | library shader | why it's certain |
|---|---|---|---|
| `FragmentProgram151` | | `DS_Phn_Dif______MulLitCsd_HemEnv` | unit-run `[11,3,7,0,6]` exact, const 41 exact; sharper than `context.md` part 18 (names the Csd variant + Dif-only set) |
| `FragmentProgram11` | | `DS_Phn_DifSpcBmp______Csd_HemDir3` | unit-run `[2,7,1,0]` exact, const 62 exact; confirms part 18's "HemDir3", shows its "terrain blend" label was loose (no `Mul`) |
| `FragmentProgram62` | | `DS_Phn_Dif___Bmp___LitSdw_HemEnv` | units 0=dif 2=bump 6=lightmap 7=shadow 11=cube - exact structural correspondence |
| `FragmentProgram20` | | `DS_Phn_DifSpc_________Sdw_HemDir3PntS` | unit-run exact, const 46 exact (39 for the no-point-light sibling) |

The 28 LOW are honest ambiguities, not scorer failures:
- trivial / passthrough shaders with no texture fingerprint (`units[]`, ~8 of them);
- the `Sdw`-vs-`Csd` pair at an identical constant count and unit run - a real
  fingerprint limit (both use `sampler2DShadow`; the cascade-select chain isn't
  visible in the capture GLSL). These surface as two co-equal `candidates` in the JSON.

**Also found:** `FragmentProgramN` numbers are **not stable across capture sessions**.
This matcher names the *current* `~/.cache/rpcs3/shaderlog`; older `context.md`
entries (parts 11-24) came from an earlier dump with different numbering - e.g. their
`FragmentProgram124` was a bumpmapped HemEnv, this dump's is a small LUT shader.

## Stage 4 - readable disassembler (done)

`RsxFp.Disassemble` emits a one-line-per-instruction RSX assembly listing for any
`.fpo`, whether the game ever drew it or not - this is what makes the ~1000
uncaptured library shaders readable instead of just fingerprinted. Same decode as
the fingerprinter, plus operand formatting (reg type / swizzle / neg / abs), the
`tex<n>` unit, and the inline-constant values (usually all-zero in the shipped
file - those slots are patched per-draw). Ran clean over all 1215 `ds_flver`
fragment programs.

Known rough edges: the input-attribute name on a `TEX` coordinate reads one slot
high (`f[TEX1]` where the capture shows `tc0`) - `src_attr_reg_num` handling isn't
exact, but the sampler *unit* is right; and RPCS3's compiler reallocates registers
per variant, so a line diff between two feature variants is noisy unless the change
is purely additive.

**First result - what `HemEnvLerp` actually is.** It was never captured (0 across
all five worlds), so it was read straight from the binary. Diffing
`DS_Phn_Dif______MulLitCsd_HemEnv` vs `..._HemEnvLerp`:

- plain HemEnv: `TEXR R4, H0, tex11` then `MULH H1, R4, c19` - the env-diffuse term
  is one cubemap (unit 11) times a scale.
- HemEnvLerp: adds **sampler unit 13, a second cubemap sampled by the same normal**,
  then `H5 = tex13*c25 - tex11*c19` and `H7 = H5*c31.w + tex11*c19` - i.e.
  `mix(envcube_11 * c19, envcube_13 * c25, c31.w)` with `c31.w` a per-draw scalar.

So `HemEnvLerp` = HemEnv with the environment-diffuse term **cross-faded between two
environment cubemaps by a per-draw constant**. Almost certainly an
environment-lighting *transition* (Nexus state changes as Archdemons fall, world
tendency, scripted per-area lighting) - which is why free-roaming a static area
state never triggers it. `context.md` part 18's "lerp toward a constant colour by
EnvDif cubemap alpha" was wrong on the mechanism (cubemap-to-cubemap, scalar not
alpha) and came from a differently-numbered capture session anyway.

## Stage 5 - vertex programs (`--vertex`, done)

`RsxVp.cs` decodes RSX vertex microcode - a different ISA from the fragment side:
co-issued vector (word1 bits 22-26) + scalar (bits 27-31) ops, 16 bytes/instruction,
**no** halfword swap, `D0..D3`/`SRC` bitfields per `RSXVertexProgram.h`. Ported from
`CgBinaryDisasm::TaskVP`. Constants are not inlined - a ref is `c[d1.const_src]`, or
`c[A + n]` when `d3.index_const` is set (the bone-matrix access pattern). Branches
(BRA/BRI) aren't walked; no ds_flver vpo uses one and `Fingerprint.HasBranch` flags it.

`CaptureVp.cs` reads the same shape out of `vs_main()`: `read_location(N)` = input
attribute N, `dst_regN` = output register N (`D3.dst`), `_fetch_constant(K)` /
`_fetch_constant(K + a0.x)` = constant index / relative addressing, plus `exp2(`/`sqrt(`/
`dot(`/`texture(` counts.

`LibVpName.cs` parses the filename (`DS_<fam>_<layout>_<texgen>_<pass>`): a `W` in the
layout = skinned, trailing `T`s = tangent-frame count, `D`-count / trailing `L` in the
texgen = diffuse-UV count / lightmap-UV, and `Non`/`Sdw`/`Dep`/`DepAlp` = the pass.

`ScoreVp` leans on the three sets name-stripping leaves intact: **input attributes**
(header `attributeInputMask` vs `read_location` set - the skinning split lives here),
**referenced constant indices** (the `D`/`DD`/`DL` texgen split is `c120` alone vs
`+c121` vs `+c122`), and **output registers**. Plus instr count, `indexed`/`ex2`/`txl`
agreement.

**`DS_Phn_*` and `DS_Gst_*` vertex programs are byte-identical** (verified by an exact
`disasm` diff - the ghost effect is entirely fragment-side). `VpCanonical` collapses the
family token; two candidates that reduce to the same canonical name are reported together,
not treated as a tie.

Results vs `~/.cache/rpcs3/shaderlog` (212 vertex captures, 161 library vpos):
**HIGH 143, MED 12, LOW 57, NONE 0**; 28 distinct canonical shaders identified HIGH;
coverage 34 captured / 108 near / 19 unseen (the unseen tail is Dbg wireframe programs
and rare `Ghost`/`Water` variants). Written to `vertex-names.json`.

The 57 LOW are genuine ambiguity, not scorer failure - the same kind the fragment pass
has: many `DS_Fil_*` fullscreen-quad shaders share one trivial passthrough vpo, and the
`Dep`/`DepAlp` prepass vpos are identical across texgen/tangent variants (a depth pass
does no UV/tangent work). Those surface as co-equal `candidates` in the JSON.

`verify --vertex` anchors on `VertexProgram1` -> `DS_*_PINT_D_Non` (lit map piece + fog,
one tangent set, single UV set) and a skinned sibling `DS_*_PIWN_D_Non`.

## Stage 6 - the .rrc frame capture (done)

`RrcCapture.cs` reads RPCS3's `~/.config/rpcs3/captures/*.rrc.gz` - a **whole frame** of the
RSX FIFO (not one draw), gzip around a `utils::serial` stream (VLE-length-prefixed
containers, raw-LE PODs, bitwise structs). `frame_capture_data` =
`{tile_map, memory_map, memory_data_map, display_buffers_map, replay_commands, reg_state}`;
`reg_state` (the ~74 KB tail) is left unparsed. Validated first try - parses to within 74 KB
of EOF on a 632 MB frame in 4.5 s. **A reader, not a recorder**: the `.rrc` is already on
disk, no RPCS3 hook.

`rrc` prints a summary (draw count, method-register histogram). `rrc-draws` (with a
`mounted/shader` library dir) replays the FIFO - tracking `SET_SHADER_PROGRAM`,
`SET_TRANSFORM_PROGRAM[_LOAD]`, `SET_TRANSFORM_CONSTANT[_LOAD]` - and at each `DRAW_*` hands
the bound FP/VP microcode to the same decoders + matcher and dumps the 468 vertex constant
registers. On a Nexus frame: 1724 draws, **FP 99.6 % HIGH-named, 0 FP ucode unresolved**.

FIFO details in `RrcCapture.cs`; the draw-state replay (including that
`capture_draw_memory` attaches the FP bytes to the *following* `SET_BEGIN_END(0)`) in
`RrcInterp.cs`.

**First result:** ran on four outdoor areas (Nexus, Boletaria 1-1, Stonefang 2-1, Shrine
4-1). The scattering block `c103..c115` came out per-area; `c108`/`c109` are per-channel with
ratios stable to 3 sig figs across all four (Rayleigh 1:1.30:1.87, Mie 1:1.69:3.50). That
replaced `output_stage.gdshaderinc`'s fitted `lambda^-1.5` Rayleigh weight and added the
missing Mie weight - see `docs/context.md` part 35.

`rrc-mine` is the "finished interpreter" pass: `RrcInterp` also tracks `SET_SURFACE_FORMAT`/
`_CLIP` (RT size + depth format), `SET_SURFACE_COLOR_TARGET`, `SET_COLOR_MASK`, `SET_DEPTH_MASK`,
`SET_VIEWPORT_*`. It classifies each draw's pass (COLOR / DEPTH / SHADOW = depth-only + square
RT) and attributes every constant register to the **VP microcode that references it**
(`ConstRefs`, not upload history), per-frame vs per-draw, plus the FP inline constants per
shader group. Across eight captures: shadows go to a 2048x2048 Z24S8 surface as 4x 1024x1024
tiles; the light matrices are `c[0..3]` (cast) and `c[112..115]` (`*_Sdw` receive). See
`docs/context.md` part 37.

`rrc-shadow` is the focused follow-up: it dumps the SHADOW-pass viewport tiles (origin+extent
within the 2048² surface), the distinct `c[0..3]` cast matrix per tile, the frame-constant
registers every `*_Sdw`/`*Csd` receiver references, and the `Sdw` fragment inline constants
laid out 4-per-row as candidate matrices. Established DeS's shadows as 4-split PSSM in a 2×2
1024² atlas — see `docs/context.md` part 37 and `docs/PLAN.md`'s shadow item.

`rrc-fog` replays a frame tracking `SET_FOG_MODE`/`SET_FOG_PARAMS` and, per draw, whether the
bound FP reads input attribute `FOGC` (`RsxFp.Fingerprint.InputMask` bit 3). Across eight
captures: fog methods never issued, 0 of ~22 000 draws read `FOGC`. RSX fixed-function fog is
unused by DeS - `des_fog()` models a dead mechanism. See `docs/context.md` part 36.
