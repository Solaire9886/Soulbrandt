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
// vertex_color (COLOR.rgb), view_dir and front_facing are passed in because built-ins aren't
// readable from a helper (front_facing needs FRONT_FACING passed all the way from fragment() -
// this function is itself a helper one level removed, so the built-in isn't visible even to it).
// In the HemEnv branch vertex_color feeds both diffuse_color and the env_specular tint (the
// disassembled DifSpc HemEnv folds COL0 into both); it is a near-universal input in DeS's own
// shaders (see docs/context.md parts 14, 43). It never touches the ungated hemisphere floor.
vec3 lightmap_shade(vec2 uv, vec2 uv2, vec3 vertex_color, vec3 view_dir, float view_distance, bool front_facing) {
	vec2 duv = scroll_uv(uv);
	vec3 diffuse_tex = texture(diffuse, duv).rgb * diffuse_tint;
	vec3 lm = texture(lightmap, uv2).rgb;
	// RGB, not just red - the HemEnv and HemDir3 specular terms use the whole sampled colour.
	vec3 spec = texture(specular, duv).rgb;
	vec3 sampled_normal = texture(normal_map, duv).rgb;

	vec3 pixel_normal = hemisphere_pixel_normal(decode_normal_map(sampled_normal.xy), front_facing);

	// Hemisphere floor common to both models; HemDir3 takes directional light, HemEnv takes env.
	// DeS gates the directional/env term by min(shadow, lightmap) - the hemisphere floor stays
	// ungated (matches the bytecode, see hemisphere_ambient.gdshaderinc).
	vec3 shadow = sun_shadow(world_position, view_distance);
	// Not shadow-gated - a torch/campfire isn't occluded by the sun's shadow map.
	vec3 point_lights = point_lights_diffuse(world_position, pixel_normal);
	if (lighting_model == LIGHTING_HEM_DIR3) {
		// HemDir3 (chr/parts/obj) usually ships no lightmap, so shadow alone is the gate.
		vec3 diffuse_color = diffuse_tex * vertex_color;
		return diffuse_color * (hemisphere_ambient(pixel_normal) + hemdir3_diffuse(pixel_normal) * shadow + point_lights)
			+ hemdir3_specular(pixel_normal, view_dir, spec, vertex_color) * shadow;
	}
	// The captured HemEnv fragment programs' f[COL0] MUL by c[11]=(0.6,0.6,0.6,1) is not an
	// extra engine constant - it IS the material's own g_DiffuseMapColorPower (0.6 on most map
	// MTDs), already folded into diffuse_tint by FlverModelBuilder.ReadMtdShading. Multiplying it
	// in again here double-applied that power (0.6*0.6=0.36), leaving every lightmap map-piece
	// ~1.67x too dark. See the map-shading research pass's finding #1
	// (MAP_SHADING_ACCURACY_2026_09_15.md - external research archive, not tracked in this repo).
	vec3 diffuse_color = diffuse_tex * vertex_color;
	// env-specular takes vertex colour too (the single-layer family's is a free multiplier).
	vec3 gate = min(shadow, lm);
	return diffuse_color * (hemisphere_ambient(pixel_normal) + env_diffuse(pixel_normal, gate) + point_lights)
		+ env_specular(pixel_normal, view_dir, gate, spec, vertex_color);
}
