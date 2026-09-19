shader_type spatial;
// Unshaded: hand-computes a finished colour, and leaving it lit made the editor's preview sun an
// uncontrolled input. blend_mix + ALPHA=1 keeps it on the transparent pass so SCREEN_TEXTURE /
// DEPTH_TEXTURE are readable (needed for the screen-space reflection + refraction taps).
render_mode blend_mix, cull_back, unshaded;

// Reconstructed from DeS's real DS_Water_Env fragment program (107 instrs, disassembled) + the
// DS_Water vertex program + the per-material water .mtd params + 6 RPCS3 frame captures that each
// picked up one water draw. See docs/context.md's water entry (part 50). Structure: 3 scrolling
// bump-normal octaves -> Fresnel-weighted blend of the body colour (screen refraction tinted by
// g_WaterColor) and the reflection (cubemap + screen tap + sun glint) -> the shared output stage
// (des_scatter + des_fog + exposure), same as the surrounding geometry. Close to RPCS3 gameplay as
// of 2026-09-05 but not 1:1 - a few spots still off, see docs/context.md.
uniform sampler2D bumpmap : hint_normal;
// g_Envmap is a real cubemap (tex8 in the disasm, sampled by a reflection vector), not a flat
// image. FlverModelBuilder.ResolveWaterEnvCube decodes it; a dim neutral cube stands in if it
// can't be resolved, so the screen-space reflection still carries the surface.
uniform samplerCube envmap : source_color;
uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear_mipmap;

// Water is a map surface - same per-material atmosphere/tonemap as everything around it.
// FlverLoader binds these (tone_key, fog_*, scatter_*) from the same drawparam rows.
#include "output_stage.gdshaderinc"

// --- Per-octave wave params. g_TileScale_i in the .mtd is a Float2: .x = base-UV tiling multiplier
// (applied directly - no fudge factor), .y = this octave's speed multiplier on the flow_dir
// velocity. Straight out of the DS_Water VP (c[128..130]); matches every captured water draw.
uniform vec2 tile_scale_0 = vec2(1.0, 0.1);
uniform vec2 tile_scale_1 = vec2(1.0, 0.1);
uniform vec2 tile_scale_2 = vec2(1.0, 0.1);
// g_TexScroll_0 - the shared flow velocity, used RAW (not normalised): its magnitude is the base
// scroll speed (~0.01-0.2 in the real data), the per-octave multiplier is tile_scale_i.y. Only _0
// is ever set. Normalising this made m02 water ~30x too fast. Near-zero -> a slow default drift.
uniform vec2 flow_dir = vec2(0.05, 0.0);
// g_TileBlend_0/1/2 - octave weights (c[4].y / c[6].x / c[8].z).
uniform float tile_blend_0 = 1.0;
uniform float tile_blend_1 = 0.8;
uniform float tile_blend_2 = 0.5;

// g_WaterColor.rgb - the deep-water body tint (c[36]). .a rides in water_alpha (a plain vec3
// source_color uniform can't carry the 4th channel); it scales the body-vs-refraction mix.
uniform vec3 water_color : source_color = vec3(0.09, 0.078, 0.031);
uniform float water_alpha = 0.7;
// g_ReflectBand / g_RefractBand (c[11].x) - screen-UV perturbation bands for the two screen taps.
uniform float refract_band = 0.15;
uniform float reflect_band = 0.1;
// g_FresnelPow / Bias / Scale / Color (c[33].x, c[13], c[15]). pow == 0 -> constant reflectance.
uniform float fresnel_pow = 3.0;
uniform float fresnel_bias = 0.1;
uniform float fresnel_scale = 0.9;
uniform vec3 fresnel_color : source_color = vec3(1.0);
// g_WaterFadeBegin (c[46]/c[48], confirmed exact against 5 captures incl. its 1/x reciprocal in
// the paired slot) - user A/B (2026-09-05) says RPCS3 shows a clear dark band right where water
// meets the coast, fading into the (brighter) open water past it - a shoreline-proximity effect,
// not a camera-distance one. A camera-distance reading (tried first, off the shared fog ramp) is
// structurally wrong for that: it fades in rings around the camera, not along the shoreline
// geometry. 0.5-1.0 world units is a believable width for a shore *band* (a contact-darkening
// strip a metre or so wide), which is what this now drives - see the shore_edge comment below.
uniform float water_fade_begin = 0.5;
// g_BumpMapSmoose (c[9].x) - a bias ADDED to the summed wave normal's Z, not an xy scale: negative
// tips Z down => choppier water, positive => flatter. Real range roughly -0.5 (m02) to +10 (a06).
uniform float bump_smoose = 1.0;
// g_SpecularMapColor / g_SpecularMapColorPower (c[39]) - the sun-glint tint and weight, on their
// own with nothing else folded in (see the glint comment in fragment() below).
uniform vec3 glint_color : source_color = vec3(1.0);
uniform float glint_power = 2.0;

varying vec3 world_position;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// One scrolling bump-normal octave: base UV scaled by s.x, drifting by the flow velocity times
// this octave's rate multiplier s.y. `flow` is g_TexScroll_0 raw, so speed = |flow| * s.y.
// Z is reconstructed from a 2-channel normal sample (sqrt(1 - x² - y²)), matching the disasm.
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
	// DS_Water_Env sums the octave normals weighted by g_TileBlend_i - NOT renormalised, NOT
	// averaged - then adds g_BumpMapSmoose to Z as a bias: negative pushes Z down => choppier,
	// positive => flatter (disasm: R1 = Σ wᵢ·nᵢ ; H2.z = R1.z + c[9].x ; NRM). c[9].x == the
	// captured g_BumpMapSmoose (m02 -0.5, a02_lake +2). Dividing by Σw (an earlier bug) plus using
	// smoose as an xy multiplier flattened the wave normal to a third of its real slope.
	vec3 wave = n0 * tile_blend_0 + n1 * tile_blend_1 + n2 * tile_blend_2;
	vec3 tangent_normal = normalize(vec3(wave.xy, max(wave.z + bump_smoose, 0.1)));
	vec3 N = normalize(TANGENT * tangent_normal.x + BINORMAL * tangent_normal.y + NORMAL * tangent_normal.z);

	float ndv = clamp(dot(N, normalize(VIEW)), 0.0, 1.0);
	float fresnel = fresnel_pow > 1e-3
		? clamp(fresnel_bias + fresnel_scale * pow(1.0 - ndv, fresnel_pow), 0.0, 1.0)
		: clamp(fresnel_scale, 0.0, 1.0);

	float raw_depth = texture(depth_tex, SCREEN_UV).r;

	vec3 camera_position = INV_VIEW_MATRIX[3].xyz;
	vec3 view_dir = normalize(camera_position - world_position);
	float view_distance = length(world_position - camera_position);
	// The reflection screen tap alone is distance-scaled in the disasm (f[TEX6] multiplies its UV
	// offset, instr 36); the refraction tap's offset (instr 23) isn't gated by it at all. An
	// earlier cut applied this same falloff to both, which collapsed the refraction/wave distortion
	// to near-nothing past a few units - invisible at any normal player distance.
	float near_fac = clamp(2.0 / max(view_distance, 1.0), 0.0, 1.0);

	// Screen-space refraction, guarded so a foreground object in front of the water can't bleed
	// its colour up onto the surface. Full-strength regardless of distance - see near_fac above.
	vec2 refract_uv = SCREEN_UV + N.xy * refract_band;
	if (texture(depth_tex, refract_uv).r > raw_depth)
		refract_uv = SCREEN_UV;
	vec3 refracted = textureLod(screen_tex, refract_uv, 0.0).rgb;
	vec3 screen_reflection = textureLod(screen_tex, SCREEN_UV + N.xy * (reflect_band * near_fac), 0.0).rgb;

	// Body colour: the scene seen through the water (screen refraction), tinted toward the deep
	// water_color by murkiness (g_WaterColor.a -> water_alpha). No shore/distance fade on the body
	// itself - see shore_edge below, applied to the whole finished surface instead.
	vec3 body = mix(refracted, water_color, water_alpha);

	// Shoreline edge: how close this pixel's water surface sits to whatever's directly behind it
	// (the coast it's touching), from the depth buffer - a small column means water meeting land
	// right here. water_fade_begin is the band's width in world units (see the uniform comment).
	vec4 view_pos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
	view_pos.xyz /= view_pos.w;
	float column = abs(view_pos.z - VERTEX.z);
	float shore_edge = 1.0 - clamp(column / max(water_fade_begin, 1e-3), 0.0, 1.0);

	// Reflection: cubemap for the distance + the screen tap for near geometry, plus a tight sun
	// glint. DeS's g_Envmap cubes are tiny (m02's is 32px), so perturbing the reflection vector
	// into one barely varies - the wave pattern at any real viewing distance is carried by the
	// glint: DS_Water_Env adds pow(dot(R, sun), g_SpecularPower) on top of the cube reflection
	// (disasm instr 47-80, exponent = g_SpecularPower = 100 on every water .mtd, weight c[39] ==
	// glint_color * glint_power - was a fixed x2 fudge, now the real per-material params). Not
	// also multiplied by scatter_sun_color/intensity - a captured m02 material (a02_water_river,
	// glint_power=2) gave c[39]=(2,2,2) exactly, with no extra factor for that area's
	// scatter_sun_intensity=2.0; folding it in anyway doubled the weight and, compounded by the
	// slow falloff of a real pow(x,100) tail, washed a wide swath of the surface toward white -
	// the "second, brighter reflection" reported 2026-09-05. scatter_sun_dir (direction only) is
	// still the right shared value for the dot product itself.
	vec3 n_world = normalize((INV_VIEW_MATRIX * vec4(N, 0.0)).xyz);
	vec3 refl_vec = reflect(-view_dir, n_world);
	vec3 cube_reflection = texture(envmap, refl_vec).rgb;
	float glint = pow(max(dot(refl_vec, scatter_sun_dir), 0.0), 100.0);
	vec3 reflection = mix(cube_reflection, screen_reflection, near_fac * 0.35) * fresnel_color
		+ glint * glint_color * glint_power;

	vec3 surface = mix(body, reflection, fresnel);
	// Darken toward the material's own water_color right at the shore, fading out over
	// water_fade_begin - the dark coastal band from the A/B reference. shore_edge is 0 almost
	// everywhere except the thin strip actually touching land, so this doesn't touch open water.
	surface = mix(surface, water_color, shore_edge);
	ALBEDO = des_output(surface, view_distance, view_dir);
	ALPHA = 1.0;
}
