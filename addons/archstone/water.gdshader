shader_type spatial;
render_mode blend_mix, cull_back, diffuse_burley, specular_schlick_ggx;

// Demon's Souls' "DS_Water_Env" MTD family (g_BlendMode=Water/WaterWave). Confirmed via
// SoulsFormatsNEXT's MTD.cs reader against the real .mtd files (mounted/mtd/, unpacked by
// WitchyBND - not present when the rest of this importer's material logic was originally
// written, see context.md): these materials carry only g_Bumpmap + g_Envmap, no diffuse
// texture at all. g_Envmap is a small flat "impression" image of the surrounding scene
// (confirmed by direct visual inspection - not a cubemap), so it's sampled here as a cheap
// matcap-style reflection lookup (reflected view-space normal's XY as UV) rather than a
// real environment/cubemap probe - the standard PS3-era substitute for real reflections.
// All wave/reflection tuning below (tile scale, scroll, blend weight, water tint, Fresnel
// curve) comes from real per-material .mtd params with no equivalent in FLVER0's own
// material data; the uniform defaults here are generic-water fallbacks used only if that
// real file couldn't be resolved at import time (see FlverSceneImporter.BuildWaterMaterial).
uniform sampler2D bumpmap : hint_normal;
uniform sampler2D envmap : source_color;
uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear_mipmap;

uniform vec2 tile_scale_0 = vec2(1.0, 1.0);
uniform vec2 tile_scale_1 = vec2(1.0, 1.0);
uniform vec2 tile_scale_2 = vec2(1.0, 1.0);
uniform vec2 tex_scroll_0 = vec2(0.0, 0.0);
uniform vec2 tex_scroll_1 = vec2(0.0, 0.0);
uniform vec2 tex_scroll_2 = vec2(0.0, 0.0);
uniform float tile_blend_0 = 1.0;
uniform float tile_blend_1 = 0.0;
uniform float tile_blend_2 = 0.0;

uniform vec3 water_color : source_color = vec3(0.1, 0.15, 0.2);
// g_WaterColor is really a Float4 - this is its 4th component, read separately since a
// plain vec3 uniform can't carry it. Confirmed to matter, not a leftover: opaque murky
// water (e.g. Valley of Defilement's swamp, water_alpha=0.784, g_ReflectBand=0) reads
// noticeably lower here than a clear reflective moat (e.g. Boletaria's, water_alpha=0.588,
// g_ReflectBand=0.1) - the two together are what separate "mud" from "lake" in the real
// data, not water_color's tint alone.
uniform float water_alpha = 0.7;
uniform float refract_band = 0.15;
uniform float reflect_band = 0.1;
uniform float fresnel_pow = 3.0;
uniform float fresnel_bias = 0.1;
uniform float fresnel_scale = 1.0;
uniform vec3 fresnel_color : source_color = vec3(1.0, 1.0, 1.0);
// g_WaterFadeBegin - real per-material data (varies 0.3-1.0 game-wide), previously unused.
// Read as a shallow/deep transition distance for the depth-fade below.
uniform float water_fade_begin = 0.5;

void fragment() {
	// Multi-octave scrolling wave normals - g_TileScale_N/g_TexScroll_N/g_TileBlend_N are
	// three independently-tuned samples of the same bump texture (big rolling waves, mid
	// ripples, fine detail) blended by weight, not one flat scale.
	// ponytail: linear-average the 3 tangent-space samples rather than a proper
	// renormalized blend - same "good enough" tradeoff as terrain_blend.gdshader's
	// two-layer normal mix.
	// g_TileScale_N applied directly to raw mesh UV (UVNumber in the real MTD confirms
	// g_Bumpmap really does use this UV channel, so that part isn't in question) produces
	// individual wave-tile repeats spanning ~40-95 *world units* on a large mesh like
	// Valley of Defilement's swamp - user-confirmed as the actual cause of the "stretched,
	// reflective-looking" wave pattern reported (the visible streaking was the scrolling
	// wave normal itself, not a rendering artifact). wave_detail_scale is an extra flat
	// multiplier bringing that down to a plausible ripple frequency - like refract_band's
	// scale below, this specific constant is an uncalibrated guess (no ground truth for
	// what unit/space g_TileScale was meant to apply in), not confirmed real data.
	// Multiplying the whole coordinate (tiling + scroll together) keeps the scroll
	// animation's speed proportional to the new, denser tile size instead of going static.
	const float wave_detail_scale = 12.0;
	vec3 n0 = texture(bumpmap, (UV * tile_scale_0 + tex_scroll_0 * TIME) * wave_detail_scale).rgb * 2.0 - 1.0;
	vec3 n1 = texture(bumpmap, (UV * tile_scale_1 + tex_scroll_1 * TIME) * wave_detail_scale).rgb * 2.0 - 1.0;
	vec3 n2 = texture(bumpmap, (UV * tile_scale_2 + tex_scroll_2 * TIME) * wave_detail_scale).rgb * 2.0 - 1.0;
	float wsum = max(tile_blend_0 + tile_blend_1 + tile_blend_2, 0.001);
	vec3 tangentNormal = normalize((n0 * tile_blend_0 + n1 * tile_blend_1 + n2 * tile_blend_2) / wsum);
	NORMAL_MAP = tangentNormal * 0.5 + 0.5;

	vec3 N = normalize(TANGENT * tangentNormal.x + BINORMAL * tangentNormal.y + NORMAL * tangentNormal.z);

	// Fresnel: more reflective at grazing angles. Schlick-style bias+scale*pow(), matching
	// how g_FresnelBias/g_FresnelScale/g_FresnelPow are named in the real MTD.
	float fresnel = clamp(fresnel_bias + fresnel_scale * pow(1.0 - max(dot(N, VIEW), 0.0), fresnel_pow), 0.0, 1.0);

	// Matcap-style reflection (see file header - g_Envmap is a flat impression image, not
	// a cubemap).
	vec3 reflected = reflect(-VIEW, N);
	vec3 reflection = texture(envmap, reflected.xy * 0.5 + 0.5).rgb * fresnel_color * reflect_band;

	// Depth-based shallow/deep fade using the real, previously-unused g_WaterFadeBegin:
	// reconstruct the view-space position of whatever's actually behind this pixel (the
	// swamp/lake floor) and compare its depth against this water pixel's own depth. Right
	// at the shoreline that difference is ~0 (this *is* the floor), so the water should be
	// almost fully see-through there regardless of how murky/opaque the material is in
	// open water - real shallow muddy edges show the bottom even when the deep water
	// doesn't. Without this, water_alpha applied the same murky opacity everywhere on the
	// surface, uniformly, with no shallow/deep distinction at all.
	float raw_depth = texture(depth_tex, SCREEN_UV).r;
	vec4 view_pos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
	view_pos.xyz /= view_pos.w;
	float water_depth = abs(view_pos.z - VERTEX.z);
	// ponytail: linear ramp from 0 at the shore to fully water_alpha at water_fade_begin -
	// g_WaterFadeBegin only gives one distance, not a separate fade-end, so there's no real
	// data to derive a more precise curve shape from.
	float shallow = clamp(water_depth / max(water_fade_begin, 0.001), 0.0, 1.0);

	// Screen-space refraction: read what's actually behind the water surface, distorted by
	// the wave normal and tinted by g_WaterColor. g_RefractBand's real range (0.05-0.8) is
	// far too large to use directly as a screen_uv offset (a raw 0.15 is 15% of the whole
	// screen) - the original shader almost certainly applies it in a different unit/space
	// with no visibility into what that is. Scaling it down and hard-clamping is a
	// deliberate, uncalibrated guess at a sane magnitude (unlike everything else in this
	// shader, which comes from confirmed real data) - without this, adjacent pixels on a
	// large, near-edge-on water surface sampled wildly different, unrelated parts of the
	// screen, producing chaotic diagonal streaking rather than a subtle ripple.
	const float refraction_scale = 0.03;
	vec2 refract_offset = clamp(N.xy * refract_band * refraction_scale, vec2(-0.03), vec2(0.03));
	vec3 refraction = textureLod(screen_tex, SCREEN_UV + refract_offset, 0.0).rgb * water_color;
	vec3 surface = mix(refraction, water_color, water_alpha * shallow);

	// EMISSION, not ALBEDO: this whole computation (matcap reflection + tinted refraction +
	// Fresnel mix) is a self-contained, hand-tuned final color, the same way the original
	// PS3 shader almost certainly worked - not a physically-based surface meant to plug
	// into Godot's real lighting model. Routing it through ALBEDO was a real bug: Godot's
	// PBR model shifts energy from diffuse to specular as ROUGHNESS drops, and with no real
	// sky/reflection probe in the scene for a low-roughness "mirror" to actually reflect,
	// ALBEDO (only visible via the diffuse term) became almost entirely invisible - needing
	// the sun cranked to 3x just to see a hint of it, exactly the reported symptom. EMISSION
	// bypasses that entirely and is always visible regardless of scene lighting/roughness.
	EMISSION = mix(surface, reflection, fresnel);
	ALBEDO = vec3(0.0);
	ALPHA = 1.0;
	// ROUGHNESS/SPECULAR still derived from g_ReflectBand (confirmed real signal, 0 or 0.1
	// across every water material - g_SpecularPower was checked and is a constant 100
	// everywhere, not usable) - kept only for a small *additional* real specular sun-glint
	// on top of the EMISSION-driven look above, not as the primary reflective effect.
	float reflectiveness = clamp(reflect_band / 0.1, 0.0, 1.0);
	ROUGHNESS = mix(0.6, 0.15, reflectiveness);
	SPECULAR = mix(0.05, 0.5, reflectiveness);
}
