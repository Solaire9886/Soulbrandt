// Shared hemisphere-ambient + lightmap-scaled-env term (`albedo = diffuse * (hemi + env)`) -
// see docs/ARCHITECTURE.md's "Lightmaps" section. Shared by the single-layer lightmap family
// and terrain_blend.gdshader. Uniform defaults are default_lightbank.param's real row 0, the
// fallback for an unresolved LightID - real per-part MSB LightID resolution overrides these
// per-instance (FlverLoader.InstantiateMap's ApplyLightBank).
uniform vec3 ambient_up : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_up_intensity = 0.3;
uniform vec3 ambient_down : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_down_intensity = 0.3;
uniform vec3 env_color : source_color = vec3(1.0, 1.0, 1.0);
// Real values run 1.5x-5x and are left unclamped on purpose - see docs/ARCHITECTURE.md's
// "Known deferred work" before attempting another clamp here; one was already tried and
// reverted.
uniform float env_intensity = 2.0;
// LIGHT_BANK's envSpc is a separate specular-env term. No real reflection probe exists in
// this project, so it's written to EMISSION too (same as water.gdshader), not treated as a
// physically integrated PBR reflection.
uniform vec3 env_spc_color : source_color = vec3(1.0, 1.0, 1.0);
uniform float env_spc_intensity = 2.0;

varying vec3 world_normal;

// Takes NORMAL/MODEL_MATRIX as explicit params rather than reading the built-ins directly -
// MODEL_MATRIX (unlike NORMAL) isn't visible for reading from a helper function. Each
// including shader's own vertex() must call `world_normal = hemisphere_world_normal(...)`
// itself - a varying can't be assigned from a helper function either.
vec3 hemisphere_world_normal(vec3 normal, mat4 model_matrix) {
	return normalize((model_matrix * vec4(normal, 0.0)).xyz);
}

// Blended by world-space normal.y - untouched by the lightmap, unlike the old multiply.
vec3 hemisphere_ambient() {
	return mix(ambient_down * ambient_down_intensity, ambient_up * ambient_up_intensity, world_normal.y * 0.5 + 0.5);
}
