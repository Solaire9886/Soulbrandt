using System.Collections.Generic;
using System.Linq;
using Godot;
using SoulsFormats;

namespace Archstone;

// The five DrawParam IDs are copied verbatim from MSBD.Part. FogID selects a FOG_BANK row - NOT
// RSX fixed-function fog (which DeS never uses, docs/context.md part 36), but the bank's colour /
// distance / degRotW, which drive output_stage.gdshaderinc's des_fog hand-rolled distance fade
// (docs/context.md part 45). Resolved in FlverLoader.ApplyDrawParams -> ApplyFogBank.
public readonly record struct MsbPlacement(string ModelPath, string Name,
	Vector3 Position, Vector3 RotationDegrees, Vector3 Scale, byte LightID, byte FogID,
	byte ToneMapID, byte ToneCorrectID, byte ScatterID, int EntityID = -1);

// A real MSBD.Events.Light: torch/campfire/candle etc, anchored to an already-placed MapPiece/
// Object part rather than a floating region. PointLightID resolves against POINT_LIGHT_BANK via
// DrawParamReader.GetPointLightBankRow (mod 64, not a direct index - see docs/context.md part 52).
// Name is the light event's own (Japanese) label, kept for diagnostics only.
//
// Position resolution differs by anchor type - see docs/ARCHITECTURE.md's POINT_LIGHT_BANK entry
// for why (MapPiece MSB transforms are always identity) and its known approximation error (no
// FLVER Dummy attach points to disambiguate multiple lights on one piece). A MapPiece anchor
// carries its .flver path instead of a position; FlverLoader resolves the smallest sub-mesh's AABB
// centre once it's loaded that model anyway. An Object anchor's own position is used directly.
public readonly record struct PointLightPlacement(Vector3 Position, string? FlverPath, int PointLightID, string Name);

// Reads a .msb's map-piece placements and resolves each one to a .flver path on disk.
// No scene-node concerns - see docs/ARCHITECTURE.md's FlverModelBuilder/FlverLoader split, mirrored here.
public partial class MsbLoader : RefCounted
{
	public List<MsbPlacement> ReadMapPieces(string msbPath)
	{
		string realMsbPath = ProjectSettings.GlobalizePath(msbPath);
		var msb = MSBD.Read(realMsbPath);

		// mapstudio/{name}.msb has its models directly in a sibling map/{name}/ folder.
		string blockName = System.IO.Path.GetFileNameWithoutExtension(realMsbPath);
		string blockDir = System.IO.Path.Combine(
			System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(realMsbPath))!, blockName);

		if (!System.IO.Directory.Exists(blockDir))
		{
			GD.PushWarning($"MsbLoader: block folder not found for '{msbPath}' (expected '{blockDir}')");
			return new List<MsbPlacement>();
		}

		var flverByName = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
		foreach (var path in System.IO.Directory.GetFiles(blockDir, "*.flver"))
			flverByName.TryAdd(System.IO.Path.GetFileNameWithoutExtension(path), path);

		return BuildPlacements(msb.Parts.MapPieces, modelName =>
		{
			if (flverByName.TryGetValue(modelName, out var flverPath)) return flverPath;
			GD.PushWarning($"MsbLoader: no .flver for model '{modelName}' in '{blockDir}'");
			return null;
		});
	}

	// MSBD.Parts.Objects - props/decorations/interactible scenery, distinct from MapPieces.
	// Resolves against the mounted obj/ corpus (a model-ID-per-folder scheme) instead of a map
	// block folder - a real, deterministic 1:1 convention (obj/{id}/sib/{id}.flver, confirmed
	// across all 777 real mounted obj/ folders, no exceptions), not the multi-rule CandidateDirs
	// chain FlverModelBuilder needs for *textures* - that's unrelated and already handled
	// downstream once FlverLoader.Instantiate() parses the resolved .flver.
	public List<MsbPlacement> ReadObjects(string msbPath)
	{
		string realMsbPath = ProjectSettings.GlobalizePath(msbPath);
		var msb = MSBD.Read(realMsbPath);

		// mapstudio/{name}.msb -> map/ -> mounted root; obj/ is a mounted-root-level sibling of map/.
		string mountedRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(
			System.IO.Path.GetDirectoryName(realMsbPath)))!;
		string objRoot = System.IO.Path.Combine(mountedRoot, "obj");

		return BuildPlacements(msb.Parts.Objects, modelName =>
		{
			string flverPath = System.IO.Path.Combine(objRoot, modelName, "sib", modelName + ".flver");
			if (System.IO.File.Exists(flverPath)) return flverPath;
			GD.PushWarning($"MsbLoader: no .flver for object model '{modelName}' (expected '{flverPath}')");
			return null;
		});
	}

	// MSBD.Events.Lights - each anchored to an already-placed Part by name (PartName), not a
	// region (RegionName is always empty on these - confirmed real, not a parsing gap). Both
	// MapPieces and Objects are searched, since every real light anchor found in m02 is a MapPiece
	// but there's no reason a brazier-style Object couldn't carry one on another map. A PartName
	// that resolves to neither is skipped - the anchor might be a Collision/Navmesh/other Part type
	// this project doesn't place at all. See PointLightPlacement's own doc comment for why MapPiece
	// and Object anchors resolve their position completely differently.
	public List<PointLightPlacement> ReadPointLights(string msbPath)
	{
		string realMsbPath = ProjectSettings.GlobalizePath(msbPath);
		var msb = MSBD.Read(realMsbPath);
		string blockName = System.IO.Path.GetFileNameWithoutExtension(realMsbPath);
		string blockDir = System.IO.Path.Combine(
			System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(realMsbPath))!, blockName);

		// Case-insensitive on purpose, matching ReadMapPieces: MSBD.Part.ModelName ("m3401B0") and
		// the actual extracted filename ("m3401b0.flver") disagree in case often enough that a
		// direct Path.Combine + File.Exists silently fails on a case-sensitive filesystem.
		var flverByName = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
		if (System.IO.Directory.Exists(blockDir))
			foreach (var path in System.IO.Directory.GetFiles(blockDir, "*.flver"))
				flverByName.TryAdd(System.IO.Path.GetFileNameWithoutExtension(path), path);

		var mapPieceFlverPath = new Dictionary<string, string>();
		foreach (var p in msb.Parts.MapPieces)
			if (flverByName.TryGetValue(p.ModelName, out var flverPath))
				mapPieceFlverPath[p.Name] = flverPath;
		var objectPosition = new Dictionary<string, Vector3>();
		foreach (var p in msb.Parts.Objects)
			objectPosition.TryAdd(p.Name, new Vector3(p.Position.X, p.Position.Y, p.Position.Z));

		var lights = new List<PointLightPlacement>();
		foreach (var light in msb.Events.Lights)
		{
			if (light.PartName == null) continue;
			if (mapPieceFlverPath.TryGetValue(light.PartName, out var flverPath))
				lights.Add(new PointLightPlacement(default, ProjectSettings.LocalizePath(flverPath), light.PointLightID, light.Name));
			else if (objectPosition.TryGetValue(light.PartName, out var pos))
				lights.Add(new PointLightPlacement(pos, null, light.PointLightID, light.Name));
		}
		return lights;
	}

	// SFX's type-specific UnkT00 is an index into POINT_PARAM_ST, separate from the
	// common Event.RegionName. Corpus: 1622 valid indices / 1693 events; the remaining
	// 71 are -1. 1376 region names match exactly, including 274/278 Nexus events.
	// PartName is NOT an emitter position. Using its mesh centre collapsed 180 Nexus
	// candle events onto one point. Keep unresolved events out of the render path.
	public readonly record struct SfxPlacement(Vector3 Position, Vector3 RotationDegrees,
		int EffectId, string Name, string RegionName, int RegionIndex, int EntityID = -1);

	internal static MSBD.Region SfxRegion(MSBD msb, MSBD.Event.SFX sfx)
	{
		var regions = msb.Regions.Regions;
		return sfx.UnkT00 >= 0 && sfx.UnkT00 < regions.Count ? regions[sfx.UnkT00] : null;
	}

	public List<SfxPlacement> ReadSfxPlacements(string msbPath)
	{
		var msb = MSBD.Read(ProjectSettings.GlobalizePath(msbPath));
		var placements = new List<SfxPlacement>();
		foreach (var sfx in msb.Events.SFX)
		{
			var region = SfxRegion(msb, sfx);
			if (region == null) continue;
			placements.Add(new SfxPlacement(
				new Vector3(-region.Position.X, region.Position.Y, region.Position.Z),
				new Vector3(region.Rotation.X, -region.Rotation.Y, -region.Rotation.Z),
				sfx.EffectID, sfx.Name, region.Name, sfx.UnkT00, sfx.EntityID));
		}
		return placements;
	}

	// MSB boxes have their origin at the bottom centre, unlike particle emitter boxes.
	// Rotation/handedness follow the same placement convention as other MSB entities.
	public readonly record struct RegionBox(int EntityID, string Name, Transform3D Transform, Vector3 Size)
	{
		public bool Contains(Vector3 mapPosition)
		{
			if (!mapPosition.IsFinite()) return false;
			var local = Transform.AffineInverse() * mapPosition;
			return System.Math.Abs(local.X) <= Size.X * 0.5f && local.Y >= 0 &&
				local.Y <= Size.Y && System.Math.Abs(local.Z) <= Size.Z * 0.5f;
		}
	}

	public List<RegionBox> ReadRegionBoxes(string msbPath)
	{
		var map = MSBD.Read(ProjectSettings.GlobalizePath(msbPath));
		var boxes = new List<RegionBox>();
		foreach (var group in map.Regions.Regions.Where(r => r.EntityID >= 0).GroupBy(r => r.EntityID))
		{
			if (group.Count() != 1) continue;
			var region = group.Single();
			if (region.Shape is not MSB.Shape.Box box) continue;
			var size = new Vector3(box.Width, box.Height, box.Depth);
			var position = new Vector3(-region.Position.X, region.Position.Y, region.Position.Z);
			var rotation = new Vector3(region.Rotation.X, -region.Rotation.Y, -region.Rotation.Z);
			if (!size.IsFinite() || !position.IsFinite() || !rotation.IsFinite() ||
				size.X <= 0 || size.Y <= 0 || size.Z <= 0) continue;
			boxes.Add(new RegionBox(region.EntityID, region.Name,
				new Transform3D(Basis.FromEuler(rotation * (Mathf.Pi / 180)), position), size));
		}
		return boxes;
	}

	private static List<MsbPlacement> BuildPlacements(
		IEnumerable<MSBD.Part> parts, System.Func<string, string> resolveFlverPath)
	{
		var placements = new List<MsbPlacement>();
		foreach (var part in parts)
		{
			string flverPath = resolveFlverPath(part.ModelName);
			if (flverPath == null) continue;

			// part.Position/Rotation/Scale are System.Numerics.Vector3 (SoulsFormats), not Godot's.
			placements.Add(new MsbPlacement(
				ProjectSettings.LocalizePath(flverPath),
				part.Name,
				new Vector3(part.Position.X, part.Position.Y, part.Position.Z),
				new Vector3(part.Rotation.X, part.Rotation.Y, part.Rotation.Z),
				new Vector3(part.Scale.X, part.Scale.Y, part.Scale.Z),
				part.LightID,
				part.FogID,
				part.ToneMapID,
				part.ToneCorrectID,
				part.ScatterID, part.EntityID));
		}
		return placements;
	}
}
