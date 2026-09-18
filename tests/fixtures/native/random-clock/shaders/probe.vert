// Original WpeBaker test shader, MIT.
attribute vec3 a_Position;
attribute vec2 a_TexCoord;
uniform mat4 g_ModelViewProjectionMatrix;
varying vec2 v_TexCoord;
void main() {
    v_TexCoord = a_TexCoord;
    gl_Position = mul(g_ModelViewProjectionMatrix, vec4(a_Position, 1.0));
}
