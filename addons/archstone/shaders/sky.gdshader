shader_type spatial;
render_mode unshaded;

// Opaque unlit (g_LightingType 0) sky domes (m02's m9999B0, the a03_sky* / m_sky* family). The
// game draws them with DS_Phn_Dif________________Non: diffuse * vertex colour through the full
// output stage. The m02 capture's sky draws carry the frame's scattering constants and their own
// FOG_BANK row (3, "天球"), so the dome takes the scattering at its radius like any map piece.
#include "output_stage.gdshaderinc"

uniform sampler2D diffuse : source_color;
uniform vec3 diffuse_tint : source_color = vec3(1.0);
uniform vec2 tex_scroll_0 = vec2(0.0);

void vertex() {
	vec3 sky_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	DES_ATMOSPHERE_VERTEX(sky_world_position);
}

void fragment() {
	vec3 color = texture(diffuse, UV + tex_scroll_0 * TIME).rgb * diffuse_tint * COLOR.rgb;
	ALBEDO = des_output(color);
}
