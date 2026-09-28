shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;

// Action43 (SetPostEffect) bump distortion, DS_Sfx_DistortionType1 and its masked variant Type5,
// with the per-draw constants read from captured draws of common effects 100/101/113. On a quad
// of UV r = |2 * UV - 1|, the scene buffer is sampled at the pixel's screen position plus
// bump.xy * strength * max(1 - r, 0) * 0.01 (bump scrolled by time * scroll), multiplied by the
// colour (and mask) and alpha-blended with alpha = colour.a * 2^r. It copies the buffer, so no
// output stage is applied.
uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear;
uniform sampler2D bump_tex : repeat_enable, filter_linear_mipmap;
uniform sampler2D mask_tex : source_color, repeat_disable, filter_linear_mipmap;
uniform bool use_mask = false;
uniform bool point_to_camera = true;
uniform float strength = 0.0;
// Bump scroll per second in Godot UV (native V runs upward, so its -V speed becomes +V here).
uniform vec2 scroll = vec2(0.0);

// pointToCamera turns the quad's normal toward the camera position with its X axis horizontal
// (captured draws: normal within 0.7 degrees of the view ray, off-centre included), not onto
// the view plane.
void vertex() {
	if (point_to_camera) {
		vec3 scale = vec3(length(MODEL_MATRIX[0].xyz), length(MODEL_MATRIX[1].xyz), length(MODEL_MATRIX[2].xyz));
		vec3 center = MODEL_MATRIX[3].xyz;
		vec3 n = normalize(CAMERA_POSITION_WORLD - center);
		vec3 x = cross(vec3(0.0, 1.0, 0.0), n);
		x = length(x) > 1e-4 ? normalize(x) : INV_VIEW_MATRIX[0].xyz;
		MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
			vec4(x * scale.x, 0.0),
			vec4(cross(n, x) * scale.y, 0.0),
			vec4(n * scale.z, 0.0),
			vec4(center, 1.0));
	}
}

void fragment() {
	float r = length(UV * 2.0 - 1.0);
	vec2 bump = texture(bump_tex, UV + TIME * scroll).rg * 2.0 - 1.0;
	vec2 offset = bump * strength * max(1.0 - r, 0.0) * 0.01;
	vec4 colour = COLOR;
	if (use_mask) {
		colour *= texture(mask_tex, UV);
	}
	ALBEDO = textureLod(screen_tex, SCREEN_UV + offset, 0.0).rgb * colour.rgb;
	ALPHA = colour.a * exp2(r);
}
