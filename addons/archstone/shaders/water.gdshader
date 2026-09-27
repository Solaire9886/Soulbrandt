shader_type spatial;
// Unshaded (a finished colour). blend_mix with ALPHA = 1 keeps it on the transparent pass so the
// screen and depth textures are readable for the refraction and reflection taps.
render_mode blend_mix, cull_back, unshaded;

// Reconstructed from DS_Water_Env (107 instructions), DS_Water, the water .mtd parameters and six
// captured water draws (docs/context.md, "Water"): three scrolling bump octaves, a
// Fresnel-weighted mix of the refracted body and the reflection (cubemap, screen tap, sun glint),
// then the shared output stage.
uniform sampler2D bumpmap : hint_normal;
// g_Envmap, a small cubemap sampled by the reflection vector (FlverModelBuilder.ResolveWaterEnvCube;
// a dim neutral cube when unresolved).
uniform samplerCube envmap : source_color;
uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear_mipmap;

// Fog and scattering rows are bound per placement like any map surface.
#include "output_stage.gdshaderinc"

// g_TileScale_i: .x = UV tiling, .y = this octave's multiplier on the flow velocity (c128..c130).
uniform vec2 tile_scale_0 = vec2(1.0, 0.1);
uniform vec2 tile_scale_1 = vec2(1.0, 0.1);
uniform vec2 tile_scale_2 = vec2(1.0, 0.1);
// g_TexScroll_0, a velocity used as is: its magnitude is the base speed (normalising it made m02
// water 30x too fast). Zero falls back to a slow drift.
uniform vec2 flow_dir = vec2(0.05, 0.0);
// g_TileBlend_0/1/2, the octave weights.
uniform float tile_blend_0 = 1.0;
uniform float tile_blend_1 = 0.8;
uniform float tile_blend_2 = 0.5;

// g_WaterColor: .rgb body tint (c36); .a in water_alpha, the body-vs-refraction mix.
uniform vec3 water_color : source_color = vec3(0.09, 0.078, 0.031);
uniform float water_alpha = 0.7;
// g_RefractBand / g_ReflectBand: screen-UV perturbation of the two screen taps.
uniform float refract_band = 0.15;
uniform float reflect_band = 0.1;
// g_FresnelPow / Bias / Scale / Color. pow 0 gives constant reflectance.
uniform float fresnel_pow = 3.0;
uniform float fresnel_bias = 0.1;
uniform float fresnel_scale = 0.9;
uniform vec3 fresnel_color : source_color = vec3(1.0);
// g_WaterFadeBegin: width in metres of the dark band where water meets the shore.
uniform float water_fade_begin = 0.5;
// g_BumpMapSmoose: a bias added to the summed wave normal's Z (negative = choppier).
uniform float bump_smoose = 1.0;
// g_SpecularMapColor / g_SpecularMapColorPower: the sun-glint weight (c39) on its own.
uniform vec3 glint_color : source_color = vec3(1.0);
uniform float glint_power = 2.0;

varying vec3 world_position;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	DES_ATMOSPHERE_VERTEX(world_position);
}

// One octave: UV * s.x drifting by flow * s.y per second; Z rebuilt from the two-channel sample.
vec3 wave_octave(vec2 base_uv, vec2 s, vec2 flow) {
	vec2 uv = base_uv * s.x + flow * (s.y * TIME);
	vec2 xy = texture(bumpmap, uv).xy * 2.0 - 1.0;
	return vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
}

void fragment() {
	vec2 flow = length(flow_dir) > 1e-6 ? flow_dir : vec2(0.03, 0.0);
	vec3 n0 = wave_octave(UV, tile_scale_0, flow);
	vec3 n1 = wave_octave(UV, tile_scale_1, flow);
	vec3 n2 = wave_octave(UV, tile_scale_2, flow);
	// Weighted sum, not renormalised, then g_BumpMapSmoose added to Z (as in the disassembly).
	vec3 wave = n0 * tile_blend_0 + n1 * tile_blend_1 + n2 * tile_blend_2;
	vec3 tangent_normal = normalize(vec3(wave.xy, max(wave.z + bump_smoose, 0.1)));
	vec3 N = normalize(TANGENT * tangent_normal.x + BINORMAL * tangent_normal.y + NORMAL * tangent_normal.z);

	float ndv = clamp(dot(N, normalize(VIEW)), 0.0, 1.0);
	float fresnel = fresnel_pow > 1e-3
		? clamp(fresnel_bias + fresnel_scale * pow(1.0 - ndv, fresnel_pow), 0.0, 1.0)
		: clamp(fresnel_scale, 0.0, 1.0);

	float raw_depth = texture(depth_tex, SCREEN_UV).r;

	vec3 camera_position = INV_VIEW_MATRIX[3].xyz;
	vec3 view_dir = normalize(camera_position - world_position);
	float view_distance = length(world_position - camera_position);
	// Only the reflection tap's offset is distance-scaled in the disassembly; the refraction tap's
	// is not. ponytail: the falloff shape stands in for the untraced per-vertex factor f[TEX6].
	float near_fac = clamp(2.0 / max(view_distance, 1.0), 0.0, 1.0);

	// Refraction, falling back to the unperturbed UV where the offset tap lands on nearer geometry.
	vec2 refract_uv = SCREEN_UV + N.xy * refract_band;
	if (texture(depth_tex, refract_uv).r > raw_depth)
		refract_uv = SCREEN_UV;
	vec3 refracted = textureLod(screen_tex, refract_uv, 0.0).rgb;
	vec3 screen_reflection = textureLod(screen_tex, SCREEN_UV + N.xy * (reflect_band * near_fac), 0.0).rgb;

	vec3 body = mix(refracted, water_color, water_alpha);

	// Shoreline band from the depth of the water column behind this pixel.
	// ponytail: a linear mix toward water_color; the game's band shape is not captured.
	vec4 view_pos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
	view_pos.xyz /= view_pos.w;
	float column = abs(view_pos.z - VERTEX.z);
	float shore_edge = 1.0 - clamp(column / max(water_fade_begin, 1e-3), 0.0, 1.0);

	// Reflection: cubemap, screen tap for near geometry, and the sun glint
	// pow(R . sun, g_SpecularPower = 100) weighted by glint_color * glint_power only (a captured
	// draw shows no sun-intensity factor). The sun direction is the scattering bank's.
	vec3 n_world = normalize((INV_VIEW_MATRIX * vec4(N, 0.0)).xyz);
	vec3 refl_vec = reflect(-view_dir, n_world);
	vec3 cube_reflection = texture(envmap, refl_vec).rgb;
	float glint = pow(max(dot(refl_vec, -scatter_light_dir), 0.0), 100.0);
	vec3 reflection = mix(cube_reflection, screen_reflection, near_fac * 0.35) * fresnel_color
		+ glint * glint_color * glint_power;

	vec3 surface = mix(body, reflection, fresnel);
	surface = mix(surface, water_color, shore_edge);
	ALBEDO = des_output(surface);
	ALPHA = 1.0;
}
