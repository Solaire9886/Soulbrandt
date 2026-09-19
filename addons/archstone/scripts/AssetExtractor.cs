using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SoulsFormats;

namespace Archstone;

// Walks a raw Demon's Souls extraction root and unpacks its .bnd/.dcx containers directly via
// SoulsFormatsNEXT, writing loose files into res://mounted's layout. No editor-only API - called
// identically from archstone.gd and the headless extract_cli.gd script.
public partial class AssetExtractor : RefCounted
{
	// The only categories the importer actually reads - see docs/ARCHITECTURE.md.
	// ponytail: flat allowlist, extend when animation/collision import needs a new category.
	// param/paramdef added for LIGHT_BANK/FOG_BANK (see DrawParamReader.cs) - "param" also pulls
	// the much larger unrelated gameparam/ folder (item/npc data, unused today) since extraction
	// is whole-top-level-folder granularity; ~8MB total, not worth a sub-folder filter for that.
	// "shader" added 2026-08-28 for the material shader library - see ShaderLibrary.cs and
	// docs/ARCHITECTURE.md's "The shader library" section. Its entry names don't carry the
	// data/DVDROOT prefix every other container uses, which ResolveEntryOutputPath handles.
	// "sfx" added 2026-09-06 for particle-effect definitions - see SfxLoader.cs and
	// docs/ARCHITECTURE.md's SFX entry. Its entry names carry "Sfx" itself (not a generic
	// DVDROOT placeholder) right after "data", so it needs the same container-qualified
	// fallback path shader's entries do - see ResolveEntryOutputPath.
	public static readonly string[] KnownCategories = { "chr", "map", "obj", "parts", "mtd", "param", "paramdef", "shader", "sfx" };

	// Instance wrapper so GDScript can read this without a second copy in archstone.gd.
	public string[] GetKnownCategories() => KnownCategories;

	// categories: null/empty means "all of KnownCategories". Runs on a background thread;
	// onProgress/onComplete are marshaled back via CallDeferred.
	public void ExtractAsync(string rawRoot, string outputRoot, string[] categories, Callable onProgress, Callable onComplete)
	{
		var selected = (categories == null || categories.Length == 0)
			? KnownCategories
			: KnownCategories.Where(k => categories.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();

		Task.Run(() => Run(rawRoot, outputRoot, selected, onProgress, onComplete));
	}

	private void Run(string rawRoot, string outputRoot, string[] categories, Callable onProgress, Callable onComplete)
	{
		// A pre-existing symlink (old mounted stopgap) would otherwise have writes follow it.
		if (Directory.Exists(outputRoot) && File.GetAttributes(outputRoot).HasFlag(FileAttributes.ReparsePoint))
			Directory.Delete(outputRoot);
		Directory.CreateDirectory(outputRoot);

		var sourceFiles = new List<string>();
		foreach (var category in categories)
		{
			var categoryDir = Path.Combine(rawRoot, category);
			if (Directory.Exists(categoryDir))
				sourceFiles.AddRange(Directory.EnumerateFiles(categoryDir, "*", SearchOption.AllDirectories));
		}

		int extracted = 0, skipped = 0;
		var errors = new List<string>();

		for (int i = 0; i < sourceFiles.Count; i++)
		{
			var sourcePath = sourceFiles[i];
			try
			{
				ProcessFile(sourcePath, rawRoot, outputRoot, ref extracted, ref skipped);
			}
			catch (Exception e)
			{
				errors.Add($"{sourcePath}: {e.Message}");
			}
			onProgress.CallDeferred(i + 1, sourceFiles.Count, sourcePath);
		}

		onComplete.CallDeferred(extracted, skipped, string.Join("\n", errors));
	}

	private void ProcessFile(string sourcePath, string rawRoot, string outputRoot, ref int extracted, ref int skipped)
	{
		byte[] raw = File.ReadAllBytes(sourcePath);
		byte[] inner = DCX.Is(raw) ? DCX.Decompress(raw) : raw;

		if (BND3.IsRead(inner, out var bnd3))
		{
			foreach (var entry in bnd3.Files)
				WriteEntry(entry.Name, entry.Bytes, sourcePath, rawRoot, outputRoot, ref extracted, ref skipped);
		}
		else if (BND4.IsRead(inner, out var bnd4))
		{
			// Not yet confirmed against a real DeS container (only BND3 observed so far) -
			// included since SoulsFormatsNEXT already ships it at zero extra cost.
			foreach (var entry in bnd4.Files)
				WriteEntry(entry.Name, entry.Bytes, sourcePath, rawRoot, outputRoot, ref extracted, ref skipped);
		}
		else if (!ReferenceEquals(raw, inner))
		{
			// Bare DCX-compressed single asset, no binder wrapper (e.g. a map piece's .flver.dcx).
			string relative = Path.GetRelativePath(rawRoot, sourcePath);
			if (relative.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
				relative = relative[..^4];
			WriteIfStale(sourcePath, Path.Combine(outputRoot, relative), inner, ref extracted, ref skipped);
		}
		else
		{
			// Already a plain loose file - copy through as-is.
			string relative = Path.GetRelativePath(rawRoot, sourcePath);
			WriteIfStale(sourcePath, Path.Combine(outputRoot, relative), raw, ref extracted, ref skipped);
		}
	}

	private void WriteEntry(string entryName, byte[] bytes, string sourcePath, string rawRoot, string outputRoot, ref int extracted, ref int skipped)
	{
		string relative = ResolveEntryOutputPath(entryName) ?? FallbackEntryOutputPath(entryName, sourcePath, rawRoot);
		if (relative == null) return;
		WriteIfStale(sourcePath, Path.Combine(outputRoot, relative), bytes, ref extracted, ref skipped);
	}

	// For containers whose entry names aren't data/DVDROOT paths - shader/*.shaderbnd names its
	// entries by their original build path (N:\DemonsSoul\Source\Shader\DS_Flver\Debug\...),
	// which carries no on-disk location at all. Mirrors the container's own place in the raw tree
	// and gives it a folder named after itself, so the 1349 ds_flver entries land in
	// shader/ds_flver/ and can't collide with ds_filter's identically-shaped names.
	//
	// Before this existed, ResolveEntryOutputPath returning null meant such entries were dropped
	// silently - the reason adding "shader" to KnownCategories alone would have looked like it
	// worked and produced nothing.
	private static string FallbackEntryOutputPath(string entryName, string sourcePath, string rawRoot)
	{
		string fileName = Path.GetFileName(entryName.Replace('\\', '/'));
		if (string.IsNullOrEmpty(fileName)) return null;
		string containerDir = Path.GetDirectoryName(Path.GetRelativePath(rawRoot, sourcePath)) ?? "";
		// Strip .dcx first: every container ships as both a plain and a .dcx copy, and both
		// decompress to the same binder - without this they'd land in two folders.
		string containerFile = Path.GetFileName(sourcePath);
		if (containerFile.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
			containerFile = containerFile[..^4];
		return Path.Combine(containerDir, Path.GetFileNameWithoutExtension(containerFile), fileName);
	}

	private void WriteIfStale(string sourcePath, string destPath, byte[] bytes, ref int extracted, ref int skipped)
	{
		if (File.Exists(destPath) && File.GetLastWriteTimeUtc(destPath) >= File.GetLastWriteTimeUtc(sourcePath))
		{
			skipped++;
			return;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
		File.WriteAllBytes(destPath, bytes);
		extracted++;
	}

	// Real BND entry names are full Windows paths (e.g. "N:\...\data\DVDROOT\chr\c2000\c2000.flver")
	// - dropping the segment right after "data" (always "DVDROOT") and joining the rest lands
	// every entry at its exact existing on-disk path.
	// Returns null when the entry name has no data/DVDROOT prefix - see FallbackEntryOutputPath.
	private static string ResolveEntryOutputPath(string entryName)
	{
		var segs = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		int dataIdx = Array.FindIndex(segs, s => s.Equals("data", StringComparison.OrdinalIgnoreCase));
		if (dataIdx < 0 || dataIdx + 2 > segs.Length) return null;
		// sfx/*.ffxbnd entries are "data/Sfx/OutputData/.../fNNNNNNN.ffx" - "Sfx" here is the real
		// category, not a generic DVDROOT placeholder, and every bank shares the same internal tree
		// (.../Effect/f0000512.ffx exists in both main and commoneffects, as a genuinely different
		// effect - docs/context.md part 57/58) - resolving by this path alone would silently
		// collide entries from different banks. Bail to the container-qualified fallback instead.
		if (segs[dataIdx + 1].Equals("Sfx", StringComparison.OrdinalIgnoreCase)) return null;
		return string.Join('/', segs[(dataIdx + 2)..]);
	}
}
