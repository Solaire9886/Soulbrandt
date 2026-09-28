shader_type spatial;
// Unshaded (a finished colour). blend_mix with ALPHA = 1 keeps it on the transparent pass so the
// screen and depth textures are readable for the refraction tap.
render_mode blend_mix, cull_back, unshaded;

// Reconstructed from DS_Water_Env (107 instructions), DS_Water, the water .mtd parameters and six
// captured water draws (docs/context.md, "Water"): three scrolling bump octaves, a
// Fresnel-weighted mix of the refracted body and the reflection (cubemap, sun glint), the shared
// output stage, and a fade back to the body where the vertex alpha marks the shoreline.
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

// g_WaterColor: .rgb body colour (c36); .a (c40.w) scales the vertex alpha in the body mix.
uniform vec3 water_color : source_color = vec3(0.09, 0.078, 0.031);
uniform float water_alpha = 1.0;
// g_RefractBand (c11.x): screen-UV offset of the refraction tap per unit of wave normal.
uniform float refract_band = 0.15;
// g_FresnelPow / Bias / Scale / Color (c33.x, c41.x, c42.x, c25).
uniform float fresnel_pow = 3.0;
uniform float fresnel_bias = 0.1;
uniform float fresnel_scale = 0.9;
uniform vec3 fresnel_color : source_color = vec3(1.0);
// g_WaterFadeBegin (c46.x, reciprocal in c48.y): the vertex alpha below which the surface fades
// to the plain body. Water meshes author their shoreline in vertex colour: alpha for this fade,
// RGB as a multiplier (m02's river banks carry 0.5).
uniform float water_fade_begin = 0.7;
// g_BumpMapSmoose: a bias added to the summed wave normal's Z (negative = choppier).
uniform float bump_smoose = 1.0;
// g_SpecularMapColor / g_SpecularMapColorPower: the sun-glint weight (c39) on its own.
uniform vec3 glint_color : source_color = vec3(1.0);
uniform float glint_power = 2.0;
// Toward-light vector of LIGHT_BANK directional light 0 (c22), which the glint follows; the
// scattering sun is a different vector. Bound per placement by FlverLoader.
uniform vec3 glint_light_direction = vec3(0.0, 1.0, 0.0);

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
	// DS_Water_Env reads the wave normal as the world normal (x, z, y), assuming flat water; X is
	// negated for Godot's mirrored space. N is its view-space copy.
	vec3 n_world = normalize(vec3(-tangent_normal.x, tangent_normal.z, tangent_normal.y));
	vec3 N = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);

	// Vertex colour (TEX6), passed through unchanged by DS_Water.
	float vertex_alpha = COLOR.a;
	bool measure = DES_MEASURING;

	// Refraction: the offset is scaled by vertex alpha, and falls back to the unperturbed tap where
	// it lands on nearer geometry (the engine tests the scene buffer's alpha instead). The screen
	// holds encoded colour, decoded to scene radiance as the engine does before mixing.
	float raw_depth = texture(depth_tex, SCREEN_UV).r;
	vec2 refract_uv = SCREEN_UV + tangent_normal.xy * (refract_band * vertex_alpha);
	if (texture(depth_tex, refract_uv).r > raw_depth)
		refract_uv = SCREEN_UV;
	vec3 refracted = des_decode(textureLod(screen_tex, refract_uv, 0.0).rgb, measure);
	vec3 body = mix(refracted, water_color, vertex_alpha * water_alpha);

	// Fresnel scale * (bias + (1 - bias) * (1 - N.V)^pow).
	float ndv = max(dot(N, normalize(VIEW)), 0.0);
	float fresnel = fresnel_scale * (fresnel_bias + (1.0 - fresnel_bias) * pow(max(1.0 - ndv, 1e-6), fresnel_pow));

	// Reflection: cubemap plus the sun glint pow(R . L, g_SpecularPower = 100); no screen tap.
	vec3 view_dir = normalize(INV_VIEW_MATRIX[3].xyz - world_position);
	vec3 refl_vec = reflect(-view_dir, n_world);
	vec3 cube_reflection = texture(envmap, refl_vec * vec3(-1.0, 1.0, 1.0)).rgb; // cube is in FLVER (X-mirrored) space
	float glint = pow(max(dot(refl_vec, glint_light_direction), 0.0), 100.0);
	vec3 reflection = cube_reflection * fresnel_color + glint * glint_color * glint_power;

	vec3 surface = mix(body, reflection, fresnel) * COLOR.rgb;
	// Below g_WaterFadeBegin the atmosphere-applied surface fades to the unfogged body.
	float shore = min(vertex_alpha, water_fade_begin) / max(water_fade_begin, 1e-3);
	ALBEDO = mix(des_exposed(body, measure), des_output(surface), shore);
	ALPHA = 1.0;
}
