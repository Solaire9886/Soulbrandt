shader_type spatial;
render_mode blend_add, unshaded;

// "_Add" lightmapped materials (additive glow with baked lighting) - see lightmap.gdshader's
// header for why this needs its own file.
#include "lightmap_common.gdshaderinc"

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
}
