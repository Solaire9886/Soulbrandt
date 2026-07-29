using System.Collections.Generic;
using Godot;

namespace Archstone;

// Manual res://-bypassing loader driving FlverModelBuilder - see docs/ARCHITECTURE.md's Architecture section.
public partial class FlverLoader : RefCounted
{
	private readonly FlverModelBuilder _builder = new();
	private readonly MsbLoader _msbLoader = new();

	// Null = this path builds to zero surfaces (some obj/ FLVER0 files are meshless dummy markers).
	private readonly Dictionary<string, ArrayMesh?> _meshCache = new();

	public Node3D Instantiate(string path)
	{
		if (!_meshCache.TryGetValue(path, out var mesh))
		{
			var importerMesh = _builder.BuildMesh(path, out bool anySurface);
			// ImporterMesh doesn't render on its own - see docs/ARCHITECTURE.md. GetMesh() converts it.
			mesh = anySurface ? importerMesh.GetMesh() : null;
			_meshCache[path] = mesh;
		}

		var root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(path) };
		if (mesh != null)
			root.AddChild(new MeshInstance3D { Mesh = mesh, Name = "Mesh" });
		return root;
	}

	public Node3D InstantiateMap(string msbPath)
	{
		var root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(msbPath) };
		foreach (var placement in _msbLoader.ReadMapPieces(msbPath))
		{
			var inst = Instantiate(placement.ModelPath);
			inst.Name = placement.Name;
			// X negated to match FlverModelBuilder's own vertex convention (mirror of FLVER's
			// coordinate space, see docs/ARCHITECTURE.md). Rotation sign flip is a starting hypothesis to
			// compensate the same mirror - not yet visually confirmed, see docs/PLAN.md/docs/context.md.
			inst.Position = new Vector3(-placement.Position.X, placement.Position.Y, placement.Position.Z);
			var rot = placement.RotationDegrees;
			inst.RotationDegrees = new Vector3(rot.X, -rot.Y, -rot.Z);
			inst.Scale = placement.Scale;
			root.AddChild(inst);
		}
		return root;
	}

	public void Evict(string path) => _meshCache.Remove(path);

	public void EvictAll()
	{
		_meshCache.Clear();
		_builder.ResetCaches();
	}
}
