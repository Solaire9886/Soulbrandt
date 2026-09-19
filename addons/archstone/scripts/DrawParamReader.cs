using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

// Resolves DeS's per-map drawparam rows (param/drawparam/<map>_<bank>.param) by row ID - MSBD's
// per-part LightID/ToneMapID/ToneCorrectID/ScatterID bytes index directly into these. See
// docs/ARCHITECTURE.md's "Known deferred work" for the row shape / field names. (FOG_BANK is
// parsed but not wired - it describes RSX fixed-function fog, which DeS never uses; see
// docs/context.md part 36.)
public partial class DrawParamReader : RefCounted
{
	// Observed sentinel for "unset, use the default bank" - see docs/PLAN.md's Phase 1 item 4.
	private const byte UnsetID = 255;

	private readonly Dictionary<string, PARAMDEF> _paramdefCache = new();
	private readonly Dictionary<string, PARAM> _paramCache = new();

	public PARAM.Row GetLightBankRow(string blockName, byte lightID) => GetRow(blockName, "lightbank", lightID);

	// MSBD.Part.ToneMapID/ToneCorrectID - same DrawParam-family bank shape as LightID.
	// Real, per-part-varying data, see docs/context.md's "third-party investigation brief"
	// entry. No consumer right now - a WorldEnvironment built from this was tried and reverted
	// (a real regression, not just uncalibrated).
	public PARAM.Row GetToneMapBankRow(string blockName, byte toneMapID) => GetRow(blockName, "tonemapbank", toneMapID);

	public PARAM.Row GetToneCorrectBankRow(string blockName, byte toneCorrectID) => GetRow(blockName, "tonecorrectbank", toneCorrectID);

	// MSBD.Part.ScatterID -> LIGHT_SCATTERING_BANK, which carries DeS's entire outdoor atmosphere.
	// Its fields are Hoffman & Preetham's real-time outdoor-scattering model under the paper's own
	// names (lsBetaRay/lsBetaMie/lsHGg/inscatteringMul/distanceMul) - see output_stage.gdshaderinc's
	// des_scatter.
	public PARAM.Row GetScatterBankRow(string blockName, byte scatterID) => GetRow(blockName, "lightscatteringbank", scatterID);

	// MSBD.Part.FogID -> FOG_BANK. NOT RSX fixed-function fog (that path is dead - SET_FOG_PARAMS is
	// never issued, docs/context.md part 36). This bank's colour (col/255 * colA/100), begin/end
	// distance and degRotW (a master-strength dial, /100) drive the hand-rolled per-material
	// distance fade in output_stage.gdshaderinc's des_fog - the `mix()` that sits two instructions
	// ahead of `result*tc8 + tc9` in every HemEnv fragment program. Constants verified against this
	// bank on m01/m02/m03/m06. See docs/context.md part 45.
	//
	// A standalone-loaded model (blockName "default_") has no MSB placement, so no FogID: return
	// null so des_fog stays a no-op. Unlike default_lightbank row 0 (a real neutral), default_fogbank
	// row 0 is white / full strength / 50-100 range - harmless as an RSX-fog default, but as a mix()
	// target it would fade everything past 100 units to white.
	public PARAM.Row GetFogBankRow(string blockName, byte fogID) =>
		blockName.StartsWith("default") ? null : GetRow(blockName, "fogbank", fogID);

	// MSBD.Part.ShadowID -> SHADOW_BANK: the shadow's own light direction (lightDegRotX/Y, distinct
	// from LIGHT_SCATTERING_BANK's sun), region reach (endDist), distance fade (fadeBeginDist/
	// fadeDist), darkness (densityRatio, 0-100) and tint (colR/G/B), and depth bias (depthOffset).
	// Consumed by ShadowRenderer + hemisphere_ambient.gdshaderinc's sun_shadow(). See docs/context.md
	// part 39 for the field survey.
	public PARAM.Row GetShadowBankRow(string blockName, byte shadowID) => GetRow(blockName, "shadowbank", shadowID);

	// MSBD.Events.Light.PointLightID -> POINT_LIGHT_BANK (torch/campfire/candle colour + falloff).
	// Not a direct row index - every mounted map's bank has exactly 64 rows (0-63) but real event
	// IDs run past that (up to 139 seen in m02). `PointLightID mod 64` is the real resolution,
	// confirmed against a live capture: a HemEnvPntS/HemDir3PntS-lit draw's fragment inline colour
	// constant matched m02_PointLightBank row 10 exactly, and the nearest torch event's
	// PointLightID (74) mod 64 is 10. See docs/context.md part 52. No `default_pointlightbank`
	// fallback - unlike LightID/ScatterID/etc a Light event with no map bank simply contributes no
	// light, rather than falling back to a same-shaped bank that would mean something different.
	public PARAM.Row GetPointLightBankRow(string blockName, int pointLightID)
	{
		string mapPrefix = blockName[..blockName.IndexOf('_')];
		return LoadParam(mapPrefix, "pointlightbank")?[pointLightID % 64];
	}

	// LIGHT_BANK's envDif/envSpc_0..3 are integer *suffixes*, not resource IDs: each map ships its
	// own cubemaps in map/<mXX>/<mXX>_9999.tpf as TPF.TexType.Cubemap entries named
	// EnvDif_<mXX>_<NNN>/EnvSpc_<mXX>_<NNN>, zero-padded to 3 digits, matched case-insensitively.
	// Names are always built in the block's own map namespace, even when GetLightBankRow fell back
	// to default_lightbank.param - the caller handles an unresolvable name (a normal outcome there).
	// See docs/ARCHITECTURE.md's "Character/parts specular accuracy" entry for the resolution
	// trail and why there's still no consumer (StandardMaterial3D has no cubemap slot).
	//
	// Returns a Dictionary, not a typed record struct - a custom struct isn't a Godot Variant type,
	// so GDScript (this project's only verification path) can't call the method at all. "env_spc"
	// holds exactly 4 entries, indexed by g_EnvSpcSlotNo.
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
