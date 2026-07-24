using System.Collections.Generic;
using Godot;

namespace Archstone;

// Manual res://-bypassing loader driving FlverModelBuilder - see CLAUDE.md's Architecture section.
public partial class FlverLoader : RefCounted
{
	private readonly FlverModelBuilder _builder = new();

	// Null = this path builds to zero surfaces (some obj/ FLVER0 files are meshless dummy markers).
	private readonly Dictionary<string, ArrayMesh?> _meshCache = new();

	public Node3D Instantiate(string path)
	{
		if (!_meshCache.TryGetValue(path, out var mesh))
		{
			var importerMesh = _builder.BuildMesh(path, out bool anySurface);
			// ImporterMesh doesn't render on its own - see CLAUDE.md. GetMesh() converts it.
			mesh = anySurface ? importerMesh.GetMesh() : null;
			_meshCache[path] = mesh;
		}

		var root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(path) };
		if (mesh != null)
			root.AddChild(new MeshInstance3D { Mesh = mesh, Name = "Mesh" });
		return root;
	}

	public void Evict(string path) => _meshCache.Remove(path);
	public void EvictAll() => _meshCache.Clear();
}
