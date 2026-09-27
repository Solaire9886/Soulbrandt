using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Archstone;

// The game's material shader library (mounted/shader/ds_flver/, 1349 programs) read by name only:
// the names list the lighting models, texture sets and shadow variants the engine compiled.
// No shader code is read. See docs/ARCHITECTURE.md, "Shader library and capture tooling".
public partial class ShaderLibrary : RefCounted
{
	// The other containers (ds_filter, ds_sfx, ds_menu, dbgfont) hold post-process, effect and UI
	// programs.
	private const string LibraryDir = "res://mounted/shader/ds_flver";

	// Names are padded to fixed columns, so a feature run appears fused ("DifSpcBmpMulLitCsd") or
	// split ("DifSpcBmpMul_Csd"); detect features by substring, not by splitting on '_'.
	private static readonly string[] TextureFeatures = { "Dif", "Spc", "Bmp", "Mul", "Lit" };
	private static readonly string[] ShadowFeatures = { "Sdw", "Csd" };

	private List<ShaderName> _index;

	private readonly record struct ShaderName(
		string FileName, string Family, string Features, string Shadow, string LightingModel);

	// Parsed once and kept - 1349 filename splits, no file contents read.
	private List<ShaderName> Index()
	{
		if (_index != null)
			return _index;

		_index = new List<ShaderName>();
		string dir = ProjectSettings.GlobalizePath(LibraryDir);
		if (!System.IO.Directory.Exists(dir))
			return _index;

		foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.fpo"))
		{
			string name = System.IO.Path.GetFileNameWithoutExtension(path);
			var segments = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length < 2 || segments[0] != "DS")
				continue;

			// DS_<family>_<features...>_<lightingModel>, but the tail fields are optional: the
			// ghost family ships a single bare DS_Ghost.fpo with neither.
			string family = segments[1];
			string lightingModel = segments.Length >= 3 ? segments[^1] : "";
			string features = segments.Length >= 4 ? string.Join("", segments[2..^1]) : "";
			string shadow = ShadowFeatures.FirstOrDefault(features.Contains) ?? "";
			_index.Add(new ShaderName(name, family, features, shadow, lightingModel));
		}
		return _index;
	}

	// The lighting models the library ships, e.g. HemDir3, HemEnv, HemEnvLerp and their point-light
	// variants (PntS/PntSS/PntSSSS = 1/2/4 lights).
	public string[] GetLightingModels() =>
		Index().Select(s => s.LightingModel).Distinct().OrderBy(x => x).ToArray();

	public string[] GetFamilies() =>
		Index().Select(s => s.Family).Distinct().OrderBy(x => x).ToArray();

	// An MTD's ShaderPath (.spx) -> the family, texture features, and the lighting models and shadow
	// variants the engine can choose among at run time. A Dictionary so GDScript checks can call it.
	public Godot.Collections.Dictionary ResolveMaterialShader(string mtdShaderPath)
	{
		if (string.IsNullOrEmpty(mtdShaderPath))
			return null;

		string spx = System.IO.Path.GetFileNameWithoutExtension(mtdShaderPath.Replace('\\', '/'));
		var segments = spx.Split('_', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 2 || segments[0] != "DS")
			return null;

		string family = segments[1];
		// segments[2] is the feature run. "Col" (vertex colour, on every .spx name) and "_Skin" (a vertex
		// layout, PIWN in .vpo names) do not appear in fragment names and are dropped.
		string featureRun = segments.Length >= 3 ? segments[2] : "";
		string wanted = featureRun.StartsWith("Col", StringComparison.Ordinal) ? featureRun[3..] : featureRun;

		var matches = Index().Where(s => s.Family == family && FeatureSetMatches(s.Features, wanted)).ToList();

		var models = new Godot.Collections.Array<string>();
		foreach (string m in matches.Select(s => s.LightingModel).Distinct().OrderBy(x => x))
			models.Add(m);
		var shadows = new Godot.Collections.Array<string>();
		foreach (string s in matches.Select(x => x.Shadow).Distinct().OrderBy(x => x))
			shadows.Add(s);

		return new Godot.Collections.Dictionary
		{
			{ "family", family },
			{ "features", wanted },
			{ "lighting_models", models },
			{ "shadow_variants", shadows },
			{ "match_count", matches.Count },
		};
	}

	// Compares texture features only; shadow flags are a run-time choice.
	private static bool FeatureSetMatches(string compiled, string wanted) =>
		TextureFeatures.All(f => compiled.Contains(f) == wanted.Contains(f));

	public Godot.Collections.Dictionary GetLibraryStats()
	{
		var index = Index();
		var byFamily = new Godot.Collections.Dictionary();
		foreach (var g in index.GroupBy(s => s.Family).OrderByDescending(g => g.Count()))
			byFamily[g.Key] = g.Count();
		return new Godot.Collections.Dictionary
		{
			{ "shaders", index.Count },
			{ "families", byFamily },
			{ "lighting_models", GetLightingModels().Length },
			{ "with_shadow", index.Count(s => s.Shadow != "") },
		};
	}

	public void ResetCaches() => _index = null;
}
