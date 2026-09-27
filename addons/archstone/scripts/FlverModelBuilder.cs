using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using SoulsFormats;

namespace Archstone;

// FLVER0 -> ImporterMesh with materials and textures resolved. Driven only by FlverLoader, on one
// thread, so the caches are plain dictionaries.
public partial class FlverModelBuilder : RefCounted
{
	// Every *.tpf in a resolved directory, merged (directories from CandidateDirs).
	private readonly Dictionary<string, Dictionary<string, TPF.Texture>> _dirTextureCache = new();

	// Decoded textures by TPF.Texture reference identity.
	private readonly Dictionary<TPF.Texture, ImageTexture> _decodedTextureCache = new();

	// "<mapPrefix>/<cubemapName>", nulls included so failures are not retried. Outside the memory
	// budget: a map's 10-25 small cubemaps total well under a megabyte.
	private readonly Dictionary<string, Cubemap?> _cubemapCache = new();

	// ponytail: whole-cache clear on budget overrun, not per-entry LRU.
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

	// "Reload Loaded Models": drops every cache so re-imported mounted/ content is picked up
	// without an editor restart.
	public void ResetCaches()
	{
		_shadingCache.Clear();
		_shaderLibrary.ResetCaches();
		_dirTextureCache.Clear();
		_decodedTextureCache.Clear();
		_cubemapCache.Clear();
		_decodedBytes = 0;
		_objTextureIndex = null;
	}

	private readonly string _mountedRoot = ProjectSettings.GlobalizePath("res://mounted");

	private readonly Shader _blendShader = GD.Load<Shader>("res://addons/archstone/shaders/terrain_blend.gdshader");
	private readonly Shader _waterShader = GD.Load<Shader>("res://addons/archstone/shaders/water.gdshader");
	// One shader per blend mode and cull mode: both are compile-time render_mode keywords.
	private readonly Shader _lightmapShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap.gdshader");
	private readonly Shader _lightmapAlphaShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_alpha.gdshader");
	private readonly Shader _lightmapAddShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_add.gdshader");
	private readonly Shader _lightmapSubShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_sub.gdshader");
	// Double-sided (CullBackfaces = false) variants; only opaque/alpha-test (457 meshes) and alpha
	// blend (23) occur.
	private readonly Shader _lightmapDoubleSidedShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_double_sided.gdshader");
	private readonly Shader _lightmapAlphaDoubleSidedShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_alpha_double_sided.gdshader");
	// Unlit blended or scrolling materials (see MtdShading.NeedsUnlitShader).
	private readonly Shader _vfxScrollShader = GD.Load<Shader>("res://addons/archstone/shaders/vfx_scroll.gdshader");
	private readonly Shader _vfxScrollAddShader = GD.Load<Shader>("res://addons/archstone/shaders/vfx_scroll_add.gdshader");
	private readonly Shader _skyShader = GD.Load<Shader>("res://addons/archstone/shaders/sky.gdshader");

	// mounted/mtd/*.mtd by file name.
	private readonly Dictionary<string, string> _mtdIndex = BuildMtdIndex();

	// The game's shader library, for routing materials by their authored shader (ClassifyMaterial).
	private readonly ShaderLibrary _shaderLibrary = new();

	// ResolveMtdShading results by MTD file name.
	private readonly Dictionary<string, MtdShading> _shadingCache = new();

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

		// Keyed by (materialIndex, CullBackfaces) - not materialIndex alone, since a mesh's own
		// CullBackfaces is a real per-mesh flag that can differ across meshes sharing one
		// material (confirmed: 39 real files, mostly hair/parts). See GetOrBuildMaterial.
		var materialCache = new Dictionary<(int, bool), Material>();
		var importerMesh = new ImporterMesh();
		anySurface = false;

		foreach (var flverMesh in flver.Meshes)
		{
			var material = GetOrBuildMaterial(flverMesh.MaterialIndex, flverMesh.CullBackfaces, flver, materialCache, flverPath);
			var (_, isBlend, hasLightmap) = ClassifyMaterial(flver.Materials[flverMesh.MaterialIndex]);
			// Selected per vertex by v.BoneIndices[0]: one mesh can mix vertices bound to different nodes.
			var rigidTransforms = GetRigidNodeTransforms(flver, flverMesh);

			// UV1 is the second layer (blend) or the lightmap (single layer).
			bool needsUV2 = isBlend || hasLightmap;
			// A blend material's lightmap is the third UV set, packed into Custom0.
			bool needsLightmapCustom0 = isBlend && hasLightmap;

			// Plain arrays handed over in one CreateFromArrays call (no per-vertex engine calls).
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
				// Rigid node binding first, in FLVER space; identity for meshes that don't use it.
				var rigidTransform = rigidTransforms[v.BoneIndices[0]];
				var pos = System.Numerics.Vector3.Transform(v.Position, rigidTransform);
				// X negated: FLVER space mirrors Godot's.
				positions[i] = new Vector3(-pos.X, pos.Y, pos.Z);
				// Some layouts omit normal/colour/UV entirely; use neutral defaults.
				if (v.Normals.Count > 0)
				{
					var normal = System.Numerics.Vector3.Normalize(
						System.Numerics.Vector3.TransformNormal(v.Normals[0], rigidTransform));
					normals[i] = new Vector3(-normal.X, normal.Y, normal.Z);
				}
				else
				{
					normals[i] = Vector3.Up;
				}
				colors[i] = v.Colors.Count > 0
					? new Color(v.Colors[0].R, v.Colors[0].G, v.Colors[0].B, v.Colors[0].A)
					: Colors.White;
				// No V-flip: Image.CreateFromData keeps the decoded row order.
				uvs[i] = v.UVs.Count > 0 ? new Vector2(v.UVs[0].X, v.UVs[0].Y) : Vector2.Zero;
				if (needsUV2)
					uv2s[i] = v.UVs.Count > 1 ? new Vector2(v.UVs[1].X, v.UVs[1].Y) : Vector2.Zero;
				if (needsLightmapCustom0 && v.UVs.Count > 2)
				{
					custom0s[i * 2] = v.UVs[2].X;
					custom0s[i * 2 + 1] = v.UVs[2].Y;
				}
			}

			// Winding swapped to compensate the X negation. Triangulate's flip check reads normals
			// and crashes without them.
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
			// Custom0's format must be given explicitly or the shader reads nothing.
			ulong customArrayFormat = needsLightmapCustom0
				? (ulong)Mesh.ArrayCustomFormat.RgFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift
				: 0;
			importerMesh.AddSurface(Mesh.PrimitiveType.Triangles, st.CommitToArrays(), material: material, flags: customArrayFormat);
			anySurface = true;
		}

		return importerMesh;
	}

	// World transform of each node in a static mesh's BoneIndices palette (parents composed).
	// Unused slots and skinned meshes (UseBoneWeights) get identity: skinning is not implemented.
	private static System.Numerics.Matrix4x4[] GetRigidNodeTransforms(FLVER0 flver, FLVER0.Mesh mesh)
	{
		var transforms = new System.Numerics.Matrix4x4[mesh.BoneIndices.Length];
		if (mesh.UseBoneWeights)
		{
			Array.Fill(transforms, System.Numerics.Matrix4x4.Identity);
			return transforms;
		}

		for (int p = 0; p < mesh.BoneIndices.Length; p++)
		{
			var transform = System.Numerics.Matrix4x4.Identity;
			short nodeIndex = mesh.BoneIndices[p];
			while (nodeIndex >= 0 && nodeIndex < flver.Nodes.Count)
			{
				var node = flver.Nodes[nodeIndex];
				transform *= node.ComputeLocalTransform();
				nodeIndex = node.ParentIndex;
			}
			transforms[p] = transform;
		}
		return transforms;
	}

	private Material GetOrBuildMaterial(int materialIndex, bool cullBackfaces, FLVER0 flver,
		Dictionary<(int, bool), Material> cache, string flverPath)
	{
		var key = (materialIndex, cullBackfaces);
		if (cache.TryGetValue(key, out var cached)) return cached;

		var flverMaterial = flver.Materials[materialIndex];
		InferMissingParamNames(flverMaterial);

		var (isWater, isBlend, hasLightmap) = ClassifyMaterial(flverMaterial);
		var shading = ResolveMtdShading(flverMaterial);

		// Every lit material (g_LightingType 1 = HemDir3, 3 = HemEnv) takes the lit shader family,
		// lightmap or not: without a lightmap the engine's min(shadow, lightmap) gate reduces to
		// the shadow, which the shader's white default reproduces. Unlit type 0 goes to
		// BuildStandardMaterial, which picks vfx_scroll, sky or StandardMaterial3D.
		bool isShaderLit = !isWater && !isBlend
			&& (shading.LightingType == 1 || shading.LightingType == 3);

		Material mat = isWater
			? BuildWaterMaterial(flverMaterial, flverPath)
			: isBlend
				? BuildBlendMaterial(flverMaterial, flverPath)
				: isShaderLit
					? BuildLightmapMaterial(flverMaterial, flverPath,
						shading.LightingType == 1 ? HemDir3 : HemEnv, cullBackfaces)
					: BuildStandardMaterial(flverMaterial, flverPath);

		// CullBackfaces = false (double-sided, e.g. glass panes). The lit family chose a
		// cull_disabled shader above; vfx_scroll takes a uniform; blend and water have no
		// double-sided meshes worth a variant (0 and 1 in the corpus).
		if (!cullBackfaces && mat is StandardMaterial3D std)
			std.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
		else if (!cullBackfaces && mat is ShaderMaterial unlit && (unlit.Shader == _vfxScrollShader || unlit.Shader == _vfxScrollAddShader))
			unlit.SetShaderParameter("cull_back_faces", false);

		cache[key] = mat;
		return mat;
	}

	// Shared by BuildMesh and GetOrBuildMaterial so UV layout and material choice agree. Routes by
	// the MTD's authored shader (ShaderLibrary: `Mul` = two-layer blend, `Lit` = lightmap), which
	// matches the old bracket-tag heuristics on 610 of 612 MTDs (the other two are the magic
	// barrier, a real two-layer material). Falls back to those heuristics if the MTD is unreadable.
	private (bool IsWater, bool IsBlend, bool HasLightmap) ClassifyMaterial(FLVER0.Material mat)
	{
		var shading = ResolveMtdShading(mat);
		bool hasSecondDiffuse = mat.Textures.Any(t => t.ParamName == "g_Diffuse_2");

		if (shading.ShaderFamily.Length == 0)
		{
			// g_Envmap uniquely identifies water materials game-wide - see docs/ARCHITECTURE.md.
			bool fallbackWater = mat.Textures.Any(t => t.ParamName == "g_Envmap");
			return (fallbackWater,
				!fallbackWater && (mat.MTD.Contains("[M]") || mat.MTD.Contains("[ML]")) && hasSecondDiffuse,
				mat.Textures.Any(t => t.ParamName == "g_Lightmap"));
		}

		bool isWater = shading.ShaderFamily == "Water";
		// The authored `Mul` says the engine treats this as two-layer; the slot check stays as a
		// guard, since terrain_blend.gdshader would mix against an unbound (black) second layer if
		// the FLVER didn't actually ship one.
		bool isBlend = !isWater && shading.ShaderFeatures.Contains("Mul") && hasSecondDiffuse;
		bool hasLightmap = shading.ShaderFeatures.Contains("Lit");
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
	// docs/ARCHITECTURE.md's "Texture resolution" section for what each rule covers and why.
	private IEnumerable<string?> CandidateDirs(FLVER0.Material mat, FLVER0.Texture texRef, string flverPath)
	{
		yield return OwnModelDir(flverPath);
		yield return RefPathDir(texRef.Path);
		yield return SiblingMapAreaDir(mat, texRef);
		yield return MapPrefixDir(texRef.Path);
		yield return SiblingObjTextureDir(texRef, flverPath);
	}

	// Last resort: an unrelated obj/ model's own container that happens to carry this exact
	// texture name (a shared prop-family texture never duplicated into its own container or
	// the map-area bucket - see docs/ARCHITECTURE.md's Texture resolution rule 5). Hits cluster
	// by numeric obj ID proximity, so ties break toward the closest ID rather than an arbitrary
	// first match. Index built once per session, lazily, only on first use.
	private Dictionary<string, List<(int Id, string Dir)>>? _objTextureIndex;
	private static readonly Regex ObjIdSegmentPattern = new(@"^o(\d+)$", RegexOptions.IgnoreCase);

	private void EnsureObjTextureIndex()
	{
		if (_objTextureIndex != null) return;
		_objTextureIndex = new Dictionary<string, List<(int, string)>>(StringComparer.OrdinalIgnoreCase);
		string objRoot = System.IO.Path.Combine(_mountedRoot, "obj");
		if (!System.IO.Directory.Exists(objRoot)) return;

		foreach (var dir in System.IO.Directory.GetDirectories(objRoot))
		{
			var idMatch = ObjIdSegmentPattern.Match(System.IO.Path.GetFileName(dir));
			if (!idMatch.Success) continue;
			int id = int.Parse(idMatch.Groups[1].Value);

			string texDir = System.IO.Path.Combine(dir, "tex");
			if (!System.IO.Directory.Exists(texDir)) continue;

			// Routed through GetMergedTextures (not LoadDirTextures directly) so this index
			// build also warms _dirTextureCache - a texture actually resolved via this rule
			// won't need its tpf re-parsed when ResolveTexture reads it moments later.
			foreach (var key in GetMergedTextures(texDir).Keys)
			{
				if (!_objTextureIndex.TryGetValue(key, out var list))
					_objTextureIndex[key] = list = new List<(int, string)>();
				list.Add((id, texDir));
			}
		}
	}

	private string? SiblingObjTextureDir(FLVER0.Texture texRef, string flverPath)
	{
		EnsureObjTextureIndex();
		var key = System.IO.Path.GetFileNameWithoutExtension(texRef.Path.Replace('\\', '/'));
		if (!_objTextureIndex!.TryGetValue(key, out var candidates) || candidates.Count == 0)
			return null;
		if (candidates.Count == 1)
			return candidates[0].Dir;

		int? ownId = null;
		foreach (var seg in flverPath.Replace('\\', '/').Split('/'))
		{
			var m = ObjIdSegmentPattern.Match(seg);
			if (m.Success) { ownId = int.Parse(m.Groups[1].Value); break; }
		}
		if (ownId == null) return candidates[0].Dir;

		var best = candidates[0];
		int bestDist = Math.Abs(best.Id - ownId.Value);
		foreach (var c in candidates)
		{
			int dist = Math.Abs(c.Id - ownId.Value);
			if (dist < bestDist) { bestDist = dist; best = c; }
		}
		return best.Dir;
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
	// stale/copy-pasted reference (see docs/ARCHITECTURE.md, e.g. the Nexus archstones).
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

	// Returns a ShaderMaterial rather than a StandardMaterial3D for the unlit VFX subset - see
	// MtdShading.NeedsUnlitShader.
	private Material BuildStandardMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		var shading = ResolveMtdShading(flverMaterial);
		if (shading.NeedsUnlitShader)
			return BuildUnlitMaterial(flverMaterial, flverPath, shading);
		if (IsSkyDome(flverMaterial, shading))
			return BuildSkyMaterial(flverMaterial, flverPath, shading);

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

		// AlbedoColor multiplies the albedo texture, so it carries the tint.
		mat.Roughness = shading.Roughness;
		mat.AlbedoColor = shading.Tint;

		// Vertex colour multiplies the diffuse, as in the engine; linear, since it is a factor.
		mat.VertexColorUseAsAlbedo = true;

		// Opaque/Water/unrecognised keep StandardMaterial3D's own Disabled default.
		switch (shading.BlendMode)
		{
			case DesBlendMode.AlphaTest:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
				break;
			case DesBlendMode.AlphaBlend:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				break;
			case DesBlendMode.Additive:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
				break;
			case DesBlendMode.Subtractive:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				mat.BlendMode = BaseMaterial3D.BlendModeEnum.Sub;
				break;
		}
		// g_LightingType=0 (blended VFX and sky domes are intercepted above) means no dynamic
		// lighting at all in the source engine. These still lack its fog/scattering/exposure
		// epilogue, which a StandardMaterial3D can't express.
		if (shading.IsUnlit)
			mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;

		return mat;
	}

	// Sky domes (m02's m9999B0, the a03_sky* / m_sky* family): unlit, opaque, "sky" in the MTD name.
	private static bool IsSkyDome(FLVER0.Material mat, MtdShading shading) =>
		shading.IsUnlit && shading.BlendMode == DesBlendMode.Opaque
		&& System.IO.Path.GetFileName(mat.MTD.Replace('\\', '/')).ToLower().Contains("sky");

	// Fog and scattering rows are bound per placement by FlverLoader.ApplyDrawParams.
	private ShaderMaterial BuildSkyMaterial(FLVER0.Material flverMaterial, string flverPath, MtdShading shading)
	{
		var mat = new ShaderMaterial { Shader = _skyShader };
		var tex = ResolveTexture(flverMaterial, "g_Diffuse", flverPath);
		if (tex != null) mat.SetShaderParameter("diffuse", tex);
		mat.SetShaderParameter("diffuse_tint", shading.Tint);
		mat.SetShaderParameter("tex_scroll_0", shading.TexScroll0);
		return mat;
	}

	// MTD g_BlendMode. Water (3) is routed by g_Envmap and never reaches the other builders; 7
	// (two thunder materials) falls through as opaque here.
	private enum DesBlendMode { Opaque = 0, AlphaTest = 1, AlphaBlend = 2, Water = 3, Additive = 4, Subtractive = 5 }

	// The shading data read from one MTD.
	private readonly record struct MtdShading(
		float Roughness, int LightingType, Color Tint, DesBlendMode BlendMode, Vector2 TexScroll0,
		Vector2 TexScroll1, int EnvSpcSlot, string ShaderFamily, string ShaderFeatures, float SpecularPower,
		Color SpecularTint)
	{
		// g_LightingType 0: sky domes, ghost/dissolve, effect-like materials.
		public bool IsUnlit => LightingType == 0;
		// EnvSpcSlot -1: no g_EnvSpcSlotNo (82 MTDs). Empty ShaderFamily: the MTD could not be read,
		// so ClassifyMaterial falls back to texture-slot heuristics.
		public static readonly MtdShading Defaults =
			new(1.0f, 1, Colors.White, DesBlendMode.Opaque, Vector2.Zero, Vector2.Zero, -1, "", "", 8.0f, Colors.White);

		// Unlit materials that need vfx_scroll: a scrolling UV, or alpha/additive blending, whose
		// output stage (fog, scattering, scene-buffer encoding) StandardMaterial3D cannot express.
		public bool NeedsUnlitShader => IsUnlit
			&& (TexScroll0 != Vector2.Zero || BlendMode is DesBlendMode.AlphaBlend or DesBlendMode.Additive);
	}

	// One cached MTD read per material (water reads its own parameters in BuildWaterMaterial).
	// Unreadable MTDs yield MtdShading.Defaults.
	private MtdShading ResolveMtdShading(FLVER0.Material flverMaterial)
	{
		string mtdName = System.IO.Path.GetFileName(flverMaterial.MTD.Replace('\\', '/'));
		if (_shadingCache.TryGetValue(mtdName, out var cached))
			return cached;
		var resolved = ReadMtdShading(mtdName);
		_shadingCache[mtdName] = resolved;
		return resolved;
	}

	private MtdShading ReadMtdShading(string mtdName)
	{
		if (_mtdIndex.TryGetValue(mtdName, out var mtdPath))
		{
			try
			{
				var mtd = MTD.Read(mtdPath);
				// Phong exponent -> GGX roughness approximation: roughness = sqrt(2/(n+2)).
				float specularPower = GetMtdFloat(mtd, "g_SpecularPower", 8.0f);
				float roughness = Mathf.Clamp(Mathf.Sqrt(2.0f / (specularPower + 2.0f)), 0.05f, 1.0f);

				// g_DiffuseMapColorPower multiplies (it is not an exponent); values above 1 brighten
				// and are not clamped.
				var tint = GetMtdColor3(mtd, "g_DiffuseMapColor", Colors.White);
				float power = GetMtdFloat(mtd, "g_DiffuseMapColorPower", 1.0f);
				tint = new Color(
					Mathf.Max(0f, tint.R * power),
					Mathf.Max(0f, tint.G * power),
					Mathf.Max(0f, tint.B * power));

				// 0 unlit, 1 HemDir3, 3 HemEnv.
				int lightingType = GetMtdInt(mtd, "g_LightingType", 1);

				// The MTD's .spx resolves to the shader family and texture features the engine
				// compiled this material for (see ClassifyMaterial).
				var shader = _shaderLibrary.ResolveMaterialShader(mtd.ShaderPath);
				string family = shader == null ? "" : (string)shader["family"];
				string features = shader == null ? "" : (string)shader["features"];

				// g_SpecularMapColor * g_SpecularMapColorPower: the HemEnv env specular weight.
				var specTint = GetMtdColor3(mtd, "g_SpecularMapColor", Colors.White);
				float specPower = GetMtdFloat(mtd, "g_SpecularMapColorPower", 1.0f);
				var specularTint = new Color(
					Mathf.Max(0f, specTint.R * specPower),
					Mathf.Max(0f, specTint.G * specPower),
					Mathf.Max(0f, specTint.B * specPower));

				return new MtdShading(
					roughness, lightingType, tint, (DesBlendMode)GetMtdInt(mtd, "g_BlendMode", 0),
					GetMtdVector2(mtd, "g_TexScroll_0", Vector2.Zero),
					GetMtdVector2(mtd, "g_TexScroll_1", Vector2.Zero),
					GetMtdInt(mtd, "g_EnvSpcSlotNo", -1), family, features, specularPower, specularTint);
			}
			catch (Exception) { /* fall through to defaults below */ }
		}
		return MtdShading.Defaults;
	}

	// hemisphere_ambient.gdshaderinc's LIGHTING_* values.
	private const int HemEnv = 0;
	private const int HemDir3 = 1;

	// Material metadata carrying g_EnvSpcSlotNo to FlverLoader.ApplyDrawParams, which binds that
	// slot's LIGHT_BANK envSpc cubemap per placement.
	internal const string EnvSpcSlotMeta = "env_spc_slot";

	private ShaderMaterial BuildLightmapMaterial(FLVER0.Material flverMaterial, string flverPath, int lightingModel = HemEnv, bool cullBackfaces = true)
	{
		var shading = ResolveMtdShading(flverMaterial);

		Shader shader = cullBackfaces ? _lightmapShader : _lightmapDoubleSidedShader;
		float scissorThreshold = 0.0f;
		switch (shading.BlendMode)
		{
			case DesBlendMode.AlphaTest:
				scissorThreshold = 0.5f; // matches StandardMaterial3D's own AlphaScissor default
				break;
			case DesBlendMode.AlphaBlend:
				shader = cullBackfaces ? _lightmapAlphaShader : _lightmapAlphaDoubleSidedShader;
				break;
			// No lit mesh in the corpus is additive; subtractive is real (139 a04_blood meshes).
			// Neither needs a double-sided variant.
			case DesBlendMode.Additive:
				shader = _lightmapAddShader;
				break;
			case DesBlendMode.Subtractive:
				shader = _lightmapSubShader;
				break;
		}

		var mat = new ShaderMaterial { Shader = shader };
		mat.SetShaderParameter("lighting_model", lightingModel);
		mat.SetShaderParameter("specular_power", shading.SpecularPower);
		mat.SetShaderParameter("specular_tint", shading.SpecularTint);

		bool Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
			return tex != null;
		}

		Assign("g_Diffuse", "diffuse");
		bool hasNormalMap = Assign("g_Bumpmap", "normal_map");
		Assign("g_Specular", "specular");
		Assign("g_Lightmap", "lightmap");
		// Many materials ship no bump map; the shader then uses the vertex normal.
		mat.SetShaderParameter("use_normal_map", hasNormalMap);

		// Only the opaque/alpha-test shaders declare this uniform.
		if (shader == _lightmapShader || shader == _lightmapDoubleSidedShader)
			mat.SetShaderParameter("alpha_scissor_threshold", scissorThreshold);

		mat.SetShaderParameter("diffuse_tint", shading.Tint);
		mat.SetShaderParameter("tex_scroll_0", shading.TexScroll0);
		mat.SetMeta(EnvSpcSlotMeta, shading.EnvSpcSlot);

		return mat;
	}

	// Unlit blended or scrolling materials (see vfx_scroll.gdshader).
	private ShaderMaterial BuildUnlitMaterial(FLVER0.Material flverMaterial, string flverPath, MtdShading shading)
	{
		var mat = new ShaderMaterial
		{
			Shader = shading.BlendMode == DesBlendMode.Additive ? _vfxScrollAddShader : _vfxScrollShader,
		};
		var tex = ResolveTexture(flverMaterial, "g_Diffuse", flverPath);
		if (tex != null) mat.SetShaderParameter("diffuse", tex);
		mat.SetShaderParameter("diffuse_tint", shading.Tint);
		mat.SetShaderParameter("tex_scroll_0", shading.TexScroll0);
		return mat;
	}

	// Two-layer materials; all 926 in the corpus are opaque.
	private ShaderMaterial BuildBlendMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		var mat = new ShaderMaterial { Shader = _blendShader };

		bool Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
			return tex != null;
		}

		Assign("g_Diffuse", "diffuse1");
		Assign("g_Diffuse_2", "diffuse2");
		bool hasNormalMap = Assign("g_Bumpmap", "normal1");
		// The layers' normals are blended before decoding; a missing one samples flat.
		hasNormalMap |= Assign("g_Bumpmap_2", "normal2");
		mat.SetShaderParameter("use_normal_map", hasNormalMap);
		Assign("g_Specular", "specular1");
		Assign("g_Specular_2", "specular2");
		Assign("g_Lightmap", "lightmap"); // optional - shader's lightmap uniform no-ops if unset

		// One tint per material, applied after the blend.
		var shading = ResolveMtdShading(flverMaterial);
		mat.SetShaderParameter("diffuse_tint", shading.Tint);
		mat.SetShaderParameter("specular_tint", shading.SpecularTint);
		mat.SetShaderParameter("tex_scroll_0", shading.TexScroll0);
		mat.SetShaderParameter("tex_scroll_1", shading.TexScroll1);
		mat.SetMeta(EnvSpcSlotMeta, shading.EnvSpcSlot);

		return mat;
	}

	// Water (has g_Envmap). Every parameter maps to a DS_Water_Env constant confirmed in captures;
	// water.gdshader documents each.
	private ShaderMaterial BuildWaterMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		var mat = new ShaderMaterial { Shader = _waterShader };

		void Assign(string paramName, string uniformName)
		{
			var tex = ResolveTexture(flverMaterial, paramName, flverPath);
			if (tex != null) mat.SetShaderParameter(uniformName, tex);
		}
		Assign("g_Bumpmap", "bumpmap");
		// g_Envmap is a cubemap (sampled by a reflection vector in DS_Water_Env), not a 2D image.
		mat.SetShaderParameter("envmap", ResolveWaterEnvCube(flverMaterial, flverPath) ?? FallbackWaterCube);

		// Falls back to the shader's own defaults if the .mtd can't be found/parsed.
		string mtdName = System.IO.Path.GetFileName(flverMaterial.MTD.Replace('\\', '/'));
		if (_mtdIndex.TryGetValue(mtdName, out var mtdPath))
		{
			try
			{
				var mtd = MTD.Read(mtdPath);
				// g_TileScale_i: .x tiling, .y this octave's speed multiplier.
				mat.SetShaderParameter("tile_scale_0", GetMtdVector2(mtd, "g_TileScale_0", new Vector2(1.0f, 0.1f)));
				mat.SetShaderParameter("tile_scale_1", GetMtdVector2(mtd, "g_TileScale_1", new Vector2(1.0f, 0.1f)));
				mat.SetShaderParameter("tile_scale_2", GetMtdVector2(mtd, "g_TileScale_2", new Vector2(1.0f, 0.1f)));
				// g_TexScroll_0 is the flow velocity, used as is.
				mat.SetShaderParameter("flow_dir", GetMtdVector2(mtd, "g_TexScroll_0", new Vector2(0.05f, 0.0f)));
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
				// A Z bias on the summed wave normal, not an xy scale.
				mat.SetShaderParameter("bump_smoose", GetMtdFloat(mtd, "g_BumpMapSmoose", 1.0f));
				// The sun-glint weight c39 = g_SpecularMapColor * g_SpecularMapColorPower exactly.
				mat.SetShaderParameter("glint_color", GetMtdColor3(mtd, "g_SpecularMapColor", Colors.White));
				mat.SetShaderParameter("glint_power", GetMtdFloat(mtd, "g_SpecularMapColorPower", 2.0f));
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

	private static int GetMtdInt(MTD mtd, string name, int fallback)
	{
		var p = mtd.Params.FirstOrDefault(x => x.Name == name);
		return p?.Value is int i ? i : fallback;
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

	// First three components (also used for the Float4 g_WaterColor). Returns Color: a Vector3
	// passed to a source_color uniform is silently ignored.
	private static Color GetMtdColor3(MTD mtd, string name, Color fallback)
	{
		var p = mtd.Params.FirstOrDefault(x => x.Name == name);
		return p?.Value is float[] v && v.Length >= 3 ? new Color(v[0], v[1], v[2]) : fallback;
	}

	private static readonly Dictionary<string, TPF.Texture> _emptyTextures = new();

	// A LIGHT_BANK env cubemap by name from the map's bucket (map/<mXX>/<mXX>_9999.tpf). Null when
	// unresolved, which is normal for rows from default_lightbank.param.
	public Cubemap? ResolveEnvCubemap(string mapPrefix, string cubemapName)
	{
		string key = $"{mapPrefix}/{cubemapName}";
		if (_cubemapCache.TryGetValue(key, out var cached))
			return cached;

		var textures = GetMergedTextures(System.IO.Path.Combine(_mountedRoot, "map", mapPrefix.ToLowerInvariant()));
		Cubemap? cube = null;
		if (textures.TryGetValue(cubemapName, out var tex) && tex.Type == TPF.TexType.Cubemap)
		{
			try { cube = DecodeCubemap(tex); }
			catch (Exception e) { GD.PushWarning($"Cubemap '{cubemapName}' failed to decode: {e.Message}"); }
		}
		_cubemapCache[key] = cube;
		return cube;
	}

	// A water material's g_Envmap, resolved like ResolveTexture but required to be a cubemap.
	private readonly Dictionary<string, Cubemap?> _waterCubeCache = new();

	private Cubemap? ResolveWaterEnvCube(FLVER0.Material flverMaterial, string flverPath)
	{
		var texRef = flverMaterial.Textures.FirstOrDefault(t => t.ParamName == "g_Envmap");
		if (texRef == null || string.IsNullOrEmpty(texRef.Path)) return null;
		string key = System.IO.Path.GetFileNameWithoutExtension(texRef.Path.Replace('\\', '/'));
		if (_waterCubeCache.TryGetValue(key, out var cached)) return cached;

		Cubemap? cube = null;
		foreach (var dir in CandidateDirs(flverMaterial, texRef, flverPath))
		{
			if (dir == null) continue;
			if (GetMergedTextures(dir).TryGetValue(key, out var tpfTex) && tpfTex.Type == TPF.TexType.Cubemap)
			{
				try { cube = DecodeCubemap(tpfTex); }
				catch (Exception e) { GD.PushWarning($"Water cubemap '{key}' failed to decode: {e.Message}"); }
				break;
			}
		}
		_waterCubeCache[key] = cube;
		return cube;
	}

	// Dim neutral cube for an unresolved g_Envmap, so the reflection does not go black.
	private static Cubemap? _fallbackWaterCube;
	private static Cubemap FallbackWaterCube
	{
		get
		{
			if (_fallbackWaterCube != null) return _fallbackWaterCube;
			var faces = new Godot.Collections.Array<Image>();
			for (int i = 0; i < 6; i++)
			{
				var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgb8);
				img.Fill(new Color(0.45f, 0.5f, 0.55f));
				faces.Add(img);
			}
			_fallbackWaterCube = new Cubemap();
			_fallbackWaterCube.CreateFromImages(faces);
			return _fallbackWaterCube;
		}
	}

	// Pfim decodes only face 0 of a cube DDS, so each face (an exact sixth of the payload) is
	// re-wrapped as its own 2D DDS. Uncompressed ARGB faces bypass Pfim: it misreads them as Rgb24
	// and rotates the channels into a rainbow (m02, m04, m06, m08, m99).
	private Cubemap DecodeCubemap(TPF.Texture texture)
	{
		var dds = Headerizer.Headerize(texture, out _);
		int headerLen = DdsHeaderLength(dds);
		int payload = dds.Length - headerLen;
		if (payload <= 0 || payload % 6 != 0)
			throw new NotSupportedException($"payload {payload} is not six equal faces");

		int stride = payload / 6;
		var faces = new Godot.Collections.Array<Image>();
		for (int f = 0; f < 6; f++)
		{
			var faceDds = new byte[headerLen + stride];
			Buffer.BlockCopy(dds, 0, faceDds, 0, headerLen);
			// dwCaps2 (offset 112) carries the CUBEMAP_ALLFACES flags; zeroing it makes this a
			// plain 2D DDS, which is what Pfim can actually read.
			BitConverter.GetBytes(0u).CopyTo(faceDds, DdsCaps2Offset);
			Buffer.BlockCopy(dds, headerLen + f * stride, faceDds, headerLen, stride);
			faces.Add(IsBlockCompressed(dds)
				? DecodeFaceImage(faceDds, texture.Name)
				: DecodeUncompressedFace(dds, headerLen + f * stride, DdsWidth(dds), DdsHeight(dds), texture.Name));
		}

		var cubemap = new Cubemap();
		cubemap.CreateFromImages(faces);
		return cubemap;
	}

	// A DX10 extended header adds 20 bytes after the standard 128, flagged by a "DX10" fourCC in
	// the pixel-format block at offset 84.
	private static int DdsHeaderLength(byte[] dds) =>
		dds.Length > 88 && dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'1' && dds[87] == (byte)'0'
			? 148 : 128;

	private const int DdsCaps2Offset = 112;

	// DDS header: dwHeight at 12, dwWidth at 16; the pixel-format block's dwFlags at 80, whose
	// DDPF_FOURCC bit (0x4) is what distinguishes a block-compressed payload from a raw one.
	private static int DdsHeight(byte[] dds) => (int)BitConverter.ToUInt32(dds, 12);
	private static int DdsWidth(byte[] dds) => (int)BitConverter.ToUInt32(dds, 16);
	private static bool IsBlockCompressed(byte[] dds) => (BitConverter.ToUInt32(dds, 80) & 0x4u) != 0;

	// Big-endian ARGB8888 (the first byte is a constant 0xFF). Base level only; Godot regenerates
	// the mips.
	private static Image DecodeUncompressedFace(byte[] dds, int offset, int width, int height, string name)
	{
		int pixels = width * height;
		if (width <= 0 || height <= 0 || offset + pixels * 4 > dds.Length)
			throw new NotSupportedException($"{name}: uncompressed face {width}x{height} doesn't fit the payload");

		var rgba = new byte[pixels * 4];
		for (int i = 0; i < pixels; i++)
		{
			int src = offset + i * 4;
			rgba[i * 4 + 0] = dds[src + 1];
			rgba[i * 4 + 1] = dds[src + 2];
			rgba[i * 4 + 2] = dds[src + 3];
			rgba[i * 4 + 3] = dds[src + 0];
		}

		var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
		image.GenerateMipmaps();
		return image;
	}

	// Block-compressed faces.
	private static Image DecodeFaceImage(byte[] faceDds, string name)
	{
		using var stream = new System.IO.MemoryStream(faceDds);
		using var pfImage = Pfim.Pfimage.FromStream(stream);
		int sourceStride = pfImage.Format switch
		{
			Pfim.ImageFormat.Rgba32 => 4,
			Pfim.ImageFormat.Rgb24 => 3,
			_ => throw new NotSupportedException($"{name}: unhandled Pfim format {pfImage.Format}"),
		};

		int pixels = pfImage.Width * pfImage.Height;
		var rgba = new byte[pixels * 4];
		for (int i = 0; i < pixels; i++)
		{
			int s = i * sourceStride;
			// Pfim decodes to BGR(A) despite the format names, same as the 2D path.
			rgba[i * 4 + 0] = pfImage.Data[s + 2];
			rgba[i * 4 + 1] = pfImage.Data[s + 1];
			rgba[i * 4 + 2] = pfImage.Data[s + 0];
			rgba[i * 4 + 3] = sourceStride == 4 ? pfImage.Data[s + 3] : (byte)255;
		}
		// No mipmaps: Cubemap.CreateFromImages requires every face to match, and nothing samples
		// these by roughness yet.
		return Image.CreateFromData(pfImage.Width, pfImage.Height, false, Image.Format.Rgba8, rgba);
	}

	// Headerize, Pfim decode, BGRA swap, mips; charges the decoded size against the budget.
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
