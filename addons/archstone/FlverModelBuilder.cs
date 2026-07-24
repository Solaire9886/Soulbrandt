using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using SoulsFormats;

namespace Archstone;

// FLVER0 parsing/mesh/material/texture-resolution logic. No Godot import-system dependency -
// only ever driven by FlverLoader, single-threaded (see CLAUDE.md's Architecture section).
public partial class FlverModelBuilder : RefCounted
{
	// Keyed by resolved directory; merges every *.tpf found there. See CLAUDE.md's "Texture
	// resolution" section for the CandidateDirs rules that produce the directory keys.
	private readonly Dictionary<string, Dictionary<string, TPF.Texture>> _dirTextureCache = new();

	// Dedupes the actual DXT decode (not just tpf parsing) - keyed by TPF.Texture reference
	// identity since it has no Equals/GetHashCode override.
	private readonly Dictionary<TPF.Texture, ImageTexture> _decodedTextureCache = new();

	// ponytail: whole-cache clear on budget overrun, not per-entry LRU - see CLAUDE.md.
	private long _decodedBytes;

	// 25% of GC-reported available memory, floored at 256MB - scales down on weaker hardware.
	private static readonly long DecodedBudgetBytes =
		Math.Max((long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.25), 256L * 1024 * 1024);

	private void MaybeEvictDecodedTextures()
	{
		if (_decodedBytes < DecodedBudgetBytes) return;
		_decodedTextureCache.Clear();
		_dirTextureCache.Clear();
		_decodedBytes = 0;
	}

	private readonly string _mountedRoot = ProjectSettings.GlobalizePath("res://mounted");

	private readonly Shader _blendShader = GD.Load<Shader>("res://addons/archstone/terrain_blend.gdshader");
	private readonly Shader _waterShader = GD.Load<Shader>("res://addons/archstone/water.gdshader");
	// Three variants, not one runtime-switched shader: blend_mix/blend_add are compile-time
	// render_mode keywords in Godot, not a per-material property.
	private readonly Shader _lightmapShader = GD.Load<Shader>("res://addons/archstone/lightmap.gdshader");
	private readonly Shader _lightmapAlphaShader = GD.Load<Shader>("res://addons/archstone/lightmap_alpha.gdshader");
	private readonly Shader _lightmapAddShader = GD.Load<Shader>("res://addons/archstone/lightmap_add.gdshader");

	// Index of mounted/mtd/*.mtd by filename - only the water shader needs real .mtd data
	// (per-material wave/reflection tuning has no equivalent in FLVER0's own material data).
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

	// Builds an ImporterMesh with every surface's material resolved, plus whether any surface
	// was actually built (some obj/ FLVER0 files are genuine meshless dummy/anchor markers).
	public ImporterMesh BuildMesh(string path, out bool anySurface)
	{
		string flverPath = ProjectSettings.GlobalizePath(path);
		var flver = FLVER0.Read(flverPath);

		var materialCache = new Dictionary<int, Material>();
		var importerMesh = new ImporterMesh();
		anySurface = false;

		foreach (var flverMesh in flver.Meshes)
		{
			var material = GetOrBuildMaterial(flverMesh.MaterialIndex, flver, materialCache, flverPath);
			var (_, isBlend, hasLightmap) = ClassifyMaterial(flver.Materials[flverMesh.MaterialIndex]);

			// Blend materials use UV1 for their second layer; non-blend materials with a lightmap
			// use UV1 for the lightmap itself. Either way, one extra UV channel is needed.
			bool needsUV2 = isBlend || hasLightmap;
			// Blend+lightmap meshes have a genuine third UV channel, packed into Custom0 (raw
			// floats) since both native UV slots are already used by the two diffuse layers.
			bool needsLightmapCustom0 = isBlend && hasLightmap;

			// Built as plain C# arrays and handed to SurfaceTool.CreateFromArrays() in one call,
			// rather than per-vertex AddVertex/SetNormal/SetUV - avoids per-vertex engine calls.
			var vertices = flverMesh.Vertices;
			int vertCount = vertices.Count;
			var positions = new Vector3[vertCount];
			var normals = new Vector3[vertCount];
			var colors = new Color[vertCount];
			var uvs = new Vector2[vertCount];
			var uv2s = needsUV2 ? new Vector2[vertCount] : null;
			var custom0s = needsLightmapCustom0 ? new float[vertCount * 2] : null;
			for (int i = 0; i < vertCount; i++)
			{
				var v = vertices[i];
				// X negated to match Godot's coordinate convention (mirror of FLVER's) - see CLAUDE.md.
				positions[i] = new Vector3(-v.Position.X, v.Position.Y, v.Position.Z);
				// Some FLVER0 meshes' BufferLayout genuinely omits Normal/Color/UV - fall back to
				// a neutral default rather than indexing [0] unconditionally (see CLAUDE.md).
				normals[i] = v.Normals.Count > 0
					? new Vector3(-v.Normals[0].X, v.Normals[0].Y, v.Normals[0].Z)
					: Vector3.Up;
				colors[i] = v.Colors.Count > 0
					? new Color(v.Colors[0].R, v.Colors[0].G, v.Colors[0].B, v.Colors[0].A)
					: Colors.White;
				// No V-flip: Image.CreateFromData uses the same top-down row order as the raw
				// decoded texture, so FLVER's raw V is already correct.
				uvs[i] = v.UVs.Count > 0 ? new Vector2(v.UVs[0].X, v.UVs[0].Y) : Vector2.Zero;
				if (needsUV2)
					uv2s[i] = v.UVs.Count > 1 ? new Vector2(v.UVs[1].X, v.UVs[1].Y) : Vector2.Zero;
				if (needsLightmapCustom0 && v.UVs.Count > 2)
				{
					custom0s[i * 2] = v.UVs[2].X;
					custom0s[i * 2 + 1] = v.UVs[2].Y;
				}
			}

			// Winding swapped (two indices per face) because the X negation above flips the
			// apparent winding of every triangle - see CLAUDE.md.
			// doCheckFlip is only meaningful (and only requested) when the mesh's vertices
			// actually carry Normal data - it reads Normal internally and crashes otherwise.
			bool canCheckFlip = vertCount > 0 && vertices[0].Normals.Count > 0;
			var tris = flverMesh.Triangulate(flver.Header.Version, false, canCheckFlip);
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
			if (needsLightmapCustom0)
				arrays[(int)Mesh.ArrayType.Custom0] = custom0s;
			arrays[(int)Mesh.ArrayType.Index] = indices;

			var st = new SurfaceTool();
			st.CreateFromArrays(arrays);
			st.GenerateTangents();
			// Custom0's component format isn't inferable like Vertex/Normal/UV are - must be
			// spelled out explicitly or the lightmap UV silently fails to read back in-shader.
			ulong customArrayFormat = needsLightmapCustom0
				? (ulong)Mesh.ArrayCustomFormat.RgFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift
				: 0;
			importerMesh.AddSurface(Mesh.PrimitiveType.Triangles, st.CommitToArrays(), material: material, flags: customArrayFormat);
			anySurface = true;
		}

		return importerMesh;
	}

	private Material GetOrBuildMaterial(int materialIndex, FLVER0 flver,
		Dictionary<int, Material> cache, string flverPath)
	{
		if (cache.TryGetValue(materialIndex, out var cached)) return cached;

		var flverMaterial = flver.Materials[materialIndex];
		InferMissingParamNames(flverMaterial);

		var (isWater, isBlend, hasLightmap) = ClassifyMaterial(flverMaterial);

		Material mat = isWater
			? BuildWaterMaterial(flverMaterial, flverPath)
			: isBlend
				? BuildBlendMaterial(flverMaterial, flverPath)
				: hasLightmap
					? BuildLightmapMaterial(flverMaterial, flverPath)
					: BuildStandardMaterial(flverMaterial, flverPath);

		cache[materialIndex] = mat;
		return mat;
	}

	// Shared classification so BuildMesh's vertex loop and GetOrBuildMaterial's material
	// selection can't drift out of sync.
	private static (bool IsWater, bool IsBlend, bool HasLightmap) ClassifyMaterial(FLVER0.Material mat)
	{
		// g_Envmap uniquely identifies water materials game-wide - see CLAUDE.md.
		bool isWater = mat.Textures.Any(t => t.ParamName == "g_Envmap");

		// Gated on the MTD's "[M]"/"[ML]" blend-shader tag, not g_Specular_2/g_Bumpmap_2
		// presence (each is independently optional per layer) - see CLAUDE.md.
		bool isBlend = !isWater
			&& (mat.MTD.Contains("[M]") || mat.MTD.Contains("[ML]"))
			&& mat.Textures.Any(t => t.ParamName == "g_Diffuse_2");

		bool hasLightmap = mat.Textures.Any(t => t.ParamName == "g_Lightmap");

		return (isWater, isBlend, hasLightmap);
	}

	// A texture slot's type (ParamName, e.g. "g_Diffuse") is sometimes absent from the raw
	// FLVER0 data even though the path is real - recovered by positionally matching each
	// untyped entry against the MTD's own bracket tag (e.g. "[DifSpcBmp_Skin]"). Must run
	// before any other logic here, since everything else depends on ParamName being set.
	private static readonly (string Token, string ParamName)[] TextureSlotTokens =
	{
		("Dif", "g_Diffuse"), ("Spc", "g_Specular"), ("Bmp", "g_Bumpmap"), ("Lit", "g_Lightmap"),
		("Dcl", "g_Diffuse"), // sky/decal materials - single backdrop texture
	};

	private static void InferMissingParamNames(FLVER0.Material mat)
	{
		var untyped = mat.Textures.Where(t => string.IsNullOrEmpty(t.ParamName)).ToList();
		if (untyped.Count == 0) return;

		var bracket = Regex.Match(mat.MTD, @"\[([^\]]*)\]");
		string tag = bracket.Success ? bracket.Groups[1].Value : mat.MTD;

		int slot = 0;
		foreach (var (token, paramName) in TextureSlotTokens)
		{
			if (slot >= untyped.Count) break;
			if (tag.Contains(token))
				untyped[slot++].ParamName = paramName;
		}
	}

	// Ordered candidate directories for a texture reference, most-trusted first - see
	// CLAUDE.md's "Texture resolution" section for what each rule covers and why.
	private IEnumerable<string?> CandidateDirs(FLVER0.Material mat, FLVER0.Texture texRef, string flverPath)
	{
		yield return OwnModelDir(flverPath);
		yield return RefPathDir(texRef.Path);
		yield return SiblingMapAreaDir(mat, texRef);
		yield return MapPrefixDir(texRef.Path);
	}

	private ImageTexture? ResolveTexture(FLVER0.Material flverMaterial, string paramName, string flverPath)
	{
		var texRef = flverMaterial.Textures.FirstOrDefault(t => t.ParamName == paramName);
		if (texRef == null || string.IsNullOrEmpty(texRef.Path)) return null;
		var key = System.IO.Path.GetFileNameWithoutExtension(texRef.Path.Replace('\\', '/'));

		foreach (var dir in CandidateDirs(flverMaterial, texRef, flverPath))
			if (GetMergedTextures(dir).TryGetValue(key, out var tpfTex))
			{
				if (!_decodedTextureCache.TryGetValue(tpfTex, out var decoded))
					_decodedTextureCache[tpfTex] = decoded = DecodeTexture(tpfTex);
				return decoded;
			}

		return null;
	}

	// The model's own container: co-located tpf for chr, sibling "tex" folder for obj/parts
	// (whose flver instead lives in its own "sib" folder).
	private static string OwnModelDir(string flverPath)
	{
		string dir = System.IO.Path.GetDirectoryName(flverPath)!;
		return string.Equals(System.IO.Path.GetFileName(dir), "sib", StringComparison.OrdinalIgnoreCase)
			? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir)!, "tex")
			: dir;
	}

	// Resolves a "Model/.../tex/..." reference path relative to mounted root. Container layout
	// differs by category (chr flattens tpf into the model folder, obj/parts keep a nested
	// "tex" subfolder) - try nested first, then the flattened fallback.
	private string? RefPathDir(string textureRefPath)
	{
		var segs = textureRefPath.Replace('\\', '/').Split('/');
		int modelIdx = Array.FindIndex(segs, s => s.Equals("Model", StringComparison.OrdinalIgnoreCase));
		int texIdx = Array.LastIndexOf(segs, "tex");
		if (modelIdx < 0 || texIdx <= modelIdx + 1) return null;

		string subDir = string.Join('/', segs[(modelIdx + 1)..texIdx]);
		string nestedDir = System.IO.Path.Combine(_mountedRoot, subDir, "tex");
		string flatDir = System.IO.Path.Combine(_mountedRoot, subDir);
		return System.IO.Directory.Exists(nestedDir) ? nestedDir : flatDir;
	}

	// Another texture slot on the same material that resolves under mounted/map - recovers a
	// stale/copy-pasted reference (see CLAUDE.md, e.g. the Nexus archstones).
	private string? SiblingMapAreaDir(FLVER0.Material mat, FLVER0.Texture exclude)
	{
		string mapRoot = System.IO.Path.Combine(_mountedRoot, "map");
		foreach (var t in mat.Textures)
		{
			if (t == exclude || string.IsNullOrEmpty(t.Path)) continue;
			var dir = RefPathDir(t.Path);
			if (dir != null && dir.StartsWith(mapRoot, StringComparison.OrdinalIgnoreCase))
				return dir;
		}
		return null;
	}

	// A map-area prefix baked into the texture's own filename (e.g. "m03_..."), tried even
	// when the reference path's own category claim is wrong.
	private static readonly Regex MapPrefixPattern = new(@"^(m\d\d)_", RegexOptions.IgnoreCase);

	private string? MapPrefixDir(string textureRefPath)
	{
		var key = System.IO.Path.GetFileNameWithoutExtension(textureRefPath.Replace('\\', '/'));
		var m = MapPrefixPattern.Match(key);
		return m.Success ? System.IO.Path.Combine(_mountedRoot, "map", m.Groups[1].Value.ToLowerInvariant()) : null;
	}

	private Dictionary<string, TPF.Texture> GetMergedTextures(string? dir)
	{
		if (dir == null) return _emptyTextures;
		if (!_dirTextureCache.TryGetValue(dir, out var textures))
			_dirTextureCache[dir] = textures = LoadDirTextures(dir);
		return textures;
	}

	// Merges every *.tpf in a directory rather than opening one guessed-basename file - a
	// folder can hold more than one real tpf. Corrupt/zero-byte tpfs are skipped with a
	// warning rather than aborting the whole model.
	private static Dictionary<string, TPF.Texture> LoadDirTextures(string dir)
	{
		var textures = new Dictionary<string, TPF.Texture>(StringComparer.OrdinalIgnoreCase);
		if (System.IO.Directory.Exists(dir))
			foreach (var tpfFile in System.IO.Directory.GetFiles(dir, "*.tpf"))
			{
				TPF tpf;
				try { tpf = TPF.Read(tpfFile); }
				catch (Exception e)
				{
					GD.PushWarning($"Skipping unreadable tpf '{tpfFile}': {e.Message}");
					continue;
				}
				foreach (var tex in tpf.Textures)
					textures.TryAdd(tex.Name, tex);
			}
		return textures;
	}

	private StandardMaterial3D BuildStandardMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		var mat = new StandardMaterial3D();

		void Assign(string paramName, Action<ImageTexture> assign)
		{
			var tex = ResolveTexture(flverMaterial, paramName, flverPath);
			if (tex != null) assign(tex);
		}

		Assign("g_Diffuse", tex => mat.AlbedoTexture = tex);
		Assign("g_Bumpmap", tex => { mat.NormalTexture = tex; mat.NormalEnabled = true; });
		// ponytail: Blinn-Phong spec map jammed into the PBR metallic slot - only slot available
		// without a custom shader.
		Assign("g_Specular", tex => { mat.MetallicTexture = tex; mat.Metallic = 1.0f; });

		// No blend-mode flag in FLVER0 material data - MTD filename convention only
		// ("_Edge"/"_Alp"/"_Add", shared across FromSoft's Souls games).
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

	// Non-blend materials with a real g_Lightmap - StandardMaterial3D has no independent-UV
	// multiply slot, so these use a small shader family instead (three variants for
	// opaque/_Alp/_Add - see lightmap.gdshader's header for why not one runtime-switched shader).
	private ShaderMaterial BuildLightmapMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		Shader shader = _lightmapShader;
		float scissorThreshold = 0.0f;
		if (flverMaterial.MTD.Contains("_Edge"))
			scissorThreshold = 0.5f; // matches StandardMaterial3D's own AlphaScissor default
		else if (flverMaterial.MTD.Contains("_Alp"))
			shader = _lightmapAlphaShader;
		else if (flverMaterial.MTD.Contains("_Add"))
			shader = _lightmapAddShader;

		var mat = new ShaderMaterial { Shader = shader };

		void Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
		}

		Assign("g_Diffuse", "diffuse");
		Assign("g_Bumpmap", "normal_map");
		Assign("g_Specular", "specular");
		Assign("g_Lightmap", "lightmap");

		// Only lightmap.gdshader has this uniform - _Alp/_Add variants always do real blending.
		if (shader == _lightmapShader)
			mat.SetShaderParameter("alpha_scissor_threshold", scissorThreshold);

		return mat;
	}

	// Blend materials are always opaque - their MTD names never overlap _Edge/_Alp/_Add.
	private ShaderMaterial BuildBlendMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		var mat = new ShaderMaterial { Shader = _blendShader };

		void Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
		}

		Assign("g_Diffuse", "diffuse1");
		Assign("g_Diffuse_2", "diffuse2");
		Assign("g_Bumpmap", "normal1");
		Assign("g_Bumpmap_2", "normal2");
		Assign("g_Specular", "specular1");
		Assign("g_Specular_2", "specular2");
		Assign("g_Lightmap", "lightmap"); // optional - shader's lightmap uniform no-ops if unset

		return mat;
	}

	// Water surfaces (g_Envmap-gated). Unlike other material families, wave/reflection tuning
	// has no FLVER0 equivalent, so this is the one path that reads the real .mtd file directly.
	private ShaderMaterial BuildWaterMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		var mat = new ShaderMaterial { Shader = _waterShader };

		void Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
		}
		Assign("g_Bumpmap", "bumpmap");
		Assign("g_Envmap", "envmap");

		// Falls back to the shader's own defaults if the .mtd can't be found/parsed.
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
				// g_WaterColor's 4th component (alpha) - see water.gdshader for how it's used.
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

	// Also used for g_WaterColor (Float4) - takes just the first 3 components. Returns Color,
	// not Vector3: SetShaderParameter silently no-ops on a `: source_color`-hinted uniform if
	// given a Vector3.
	private static Color GetMtdColor3(MTD mtd, string name, Color fallback)
	{
		var p = mtd.Params.FirstOrDefault(x => x.Name == name);
		return p?.Value is float[] v && v.Length >= 3 ? new Color(v[0], v[1], v[2]) : fallback;
	}

	private static readonly Dictionary<string, TPF.Texture> _emptyTextures = new();

	// Headerize -> Pfim DXT decompress -> BGRA/RGBA swap -> GenerateMipmaps. Instance method
	// (not static) so it can charge the decoded size against _decodedBytes.
	private ImageTexture DecodeTexture(TPF.Texture texture)
	{
		var ddsBytes = Headerizer.Headerize(texture, out _);
		using var ddsStream = new System.IO.MemoryStream(ddsBytes);
		using var pfImage = Pfim.Pfimage.FromStream(ddsStream);
		if (pfImage.Format != Pfim.ImageFormat.Rgba32)
			throw new NotSupportedException($"{texture.Name}: unhandled Pfim format {pfImage.Format}");

		// Pfim's Data buffer includes the full mip chain; only the base level is needed here.
		int baseLevelSize = pfImage.Width * pfImage.Height * 4;
		var rgba = pfImage.Data.AsSpan(0, baseLevelSize).ToArray();
		for (int i = 0; i < rgba.Length; i += 4)
			(rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); // Pfim decodes to BGRA despite "Rgba32"

		var image = Image.CreateFromData(pfImage.Width, pfImage.Height, false, Image.Format.Rgba8, rgba);
		image.GenerateMipmaps();

		_decodedBytes += baseLevelSize * 4 / 3;
		MaybeEvictDecodedTextures();

		return ImageTexture.CreateFromImage(image);
	}
}
