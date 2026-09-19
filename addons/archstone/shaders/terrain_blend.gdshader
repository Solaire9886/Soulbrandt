shader_type spatial;
render_mode unshaded;

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

// g_TexScroll_0/_1, one per diffuse layer, in UV units per second. Zero = no-op, which is
// every blend material except the three c5000 slime ones. The lightmap UV isn't scrolled -
// same reasoning as lightmap_common.gdshaderinc.
uniform vec2 tex_scroll_0 = vec2(0.0);
uniform vec2 tex_scroll_1 = vec2(0.0);

// Shares the hemisphere-ambient/env formula via hemisphere_ambient.gdshaderinc rather than
// duplicating it - see docs/ARCHITECTURE.md's Lightmaps section.
#include "hemisphere_ambient.gdshaderinc"
#include "output_stage.gdshaderinc"

// CUSTOM0 is only readable inside vertex(), unlike COLOR/UV/UV2 - forwarded via varying.
varying vec2 lightmap_uv;

void vertex() {
	lightmap_uv = CUSTOM0.rg;
	world_normal = hemisphere_world_normal(NORMAL, MODEL_MATRIX);
	world_tangent = hemisphere_world_normal(TANGENT, MODEL_MATRIX);
	world_binormal = hemisphere_world_normal(BINORMAL, MODEL_MATRIX);
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// COLOR.a is the blend weight; COLOR.rgb is a second, independent signal - native HemEnv
	// Mul-family programs (DS_Phn_Dif______MulLitSdw_HemEnv and the DifSpc sibling) multiply the
	// interpolated diffuse AND the blended RGB specular by vertex colour, same free multiplier
	// role the single-layer family's vertex colour plays. See the map-shading research pass's
	// finding #3 (MAP_SHADING_ACCURACY_2026_09_15.md - external research archive, not tracked
	// in this repo) - the earlier "unknown, deliberately unused" read (an ALBEDO-only
	// experiment, not this multiplicative role) was wrong.
	float blend = COLOR.a;
	vec2 uv1 = UV + tex_scroll_0 * TIME;
	vec2 uv2 = UV2 + tex_scroll_1 * TIME;
	vec3 diffuse_color = mix(texture(diffuse1, uv1).rgb, texture(diffuse2, uv2).rgb, blend) * diffuse_tint * COLOR.rgb;
	vec3 lm = texture(lightmap, lightmap_uv).rgb;
	// RGB, not just red - the native HemEnv specular term uses the whole sampled colour (see
	// lightmap_common.gdshaderinc's identical note).
	vec3 spec = mix(texture(specular1, uv1).rgb, texture(specular2, uv2).rgb, blend);
	// ponytail: linear mix of two tangent-space normal maps, not a proper renormalized blend.
	vec3 sampled_normal = mix(texture(normal1, uv1).rgb, texture(normal2, uv2).rgb, blend);
	// This shader has no double-sided variant yet (0 such meshes in the corpus), but FRONT_FACING
	// is free to pass here - it's a direct call from fragment(), not through another helper.
	vec3 pixel_normal = hemisphere_pixel_normal(decode_normal_map(sampled_normal.xy), FRONT_FACING);
	vec3 camera_position = INV_VIEW_MATRIX[3].xyz;
	vec3 view_dir = normalize(camera_position - world_position);
	// Radial camera distance - the scattering model uses the true distance, not a planar depth.
	float view_distance = length(world_position - camera_position);

	// envSpc added straight in, not routed through EMISSION - this shader is unshaded.
	// Gate is DeS's min(shadow, lightmap); the hemisphere floor stays ungated.
	vec3 gate = min(sun_shadow(world_position, view_distance), lm);
	// No fixed 0.6 here - that was a duplicate of the material's own g_DiffuseMapColorPower
	// (already folded into diffuse_tint), not a real engine constant. See the map-shading
	// research pass's finding #1 (MAP_SHADING_ACCURACY_2026_09_15.md - external research
	// archive, not tracked in this repo); lightmap_common.gdshaderinc had the same bug. The
	// env_specular tint argument is COLOR.rgb above, not the diffuse factor.
	// Not shadow-gated - a torch/campfire isn't occluded by the sun's shadow map.
	vec3 point_lights = point_lights_diffuse(world_position, pixel_normal);
	vec3 color = diffuse_color * (hemisphere_ambient(pixel_normal) + env_diffuse(pixel_normal, gate) + point_lights)
		+ env_specular(pixel_normal, view_dir, gate, spec, COLOR.rgb);
	ALBEDO = des_output(color, view_distance, view_dir);
}
