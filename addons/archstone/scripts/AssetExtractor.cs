using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SoulsFormats;

namespace Archstone;

// Unpacks the .bnd/.dcx containers of a raw game root into loose files under res://mounted. No
// editor API: used by archstone.gd and the headless extract_cli.gd alike.
public partial class AssetExtractor : RefCounted
{
	// The top-level folders the loaders read. "param" also brings gameparam/ (~8 MB, unused yet).
	// "shader" and "sfx" entries need FallbackEntryOutputPath (see ResolveEntryOutputPath).
	// ponytail: flat allowlist; extend when a new category is needed.
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
			// No DeS BND4 has been seen; supported because SoulsFormatsNEXT reads it anyway.
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

	// For entries without a data/DVDROOT path (shaderbnd entries are build paths; sfx paths collide
	// between banks): the container's own location plus a folder named after it, e.g.
	// shader/ds_flver/. Without this such entries were silently dropped.
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

	// Entry names are Windows paths ("N:\...\data\DVDROOT\chr\c2000\c2000.flver"); dropping the
	// segment after "data" gives the on-disk path. Null without that prefix (see
	// FallbackEntryOutputPath).
	private static string ResolveEntryOutputPath(string entryName)
	{
		var segs = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		int dataIdx = Array.FindIndex(segs, s => s.Equals("data", StringComparison.OrdinalIgnoreCase));
		if (dataIdx < 0 || dataIdx + 2 > segs.Length) return null;
		// .ffxbnd entries ("data/Sfx/OutputData/.../fNNNNNNN.ffx") share one internal tree across banks
		// (f0000512.ffx differs between main and commoneffects), so they use the fallback path.
		if (segs[dataIdx + 1].Equals("Sfx", StringComparison.OrdinalIgnoreCase)) return null;
		return string.Join('/', segs[(dataIdx + 2)..]);
	}
}
