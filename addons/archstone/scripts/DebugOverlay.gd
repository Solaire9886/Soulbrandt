@tool
extends Control
## Text diagnostics drawn over a 3D view: frame timing, render counters, camera pose, map
## VFX budget, map context and a frustum render log. The Archstone editor plugin hosts one
## instance outside the tree and draws it over an editor viewport; in a running game, add
## it under a CanvasLayer and it ticks and draws itself.

const SECTIONS := ["frame", "render", "camera", "vfx", "map", "log"]
const TITLES := {
	"frame": "Frame timing",
	"render": "Render counters",
	"camera": "Camera pose",
	"vfx": "Map VFX budget",
	"map": "Map context",
	"log": "Render log (view frustum)",
}
const FONT_SIZE := 12
const OUTLINE := 3
const LINE := 14
const TOP := 48 # clears the editor viewport's view menu
const LOG_LINES := 24
const SLOW_INTERVAL := 0.5 # scene walks and culling; the overlay must not become the stutter
const MAP_SFX_SCRIPT := preload("res://addons/archstone/scripts/MapSfxPreview.cs")

var enabled := {}
## Set by the editor host when the editor only redraws on change (EditorInterface is not
## available in exported games, so this script never reads editor settings itself).
var redraws_on_demand := false
var _font := SystemFont.new()
var _fast := PackedStringArray()
var _slow := {}
var _slow_timer := 0.0
var _measured: RID


func _init() -> void:
	_font.font_names = PackedStringArray(["DejaVu Sans Mono", "Liberation Mono", "Consolas", "Menlo", "monospace"])
	for s in SECTIONS:
		enabled[s] = false
	mouse_filter = MOUSE_FILTER_IGNORE
	set_anchors_preset(PRESET_FULL_RECT)


func any_enabled() -> bool:
	return enabled.values().has(true)


## Game hosting. The editor plugin calls tick() and draw_on() itself.
func _process(delta: float) -> void:
	if Engine.is_editor_hint():
		return
	tick(delta, get_viewport().get_camera_3d(), get_tree().current_scene, get_viewport())
	queue_redraw()


func _draw() -> void:
	if not Engine.is_editor_hint():
		draw_on(self)


func tick(delta: float, camera: Camera3D, scene_root: Node, viewport: Viewport) -> void:
	_fast.clear()
	if not any_enabled() or viewport == null:
		release()
		return
	if enabled["frame"]:
		_frame_lines(viewport)
	elif _measured.is_valid():
		release()
	if enabled["render"]:
		_render_lines(viewport)
	if enabled["camera"]:
		_camera_lines(camera)
	_slow_timer -= delta
	if _slow_timer <= 0:
		_slow_timer = SLOW_INTERVAL
		_slow.clear()
		if enabled["vfx"] or enabled["map"]:
			_scene_lines(camera, scene_root)
		if enabled["log"]:
			_log_lines(camera, scene_root, viewport)
	# Sections switched off since the last slow refresh disappear immediately.
	for s in _slow.keys():
		if not enabled[s]:
			_slow.erase(s)


## Stops the render-time measurement this overlay enabled.
func release() -> void:
	if _measured.is_valid():
		RenderingServer.viewport_set_measure_render_time(_measured, false)
		_measured = RID()


func draw_on(target: CanvasItem) -> void:
	var lines := PackedStringArray(_fast)
	for s in SECTIONS:
		if _slow.has(s):
			lines.append_array(_slow[s])
	var y := TOP
	for line in lines:
		var at := Vector2(8, y)
		target.draw_string_outline(_font, at, line, HORIZONTAL_ALIGNMENT_LEFT, -1, FONT_SIZE, OUTLINE, Color.BLACK)
		target.draw_string(_font, at, line, HORIZONTAL_ALIGNMENT_LEFT, -1, FONT_SIZE, Color.WHITE)
		y += LINE


func _frame_lines(viewport: Viewport) -> void:
	var rid := viewport.get_viewport_rid()
	if rid != _measured:
		release()
		RenderingServer.viewport_set_measure_render_time(rid, true)
		_measured = rid
	var gpu := RenderingServer.viewport_get_measured_render_time_gpu(rid)
	var cpu := RenderingServer.viewport_get_measured_render_time_cpu(rid) + RenderingServer.get_frame_setup_time_cpu()
	_fast.append("FPS %4d  process %6.2f ms  physics %6.2f ms" % [Engine.get_frames_per_second(),
		Performance.get_monitor(Performance.TIME_PROCESS) * 1000, Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000])
	_fast.append("RENDER cpu %6.2f ms  gpu %s" % [cpu, "%6.2f ms" % gpu if gpu > 0 else "n/a (not reported)"])
	if redraws_on_demand:
		_fast.append("  editor low-processor mode: FPS is capped by its sleep (Update Continuously uncaps it)")


func _render_lines(viewport: Viewport) -> void:
	var v := Viewport.RENDER_INFO_TYPE_VISIBLE
	var s := Viewport.RENDER_INFO_TYPE_SHADOW
	_fast.append("DRAW calls %5d  objects %5d  primitives %8d  | shadow calls %5d" % [
		viewport.get_render_info(v, Viewport.RENDER_INFO_DRAW_CALLS_IN_FRAME),
		viewport.get_render_info(v, Viewport.RENDER_INFO_OBJECTS_IN_FRAME),
		viewport.get_render_info(v, Viewport.RENDER_INFO_PRIMITIVES_IN_FRAME),
		viewport.get_render_info(s, Viewport.RENDER_INFO_DRAW_CALLS_IN_FRAME)])
	var mb := 1.0 / (1024 * 1024)
	_fast.append("VRAM textures %7.1f MB  buffers %7.1f MB  total %7.1f MB" % [
		RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TEXTURE_MEM_USED) * mb,
		RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_BUFFER_MEM_USED) * mb,
		RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_VIDEO_MEM_USED) * mb])


func _camera_lines(camera: Camera3D) -> void:
	if camera == null:
		_fast.append("CAMERA none")
		return
	var p := camera.global_position
	var r := camera.global_rotation_degrees
	_fast.append("CAMERA pos %9.3f %9.3f %9.3f  rot %7.2f %7.2f %7.2f  fov %.1f" % [p.x, p.y, p.z, r.x, r.y, r.z, camera.fov])
	# Soulbrandt mirrors raw X on import; native (MSB) coordinates un-mirror it.
	_fast.append("  native pos %9.3f %9.3f %9.3f" % [0.0 if p.x == 0 else -p.x, p.y, p.z])


func _scene_lines(camera: Camera3D, scene_root: Node) -> void:
	var vfx := PackedStringArray()
	var map := PackedStringArray()
	if scene_root:
		for preview in scene_root.find_children("*", "Node3D", true, false):
			if preview.get_script() != MAP_SFX_SCRIPT:
				continue
			var root: Node = preview.get_parent()
			if enabled["vfx"]:
				var d: Dictionary = preview.GetSummary()
				vfx.append("VFX %s %s  active %d parked %d  systems %d/%d  particles %d (alloc %d/%d)  omitted %d" % [
					root.name, "on" if d["enabled"] else "off", d["active"], d["parked"], d["systems"], d["max_systems"],
					d["live"], d["particles"], d["max_particles"], d["omitted"]])
				vfx.append("  placements %d (%d unresolved)  effects built %d, unsupported %d" % [
					d["placements"], d["unresolved"], d["catalog"], d["unsupported"]])
			if enabled["map"]:
				map.append(_map_line(root, camera))
	if enabled["vfx"]:
		_slow["vfx"] = vfx if not vfx.is_empty() else PackedStringArray(["VFX no MapSfxPreview in scene"])
	if enabled["map"]:
		_slow["map"] = map if not map.is_empty() else PackedStringArray(["MAP no loaded map in scene"])


func _map_line(root: Node, camera: Camera3D) -> String:
	var parts := 0
	var nearest: Node3D
	var best := INF
	for child in root.get_children():
		if not child is Node3D or not child.has_meta("flver_path"):
			continue
		parts += 1
		if camera:
			var d := camera.global_position.distance_squared_to(child.global_position)
			if d < best:
				best = d
				nearest = child
	var line := "MAP %s  parts %d" % [root.name, parts]
	if nearest:
		line += "  nearest %s (%s, entity %d) %.1f m" % [nearest.name,
			str(nearest.get_meta("flver_path")).get_file(), nearest.get_meta("msb_entity_id", -1), sqrt(best)]
	return line


func _log_lines(camera: Camera3D, scene_root: Node, viewport: Viewport) -> void:
	if camera == null or scene_root == null:
		_slow["log"] = PackedStringArray(["LOG no camera or scene"])
		return
	var ids := RenderingServer.instances_cull_convex(camera.get_frustum(), viewport.find_world_3d().scenario)
	var seen := {}
	var rows := []
	var shaders := {}
	for id in ids:
		if seen.has(id):
			continue # editor gizmos attach to their node's ID too
		seen[id] = true
		var node = instance_from_id(id)
		if not node is GeometryInstance3D or not node.is_visible_in_tree() or not scene_root.is_ancestor_of(node):
			continue
		var shader := _shader_name(node)
		shaders[shader] = shaders.get(shader, 0) + 1
		var center: Vector3 = node.global_transform * node.get_aabb().get_center()
		rows.append([camera.global_position.distance_to(center), node, shader])
	rows.sort_custom(func(a, b): return a[0] < b[0])
	var out := PackedStringArray()
	out.append("LOG %d visible instances in frustum (not occlusion-tested)" % rows.size())
	var tally := shaders.keys()
	tally.sort_custom(func(a, b): return shaders[a] > shaders[b])
	out.append("  by shader: " + ", ".join(PackedStringArray(tally.slice(0, 6).map(func(k): return "%s %d" % [k, shaders[k]]))))
	for row in rows.slice(0, LOG_LINES):
		out.append("  %7.1f m  %-28s %-24s %s" % [row[0], _owner_name(row[1], scene_root).left(28), row[2].left(24), _detail(row[1])])
	if rows.size() > LOG_LINES:
		out.append("  ... %d more" % (rows.size() - LOG_LINES))
	_slow["log"] = out


func _shader_name(node: GeometryInstance3D) -> String:
	var material: Material = node.material_override
	if material == null and node is MeshInstance3D and node.mesh and node.mesh.get_surface_count() > 0:
		material = node.get_active_material(0)
	if material == null and node is MultiMeshInstance3D and node.multimesh and node.multimesh.mesh:
		material = node.multimesh.mesh.surface_get_material(0)
	if material is ShaderMaterial and material.shader:
		return material.shader.resource_path.get_file().get_basename() if material.shader.resource_path else "ShaderMaterial"
	return material.get_class() if material else "(none)"


# The nearest ancestor that names a model (map part / loaded model) or an SFX preview.
func _owner_name(node: Node, scene_root: Node) -> String:
	var n := node
	while n and n != scene_root:
		if n.has_meta("flver_path") or str(n.name).begins_with("SFX_"):
			return str(n.name)
		n = n.get_parent()
	return str(node.name)


func _detail(node: GeometryInstance3D) -> String:
	if node is MultiMeshInstance3D and node.multimesh:
		return "multimesh %d/%d" % [node.multimesh.visible_instance_count if node.multimesh.visible_instance_count >= 0 else node.multimesh.instance_count, node.multimesh.instance_count]
	if node is MeshInstance3D and node.mesh:
		return "%d surf" % node.mesh.get_surface_count()
	return node.get_class()
