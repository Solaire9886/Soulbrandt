shader_type spatial;
render_mode unshaded;

// Single-layer lit material (HemEnv or HemDir3), opaque or alpha-tested (g_BlendMode 0/1).
// With alpha_scissor_threshold 0 nothing is discarded; writing ALPHA_SCISSOR_THRESHOLD keeps the
// opaque, unsorted render path.
#include "lightmap_common.gdshaderinc"

uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.0;

void vertex() {
	// Varyings can only be written here, not from a helper.
	world_normal = hemisphere_world_normal(NORMAL, MODEL_MATRIX);
	world_tangent = hemisphere_world_normal(TANGENT, MODEL_MATRIX);
	world_binormal = hemisphere_world_normal(BINORMAL, MODEL_MATRIX);
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	DES_ATMOSPHERE_VERTEX(world_position);
}

void fragment() {
	vec3 camera_position = INV_VIEW_MATRIX[3].xyz;
	// Radial distance for the shadow fade.
	float view_distance = length(world_position - camera_position);
	vec3 view_dir = normalize(camera_position - world_position);
	vec3 color = lightmap_shade(UV, UV2, COLOR.rgb, view_dir, view_distance, FRONT_FACING);
	ALBEDO = des_output(color);
	ALPHA = texture(diffuse, scroll_uv(UV)).a;
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
