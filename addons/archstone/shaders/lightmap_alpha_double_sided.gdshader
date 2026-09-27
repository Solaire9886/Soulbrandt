shader_type spatial;
render_mode unshaded, cull_disabled;

// lightmap_alpha.gdshader with cull_disabled (see lightmap_double_sided.gdshader).
#include "lightmap_common.gdshaderinc"

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
}
