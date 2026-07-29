// Shared hemisphere-ambient + lightmap-scaled-env term - the DSR-shader-shape fix described
// in CLAUDE.md's Lightmaps section (`albedo = diffuse * (hemi + env)`, replacing a straight
// diffuse*lightmap multiply). Used by both the single-layer lightmap shader family
// (lightmap_common.gdshaderinc) and terrain_blend.gdshader - factored out once both needed it
// rather than duplicated a second time. Uniform defaults are default_lightbank.param's real
// row 0 (read via SoulsFormats.PARAM/PARAMDEF against the mounted game data, not invented) -
// see context.md's "Lightmap/drawparam system, part 6". A global placeholder until per-part
// MSB LightID resolution exists (see CLAUDE.md's "Known deferred work") - not the final answer.
uniform vec3 ambient_up : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_up_intensity = 0.3;
uniform vec3 ambient_down : source_color = vec3(1.0, 1.0, 1.0);
uniform float ambient_down_intensity = 0.3;
uniform vec3 env_color : source_color = vec3(1.0, 1.0, 1.0);
uniform float env_intensity = 2.0;
// LIGHT_BANK's envDif/envSpc are two separate environment terms - the DSR source keeps a
// diffuse env-reflection component (env_color/env_intensity above, into ALBEDO) distinct from
// a specular one. No real reflection probe exists in this project (same gap water.gdshader's
// own EMISSION-based reflection hack already works around), so env_spc is written to EMISSION
// too, weighted by the material's own specular texture rather than treated as a physically
// integrated PBR reflection.
uniform vec3 env_spc_color : source_color = vec3(1.0, 1.0, 1.0);
uniform float env_spc_intensity = 2.0;

varying vec3 world_normal;

// Returns rather than assigning the varying directly, and takes NORMAL/MODEL_MATRIX as
// explicit params rather than reading the built-ins directly - both confirmed the hard way
// (real shader-compile errors under a non-dummy rendering driver) to be required: a varying
// can't be assigned from a helper function, and MODEL_MATRIX (unlike NORMAL) isn't even
// visible for reading from one. Each including shader's own vertex() must do
// `world_normal = hemisphere_world_normal(NORMAL, MODEL_MATRIX);` itself.
vec3 hemisphere_world_normal(vec3 normal, mat4 model_matrix) {
	return normalize((model_matrix * vec4(normal, 0.0)).xyz);
}

// Blended by world-space normal.y, same as the two-color hemisphere-ambient term in decompiled
// FRPG_FS_HemEnv.fx (see CLAUDE.md) - untouched by the lightmap, unlike the old multiply.
vec3 hemisphere_ambient() {
	return mix(ambient_down * ambient_down_intensity, ambient_up * ambient_up_intensity, world_normal.y * 0.5 + 0.5);
}
