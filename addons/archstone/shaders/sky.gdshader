shader_type spatial;
render_mode unshaded;

// g_LightingType=0 opaque sky-dome backdrops (m02's m9999B0, the a03_sky* / m_sky* family).
// Unlike the ghost/dissolve and additive-VFX materials that also carry g_LightingType=0, DeS runs
// these through the same FOG_BANK fade + exposure epilogue as lit geometry - their DS_Phn_ColDif
// shader (disassembled form DS_Phn_Dif________________Non) ends with
// `mix(diffuse*vtxColour, FOG_BANK.col, fogFactor*degRotW/100)` -> `*transmittance + inscatter`
// -> `saturate(*E/2)`. This reproduces all of that EXCEPT the per-vertex H&P scatter term
// (f[TEX8]/f[TEX9]): the sky's scatter VP constants aren't recovered, and des_scatter at the
// dome's geometric radius over-attenuates. FOG_BANK row 3 (名 "天球") for m02 gives the washed
// green-yellow the reference shows. See docs/context.md part 45.
#include "output_stage.gdshaderinc"

uniform sampler2D diffuse : source_color;
uniform vec3 diffuse_tint : source_color = vec3(1.0);
uniform vec2 tex_scroll_0 = vec2(0.0);

varying vec3 sky_world_position;

void vertex() {
	sky_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 camera_position = INV_VIEW_MATRIX[3].xyz;
	float view_distance = length(sky_world_position - camera_position);
	vec3 color = texture(diffuse, UV + tex_scroll_0 * TIME).rgb * diffuse_tint * COLOR.rgb;
	// Fog (FOG_BANK distance fade) -> exposure -> tone-correct. No des_scatter - see file header.
	ALBEDO = des_tone_correct(des_tonemap(des_fog(color, view_distance)));
}
