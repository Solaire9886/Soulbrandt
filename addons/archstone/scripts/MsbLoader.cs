using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

public readonly record struct MsbPlacement(string ModelPath, string Name,
	Vector3 Position, Vector3 RotationDegrees, Vector3 Scale);

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

		var placements = new List<MsbPlacement>();
		if (!System.IO.Directory.Exists(blockDir))
		{
			GD.PushWarning($"MsbLoader: block folder not found for '{msbPath}' (expected '{blockDir}')");
			return placements;
		}

		var flverByName = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
		foreach (var path in System.IO.Directory.GetFiles(blockDir, "*.flver"))
			flverByName.TryAdd(System.IO.Path.GetFileNameWithoutExtension(path), path);

		foreach (var part in msb.Parts.MapPieces)
		{
			if (!flverByName.TryGetValue(part.ModelName, out var flverPath))
			{
				GD.PushWarning($"MsbLoader: no .flver for model '{part.ModelName}' in '{blockDir}'");
				continue;
			}

			// part.Position/Rotation/Scale are System.Numerics.Vector3 (SoulsFormats), not Godot's.
			placements.Add(new MsbPlacement(
				ProjectSettings.LocalizePath(flverPath),
				part.Name,
				new Vector3(part.Position.X, part.Position.Y, part.Position.Z),
				new Vector3(part.Rotation.X, part.Rotation.Y, part.Rotation.Z),
				new Vector3(part.Scale.X, part.Scale.Y, part.Scale.Z)));
		}

		return placements;
	}
}
