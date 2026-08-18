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
