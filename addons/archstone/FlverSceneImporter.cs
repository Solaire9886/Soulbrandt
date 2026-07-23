using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using SoulsFormats;

namespace Archstone;

// Native scene importer for Demon's Souls .flver files. Reads FLVER0 mesh/material data
// and the sibling .tpf texture archive directly into Godot ImporterMesh/StandardMaterial3D/
// ImageTexture objects, bypassing the OBJ/MTL/DDS relay in desflver_test entirely.
public partial class FlverSceneImporter : EditorSceneFormatImporter
{
	// Every texture is identified purely by its base filename (case-insensitive), so every
	// resolution rule in ResolveTexture below ultimately reduces to the same question: does
	// this directory's merged *.tpf contents have a texture with this name. One cache for all
	// of them, keyed by the resolved absolute directory. ConcurrentDictionary, not Dictionary:
	// Godot's bulk reimport dispatches _ImportScene calls across multiple worker threads
	// against this same importer instance, so this cache is genuinely shared/mutated
	// concurrently - a plain Dictionary here silently loses entries under contention, which
	// read as random per-piece missing textures.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, TPF.Texture>> _dirTextureCache = new();

	// DecodeTexture (Headerize -> Pfim DXT decompress -> BGRA/RGBA swap -> GenerateMipmaps) is
	// real per-call CPU work, not just an engine-call/allocation cost like the vertex arrays
	// above - and unlike _dirTextureCache (which only dedupes reading/parsing a *.tpf), nothing
	// was deduping the decode itself. Shared textures are the norm here, not the edge case (a
	// map-area bucket's ~19 tpfs are reused by every piece in that area, cross-category reuse
	// like m4020b0 pulling c2000's own texture set, etc.), so the same DDS bytes were getting
	// fully redecoded on every referencing material. Keyed by TPF.Texture reference identity,
	// not name: SoulsFormatsNEXT's TPF.Texture has no Equals/GetHashCode override, so default
	// reference equality applies, and GetMergedTextures already returns the same cached
	// dictionary (and therefore the same TPF.Texture instances) for a given directory across
	// calls - so the same texture reference reliably comes back on a repeat resolution.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<TPF.Texture, ImageTexture> _decodedTextureCache = new();

	private readonly string _mountedRoot = ProjectSettings.GlobalizePath("res://mounted");

	// Loaded once per importer instance (mirrors _dirTextureCache's one-time-init-is-
	// thread-safe pattern) rather than lazily, since the bulk reimport calls _ImportScene
	// across multiple threads against this same instance.
	private readonly Shader _blendShader = GD.Load<Shader>("res://addons/archstone/terrain_blend.gdshader");
	private readonly Shader _waterShader = GD.Load<Shader>("res://addons/archstone/water.gdshader");
	// Three variants rather than one runtime-switched shader: Godot's blend_mix/blend_add are
	// compile-time render_mode keywords, not per-material properties like
	// StandardMaterial3D.BlendMode was - see lightmap.gdshader's header comment.
	private readonly Shader _lightmapShader = GD.Load<Shader>("res://addons/archstone/lightmap.gdshader");
	private readonly Shader _lightmapAlphaShader = GD.Load<Shader>("res://addons/archstone/lightmap_alpha.gdshader");
	private readonly Shader _lightmapAddShader = GD.Load<Shader>("res://addons/archstone/lightmap_add.gdshader");

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

		var materialCache = new Dictionary<int, Material>();
		var importerMesh = new ImporterMesh();
		bool anySurface = false;

		foreach (var flverMesh in flver.Meshes)
		{
			var material = GetOrBuildMaterial(flverMesh.MaterialIndex, flver, materialCache, flverPath);
			var (_, isBlend, hasLightmap) = ClassifyMaterial(flver.Materials[flverMesh.MaterialIndex]);

			// FLVER's coordinate convention is the mirror image of Godot's: confirmed via bind-pose
			// FK on c0000/c9990's skeleton (L_Hand/R_Shield/etc. bones land on the opposite X side
			// from where their own baked mesh geometry sits) and independently via m03_01_00_00
			// rendering as a literal mirror image of m03_00_00_00. Negate X on position and normal
			// to undo it.
			// Blend materials always use UV1 for their second diffuse/normal/specular layer;
			// non-blend materials with a real g_Lightmap use UV1 for the lightmap itself instead
			// - confirmed via a corpus-wide survey (11646 materials, 7394 with a lightmap) that
			// every lightmapped mesh has exactly one more UV channel than its non-lightmapped
			// counterpart would, zero exceptions. Either way that's "this mesh needs a second UV
			// channel." water.gdshader is also a ShaderMaterial but, like the plain single-layer
			// path, only ever needs one UV channel - so this is recomputed from the FLVER
			// material directly (ClassifyMaterial) rather than inferred from the built Material/
			// Shader instance, which can't distinguish "blend without lightmap" from "blend with."
			bool needsUV2 = isBlend || hasLightmap;
			// Blend+lightmap meshes (865 of 912 real blend materials) have a genuine third UV
			// channel - FLVER's UV2 - with nowhere else to go, since both of Godot's native UV
			// slots are already spoken for by the two diffuse layers. Packed into
			// Mesh.ArrayType.Custom0 below (raw floats, read back in terrain_blend.gdshader via
			// CUSTOM0.rg) - this is the Custom0 plan CLAUDE.md's "Known deferred work" described
			// before lightmap support existed at all.
			bool needsLightmapCustom0 = isBlend && hasLightmap;

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
			var custom0s = needsLightmapCustom0 ? new float[vertCount * 2] : null;
			for (int i = 0; i < vertCount; i++)
			{
				var v = vertices[i];
				positions[i] = new Vector3(-v.Position.X, v.Position.Y, v.Position.Z);
				// Normals/Colors/UVs are each populated per-vertex only if the mesh's own
				// FLVER0 BufferLayout actually has a member for that semantic - confirmed via
				// SoulsFormatsNEXT's Vertex.Read: some meshes' layout genuinely omits Normal
				// and/or UV entirely (m9999b0/m9900 across several map areas, o9996 in obj -
				// all suspicious "reserved/debug ID"-looking names, plausibly collision-only or
				// tool geometry never meant to render). Indexing [0] unconditionally is what
				// threw ArgumentOutOfRangeException here before; a missing channel now falls
				// back to a neutral default instead of taking the whole mesh's import down.
				normals[i] = v.Normals.Count > 0
					? new Vector3(-v.Normals[0].X, v.Normals[0].Y, v.Normals[0].Z)
					: Vector3.Up;
				// Vertex color is FLVER's blend-weight channel (SoulsFormats' own doc comment:
				// "data used for blending, alpha, etc.") - harmless to set unconditionally since
				// StandardMaterial3D ignores vertex color unless VertexColorUseAsAlbedo is on.
				colors[i] = v.Colors.Count > 0
					? new Color(v.Colors[0].R, v.Colors[0].G, v.Colors[0].B, v.Colors[0].A)
					: Colors.White;
				// No V-flip here (unlike Program.cs's OBJ output): that flip compensates for
				// Wavefront OBJ's V convention, but Image.CreateFromData uses the same top-down
				// row order as the raw decoded texture, so FLVER's raw V is already correct.
				uvs[i] = v.UVs.Count > 0 ? new Vector2(v.UVs[0].X, v.UVs[0].Y) : Vector2.Zero;
				if (needsUV2)
					uv2s[i] = v.UVs.Count > 1 ? new Vector2(v.UVs[1].X, v.UVs[1].Y) : Vector2.Zero;
				// Left at the C# array default (0,0) if this particular vertex's layout somehow
				// lacks a third UV channel - same "missing channel -> neutral default" fallback
				// used for normals/colors/UVs above, though the corpus survey found zero real
				// instances of this for blend+lightmap meshes.
				if (needsLightmapCustom0 && v.UVs.Count > 2)
				{
					custom0s[i * 2] = v.UVs[2].X;
					custom0s[i * 2 + 1] = v.UVs[2].Y;
				}
			}

			// Winding must be swapped here even though it wasn't for the OBJ path: mirroring the
			// X axis above flips the apparent winding of every triangle, turning what used to
			// coincidentally match Godot's clockwise-front convention into the wrong direction.
			// doCheckFlip only applies to triangle-strip meshes at a strip-restart marker, where
			// it averages the three vertices' own Normals against the computed face normal to
			// decide whether to flip - meaningless (and, confirmed on o9996.flver, an
			// ArgumentOutOfRangeException inside SoulsFormats.FLVER.Vertex.get_Normal at
			// Mesh.Triangulate's Normal access) on a mesh whose BufferLayout has no Normal
			// member at all. Only request it when this mesh's own vertices actually carry one.
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

			// GenerateTangents() still needs a SurfaceTool - CreateFromArrays() just loads the
			// prebuilt arrays into it instead of the array being built via per-vertex calls.
			var st = new SurfaceTool();
			st.CreateFromArrays(arrays);
			st.GenerateTangents();
			// Custom0's component count (2 floats/vertex here, matching custom0s' packing above)
			// isn't inferable the way Vertex/Normal/UV are, since ArrayCustomFormat also covers
			// byte-packed layouts of the same nominal size - has to be spelled out explicitly or
			// the lightmap UV silently fails to read back as CUSTOM0 in the shader.
			ulong customArrayFormat = needsLightmapCustom0
				? (ulong)Mesh.ArrayCustomFormat.RgFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift
				: 0;
			// The scene-import pipeline (LOD/shadow mesh generation, mesh compression) only
			// operates on the import-time ImporterMesh/ImporterMeshInstance3D representation -
			// returning a runtime ArrayMesh/MeshInstance3D here gets silently dropped on save.
			importerMesh.AddSurface(Mesh.PrimitiveType.Triangles, st.CommitToArrays(), material: material, flags: customArrayFormat);
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

	// Shared by GetOrBuildMaterial (to pick a material/shader family) and _ImportScene's
	// per-mesh vertex loop (to know which extra UV/custom channels this mesh needs) - both
	// need the exact same three-way classification, and computing it twice via ad hoc inline
	// checks risked the two call sites silently drifting out of sync.
	private static (bool IsWater, bool IsBlend, bool HasLightmap) ClassifyMaterial(FLVER0.Material mat)
	{
		// Water surfaces (DS_Water_Env MTD family: reflective/refractive, no diffuse texture
		// at all, just g_Bumpmap + g_Envmap). Gated on g_Envmap's presence - confirmed unique
		// to this family across all 8889 materials in the game (58/58 instances), so unlike
		// the blend gate below this needs no filename heuristic at all.
		bool isWater = mat.Textures.Any(t => t.ParamName == "g_Envmap");

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
			&& (mat.MTD.Contains("[M]") || mat.MTD.Contains("[ML]"))
			&& mat.Textures.Any(t => t.ParamName == "g_Diffuse_2");

		// A real baked-lighting texture, present on 7394/11646 mounted materials (63%).
		// StandardMaterial3D has no independent-UV multiply slot for it, so non-blend
		// materials with one route to BuildLightmapMaterial's shader family instead of
		// BuildStandardMaterial; blend materials keep using BuildBlendMaterial, which now also
		// samples this when present (see terrain_blend.gdshader). Never true alongside
		// isWater - confirmed 0/58 water materials have a lightmap, via corpus survey.
		bool hasLightmap = mat.Textures.Any(t => t.ParamName == "g_Lightmap");

		return (isWater, isBlend, hasLightmap);
	}

	// A texture slot's type string ("g_Diffuse" etc) is a separate optional field in FLVER0's
	// raw format (SoulsFormats leaves ParamName null when the underlying typeOffset is 0),
	// and some materials never got one written at all - confirmed on 44 real mounted files
	// across every category, not just obscure debug content: it also affects the soul-form
	// "Ghost"/Wanderer phantom template (c9983, c9981) that dresses invaders/phantoms in
	// their equipped gear. Every other texture-identification signal in this file (isWater,
	// isBlend, ResolveTexture's own paramName match) depends on ParamName being set, so this
	// has to run first, before any of that. There's no name to match against, but the MTD's
	// own bracket tag (e.g. "[DifSpcBmp_Skin]") reliably lists which texture slots the
	// material has and in what order - confirmed positionally 100% (83/83 real affected
	// materials, spanning map/chr/obj/parts) against the same Dif/Spc/Bmp/Lit convention this
	// file already trusts elsewhere for blend/transparency gating. Mutates the parsed Texture
	// objects in place (ParamName has a public setter) so everything downstream keeps working
	// unchanged.
	private static readonly (string Token, string ParamName)[] TextureSlotTokens =
	{
		("Dif", "g_Diffuse"), ("Spc", "g_Specular"), ("Bmp", "g_Bumpmap"), ("Lit", "g_Lightmap"),
		("Dcl", "g_Diffuse"), // sky/decal materials (DS_map_sky[Dcl].mtd) - a single backdrop texture
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

	// Every rule below answers the same question - "which directory might have a *.tpf
	// containing this texture name" - tried in order of how much we trust it, cheapest/most
	// trustworthy first. Confirmed via real data, not guessed:
	//  1. the model's own real shipped container (flat co-located tpf for chr, sibling tex/
	//     folder for obj/parts) - always correct when present, since it's exactly what got
	//     extracted for this specific model.
	//  2. the directory the texture's own reference path names - correct for map pieces and
	//     genuine cross-category reuse (e.g. a map corpse-pile FLVER pulling a chr texture
	//     set), but the reference path's category segment is dev-tool metadata, not something
	//     the game actually re-validates at runtime, so it's sometimes stale or just wrong.
	//  3. another texture slot on the *same material* that does correctly point into a map
	//     area - confirmed on Demon's Souls' six Nexus archstones (o1000-o1050): all six
	//     share one map-area (m01) body texture but only their g_Lightmap ref is honestly
	//     labeled, the g_Diffuse/Specular/Bumpmap refs are all copy-pasted from the o1000
	//     template and never updated.
	//  4. a map-area prefix baked into the texture's own filename (e.g. "m03_wall_oi2"),
	//     tried even when every ref on the material lies about its own category - confirmed
	//     on o3120/o3129 (Boletaria wall pieces), where even the lightmap ref is mislabeled
	//     "obj/o3120" but every texture name still honestly starts "m03_".
	// Together these resolve ~90% of the obj/parts texture refs that don't already succeed
	// via rule 1 or 2 (measured against every mounted obj/parts material).
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
				return _decodedTextureCache.GetOrAdd(tpfTex, DecodeTexture);

		return null;
	}

	// The model's own real container: a flat co-located tpf for chr (mounted/chr/c2000/ holds
	// both c2000.flver and c2000.tpf, so the flver's own directory already has everything),
	// or the sibling "tex" folder for obj/parts, which instead split the flver into its own
	// "sib" folder with "tex" as a sibling (mounted/obj/o0211/sib/o0211.flver +
	// mounted/obj/o0211/tex/o0211.tpf) - confirmed via a directory scan not an edge case,
	// every one of the 777 mounted obj/ and 309 mounted parts/ models uses this split.
	private static string OwnModelDir(string flverPath)
	{
		string dir = System.IO.Path.GetDirectoryName(flverPath)!;
		return string.Equals(System.IO.Path.GetFileName(dir), "sib", StringComparison.OrdinalIgnoreCase)
			? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir)!, "tex")
			: dir;
	}

	// Resolves a reference like "Model/chr/c2000/tex/c2000_body.tga" straight to that
	// model's own texture set. The real on-disk container layout is not uniform across
	// categories - confirmed via real extraction (AssetExtractor.cs), not guessed: chr's
	// chrbnd entries flatten the tpf straight into the model folder (mounted/chr/c2000/
	// c2000.tpf, no "tex" folder at all), while obj/parts' containers keep a real "tex"
	// subfolder matching the reference path literally (mounted/parts/Weapon/WP_A_1503/tex/
	// WP_A_1503.tpf). Try the nested-tex layout first since it matches the reference path
	// as-is, then fall back to the flattened one.
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

	// Any other texture slot on this same material whose own reference path names a map
	// area - see CandidateDirs' doc comment for why (Nexus archstones' shared body texture).
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

	// Map textures are named like "m03_01_Wall_Cliff_00" - a leading map-area prefix baked
	// into the filename itself, independent of whatever the reference path's category claims
	// (see CandidateDirs' doc comment for the o3120/o3129 case this covers).
	private static readonly Regex MapPrefixPattern = new(@"^(m\d\d)_", RegexOptions.IgnoreCase);

	private string? MapPrefixDir(string textureRefPath)
	{
		var key = System.IO.Path.GetFileNameWithoutExtension(textureRefPath.Replace('\\', '/'));
		var m = MapPrefixPattern.Match(key);
		return m.Success ? System.IO.Path.Combine(_mountedRoot, "map", m.Groups[1].Value.ToLowerInvariant()) : null;
	}

	private Dictionary<string, TPF.Texture> GetMergedTextures(string? dir) =>
		dir == null ? _emptyTextures : _dirTextureCache.GetOrAdd(dir, LoadDirTextures);

	// Merges every *.tpf found in a directory rather than opening one guessed-basename file:
	// a folder can hold more than one real tpf (e.g. WP_A_1503.tpf and a separate
	// WP_A_1503_L.tpf variant sitting side by side, confirmed on real weapon data - the
	// entry a reference actually needs isn't always in the tpf whose name matches the
	// containing folder), and map-area buckets are always many numbered tpfs by design.
	// Per-file try/catch: confirmed 3 real zero-byte .tpf across the mounted corpus (o3104,
	// o0050, o7999 - a genuine extraction artifact, not a code bug), and this is the first
	// code path that ever actually opens an obj/parts model's own tpf, so it's the first to
	// hit them. Reading files pulled from the user's own game dump is a real trust boundary -
	// one bad file must not abort an unrelated model's entire import.
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

	// Single-layer materials with a real g_Lightmap texture (the ~6529-material majority of
	// hasLightmap materials, i.e. every one that isn't also a blend material - see
	// ClassifyMaterial). StandardMaterial3D has no independent-UV multiply slot for it, so
	// these route to a small custom shader family instead (see lightmap.gdshader's header for
	// why there are three files, not one). Same "_Edge"/"_Alp"/"_Add" MTD convention as
	// BuildStandardMaterial still applies and is unrelated to the lightmap itself - it just
	// has to be expressed as shader/render_mode selection here instead of the
	// Transparency/BlendMode properties BuildStandardMaterial sets directly.
	private ShaderMaterial BuildLightmapMaterial(FLVER0.Material flverMaterial, string flverPath)
	{
		Shader shader = _lightmapShader;
		float scissorThreshold = 0.0f;
		if (flverMaterial.MTD.Contains("_Edge"))
			scissorThreshold = 0.5f; // matches StandardMaterial3D's own AlphaScissor default.
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

		// alpha_scissor_threshold only exists on lightmap.gdshader (see its header) - the
		// _Alp/_Add variants always do real ALPHA blending, no scissor uniform to set.
		if (shader == _lightmapShader)
			mat.SetShaderParameter("alpha_scissor_threshold", scissorThreshold);

		return mat;
	}

	// Blend materials' own MTD names (e.g. "M_4Stone[DSB][ML].mtd") never overlap with the
	// _Edge/_Alp/_Add tags above - confirmed always opaque, so no transparency handling here.
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
		// Only 865 of 912 real blend materials have one - the other 47 leave this unset,
		// which is safe: terrain_blend.gdshader's lightmap uniform defaults to white/no-op.
		Assign("g_Lightmap", "lightmap");

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
