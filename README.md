# Soulbrandt

A native Godot 4 (.NET/C#) asset-import pipeline for Demon's Souls (2009) — reads
FromSoftware's proprietary formats (FLVER0 meshes, TPF textures, MTD materials, MSB level
layouts) directly into Godot resources, no intermediate OBJ/PNG conversion step and no
external unpacking tool. This is Phase 1 of a larger goal: a full Demon's Souls recreation
in Godot that bundles no proprietary game assets, relying entirely on each user's own
legally-owned copy of the game.

**No game assets are included in this repo, and none ever will be.** Everything here is
original code that reads a format; you provide your own copy of the game.

## Disclaimer

- This project is not affiliated with, endorsed by, or connected to Sony Interactive
  Entertainment (Japan publisher, current worldwide publisher of the 2020 PS5 remake, and
  owner of the Demon's Souls IP), FromSoftware (developer), Atlus (original North American
  publisher), Bandai Namco (original PAL-region publisher), or any other rights holder.
- This project does not condone or support piracy in any form. It does not distribute,
  host, or link to game assets, ROMs, ISOs, or copyrighted files of any kind, and never
  will.
- This project does not implement, provide, or document any method of bypassing disc
  encryption or other copy protection. It only reads container/asset formats
  (BND/DCX/FLVER/TPF/MSB) from files you already have — how you obtain an authentic, legal
  copy of your own game is entirely your own responsibility and outside this project's scope.
- Using this software requires you to own a legitimate dumped copy of Demon's Souls.
  No extraction guides, decryption keys, or pre-extracted files are provided here.
- Dumping your own copy is subject to your own country's software-copying and
  DRM-circumvention laws, which vary by jurisdiction. Proceeding is entirely your own
  discretion; this project and its contributors are not responsible for your actions.
- This is an independent reverse-engineering effort undertaken for preservation and
  educational purposes, provided with no warranty of any kind.

## What works today

- Native FLVER0 → `ArrayMesh`/`MeshInstance3D` import (correct coordinate space, winding,
  UVs, rigid mesh-to-node binding — verified against real gameplay footage, not just
  "looks right in editor")
- Texture resolution across every asset category (`chr`, `map`, `obj`, `parts`), including
  cross-category reuse, per-map-area texture atlases, and corrupt/missing-source-data
  handling
- Real materials, not placeholders: alpha modes (cutout / soft blend / additive) driven by
  FromSoft's own MTD naming, chr/parts specular roughness from real per-material data,
  terrain ground-blending, reflective/refractive water, and lightmapped surfaces using the
  real DeS shading formula (hemisphere ambient + a lightmap-scaled environment term),
  confirmed against decompiled DeS shader bytecode, not guessed
- **MSB level-layout parsing**: map pieces and placed objects/props load at their real
  positions with real per-instance lighting (each placement resolves its own `LightID` into
  real `LIGHT_BANK` ambient/environment data), not a shared global guess
- An in-editor and headless **asset mounting** system: point it at your own raw PS3 game
  directory and it unpacks the game's BND/DCX containers directly, in-process, from the
  Archstone editor toolbar — no external unpacking tool needed
- A manual, on-demand **load workflow** ("Load Model(s)...", "Load Folder...", "Load
  Map...") instead of Godot's own reimport pipeline — a deliberate architectural choice,
  see `docs/ARCHITECTURE.md`'s "Standing priority" section for why

See `docs/ARCHITECTURE.md` for the full architecture writeup, `docs/PLAN.md` for the
roadmap, and `docs/context.md` for the development history behind the trickier decisions.

## Not yet implemented

Skeletal animation, physics/collision, navmesh, and gameplay systems don't exist yet — this
is still an asset importer, not a game. These are real, scoped gaps tracked in
`docs/PLAN.md`'s roadmap and `docs/ARCHITECTURE.md`'s "Known deferred work" section, not
just unlisted TODOs.

## Contributing

Want to help? See `CONTRIBUTING.md` for the contributor workflow.

## Requirements

- Godot 4.7 (.NET/Mono build)
- .NET SDK 8.0 (Godot's C# tooling requires it specifically — see `docs/ARCHITECTURE.md`
  if you're on a machine that only ships newer .NET runtimes)
- Your own legal copy of Demon's Souls (PS3), dumped and unmodified

## Getting started

1. Clone with submodules (this project vendors a patched fork of
   [SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT)):
   ```
   git clone --recurse-submodules git@github.com:Solaire9886/Soulbrandt.git
   ```
   (or `git submodule update --init` after a plain clone)
2. Build the C# assembly:
   ```
   dotnet build Soulbrandt.csproj
   ```
3. Open the project in Godot. In the Archstone toolbar: **Mount...** to point it at your
   own raw game directory, then **Import** (full import, or choose categories).
4. Still in the Archstone toolbar: **Load Map...** to place a real level with lighting, or
   **Load Model(s).../Load Folder...** for individual assets. Nothing loads automatically —
   see `docs/ARCHITECTURE.md`'s "Architecture" section for why.

## License

GPLv3 — see `LICENSE`. This follows from vendoring
[SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT), which is itself GPLv3
licensed with no linking exception; since this project links against it directly, the
combined work is GPLv3 as a whole.

## Credits

- [SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT) and the broader
  [soulsmods](https://github.com/soulsmods) community for reverse-engineering FromSoftware's
  file formats
- [RPCS3](https://rpcs3.net/) for making it possible to run and study Demon's Souls on PC at
  all, and for shader-capture tooling that helped confirm this project's own material work
  against real DeS bytecode
