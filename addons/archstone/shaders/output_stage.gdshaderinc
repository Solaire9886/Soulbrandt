// DeS's per-material output stage: scattering atmosphere, then the exposure/tonemap transfer
// function, then per-channel tone correction (pipeline stages 8-10). In the material shader, not
// a post-process pass, because DeS puts it there too and Compatibility is RGBA8/LDR - clipped
// highlights can't be recovered later. See docs/ARCHITECTURE.md's rendering-pipeline section and
// docs/context.md parts 18-33.
//
// Uniform defaults are the neutral no-op case plus tonemapbank.paramdef's defaults; FlverLoader
// overrides them per placement from the ToneMapID/ToneCorrectID/ScatterID banks.
//
// FOG_BANK is deliberately not represented here. It describes RSX fixed-function fog, and a
// frame-capture sweep (docs/context.md part 36) proved Demon's Souls never uses that path -
// SET_FOG_PARAMS is never issued and no shader in the corpus reads f[FOGC]. The atmosphere on
// every outdoor surface is the scattering term below. (A separate hand-rolled per-material
// distance ramp exists in ~7 of 183 scattering shaders; its constants are still unrecovered.)

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
// Map-unit -> optical-depth conversion (the last fitted knob in this file). Sets how far the
// in-scatter travels before approaching its ceiling: 3.5e-3 puts that at the ~150-220 m back of
// the visible Nexus. The real per-vertex extinction rate is the captured `c104` (docs/context.md
// part 46) times a VP distance-factor chain that is not yet fully traced - a re-fit against it,
// with planar camera-Z instead of the radial distance used here (part 48), is the remaining
// scatter accuracy gap. See docs/context.md parts 33, 46-48.
uniform float scatter_distance_scale = 0.0035;
uniform float scatter_inscatter_mul = 1.0;
// blendCoef/100. Strength dial on the scattering only (0 disables it), not a crossfade with fog.
uniform float scatter_blend = 0.0;

// FOG_BANK (MSBD.Part.FogID) - a hand-rolled per-material distance fade toward the bank's colour,
// applied in the fragment epilogue *before* the scattering composite. NOT RSX fixed-function fog
// (SET_FOG_PARAMS is never issued - docs/context.md part 36); this is the `mix()` two instructions
// before `result*tc8 + tc9` in every HemEnv fragment program. Verified against FOG_BANK on
// m01/m02/m03/m06 (part 45): the shader's inline colour == col/255 * (colA/100) exactly, and the
// per-vertex weight's scale == degRotW/100 exactly. fog_weight_scale defaults to 0 -> des_fog is a
// no-op for water / standalone models with no bank bound (and for m06, whose degRotW is 0).
uniform vec3 fog_color : source_color = vec3(0.0);   // FOG_BANK.col/255 * (colA/100)
uniform float fog_weight_scale = 0.0;                // FOG_BANK.degRotW / 100
uniform float fog_begin = 0.0;                       // FOG_BANK.fogBeginZ
uniform float fog_end = 1.0;                         // FOG_BANK.fogEndZ

// TONE_MAP_BANK. tone_key is grayKeyValue; tone_adapted_lum stands in for the engine's
// temporally-adapted scene luminance (FlverLoader binds clamp(0.10, minAdaptedLum, maxAdapredLum)).
// Their ratio is the per-frame exposure E applied in des_tonemap - see there.
uniform float tone_key = 0.18;
uniform float tone_adapted_lum = 0.1;

// TONE_CORRECT_BANK: per-channel brightness/contrast, then global saturation and hue rotation.
// Not `: source_color` despite being per-RGB - these are plain multipliers, and that hint's
// sRGB->linear conversion would bend an authored 0.8 into ~0.6.
uniform vec3 tone_brightness = vec3(1.0);
uniform vec3 tone_contrast = vec3(1.0);
uniform float tone_saturation = 1.0;
uniform float tone_hue_radians = 0.0;

// Per-channel scattering coefficients: both terms are wavelength-dependent in DeS. Weights are the
// c109 (Rayleigh) and c108 (Mie) vertex-constant channel ratios measured from RSX frame captures
// across the Nexus, Boletaria, Stonefang and Shrine - stable to 3 sig figs in every area (Rayleigh
// B/R 1.87, Mie B/R 3.50), so they are the engine's real values, not a screenshot fit. Each is
// mean-normalized so lsBetaRay/lsBetaMie stay the mean rate and this is a hue correction only. The
// prior Rayleigh weight was lambda^-1.5 fit to a *tonemapped* reference (B/R ~1.56, compressed);
// the pre-tonemap coefficient is steeper. Mie had no weight before. See docs/context.md part 35.
const vec3 RAYLEIGH_WAVELENGTH_WEIGHTS = vec3(0.719, 0.935, 1.347);
const vec3 MIE_WAVELENGTH_WEIGHTS = vec3(0.485, 0.818, 1.697);

// The EXTINCTION coefficient (drives fex and the in-scatter 1/beta normalisation) runs bluer than
// the scattering coefficients above: the captured c104 is B/R ~2.5-2.75 on m01/m02 vs ~2.0 for the
// Rayleigh/Mie mix, i.e. there is wavelength-dependent absorption stacked on top of scattering.
// Mean-normalised average of c104 from the m01 and m02 frame captures (docs/context.md part 47).
// Using it makes distant geometry shed blue faster - so a blue-violet FOG_BANK tint (m01's
// (0.212,0.196,0.471)) washes back toward the surface's own warm lighting instead of staying violet.
const vec3 EXTINCTION_WAVELENGTH_WEIGHTS = vec3(0.59, 0.87, 1.54);

// 3/(16*pi) and 1/(4*pi), the phase-function normalizers.
float rayleigh_phase(float cos_theta) {
	return 0.0596831 * (1.0 + cos_theta * cos_theta);
}

float henyey_greenstein_phase(float cos_theta, float g) {
	float g2 = g * g;
	return 0.0795775 * (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cos_theta, 0.0001), 1.5);
}

// Hoffman & Preetham real-time outdoor scattering. Structure and the normalisations are read from
// the vertex program's captured per-draw constants (docs/context.md parts 46-47):
//   fex (extinction)    = exp2(-beta_ext * distance ...)          beta_ext ratio = c104
//   tc8 (transmittance) = (1 - blend) + blend * fex * reflectance             reflectance = c105
//   inscatter colour    = (phase_ray*beta_ray + phase_mie*beta_mie) / beta_ext   (c106 == 1/c104,
//                         verified to 4 sig figs on all three channels of m01/m02/m03 - a
//                         PER-CHANNEL divide, not the scalar 1/(lsBetaRay+lsBetaMie) used before).
//                         c104 is blue-heavy (absorption), so 1/c104 is red-heavy and the
//                         normalised in-scatter is slightly warm - the grey/olive haze the RPCS3
//                         reference shows, not blue. Part 35's "scalar fold / stays blue" reading
//                         was calibrated to a visual expectation, not to c106.
//   tc9 (in-scatter)    = inscatter_colour * sun_colour * sun_intensity * blend * (1 - fex)
// Fragment epilogue: result * tc8 + tc9. view_distance is the radial camera distance, used raw.
vec3 des_scatter(vec3 color, float view_distance, vec3 view_dir) {
	vec3 beta_ray = RAYLEIGH_WAVELENGTH_WEIGHTS * scatter_beta_ray;
	vec3 beta_mie = MIE_WAVELENGTH_WEIGHTS * scatter_beta_mie;
	// Extinction (bluer than scattering - absorption; = c104's ratio) drives fex and the in-scatter
	// normalisation. The scattering pair above still weights the in-scatter numerator.
	vec3 beta_ext = EXTINCTION_WAVELENGTH_WEIGHTS * (scatter_beta_ray + scatter_beta_mie);
	// exp2, not exp - the real instruction is EX2, which folds a factor of ln2 into the scale.
	vec3 fex = exp2(-beta_ext * view_distance * scatter_distance_mul * scatter_distance_scale);

	float cos_theta = dot(-view_dir, scatter_sun_dir);
	// Per-channel / beta_ext (DeS's c106 == 1/c104). beta_ext is blue-heavy so 1/beta_ext is
	// red-heavy - the normalised in-scatter comes out slightly warm, matching the reference haze.
	vec3 inscatter = (beta_ray * rayleigh_phase(cos_theta)
			+ beta_mie * henyey_greenstein_phase(cos_theta, scatter_hg_g))
		/ max(beta_ext, vec3(0.0001))
		* scatter_sun_color * scatter_sun_intensity * scatter_inscatter_mul;

	// tc8: reflectance (c105) rides inside the transmittance, not the in-scatter. blendCoef (c110.w)
	// lerps 1 -> fex*reflectance and scales the additive in-scatter by the same factor.
	vec3 transmittance = (1.0 - scatter_blend) + scatter_blend * fex * scatter_reflectance;
	return color * transmittance + inscatter * (1.0 - fex) * scatter_blend;
}

// FOG_BANK distance fade: `mix(color, fog_color, saturate(ramp * fog_weight_scale))`, where the
// linear near->far ramp is DeS's per-vertex fog factor (its vertex program builds it from
// FOG_BANK's fogBeginZ / 1/(fogEndZ-fogBeginZ) in c[103]). Evaluated here per-fragment from the
// same radial camera distance des_scatter uses. Runs before des_scatter, matching the fragment
// epilogue (the `mix` sits two instructions ahead of `result*tc8 + tc9`). See docs/context.md
// part 45. A no-op while fog_weight_scale is 0.
vec3 des_fog(vec3 color, float view_distance) {
	float ramp = clamp((view_distance - fog_begin) / max(fog_end - fog_begin, 0.001), 0.0, 1.0);
	return mix(color, fog_color, clamp(ramp * fog_weight_scale, 0.0, 1.0));
}

// DeS's tone reproduction, from disassembling both passes: it is FULLY LINEAR - no Reinhard, no
// shoulder, no LUT anywhere. The geometry pass epilogue (HemEnv FP instrs 42-43) is
// `saturate(color * E / 2.0)`, E = c16.x the frame's adapted exposure. The fullscreen post pass
// `DS_Fil_HDR_ColAdj.fpo` (5 real instrs) then does `saturate(scene * 2.0) + bloom * w`, followed
// by a TONE_CORRECT_BANK colour matrix. The geometry `/ 2.0` and the post `* 2.0` are a matched
// pair: the `/ 2` only buys one stop of headroom in the RGBA8 buffer so the bright-pass filter has
// something to bloom from. Net of both passes (minus bloom) is simply `saturate(color * E)`, which
// is what this reproduces - dropping the `/ 2` because we don't run the post `* 2`. `DS_Fil_HDR`
// being "a tabulated Reinhard 2002 curve" was a wrong prior assumption; the .fpo is
// `sat(scene*k) + bloom`, k = 2. See docs/context.md part 43.
//
// E = tone_key / tone_adapted_lum. Captured geometry E: 1.77 (Nexus 094137), 1.78 (Boletaria),
// 1.31 (m03), 1.15 (a bright Nexus vantage). FlverLoader binds `tone_adapted_lum =
// clamp(0.10, minAdaptedLum, maxAdapredLum)` - both captured frames imply the engine's adapted
// luminance rested at ~0.101, so DeS scenes sit near 0.10 rather than at the authored floor.
// That gives E ~= 1.8 on every gameplay row-0 (m01..m08), 1.0 on m05, 1.29 on m07's tight band -
// matching the captures. This is the one value not readable from authored data or a live frame
// (Compatibility can't sample scene luminance for real adaptation). See docs/context.md part 48.
vec3 des_tonemap(vec3 color) {
	return clamp(color * (tone_key / max(tone_adapted_lum, 0.0001)), 0.0, 1.0);
}

// Rodrigues rotation about the grey axis - exact identity at hue 0, which every gameplay row is.
vec3 des_hue_rotate(vec3 color, float angle) {
	const vec3 grey_axis = vec3(0.57735);
	float c = cos(angle);
	return color * c + cross(grey_axis, color) * sin(angle) + grey_axis * dot(grey_axis, color) * (1.0 - c);
}

// TONE_CORRECT_BANK: per-channel brightness, then per-channel contrast (pivot 0.5), then global
// saturation, then a grey-axis hue rotation. Verified engine-accurate 2026-09-04 (docs/context.md
// part 44): DeS bakes exactly this into the DS_Fil_HDR_ColAdj colour matrix `M = S(sat) *
// diag(contrast) * diag(brightness)` plus offset `0.5*(1 - contrast)` - the disassembled matrix
// from an m01 capture (contrast 1.1, brightness 0.8/0.87/0.83, saturation 0.9) reproduces from
// this composition to measurement precision, with these same Rec.601 luma weights. There is no
// LUT and no separate tonemap curve (part 43). The old "pivot is an unrecoverable-LUT stand-in"
// note was written before that disassembly. Applied unclamped between the ops (matching the
// matrix), clamped once at the end.
vec3 des_tone_correct(vec3 color) {
	color *= tone_brightness;
	color = (color - vec3(0.5)) * tone_contrast + vec3(0.5);
	float luma = dot(color, vec3(0.299, 0.587, 0.114));
	color = mix(vec3(luma), color, tone_saturation);
	return clamp(des_hue_rotate(color, tone_hue_radians), 0.0, 1.0);
}

// Order matches the HemEnv fragment epilogue: FOG_BANK fade -> H&P scatter -> linear exposure ->
// TONE_CORRECT matrix. view_distance is the radial camera distance (DeS's fog/scatter use planar
// camera-Z / clip.w - part 48 - a ≤1.15x difference at the screen edge, not yet corrected);
// view_dir is the normalized world-space fragment->camera direction. Both passed in because
// INV_VIEW_MATRIX isn't readable from a helper function.
vec3 des_output(vec3 color, float view_distance, vec3 view_dir) {
	return des_tone_correct(des_tonemap(des_scatter(des_fog(color, view_distance), view_distance, view_dir)));
}
