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

	// "Load Model(s)"/"Load Folder": there's no MSB placement to resolve a LightID from, so
	// bind default_lightbank.param row 0 (and the default tone/scatter rows) the same way
	// ApplyDrawParams binds a placement's real rows. Without this a lightmap/hemisphere-family
	// material renders on shader defaults - flat diffuse * vertex_color * 0.3, every
	// directional/env term zero. DrawParamReader splits "<prefix>_..." on the first
	// underscore and treats "default" as its own fallback namespace; a default MsbPlacement is
	// all-zero IDs, i.e. row 0 of each default_* bank. See docs/ARCHITECTURE.md's "Known deferred work".
	public Node3D InstantiateWithDefaultDrawParams(string path)
	{
		var inst = Instantiate(path);
		ApplyDrawParams(inst, "default_", default);
		return inst;
	}

	public Node3D InstantiateMap(string msbPath)
	{
		string blockName = System.IO.Path.GetFileNameWithoutExtension(msbPath);
		var root = new Node3D { Name = blockName };
		root.AddChild(BuildBloomEnvironment(blockName));
		foreach (var placement in _msbLoader.ReadMapPieces(msbPath))
			root.AddChild(InstantiatePlacement(placement, blockName));
		foreach (var placement in _msbLoader.ReadObjects(msbPath))
			root.AddChild(InstantiatePlacement(placement, blockName));
		return root;
	}

	// Approximates DeS's bright-pass -> bloom stage. Environment.glow is the only post effect
	// that shows in the editor's 3D viewport under the Compatibility renderer; every other
	// Environment feature is pinned off, ambient/reflection specifically so this can't
	// re-trigger the past near-black regression. See docs/ARCHITECTURE.md's "Bloom" section.
	private WorldEnvironment BuildBloomEnvironment(string blockName)
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

		// Row 0 is the map baseline; glow is per-viewport, so per-part bloom isn't expressible.
		var tone = _drawParamReader.GetToneMapBankRow(blockName, 0);
		if (tone != null)
		{
			// Percent-style like every other DrawParam bank (100 = 1.0).
			float begin = System.Convert.ToSingle(tone["bloomBegin"].Value) / 100f;
			float mul = System.Convert.ToSingle(tone["bloomMul"].Value) / 100f;
			if (mul > 0.0f)
			{
				env.GlowEnabled = true;
				// GlowBloom is the dial that carries the effect on an LDR buffer - the
				// HDR-threshold path barely contributes - so bloomMul maps straight to it.
				env.GlowBloom = Mathf.Clamp(mul, 0.0f, 1.0f);
				// bloomBegin -> HDR threshold for the extra kick on top.
				env.GlowHdrThreshold = Mathf.Clamp(begin, 0.0f, 0.99f);
				env.GlowHdrScale = System.Math.Min(1.0f / System.Math.Max(1.0f - env.GlowHdrThreshold, 0.01f), 8.0f);
				env.GlowIntensity = 1.0f;
				env.GlowStrength = 1.2f;   // DS_Fil_Bloom is a wide downscaled gaussian
				env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
				env.GlowNormalized = false;
				// Approximate that gaussian with a few mid mip levels, not the full 7-level spread.
				for (int i = 0; i < 7; i++)
					env.SetGlowLevel(i, i is 2 or 3 or 4 ? 1.0f : 0.0f);
			}
		}

		return new WorldEnvironment { Environment = env, Name = "BloomEnvironment" };
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
		ApplyDrawParams(inst, blockName, placement);
		return inst;
	}

	// Per-instance drawparam uniforms, resolved from this placement's own LightID/FogID/
	// ToneMapID/ToneCorrectID rather than the shared default_*.param row-0 fallback every
	// lightmapped material's base mesh material otherwise uses. Covers both halves of the
	// pipeline: the shading inputs (hemisphere_ambient.gdshaderinc) and the output stage
	// (output_stage.gdshaderinc) - see docs/ARCHITECTURE.md's "The DeS rendering pipeline,
	// reconstructed" section.
	private void ApplyDrawParams(Node3D inst, string blockName, MsbPlacement placement)
	{
		if (inst.GetNodeOrNull<MeshInstance3D>("Mesh") is not MeshInstance3D meshInst || meshInst.Mesh == null)
			return;

		var row = _drawParamReader.GetLightBankRow(blockName, placement.LightID);
		if (row == null)
			return;

		// Each may be null when a map ships no such bank - the shader defaults are the neutral
		// no-op case (no fog, identity correction), so a missing bank simply skips that stage.
		var fogRow = _drawParamReader.GetFogBankRow(blockName, placement.FogID);
		var toneMapRow = _drawParamReader.GetToneMapBankRow(blockName, placement.ToneMapID);
		var toneCorrectRow = _drawParamReader.GetToneCorrectBankRow(blockName, placement.ToneCorrectID);
		var scatterRow = _drawParamReader.GetScatterBankRow(blockName, placement.ScatterID);

		// LIGHT_BANK's own per-situation environment cubemaps. envSpc_0..3 are four progressively
		// different sets and a material picks one via g_EnvSpcSlotNo - slot 0 is used here as a
		// deliberate placeholder, since the slot-to-roughness ordering is still unsettled (see
		// docs/context.md's "part 17") and picking per-material would mean a per-surface lookup
		// on a guess. A name that doesn't resolve falls back to a white cubemap, which makes the
		// env terms behave exactly as they did before cubemaps existed.
		var cubemaps = _drawParamReader.GetEnvCubemapNames(blockName, placement.LightID);
		string mapPrefix = blockName[..blockName.IndexOf('_')];
		var envDif = cubemaps == null ? null
			: _builder.ResolveEnvCubemap(mapPrefix, (string)cubemaps["env_dif"]);
		var envSpc = cubemaps == null ? null
			: _builder.ResolveEnvCubemap(mapPrefix, (string)((Godot.Collections.Array<string>)cubemaps["env_spc"])[0]);

		for (int i = 0; i < meshInst.Mesh.GetSurfaceCount(); i++)
		{
			if (meshInst.Mesh.SurfaceGetMaterial(i) is not ShaderMaterial baseMaterial)
				continue;
			// Two independent uniform groups: the light bank's shading inputs (which only the
			// hemisphere/env families carry) and the output stage (which every map surface
			// carries, water included). Gated separately so water gets the atmosphere without
			// needing ambient uniforms it has no use for.
			bool wantsLightBank = HasUniform(baseMaterial.Shader, "ambient_up");
			bool wantsOutputStage = HasUniform(baseMaterial.Shader, "tone_key");
			if (!wantsLightBank && !wantsOutputStage)
				continue;

			// The base material lives on the shared, cached ArrayMesh (one placed instance's
			// worth of uniforms would otherwise leak onto every other placement of the same
			// model) - Duplicate() (shallow: shares the compiled Shader, copies only parameters)
			// gives this instance its own values via a per-surface override.
			var material = (ShaderMaterial)baseMaterial.Duplicate();
			if (wantsLightBank)
			{
			// colR/colG/colB (0-255) -> normalized color; colA (0-1000, default 100) -> intensity,
			// /100 - see ApplyColorIntensity. Overall visual correctness still pending in-editor
			// comparison against real DeS rendering. See docs/PLAN.md's "Per-part lighting/fog" item.
				ApplyColorIntensity(material, row, "colR_u", "colG_u", "colB_u", "colA_u", "ambient_up", "ambient_up_intensity");
				ApplyColorIntensity(material, row, "colR_d", "colG_d", "colB_d", "colA_d", "ambient_down", "ambient_down_intensity");
				// LIGHT_BANK's "diffuse hemisphere" (colA_du/colA_dd) is deliberately not wired -
				// tried and reverted three times, see hemisphere_ambient.gdshaderinc's note and
				// docs/context.md's parts 15/19/32.
				ApplyColorIntensity(material, row, "envDif_colR", "envDif_colG", "envDif_colB", "envDif_colA", "env_color", "env_intensity");
				ApplyColorIntensity(material, row, "envSpc_colR", "envSpc_colG", "envSpc_colB", "envSpc_colA", "env_spc_color", "env_spc_intensity");
				material.SetShaderParameter("env_dif_cube", envDif ?? WhiteCubemap);
				material.SetShaderParameter("env_spc_cube", envSpc ?? WhiteCubemap);
				ApplyDirectionalLights(material, row);
			}
			if (wantsOutputStage)
			{
				ApplyFogBank(material, fogRow);
				ApplyToneBanks(material, toneMapRow, toneCorrectRow);
				ApplyScatterBank(material, scatterRow);
			}
			meshInst.SetSurfaceOverrideMaterial(i, material);
		}
	}

	// Bound whenever a placement's LightID doesn't resolve to a real cubemap (a fallback row's
	// IDs live in default_lightbank.param's namespace, not this map's). White makes env_diffuse/
	// env_specular collapse to the flat lightmap-scaled terms they replaced, so an unresolved
	// name degrades to the previous behaviour rather than going black.
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

	private static bool HasUniform(Shader shader, string name)
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
		// The *_colA fields are min=0/max=1000/default=100 in lightbank.paramdef (confirmed by
		// reading the real field metadata, not guessed) - a percent-style scale where 100 = 1.0x
		// baseline intensity, not a raw multiplier or an alpha-style 0-255 channel.
		float a = System.Convert.ToSingle(row[aField].Value) / 100f;
		material.SetShaderParameter(colorUniform, new Color(r, g, b));
		material.SetShaderParameter(intensityUniform, a);
	}

	// LIGHT_BANK's three directional lights (平行光源 0/1/2) plus its separate specular-only
	// directional (平行光源：スペキュラ), for hemisphere_ambient.gdshaderinc's HemDir3 path. Read
	// by g_LightingType=1 materials only - the map/lightmap family provably uses none of them
	// (see docs/ARCHITECTURE.md's LIGHT_BANK entry), so binding them costs those materials
	// nothing and the shader never reads them.
	//
	// Colours are premultiplied by their colA intensity and passed as plain Vector3: the shader
	// side is a vec3 array, which can't carry a source_color hint, and that hint is a no-op under
	// the Compatibility renderer regardless.
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

	// FOG_BANK -> output_stage.gdshaderinc's fog uniforms. degRotW and colA are both the
	// percent-style 0-1000 scales the paramdef describes (100 = 1.0x); degRotZ is flagged dummy
	// in the paramdef itself and is not a rotation, despite the name.
	private static void ApplyFogBank(ShaderMaterial material, PARAM.Row row)
	{
		if (row == null)
			return;
		ApplyColorIntensity(material, row, "colR", "colG", "colB", "colA", "fog_color", "fog_intensity");
		material.SetShaderParameter("fog_begin", System.Convert.ToSingle(row["fogBeginZ"].Value));
		material.SetShaderParameter("fog_end", System.Convert.ToSingle(row["fogEndZ"].Value));
		material.SetShaderParameter("fog_density", System.Convert.ToSingle(row["degRotW"].Value) / 100f);
	}

	// TONE_MAP_BANK/TONE_CORRECT_BANK -> output_stage.gdshaderinc's transfer function.
	private static void ApplyToneBanks(ShaderMaterial material, PARAM.Row toneMap, PARAM.Row toneCorrect)
	{
		if (toneMap != null)
		{
			material.SetShaderParameter("tone_key", System.Convert.ToSingle(toneMap["grayKeyValue"].Value));
			// minAdaptedLum, not the max or a midpoint: the engine's adapted luminance is the
			// frame's own average clamped into [min, max], and the maps this drives are dark
			// interiors that would settle at the low end. The Compatibility renderer can't read
			// back frame luminance to do the real thing - see output_stage.gdshaderinc.
			material.SetShaderParameter("tone_adapted_lum", System.Convert.ToSingle(toneMap["minAdaptedLum"].Value));
		}
		if (toneCorrect == null)
			return;
		material.SetShaderParameter("tone_brightness", Vec3(toneCorrect, "brightnessR", "brightnessG", "brightnessB"));
		material.SetShaderParameter("tone_contrast", Vec3(toneCorrect, "contrastR", "contrastG", "contrastB"));
		material.SetShaderParameter("tone_saturation", System.Convert.ToSingle(toneCorrect["saturation"].Value));
		material.SetShaderParameter("tone_hue_radians", Mathf.DegToRad(System.Convert.ToSingle(toneCorrect["hue"].Value)));
	}

	// LIGHT_SCATTERING_BANK -> output_stage.gdshaderinc's des_scatter. distanceMul/
	// inscatteringMul/blendCoef/sunA/reflectanceA are all the same percent-style scales the other
	// banks use (100 = 1.0x).
	private static void ApplyScatterBank(ShaderMaterial material, PARAM.Row row)
	{
		if (row == null)
			return;
		material.SetShaderParameter("scatter_beta_ray", System.Convert.ToSingle(row["lsBetaRay"].Value));
		material.SetShaderParameter("scatter_beta_mie", System.Convert.ToSingle(row["lsBetaMie"].Value));
		material.SetShaderParameter("scatter_hg_g", System.Convert.ToSingle(row["lsHGg"].Value));
		ApplyColorIntensity(material, row, "sunR", "sunG", "sunB", "sunA", "scatter_sun_color", "scatter_sun_intensity");
		material.SetShaderParameter("scatter_reflectance", new Color(
			System.Convert.ToSingle(row["reflectanceR"].Value) / 255f,
			System.Convert.ToSingle(row["reflectanceG"].Value) / 255f,
			System.Convert.ToSingle(row["reflectanceB"].Value) / 255f)
			* (System.Convert.ToSingle(row["reflectanceA"].Value) / 100f));
		material.SetShaderParameter("scatter_distance_mul", System.Convert.ToSingle(row["distanceMul"].Value) / 100f);
		material.SetShaderParameter("scatter_inscatter_mul", System.Convert.ToSingle(row["inscatteringMul"].Value) / 100f);
		material.SetShaderParameter("scatter_blend", System.Convert.ToSingle(row["blendCoef"].Value) / 100f);
		material.SetShaderParameter("scatter_sun_dir", SunDirection(
			System.Convert.ToSingle(row["sunRotX"].Value),
			System.Convert.ToSingle(row["sunRotY"].Value)));
	}

	// Shared by LIGHT_BANK's directional lights and LIGHT_SCATTERING_BANK's sun - both spell the
	// same degRotX/degRotY (elevation/azimuth in degrees) pair.
	// sunRotX is elevation (-90..90), sunRotY azimuth (-180..180). X is negated to match the
	// mirrored vertex/placement convention this file already applies to MSB positions - the same
	// starting hypothesis, and equally unproven; the scattering result is symmetric enough in the
	// Nexus's near-overhead case that this won't show until an area with a low sun is tested.
	private static Vector3 SunDirection(float elevationDegrees, float azimuthDegrees)
	{
		float elevation = Mathf.DegToRad(elevationDegrees);
		float azimuth = Mathf.DegToRad(azimuthDegrees);
		return new Vector3(
			-Mathf.Sin(azimuth) * Mathf.Cos(elevation),
			Mathf.Sin(elevation),
			Mathf.Cos(azimuth) * Mathf.Cos(elevation)).Normalized();
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
