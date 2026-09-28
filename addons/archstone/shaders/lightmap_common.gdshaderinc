// Shared body of the single-layer lit family (lightmap*.gdshader). The variants differ only in
// render_mode (blend and cull are compile-time keywords).
uniform sampler2D diffuse : source_color;
uniform sampler2D normal_map : hint_normal;
uniform sampler2D specular : hint_default_black;
// White by default: a lit material without a lightmap is gated by the shadow alone, as in the
// engine. Sampled at UV2.
uniform sampler2D lightmap : hint_default_white;

// g_DiffuseMapColor * g_DiffuseMapColorPower (the 0.6 seen on vertex colour in captured HemEnv
// programs is this power, not a separate constant).
uniform vec3 diffuse_tint : source_color = vec3(1.0);

// g_TexScroll_0, UV units per second. The lightmap UV does not scroll.
uniform vec2 tex_scroll_0 = vec2(0.0);

vec2 scroll_uv(vec2 uv) {
	return uv + tex_scroll_0 * TIME;
}

#include "hemisphere_ambient.gdshaderinc"
#include "output_stage.gdshaderinc"

// Returns the finished pre-output-stage colour. Built-ins are passed in because helpers cannot
// read them. Vertex colour multiplies the diffuse and the specular, never the hemisphere.
vec3 lightmap_shade(vec2 uv, vec2 uv2, vec3 vertex_color, vec3 view_dir, float view_distance, bool front_facing) {
	vec2 duv = scroll_uv(uv);
	vec3 diffuse_tex = texture(diffuse, duv).rgb * diffuse_tint;
	vec3 lm = texture(lightmap, uv2).rgb;
	vec3 spec = texture(specular, duv).rgb;
	vec3 sampled_normal = texture(normal_map, duv).rgb;

	vec3 pixel_normal = hemisphere_pixel_normal(decode_normal_map(sampled_normal.xy), duv, front_facing);

	vec3 shadow = sun_shadow(world_position, view_distance);
	vec3 point_lights = point_lights_diffuse(world_position, pixel_normal);
	if (lighting_model == LIGHTING_HEM_DIR3) {
		vec3 diffuse_color = diffuse_tex * vertex_color;
		return diffuse_color * (hemisphere_ambient(pixel_normal) + hemdir3_diffuse(pixel_normal) * shadow + point_lights)
			+ hemdir3_specular(pixel_normal, view_dir, spec, vertex_color) * shadow;
	}
	vec3 diffuse_color = diffuse_tex * vertex_color;
	vec3 gate = min(shadow, lm);
	return diffuse_color * (hemisphere_ambient(pixel_normal) + env_diffuse(pixel_normal, gate) + point_lights)
		+ env_specular(pixel_normal, view_dir, gate, spec, vertex_color);
}
