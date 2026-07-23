shader_type spatial;

// "_Alp" lightmapped materials (soft alpha blend, e.g. translucent hair/cloth with baked
// lighting) - see lightmap.gdshader's header for why this needs its own file.
#include "lightmap_common.gdshaderinc"

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
}
