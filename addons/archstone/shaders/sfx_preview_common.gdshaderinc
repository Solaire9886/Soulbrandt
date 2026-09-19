// Deliberately a preview output domain. No guessed DeS transparency enum, atmosphere,
// exposure, volume texture, or second tone-correction pass.
uniform sampler2D diffuse_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D frame_curve : filter_nearest, repeat_disable;
uniform int frame_columns = 1;
uniform int frame_count = 1;
varying float particle_frame;

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
}

void fragment() {
    float columns = float(max(frame_columns, 1));
    float rows = ceil(float(max(frame_count, 1)) / columns);
    vec2 cell = vec2(mod(particle_frame, columns), floor(particle_frame / columns));
    vec4 texel = texture(diffuse_tex, (UV + cell) / vec2(columns, rows));
    ALBEDO = texel.rgb * COLOR.rgb;
    ALPHA = texel.a * COLOR.a;
}
