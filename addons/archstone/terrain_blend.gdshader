shader_type spatial;

// Two-layer diffuse/normal/specular blend for FLVER materials whose MTD carries FromSoft's
// "[M]"/"[ML]" blend-shader bracket tag - confirmed via FlverSceneImporter.cs to be the
// terrain ground-blend convention (grass/dirt/stone transitions), distinct from the same
// g_Diffuse_2 param used alone (no bracket tag) on unrelated "ghost" character dissolve
// materials, which aren't routed to this shader at all. Bumpmap and specular are each
// independently optional per layer in the source data - hint_default_black on the specular
// uniforms (not hint_default_white) matches the single-layer path's own default of
// Metallic=0 when no specular texture is present, rather than rendering fully metallic.
uniform sampler2D diffuse1 : source_color;
uniform sampler2D diffuse2 : source_color;
uniform sampler2D normal1 : hint_normal;
uniform sampler2D normal2 : hint_normal;
uniform sampler2D specular1 : hint_default_black;
uniform sampler2D specular2 : hint_default_black;
// 865 of the 912 real blend materials also have a real g_Lightmap (the other 47 leave this
// unset, defaulting to white/no-op via hint_default_white below - safe since CUSTOM0 also
// defaults to (0,0,0,0) for those meshes, sampling harmlessly into the corner of whatever
// 1x1 default texture is bound). Its UV is FLVER's third UV channel - both of Godot's native
// UV/UV2 slots are already spoken for by diffuse1/diffuse2 here, so FlverSceneImporter.cs
// packs it into the mesh's Custom0 channel instead (as raw floats, read back below).
uniform sampler2D lightmap : hint_default_white;

// A straight diffuse*lightmap multiply here had the same architecturally-wrong shape as the
// single-layer path (see CLAUDE.md's Lightmaps section) - shares the hemisphere-ambient/env
// fix via hemisphere_ambient.gdshaderinc rather than duplicating it a second time.
#include "hemisphere_ambient.gdshaderinc"

// CUSTOM0 (the mesh's raw Custom0 array) is only readable inside vertex() - confirmed the
// hard way (a real shader-compile error under a non-dummy rendering driver; --headless's
// dummy driver never actually compiles GLSL and so never caught this) that, unlike COLOR/UV/
// UV2, it isn't automatically available as an interpolated fragment-stage input. Has to be
// forwarded through an explicit varying instead.
varying vec2 lightmap_uv;

void vertex() {
	lightmap_uv = CUSTOM0.rg;
	world_normal = hemisphere_world_normal(NORMAL, MODEL_MATRIX);
}

void fragment() {
	// COLOR.a is the blend weight, not COLOR.r/g/b: confirmed by scanning all 907 blend
	// meshes in the mounted game data. R=G=B is a separate, near-constant-1 signal that
	// only dips locally (vertex AO/shadow, not blend), while A varies broadly and smoothly
	// across whole surfaces - transition-triangle coverage (>0.2 spread across a triangle's
	// 3 vertices) using A averages 3-9x higher than using R on the same test mesh
	// (m0002b1.flver, e.g. 88.1% vs 3.5% on one material), and R/A are statistically
	// uncorrelated (corr=0.07) across the full dataset. Using R was the bug behind blending
	// looking like "one texture always wins" - R is ~0.9+ almost everywhere, so mix() picked
	// nearly the same texture across an entire surface except in sparse AO crevices.
	float blend = COLOR.a;
	// COLOR.r (=G=B) is NOT a shading/AO gradient, despite looking AO-shaped statistically
	// (mostly ~1.0, non-constant, uncorrelated with A) - confirmed by direct inspection that
	// it only ever takes exactly two discrete values (e.g. 1.000 or 0.549, never anything
	// between) on real terrain meshes. Multiplying it into ALBEDO was tried and reverted:
	// it produced sharp light/dark borders with no gradient at all, visible even with the
	// sun disabled, and confirmed absent from the real game - see context.md. What this
	// channel actually encodes is unknown; it's read (via VertexColor above) but
	// deliberately left unused here.
	vec3 diffuse_color = mix(texture(diffuse1, UV).rgb, texture(diffuse2, UV2).rgb, blend);
	vec3 lm = texture(lightmap, lightmap_uv).rgb;
	float spec = mix(texture(specular1, UV).r, texture(specular2, UV2).r, blend);
	vec3 env = lm * env_color * env_intensity;
	ALBEDO = diffuse_color * (hemisphere_ambient() + env);
	// ponytail: linear mix of two tangent-space normal maps, not a proper renormalized
	// blend - same "good enough" tradeoff already accepted for the single-layer
	// g_Specular->Metallic mapping in FlverSceneImporter.cs.
	NORMAL_MAP = mix(texture(normal1, UV).rgb, texture(normal2, UV2).rgb, blend);
	METALLIC = spec;
	// See hemisphere_ambient.gdshaderinc - envSpc is LIGHT_BANK's separate specular-env term,
	// same EMISSION-not-ALBEDO treatment as the single-layer path for the same "no real
	// reflection probe" reason water.gdshader already established.
	EMISSION = lm * env_spc_color * env_spc_intensity * spec;
}
