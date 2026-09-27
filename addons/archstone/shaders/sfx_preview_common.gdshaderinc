// Effect billboards (SfxBatchParticles). Output follows the DS_Sfx_SimpleSprite programs: every
// type halves the sprite colour into the scene buffer, and Type0 (Setup71 bump and volume absent)
// then multiplies by E/2; Type1-3 apply no exposure. Their per-vertex fog/scattering factors are
// not reproduced. Additive and subtractive wrappers define SFX_BLEND_ADDITIVE.
#include "output_stage.gdshaderinc"

uniform sampler2D diffuse_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D frame_curve : filter_nearest, repeat_disable;
uniform int frame_columns = 1;
uniform int frame_count = 1;
uniform bool apply_exposure = false;
// DS_Sfx_SimpleSpriteType2 (Setup71 bump absent, volume set): each sprite is a slab of
// thickness h = (width + height) / 2 centred on its view depth z, and alpha is scaled by
// the slab's part in front of the scene, min(sceneZ - (z - h/2), h) / h. The native
// near-plane branch is not reproduced.
uniform bool volume_depth = false;
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_nearest;
varying float particle_frame;
varying float particle_depth;
varying float particle_thickness;

void vertex() {
    vec3 particle_scale = vec3(length(MODEL_MATRIX[0].xyz), length(MODEL_MATRIX[1].xyz), length(MODEL_MATRIX[2].xyz));
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
        vec4(INV_VIEW_MATRIX[0].xyz * particle_scale.x, 0.0),
        vec4(INV_VIEW_MATRIX[1].xyz * particle_scale.y, 0.0),
        vec4(INV_VIEW_MATRIX[2].xyz * particle_scale.z, 0.0),
        MODEL_MATRIX[3]);
    float c = cos(INSTANCE_CUSTOM.x);
    float s = sin(INSTANCE_CUSTOM.x);
    VERTEX.xy = mat2(vec2(c, s), vec2(-s, c)) * VERTEX.xy;
    particle_frame = floor(texture(frame_curve, vec2(clamp(INSTANCE_CUSTOM.y, 0.0, 1.0), 0.5)).r);
    particle_depth = -(VIEW_MATRIX * vec4(MODEL_MATRIX[3].xyz, 1.0)).z;
    particle_thickness = max(0.5 * (particle_scale.x + particle_scale.y), 1e-4);
}

void fragment() {
    float columns = float(max(frame_columns, 1));
    float rows = ceil(float(max(frame_count, 1)) / columns);
    vec2 cell = vec2(mod(particle_frame, columns), floor(particle_frame / columns));
    vec4 texel = texture(diffuse_tex, (UV + cell) / vec2(columns, rows));
    // Twice the scene-buffer value: saturate(colour / 2) for Type1-3, saturate(colour * E / 4) for Type0.
    vec3 value = texel.rgb * COLOR.rgb * (apply_exposure ? 0.5 * des_exposure() : 1.0);
#ifdef SFX_BLEND_ADDITIVE
    ALBEDO = des_encode_additive(value, DES_MEASURING);
#else
    ALBEDO = des_encode(value, DES_MEASURING);
#endif
    ALPHA = texel.a * COLOR.a;
    if (volume_depth) {
        float raw = texture(depth_tex, SCREEN_UV).r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
        vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw * 2.0 - 1.0);
#else
        vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw);
#endif
        vec4 view = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
        float scene = -view.z / view.w;
        ALPHA *= clamp((scene - (particle_depth - 0.5 * particle_thickness)) / particle_thickness, 0.0, 1.0);
    }
}
