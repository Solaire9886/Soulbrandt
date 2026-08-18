shader_type spatial;

// Two-layer diffuse/normal/specular blend for FLVER materials whose MTD carries FromSoft's
// "[M]"/"[ML]" blend-shader bracket tag (the terrain ground-blend convention - grass/dirt/
// stone transitions). Bumpmap/specular are each independently optional per layer - specular
// defaults to hint_default_black to match the single-layer path's Metallic=0 default.
uniform sampler2D diffuse1 : source_color;
uniform sampler2D diffuse2 : source_color;
uniform sampler2D normal1 : hint_normal;
uniform sampler2D normal2 : hint_normal;
uniform sampler2D specular1 : hint_default_black;
uniform sampler2D specular2 : hint_default_black;
// UV is FLVER's third UV channel - UV/UV2 are already spoken for by diffuse1/diffuse2, so
// FlverModelBuilder.cs packs it into the mesh's Custom0 channel instead (read back below).
uniform sampler2D lightmap : hint_default_white;

// Same tint gap the single-layer lightmap family had - only one g_DiffuseMapColor per
// material (no _2 variant), so the tint applies after the two layers are blended below.
uniform vec3 diffuse_tint : source_color = vec3(1.0);

// Shares the hemisphere-ambient/env formula via hemisphere_ambient.gdshaderinc rather than
// duplicating it - see docs/ARCHITECTURE.md's Lightmaps section.
#include "hemisphere_ambient.gdshaderinc"

// CUSTOM0 is only readable inside vertex(), unlike COLOR/UV/UV2 - forwarded via varying.
varying vec2 lightmap_uv;

void vertex() {
	lightmap_uv = CUSTOM0.rg;
	world_normal = hemisphere_world_normal(NORMAL, MODEL_MATRIX);
}

void fragment() {
	// COLOR.a is the blend weight, not .r/g/b - see docs/ARCHITECTURE.md's terrain-blend entry
	// for why. COLOR.r (=g=b) is a real signal too (only two discrete values per mesh) but
	// isn't AO/shading - tried in ALBEDO and reverted, still unknown, deliberately left unused.
	float blend = COLOR.a;
	vec3 diffuse_color = mix(texture(diffuse1, UV).rgb, texture(diffuse2, UV2).rgb, blend) * diffuse_tint;
	vec3 lm = texture(lightmap, lightmap_uv).rgb;
	float spec = mix(texture(specular1, UV).r, texture(specular2, UV2).r, blend);
	vec3 env = lm * env_color * env_intensity;
	ALBEDO = diffuse_color * (hemisphere_ambient() + env);
	// ponytail: linear mix of two tangent-space normal maps, not a proper renormalized blend.
	NORMAL_MAP = mix(texture(normal1, UV).rgb, texture(normal2, UV2).rgb, blend);
	METALLIC = spec;
	// envSpc (LIGHT_BANK's specular-env term) to EMISSION, same no-reflection-probe reasoning
	// as water.gdshader and the single-layer lightmap path.
	EMISSION = lm * env_spc_color * env_spc_intensity * spec;
}
