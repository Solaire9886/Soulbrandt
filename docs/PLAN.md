# PLAN.md

Roadmap: what Soulbrandt is building and in what order. `docs/ARCHITECTURE.md` describes the
current state; `docs/context.md` records how it got there.

## Mission

A **full recreation of Demon's Souls (2009, PS3) in Godot**, in the spirit of asset-required
fan recreations such as Daggerfall Unity. The project never contains FromSoftware code or
assets: everything comes from the user's own copy, read at run time. A feature that only works
by bundling or redistributing original data is the wrong design.

## Phase 1: asset bridge (current)

Goal: read every asset type the game ships into native Godot resources, well enough that a
real map area with correctly textured, animated characters and geometry can be opened and
explored in the editor. Phase 1 is done when that works, not when every format is supported.

**Done**
- Extraction from the user's game copy in the editor or headless (`AssetExtractor`), no
  external tools.
- FLVER0 meshes, TPF textures and cubemaps, MTD-driven material routing, through a manual
  loader rather than Godot's import system.
- MSB map pieces and objects with engine-accurate transforms; SFX and point-light events.
- Map and object collision: a Havok packfile reader in the fork, and every MSB collision and
  object part as a Godot static body.
- The map rendering pipeline reconstructed from the shader library, frame captures and
  executable specifications: HemEnv/HemDir3 lighting, lightmaps, env cubemaps, point lights,
  fog, scattering, water, sky, exposure adaptation, bloom, tone correction, and a static sun
  shadow.
- Effect (`.ffx`) parsing for the whole corpus, and bounded billboard playback in the editor
  with native placement, emitters and motion where recovered.

**Priority (user decision, 2026-09-06).** Most remaining graphics work needs a moving,
gameplay-matched camera to implement and verify: four-split shadows, water viewing-angle
accuracy, and anything tuned against the player's view. So the next major step is game-side:
**Havok collision parsing (done 2026-09-27) and a real player and camera**. Graphics and VFX
items that do not need a camera may still be taken on in between (the 2026-09-14 to 09-26 VFX
and lighting work was such an interlude).

**Next, roughly in order**
1. **A player and camera** on the map collision.
2. **More Havok content on the same reader**, one class family at a time: skeletons
   (`hkaSkeleton`; object shapes are done, as static collision), then animation
   (`hkaAnimationContainer`; DeS animations are wavelet-compressed) and posing (`InitAnimID`
   on placed objects). Reference: `Grimrukh/soulstruct-havok` (GPL, Python, DeS-specific).
3. **Map assembly:** navmesh (`NVM` already reads it), enemy/player placement once game logic
   exists (deliberately deferred until then), draw-group visibility (semantics unknown for
   DeS; needs a capture comparison), LOD selection, event scripts (`script/` Lua and `.esd`),
   destructible debris (`map/breakobj/*.breakobj`), cutscenes (`remo/`).
4. **A runtime loader** for exported builds: no editor, no pre-extraction step, reading the
   player's own copy at play time. `AssetExtractor` and the loaders have no editor
   dependency, so the parsing code carries over.

**Graphics items that need a camera first:** four-split perspective shadows, the player's
point light and per-frame light selection, DoF, motion blur, lens flare, `HemEnvLerp`
transitions.

**Graphics and VFX items that do not:** light-shaft edge softness; ghost/dissolve materials;
VFX activation, container motion, the model and light primitives and the remaining templates (see
`docs/ARCHITECTURE.md`, "Effects").

## Phase 2: game recreation (not yet designed)

Assembling imported pieces into playable levels and building the systems to play: movement,
combat, AI, items, UI, saves. Where the data lives, from a preliminary look: gameplay numbers
in PARAM tables with shipped paramdefs; map event logic and enemy AI as plain Lua under
`script/`; per-animation timed events (hit windows, invincibility frames) in `.tae` files that
`SoulsFormatsNEXT` reads (`TAEFormat.DES`). Two gaps: TAE event types have no names without a
template, and precise invincibility-frame counts are not documented publicly for DeS and will
need frame-by-frame observation in RPCS3. DeS's poise-like mechanic is internally "Super
Armor"; do not port Dark Souls terminology.

## Standing constraints

- No game code, assets or extracted data are ever committed, in any form.
- Stability and resource safety in extraction and loading are a standing priority (see
  `docs/ARCHITECTURE.md`): a regular user must be able to run this unsupervised against a full
  game copy.
