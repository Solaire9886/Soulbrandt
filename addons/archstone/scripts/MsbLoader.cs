using System.Collections.Generic;
using System.Linq;
using Godot;
using SoulsFormats;

namespace Archstone;

// A MapPiece, Object or Collision part with its draw-parameter IDs as stored in MSBD.Part.
// ToneMapID and ToneCorrectID are carried but not bound per placement (those banks are
// frame-global).
public readonly record struct MsbPlacement(string ModelPath, string Name,
	Vector3 Position, Vector3 RotationDegrees, Vector3 Scale, byte LightID, byte FogID,
	byte ToneMapID, byte ToneCorrectID, byte ScatterID, int EntityID = -1, short InitAnimID = -1);

// A light event: its region's position in Godot space and its POINT_LIGHT_BANK row. Name is for
// diagnostics.
public readonly record struct PointLightPlacement(Vector3 Position, int BankRow, string Name);

// MSB parsing: placements, events and regions, resolved to paths on disk. No scene nodes.
public partial class MsbLoader : RefCounted
{
	public List<MsbPlacement> ReadMapPieces(string msbPath)
	{
		string realMsbPath = ProjectSettings.GlobalizePath(msbPath);
		var msb = MSBD.Read(realMsbPath);

		// mapstudio/{name}.msb's models are in the sibling map/{name}/ folder.
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

	// MSBD.Parts.Objects, resolved to obj/{id}/sib/{id}.flver (true for all 777 obj folders).
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

	// MSBD.Parts.Objects, resolved to obj/{id}/hkx/{id}.hkx; objects without one have no collision.
	// obj/{id}/hkx/{id}_1.hkx, the broken state of a breakable object, is not placed.
	public List<MsbPlacement> ReadObjectCollisions(string msbPath)
	{
		string realMsbPath = ProjectSettings.GlobalizePath(msbPath);
		var msb = MSBD.Read(realMsbPath);
		string objRoot = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(
			System.IO.Path.GetDirectoryName(realMsbPath)))!, "obj");

		return BuildPlacements(msb.Parts.Objects, modelName =>
		{
			string hkxPath = System.IO.Path.Combine(objRoot, modelName, "hkx", modelName + ".hkx");
			return System.IO.File.Exists(hkxPath) ? hkxPath : null;
		});
	}

	// MSBD.Parts.Collisions, resolved to map/{block}/{model}.hkx (case varies on disk). Collision
	// parts name only the h-prefixed files; the l-prefixed ones are not placed.
	public List<MsbPlacement> ReadCollisions(string msbPath)
	{
		string realMsbPath = ProjectSettings.GlobalizePath(msbPath);
		var msb = MSBD.Read(realMsbPath);
		string blockDir = System.IO.Path.Combine(
			System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(realMsbPath))!,
			System.IO.Path.GetFileNameWithoutExtension(realMsbPath));

		var hkxByName = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
		if (System.IO.Directory.Exists(blockDir))
		{
			foreach (var path in System.IO.Directory.GetFiles(blockDir))
			{
				if (path.EndsWith(".hkx", System.StringComparison.OrdinalIgnoreCase))
					hkxByName.TryAdd(System.IO.Path.GetFileNameWithoutExtension(path), path);
			}
		}

		return BuildPlacements(msb.Parts.Collisions, modelName =>
		{
			if (hkxByName.TryGetValue(modelName, out var hkxPath)) return hkxPath;
			GD.PushWarning($"MsbLoader: no .hkx for collision model '{modelName}' in '{blockDir}'");
			return null;
		});
	}

	// The (ToneMapID, ToneCorrectID) pair most of the block's collisions carry; (0, 0) without any.
	public (byte ToneMapId, byte ToneCorrectId) ReadDominantCollisionToneIds(string msbPath)
	{
		var msb = MSBD.Read(ProjectSettings.GlobalizePath(msbPath));
		return msb.Parts.Collisions
			.GroupBy(part => (part.ToneMapID, part.ToneCorrectID))
			.OrderByDescending(group => group.Count())
			.Select(group => group.Key)
			.FirstOrDefault();
	}

	// MSBD.Events.Lights. PointLightID is the region index (as SFX's UnkT00) and UnkT04 the
	// POINT_LIGHT_BANK row (-1: no light); captures confirm both (docs/context.md, "Point-light
	// positions and rows"). PartName carries no position.
	public List<PointLightPlacement> ReadPointLights(string msbPath)
	{
		var msb = MSBD.Read(ProjectSettings.GlobalizePath(msbPath));
		var lights = new List<PointLightPlacement>();
		foreach (var light in msb.Events.Lights)
		{
			var (region, _, _) = EventRegion(msb, light, light.PointLightID);
			if (region == null || light.UnkT04 < 0) continue;
			lights.Add(new PointLightPlacement(
				new Vector3(-region.Position.X, region.Position.Y, region.Position.Z), light.UnkT04, light.Name));
		}
		return lights;
	}

	// A map SFX event at its region (external ELF_ENGINE_ACCURACY_RESEARCH.md 9.4). 1,691 of 1,693
	// events resolve; unresolved ones are skipped (a part's mesh centre is not an emitter position).
	// RotationDegrees are already mirrored (alpha, -beta, -gamma); build with EulerOrder.Yzx.
	public readonly record struct SfxPlacement(Vector3 Position, Vector3 RotationDegrees,
		int EffectId, string Name, string RegionName, int RegionIndex, int EntityID = -1);

	// SoulsFormats exposes the common region index only as a resolved name (null when out of range);
	// the raw index is read so an invalid one is reported instead of replaced by the type index.
	private static readonly System.Reflection.FieldInfo CommonRegionIndex =
		typeof(MSBD.Event).GetField("RegionIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
		?? throw new System.MissingFieldException(nameof(MSBD.Event), "RegionIndex");

	internal readonly record struct SfxRegionSelection(MSBD.Region Region, int Index, string Status);

	internal static SfxRegionSelection SfxRegion(MSBD msb, MSBD.Event.SFX sfx) => EventRegion(msb, sfx, sfx.UnkT00);

	// The common region index when nonnegative, else the type's own (SFX UnkT00, Light PointLightID).
	private static SfxRegionSelection EventRegion(MSBD msb, MSBD.Event e, int typeIndex)
	{
		var regions = msb.Regions.Regions;
		int common = (int)CommonRegionIndex.GetValue(e);
		if (common >= 0)
			return common < regions.Count
				? new(regions[common], common, "Resolved common region index")
				: new(null, common, "Invalid common region index");
		return typeIndex >= 0 && typeIndex < regions.Count
			? new(regions[typeIndex], typeIndex, "Resolved type region index")
			: new(null, typeIndex, "Unresolved type region index");
	}

	public List<SfxPlacement> ReadSfxPlacements(string msbPath)
	{
		var msb = MSBD.Read(ProjectSettings.GlobalizePath(msbPath));
		var placements = new List<SfxPlacement>();
		foreach (var sfx in msb.Events.SFX)
		{
			var (region, index, _) = SfxRegion(msb, sfx);
			if (region == null) continue;
			placements.Add(new SfxPlacement(
				new Vector3(-region.Position.X, region.Position.Y, region.Position.Z),
				new Vector3(region.Rotation.X, -region.Rotation.Y, -region.Rotation.Z),
				sfx.EffectID, sfx.Name, region.Name, index, sfx.EntityID));
		}
		return placements;
	}

	// An MSB box region (origin at the bottom centre). Mirrored angles, composed Y-Z-X.
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
				new Transform3D(Basis.FromEuler(rotation * (Mathf.Pi / 180), EulerOrder.Yzx), position), size));
		}
		return boxes;
	}

	private static List<MsbPlacement> BuildPlacements(
		IEnumerable<MSBD.Part> parts, System.Func<string, string> resolveModelPath)
	{
		var placements = new List<MsbPlacement>();
		foreach (var part in parts)
		{
			string modelPath = resolveModelPath(part.ModelName);
			if (modelPath == null) continue;

			// System.Numerics vectors from SoulsFormats; mirroring happens in FlverLoader.
			placements.Add(new MsbPlacement(
				ProjectSettings.LocalizePath(modelPath),
				part.Name,
				new Vector3(part.Position.X, part.Position.Y, part.Position.Z),
				new Vector3(part.Rotation.X, part.Rotation.Y, part.Rotation.Z),
				new Vector3(part.Scale.X, part.Scale.Y, part.Scale.Z),
				part.LightID,
				part.FogID,
				part.ToneMapID,
				part.ToneCorrectID,
				part.ScatterID, part.EntityID,
				// Objects only: the a00_ clip they start in (-1: none).
				part is MSBD.Part.ObjectBase obj ? obj.InitAnimID : (short)-1));
		}
		return placements;
	}
}
