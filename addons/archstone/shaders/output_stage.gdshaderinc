// DeS's per-material output stage: scattering atmosphere, then the exposure/tonemap transfer
// function, then per-channel tone correction (pipeline stages 8-10). In the material shader, not
// a post-process pass, because DeS puts it there too and Compatibility is RGBA8/LDR - clipped
// highlights can't be recovered later. See docs/ARCHITECTURE.md's rendering-pipeline section and
// docs/context.md parts 18-33.
//
// Uniform defaults are the neutral no-op case plus tonemapbank.paramdef's defaults; FlverLoader
// overrides them per placement from the FogID/ToneMapID/ToneCorrectID/ScatterID banks.

// FOG_BANK. fogBeginZ/fogEndZ are view-space distances; degRotW is a 0-1000 density dial
// (100 = normal, 0 = off); colA a percent-style colour multiplier. Wired but not applied - see
// des_output.
uniform vec3 fog_color : source_color = vec3(1.0);
uniform float fog_intensity = 1.0;
uniform float fog_begin = 0.0;
uniform float fog_end = 0.0;
uniform float fog_density = 0.0;

// LIGHT_SCATTERING_BANK (MSBD.Part.ScatterID). The dominant atmosphere term on most Nexus
// geometry - see des_scatter.
uniform float scatter_beta_ray = 0.0;
uniform float scatter_beta_mie = 0.0;
uniform float scatter_hg_g = 0.5;
uniform vec3 scatter_sun_color : source_color = vec3(1.0);
uniform float scatter_sun_intensity = 1.0;
uniform vec3 scatter_reflectance : source_color = vec3(1.0);
uniform vec3 scatter_sun_dir = vec3(0.0, 1.0, 0.0);
uniform float scatter_distance_mul = 1.0;
// Map-unit -> optical-depth conversion. Folded into c104 CPU-side, so it is unrecoverable and is
// the only knob here set from observation rather than read. Sets how far the in-scatter travels
// before approaching its ceiling: 3.5e-3 puts that at the ~150-220 m back of the visible Nexus
// while keeping the first few metres near zero. See docs/context.md part 33.
uniform float scatter_distance_scale = 0.0035;
uniform float scatter_inscatter_mul = 1.0;
// blendCoef/100. Strength dial on the scattering only (0 disables it), not a crossfade with fog.
uniform float scatter_blend = 0.0;

// TONE_MAP_BANK. tone_key is Reinhard's key (grayKeyValue); tone_adapted_lum stands in for the
// engine's temporally-adapted scene luminance - see des_tonemap.
uniform float tone_key = 0.18;
uniform float tone_adapted_lum = 0.1;

// TONE_CORRECT_BANK: per-channel brightness/contrast, then global saturation and hue rotation.
// Not `: source_color` despite being per-RGB - these are plain multipliers, and that hint's
// sRGB->linear conversion would bend an authored 0.8 into ~0.6.
uniform vec3 tone_brightness = vec3(1.0);
uniform vec3 tone_contrast = vec3(1.0);
uniform float tone_saturation = 1.0;
uniform float tone_hue_radians = 0.0;

float des_fog_distance(float view_depth) {
	return clamp((view_depth - fog_begin) / max(fog_end - fog_begin, 0.0001), 0.0, 1.0);
}

vec3 des_fog(vec3 color, float view_depth) {
	return mix(color, fog_color * fog_intensity, clamp(des_fog_distance(view_depth) * fog_density, 0.0, 1.0));
}

// Rayleigh extinction is wavelength-dependent (hence a vec3 transmittance). Weights normalized to
// mean 1 so lsBetaRay stays the mean rate. Exponent is lambda^-1.5, fit to the reference frame's
// measured in-scatter ceiling (B/R ~1.56) - since a scalar divide and the distance scale can't
// move a channel ratio, the ceiling hue is set by these weights alone. See docs/context.md
// parts 30-31.
const vec3 RAYLEIGH_WAVELENGTH_WEIGHTS = vec3(0.786, 0.957, 1.258);

// 3/(16*pi) and 1/(4*pi), the phase-function normalizers.
float rayleigh_phase(float cos_theta) {
	return 0.0596831 * (1.0 + cos_theta * cos_theta);
}

float henyey_greenstein_phase(float cos_theta, float g) {
	float g2 = g * g;
	return 0.0795775 * (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cos_theta, 0.0001), 1.5);
}

// Hoffman & Preetham real-time outdoor scattering, whose parameters LIGHT_SCATTERING_BANK names
// verbatim. Confirmed from the vertex bytecode: per-channel exp2() extinction plus an in-scatter
// colour, `result * tc8 + tc9` in the fragment epilogue.
//
// view_distance is the *radial* camera distance, used raw - clamping the exponent input to [0,1]
// saturates the curve immediately and kills the per-channel split that makes scattered light blue.
//
// The in-scatter sum is divided by the *scalar* (lsBetaRay + lsBetaMie) - H&P's normalization
// with the authored coefficients as scalars. The bytecode has no DIV, but it applies the phase
// functions raw, so the normalization is CPU-folded into c108/c109 and invisible to capture.
// Scalar, not the per-channel beta_total vector: per-channel cancels the wavelength split
// (rejected in part 27), scalar only rescales brightness. See docs/context.md part 33.
vec3 des_scatter(vec3 color, float view_distance, vec3 view_dir) {
	vec3 beta_ray = RAYLEIGH_WAVELENGTH_WEIGHTS * scatter_beta_ray;
	vec3 beta_total = beta_ray + vec3(scatter_beta_mie);
	// exp2, not exp - the real instruction is EX2, which folds a factor of ln2 into the scale.
	vec3 fex = exp2(-beta_total * view_distance * scatter_distance_mul * scatter_distance_scale);

	float cos_theta = dot(-view_dir, scatter_sun_dir);
	vec3 inscatter = (beta_ray * rayleigh_phase(cos_theta)
			+ scatter_beta_mie * henyey_greenstein_phase(cos_theta, scatter_hg_g))
		/ max(scatter_beta_ray + scatter_beta_mie, 0.0001)
		* scatter_sun_color * scatter_sun_intensity * scatter_reflectance * scatter_inscatter_mul;

	// blendCoef lerps transmittance 1 -> Fex and scales the in-scatter by the same factor
	// (`tc8 = 1 + (Fex * c105 - 1) * c110.w` in the vertex program).
	return color * mix(vec3(1.0), fex, scatter_blend) + inscatter * (1.0 - fex) * scatter_blend;
}

// Reinhard 2002, per channel because the engine applies it as DS_Fil_HDR, a 1D LUT. Folds DeS's
// exposure divide and LUT into one curve - the LUT is a tabulation of the same curve, so the
// exposure constant cancels. tone_adapted_lum is the one value not read from authored data
// (Compatibility can't read frame luminance back); FlverLoader passes the bank's minAdaptedLum.
vec3 des_tonemap(vec3 color) {
	vec3 scaled = color * (tone_key / max(tone_adapted_lum, 0.0001));
	return scaled / (1.0 + scaled);
}

// Rodrigues rotation about the grey axis - exact identity at hue 0, which every gameplay row is.
vec3 des_hue_rotate(vec3 color, float angle) {
	const vec3 grey_axis = vec3(0.57735);
	float c = cos(angle);
	return color * c + cross(grey_axis, color) * sin(angle) + grey_axis * dot(grey_axis, color) * (1.0 - c);
}

// Contrast as pivot-and-scale about 0.5 - not engine-accurate (the real curve is an unrecoverable
// baked LUT). This is the part-19 output stage, the composition closest to RPCS3's map-piece
// lighting colour: pivot lifts above 0.5 where pow(x, contrast) only darkens. Hazard: below a
// ~0.027 scene value channels clamp to black - kept in check by the scattering haze lifting
// shadowed geometry at player range. See docs/context.md parts 23/26/33.
vec3 des_tone_correct(vec3 color) {
	color *= tone_brightness;
	color = (color - vec3(0.5)) * tone_contrast + vec3(0.5);
	float luma = dot(color, vec3(0.299, 0.587, 0.114));
	color = mix(vec3(luma), color, tone_saturation);
	return clamp(des_hue_rotate(color, tone_hue_radians), 0.0, 1.0);
}

// view_depth is planar (FOG_BANK's fogBeginZ/fogEndZ are Z distances); view_distance is radial
// (scattering). Both passed in because INV_VIEW_MATRIX isn't readable from a helper function.
// des_fog() is not called - the captured corpus applies it in only 7 of 183 scattering-carrying
// programs, all unlit, so a blanket per-pixel lerp was wrong. See docs/context.md part 28.
vec3 des_output(vec3 color, float view_depth, float view_distance, vec3 view_dir) {
	return des_tone_correct(des_tonemap(des_scatter(color, view_distance, view_dir)));
}
