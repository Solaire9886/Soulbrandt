using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

// Draw-parameter bank rows (param/drawparam/<mXX>_<bank>.param) by row ID, falling back to row 0
// of default_<bank>.param. See docs/ARCHITECTURE.md, "Draw parameters".
public partial class DrawParamReader : RefCounted
{
	// "Unset": use the default bank.
	private const byte UnsetID = 255;

	private readonly Dictionary<string, PARAMDEF> _paramdefCache = new();
	private readonly Dictionary<string, PARAM> _paramCache = new();

	public PARAM.Row GetLightBankRow(string blockName, byte lightID) => GetRow(blockName, "lightbank", lightID);

	// Frame-global: PostProcessPipeline uses one row of each per map.
	public PARAM.Row GetToneMapBankRow(string blockName, byte toneMapID) => GetRow(blockName, "tonemapbank", toneMapID);

	public PARAM.Row GetToneCorrectBankRow(string blockName, byte toneCorrectID) => GetRow(blockName, "tonecorrectbank", toneCorrectID);

	// Hoffman-Preetham scattering parameters (FlverLoader.ApplyScatterBank).
	public PARAM.Row GetScatterBankRow(string blockName, byte scatterID) => GetRow(blockName, "lightscatteringbank", scatterID);

	// The distance fade toward the bank colour (des_fog). A standalone model gets none:
	// default_fogbank row 0 is white at full strength and would fade everything past 100 m to white.
	public PARAM.Row GetFogBankRow(string blockName, byte fogID) =>
		blockName.StartsWith("default") ? null : GetRow(blockName, "fogbank", fogID);

	// The sun shadow's own direction, density, tint, fade and bias (ShadowRenderer).
	public PARAM.Row GetShadowBankRow(string blockName, byte shadowID) => GetRow(blockName, "shadowbank", shadowID);

	// The row a light event's UnkT04 names. No default fallback: without a map bank there is no light.
	public PARAM.Row GetPointLightBankRow(string blockName, int row)
	{
		string mapPrefix = blockName[..blockName.IndexOf('_')];
		return LoadParam(mapPrefix, "pointlightbank")?[row];
	}

	// Cubemap names a LIGHT_BANK row selects: envDif and envSpc_0..3 are the NNN of
	// EnvDif_<mXX>_<NNN> / EnvSpc_<mXX>_<NNN> in map/<mXX>/<mXX>_9999.tpf. Names use the block's map
	// even for a default_lightbank row (the caller then gets no cubemap). A Dictionary, so GDScript
	// checks can call it; "env_spc" is indexed by g_EnvSpcSlotNo.
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
