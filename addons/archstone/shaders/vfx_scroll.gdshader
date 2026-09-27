shader_type spatial;
render_mode blend_mix, unshaded, cull_disabled;

// Unlit (g_LightingType 0) blended or scrolling map materials other than sky domes: light
// shafts, fog sheets, clouds, the magic barrier, the Wanderer ghost. The game's
// DS_Phn_Dif________________Non: diffuse * vertex colour * tint through the lit families' output
// stage (fog, scattering, E/2) without lighting. FlverLoader binds fog and scattering rows as for
// any map surface. Blend mode and culling are compile-time, so additive is a second file and
// cull_back_faces restores single-sidedness per material.
#include "output_stage.gdshaderinc"

uniform sampler2D diffuse : source_color;
uniform vec3 diffuse_tint : source_color = vec3(1.0);
uniform vec2 tex_scroll_0 = vec2(0.0);
uniform bool cull_back_faces = true;

void vertex() {
	DES_ATMOSPHERE_VERTEX((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz);
}

void fragment() {
	if (cull_back_faces && !FRONT_FACING)
		discard;
	vec4 c = texture(diffuse, UV + tex_scroll_0 * TIME);
	ALBEDO = des_output(c.rgb * diffuse_tint * COLOR.rgb);
	ALPHA = c.a * COLOR.a;
}
