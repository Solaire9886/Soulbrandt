// The per-material output stage: FOG_BANK fade, scattering, exposure and TONE_CORRECT_BANK. It
// lives in the material shader, as in DeS; Compatibility's RGBA8 target could not recover clipped
// highlights later. See docs/ARCHITECTURE.md, "Output stage".
//
// The atmosphere is evaluated per vertex like the native vertex program: each entry point calls
// DES_ATMOSPHERE_VERTEX(world_position) in vertex() and des_output(color) in fragment().
// Equations and constants: external ELF_ENGINE_ACCURACY_RESEARCH.md section 12.
//
// Exposure and tone correction are frame-global uniforms bound by PostProcessPipeline. For its
// measurement camera (DES_MEASURE_LAYER) the stage writes the game's scene-buffer value
// saturate(colour * E / 2), which the luminance and bloom stages read.
//
// Defaults are neutral: scatter_blend 0 and fog_weight_scale 0 leave the colour unchanged.

// LIGHT_SCATTERING_BANK (Part.ScatterID) as the engine's vertex constants c104..c111, built by
// FlverLoader.ApplyScatterBank exactly as the engine builds them.
uniform vec3 scatter_extinction = vec3(0.0);             // c104: total extinction per metre
uniform vec3 scatter_reflectance = vec3(1.0);            // c105.xyz: reflectance RGB * A
uniform float scatter_inscatter_mul = 1.0;               // c105.w: inscatteringMul / 100
uniform vec3 scatter_phase = vec3(1.0, 1.0, 0.0);        // c107: (1 - g^2, 1 + g, 2g)
uniform vec3 scatter_rayleigh = vec3(0.0);               // c108: angular Rayleigh coefficient
uniform vec3 scatter_mie = vec3(0.0);                    // c109: angular Mie coefficient
uniform vec3 scatter_sun_color = vec3(0.0);              // c110.xyz: sun RGB * A
uniform float scatter_blend = 0.0;                       // c110.w: blendCoef / 100
uniform vec3 scatter_light_dir = vec3(0.0, -1.0, 0.0);   // c111.xyz: sunlight travel direction
uniform float scatter_distance_mul = 1.0;                // c111.w: distanceMul / 100

// FOG_BANK (Part.FogID): ramp (clip.w - fogBeginZ) / (fogEndZ - fogBeginZ) on planar depth; mix
// toward the bank colour by saturate(ramp) * degRotW/100, the product unclamped (m02 authors
// degRotW 200).
uniform vec3 fog_color : source_color = vec3(0.0);   // FOG_BANK.col/255 * (colA/100)
uniform float fog_weight_scale = 0.0;                // FOG_BANK.degRotW / 100
uniform float fog_begin = 0.0;                       // FOG_BANK.fogBeginZ
uniform float fog_end = 1.0;                         // FOG_BANK.fogEndZ

// The adapted exposure E (a 1x1 float texture, or a fixed fallback without a map) and the frame's
// TONE_CORRECT_BANK row. Declared in project.godot.
global uniform sampler2D des_exposure_map : filter_nearest;
global uniform vec3 des_tone_brightness;
global uniform vec3 des_tone_contrast;
global uniform float des_tone_saturation;
global uniform float des_tone_hue_radians;

// Visual layer 21, outside every editor camera's mask: set only on the measurement camera.
const uint DES_MEASURE_LAYER = 1u << 20u;

// c466.y and c466.z of every scattering vertex program: log2(e), applied twice before EX2.
const float SCATTER_EXPONENT_SCALE = 1.442695 * 1.442695;

varying vec3 des_transmittance; // TEX8: multiplies the shaded colour
varying vec3 des_inscatter;     // TEX9: added after it
varying float des_fog_ramp;     // TEX2.w: unclamped FOG_BANK ramp

struct DesAtmosphere {
	vec3 transmittance;
	vec3 inscatter;
	float fog_ramp;
};

// Hoffman-Preetham scattering as the native vertex program evaluates it: d radial; cos_theta
// between the vertex-to-camera direction and the sunlight travel direction; the engine's
// Henyey-Greenstein denominator is (1 + g) - 2g cos_theta; the 3/(16 pi) and 1/(4 pi) factors are
// folded into c108/c109; the in-scatter is divided per channel by the extinction (c106 = 1/c104).
DesAtmosphere des_atmosphere(vec3 vertex_world, vec3 camera_position, float view_depth) {
	DesAtmosphere a;
	vec3 to_camera = camera_position - vertex_world;
	float d = length(to_camera);
	float cos_theta = dot(to_camera / max(d, 1e-5), scatter_light_dir);
	vec3 fex = exp2(-scatter_extinction * d * scatter_distance_mul * SCATTER_EXPONENT_SCALE);
	float hg = scatter_phase.x * pow(max(scatter_phase.y - cos_theta * scatter_phase.z, 1e-10), -1.5);
	vec3 phase = (1.0 + cos_theta * cos_theta) * scatter_rayleigh + hg * scatter_mie;
	a.transmittance = 1.0 + (fex * scatter_reflectance - 1.0) * scatter_blend;
	a.inscatter = phase * (1.0 - fex) / max(scatter_extinction, vec3(1e-20))
		* scatter_inscatter_mul * scatter_sun_color * scatter_blend;
	a.fog_ramp = (view_depth - fog_begin) / max(fog_end - fog_begin, 0.001);
	return a;
}

// Expanded inside an entry point's vertex(): the only place Godot lets varyings be written.
#define DES_ATMOSPHERE_VERTEX(position) { DesAtmosphere des_a = des_atmosphere(position, INV_VIEW_MATRIX[3].xyz, -(VIEW_MATRIX * vec4(position, 1.0)).z); des_transmittance = des_a.transmittance; des_inscatter = des_a.inscatter; des_fog_ramp = des_a.fog_ramp; }

vec3 des_fog(vec3 color) {
	return mix(color, fog_color, clamp(des_fog_ramp, 0.0, 1.0) * fog_weight_scale);
}

// Tone reproduction is linear (no curve, no LUT): the geometry pass writes saturate(colour * E / 2)
// and DS_Fil_HDR_ColAdj reads it back as saturate(buffer * 2), adds the bloom and applies the
// TONE_CORRECT_BANK matrix. For display this stage writes saturate(colour * E) through the matrix;
// PostProcessPipeline adds the bloom.
float des_exposure() {
	return texelFetch(des_exposure_map, ivec2(0), 0).r;
}

vec3 des_tonemap(vec3 color) {
	return clamp(color * des_exposure(), 0.0, 1.0);
}

// Rodrigues rotation about the grey axis - exact identity at hue 0, which every gameplay row is.
vec3 des_hue_rotate(vec3 color, float angle) {
	const vec3 grey_axis = vec3(0.57735);
	float c = cos(angle);
	return color * c + cross(grey_axis, color) * sin(angle) + grey_axis * dot(grey_axis, color) * (1.0 - c);
}

// TONE_CORRECT_BANK as DS_Fil_HDR_ColAdj's matrix: M = S(sat) * diag(contrast) * diag(brightness)
// with Rec.601 luma, offset 0.5 * (1 - contrast), then a grey-axis hue rotation. Reproduces a
// captured m01 matrix to measurement precision. Unclamped; des_tone_correct clamps once.
vec3 des_tone_matrix(vec3 color) {
	color *= des_tone_brightness;
	color = (color - vec3(0.5)) * des_tone_contrast + vec3(0.5);
	float luma = dot(color, vec3(0.299, 0.587, 0.114));
	color = mix(vec3(luma), color, des_tone_saturation);
	return des_hue_rotate(color, des_tone_hue_radians);
}

vec3 des_tone_correct(vec3 color) {
	return clamp(des_tone_matrix(color), 0.0, 1.0);
}

// `value` is twice what the engine's fragment program writes to the scene buffer - the value
// DS_Fil_HDR_ColAdj reads back as saturate(buffer * 2). For PostProcessPipeline's measurement
// camera this writes the buffer itself; for display, the tone-corrected result.
vec3 des_encode(vec3 value, bool measure) {
	return measure ? clamp(value * 0.5, 0.0, 1.0) : des_tone_correct(clamp(value, 0.0, 1.0));
}

// Inverse of des_encode followed by the exposure: a scene-texture colour back to scene radiance, as
// DS_Water_Env decodes its refraction tap. Values des_encode clamped stay clamped.
vec3 des_decode(vec3 encoded, bool measure) {
	vec3 c = encoded * 2.0;
	if (!measure) {
		c = des_hue_rotate(encoded, -des_tone_hue_radians);
		float luma = dot(c, vec3(0.299, 0.587, 0.114));
		c = vec3(luma) + (c - vec3(luma)) / max(des_tone_saturation, 1e-3);
		c = (c - vec3(0.5)) / max(des_tone_contrast, vec3(1e-3)) + vec3(0.5);
		c /= max(des_tone_brightness, vec3(1e-3));
	}
	return c / max(des_exposure(), 1e-6);
}

// The same for an additive or subtractive draw. The engine applies the tone-correction matrix
// once, to the blended buffer: M(a + b) = M(a) + (M(b) - M(0)), so a term blended onto an
// already-corrected colour carries the matrix's linear part only, not its offset a second time.
vec3 des_encode_additive(vec3 value, bool measure) {
	vec3 v = clamp(value, 0.0, 1.0);
	return measure ? clamp(value * 0.5, 0.0, 1.0) : des_tone_matrix(v) - des_tone_matrix(vec3(0.0));
}

// The exposure epilogue on an already-fogged, already-scattered colour: saturate(colour * E / 2).
vec3 des_exposed(vec3 color, bool measure) {
	return des_encode(color * des_exposure(), measure);
}

// Order matches the HemEnv fragment epilogue: FOG_BANK fade, fma(colour, TEX8, TEX9), exposure.
vec3 des_atmosphere_applied(vec3 color) {
	return des_fog(color) * des_transmittance + des_inscatter;
}

vec3 des_output_stage(vec3 color, bool measure) {
	return des_exposed(des_atmosphere_applied(color), measure);
}

// A macro so CAMERA_VISIBLE_LAYERS, a fragment() built-in, is read in the entry point's scope.
#define DES_MEASURING ((CAMERA_VISIBLE_LAYERS & DES_MEASURE_LAYER) != 0u)
#define des_output(color) des_output_stage(color, DES_MEASURING)
#define des_output_additive(color) des_encode_additive(des_atmosphere_applied(color) * des_exposure(), DES_MEASURING)
