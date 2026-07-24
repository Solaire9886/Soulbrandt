@tool
extends EditorPlugin

const MOUNT_CONFIG_PATH := "user://archstone_mount.cfg"

var import_button

var _mount_dialog: EditorFileDialog
var _import_scope_dialog: ConfirmationDialog
var _category_dialog: ConfirmationDialog
var _clear_confirm_dialog: ConfirmationDialog
var _progress_dialog: AcceptDialog
var _progress_bar: ProgressBar
var _progress_label: Label
var _message_dialog: AcceptDialog
var _load_files_dialog: EditorFileDialog
var _load_folder_dialog: EditorFileDialog
var _loader


func _enable_plugin() -> void:
	pass


func _disable_plugin() -> void:
	pass


func _enter_tree() -> void:
	import_button = preload("res://addons/archstone/import.tscn").instantiate()

	add_control_to_container(EditorPlugin.CONTAINER_TOOLBAR, import_button)
	import_button.get_node("MenuButton").get_popup().id_pressed.connect(_on_menu_item_pressed)

	# No EditorSceneFormatImporter for .flver anymore - see CLAUDE.md's Architecture section.
	# One FlverLoader instance for the whole editor session so its cache persists across loads.
	_loader = load("res://addons/archstone/FlverLoader.cs").new()


func _exit_tree() -> void:
	remove_control_from_container(EditorPlugin.CONTAINER_TOOLBAR, import_button)

	import_button.free()
	for dialog in [_mount_dialog, _import_scope_dialog, _category_dialog, _clear_confirm_dialog, _progress_dialog, _message_dialog, _load_files_dialog, _load_folder_dialog]:
		if dialog:
			dialog.queue_free()


func _on_menu_item_pressed(id: int) -> void:
	if id == 0:
		_on_import_pressed()
	elif id == 1:
		_show_mount_dialog()
	elif id == 2:
		_show_clear_confirm_dialog()
	elif id == 3:
		_show_load_files_dialog()
	elif id == 4:
		_show_load_folder_dialog()
	elif id == 5:
		_loader.EvictAll()
		_show_message("Cache cleared", "Every previously loaded model will rebuild from source next time it's loaded.")


func _show_mount_dialog() -> void:
	_mount_dialog = EditorFileDialog.new()
	_mount_dialog.title = "Select your extracted Demon's Souls game root"
	_mount_dialog.file_mode = EditorFileDialog.FILE_MODE_OPEN_DIR
	_mount_dialog.access = EditorFileDialog.ACCESS_FILESYSTEM

	var saved_root := _load_mount_root()
	if not saved_root.is_empty():
		_mount_dialog.current_dir = saved_root

	_mount_dialog.dir_selected.connect(_on_raw_root_selected)
	EditorInterface.get_base_control().add_child(_mount_dialog)
	_mount_dialog.popup_centered_ratio(0.7)


func _on_raw_root_selected(dir: String) -> void:
	var extractor = load("res://addons/archstone/AssetExtractor.cs").new()
	var found: Array = []
	for category in extractor.GetKnownCategories():
		if DirAccess.dir_exists_absolute(dir.path_join(category)):
			found.append(category)

	if found.is_empty():
		_show_message("Not a valid Demon's Souls root", "None of the known asset folders (%s) were found directly under:\n%s" % [", ".join(extractor.GetKnownCategories()), dir])
		return

	_save_mount_root(dir)
	_show_message("Game root mounted", "Found: %s\n%s\n\nUse \"Import\" to extract assets." % [", ".join(found), dir])


func _show_message(title: String, text: String) -> void:
	_message_dialog = AcceptDialog.new()
	_message_dialog.title = title
	_message_dialog.dialog_text = text
	EditorInterface.get_base_control().add_child(_message_dialog)
	_message_dialog.popup_centered()
	_message_dialog.confirmed.connect(_message_dialog.queue_free)
	_message_dialog.close_requested.connect(_message_dialog.queue_free)


func _on_import_pressed() -> void:
	var root := _load_mount_root()
	if root.is_empty():
		_show_message("No game mounted yet", "Use \"Mount...\" first to select your Demon's Souls extraction root.")
		return
	_show_import_scope_dialog(root)


func _show_import_scope_dialog(raw_root: String) -> void:
	_import_scope_dialog = ConfirmationDialog.new()
	_import_scope_dialog.title = "Import Demon's Souls assets"
	_import_scope_dialog.dialog_text = "Import every known asset category, or choose which ones?"
	_import_scope_dialog.ok_button_text = "Full Import"
	_import_scope_dialog.add_button("Choose Categories...", true, "choose")

	_import_scope_dialog.confirmed.connect(func(): _run_extraction(raw_root, []))
	_import_scope_dialog.custom_action.connect(func(action):
		if action == "choose":
			_import_scope_dialog.hide()
			_show_category_dialog(raw_root)
	)

	EditorInterface.get_base_control().add_child(_import_scope_dialog)
	_import_scope_dialog.popup_centered()


func _show_category_dialog(raw_root: String) -> void:
	var extractor = load("res://addons/archstone/AssetExtractor.cs").new()

	_category_dialog = ConfirmationDialog.new()
	_category_dialog.title = "Choose asset categories to extract"

	var vbox := VBoxContainer.new()
	_category_dialog.add_child(vbox)

	var checkboxes: Dictionary = {}
	for category in extractor.GetKnownCategories():
		var cb := CheckBox.new()
		cb.text = category
		cb.button_pressed = true
		vbox.add_child(cb)
		checkboxes[category] = cb

	_category_dialog.confirmed.connect(func():
		var selected: Array = []
		for category in checkboxes.keys():
			if checkboxes[category].button_pressed:
				selected.append(category)
		_run_extraction(raw_root, selected)
	)

	EditorInterface.get_base_control().add_child(_category_dialog)
	_category_dialog.popup_centered()


func _run_extraction(raw_root: String, categories: Array) -> void:
	_progress_label = Label.new()
	_progress_label.text = "Extracting..."
	_progress_bar = ProgressBar.new()
	_progress_bar.max_value = 1
	_progress_bar.value = 0

	var vbox := VBoxContainer.new()
	vbox.add_child(_progress_label)
	vbox.add_child(_progress_bar)

	_progress_dialog = AcceptDialog.new()
	_progress_dialog.title = "Mounting Demon's Souls assets"
	_progress_dialog.add_child(vbox)
	EditorInterface.get_base_control().add_child(_progress_dialog)
	_progress_dialog.popup_centered_ratio(0.4)

	var output_root := ProjectSettings.globalize_path("res://mounted")
	var extractor = load("res://addons/archstone/AssetExtractor.cs").new()
	extractor.ExtractAsync(raw_root, output_root, categories, Callable(self, "_on_extract_progress"), Callable(self, "_on_extract_complete"))


func _on_extract_progress(current: int, total: int, path: String) -> void:
	if not _progress_bar:
		return
	_progress_bar.max_value = total
	_progress_bar.value = current
	_progress_label.text = "[%d/%d] %s" % [current, total, path]


func _on_extract_complete(extracted: int, skipped: int, errors: String) -> void:
	if _progress_label:
		_progress_label.text = "Done. Extracted %d file(s), %d already up to date." % [extracted, skipped]
	if not errors.is_empty():
		for line in errors.split("\n"):
			if not line.is_empty():
				push_error("Archstone mount: " + line)
	# Deliberately no EditorInterface.get_resource_filesystem().scan() call - see _enter_tree().


func _show_clear_confirm_dialog() -> void:
	_clear_confirm_dialog = ConfirmationDialog.new()
	_clear_confirm_dialog.title = "Clear mounted assets"
	_clear_confirm_dialog.dialog_text = "This will delete res://mounted and everything in it. You can re-mount and re-import afterwards. Continue?"
	_clear_confirm_dialog.confirmed.connect(_clear_mounted_assets)
	EditorInterface.get_base_control().add_child(_clear_confirm_dialog)
	_clear_confirm_dialog.popup_centered()


func _clear_mounted_assets() -> void:
	if DirAccess.dir_exists_absolute("res://mounted"):
		_delete_recursive("res://mounted")
	_show_message("Mounted assets cleared", "res://mounted has been deleted.")


func _delete_recursive(path: String) -> void:
	var dir := DirAccess.open(path)
	if not dir:
		return
	dir.list_dir_begin()
	var entry := dir.get_next()
	while entry != "":
		if entry != "." and entry != "..":
			var entry_path := path.path_join(entry)
			if dir.current_is_dir():
				_delete_recursive(entry_path)
			else:
				dir.remove(entry_path)
		entry = dir.get_next()
	dir.list_dir_end()
	DirAccess.remove_absolute(path)


func _show_load_files_dialog() -> void:
	_load_files_dialog = EditorFileDialog.new()
	_load_files_dialog.title = "Select .flver model(s) to load"
	_load_files_dialog.file_mode = EditorFileDialog.FILE_MODE_OPEN_FILES
	_load_files_dialog.access = EditorFileDialog.ACCESS_FILESYSTEM
	_load_files_dialog.add_filter("*.flver", "FLVER Models")

	var mounted_dir := ProjectSettings.globalize_path("res://mounted")
	if DirAccess.dir_exists_absolute(mounted_dir):
		_load_files_dialog.current_dir = mounted_dir

	_load_files_dialog.files_selected.connect(_on_files_selected)
	EditorInterface.get_base_control().add_child(_load_files_dialog)
	_load_files_dialog.popup_centered_ratio(0.7)


func _show_load_folder_dialog() -> void:
	_load_folder_dialog = EditorFileDialog.new()
	_load_folder_dialog.title = "Select a folder - every .flver found under it (recursively) will be loaded"
	_load_folder_dialog.file_mode = EditorFileDialog.FILE_MODE_OPEN_DIR
	_load_folder_dialog.access = EditorFileDialog.ACCESS_FILESYSTEM

	var mounted_dir := ProjectSettings.globalize_path("res://mounted")
	if DirAccess.dir_exists_absolute(mounted_dir):
		_load_folder_dialog.current_dir = mounted_dir

	_load_folder_dialog.dir_selected.connect(_on_load_folder_selected)
	EditorInterface.get_base_control().add_child(_load_folder_dialog)
	_load_folder_dialog.popup_centered_ratio(0.7)


func _on_files_selected(paths: PackedStringArray) -> void:
	var res_paths: Array[String] = []
	for path in paths:
		res_paths.append(ProjectSettings.localize_path(path))
	_load_models(res_paths)


func _on_load_folder_selected(dir: String) -> void:
	_load_models(_flver_files_recursive(dir))


# Every .flver under a folder, recursively - lets "Load Folder..." grab a whole map area
# (or an obj/parts model's sib/ subfolder) in one click.
func _flver_files_recursive(dir_path: String) -> Array[String]:
	var found: Array[String] = []
	var dir := DirAccess.open(dir_path)
	if not dir:
		return found
	dir.list_dir_begin()
	var entry := dir.get_next()
	while entry != "":
		if not entry.begins_with("."):
			var full_path := dir_path.path_join(entry)
			if DirAccess.dir_exists_absolute(full_path):
				found.append_array(_flver_files_recursive(full_path))
			elif entry.get_extension().to_lower() == "flver":
				found.append(ProjectSettings.localize_path(full_path))
		entry = dir.get_next()
	dir.list_dir_end()
	return found


func _load_models(res_paths: Array[String]) -> void:
	if res_paths.is_empty():
		_show_message("Nothing to load", "No .flver files were found in that selection.")
		return

	var target := _pick_load_target()
	if not target:
		_show_message("No scene open", "Open or create a scene first - loaded models are placed under the edited scene's root, or the currently selected scene-tree node.")
		return

	for path in res_paths:
		var inst: Node3D = _loader.Instantiate(path)
		target.add_child(inst)
		_set_owner_recursive(inst, target.owner if target.owner else target)

	_show_message("Models loaded", "Loaded %d model(s) under '%s'." % [res_paths.size(), target.name])


# The first selected Node3D in the Scene dock, or the edited scene's own root.
func _pick_load_target() -> Node3D:
	for node in EditorInterface.get_selection().get_selected_nodes():
		if node is Node3D:
			return node
	var root := EditorInterface.get_edited_scene_root()
	return root if root is Node3D else null


func _set_owner_recursive(node: Node, owner: Node) -> void:
	if node != owner:
		node.owner = owner
	for child in node.get_children():
		_set_owner_recursive(child, owner)


func _load_mount_root() -> String:
	var config := ConfigFile.new()
	if config.load(MOUNT_CONFIG_PATH) != OK:
		return ""
	return config.get_value("mount", "root", "")


func _save_mount_root(path: String) -> void:
	var config := ConfigFile.new()
	config.load(MOUNT_CONFIG_PATH)
	config.set_value("mount", "root", path)
	config.save(MOUNT_CONFIG_PATH)
