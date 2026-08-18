shader_type spatial;
render_mode blend_mix, cull_back, diffuse_burley, specular_schlick_ggx;

// DeS's water MTD family (g_Bumpmap + g_Envmap only, no diffuse). g_Envmap is a small flat
// "impression" image, not a cubemap - sampled below as a matcap-style lookup. Wave/reflection
// tuning below comes from real per-material .mtd params (see docs/ARCHITECTURE.md's water
// section); the defaults here only apply if that file couldn't be resolved at import time.
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
// g_WaterColor's 4th component (a plain vec3 uniform can't carry it) - separates murky from
// clear water; water_color's tint alone isn't enough to tell them apart.
uniform float water_alpha = 0.7;
uniform float refract_band = 0.15;
uniform float reflect_band = 0.1;
uniform float fresnel_pow = 3.0;
uniform float fresnel_bias = 0.1;
uniform float fresnel_scale = 1.0;
uniform vec3 fresnel_color : source_color = vec3(1.0, 1.0, 1.0);
// g_WaterFadeBegin - shallow/deep transition distance for the depth-fade below.
uniform float water_fade_begin = 0.5;
// g_BumpMapSmoose - wave-normal intensity scale. ponytail: uncalibrated guess at intent, no
// ground truth for what the original shader did with this value.
uniform float bump_smoose = 1.0;

void fragment() {
	// Three independently-tuned bump-texture samples (big waves/mid ripples/fine detail),
	// linearly blended by weight - ponytail: not a proper renormalized blend, same tradeoff
	// as terrain_blend.gdshader's two-layer normal mix.
	// ponytail: wave_detail_scale is an uncalibrated flat multiplier bringing g_TileScale_N's
	// raw-UV tiling down to a plausible ripple frequency - no ground truth for the real unit.
	// Applied to the whole coordinate (tiling+scroll together) so scroll speed stays
	// proportional to tile size.
	const float wave_detail_scale = 12.0;
	vec3 n0 = texture(bumpmap, (UV * tile_scale_0 + tex_scroll_0 * TIME) * wave_detail_scale).rgb * 2.0 - 1.0;
	vec3 n1 = texture(bumpmap, (UV * tile_scale_1 + tex_scroll_1 * TIME) * wave_detail_scale).rgb * 2.0 - 1.0;
	vec3 n2 = texture(bumpmap, (UV * tile_scale_2 + tex_scroll_2 * TIME) * wave_detail_scale).rgb * 2.0 - 1.0;
	float wsum = max(tile_blend_0 + tile_blend_1 + tile_blend_2, 0.001);
	vec3 tangentNormal = normalize((n0 * tile_blend_0 + n1 * tile_blend_1 + n2 * tile_blend_2) / wsum);
	tangentNormal = normalize(vec3(tangentNormal.xy * bump_smoose, tangentNormal.z));
	NORMAL_MAP = tangentNormal * 0.5 + 0.5;

	vec3 N = normalize(TANGENT * tangentNormal.x + BINORMAL * tangentNormal.y + NORMAL * tangentNormal.z);

	// Fresnel: more reflective at grazing angles. Schlick-style bias+scale*pow(), matching
	// how g_FresnelBias/g_FresnelScale/g_FresnelPow are named in the real MTD.
	float fresnel = clamp(fresnel_bias + fresnel_scale * pow(1.0 - max(dot(N, VIEW), 0.0), fresnel_pow), 0.0, 1.0);

	vec3 reflected = reflect(-VIEW, N);
	vec3 reflection = texture(envmap, reflected.xy * 0.5 + 0.5).rgb * fresnel_color * reflect_band;

	// Depth-fade: compare this water pixel's depth against whatever's behind it, so shallow
	// edges stay see-through regardless of how murky the material is in open water.
	float raw_depth = texture(depth_tex, SCREEN_UV).r;
	vec4 view_pos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
	view_pos.xyz /= view_pos.w;
	float water_depth = abs(view_pos.z - VERTEX.z);
	// ponytail: linear ramp from 0 at the shore to fully water_alpha at water_fade_begin -
	// g_WaterFadeBegin only gives one distance, not a separate fade-end, so there's no real
	// data to derive a more precise curve shape from.
	float shallow = clamp(water_depth / max(water_fade_begin, 0.001), 0.0, 1.0);

	// Screen-space refraction, tinted by g_WaterColor. ponytail: refraction_scale is an
	// uncalibrated guess - g_RefractBand's real range (0.05-0.8) is far too large to use
	// directly as a screen_uv offset; hard-clamped to avoid chaotic diagonal streaking.
	const float refraction_scale = 0.03;
	vec2 refract_offset = clamp(N.xy * refract_band * refraction_scale, vec2(-0.03), vec2(0.03));
	vec3 refraction = textureLod(screen_tex, SCREEN_UV + refract_offset, 0.0).rgb * water_color;
	vec3 surface = mix(refraction, water_color, water_alpha * shallow);

	// EMISSION, not ALBEDO: this is a self-contained hand-tuned final color, not a
	// physically-based surface - with no reflection probe in the scene, routing it through
	// ALBEDO made it nearly invisible regardless of scene lighting/roughness.
	EMISSION = mix(surface, reflection, fresnel);
	ALBEDO = vec3(0.0);
	ALPHA = 1.0;
	// ROUGHNESS/SPECULAR from g_ReflectBand - a small *additional* specular sun-glint on top
	// of the EMISSION-driven look, not the primary reflective effect.
	float reflectiveness = clamp(reflect_band / 0.1, 0.0, 1.0);
	ROUGHNESS = mix(0.6, 0.15, reflectiveness);
	SPECULAR = mix(0.05, 0.5, reflectiveness);
}
