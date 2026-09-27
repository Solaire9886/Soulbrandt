// Lighting terms shared by the lightmap family and terrain_blend.gdshader, following the captured
// HemEnv/HemDir3 fragment programs: hemisphere ambient, env cubemaps or three directional lights,
// point lights and the sun-shadow gate. Uniform defaults are default_lightbank.param row 0.
// See docs/ARCHITECTURE.md, "Rendering pipeline".

// MTD g_LightingType, as a uniform branch: HemEnv (3; hemisphere + env cubemaps) or HemDir3 (1;
// hemisphere + three directional lights). HemDir3 takes no env term.
const int LIGHTING_HEM_ENV = 0;
const int LIGHTING_HEM_DIR3 = 1;
uniform int lighting_model = 0;

// LIGHT_BANK's three directional lights (world-space travel directions from degRotX/Y, colours
// premultiplied by colA) and its specular colour. Zero by default: an unbound material is unlit.
uniform vec3 dir_light_colors[3];
uniform vec3 dir_light_directions[3];
uniform vec3 spec_light_color = vec3(0.0);
uniform vec3 spec_light_direction = vec3(0.0, 1.0, 0.0);
uniform float specular_power = 8.0;

// g_SpecularMapColor * g_SpecularMapColorPower, on the HemEnv env specular term only.
uniform vec3 specular_tint : source_color = vec3(1.0);

// Up to four MSB point lights (POINT_LIGHT_BANK, see MsbLoader.ReadPointLights), bound per
// placement by FlverLoader. World-space positions; colours premultiplied by colA. An unused slot
// has dwindle.y = 0, which the falloff turns into no contribution.
uniform vec3 point_light_pos[4];
uniform vec3 point_light_color[4];
// x = dwindleBegin, y = dwindleEnd, in metres.
uniform vec2 point_light_dwindle[4];

uniform vec3 ambient_up : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_up_intensity = 0.3;
uniform vec3 ambient_down : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_down_intensity = 0.3;

// LIGHT_BANK's diffuse hemisphere pair (colA_du/colA_dd) is deliberately not read: three
// attempts flattened the Nexus (docs/context.md, "Dead ends").
uniform vec3 env_color : source_color = vec3(1.0, 1.0, 1.0);
// Real values run 1.5-5x and are left unclamped; a clamp was tried and reverted.
uniform float env_intensity = 2.0;
uniform vec3 env_spc_color : source_color = vec3(1.0, 1.0, 1.0);
uniform float env_spc_intensity = 2.0;

// EnvDif and the material's EnvSpc slot from map/<mXX>/<mXX>_9999.tpf; a 1x1 white cubemap when a
// name does not resolve.
uniform samplerCube env_dif_cube;
uniform samplerCube env_spc_cube;

uniform bool use_normal_map = false;

// Sun shadow, bound by ShadowRenderer from the map's SHADOW_BANK row. shadow_light_clip maps world
// to light clip space (atlas UV = xy/w * 0.5 + 0.5); shadow_light_view rebuilds the linear depth
// shadow_depth.gdshader wrote. shadow_strength stays 0 until a map binds it.
uniform sampler2D shadow_map : hint_default_black; // an empty texel reads 0: no occluder
uniform mat4 shadow_light_clip = mat4(1.0);
uniform mat4 shadow_light_view = mat4(1.0);
uniform float shadow_light_far = 1.0;
uniform float shadow_bias = 0.0015;
uniform float shadow_strength = 0.0;
uniform vec2 shadow_texel = vec2(0.00048828125); // 1 / 2048
uniform float shadow_density = 0.9;          // densityRatio / 100
uniform vec3 shadow_tint = vec3(0.0);        // colR/G/B / 255
uniform float shadow_fade_begin = 1000.0;    // fadeBeginDist
uniform float shadow_fade_range = 1.0;       // fadeDist

varying vec3 world_normal;
varying vec3 world_tangent;
varying vec3 world_binormal;
varying vec3 world_position;

// Takes MODEL_MATRIX as a parameter: it is not readable from a helper.
vec3 hemisphere_world_normal(vec3 normal, mat4 model_matrix) {
	return normalize((model_matrix * vec4(normal, 0.0)).xyz);
}

// DeS normal maps are two-channel; Z is rebuilt.
vec3 decode_normal_map(vec2 sampled_xy) {
	vec2 n = sampled_xy * 2.0 - 1.0;
	return vec3(n, sqrt(clamp(1.0 - dot(n, n), 0.0, 1.0)));
}

// Per-pixel normal through the TBN, or the vertex normal without a bump map. Flipped on back
// faces so double-sided meshes shade toward the camera; FRONT_FACING is passed in because a
// nested helper cannot read it.
vec3 hemisphere_pixel_normal(vec3 tangent_space_normal, bool front_facing) {
	vec3 n;
	if (!use_normal_map) {
		n = normalize(world_normal);
	} else {
		n = normalize(tangent_space_normal.x * world_tangent
			+ tangent_space_normal.y * world_binormal
			+ tangent_space_normal.z * world_normal);
	}
	return front_facing ? n : -n;
}

// mix(down, up, (N.y + 1) / 2), not gated.
vec3 hemisphere_ambient(vec3 normal) {
	return mix(ambient_down * ambient_down_intensity, ambient_up * ambient_up_intensity, normal.y * 0.5 + 0.5);
}

vec3 hemdir3_diffuse(vec3 normal) {
	vec3 total = vec3(0.0);
	for (int i = 0; i < 3; i++) {
		total += dir_light_colors[i] * max(dot(normal, dir_light_directions[i]), 0.0);
	}
	return total;
}

// Three Phong lobes (reflect . L) over the directional lights, weighted by the single specular
// colour; the native program builds no half-vector. spec_light_direction is unused here.
vec3 hemdir3_specular(vec3 normal, vec3 view_dir, vec3 specular_sample, vec3 tint) {
	vec3 reflected = reflect(-view_dir, normal);
	float lobe = 0.0;
	for (int i = 0; i < 3; i++) {
		lobe += pow(max(dot(reflected, dir_light_directions[i]), 0.0), specular_power);
	}
	return spec_light_color * lobe * specular_sample * tint;
}

// Lambert with the captured linear falloff saturate((dwindleEnd - d) / (dwindleEnd - dwindleBegin)).
// Not shadowed.
vec3 point_lights_diffuse(vec3 world_pos, vec3 normal) {
	vec3 total = vec3(0.0);
	for (int i = 0; i < 4; i++) {
		vec3 to_light = point_light_pos[i] - world_pos;
		float dist = length(to_light);
		float range = max(point_light_dwindle[i].y - point_light_dwindle[i].x, 0.001);
		float atten = clamp(1.0 - (dist - point_light_dwindle[i].x) / range, 0.0, 1.0);
		if (atten <= 0.0) continue;
		float ndotl = max(dot(normal, to_light / max(dist, 0.001)), 0.0);
		total += point_light_color[i] * atten * ndotl;
	}
	return total;
}

// The engine's per-channel shadow term 1 - (density - tint) * fade * inShadow. vec3(1.0) wherever
// there is no shadow information (disabled, faded, outside the atlas). The game does one
// hardware 2x2 compare; the packed RGBA8 depth here cannot be hardware-filtered, so a rotated
// 12-tap Poisson disk (rotation hashed from world_pos, stable on a static scene) stands in.
const vec2 SHADOW_POISSON_12[12] = {
	vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696, 0.457), vec2(-0.203, 0.621),
	vec2(0.962, -0.195), vec2(0.473, -0.480), vec2(0.519, 0.767), vec2(0.185, -0.893),
	vec2(0.507, 0.064), vec2(0.896, 0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598)
};
const float SHADOW_PCF_RADIUS = 3.5; // texels

vec3 sun_shadow(vec3 world_pos, float view_distance) {
	if (shadow_strength <= 0.0) {
		return vec3(1.0);
	}
	float fade = clamp((shadow_fade_begin + shadow_fade_range - view_distance) / max(shadow_fade_range, 0.001), 0.0, 1.0);
	if (fade <= 0.0) {
		return vec3(1.0);
	}
	vec4 clip = shadow_light_clip * vec4(world_pos, 1.0);
	if (clip.w <= 0.0) { // behind the shadow camera
		return vec3(1.0);
	}
	vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
	if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
		return vec3(1.0);
	}
	float receiver_d = -(shadow_light_view * vec4(world_pos, 1.0)).z / shadow_light_far;

	float angle = fract(sin(dot(world_pos.xy + world_pos.yz, vec2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
	vec2 rot = vec2(cos(angle), sin(angle));
	float lit = 0.0;
	for (int i = 0; i < 12; i++) {
		vec2 o = SHADOW_POISSON_12[i];
		vec2 offset = vec2(o.x * rot.x - o.y * rot.y, o.x * rot.y + o.y * rot.x) * SHADOW_PCF_RADIUS * shadow_texel;
		vec2 packed = texture(shadow_map, uv + offset).rg;
		float occluder_d = packed.r + packed.g / 255.0; // 16-bit, see shadow_depth.gdshader
		lit += (occluder_d <= 0.0 || receiver_d - shadow_bias <= occluder_d) ? 1.0 : 0.0;
	}
	float in_shadow = (1.0 - lit / 12.0) * fade * shadow_strength;
	return vec3(1.0) - max(vec3(shadow_density) - shadow_tint, vec3(0.0)) * in_shadow;
}

// EnvDif by the normal, gated by min(shadow, lightmap).
vec3 env_diffuse(vec3 normal, vec3 gate) {
	return texture(env_dif_cube, normal).rgb * env_color * env_intensity * gate;
}

// EnvSpc by the reflection vector, weighted by the RGB specular sample, `tint` (the vertex colour)
// and specular_tint, gated like env_diffuse.
vec3 env_specular(vec3 normal, vec3 view_dir, vec3 gate, vec3 specular_sample, vec3 tint) {
	vec3 reflected = reflect(-view_dir, normal);
	return texture(env_spc_cube, reflected).rgb * env_spc_color * env_spc_intensity
		* gate * specular_sample * tint * specular_tint;
}
