shader_type spatial;
render_mode blend_add, unshaded, cull_disabled;

// Additive variant of vfx_scroll.gdshader (g_BlendMode 4), with the additive form of the tone
// correction (des_encode_additive).
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
	ALBEDO = des_output_additive(c.rgb * diffuse_tint * COLOR.rgb);
	ALPHA = c.a * COLOR.a;
}
