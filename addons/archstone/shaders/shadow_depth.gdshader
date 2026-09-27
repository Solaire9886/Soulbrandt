shader_type spatial;
render_mode unshaded, cull_front;

// Caster depth pass for ShadowRenderer: linear light-space distance over the far plane, packed
// 16-bit across R,G (unpacked as r + g/255 by sun_shadow). cull_front: only back faces write
// depth, so a lit surface sits ahead of its occluder by the mesh thickness (no acne; slight light
// leak at contacts, and light-facing single-sided planes cast nothing).
uniform float light_far = 1.0;

void fragment() {
	float d = clamp(-VERTEX.z / light_far, 0.0, 1.0);
	float hi = floor(d * 255.0) / 255.0;
	ALBEDO = vec3(hi, (d - hi) * 255.0, 0.0);
}
