shader_type spatial;
render_mode unshaded, cull_front;

// Caster depth pass for the sun-shadow region (ShadowRenderer.cs, v2: one static orthographic
// pass over the map's casters). Writes linear light-space distance from the shadow camera,
// normalized by its far plane, packed 16-bit across R,G on the RGBA8 target; unpacked as
// r + g/255 by hemisphere_ambient.gdshaderinc's sun_shadow(). VERTEX in fragment() is view space,
// so -VERTEX.z is the positive forward distance.
//
// cull_front: only back faces write depth, so a lit surface sits ahead of its recorded occluder
// by roughly the mesh thickness - kills self-shadow acne. Trade-off is mild light-leak at contact
// shadows and single-sided planes facing the light casting nothing, both acceptable for v2.
uniform float light_far = 1.0;

void fragment() {
	float d = clamp(-VERTEX.z / light_far, 0.0, 1.0);
	float hi = floor(d * 255.0) / 255.0;
	ALBEDO = vec3(hi, (d - hi) * 255.0, 0.0);
}
