using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SoulsFormats;

namespace Archstone;

// Native scene importer for Demon's Souls .flver files. Reads FLVER0 mesh/material data
// and the sibling .tpf texture archive directly into Godot ImporterMesh/StandardMaterial3D/
// ImageTexture objects, bypassing the OBJ/MTL/DDS relay in desflver_test entirely.
public partial class FlverSceneImporter : EditorSceneFormatImporter
{
	// Merged name->texture lookup per shared texture-bucket folder (e.g. mounted/map/m03/),
	// keyed by that folder's absolute path. Map pieces don't ship a co-located same-basename
	// .tpf like chr/obj models do - their textures are split across many numbered .tpf files
	// in a folder shared by the whole map area, so this is built once and reused across every
	// piece FLVER in that area instead of re-reading ~dozens of MB per mesh.
	// ConcurrentDictionary, not Dictionary: Godot's bulk reimport dispatches _ImportScene calls
	// across multiple worker threads against this same importer instance, so this cache is
	// genuinely shared/mutated concurrently - a plain Dictionary here silently loses entries
	// under contention, which read as random per-piece missing textures.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, TPF.Texture>> _areaTextureCache = new();

	// Some map pieces reuse another asset category's own texture set directly - e.g.
	// m4020b0.flver (a corpse pile in Boletarian Palace) has a material referencing
	// "Model/chr/c2000/tex/c2000_body.tga", chr's own same-basename-tpf convention, not
	// map's shared-area-bucket one. Confirmed via a game-wide scan this isn't a one-off:
	// at least 4 real map files reuse a chr texture set this way, plus ~14 more reference
	// obj/parts the same way (those still resolve to nothing today since obj/ and parts/
	// haven't been unpacked from their .objbnd/.partsbnd archives into loose files the way
	// chr/map already have - this cache just has nothing to find yet, not a code bug).
	// Keyed by resolved absolute .tpf path, not by folder, since - unlike the area bucket -
	// this is always exactly one tpf file, not a merge across many.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, TPF.Texture>> _foreignCategoryTextureCache = new();

	private readonly string _mountedRoot = ProjectSettings.GlobalizePath("res://mounted");

	// Loaded once per importer instance (mirrors _areaTextureCache's one-time-init-is-
	// thread-safe pattern) rather than lazily, since the bulk reimport calls _ImportScene
	// across multiple threads against this same instance.
	private readonly Shader _blendShader = GD.Load<Shader>("res://addons/archstone/terrain_blend.gdshader");
	private readonly Shader _waterShader = GD.Load<Shader>("res://addons/archstone/water.gdshader");

	// One-time index of every real .mtd file under mounted/mtd/ (extracted from mtd.mtdbnd.dcx
	// by AssetExtractor.cs - not present when the rest of this importer's material logic
	// was originally written, see context.md), keyed by lowercase filename. Only the water
	// shader needs this: its per-material wave/reflection tuning (WaterColor, Fresnel*,
	// TileScale/Blend, TexScroll) exists nowhere in FLVER0's own material data, only in
	// the real .mtd. Eager field init, not lazy - runs during construction before any
	// _ImportScene call, so it's naturally thread-safe like _blendShader above, and 306
	// small files is cheap enough to just always index once per importer instance.
	private readonly Dictionary<string, string> _mtdIndex = BuildMtdIndex();

	private static Dictionary<string, string> BuildMtdIndex()
	{
		var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string mtdRoot = ProjectSettings.GlobalizePath("res://mounted/mtd");
		if (System.IO.Directory.Exists(mtdRoot))
			foreach (var path in System.IO.Directory.GetFiles(mtdRoot, "*.mtd", System.IO.SearchOption.AllDirectories))
				index.TryAdd(System.IO.Path.GetFileName(path), path);
		return index;
	}

	public override string[] _GetExtensions() => new[] { "flver" };

	public override GodotObject _ImportScene(string path, uint flags, Godot.Collections.Dictionary options)
	{
		string flverPath = ProjectSettings.GlobalizePath(path);
		var flver = FLVER0.Read(flverPath);

		string tpfPath = System.IO.Path.ChangeExtension(flverPath, ".tpf");
		// Case-insensitive: material texture references don't always match the TPF entry's
		// own casing (e.g. "m03_01_wall_cliff_00" referencing "m03_01_Wall_Cliff_00").
		var texturesByName = System.IO.File.Exists(tpfPath)
			? TPF.Read(tpfPath).Textures.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, TPF.Texture>(StringComparer.OrdinalIgnoreCase);

		var materialCache = new Dictionary<int, Material>();
		var importerMesh = new ImporterMesh();
		bool anySurface = false;

		foreach (var flverMesh in flver.Meshes)
		{
			var material = GetOrBuildMaterial(flverMesh.MaterialIndex, flver, texturesByName, materialCache, flverPath);

			// FLVER's coordinate convention is the mirror image of Godot's: confirmed via bind-pose
			// FK on c0000/c9990's skeleton (L_Hand/R_Shield/etc. bones land on the opposite X side
			// from where their own baked mesh geometry sits) and independently via m03_01_00_00
			// rendering as a literal mirror image of m03_00_00_00. Negate X on position and normal
			// to undo it.
			// Only blend materials (see GetOrBuildMaterial) need a second UV set - the terrain-
			// blend shader samples diffuse2/normal2/specular2 at UV2. Gated on the material
			// itself rather than "mesh has >=2 UV channels" since that's the exact condition
			// the shader needs, and not every mesh in the game is confirmed to have a second
			// UV channel at all (the importer never read past index 0 until now).
			// water.gdshader is also a ShaderMaterial but, like the single-layer path, only
			// ever needs one UV channel - so this checks the specific Shader resource rather
			// than "is this a ShaderMaterial at all", or water meshes (which really do only
			// carry one UV channel, confirmed on real water meshes game-wide) would wrongly
			// try to read a nonexistent v.UVs[1].
			bool needsUV2 = material is ShaderMaterial sm && sm.Shader == _blendShader;

			// Built as plain C# arrays rather than SurfaceTool.AddVertex/SetNormal/SetUV/.../per
			// vertex: each of those is a separate Godot-engine call, and with meshes running into
			// the tens of thousands of vertices across ~3400 mounted FLVERs that overhead adds up
			// across a bulk reimport. Filling native arrays first and handing the whole buffer to
			// the engine in one SurfaceTool.CreateFromArrays() call keeps the same per-vertex math
			// but cuts the per-vertex engine-call count to zero.
			var vertices = flverMesh.Vertices;
			int vertCount = vertices.Count;
			var positions = new Vector3[vertCount];
			var normals = new Vector3[vertCount];
			var colors = new Color[vertCount];
			var uvs = new Vector2[vertCount];
			var uv2s = needsUV2 ? new Vector2[vertCount] : null;
			for (int i = 0; i < vertCount; i++)
			{
				var v = vertices[i];
				positions[i] = new Vector3(-v.Position.X, v.Position.Y, v.Position.Z);
				normals[i] = new Vector3(-v.Normals[0].X, v.Normals[0].Y, v.Normals[0].Z);
				// Vertex color is FLVER's blend-weight channel (SoulsFormats' own doc comment:
				// "data used for blending, alpha, etc.") - harmless to set unconditionally since
				// StandardMaterial3D ignores vertex color unless VertexColorUseAsAlbedo is on.
				var c = v.Colors[0];
				colors[i] = new Color(c.R, c.G, c.B, c.A);
				// No V-flip here (unlike Program.cs's OBJ output): that flip compensates for
				// Wavefront OBJ's V convention, but Image.CreateFromData uses the same top-down
				// row order as the raw decoded texture, so FLVER's raw V is already correct.
				uvs[i] = new Vector2(v.UVs[0].X, v.UVs[0].Y);
				if (needsUV2)
					uv2s[i] = new Vector2(v.UVs[1].X, v.UVs[1].Y);
			}

			// Winding must be swapped here even though it wasn't for the OBJ path: mirroring the
			// X axis above flips the apparent winding of every triangle, turning what used to
			// coincidentally match Godot's clockwise-front convention into the wrong direction.
			var tris = flverMesh.Triangulate(flver.Header.Version, false, true);
			var indices = new int[tris.Count];
			for (int i = 0; i < tris.Count; i += 3)
			{
				indices[i] = tris[i];
				indices[i + 1] = tris[i + 2];
				indices[i + 2] = tris[i + 1];
			}

			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = positions;
			arrays[(int)Mesh.ArrayType.Normal] = normals;
			arrays[(int)Mesh.ArrayType.Color] = colors;
			arrays[(int)Mesh.ArrayType.TexUV] = uvs;
			if (needsUV2)
				arrays[(int)Mesh.ArrayType.TexUV2] = uv2s;
			arrays[(int)Mesh.ArrayType.Index] = indices;

			// GenerateTangents() still needs a SurfaceTool - CreateFromArrays() just loads the
			// prebuilt arrays into it instead of the array being built via per-vertex calls.
			var st = new SurfaceTool();
			st.CreateFromArrays(arrays);
			st.GenerateTangents();
			// The scene-import pipeline (LOD/shadow mesh generation, mesh compression) only
			// operates on the import-time ImporterMesh/ImporterMeshInstance3D representation -
			// returning a runtime ArrayMesh/MeshInstance3D here gets silently dropped on save.
			importerMesh.AddSurface(Mesh.PrimitiveType.Triangles, st.CommitToArrays(), material: material);
			anySurface = true;
		}

		var root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(path) };
		if (anySurface)
		{
			var meshInstance = new ImporterMeshInstance3D { Mesh = importerMesh, Name = "Mesh" };
			root.AddChild(meshInstance);
			// PackedScene.pack() only serializes descendants whose Owner is set - a plain
			// AddChild() alone is invisible to the scene-import save step.
			meshInstance.Owner = root;
		}
		return root;
	}

	private Material GetOrBuildMaterial(int materialIndex, FLVER0 flver,
		Dictionary<string, TPF.Texture> texturesByName, Dictionary<int, Material> cache, string flverPath)
	{
		if (cache.TryGetValue(materialIndex, out var cached)) return cached;

		var flverMaterial = flver.Materials[materialIndex];

		// Water surfaces (DS_Water_Env MTD family: reflective/refractive, no diffuse texture
		// at all, just g_Bumpmap + g_Envmap). Gated on g_Envmap's presence - confirmed unique
		// to this family across all 8889 materials in the game (58/58 instances), so unlike
		// the blend gate below this needs no filename heuristic at all.
		bool isWater = flverMaterial.Textures.Any(t => t.ParamName == "g_Envmap");

		// Map terrain ground-blend materials (grass/dirt/stone transitions): a second
		// diffuse/bumpmap/specular set blended with the first via vertex color. Gated on the
		// MTD's own "[M]"/"[ML]" bracket tag (FromSoft's blend-shader family marker), not on
		// which of specular_2/bumpmap_2 happen to be present - confirmed across all 963
		// g_Diffuse_2 materials in the game that bumpmap and specular are each independently
		// optional per layer (some blend materials have neither, matching g_Diffuse only).
		// The earlier specular_2-required gate wrongly excluded these no-specular blend
		// materials, silently falling back to a single flat texture (e.g. a Boletaria cliff
		// ledge showing bare rock instead of its blended grass layer). The "[M]"/"[ML]" tag
		// cleanly separates all 912 real blend materials from the other 51 g_Diffuse_2
		// materials, which are all unrelated "ghost"/dissolve character effects (MTDs like
		// "Cs_Ghost_Param_Wander.mtd") with no bracket tag at all - those must keep using the
		// single-layer path below unchanged.
		bool isBlend = !isWater
			&& (flverMaterial.MTD.Contains("[M]") || flverMaterial.MTD.Contains("[ML]"))
			&& flverMaterial.Textures.Any(t => t.ParamName == "g_Diffuse_2");

		Material mat = isWater
			? BuildWaterMaterial(flverMaterial, texturesByName, flverPath)
			: isBlend
				? BuildBlendMaterial(flverMaterial, texturesByName, flverPath)
				: BuildStandardMaterial(flverMaterial, texturesByName, flverPath);

		cache[materialIndex] = mat;
		return mat;
	}

	private ImageTexture? ResolveTexture(FLVER0.Material flverMaterial, string paramName,
		Dictionary<string, TPF.Texture> texturesByName, string flverPath)
	{
		var texRef = flverMaterial.Textures.FirstOrDefault(t => t.ParamName == paramName);
		if (texRef == null) return null;
		var refPath = texRef.Path.Replace('\\', '/');
		var key = System.IO.Path.GetFileNameWithoutExtension(refPath);

		if (!texturesByName.TryGetValue(key, out var tpfTex) && !GetAreaTextures(refPath, flverPath).TryGetValue(key, out tpfTex))
			GetForeignCategoryTextures(refPath).TryGetValue(key, out tpfTex);

		return tpfTex != null ? DecodeTexture(tpfTex) : null;
	}

	// Resolves a reference like "Model/chr/c2000/tex/c2000_body.tga" straight to that
	// model's own texture set, for the case where a FLVER in one asset category (here always
	// "map") reuses another category's texture set wholesale rather than shipping/sharing its
	// own. The real on-disk container layout is not uniform across categories - confirmed via
	// real extraction (AssetExtractor.cs), not guessed: chr's chrbnd entries flatten the tpf
	// straight into the model folder (mounted/chr/c2000/c2000.tpf, no "tex" folder at all),
	// while obj/parts' containers keep a real "tex" subfolder matching the reference path
	// literally (mounted/parts/Weapon/WP_A_1503/tex/WP_A_1503.tpf). Try the nested-tex layout
	// first since it matches the reference path as-is, then fall back to the flattened one.
	// Merge every *.tpf found there rather than opening one guessed-basename file: a folder
	// can hold more than one real tpf (e.g. WP_A_1503.tpf and a separate WP_A_1503_L.tpf
	// variant sitting side by side, confirmed on real weapon data - the entry a reference
	// actually needs isn't always in the tpf whose name matches the containing folder). Same
	// merge-everything-in-the-folder pattern BuildAreaTextures already uses for the map-area
	// bucket case below.
	private Dictionary<string, TPF.Texture> GetForeignCategoryTextures(string textureRefPath)
	{
		var segs = textureRefPath.Split('/');
		int modelIdx = Array.FindIndex(segs, s => s.Equals("Model", StringComparison.OrdinalIgnoreCase));
		int texIdx = Array.LastIndexOf(segs, "tex");
		if (modelIdx < 0 || texIdx <= modelIdx + 1) return _emptyTextures;

		string subDir = string.Join('/', segs[(modelIdx + 1)..texIdx]);
		string nestedDir = System.IO.Path.Combine(_mountedRoot, subDir, "tex");
		string flatDir = System.IO.Path.Combine(_mountedRoot, subDir);
		string dir = System.IO.Directory.Exists(nestedDir) ? nestedDir : flatDir;

		return _foreignCategoryTextureCache.GetOrAdd(dir, static d =>
		{
			var textures = new Dictionary<string, TPF.Texture>(StringComparer.OrdinalIgnoreCase);
			if (System.IO.Directory.Exists(d))
				foreach (var tpfFile in System.IO.Directory.GetFiles(d, "*.tpf"))
					foreach (var tex in TPF.Read(tpfFile).Textures)
						textures.TryAdd(tex.Name, tex);
			return textures;
		});
	}

	private StandardMaterial3D BuildStandardMaterial(FLVER0.Material flverMaterial,
		Dictionary<string, TPF.Texture> texturesByName, string flverPath)
	{
		var mat = new StandardMaterial3D();

		void Assign(string paramName, Action<ImageTexture> assign)
		{
			var tex = ResolveTexture(flverMaterial, paramName, texturesByName, flverPath);
			if (tex != null) assign(tex);
		}

		Assign("g_Diffuse", tex => mat.AlbedoTexture = tex);
		Assign("g_Bumpmap", tex => { mat.NormalTexture = tex; mat.NormalEnabled = true; });
		// ponytail: Blinn-Phong spec map jammed into the PBR metallic slot, same
		// simplification the generic OBJ importer forced on us, just explicit now.
		Assign("g_Specular", tex => { mat.MetallicTexture = tex; mat.Metallic = 1.0f; });

		// No blend-mode flag is exposed anywhere in the parsed FLVER0 material - the MTD
		// path name is the only available signal, following the same "_Edge"/"_Alp"/"_Add"
		// naming convention used across FromSoft's Souls games generally (confirmed here: grass/
		// foliage MTDs use "_Edge" and decode to a real, non-trivial alpha mask; "_Add" is used
		// consistently across ~15 map files, all volumetric light shafts/clouds/water shimmer -
		// e.g. "A03_vollight11[Dn]_Add.mtd", "A03_light_shaft[Dn]_Add.mtd" - confirming it's a
		// real additive-blend convention, not a guess).
		if (flverMaterial.MTD.Contains("_Edge"))
			mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
		else if (flverMaterial.MTD.Contains("_Alp"))
			mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		else if (flverMaterial.MTD.Contains("_Add"))
		{
			mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
		}

		return mat;
	}

	// Blend materials' own MTD names (e.g. "M_4Stone[DSB][ML].mtd") never overlap with the
	// _Edge/_Alp/_Add tags above - confirmed always opaque, so no transparency handling here.
	private ShaderMaterial BuildBlendMaterial(FLVER0.Material flverMaterial,
		Dictionary<string, TPF.Texture> texturesByName, string flverPath)
	{
		var mat = new ShaderMaterial { Shader = _blendShader };

		void Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, texturesByName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
		}

		Assign("g_Diffuse", "diffuse1");
		Assign("g_Diffuse_2", "diffuse2");
		Assign("g_Bumpmap", "normal1");
		Assign("g_Bumpmap_2", "normal2");
		Assign("g_Specular", "specular1");
		Assign("g_Specular_2", "specular2");

		return mat;
	}

	// Water surfaces (moats, swamps, lakes, fountains - 58 materials across 45 map files).
	// Unlike every other material family here, g_Bumpmap/g_Envmap are the *only* textures
	// (no g_Diffuse at all - confirmed by direct visual inspection: g_Envmap is a small
	// flat "impression" image of the surrounding scene, not a cubemap, used in-shader as a
	// cheap matcap-style reflection lookup - the standard PS3-era substitute for real
	// reflections). The wave/reflection tuning (tile scale, scroll speed, blend weight per
	// bump layer, water tint, Fresnel curve) has no equivalent anywhere in FLVER0's own
	// material data - it only exists in the real .mtd shader definition, so this is the
	// one material path that reads one via _mtdIndex instead of just the MTD path string.
	private ShaderMaterial BuildWaterMaterial(FLVER0.Material flverMaterial,
		Dictionary<string, TPF.Texture> texturesByName, string flverPath)
	{
		var mat = new ShaderMaterial { Shader = _waterShader };

		void Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, texturesByName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
		}
		Assign("g_Bumpmap", "bumpmap");
		Assign("g_Envmap", "envmap");

		// Falls back to the shader's own built-in uniform defaults (plausible generic
		// water) if the real .mtd can't be found or fails to parse, rather than failing
		// the whole material - same tolerance ResolveTexture already has for a missing
		// texture. mounted/mtd/ is a recent addition and hasn't been proven to cover
		// literally every material referenced in the game.
		string mtdName = System.IO.Path.GetFileName(flverMaterial.MTD.Replace('\\', '/'));
		if (_mtdIndex.TryGetValue(mtdName, out var mtdPath))
		{
			try
			{
				var mtd = MTD.Read(mtdPath);
				mat.SetShaderParameter("tile_scale_0", GetMtdVector2(mtd, "g_TileScale_0", Vector2.One));
				mat.SetShaderParameter("tile_scale_1", GetMtdVector2(mtd, "g_TileScale_1", Vector2.One));
				mat.SetShaderParameter("tile_scale_2", GetMtdVector2(mtd, "g_TileScale_2", Vector2.One));
				mat.SetShaderParameter("tex_scroll_0", GetMtdVector2(mtd, "g_TexScroll_0", Vector2.Zero));
				mat.SetShaderParameter("tex_scroll_1", GetMtdVector2(mtd, "g_TexScroll_1", Vector2.Zero));
				mat.SetShaderParameter("tex_scroll_2", GetMtdVector2(mtd, "g_TexScroll_2", Vector2.Zero));
				mat.SetShaderParameter("tile_blend_0", GetMtdFloat(mtd, "g_TileBlend_0", 1.0f));
				mat.SetShaderParameter("tile_blend_1", GetMtdFloat(mtd, "g_TileBlend_1", 0.0f));
				mat.SetShaderParameter("tile_blend_2", GetMtdFloat(mtd, "g_TileBlend_2", 0.0f));
				mat.SetShaderParameter("water_color", GetMtdColor3(mtd, "g_WaterColor", new Color(0.1f, 0.15f, 0.2f)));
				// g_WaterColor's 4th component (opacity, not read by GetMtdColor3 above, which
				// only keeps RGB) - confirmed to matter by comparing real per-material data: a
				// murky/opaque swamp (g_ReflectBand=0) has a *higher* water_alpha than a clear
				// reflective moat (g_ReflectBand=0.1), not a lower one, so this is a real "how
				// much does this water hide what's beneath it" signal, not a stray value to
				// ignore. See water.gdshader for how it's used.
				mat.SetShaderParameter("water_alpha", GetMtdFloat4Alpha(mtd, "g_WaterColor", 0.7f));
				mat.SetShaderParameter("refract_band", GetMtdFloat(mtd, "g_RefractBand", 0.15f));
				mat.SetShaderParameter("reflect_band", GetMtdFloat(mtd, "g_ReflectBand", 0.1f));
				mat.SetShaderParameter("fresnel_pow", GetMtdFloat(mtd, "g_FresnelPow", 3.0f));
				mat.SetShaderParameter("fresnel_bias", GetMtdFloat(mtd, "g_FresnelBias", 0.1f));
				mat.SetShaderParameter("fresnel_scale", GetMtdFloat(mtd, "g_FresnelScale", 1.0f));
				mat.SetShaderParameter("fresnel_color", GetMtdColor3(mtd, "g_FresnelColor", Colors.White));
				mat.SetShaderParameter("water_fade_begin", GetMtdFloat(mtd, "g_WaterFadeBegin", 0.5f));
			}
			catch (Exception) { /* keep shader defaults */ }
		}

		return mat;
	}

	private static float GetMtdFloat(MTD mtd, string name, float fallback)
	{
		var p = mtd.Params.FirstOrDefault(x => x.Name == name);
		return p?.Value is float f ? f : fallback;
	}

	private static float GetMtdFloat4Alpha(MTD mtd, string name, float fallback)
	{
		var p = mtd.Params.FirstOrDefault(x => x.Name == name);
		return p?.Value is float[] v && v.Length >= 4 ? v[3] : fallback;
	}

	private static Vector2 GetMtdVector2(MTD mtd, string name, Vector2 fallback)
	{
		var p = mtd.Params.FirstOrDefault(x => x.Name == name);
		return p?.Value is float[] v && v.Length >= 2 ? new Vector2(v[0], v[1]) : fallback;
	}

	// Also used for g_WaterColor, which is a Float4 - the array-length check makes this
	// work for either Float3 or Float4 params, just taking the first 3 components (RGB,
	// ignoring source alpha - see water.gdshader's header comment on why). Returns Color,
	// not Vector3: Godot's C# SetShaderParameter silently no-ops (leaves the parameter
	// unset, not an exception) when handed a Vector3 for a uniform hinted `: source_color`
	// - confirmed by inspecting the imported material with get_shader_parameter() after
	// wiring this up with Vector3 first and finding water_color/fresnel_color read back
	// null while every other (non-color-hinted) parameter came through fine.
	private static Color GetMtdColor3(MTD mtd, string name, Color fallback)
	{
		var p = mtd.Params.FirstOrDefault(x => x.Name == name);
		return p?.Value is float[] v && v.Length >= 3 ? new Color(v[0], v[1], v[2]) : fallback;
	}

	// Map textures are named like ".../Model/map/m03/tex/m03_01_Wall_Cliff_00.tga" - the
	// segment right before "tex" (here "m03") is a folder shared by the whole map area,
	// sitting as a sibling of the piece's own containing folder
	// (mounted/map/m03_01_00_00/piece.flver -> mounted/map/m03/), holding many numbered .tpf
	// files that together cover every texture used across that area.
	private Dictionary<string, TPF.Texture> GetAreaTextures(string textureRefPath, string flverPath)
	{
		var segments = textureRefPath.Split('/');
		int texIndex = Array.LastIndexOf(segments, "tex");
		if (texIndex < 1) return _emptyTextures;
		string area = segments[texIndex - 1];

		string? typeDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(flverPath));
		if (typeDir == null) return _emptyTextures;
		string areaDir = System.IO.Path.Combine(typeDir, area);

		return _areaTextureCache.GetOrAdd(areaDir, BuildAreaTextures);
	}

	private static Dictionary<string, TPF.Texture> BuildAreaTextures(string areaDir)
	{
		var merged = new Dictionary<string, TPF.Texture>(StringComparer.OrdinalIgnoreCase);
		if (System.IO.Directory.Exists(areaDir))
		{
			foreach (var tpfFile in System.IO.Directory.GetFiles(areaDir, "*.tpf"))
				foreach (var tex in TPF.Read(tpfFile).Textures)
					merged.TryAdd(tex.Name, tex);
		}
		return merged;
	}

	private static readonly Dictionary<string, TPF.Texture> _emptyTextures = new();

	// Mirrors desflver_test/Program.cs's texture decode path (Headerize -> Pfim -> BGRA swap).
	// Kept as a separate copy since Program.cs is top-level-statement code with nothing to
	// reference and SoulsFormats.csproj shouldn't gain an image-decoding dependency.
	private static ImageTexture DecodeTexture(TPF.Texture texture)
	{
		var ddsBytes = Headerizer.Headerize(texture, out _);
		using var ddsStream = new System.IO.MemoryStream(ddsBytes);
		using var pfImage = Pfim.Pfimage.FromStream(ddsStream);
		if (pfImage.Format != Pfim.ImageFormat.Rgba32)
			throw new NotSupportedException($"{texture.Name}: unhandled Pfim format {pfImage.Format}");

		// Pfim's Data buffer includes the full mip chain (~4/3 of the base level's size),
		// but we only want the base level here since GenerateMipmaps() rebuilds the rest.
		int baseLevelSize = pfImage.Width * pfImage.Height * 4;
		var rgba = pfImage.Data.AsSpan(0, baseLevelSize).ToArray();
		for (int i = 0; i < rgba.Length; i += 4)
			(rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); // Pfim decodes to BGRA despite the "Rgba32" name

		var image = Image.CreateFromData(pfImage.Width, pfImage.Height, false, Image.Format.Rgba8, rgba);
		image.GenerateMipmaps();
		return ImageTexture.CreateFromImage(image);
	}
}
