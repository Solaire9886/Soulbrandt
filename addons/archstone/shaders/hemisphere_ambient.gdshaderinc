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
vec3 hemisphere_pixel_normal(vec3 tangent_space_normal) {
	if (!use_normal_map) {
		return normalize(world_normal);
	}
	return normalize(tangent_space_normal.x * world_tangent
		+ tangent_space_normal.y * world_binormal
		+ tangent_space_normal.z * world_normal);
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

// Irradiance lookup by the normal itself (not a reflection vector) - the term that carries
// per-pixel relief, since it varies with the normal map at full texture frequency. `gate` is the
// lightmap: the engine gates by min(shadow, lightmap) and this project has no shadow map.
vec3 env_diffuse(vec3 normal, vec3 gate) {
	return texture(env_dif_cube, normal).rgb * env_color * env_intensity * gate;
}

// Reflection-vector lookup off the per-pixel normal. The HemEnv bytecode weights this by the
// RGB specular sample (not just red) and by vertex colour; `tint` is vec3(1.0) from callers
// whose vertex-colour channel isn't a free multiplier - terrain_blend's is the layer-blend
// weight. See docs/context.md part 34.
vec3 env_specular(vec3 normal, vec3 view_dir, vec3 gate, vec3 specular_sample, vec3 tint) {
	vec3 reflected = reflect(-view_dir, normal);
	return texture(env_spc_cube, reflected).rgb * env_spc_color * env_spc_intensity
		* gate * specular_sample * tint;
}
