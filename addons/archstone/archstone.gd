@tool
extends EditorPlugin

const MOUNT_CONFIG_PATH := "user://archstone_mount.cfg"

var import_button
var flver_importer

var _mount_dialog: EditorFileDialog
var _category_dialog: ConfirmationDialog
var _progress_dialog: AcceptDialog
var _progress_bar: ProgressBar
var _progress_label: Label


func _enable_plugin() -> void:
	# Add autoloads here.
	pass


func _disable_plugin() -> void:
	# Remove autoloads here.
	pass


func _enter_tree() -> void:
	# Initialization of the plugin goes here.
	import_button = preload("res://addons/archstone/import.tscn").instantiate()

	add_control_to_container(EditorPlugin.CONTAINER_TOOLBAR, import_button)
	import_button.get_node("MenuButton").get_popup().id_pressed.connect(_on_menu_item_pressed)

	flver_importer = load("res://addons/archstone/FlverSceneImporter.cs").new()
	add_scene_format_importer_plugin(flver_importer)


func _exit_tree() -> void:
	# Clean-up of the plugin goes here.
	remove_scene_format_importer_plugin(flver_importer)
	remove_control_from_container(EditorPlugin.CONTAINER_TOOLBAR, import_button)

	import_button.free()
	for dialog in [_mount_dialog, _category_dialog, _progress_dialog]:
		if dialog:
			dialog.queue_free()


func _on_menu_item_pressed(id: int) -> void:
	if id == 1:
		_show_mount_dialog()


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
	_save_mount_root(dir)
	_show_category_dialog(dir)


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
	EditorInterface.get_resource_filesystem().scan()


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
