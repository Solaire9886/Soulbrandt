# Soulbrandt

A native Godot 4 (.NET/C#) asset-import pipeline for **Demon's Souls (PS3)** — reads
FromSoftware's proprietary formats (FLVER0 meshes, TPF textures, MTD materials) directly
into Godot resources, no intermediate OBJ/PNG conversion step. This is Phase 1 of a larger
goal: a full Demon's Souls recreation in Godot that bundles no proprietary game assets,
relying entirely on each user's own legally-owned copy of the game.

**No game assets are included in this repo, and none ever will be.** Everything here is
original code that reads a format; you provide your own copy of the game.

## Disclaimer

- This project is not affiliated with, endorsed by, or connected to Sony Interactive
  Entertainment, FromSoftware, Bandai Namco, or any other rights holder.
- This project does not condone or support piracy in any form. It does not distribute,
  host, or link to game assets, ROMs, ISOs, or copyrighted files of any kind, and never
  will.
- This project does not implement, provide, or document any method of bypassing disc
  encryption or other copy protection. It only reads container/asset formats
  (BND/DCX/FLVER/TPF) from files you already have — how you obtain an authentic, legal
  copy of your own game is entirely your own responsibility and outside this project's scope.
- Using this software requires you to own a legitimate dumped copy of Demon's Souls.
  No extraction guides, decryption keys, or pre-extracted files are provided here.
- This is an independent reverse-engineering effort undertaken for interoperability and
  preservation purposes, provided with no warranty of any kind.

## What works today

- Native FLVER0 → `ImporterMesh`/`StandardMaterial3D` import (correct coordinate space,
  winding, UVs — verified against real gameplay footage, not just "looks right in editor")
- Texture resolution across every asset category (`chr`, `map`, `obj`, `parts`), including
  cross-category texture reuse and per-map-area texture atlases
- Alpha modes (cutout / soft blend / additive glow) driven by FromSoft's own MTD naming
  convention
- Custom shaders for terrain ground-blending and water (reflection/refraction/depth-fade)
- An in-editor and headless **asset mounting** system: point it at your own raw PS3
  game directory and it unpacks the game's BND/DCX containers directly, in-process — no
  external unpacking tool needed

See `CLAUDE.md` for the full architecture writeup, `PLAN.md` for the roadmap, and
`context.md` for the development history behind the trickier decisions.

## Contributing

Want to help? See `CONTRIBUTING.md` for the contributor workflow.

## Requirements

- Godot 4.7 (.NET/Mono build)
- .NET SDK 8.0 (Godot's C# tooling requires it specifically — see `CLAUDE.md` if you're
  on a machine that only ships newer .NET runtimes)
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
3. Open the project in Godot, use the **Mount...** action in the editor toolbar to point
   it at your own raw game directory, then import.

## License

GPLv3 — see `LICENSE`. This follows from vendoring
[SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT), which is itself GPLv3
licensed with no linking exception; since this project links against it directly, the
combined work is GPLv3 as a whole.

## Credits

- [SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT) and the broader
  [soulsmods](https://github.com/soulsmods) community for reverse-engineering FromSoftware's
  file formats
