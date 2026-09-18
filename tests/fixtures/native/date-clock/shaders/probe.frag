// Original WpeBaker test shader, MIT.
uniform float g_Time;
uniform float g_Alpha;
uniform sampler2D g_Texture0;
varying vec2 v_TexCoord;
void main() {
    vec2 p = v_TexCoord;
    vec3 c = p.y < 0.5
        ? (p.x < 0.5 ? vec3(1.0,0.0,0.0) : vec3(0.0,1.0,0.0))
        : (p.x < 0.5 ? vec3(0.0,0.0,1.0) : vec3(1.0,1.0,0.0));
    float phase = 0.0;
    if (abs(p.x - (0.2 + phase * 0.6)) < 0.03 && abs(p.y - 0.65) < 0.09)
        c = vec3(1.0);
    // A continuous color ramp complements the spatial marker at high FPS.
    if (p.y > 0.90) c = vec3(phase, 0.25, 1.0 - phase);
    gl_FragColor = vec4(c * g_Alpha, 1.0) * texSample2D(g_Texture0, p);
}
