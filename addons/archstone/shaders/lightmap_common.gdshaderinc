// Shared diffuse/normal/specular/lightmap sampling for the single-layer lightmap shader
// family (lightmap.gdshader/lightmap_alpha.gdshader/lightmap_add.gdshader) - only the
// render_mode/blend declaration differs between variants (a compile-time keyword, so one
// runtime-switched shader can't cover all three).
uniform sampler2D diffuse : source_color;
uniform sampler2D normal_map : hint_normal;
uniform sampler2D specular : hint_default_black;
// Defaults to white (no darkening), unlike specular's hint_default_black - diffuse shouldn't
// go dark just because its lightmap couldn't be resolved. Sampled at UV2 (FLVER's own UV1) -
// blend materials' lightmap UV goes through CUSTOM0 instead, see terrain_blend.gdshader.
uniform sampler2D lightmap : hint_default_white;

// g_DiffuseMapColor(Power) tint - see FlverModelBuilder.ResolveMtdShading. White = no-op.
uniform vec3 diffuse_tint : source_color = vec3(1.0);

// The real shader shape (hemisphere-ambient floor untouched by the lightmap, lightmap only
// scaling a secondary env term) lives in hemisphere_ambient.gdshaderinc, shared with
// terrain_blend.gdshader - see docs/ARCHITECTURE.md's Lightmaps section.
#include "hemisphere_ambient.gdshaderinc"

// Returns values via out-params rather than assigning ALBEDO/NORMAL_MAP/METALLIC directly -
// Godot's shader built-ins can only be assigned directly inside fragment() itself, not from a
// helper function called from it, even a #include-d one only ever called from there.
void lightmap_shade(vec2 uv, vec2 uv2, out vec3 albedo, out vec3 normal_map_out, out float metallic, out vec3 emission) {
	vec3 diffuse_color = texture(diffuse, uv).rgb * diffuse_tint;
	vec3 lm = texture(lightmap, uv2).rgb;
	float spec = texture(specular, uv).r;

	vec3 env = lm * env_color * env_intensity;

	albedo = diffuse_color * (hemisphere_ambient() + env);
	normal_map_out = texture(normal_map, uv).rgb;
	metallic = spec;
	emission = lm * env_spc_color * env_spc_intensity * spec;
}
