extends SceneTree

# Headless entry point into AssetExtractor.cs, for CLI-driven extraction without an editor
# GUI session - same underlying code path archstone.gd's "Mount..." dialog calls into.
# Usage: godot-mono --headless --path . -s addons/archstone/extract_cli.gd -- \
#            --raw-root=/path/to/raw/game/root [--categories=chr,map,...]

func _init():
	var raw_root := ""
	var categories: Array = []

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--raw-root="):
			raw_root = arg.substr("--raw-root=".length())
		elif arg.begins_with("--categories="):
			categories = arg.substr("--categories=".length()).split(",")

	if raw_root.is_empty():
		printerr("Usage: -- --raw-root=<path> [--categories=chr,map,obj,parts,mtd]")
		quit(1)
		return

	if not DirAccess.dir_exists_absolute(raw_root):
		printerr("raw-root does not exist: ", raw_root)
		quit(1)
		return

	var output_root = ProjectSettings.globalize_path("res://mounted")
	var extractor = load("res://addons/archstone/AssetExtractor.cs").new()

	print("Extracting ", ("all categories" if categories.is_empty() else str(categories)), " from ", raw_root, " into ", output_root, " ...")
	extractor.ExtractAsync(raw_root, output_root, categories, Callable(self, "_on_progress"), Callable(self, "_on_complete"))


func _on_progress(current: int, total: int, path: String) -> void:
	if current == total or current % 100 == 0:
		print("[%d/%d] %s" % [current, total, path])


func _on_complete(extracted: int, skipped: int, errors: String) -> void:
	print("Done. extracted=%d skipped=%d" % [extracted, skipped])
	if not errors.is_empty():
		printerr("Errors:\n", errors)
	quit()
