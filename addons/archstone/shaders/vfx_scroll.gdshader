shader_type spatial;
render_mode blend_mix, unshaded;

// Unlit VFX materials with a real g_TexScroll_0 - skies, clouds, light shafts, vollight dust,
// the magic barrier, the Wanderer ghost. StandardMaterial3D covers everything else these need
// (they're g_LightingType=0, so Unshaded) but has no way to animate a UV, which is the whole
// point of these materials - hence a shader. 108 meshes game-wide; see docs/ARCHITECTURE.md's
// known-deferred-work entry on this item.
//
// Two files, not one: blend_mix vs blend_add is a compile-time render_mode, same reason the
// lightmap family is split. Cull mode is compile-time too, so the 12 of those 108 meshes with
// FLVER0 CullBackfaces=false lose two-sidedness here - the same already-documented
// ShaderMaterial limitation that affects 338 lightmap-family meshes.
uniform sampler2D diffuse : source_color;
uniform vec3 diffuse_tint : source_color = vec3(1.0);
uniform vec2 tex_scroll_0 = vec2(0.0);

void fragment() {
	vec4 c = texture(diffuse, UV + tex_scroll_0 * TIME);
	ALBEDO = c.rgb * diffuse_tint * COLOR.rgb;
	ALPHA = c.a * COLOR.a;
}
