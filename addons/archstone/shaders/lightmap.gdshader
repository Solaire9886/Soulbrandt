shader_type spatial;

// Single-layer FLVER materials with a real g_Lightmap texture - StandardMaterial3D has no
// independent-UV multiply slot for it (see lightmap_common.gdshaderinc). alpha_scissor_threshold
// defaults to 0.0 (never discards) so the "_Edge" cutout case can share this file instead of
// needing its own - referencing ALPHA_SCISSOR_THRESHOLD at all keeps Godot's opaque/no-sort
// render path regardless of value, unlike real ALPHA blending (lightmap_alpha/add.gdshader
// need their own files since blend_mix vs blend_add is a compile-time render_mode).
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
