shader_type spatial;
render_mode unshaded, cull_disabled;

// Identical to lightmap.gdshader except cull_disabled - see FlverModelBuilder's
// FLVER0.Mesh.CullBackfaces handling. Cull mode is a compile-time render_mode keyword on
// ShaderMaterial (no per-instance property like StandardMaterial3D.CullMode), so a
// double-sided mesh on this shader family needs its own file - the same reason blend_mix vs
// blend_add already needed separate files. Covers g_BlendMode Opaque/AlphaTest (the two
// modes lightmap.gdshader itself handles) - e.g. m2304b0's stained-glass windows in Doran's
// Mausoleum, meant to be visible from inside the room. The back-face normal flip that makes
// this shade correctly from either side lives in hemisphere_pixel_normal
// (hemisphere_ambient.gdshaderinc), not here.
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
	vec3 color = lightmap_shade(UV, UV2, COLOR.rgb, view_dir, view_distance, FRONT_FACING);
	ALBEDO = des_output(color, view_distance, view_dir);
	ALPHA = texture(diffuse, scroll_uv(UV)).a;
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
