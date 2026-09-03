using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

// The five DrawParam IDs are copied verbatim from MSBD.Part. FogID has no consumer - FOG_BANK
// selects RSX fixed-function fog, which DeS never uses (docs/context.md part 36) - but it's kept
// so the record stays a faithful mirror of the part structure.
public readonly record struct MsbPlacement(string ModelPath, string Name,
	Vector3 Position, Vector3 RotationDegrees, Vector3 Scale, byte LightID, byte FogID,
	byte ToneMapID, byte ToneCorrectID, byte ScatterID);

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
				part.ScatterID));
		}
		return placements;
	}
}
