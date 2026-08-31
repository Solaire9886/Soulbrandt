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

// g_TexScroll_0, UV units/sec. The lightmap's own UV is deliberately NOT scrolled - it's baked
// to fixed geometry, only the tiling detail moves.
uniform vec2 tex_scroll_0 = vec2(0.0);

vec2 scroll_uv(vec2 uv) {
	return uv + tex_scroll_0 * TIME;
}

// The real shader shape (hemisphere-ambient floor untouched by the lightmap, lightmap only
// scaling a secondary env term) lives in hemisphere_ambient.gdshaderinc, shared with
// terrain_blend.gdshader - see docs/ARCHITECTURE.md's Lightmaps section.
#include "hemisphere_ambient.gdshaderinc"
#include "output_stage.gdshaderinc"

// Returns a finished colour, not an ALBEDO/EMISSION surface - these shaders are unshaded.
// vertex_color (COLOR.rgb) and view_dir are passed in because built-ins aren't readable from a
// helper. vertex_color goes onto diffuse_color only, not the final colour, so it doesn't scale
// envSpc - it's a near-universal input in DeS's own shaders (see docs/context.md part 14).
vec3 lightmap_shade(vec2 uv, vec2 uv2, vec3 vertex_color, vec3 view_dir) {
	vec2 duv = scroll_uv(uv);
	vec3 diffuse_color = texture(diffuse, duv).rgb * diffuse_tint * vertex_color;
	vec3 lm = texture(lightmap, uv2).rgb;
	// RGB, not just red - the HemEnv and HemDir3 specular terms use the whole sampled colour.
	vec3 spec = texture(specular, duv).rgb;
	vec3 sampled_normal = texture(normal_map, duv).rgb;

	vec3 pixel_normal = hemisphere_pixel_normal(decode_normal_map(sampled_normal.xy));

	// Hemisphere floor common to both models; HemDir3 takes directional light, HemEnv takes env.
	if (lighting_model == LIGHTING_HEM_DIR3) {
		return diffuse_color * (hemisphere_ambient(pixel_normal) + hemdir3_diffuse(pixel_normal))
			+ hemdir3_specular(pixel_normal, view_dir, spec, vertex_color);
	}
	// env-specular takes vertex colour too (the single-layer family's is a free multiplier).
	return diffuse_color * (hemisphere_ambient(pixel_normal) + env_diffuse(pixel_normal, lm))
		+ env_specular(pixel_normal, view_dir, lm, spec, vertex_color);
}
