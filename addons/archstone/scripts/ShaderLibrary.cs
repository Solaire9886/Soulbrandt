using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Archstone;

// Reads DeS's own material shader library as data - see docs/ARCHITECTURE.md's "The shader
// library" section. The library is 1349 named shaders under mounted/shader/ds_flver/, and their
// names spell out the engine's complete material system: which lighting models exist, which
// texture sets they come in, and which shadow variants each is compiled for.
//
// Names only - the binaries themselves are stripped RSX microcode with no parameter table, so
// nothing here reads a single byte of shader code. That is the point: the *names* answer
// questions this project previously had to infer statistically, and they are authored fact.
public partial class ShaderLibrary : RefCounted
{
	// The shipped material shader library. The other four containers (ds_filter/ds_sfx/ds_menu/
	// dbgfont) are real but describe the post chain, SFX and UI, not FLVER materials.
	private const string LibraryDir = "res://mounted/shader/ds_flver";

	// The fragment-name feature region is an ordered flag list, not a token sequence - the names
	// are padded to fixed columns, so a feature run can be fused ("DifSpcBmpMulLitCsd") or split
	// by underscores ("DifSpcBmpMul_Csd"), depending on how wide the columns happen to fall.
	// Substring detection reads both shapes identically; splitting on '_' does not.
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

	// Which lighting models the engine actually ships, as opposed to the ones this project has
	// inferred from MTD.LightingType. Returns e.g. HemDir3/HemEnv/HemEnvLerp and their
	// point-light variants (PntS/PntSS/PntSSSS = 1/2/4 point lights).
	public string[] GetLightingModels() =>
		Index().Select(s => s.LightingModel).Distinct().OrderBy(x => x).ToArray();

	public string[] GetFamilies() =>
		Index().Select(s => s.Family).Distinct().OrderBy(x => x).ToArray();

	// Resolves an MTD's own ShaderPath (the .spx it names) to the set of compiled fragment
	// shaders the engine would pick from for that material. The material fixes the family and
	// the texture set; the runtime picks the shadow variant and the lighting model, which is why
	// this returns lists rather than one shader.
	//
	// Returns a Dictionary rather than a typed struct for the same reason DrawParamReader's
	// GetEnvCubemapNames does: a custom struct isn't a Variant type, so GDScript - this project's
	// only verification path - couldn't call the method at all.
	public Godot.Collections.Dictionary ResolveMaterialShader(string mtdShaderPath)
	{
		if (string.IsNullOrEmpty(mtdShaderPath))
			return null;

		string spx = System.IO.Path.GetFileNameWithoutExtension(mtdShaderPath.Replace('\\', '/'));
		var segments = spx.Split('_', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 2 || segments[0] != "DS")
			return null;

		string family = segments[1];
		// segments[2] carries the feature run; "Col" (the vertex-colour multiply, universal on
		// .spx names and absent from the compiled names) and a trailing "_Skin" (a *vertex*
		// layout concern - the .vpo names carry it as PIWN, the fragment names never do) are both
		// dropped so the remaining features line up with the fragment-side spelling.
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

	// Compares by texture-feature set, ignoring the shadow flags - those live in the same region
	// of the compiled name but are a runtime choice, not part of what the material asked for.
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
