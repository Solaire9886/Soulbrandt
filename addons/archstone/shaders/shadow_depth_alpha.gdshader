shader_type spatial;
render_mode unshaded, cull_front;

// Alpha-tested caster variant of shadow_depth.gdshader: discards where the visible lit material
// does (diffuse alpha below alpha_scissor_threshold), so cutout cards cast their silhouette, not
// their quad. cull_front as there: casting both faces put closed alpha-tested meshes at their
// own depth (self-shadowing).
uniform float light_far = 1.0;
uniform sampler2D diffuse;
uniform float alpha_scissor_threshold = 0.5;

void fragment() {
	if (texture(diffuse, UV).a < alpha_scissor_threshold) {
		discard;
	}
	float d = clamp(-VERTEX.z / light_far, 0.0, 1.0);
	float hi = floor(d * 255.0) / 255.0;
	ALBEDO = vec3(hi, (d - hi) * 255.0, 0.0);
}
