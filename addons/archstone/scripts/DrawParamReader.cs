using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

// Resolves DeS's per-map LIGHT_BANK/FOG_BANK drawparam rows (param/drawparam/<map>_<bank>.param)
// by row ID - MSBD's per-part LightID/FogID byte indexes directly into these. See
// docs/ARCHITECTURE.md's "Known deferred work" for the row shape / field names.
public partial class DrawParamReader : RefCounted
{
	// Observed sentinel for "unset, use the default bank" - see docs/PLAN.md's Phase 1 item 4.
	private const byte UnsetID = 255;

	private readonly Dictionary<string, PARAMDEF> _paramdefCache = new();
	private readonly Dictionary<string, PARAM> _paramCache = new();

	public PARAM.Row GetLightBankRow(string blockName, byte lightID) => GetRow(blockName, "lightbank", lightID);

	public PARAM.Row GetFogBankRow(string blockName, byte fogID) => GetRow(blockName, "fogbank", fogID);

	// MSBD.Part.ToneMapID/ToneCorrectID - same DrawParam-family bank shape as LightID/FogID.
	// Real, per-part-varying data, see docs/context.md's "third-party investigation brief"
	// entry. No consumer right now - a WorldEnvironment built from this was tried and reverted
	// (a real regression, not just uncalibrated).
	public PARAM.Row GetToneMapBankRow(string blockName, byte toneMapID) => GetRow(blockName, "tonemapbank", toneMapID);

	public PARAM.Row GetToneCorrectBankRow(string blockName, byte toneCorrectID) => GetRow(blockName, "tonecorrectbank", toneCorrectID);

	// MSBD.Part.ScatterID -> LIGHT_SCATTERING_BANK, the other half of DeS's atmosphere alongside
	// FOG_BANK. Its fields are Hoffman & Preetham's real-time outdoor-scattering model under the
	// paper's own names (lsBetaRay/lsBetaMie/lsHGg/inscatteringMul/distanceMul) - see
	// output_stage.gdshaderinc's des_scatter.
	public PARAM.Row GetScatterBankRow(string blockName, byte scatterID) => GetRow(blockName, "lightscatteringbank", scatterID);

	// LIGHT_BANK's envDif/envSpc_0..3 are integer *suffixes*, not resource IDs needing a lookup
	// table: each map ships its own cubemaps in map/<mXX>/<mXX>_9999.tpf as real
	// TPF.TexType.Cubemap entries named EnvDif_<mXX>_<NNN>/EnvSpc_<mXX>_<NNN>, zero-padded to 3
	// digits. Verified against every LIGHT_BANK row actually referenced by an MSB part:
	// 930/930 resolve, 0 missing. Case-insensitively - the corpus ships envdif_m08_100 lowercase
	// and Envspc_m06_014 mixed-case, which costs nothing since texture lookups are already
	// OrdinalIgnoreCase throughout this project.
	//
	// Deliberately name-resolution only, with no consumer yet - the same way ToneMapID/
	// ToneCorrectID were landed. The resolution rule was the genuinely unknown part (previously
	// recorded as "per-map resolution isn't confirmed" in docs/ARCHITECTURE.md); how a cubemap
	// should actually reach a material is a separate, unsettled question - StandardMaterial3D
	// has no cubemap slot, and the last time reflection response was added with nothing real to
	// reflect it regressed every map surface (see the roughness revert in that same doc).
	//
	// Names are always built in the *block's own* map namespace, even when GetLightBankRow fell
	// back to default_lightbank.param, since the cubemaps themselves are per-map assets. An
	// unresolvable name is a normal outcome for that fallback case and is the caller's to handle.
	//
	// Returns a Dictionary rather than a typed record struct on purpose: a custom struct isn't a
	// Godot Variant type, so GDScript can't call the method at all - and a throwaway GDScript
	// check script is this project's only verification path (see docs/ARCHITECTURE.md's "Build &
	// verify"). A typed C# shape would be nicer at a future call site, but untestable groundwork
	// is worse than loosely-typed groundwork; tighten it when a real consumer exists and shows
	// what it actually needs. "env_spc" holds exactly 4 entries, indexed by g_EnvSpcSlotNo.
	public Godot.Collections.Dictionary GetEnvCubemapNames(string blockName, byte lightID)
	{
		var row = GetLightBankRow(blockName, lightID);
		if (row == null)
			return null;

		string mapPrefix = blockName[..blockName.IndexOf('_')];
		var envSpc = new Godot.Collections.Array<string>();
		for (int i = 0; i < 4; i++)
			envSpc.Add($"EnvSpc_{mapPrefix}_{System.Convert.ToInt32(row[$"envSpc_{i}"].Value):000}");

		return new Godot.Collections.Dictionary
		{
			{ "env_dif", $"EnvDif_{mapPrefix}_{System.Convert.ToInt32(row["envDif"].Value):000}" },
			{ "env_spc", envSpc },
		};
	}

	private PARAM.Row GetRow(string blockName, string bank, byte rowID)
	{
		string mapPrefix = blockName[..blockName.IndexOf('_')];
		var row = rowID == UnsetID ? null : LoadParam(mapPrefix, bank)?[rowID];
		return row ?? LoadParam("default", bank)?[0];
	}

	private PARAM LoadParam(string mapPrefix, string bank)
	{
		string relPath = $"param/drawparam/{mapPrefix}_{bank}.param";
		if (_paramCache.TryGetValue(relPath, out var cached))
			return cached;

		string fullPath = ProjectSettings.GlobalizePath($"res://mounted/{relPath}");
		PARAM param = null;
		if (System.IO.File.Exists(fullPath))
		{
			param = PARAM.Read(fullPath);
			param.ApplyParamdef(LoadParamdef(bank));
		}
		_paramCache[relPath] = param;
		return param;
	}

	private PARAMDEF LoadParamdef(string bank)
	{
		if (!_paramdefCache.TryGetValue(bank, out var def))
		{
			def = PARAMDEF.Read(ProjectSettings.GlobalizePath($"res://mounted/paramdef/{bank}.paramdef"));
			_paramdefCache[bank] = def;
		}
		return def;
	}

	public void ResetCaches()
	{
		_paramdefCache.Clear();
		_paramCache.Clear();
	}
}
