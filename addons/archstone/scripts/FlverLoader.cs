using System.Collections.Generic;
using System.Linq;
using Godot;
using SoulsFormats;

namespace Archstone;

// Loads FLVERs and maps into scene nodes, outside Godot's import system (docs/ARCHITECTURE.md,
// "Code layout"). Also binds each placement's draw parameters.
public partial class FlverLoader : RefCounted
{
	private readonly FlverModelBuilder _builder = new();
	private readonly MsbLoader _msbLoader = new();
	private readonly DrawParamReader _drawParamReader = new();

	// Null: the file has no surfaces (some obj FLVERs are dummy-only markers).
	private readonly Dictionary<string, ArrayMesh?> _meshCache = new();

	// Map materials read exposure and tone correction from shader globals; until a map's
	// PostProcessPipeline drives them they hold a fixed exposure and a neutral correction.
	public FlverLoader() => PostProcessPipeline.BindFallback();

	// MSB point lights reach objects and characters, never map pieces: in every RPCS3 capture a
	// map-piece draw carries only the player's light, even beside a lit campfire (docs/context.md,
	// "Point lights skip map pieces"). True departs from the game so map geometry takes them too.
	public bool PointLightsOnMapPieces { get; set; }

	// A light event in Godot space with its bank colour (premultiplied by colA) and falloff. Resolved
	// per InstantiateMap and emptied for a standalone load, so no map's lights leak onto another.
	private readonly record struct ResolvedPointLight(Vector3 Position, Vector3 Color, float DwindleBegin, float DwindleEnd);
	private List<ResolvedPointLight> _mapPointLights = new();

	public Node3D Instantiate(string path)
	{
		if (!_meshCache.TryGetValue(path, out var mesh))
		{
			var importerMesh = _builder.BuildMesh(path, out bool anySurface);
			// ImporterMesh does not render; GetMesh() converts it.
			mesh = anySurface ? importerMesh.GetMesh() : null;
			_meshCache[path] = mesh;
		}

		var root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(path) };
		if (mesh != null)
			root.AddChild(new MeshInstance3D { Mesh = mesh, Name = "Mesh" });
		return root;
	}

	// "Load Model(s)" and "Load Folder": no MSB, so row 0 of each default_* bank is bound (a default
	// MsbPlacement has all-zero IDs), which lights the model instead of leaving shader defaults.
	public Node3D InstantiateWithDefaultDrawParams(string path)
	{
		_mapPointLights = EmptyPointLights; // no MSB in this load path, so no light events to resolve
		var inst = Instantiate(path);
		ApplyDrawParams(inst, "default_", default);
		return inst;
	}

	private static readonly List<ResolvedPointLight> EmptyPointLights = new();

	public Node3D InstantiateMap(string msbPath)
	{
		string blockName = System.IO.Path.GetFileNameWithoutExtension(msbPath);
		_mapPointLights = ResolvePointLights(msbPath, blockName);
		var root = new Node3D { Name = blockName };
		root.AddChild(BuildEnvironment());
		root.AddChild(BuildPostProcess(msbPath, blockName));
		var casters = new Godot.Collections.Array<MeshInstance3D>();
		foreach (var placement in _msbLoader.ReadMapPieces(msbPath))
			root.AddChild(CollectCaster(InstantiatePlacement(placement, blockName, PointLightsOnMapPieces), casters));
		foreach (var placement in _msbLoader.ReadObjects(msbPath))
			root.AddChild(CollectCaster(InstantiatePlacement(placement, blockName, true), casters));
		AttachShadowRenderer(root, blockName, casters);
		root.AddChild(MapCollision.Build("Collision", _msbLoader.ReadCollisions(msbPath)));
		root.AddChild(MapCollision.Build("ObjectCollision", _msbLoader.ReadObjectCollisions(msbPath)));
		// Disabled until enabled in the inspector.
		root.AddChild(new MapSfxPreview { Name = "MapSfxPreview", MapPath = msbPath });
		return root;
	}

	// Light events -> POINT_LIGHT_BANK rows, once per map. A light whose row does not resolve is
	// dropped rather than given a default.
	private List<ResolvedPointLight> ResolvePointLights(string msbPath, string blockName)
	{
		var resolved = new List<ResolvedPointLight>();
		foreach (var light in _msbLoader.ReadPointLights(msbPath))
		{
			var row = _drawParamReader.GetPointLightBankRow(blockName, light.BankRow);
			if (row == null) continue;
			Vector3 color = Vec3(row, "colR", "colG", "colB") / 255f
				* (System.Convert.ToSingle(row["colA"].Value) / 100f);
			resolved.Add(new ResolvedPointLight(light.Position, color,
				System.Convert.ToSingle(row["dwindleBegin"].Value),
				System.Convert.ToSingle(row["dwindleEnd"].Value)));
		}
		return resolved;
	}

	private static Node3D CollectCaster(Node3D placement, Godot.Collections.Array<MeshInstance3D> casters)
	{
		if (placement.GetNodeOrNull<MeshInstance3D>("Mesh") is MeshInstance3D mesh && mesh.Mesh != null
			&& CastsSunShadow(mesh))
			casters.Add(mesh);
		return placement;
	}

	// Lit surfaces cast (the shadow_strength uniform, the same test receivers use); sky domes, unlit
	// materials and water do not (a sky dome would swamp the shadow region).
	private static bool CastsSunShadow(MeshInstance3D mesh)
	{
		for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
		{
			var material = (mesh.GetSurfaceOverrideMaterial(surface)
				?? mesh.Mesh.SurfaceGetMaterial(surface)) as ShaderMaterial;
			if (material != null && HasUniform(material.Shader, "shadow_strength"))
				return true;
		}
		return false;
	}

	// One static sun-shadow pass per map from SHADOW_BANK row 0, the map baseline (parts may name
	// other rows by ShadowID). The region is sized from the casters, not the row.
	private void AttachShadowRenderer(Node3D root, string blockName, Godot.Collections.Array<MeshInstance3D> casters)
	{
		if (casters.Count == 0)
			return;
		var row = _drawParamReader.GetShadowBankRow(blockName, 0);
		if (row == null)
			return;
		float F(string field) => System.Convert.ToSingle(row[field].Value);

		// A default-shaped row (endDist >= 200) means the map has no shadow. beginDist/endDist are the
		// game's split range, unused until four-split shadows exist.
		float endDist = F("endDist");
		if (endDist <= 0.0f || endDist >= 200.0f)
			return;

		Vector3 lightDir = SunDirection(F("lightDegRotX"), F("lightDegRotY"));
		var renderer = new ShadowRenderer { Name = "ShadowRenderer" };
		bool ok = renderer.Setup(casters, lightDir, F("beginDist"), endDist,
			F("fadeBeginDist"), F("fadeDist"),
			Mathf.Clamp(F("densityRatio") / 100.0f, 0.0f, 1.0f),
			new Color(F("colR") / 255.0f, F("colG") / 255.0f, F("colB") / 255.0f),
			F("depthOffset"), F("shadowVolumeDepth"));
		if (ok)
			root.AddChild(renderer);
		else
			renderer.Free();
	}

	// Everything off: the material shaders and PostProcessPipeline produce the final image, and
	// Godot ambient or reflections would add light the game does not have.
	private static WorldEnvironment BuildEnvironment()
	{
		var env = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = Colors.Black,
			AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
			AmbientLightEnergy = 0.0f,
			ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
			TonemapMode = Godot.Environment.ToneMapper.Linear,
			TonemapExposure = 1.0f,
			TonemapWhite = 1.0f,
			AdjustmentEnabled = false,
			SsaoEnabled = false,
			SsilEnabled = false,
			SsrEnabled = false,
			SdfgiEnabled = false,
			FogEnabled = false,
			VolumetricFogEnabled = false,
			GlowEnabled = false,
		};
		return new WorldEnvironment { Environment = env, Name = "Environment" };
	}

	// The frame's tone rows are inferred to follow the player's collision; without a player the
	// block's most common collision rows stand in.
	private Node3D BuildPostProcess(string msbPath, string blockName)
	{
		var (toneMapId, toneCorrectId) = _msbLoader.ReadDominantCollisionToneIds(msbPath);
		var pipeline = new PostProcessPipeline { Name = "PostProcess" };
		pipeline.Configure(_drawParamReader.GetToneMapBankRow(blockName, toneMapId),
			_drawParamReader.GetToneCorrectBankRow(blockName, toneCorrectId));
		return pipeline;
	}

	private Node3D InstantiatePlacement(MsbPlacement placement, string blockName, bool receivesPointLights)
	{
		var inst = Instantiate(placement.ModelPath);
		inst.Name = placement.Name;
		inst.SetMeta("msb_entity_id", placement.EntityID);
		inst.SetMeta("flver_path", placement.ModelPath);
		ApplyPartTransform(inst, placement);
		ApplyDrawParams(inst, blockName, placement, receivesPointLights);
		return inst;
	}

	// Mirrored: X negated, Y and Z angles negated. The engine composes Ry·Rz·Rx; every multi-axis
	// object in the RPCS3 captures matches it to 1e-6 (docs/context.md, "Part rotation order").
	internal static void ApplyPartTransform(Node3D node, MsbPlacement placement)
	{
		node.Position = new Vector3(-placement.Position.X, placement.Position.Y, placement.Position.Z);
		var rot = placement.RotationDegrees;
		node.RotationOrder = EulerOrder.Yzx;
		node.RotationDegrees = new Vector3(rot.X, -rot.Y, -rot.Z);
		node.Scale = placement.Scale;
	}

	// Binds this placement's LIGHT_BANK, LIGHT_SCATTERING_BANK and FOG_BANK rows (and point lights)
	// as per-surface material overrides.
	private void ApplyDrawParams(Node3D inst, string blockName, MsbPlacement placement, bool receivesPointLights = false)
	{
		if (inst.GetNodeOrNull<MeshInstance3D>("Mesh") is not MeshInstance3D meshInst || meshInst.Mesh == null)
			return;

		var row = _drawParamReader.GetLightBankRow(blockName, placement.LightID);
		if (row == null)
			return;

		// Null when the map ships no such bank; the shader defaults are neutral. The tone banks are
		// frame-global (PostProcessPipeline).
		var scatterRow = _drawParamReader.GetScatterBankRow(blockName, placement.ScatterID);
		var fogRow = _drawParamReader.GetFogBankRow(blockName, placement.FogID);

		// The row's envDif cubemap, and the envSpc cubemap each material's g_EnvSpcSlotNo names.
		var cubemaps = _drawParamReader.GetEnvCubemapNames(blockName, placement.LightID);
		string mapPrefix = blockName[..blockName.IndexOf('_')];
		var envDif = cubemaps == null ? null
			: _builder.ResolveEnvCubemap(mapPrefix, (string)cubemaps["env_dif"]);
		var envSpc = new Cubemap[4];
		// A material without g_EnvSpcSlotNo (-1) uses slot 0 (inferred; the engine default is untraced).
		Cubemap EnvSpcCubemap(Material material)
		{
			if (cubemaps == null)
				return null;
			int slot = Mathf.Clamp(material.GetMeta(FlverModelBuilder.EnvSpcSlotMeta, 0).AsInt32(), 0, 3);
			return envSpc[slot] ??= _builder.ResolveEnvCubemap(mapPrefix,
				(string)((Godot.Collections.Array<string>)cubemaps["env_spc"])[slot]);
		}

		// Nearest lights are measured from the mesh's AABB centre in world space. The placement is still
		// off-tree, where GlobalTransform returns identity, so the local transforms are composed by hand.
		Vector3 lightQueryPosition = (inst.Transform * meshInst.Transform) * meshInst.Mesh.GetAabb().GetCenter();

		for (int i = 0; i < meshInst.Mesh.GetSurfaceCount(); i++)
		{
			if (meshInst.Mesh.SurfaceGetMaterial(i) is not ShaderMaterial baseMaterial)
				continue;
			// Light-bank uniforms exist only on the lit families; the output stage on every map surface,
			// water included.
			bool wantsLightBank = HasUniform(baseMaterial.Shader, "ambient_up");
			bool wantsOutputStage = HasUniform(baseMaterial.Shader, "fog_begin");
			if (!wantsLightBank && !wantsOutputStage)
				continue;

			// The base material belongs to the shared cached mesh; a duplicate (same shader) keeps this
			// placement's values off other placements of the same model.
			var material = (ShaderMaterial)baseMaterial.Duplicate();
			if (wantsLightBank)
			{
				ApplyColorIntensity(material, row, "colR_u", "colG_u", "colB_u", "colA_u", "ambient_up", "ambient_up_intensity");
				ApplyColorIntensity(material, row, "colR_d", "colG_d", "colB_d", "colA_d", "ambient_down", "ambient_down_intensity");
				// The diffuse hemisphere pair (colA_du/colA_dd) is deliberately not read.
				ApplyColorIntensity(material, row, "envDif_colR", "envDif_colG", "envDif_colB", "envDif_colA", "env_color", "env_intensity");
				ApplyColorIntensity(material, row, "envSpc_colR", "envSpc_colG", "envSpc_colB", "envSpc_colA", "env_spc_color", "env_spc_intensity");
				material.SetShaderParameter("env_dif_cube", envDif ?? WhiteCubemap);
				material.SetShaderParameter("env_spc_cube", EnvSpcCubemap(baseMaterial) ?? WhiteCubemap);
				ApplyDirectionalLights(material, row);
				if (receivesPointLights)
					BindPointLights(material, lightQueryPosition);
			}
			// Water's sun glint follows directional light 0 (DS_Water_Env c22).
			if (HasUniform(baseMaterial.Shader, "glint_light_direction"))
				material.SetShaderParameter("glint_light_direction", SunDirection(
					System.Convert.ToSingle(row["degRotX_0"].Value),
					System.Convert.ToSingle(row["degRotY_0"].Value)));
			if (wantsOutputStage)
			{
				ApplyScatterBank(material, scatterRow);
				ApplyFogBank(material, fogRow);
			}
			meshInst.SetSurfaceOverrideMaterial(i, material);
		}
	}

	// Bound when a cubemap name does not resolve (e.g. default_lightbank rows), so the env terms
	// reduce to lightmap-scaled colours instead of going black.
	private static Cubemap WhiteCubemap => _whiteCubemap ??= BuildWhiteCubemap();
	private static Cubemap? _whiteCubemap;

	private static Cubemap BuildWhiteCubemap()
	{
		var faces = new Godot.Collections.Array<Image>();
		for (int i = 0; i < 6; i++)
		{
			var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
			image.Fill(Colors.White);
			faces.Add(image);
		}
		var cubemap = new Cubemap();
		cubemap.CreateFromImages(faces);
		return cubemap;
	}

	internal static bool HasUniform(Shader shader, string name)
	{
		foreach (Godot.Collections.Dictionary uniform in shader.GetShaderUniformList())
			if (uniform["name"].AsStringName() == name)
				return true;
		return false;
	}

	private static void ApplyColorIntensity(ShaderMaterial material, PARAM.Row row,
		string rField, string gField, string bField, string aField, string colorUniform, string intensityUniform)
	{
		float r = System.Convert.ToSingle(row[rField].Value) / 255f;
		float g = System.Convert.ToSingle(row[gField].Value) / 255f;
		float b = System.Convert.ToSingle(row[bField].Value) / 255f;
		// colA fields are percent scales (0-1000, default 100).
		float a = System.Convert.ToSingle(row[aField].Value) / 100f;
		material.SetShaderParameter(colorUniform, new Color(r, g, b));
		material.SetShaderParameter(intensityUniform, a);
	}

	// LIGHT_BANK's three directional lights (平行光源 0/1/2) and specular light (平行光源：スペキュラ),
	// read only by HemDir3 materials. Colours premultiplied by colA, as plain Vector3 arrays.
	private static void ApplyDirectionalLights(ShaderMaterial material, PARAM.Row row)
	{
		var colors = new Vector3[3];
		var directions = new Vector3[3];
		for (int i = 0; i < 3; i++)
		{
			colors[i] = Vec3(row, $"colR_{i}", $"colG_{i}", $"colB_{i}") / 255f
				* (System.Convert.ToSingle(row[$"colA_{i}"].Value) / 100f);
			directions[i] = SunDirection(
				System.Convert.ToSingle(row[$"degRotX_{i}"].Value),
				System.Convert.ToSingle(row[$"degRotY_{i}"].Value));
		}
		material.SetShaderParameter("dir_light_colors", colors);
		material.SetShaderParameter("dir_light_directions", directions);
		material.SetShaderParameter("spec_light_color",
			Vec3(row, "colR_s", "colG_s", "colB_s") / 255f
				* (System.Convert.ToSingle(row["colA_s"].Value) / 100f));
		material.SetShaderParameter("spec_light_direction", SunDirection(
			System.Convert.ToSingle(row["degRotX_s"].Value),
			System.Convert.ToSingle(row["degRotY_s"].Value)));
	}

	// The four nearest map lights (the engine's PntSSSS maximum). ponytail: fixed at load from the
	// placement's centre; the engine selects per frame. Out-of-range and unused slots contribute
	// nothing in the shader.
	private void BindPointLights(ShaderMaterial material, Vector3 worldPosition)
	{
		var nearest = _mapPointLights
			.OrderBy(l => l.Position.DistanceSquaredTo(worldPosition))
			.Take(4).ToList();
		var pos = new Vector3[4];
		var color = new Vector3[4];
		var dwindle = new Vector2[4];
		for (int i = 0; i < nearest.Count; i++)
		{
			pos[i] = nearest[i].Position;
			color[i] = nearest[i].Color;
			dwindle[i] = new Vector2(nearest[i].DwindleBegin, nearest[i].DwindleEnd);
		}
		material.SetShaderParameter("point_light_pos", pos);
		material.SetShaderParameter("point_light_color", color);
		material.SetShaderParameter("point_light_dwindle", dwindle);
	}

	// LIGHT_SCATTERING_BANK -> the engine's vertex constants c104..c111, computed as its bank builder
	// computes them (external ELF_ENGINE_ACCURACY_RESEARCH.md 12): Hoffman-Preetham coefficients for
	// 650/570/475 nm scaled by lsBetaRay/lsBetaMie. Reproduces the captured m01/m02 c104 exactly.
	private static readonly Vector3 RayleighExtinction = Wavelength(1.2444366e-28, 4);
	private static readonly Vector3 RayleighAngular = Wavelength(7.4271841e-30, 4);
	private static readonly Vector3 MieExtinction = Wavelength(3.5407337e-15, 2) * new Vector3(0.685f, 0.679f, 0.670f);
	private static readonly Vector3 MieAngular = Wavelength(5.6352525e-16, 2);

	private static Vector3 Wavelength(double scale, int power) => new(
		(float)(scale / System.Math.Pow(650e-9, power)),
		(float)(scale / System.Math.Pow(570e-9, power)),
		(float)(scale / System.Math.Pow(475e-9, power)));

	private static void ApplyScatterBank(ShaderMaterial material, PARAM.Row row)
	{
		if (row == null)
			return;
		float F(string field) => System.Convert.ToSingle(row[field].Value);
		float ray = F("lsBetaRay"), mie = F("lsBetaMie"), g = F("lsHGg");
		material.SetShaderParameter("scatter_extinction", RayleighExtinction * ray + MieExtinction * mie);
		material.SetShaderParameter("scatter_reflectance",
			Vec3(row, "reflectanceR", "reflectanceG", "reflectanceB") / 255f * (F("reflectanceA") / 100f));
		material.SetShaderParameter("scatter_inscatter_mul", F("inscatteringMul") / 100f);
		material.SetShaderParameter("scatter_phase", new Vector3(1f - g * g, 1f + g, 2f * g));
		material.SetShaderParameter("scatter_rayleigh", RayleighAngular * ray);
		material.SetShaderParameter("scatter_mie", MieAngular * mie);
		material.SetShaderParameter("scatter_sun_color", Vec3(row, "sunR", "sunG", "sunB") / 255f * (F("sunA") / 100f));
		material.SetShaderParameter("scatter_blend", F("blendCoef") / 100f);
		material.SetShaderParameter("scatter_light_dir", -SunDirection(F("sunRotX"), F("sunRotY")));
		material.SetShaderParameter("scatter_distance_mul", F("distanceMul") / 100f);
	}

	// FOG_BANK -> des_fog, the engine's distance fade toward the bank colour (not RSX fog).
	// Colour and degRotW/100 match captured constants on m01/m02/m03/m06; degRotW 0 disables it.
	private static void ApplyFogBank(ShaderMaterial material, PARAM.Row row)
	{
		if (row == null)
			return;
		material.SetShaderParameter("fog_color", new Color(
			System.Convert.ToSingle(row["colR"].Value) / 255f,
			System.Convert.ToSingle(row["colG"].Value) / 255f,
			System.Convert.ToSingle(row["colB"].Value) / 255f)
			* (System.Convert.ToSingle(row["colA"].Value) / 100f));
		material.SetShaderParameter("fog_weight_scale", System.Convert.ToSingle(row["degRotW"].Value) / 100f);
		material.SetShaderParameter("fog_begin", System.Convert.ToSingle(row["fogBeginZ"].Value));
		material.SetShaderParameter("fog_end", System.Convert.ToSingle(row["fogEndZ"].Value));
	}

	// Toward-light vector for LIGHT_BANK, LIGHT_SCATTERING_BANK and SHADOW_BANK angles. The engine
	// builds the travel direction (cos X sin Y, -sin X, cos X cos Y); this negates it and mirrors X.
	private static Vector3 SunDirection(float elevationDegrees, float azimuthDegrees)
	{
		float elevation = Mathf.DegToRad(elevationDegrees);
		float azimuth = Mathf.DegToRad(azimuthDegrees);
		return new Vector3(
			Mathf.Cos(elevation) * Mathf.Sin(azimuth),
			Mathf.Sin(elevation),
			-Mathf.Cos(elevation) * Mathf.Cos(azimuth));
	}

	private static Vector3 Vec3(PARAM.Row row, string x, string y, string z) => new(
		System.Convert.ToSingle(row[x].Value),
		System.Convert.ToSingle(row[y].Value),
		System.Convert.ToSingle(row[z].Value));

	public void Evict(string path) => _meshCache.Remove(path);

	public void EvictAll()
	{
		_meshCache.Clear();
		_builder.ResetCaches();
		_drawParamReader.ResetCaches();
	}
}
