shader_type spatial;

// Single-layer FLVER materials with a real g_Lightmap texture (~6529 of 8889 materials,
// confirmed via corpus survey) - StandardMaterial3D has no independent-UV multiply slot for
// it, so these need a real shader (see lightmap_common.gdshaderinc). alpha_scissor_threshold
// defaults to 0.0 (never discards) so the "_Edge" cutout case can share this same shader
// instead of needing its own file - referencing ALPHA_SCISSOR_THRESHOLD at all keeps Godot's
// opaque/no-sort render path regardless of the threshold value, unlike real ALPHA blending
// (see lightmap_alpha.gdshader/lightmap_add.gdshader, which do need their own files since
// blend_mix vs blend_add is a compile-time render_mode, not a per-material property).
#include "lightmap_common.gdshaderinc"

uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.0;

void vertex() {
	world_normal = hemisphere_world_normal(NORMAL, MODEL_MATRIX);
}

void fragment() {
	vec3 albedo, normal_map_out, emission;
	float metallic;
	lightmap_shade(UV, UV2, albedo, normal_map_out, metallic, emission);
	ALBEDO = albedo;
	NORMAL_MAP = normal_map_out;
	METALLIC = metallic;
	EMISSION = emission;
	ALPHA = texture(diffuse, UV).a;
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
