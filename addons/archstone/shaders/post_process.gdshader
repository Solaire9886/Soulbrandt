shader_type canvas_item;
render_mode blend_mix;

// DeS's frame post-process chain (the DS_Fil_* fragment programs), one stage per SubViewport of
// PostProcessPipeline. Taps, weights and constants are the ones a Nexus frame capture carries;
// the chain and its render-target formats are recorded in ELF_ENGINE_ACCURACY_RESEARCH.md 13
// (external research archive). Every stage but STAGE_ADAPT writes alpha 1, so blend_mix is a
// plain overwrite there; STAGE_ADAPT uses it to blend toward the target exposure.

const int STAGE_COPY = 0;          // one bilinear tap: the 4:1 and 2:1 scene downsamples
const int STAGE_LUM_INITIAL = 1;   // DS_Fil_SampleLumInitial
const int STAGE_DOWNSCALE_4X4 = 2; // DS_Fil_DownScale4x4
const int STAGE_LUM_FINAL = 3;     // DS_Fil_SampleLumFinal
const int STAGE_ADAPT = 4;         // DS_Fil_CalcAdaptedLum
const int STAGE_BRIGHT_PASS = 5;   // DS_Fil_BrightPassFilter
const int STAGE_GAUSS_5X5 = 6;     // DS_Fil_GaussBlur5x5
const int STAGE_BLOOM = 7;         // DS_Fil_Bloom, one axis per pass

uniform int stage;
uniform sampler2D source_nearest : filter_nearest, repeat_disable;
uniform sampler2D source_bilinear : filter_linear, repeat_disable;
// STAGE_ADAPT: the frame's TONE_MAP_BANK row, and adaptSpeed * frame time in seconds.
uniform float gray_key = 0.18;
uniform float min_adapted_lum = 0.1;
uniform float max_adapted_lum = 0.2;
uniform float adapt_amount = 0.0;
// STAGE_BRIGHT_PASS: bloomBegin / 100.
uniform float bloom_threshold = 0.5;
// STAGE_BLOOM: one texel along the blur axis.
uniform vec2 bloom_step = vec2(0.0);

global uniform sampler2D des_exposure_map : filter_nearest;

const vec3 LUMINANCE_WEIGHTS = vec3(0.2125, 0.7154, 0.0721);

// log2 of a 3x3 bilinear neighbourhood's luminance, rescaled by 2/E out of the scene buffer's
// saturate(colour * E / 2) encoding, plus 0.0001; the sum times ln(2)/9 is the natural-log mean.
float lum_initial(vec2 uv, vec2 texel) {
	float scale = 2.0 * (1.0 / texelFetch(des_exposure_map, ivec2(0), 0).r);
	float sum = 0.0;
	for (int y = -1; y <= 1; y++)
		for (int x = -1; x <= 1; x++)
			sum += log2(dot(texture(source_bilinear, uv + vec2(float(x), float(y)) * texel).rgb, LUMINANCE_WEIGHTS) * scale + 0.0001);
	return sum * 0.07701635;
}

// A 4x4 box at the texel centres around a 4:1 output texel.
vec4 downscale_4x4(vec2 uv, vec2 texel) {
	vec4 sum = vec4(0.0);
	for (int y = 0; y < 4; y++)
		for (int x = 0; x < 4; x++)
			sum += texture(source_nearest, uv + (vec2(float(x), float(y)) - 1.5) * texel);
	return sum * 0.0625;
}

// exp of the mean natural log over the 4x4 source.
float lum_final() {
	float sum = 0.0;
	for (int y = 0; y < 4; y++)
		for (int x = 0; x < 4; x++)
			sum += texelFetch(source_nearest, ivec2(x, y), 0).r;
	return exp2(sum * 0.0625 * 1.4426950);
}

// The 13-tap 5x5 Gaussian (rho 1), normalised over its taps.
vec3 gauss_5x5(vec2 uv, vec2 texel) {
	vec3 sum = texture(source_nearest, uv).rgb * 0.18385795;
	sum += (texture(source_nearest, uv + vec2(-1.0, 0.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(1.0, 0.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(0.0, -1.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(0.0, 1.0) * texel).rgb) * 0.11151548;
	sum += (texture(source_nearest, uv + vec2(-1.0, -1.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(-1.0, 1.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(1.0, -1.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(1.0, 1.0) * texel).rgb) * 0.06763755;
	sum += (texture(source_nearest, uv + vec2(-2.0, 0.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(2.0, 0.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(0.0, -2.0) * texel).rgb
		+ texture(source_nearest, uv + vec2(0.0, 2.0) * texel).rgb) * 0.024882466;
	return sum;
}

// 15 taps of a sigma-3 Gaussian at twice unit weight (the kernel sums to 1.976); the RGBA8
// target clamps the result, as the engine's does.
vec3 bloom(vec2 uv) {
	const float weights[8] = float[8](0.2659615, 0.25158882, 0.21296534, 0.16131382,
		0.10934004, 0.0663181, 0.035993975, 0.017481254);
	vec3 sum = texture(source_nearest, uv).rgb * weights[0];
	for (int i = 1; i < 8; i++)
		sum += (texture(source_nearest, uv + float(i) * bloom_step).rgb
			+ texture(source_nearest, uv - float(i) * bloom_step).rgb) * weights[i];
	return sum;
}

void fragment() {
	vec2 texel_nearest = 1.0 / vec2(textureSize(source_nearest, 0));
	vec2 texel_bilinear = 1.0 / vec2(textureSize(source_bilinear, 0));
	if (stage == STAGE_COPY) {
		COLOR = vec4(texture(source_bilinear, UV).rgb, 1.0);
	} else if (stage == STAGE_LUM_INITIAL) {
		COLOR = vec4(vec3(lum_initial(UV, texel_bilinear)), 1.0);
	} else if (stage == STAGE_DOWNSCALE_4X4) {
		COLOR = vec4(downscale_4x4(UV, texel_nearest).rgb, 1.0);
	} else if (stage == STAGE_LUM_FINAL) {
		COLOR = vec4(vec3(lum_final()), 1.0);
	} else if (stage == STAGE_ADAPT) {
		// E += (grayKey / (clamp(L, min, max) + 0.001) - E) * (1 - 0.98^(30 * adaptSpeed * dt)),
		// as alpha blending into the never-cleared exposure target. -0.87438947 = 30 * log2(0.98).
		float lum = clamp(texelFetch(source_nearest, ivec2(0), 0).r, min_adapted_lum, max_adapted_lum);
		float target = gray_key / (lum + 0.001);
		COLOR = vec4(vec3(target), 1.0 - exp2(-0.87438947 * adapt_amount));
	} else if (stage == STAGE_BRIGHT_PASS) {
		vec3 c = texture(source_nearest, UV).rgb;
		COLOR = vec4(max(c - bloom_threshold, 0.0) / (1.0 - bloom_threshold), 1.0);
	} else if (stage == STAGE_GAUSS_5X5) {
		COLOR = vec4(gauss_5x5(UV, texel_nearest), 1.0);
	} else {
		COLOR = vec4(bloom(UV), 1.0);
	}
}
