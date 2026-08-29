shader_type spatial;
render_mode blend_add, unshaded;

// Additive variant of vfx_scroll.gdshader (g_BlendMode=4) - vollight dust, the magic barrier,
// the Wanderer ghost, a05_water02. See that file's header for why this family exists and why
// blend_mix vs blend_add needs a second file.
uniform sampler2D diffuse : source_color;
uniform vec3 diffuse_tint : source_color = vec3(1.0);
uniform vec2 tex_scroll_0 = vec2(0.0);

void fragment() {
	vec4 c = texture(diffuse, UV + tex_scroll_0 * TIME);
	ALBEDO = c.rgb * diffuse_tint * COLOR.rgb;
	ALPHA = c.a * COLOR.a;
}
