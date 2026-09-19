// Shared hemisphere-ambient + environment-cubemap terms (`albedo = diffuse * (hemi + envDif)`,
// envSpc to EMISSION) for the single-layer lightmap family and terrain_blend.gdshader. Structure
// follows a captured DeS HemEnv fragment program - a per-pixel normal from the bumpmap drives both
// the hemisphere blend and the env lookups. Uniform defaults are default_lightbank.param row 0,
// the fallback for an unresolved LightID. See docs/ARCHITECTURE.md's Lightmaps section and
// docs/context.md parts 16/17.

// MTD.LightingType, branched rather than compiled per permutation: 3 = HemEnv (map/lightmap,
// hemisphere + env cubemaps), 1 = HemDir3 (chr/parts/obj, hemisphere + three directional lights).
// Mutually exclusive - HemDir3 takes no env term (its name carries no "Env"), which also stops a
// lightmap-less material from getting env_diffuse at full strength off the white default.
const int LIGHTING_HEM_ENV = 0;
const int LIGHTING_HEM_DIR3 = 1;
uniform int lighting_model = 0;

// LIGHT_BANK's three directional lights and its separate specular-only directional. Directions are
// world-space unit vectors built C#-side from each light's degRotX/degRotY; colours premultiplied
// by colA. Plain vec3, not source_color (hint is a no-op under Compatibility and arrays can't
// carry it). Default zero, so an unbound material stays unlit rather than inventing a light.
uniform vec3 dir_light_colors[3];
uniform vec3 dir_light_directions[3];
uniform vec3 spec_light_color = vec3(0.0);
uniform vec3 spec_light_direction = vec3(0.0, 1.0, 0.0);
uniform float specular_power = 8.0;

// g_SpecularMapColor * g_SpecularMapColorPower - the HemEnv env-cubemap specular term's own
// material tint/intensity multiplier (Cs in the map-shading research pass's finding #2,
// MAP_SHADING_ACCURACY_2026_09_15.md - external research archive, not tracked in this repo).
// Read only by env_specular below - hemdir3_specular's chr/parts Phong lobe is a separate,
// unaudited specular model and doesn't take this. Defaults to white/no-op.
uniform vec3 specular_tint : source_color = vec3(1.0);

// MSBD.Events.Lights ("点光源" torch/campfire/candle events) via POINT_LIGHT_BANK - see
// docs/context.md part 52 for how PointLightID resolves to a bank row. The HemEnvPntS/PntSS/
// PntSSSS and HemDir3PntS/... shader variants confirm point lights reach both lighting models,
// up to 4 (DeS's own max compiled variant, PntSSSS). FlverLoader binds the nearest 4 lights
// within reach of each placement here, not a global per-map list; an unused slot's
// point_light_dwindle.y is 0, which the falloff below turns into zero contribution without
// needing a separate count uniform. Position is world-space (matches world_position); colour is
// colR/G/B/255 * colA/100, premultiplied C#-side like every other _BANK colour. Diffuse
// (Lambertian) only - no captured data pins down whether DeS's own PntS variants add a specular
// lobe from these too.
uniform vec3 point_light_pos[4];
uniform vec3 point_light_color[4];
// x = dwindleBegin (full intensity within), y = dwindleEnd (zero beyond) - POINT_LIGHT_BANK's own
// "減衰開始/終了距離[m]" (attenuation start/end distance). The exact falloff curve DeS uses between
// the two isn't captured; this is a linear ramp, the simplest curve consistent with those names.
uniform vec2 point_light_dwindle[4];

uniform vec3 ambient_up : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_up_intensity = 0.3;
uniform vec3 ambient_down : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_down_intensity = 0.3;

// LIGHT_BANK's "diffuse hemisphere" pair (colA_du/colA_dd) is deliberately NOT read - real data,
// 30x the ambient term on m01 row 0, but wired and reverted three times (each flattened the Nexus
// or washed it to beige). This is the part-19 composition: ambient hemisphere only, warmth from
// TONE_CORRECT_BANK. See docs/context.md parts 15/32/33 and .local-notes.md before re-adding.
uniform vec3 env_color : source_color = vec3(1.0, 1.0, 1.0);
// Real values run 1.5x-5x, left unclamped on purpose - a clamp was tried and reverted, see
// docs/ARCHITECTURE.md's Known deferred work.
uniform float env_intensity = 2.0;
// envSpc to EMISSION, not a PBR reflection - no reflection probe exists in this project.
uniform vec3 env_spc_color : source_color = vec3(1.0, 1.0, 1.0);
uniform float env_spc_intensity = 2.0;

// envDif / envSpc_0..3 cubemaps, resolved per placement from map/<mXX>/<mXX>_9999.tpf. A 1x1 white
// cubemap is bound when a name doesn't resolve, degrading these to flat lightmap-scaled behaviour.
uniform samplerCube env_dif_cube;
uniform samplerCube env_spc_cube;

// Set explicitly from C# (not left to a sampler default) so a material with no g_Bumpmap can't be
// mistaken for one whose normal points somewhere odd.
uniform bool use_normal_map = false;

// --- Sun shadow (v2: one STATIC orthographic depth pass over the map's casters, not yet the
// 4-split PSM). Driven by the map's SHADOW_BANK row via ShadowRenderer.cs (docs/context.md parts
// 39/49): shadow_light_clip = light_proj * light_view (world -> light clip; .xy/.w*0.5+0.5 is the
// atlas UV - .w is 1 under ortho, the code stays general for the 4-split perspective phase);
// shadow_light_view is light_view alone, so the receiver rebuilds the exact linear light-space
// distance shadow_depth.gdshader wrote (-z / far). shadow_strength stays 0 until ShadowRenderer
// binds a map. shadow_density (SHADOW_BANK.densityRatio/100) is how dark a full shadow gets - never
// fully black; shadow_tint (colR/G/B) lifts specific channels back; shadow_fade_begin/_range
// (fadeBeginDist/fadeDist) fade the shadow out with distance from the *viewer* - the only
// camera-dynamic part of the cast. Composition 1 - (density - tint)*fade*inShadow is verified
// against the DS_*_Sdw and Csd fragment disassembly (part 49).
uniform sampler2D shadow_map : hint_default_black; // empty texels read 0 -> "no occluder", see sun_shadow
uniform mat4 shadow_light_clip = mat4(1.0);
uniform mat4 shadow_light_view = mat4(1.0);
uniform float shadow_light_far = 1.0;
uniform float shadow_bias = 0.0015;
uniform float shadow_strength = 0.0;
uniform vec2 shadow_texel = vec2(0.00048828125); // 1 / 2048 atlas texel; ShadowRenderer sets it
uniform float shadow_density = 0.9;          // SHADOW_BANK.densityRatio / 100
uniform vec3 shadow_tint = vec3(0.0);        // SHADOW_BANK.colR/G/B / 255 (usually black)
uniform float shadow_fade_begin = 1000.0;    // SHADOW_BANK.fadeBeginDist
uniform float shadow_fade_range = 1.0;       // SHADOW_BANK.fadeDist

varying vec3 world_normal;
varying vec3 world_tangent;
varying vec3 world_binormal;
varying vec3 world_position;

// Explicit params, not the built-ins: MODEL_MATRIX isn't readable from a helper, and a varying
// can't be assigned from one - each including shader's vertex() must call this itself.
vec3 hemisphere_world_normal(vec3 normal, mat4 model_matrix) {
	return normalize((model_matrix * vec4(normal, 0.0)).xyz);
}

// Rebuild Z from XY - DeS's normal maps are two-channel.
vec3 decode_normal_map(vec2 sampled_xy) {
	vec2 n = sampled_xy * 2.0 - 1.0;
	return vec3(n, sqrt(clamp(1.0 - dot(n, n), 0.0, 1.0)));
}

// Tangent-space normal -> world via the interpolated TBN; plain vertex normal when no bumpmap.
// front_facing-flipped so a double-sided material (cull_disabled - see lightmap_double_sided.gdshader)
// shades its back face with a normal pointing at the camera instead of the mesh's single authored
// front-face direction. A no-op on every cull_back shader sharing this file: front-facing is always
// true there, since a back face never reaches the fragment stage to begin with. Takes FRONT_FACING
// as a parameter rather than reading the built-in directly - it isn't visible from this include
// when called through lightmap_shade (a second level of helper function), the same reason view_dir/
// MODEL_MATRIX are threaded through as parameters everywhere else in this file.
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

// Blended by per-pixel normal.y, untouched by the lightmap. Often near-constant in real data
// (up and down colours set equal), so it is not where surface relief comes from - env_diffuse is.
vec3 hemisphere_ambient(vec3 normal) {
	return mix(ambient_down * ambient_down_intensity, ambient_up * ambient_up_intensity, normal.y * 0.5 + 0.5);
}

// Lambertian N.L over three directional lights - the captured HemDir3 bytecode does three, never
// one or two.
vec3 hemdir3_diffuse(vec3 normal) {
	vec3 total = vec3(0.0);
	for (int i = 0; i < 3; i++) {
		total += dir_light_colors[i] * max(dot(normal, dir_light_directions[i]), 0.0);
	}
	return total;
}

// Phong (reflect.L), one lobe per directional light - the HemDir3 bytecode has no half-vector.
// Weighted by the single spec-only colour (colA_s) and g_SpecularPower directly - nothing here
// feeds Godot's PBR, so no GGX roughness conversion. spec_light_direction is unused by this
// variant, kept for the HemDir3Pnt* family. See docs/context.md part 34 for the disassembly.
vec3 hemdir3_specular(vec3 normal, vec3 view_dir, vec3 specular_sample, vec3 tint) {
	vec3 reflected = reflect(-view_dir, normal);
	float lobe = 0.0;
	for (int i = 0; i < 3; i++) {
		lobe += pow(max(dot(reflected, dir_light_directions[i]), 0.0), specular_power);
	}
	return spec_light_color * lobe * specular_sample * tint;
}

// Lambertian N.L over up to 4 nearby point lights, linearly attenuated between dwindleBegin/End.
// Not gated by sun_shadow - a torch isn't occluded by the sun's shadow map, it's a separate light
// source with no shadow casting of its own in this pass.
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

// Per-channel shadow multiplier for the env/directional term: vec3(1.0) = lit, darkening toward
// vec3(1.0) - (shadow_density - shadow_tint) where fully shadowed, faded out by camera distance.
// Returns vec3(1.0) (no effect) when the term is disabled, the fragment is outside the rendered
// region, or the whole thing has faded - DeS's gate is min(shadow, lightmap) and a fragment with
// no shadow information must not be treated as occluded. An empty (transparent-cleared) atlas
// texel reads 0.0 and is likewise "no occluder". `view_distance` is the fragment->camera range.
// A rotated 12-tap Poisson disk. DeS does one hardware 2x2 bilinear TXPR on a true depth sampler;
// our depth is linear distance packed 16-bit across R,G on an RGBA8 target, which can't be hardware-
// filtered, so we spread more point taps over a few texels and rotate the disk per fragment to
// trade edge banding for stable noise (the rotation is hashed from world_pos, so it doesn't shimmer
// on a static scene).
const vec2 SHADOW_POISSON_12[12] = {
	vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696, 0.457), vec2(-0.203, 0.621),
	vec2(0.962, -0.195), vec2(0.473, -0.480), vec2(0.519, 0.767), vec2(0.185, -0.893),
	vec2(0.507, 0.064), vec2(0.896, 0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598)
};
const float SHADOW_PCF_RADIUS = 3.5; // texels (atlas is 2048^2, so this is still a modest penumbra)

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
	// Disasm form: shadowTerm = 1 - (density - tint) * fade * inShadow, per channel.
	float in_shadow = (1.0 - lit / 12.0) * fade * shadow_strength;
	return vec3(1.0) - max(vec3(shadow_density) - shadow_tint, vec3(0.0)) * in_shadow;
}

// Irradiance lookup by the normal itself (not a reflection vector) - the term that carries
// per-pixel relief, since it varies with the normal map at full texture frequency. `gate` is the
// engine's min(shadow, lightmap): the lightmap alone where there is no shadow map, both once
// ShadowRenderer binds one.
vec3 env_diffuse(vec3 normal, vec3 gate) {
	return texture(env_dif_cube, normal).rgb * env_color * env_intensity * gate;
}

// Reflection-vector lookup off the per-pixel normal. The HemEnv bytecode weights this by the
// RGB specular sample (not just red) and by vertex colour; `tint` is vec3(1.0) from callers
// whose vertex-colour channel isn't a free multiplier - terrain_blend's is the layer-blend
// weight. See docs/context.md part 34. `specular_tint` (g_SpecularMapColor*Power, uniform above)
// is the missing material-level factor found in the map-shading research pass's finding #2
// (MAP_SHADING_ACCURACY_2026_09_15.md - external research archive, not tracked in this repo)
// - previously this term had no material specular scaling at all.
vec3 env_specular(vec3 normal, vec3 view_dir, vec3 gate, vec3 specular_sample, vec3 tint) {
	vec3 reflected = reflect(-view_dir, normal);
	return texture(env_spc_cube, reflected).rgb * env_spc_color * env_spc_intensity
		* gate * specular_sample * tint * specular_tint;
}
