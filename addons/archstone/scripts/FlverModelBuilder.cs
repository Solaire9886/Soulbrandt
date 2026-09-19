using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using SoulsFormats;

namespace Archstone;

// FLVER0 parsing/mesh/material/texture-resolution logic. No Godot import-system dependency -
// only ever driven by FlverLoader, single-threaded (see docs/ARCHITECTURE.md's Architecture section).
public partial class FlverModelBuilder : RefCounted
{
	// Keyed by resolved directory; merges every *.tpf found there. See docs/ARCHITECTURE.md's "Texture
	// resolution" section for the CandidateDirs rules that produce the directory keys.
	private readonly Dictionary<string, Dictionary<string, TPF.Texture>> _dirTextureCache = new();

	// Dedupes the actual DXT decode (not just tpf parsing) - keyed by TPF.Texture reference
	// identity since it has no Equals/GetHashCode override.
	private readonly Dictionary<TPF.Texture, ImageTexture> _decodedTextureCache = new();

	// Keyed "<mapPrefix>/<cubemapName>". Holds nulls too, so an unresolvable name isn't retried
	// per placement - a map has only a handful of cubemaps and they're shared across every
	// placement in it. Not counted toward the decoded-texture memory budget: 10-25 cubemaps per
	// map at 32x32/64x64 is well under a megabyte, unlike the 2D texture population.
	private readonly Dictionary<string, Cubemap?> _cubemapCache = new();

	// ponytail: whole-cache clear on budget overrun, not per-entry LRU - see docs/ARCHITECTURE.md.
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

	// Full manual reset for "Reload Loaded Models" - not just the mesh cache FlverLoader owns.
	// A code-only change never needs this (nothing on disk changed), but re-running "Import"
	// mid-session to pull in new/changed mounted/ content (e.g. modded textures) would otherwise
	// stay invisible until the editor restarts, since every cache here is scoped to whatever was
	// on disk the first time each directory/texture was touched this session.
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
	// One variant per blend mode, not one runtime-switched shader: blend_mix/blend_add/blend_sub
	// are compile-time render_mode keywords in Godot, not a per-material property.
	private readonly Shader _lightmapShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap.gdshader");
	private readonly Shader _lightmapAlphaShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_alpha.gdshader");
	private readonly Shader _lightmapAddShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_add.gdshader");
	private readonly Shader _lightmapSubShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_sub.gdshader");
	// cull_disabled siblings for FLVER0.Mesh.CullBackfaces=false meshes (e.g. m2304b0's stained-glass
	// windows in Doran's Mausoleum) - cull mode is a compile-time render_mode keyword here too, same
	// reason the four shaders above are separate files instead of one. Only Opaque/AlphaTest
	// (_lightmapShader) and AlphaBlend (_lightmapAlphaShader) have any double-sided meshes in the
	// corpus (457/23 respectively) - Add/Sub don't need one yet.
	private readonly Shader _lightmapDoubleSidedShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_double_sided.gdshader");
	private readonly Shader _lightmapAlphaDoubleSidedShader = GD.Load<Shader>("res://addons/archstone/shaders/lightmap_alpha_double_sided.gdshader");
	// Unlit materials whose UV scrolls - StandardMaterial3D covers everything else about them
	// but can't animate a UV at all. Same compile-time blend_mix/blend_add split as above.
	private readonly Shader _vfxScrollShader = GD.Load<Shader>("res://addons/archstone/shaders/vfx_scroll.gdshader");
	private readonly Shader _vfxScrollAddShader = GD.Load<Shader>("res://addons/archstone/shaders/vfx_scroll_add.gdshader");
	private readonly Shader _skyShader = GD.Load<Shader>("res://addons/archstone/shaders/sky.gdshader");

	// Index of mounted/mtd/*.mtd by filename - only the water shader needs real .mtd data
	// (per-material wave/reflection tuning has no equivalent in FLVER0's own material data).
	private readonly Dictionary<string, string> _mtdIndex = BuildMtdIndex();

	// DeS's own shader library, used to route materials off authored data instead of heuristics -
	// see ClassifyMaterial and docs/ARCHITECTURE.md's "The shader library" section.
	private readonly ShaderLibrary _shaderLibrary = new();

	// ResolveMtdShading is now on the per-mesh path (ClassifyMaterial calls it), not just the
	// per-material one, so its MTD.Read is cached by mtd filename.
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
			// Indexed per-vertex below via v.BoneIndices[0] - a single mesh can mix vertices
			// rigidly bound to different nodes (see docs/ARCHITECTURE.md's "Rigid mesh-to-node binding" note).
			var rigidTransforms = GetRigidNodeTransforms(flver, flverMesh);

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
				// Rigid mesh-to-node bind applied first, in FLVER space - see docs/ARCHITECTURE.md's
				// "Rigid mesh-to-node binding" note. Identity (a no-op) for meshes that don't use it.
				var rigidTransform = rigidTransforms[v.BoneIndices[0]];
				var pos = System.Numerics.Vector3.Transform(v.Position, rigidTransform);
				// X negated to match Godot's coordinate convention (mirror of FLVER's) - see docs/ARCHITECTURE.md.
				positions[i] = new Vector3(-pos.X, pos.Y, pos.Z);
				// Some FLVER0 meshes' BufferLayout genuinely omits Normal/Color/UV - fall back to
				// a neutral default rather than indexing [0] unconditionally (see docs/ARCHITECTURE.md).
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
			// apparent winding of every triangle - see docs/ARCHITECTURE.md.
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

	// A static (UseBoneWeights=false) mesh's own small BoneIndices palette can list more than
	// one node - a single mesh can mix vertices rigidly bound to different nodes, selected
	// per-vertex via v.BoneIndices[0]. See docs/ARCHITECTURE.md's "Rigid mesh-to-node binding"
	// note. Unused palette slots (-1, or any index for a per-vertex-weighted mesh) resolve to
	// identity - real skeletal skinning is out of scope, not implemented.
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

		// Anything with a real lighting model goes to the shader family, whether or not it has a
		// lightmap. StandardMaterial3D relies on Godot's own lighting and this project has no
		// light nodes, so every material left on it renders black without a WorldEnvironment -
		// which is exactly what the chr/parts/obj population did.
		//
		// g_LightingType=1 (HemDirDifSpcx3) takes the HemDir3 path; =3 (HemEnvDifSpc) takes
		// HemEnv, and a type-3 material with no lightmap is *not* a special case - the engine's
		// gate is min(shadow, lightmap), so a material shipping no lightmap is gated by the
		// shadow term alone, which is ~1 wherever nothing shadows it. The shader's
		// hint_default_white lightmap is the correct stand-in for that with no shadow system,
		// and it lands about 2.3x brighter than an equivalent lightmapped surface, not blown out.
		//
		// Only type 0 (unlit) stays off the lit shader family: ghost/dissolve and additive VFX
		// (StandardMaterial3D / vfx_scroll), plus opaque sky-dome backdrops (sky.gdshader - the
		// output stage minus lighting, routed in BuildStandardMaterial, see IsSkyDome).
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

		// FLVER0's own CullBackfaces=false ("can be seen through from behind", e.g. glass panes,
		// m2304b0's stained-glass windows in Doran's Mausoleum) was being parsed and then never
		// applied anywhere - StandardMaterial3D has a real per-instance cull property, so that half
		// is just a property set. BuildLightmapMaterial handles its own family above (a compile-time
		// render_mode keyword needs a whole sibling shader, not a property - see
		// lightmap_double_sided.gdshader); blend/water still don't (0/1 meshes affected game-wide,
		// not worth a sibling shader yet - see docs/ARCHITECTURE.md's "Known deferred work").
		if (!cullBackfaces && mat is StandardMaterial3D std)
			std.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

		cache[key] = mat;
		return mat;
	}

	// Shared classification so BuildMesh's vertex loop and GetOrBuildMaterial's material
	// selection can't drift out of sync.
	//
	// Routes off the material's *authored* shader assignment where it's available: each .mtd names
	// the .spx the engine compiled it against, and that resolves to a family (Phn/Gst/Water/Ghost)
	// and a texture-feature set (Dif/Spc/Bmp/Mul/Lit) via ShaderLibrary. `Mul` is what this file
	// used to detect as the "[M]"/"[ML]" bracket tag and `Lit` as g_Lightmap presence - the same
	// two facts, read instead of inferred. Checked against the whole corpus before switching:
	// 610 of 612 MTDs classify identically, and both disagreements are the magic barrier, which
	// really is a `ColDifMul` two-layer material that the bracket-tag rule missed because its name
	// carries no tag. (No FLVER map material references it, so this fixes a latent case, not a
	// visible one.)
	//
	// Falls back to the old slot/tag heuristics when the .mtd couldn't be read at all - without
	// that, an unresolvable material would silently lose its lightmap or blend routing.
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

	// Returns a ShaderMaterial rather than a StandardMaterial3D for the unlit-scrolling VFX
	// subset - see MtdShading.NeedsUnlitScrollShader.
	private Material BuildStandardMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		var shading = ResolveMtdShading(flverMaterial);
		if (shading.NeedsUnlitScrollShader)
			return BuildUnlitScrollMaterial(flverMaterial, flverPath, shading);
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

		// StandardMaterial3D's own Roughness/AlbedoColor defaults (1.0 fully matte, white) are
		// what made every chr/parts material look flat regardless of g_Specular - see
		// ResolveMtdShading. AlbedoColor multiplies natively against AlbedoTexture in Godot's
		// own built-in shader, so the tint needs no shader changes here.
		mat.Roughness = shading.Roughness;
		mat.AlbedoColor = shading.Tint;

		// FLVER0 vertex colours were being uploaded to every mesh and then read by nothing but
		// terrain_blend.gdshader (and there only COLOR.a, as its blend weight). In the real
		// engine they're a universal diffuse multiply - see lightmap_common.gdshaderinc. A no-op
		// on the 88% of standard-family meshes whose vertex colours are pure white. Left as
		// linear (VertexColorIsSrgb stays false): the value is a multiplier, not a colour.
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
		// g_LightingType=0 (ghost/dissolve, additive VFX - opaque sky domes are intercepted above
		// by IsSkyDome) means no dynamic lighting at all in the source engine.
		if (shading.IsUnlit)
			mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;

		return mat;
	}

	// Sky-dome backdrops (m02's m9999B0, plus the a03_sky* / m_sky* family): g_LightingType=0 and
	// opaque, with "sky" in the .mtd name. The ghost/dissolve and additive-VFX materials that also
	// carry g_LightingType=0 don't match either clause. DeS runs sky domes through the same fog +
	// exposure epilogue as lit geometry (their DS_Phn_ColDif shader), so they get sky.gdshader +
	// output_stage.gdshaderinc rather than a raw unshaded StandardMaterial3D. See docs/context.md
	// part 45.
	private static bool IsSkyDome(FLVER0.Material mat, MtdShading shading) =>
		shading.IsUnlit && shading.BlendMode == DesBlendMode.Opaque
		&& System.IO.Path.GetFileName(mat.MTD.Replace('\\', '/')).ToLower().Contains("sky");

	// sky.gdshader = the diffuse backdrop through output_stage's des_fog (FOG_BANK distance fade) +
	// des_tonemap (exposure) + des_tone_correct. FogID/ToneMapID/ToneCorrectID land via
	// FlverLoader.ApplyDrawParams (the shader carries `tone_key`, so its wantsOutputStage gate
	// fires). Per-vertex H&P scatter is deliberately left out - see sky.gdshader's header.
	private ShaderMaterial BuildSkyMaterial(FLVER0.Material flverMaterial, string flverPath, MtdShading shading)
	{
		var mat = new ShaderMaterial { Shader = _skyShader };
		var tex = ResolveTexture(flverMaterial, "g_Diffuse", flverPath);
		if (tex != null) mat.SetShaderParameter("diffuse", tex);
		mat.SetShaderParameter("diffuse_tint", shading.Tint);
		mat.SetShaderParameter("tex_scroll_0", shading.TexScroll0);
		return mat;
	}

	// g_BlendMode, a real 0-7 enum on every .mtd, decoded 2026-08-27 by cross-referencing all
	// 612 mounted MTDs against their filename tags - see docs/ARCHITECTURE.md's "Alpha/blend
	// handling" section. Replaces the old
	// "_Edge"/"_Alp"/"_Add" filename heuristic, which disagreed with the engine on 153/584 MTDs
	// and only ever under-detected (nothing tagged transparent is really opaque). Value 3 is
	// water, which never reaches these builders - BuildWaterMaterial is gated on g_Envmap
	// instead. Values 6/7 exist in the paramdef but no mesh in the corpus uses them.
	private enum DesBlendMode { Opaque = 0, AlphaTest = 1, AlphaBlend = 2, Water = 3, Additive = 4, Subtractive = 5 }

	// One .mtd read's worth of shading data. A record struct rather than a tuple because every
	// material family reads a different subset of it and positional tuples got unreadable.
	private readonly record struct MtdShading(
		float Roughness, int LightingType, Color Tint, DesBlendMode BlendMode, Vector2 TexScroll0,
		Vector2 TexScroll1, int EnvSpcSlot, string ShaderFamily, string ShaderFeatures, float SpecularPower,
		Color SpecularTint)
	{
		// 0 = no lighting response at all (sky domes, ghost/dissolve, additive VFX). Kept as a
		// derived property so the many existing call sites reading IsUnlit are unaffected.
		public bool IsUnlit => LightingType == 0;
		// EnvSpcSlot -1 = the material carries no g_EnvSpcSlotNo at all (82 of 612 MTDs), which
		// is distinct from a real slot 0.
		// Empty ShaderFamily/ShaderFeatures mean "the .mtd couldn't be read", which ClassifyMaterial
		// treats as "fall back to the old texture-slot heuristics" rather than "no features".
		public static readonly MtdShading Defaults =
			new(1.0f, 1, Colors.White, DesBlendMode.Opaque, Vector2.Zero, Vector2.Zero, -1, "", "", 8.0f, Colors.White);

		// Whether this material needs the vfx_scroll shader family instead of StandardMaterial3D:
		// an animated UV, which StandardMaterial3D can't express at all. Gated on IsUnlit because
		// the lit scrolling materials (25 meshes - lava, slime, a05water03) would need a full PBR
		// reimplementation in shader form to move off StandardMaterial3D; they keep their static
		// UV for now. See docs/ARCHITECTURE.md's known-deferred-work entry for this item.
		public bool NeedsUnlitScrollShader => IsUnlit && TexScroll0 != Vector2.Zero;
	}

	// Shared by all three material families that read real .mtd data for shading beyond what
	// FLVER0's own material struct exposes (Water has its own dedicated read, see
	// BuildWaterMaterial). One MTD.Read() per material instead of one per property. Falls back
	// to StandardMaterial3D's own defaults (roughness 1.0, tint white/no-op) if unreadable.
	// An Emission-channel glow boost for these same VFX-style materials was tried and reverted
	// - see docs/context.md's "Nexus VFX gaps investigated" entry for why; g_LightingType=0
	// (the ShadingMode.Unshaded branch above) turned out to be the real mechanism instead.
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

				// g_DiffuseMapColorPower is a plain intensity MULTIPLIER, not an exponent - see
				// ARCHITECTURE.md's "g_DiffuseMapColor/g_DiffuseMapColorPower" entry for the
				// Japanese-description evidence and the live-capture confirmation. Not clamped to
				// 1.0 - every consumer uniform is `: source_color` and multiplies against the
				// sampled texture in-shader, so a >1 tint legitimately brightens unsaturated texture
				// data; clamping here would reintroduce the no-op bug this replaces.
				var tint = GetMtdColor3(mtd, "g_DiffuseMapColor", Colors.White);
				float power = GetMtdFloat(mtd, "g_DiffuseMapColorPower", 1.0f);
				tint = new Color(
					Mathf.Max(0f, tint.R * power),
					Mathf.Max(0f, tint.G * power),
					Mathf.Max(0f, tint.B * power));

				// g_LightingType corpus-scanned 2026-08-18 (612 mtds): a clean three-way split,
				// 1=Phong (chr/parts metal/leather), 3=HemEnv (lightmap/blend), 0=every sky dome
				// variant plus the ghost/dissolve and additive-VFX materials already flagged
				// elsewhere in this file/docs/ARCHITECTURE.md - real, not a guess.
				int lightingType = GetMtdInt(mtd, "g_LightingType", 1);

				// g_TexScroll_0/_1 are UV units per second (real values ~+/-0.3), non-zero on
				// ~92 MTDs - water/lava/cloud/light-shaft/sky/slime. Previously read only by
				// BuildWaterMaterial; every other family ignored them and rendered static.
				// g_EnvSpcSlotNo (0-3, on 530 MTDs) picks which of LIGHT_BANK's four envSpc
				// cubemaps this material reflects - see DrawParamReader.GetEnvCubemapNames for
				// the other half of the lookup. The slots track sharpness where it's meaningful
				// (median g_SpecularPower 4.0/7.0/60.0 for slots 1/2/3); slot 0 is the default
				// bucket with a mixed population (median 4.0, mean 26.8), so treat it as unset
				// rather than "roughest". Read-only for now, no consumer.
				// The .mtd names its own .spx, which resolves against the shipped shader library to
				// the family and texture-feature set the engine itself compiled this material for -
				// authored fact where this file previously inferred the same thing from bracket
				// tags and slot presence. See ClassifyMaterial.
				var shader = _shaderLibrary.ResolveMaterialShader(mtd.ShaderPath);
				string family = shader == null ? "" : (string)shader["family"];
				string features = shader == null ? "" : (string)shader["features"];

				// g_SpecularMapColor * g_SpecularMapColorPower - the HemEnv env-cubemap specular
				// term's own material tint/intensity multiplier (Cs in the map-shading research
				// pass's finding #2, MAP_SHADING_ACCURACY_2026_09_15.md - external research
				// archive, not tracked in this repo), distinct from the Phong
				// exponent g_SpecularPower above. BuildWaterMaterial already reads the same two
				// params for its sun glint (glint_color/glint_power); this was the only other
				// HemEnv-family consumer still missing them - env_specular previously had no
				// material-level specular scaling at all.
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

	// Non-blend materials with a real g_Lightmap - StandardMaterial3D has no independent-UV
	// multiply slot, so these use a small shader family instead (one variant per blend mode -
	// see lightmap.gdshader's header for why not one runtime-switched shader).
	// Must match hemisphere_ambient.gdshaderinc's LIGHTING_* constants.
	private const int HemEnv = 0;
	private const int HemDir3 = 1;

	private ShaderMaterial BuildLightmapMaterial(FLVER0.Material flverMaterial, string flverPath, int lightingModel = HemEnv, bool cullBackfaces = true)
	{
		// g_DiffuseMapColor tint only - NOT roughness. Tried and reverted here (see
		// docs/ARCHITECTURE.md's "Known deferred work") - needs real environment/lighting
		// groundwork first, not a quick re-guess.
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
			// No lightmapped mesh in the corpus is additive, but the branch costs nothing and
			// keeps modded//future assets working; subtractive is real (139 meshes, a04_blood).
			// Neither has a double-sided sibling yet - see the field declarations above.
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
		// Many lightmapped materials genuinely ship no bumpmap ([D][L] and friends); the shader
		// falls back to the interpolated vertex normal rather than decoding an absent texture.
		mat.SetShaderParameter("use_normal_map", hasNormalMap);

		// Only the Opaque/AlphaTest shaders have this uniform - the blended variants always do
		// real blending.
		if (shader == _lightmapShader || shader == _lightmapDoubleSidedShader)
			mat.SetShaderParameter("alpha_scissor_threshold", scissorThreshold);

		mat.SetShaderParameter("diffuse_tint", shading.Tint);
		mat.SetShaderParameter("tex_scroll_0", shading.TexScroll0);

		return mat;
	}

	// Unlit VFX materials whose UV animates (skies, clouds, light shafts, vollight, the magic
	// barrier, the Wanderer ghost). Everything else about them is StandardMaterial3D-shaped,
	// but StandardMaterial3D has no animated-UV property at all, so they need a shader.
	private ShaderMaterial BuildUnlitScrollMaterial(FLVER0.Material flverMaterial, string flverPath, MtdShading shading)
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

	// Blend materials are always opaque - corpus-confirmed 2026-08-27, all 926 blend-family
	// meshes carry g_BlendMode=0 (the old comment asserted the same thing from their MTD names
	// never overlapping _Edge/_Alp/_Add, which was the weaker version of the same check).
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
		// Either layer's bumpmap is enough - the two are blended before decoding, and an absent
		// one samples the shader's flat-normal default.
		hasNormalMap |= Assign("g_Bumpmap_2", "normal2");
		mat.SetShaderParameter("use_normal_map", hasNormalMap);
		Assign("g_Specular", "specular1");
		Assign("g_Specular_2", "specular2");
		Assign("g_Lightmap", "lightmap"); // optional - shader's lightmap uniform no-ops if unset

		// Tint only, not roughness - see BuildLightmapMaterial for why. There's only one
		// g_DiffuseMapColor per material (no _2 variant), so the tint applies to the
		// already-blended diffuse1/diffuse2 result, not per-layer.
		var shading = ResolveMtdShading(flverMaterial);
		mat.SetShaderParameter("diffuse_tint", shading.Tint);
		mat.SetShaderParameter("specular_tint", shading.SpecularTint);
		mat.SetShaderParameter("tex_scroll_0", shading.TexScroll0);
		mat.SetShaderParameter("tex_scroll_1", shading.TexScroll1);

		return mat;
	}

	// Water surfaces (g_Envmap-gated). Wave/reflection tuning has no FLVER0 equivalent, so this is
	// the one path that reads the real .mtd directly - and every param maps onto a DS_Water_Env
	// shader constant confirmed against RPCS3 captures (docs/context.md part 50). water.gdshader
	// documents the shader-side meaning of each.
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
				// g_TileScale_i is a Float2: .x = base-UV tiling, .y = this octave's speed multiplier
				// on flow_dir. Fed straight through - the old `wave_detail_scale` fudge is gone.
				mat.SetShaderParameter("tile_scale_0", GetMtdVector2(mtd, "g_TileScale_0", new Vector2(1.0f, 0.1f)));
				mat.SetShaderParameter("tile_scale_1", GetMtdVector2(mtd, "g_TileScale_1", new Vector2(1.0f, 0.1f)));
				mat.SetShaderParameter("tile_scale_2", GetMtdVector2(mtd, "g_TileScale_2", new Vector2(1.0f, 0.1f)));
				// Only g_TexScroll_0 is ever set; it's the shared flow VELOCITY - the shader uses it
				// raw (its magnitude is the base scroll speed), scaled per-octave by tile_scale_i.y.
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
				// g_BumpMapSmoose (c[9].x) - a bias added to the summed wave normal's Z: negative
				// => choppier, positive => flatter. Not an xy strength scale.
				mat.SetShaderParameter("bump_smoose", GetMtdFloat(mtd, "g_BumpMapSmoose", 1.0f));
				// Sun-glint weight (c[39]) - previously a fixed x2 fudge. Cross-checked against
				// three captures (docs/context.md part 50 follow-up): c[39] == g_SpecularMapColor *
				// g_SpecularMapColorPower, times the scene's own sun colour (already applied
				// separately in water.gdshader via scatter_sun_color) - so only the material's own
				// two factors are bound here.
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
	// LIGHT_BANK's per-situation environment cubemaps, resolved by name (see
	// DrawParamReader.GetEnvCubemapNames) out of the map's own bucket - map/<mXX>/<mXX>_9999.tpf,
	// picked up by the same GetMergedTextures cache every other map texture already goes through.
	// Returns null when the name doesn't resolve, which is a normal outcome for a placement whose
	// LightID fell back to default_lightbank.param (that row's IDs are in the default bank's
	// namespace, not this map's).
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

	// A water material's g_Envmap texture, decoded as a cubemap - same resolution walk as
	// ResolveTexture (OwnModelDir -> RefPathDir -> sibling map area -> ...), but the TPF entry must
	// be a real TexType.Cubemap. Returns null if the reference or the cube can't be resolved;
	// BuildWaterMaterial then binds FallbackWaterCube so the screen-space reflection still shows.
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

	// Dim neutral cube for water whose g_Envmap didn't resolve - keeps the reflection term from
	// collapsing to black (which would read darker than the old flat-image matcap did).
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

	// Pfim has no concept of a cubemap - handed a 6-face DDS it silently decodes face 0 only
	// (confirmed: a 64x64 m01 cubemap comes back as 21952 bytes, exactly one face plus its mip
	// chain). So each face is re-wrapped as its own single-image DDS and decoded separately,
	// which reuses Pfim's DXT decoding rather than reimplementing it. The face payload is always
	// an exact sixth of the headerized blob - verified on both real layouts in this game: m01's
	// are block-compressed with a full mip chain per face (2744B stride), m02's are uncompressed
	// 32x32 ARGB (4096B stride).
	//
	// **Uncompressed faces bypass Pfim entirely**, and must: handed one, Pfim reports it as
	// `Rgb24` and reads 3 bytes per pixel out of 4-byte ARGB data, so every pixel is misaligned
	// and the channels rotate on a four-pixel cycle - a literal RGB rainbow. It decodes without
	// error, which is why an earlier revision recorded `Rgb24` as a legitimate second format
	// instead of recognising the misread. It affected every uncompressed-cubemap map (m02, m04,
	// m06, m08, m99) and none of the block-compressed ones (m01, m03, m05, m07). There is nothing
	// to decompress in these anyway.
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

	// Raw ARGB8888, big-endian as the PS3 stores it, so the per-pixel byte order is A,R,G,B -
	// confirmed against real data, where every pixel's first byte is a constant 0xFF (opaque
	// alpha) while the other three vary together (these environment maps are near-neutral grey).
	// Reading it as B,G,R,A instead would make every pixel maximally blue.
	//
	// Only the base level is read; any mip chain in the face's remaining bytes is ignored and
	// Godot regenerates it. Real strides confirm that layout: 4096 = 32x32x4 with no mips, and
	// 5460 / 21844 are 32x32 and 64x64 with a full chain.
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

	// Block-compressed faces only - see DecodeUncompressedFace for why the raw ones can't go
	// through Pfim. Rgb24 is still accepted here because a genuinely 24-bit compressed format
	// would be a real case, but no cubemap in this game's data reaches it.
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
