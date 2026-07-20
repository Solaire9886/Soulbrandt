using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SoulsFormats;

namespace Archstone;

// Walks a raw, unmodified Demon's Souls extraction root and unpacks its .bnd/.dcx containers
// directly via SoulsFormatsNEXT (DCX_EDGE + BND3, confirmed against real containers this game
// actually ships - see context.md), writing loose files into the same res://mounted layout
// FlverSceneImporter.cs already assumes. No external unpacker tool involved anywhere in this
// workflow. No editor-only API anywhere in this class - it's called identically from
// archstone.gd's dialog and from the headless extract_cli.gd script.
public partial class AssetExtractor : RefCounted
{
	// The only top-level source directories the importer actually reads today. Extraction
	// intentionally never walks the raw root's movie/sound/msg/font/script/param/etc data -
	// nothing consumes it, and unpacking it too would multiply first-run extraction time and
	// disk usage for zero benefit today.
	// ponytail: flat allowlist, extend when animation/collision import needs a new category -
	// HKX/anibnd already live inside chr/, so skeleton import won't even need one.
	public static readonly string[] KnownCategories = { "chr", "map", "obj", "parts", "mtd" };

	// Instance wrapper so GDScript (which can't reliably reach a static field across the
	// C#/GDScript boundary on a load(...).new() instance) can still read this list without
	// a second, separately-maintained copy of it in archstone.gd.
	public string[] GetKnownCategories() => KnownCategories;

	// categories: null/empty means "all of KnownCategories"; unknown names are ignored, not
	// fatal. Runs on a background thread so the caller (editor UI or a headless script) is
	// never blocked; onProgress/onComplete are marshaled back via CallDeferred, the standard
	// safe way to reach the main thread from a background one in Godot.
	public void ExtractAsync(string rawRoot, string outputRoot, string[] categories, Callable onProgress, Callable onComplete)
	{
		var selected = (categories == null || categories.Length == 0)
			? KnownCategories
			: KnownCategories.Where(k => categories.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();

		Task.Run(() => Run(rawRoot, outputRoot, selected, onProgress, onComplete));
	}

	private void Run(string rawRoot, string outputRoot, string[] categories, Callable onProgress, Callable onComplete)
	{
		// A pre-existing symlink (an old `mounted` stopgap from before this class existed)
		// would otherwise have every write transparently follow it into that old target
		// instead of creating a real directory here.
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
			// Not yet confirmed against a real Demon's Souls container (this game's binders
			// have only ever been observed as BND3) - included since SoulsFormatsNEXT already
			// ships it and the cost of handling it here is zero, but treat as unverified.
			foreach (var entry in bnd4.Files)
				WriteEntry(entry.Name, entry.Bytes, sourcePath, outputRoot, ref extracted, ref skipped);
		}
		else if (!ReferenceEquals(raw, inner))
		{
			// A bare DCX-compressed single asset with no binder wrapper at all (e.g. a map
			// piece's own .flver.dcx) - write the decompressed bytes at the source's own
			// relative path, minus the trailing .dcx.
			string relative = Path.GetRelativePath(rawRoot, sourcePath);
			if (relative.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
				relative = relative[..^4];
			WriteIfStale(sourcePath, Path.Combine(outputRoot, relative), inner, ref extracted, ref skipped);
		}
		else
		{
			// Not a container, not DCX - already a plain loose file (e.g. an already-unpacked
			// .mtd sitting next to the containers). Copy through as-is.
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

	// Real BND entry names are full Windows paths, e.g.
	// "N:\DemonsSoul\data\DVDROOT\chr\c2000\c2000.flver" - confirmed (not guessed) against a
	// real c2000.chrbnd.dcx: dropping exactly one segment right after "data" (always
	// "DVDROOT" in real entry names - a different embedded-path convention than the "Model"
	// segment seen in FLVER material texture *references*, easy to conflate but confirmed
	// distinct) and joining the rest lands every entry at its exact existing on-disk path.
	private static string ResolveEntryOutputPath(string entryName)
	{
		var segs = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		int dataIdx = Array.FindIndex(segs, s => s.Equals("data", StringComparison.OrdinalIgnoreCase));
		if (dataIdx < 0 || dataIdx + 2 > segs.Length) return null;
		return string.Join('/', segs[(dataIdx + 2)..]);
	}
}
