shader_type spatial;
render_mode unshaded;

// Single-layer FLVER materials with a real g_Lightmap texture - StandardMaterial3D has no
// independent-UV multiply slot for it (see lightmap_common.gdshaderinc). alpha_scissor_threshold
// defaults to 0.0 (never discards) so the "_Edge" cutout case can share this file instead of
// needing its own - referencing ALPHA_SCISSOR_THRESHOLD at all keeps Godot's opaque/no-sort
// render path regardless of value, unlike real ALPHA blending (lightmap_alpha/add.gdshader
// need their own files since blend_mix vs blend_add is a compile-time render_mode).
#include "lightmap_common.gdshaderinc"

uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.0;

void vertex() {
	// Varyings can't be assigned from a helper, so each entry point sets the TBN + world pos itself.
	world_normal = hemisphere_world_normal(NORMAL, MODEL_MATRIX);
	world_tangent = hemisphere_world_normal(TANGENT, MODEL_MATRIX);
	world_binormal = hemisphere_world_normal(BINORMAL, MODEL_MATRIX);
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 camera_position = INV_VIEW_MATRIX[3].xyz;
	// view_distance is the radial camera distance for the scattering term - see output_stage.
	float view_distance = length(world_position - camera_position);
	vec3 view_dir = normalize(camera_position - world_position);
	vec3 color = lightmap_shade(UV, UV2, COLOR.rgb, view_dir);
	ALBEDO = des_output(color, view_distance, view_dir);
	ALPHA = texture(diffuse, scroll_uv(UV)).a;
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
