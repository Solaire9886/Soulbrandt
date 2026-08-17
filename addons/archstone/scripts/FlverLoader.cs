using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

// Manual res://-bypassing loader driving FlverModelBuilder - see docs/ARCHITECTURE.md's Architecture section.
public partial class FlverLoader : RefCounted
{
	private readonly FlverModelBuilder _builder = new();
	private readonly MsbLoader _msbLoader = new();
	private readonly DrawParamReader _drawParamReader = new();

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
		string blockName = System.IO.Path.GetFileNameWithoutExtension(msbPath);
		var root = new Node3D { Name = blockName };
		foreach (var placement in _msbLoader.ReadMapPieces(msbPath))
			root.AddChild(InstantiatePlacement(placement, blockName));
		foreach (var placement in _msbLoader.ReadObjects(msbPath))
			root.AddChild(InstantiatePlacement(placement, blockName));
		return root;
	}

	private Node3D InstantiatePlacement(MsbPlacement placement, string blockName)
	{
		var inst = Instantiate(placement.ModelPath);
		inst.Name = placement.Name;
		// X negated to match FlverModelBuilder's own vertex convention (mirror of FLVER's
		// coordinate space, see docs/ARCHITECTURE.md). Rotation sign flip is a starting hypothesis to
		// compensate the same mirror - spot-checked but not fully proven, see docs/ARCHITECTURE.md's
		// "MSB map placement".
		inst.Position = new Vector3(-placement.Position.X, placement.Position.Y, placement.Position.Z);
		var rot = placement.RotationDegrees;
		inst.RotationDegrees = new Vector3(rot.X, -rot.Y, -rot.Z);
		inst.Scale = placement.Scale;
		ApplyLightBank(inst, blockName, placement.LightID);
		return inst;
	}

	// Per-instance ambient/env uniforms (hemisphere_ambient.gdshaderinc), resolved from this
	// placement's own LightID rather than the shared default_lightbank.param row-0 fallback
	// every lightmapped material's base mesh material otherwise uses - see docs/PLAN.md's
	// "Per-part lighting/fog" item. FogID/WorldEnvironment wiring is a later follow-up.
	private void ApplyLightBank(Node3D inst, string blockName, byte lightID)
	{
		if (inst.GetNodeOrNull<MeshInstance3D>("Mesh") is not MeshInstance3D meshInst || meshInst.Mesh == null)
			return;

		var row = _drawParamReader.GetLightBankRow(blockName, lightID);
		if (row == null)
			return;

		for (int i = 0; i < meshInst.Mesh.GetSurfaceCount(); i++)
		{
			if (meshInst.Mesh.SurfaceGetMaterial(i) is not ShaderMaterial baseMaterial || !HasAmbientUniforms(baseMaterial.Shader))
				continue;

			// The base material lives on the shared, cached ArrayMesh (one placed instance's
			// worth of uniforms would otherwise leak onto every other placement of the same
			// model) - Duplicate() (shallow: shares the compiled Shader, copies only parameters)
			// gives this instance its own values via a per-surface override.
			var material = (ShaderMaterial)baseMaterial.Duplicate();
			// colR/colG/colB (0-255) -> normalized color; colA (0-1000, default 100) -> intensity,
			// /100 - see ApplyColorIntensity. Overall visual correctness still pending in-editor
			// comparison against real DeS rendering. See docs/PLAN.md's "Per-part lighting/fog" item.
			ApplyColorIntensity(material, row, "colR_u", "colG_u", "colB_u", "colA_u", "ambient_up", "ambient_up_intensity");
			ApplyColorIntensity(material, row, "colR_d", "colG_d", "colB_d", "colA_d", "ambient_down", "ambient_down_intensity");
			ApplyColorIntensity(material, row, "envDif_colR", "envDif_colG", "envDif_colB", "envDif_colA", "env_color", "env_intensity");
			ApplyColorIntensity(material, row, "envSpc_colR", "envSpc_colG", "envSpc_colB", "envSpc_colA", "env_spc_color", "env_spc_intensity");
			meshInst.SetSurfaceOverrideMaterial(i, material);
		}
	}

	private static bool HasAmbientUniforms(Shader shader)
	{
		foreach (Godot.Collections.Dictionary uniform in shader.GetShaderUniformList())
			if (uniform["name"].AsStringName() == "ambient_up")
				return true;
		return false;
	}

	private static void ApplyColorIntensity(ShaderMaterial material, PARAM.Row row,
		string rField, string gField, string bField, string aField, string colorUniform, string intensityUniform)
	{
		float r = System.Convert.ToSingle(row[rField].Value) / 255f;
		float g = System.Convert.ToSingle(row[gField].Value) / 255f;
		float b = System.Convert.ToSingle(row[bField].Value) / 255f;
		// The *_colA fields are min=0/max=1000/default=100 in lightbank.paramdef (confirmed by
		// reading the real field metadata, not guessed) - a percent-style scale where 100 = 1.0x
		// baseline intensity, not a raw multiplier or an alpha-style 0-255 channel.
		float a = System.Convert.ToSingle(row[aField].Value) / 100f;
		material.SetShaderParameter(colorUniform, new Color(r, g, b));
		material.SetShaderParameter(intensityUniform, a);
	}

	public void Evict(string path) => _meshCache.Remove(path);

	public void EvictAll()
	{
		_meshCache.Clear();
		_builder.ResetCaches();
		_drawParamReader.ResetCaches();
	}
}
