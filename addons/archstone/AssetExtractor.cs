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
	// The only categories the importer actually reads - see CLAUDE.md.
	// ponytail: flat allowlist, extend when animation/collision import needs a new category.
	public static readonly string[] KnownCategories = { "chr", "map", "obj", "parts", "mtd" };

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
				WriteEntry(entry.Name, entry.Bytes, sourcePath, outputRoot, ref extracted, ref skipped);
		}
		else if (BND4.IsRead(inner, out var bnd4))
		{
			// Not yet confirmed against a real DeS container (only BND3 observed so far) -
			// included since SoulsFormatsNEXT already ships it at zero extra cost.
			foreach (var entry in bnd4.Files)
				WriteEntry(entry.Name, entry.Bytes, sourcePath, outputRoot, ref extracted, ref skipped);
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

	private void WriteEntry(string entryName, byte[] bytes, string sourcePath, string outputRoot, ref int extracted, ref int skipped)
	{
		string relative = ResolveEntryOutputPath(entryName);
		if (relative == null) return;
		WriteIfStale(sourcePath, Path.Combine(outputRoot, relative), bytes, ref extracted, ref skipped);
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
	private static string ResolveEntryOutputPath(string entryName)
	{
		var segs = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		int dataIdx = Array.FindIndex(segs, s => s.Equals("data", StringComparison.OrdinalIgnoreCase));
		if (dataIdx < 0 || dataIdx + 2 > segs.Length) return null;
		return string.Join('/', segs[(dataIdx + 2)..]);
	}
}
