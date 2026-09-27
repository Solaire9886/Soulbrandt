shader_type spatial;
render_mode unshaded, blend_add, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled;

// The bloom half of DS_Fil_HDR_ColAdj, drawn over the whole view after everything else:
// out = M * (saturate(buffer * 2) + bloom * bloomMul/100) + offset. The material output stage
// has already written saturate(M * saturate(colour * E) + offset); M is linear, so this adds
// M * bloom * bloomMul/100 on top. The two differ only where the stage clamped: near-black pixels
// under a negative offset (at most |offset|, 0.05 on m01) and channels already at 1.
#include "output_stage.gdshaderinc"

uniform sampler2D bloom : filter_linear, repeat_disable;
uniform float bloom_mul = 0.0; // bloomMul / 100

void vertex() {
	POSITION = vec4(VERTEX.xy, 0.0, 1.0);
}

void fragment() {
	vec3 b = texture(bloom, SCREEN_UV).rgb * bloom_mul;
	ALBEDO = des_tone_matrix(b) - des_tone_matrix(vec3(0.0));
}
