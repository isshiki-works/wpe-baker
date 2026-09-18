using System.Text.Json.Nodes;
using System.Buffers.Binary;
using Baker.Core;

string root = Path.Combine(Path.GetTempPath(), "shader-period-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    passed.Add(name);
}
void Write(string relative, string text)
{
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}
Write("project.json", "{\"type\":\"scene\",\"file\":\"scene.json\"}");
Write("effects/water.json", "{\"passes\":[{\"material\":\"materials/water.json\"}]}");
Write("effects/sway.json", "{\"passes\":[{\"material\":\"materials/sway.json\"}]}");
Write("effects/foliage.json", "{\"passes\":[{\"material\":\"materials/foliage.json\"}]}");
Write("effects/water-bad.json", "{\"passes\":[{\"material\":\"materials/water-bad.json\"}]}");
Write("effects/sway-bad.json", "{\"passes\":[{\"material\":\"materials/sway-bad.json\"}]}");
Write("effects/shine.json", "{\"passes\":[{\"material\":\"materials/shine-down.json\"},{\"material\":\"materials/shine-cast.json\"}]}");
Write("effects/scroll.json", "{\"passes\":[{\"material\":\"materials/scroll.json\"}]}");
Write("effects/swing.json", "{\"passes\":[{\"material\":\"materials/swing.json\"}]}");
Write("effects/twirl.json", "{\"passes\":[{\"material\":\"materials/twirl.json\"}]}");
Write("effects/static.json", "{\"passes\":[{\"material\":\"materials/static.json\"}]}");
Write("materials/water.json", "{\"passes\":[{\"shader\":\"effects/waterwaves\"}]}");
Write("materials/sway.json", "{\"passes\":[{\"shader\":\"effects/auto_sway\"}]}");
Write("materials/foliage.json", "{\"passes\":[{\"shader\":\"effects/foliagesway\"}]}");
Write("materials/water-bad.json", "{\"passes\":[{\"shader\":\"effects/waterwaves-bad\"}]}");
Write("materials/sway-bad.json", "{\"passes\":[{\"shader\":\"effects/auto_sway-bad\"}]}");
Write("materials/shine-down.json", "{\"passes\":[{\"shader\":\"effects/shine_downsample2\"}]}");
Write("materials/shine-cast.json", "{\"passes\":[{\"shader\":\"effects/shine_cast\"}]}");
Write("materials/scroll.json", "{\"passes\":[{\"shader\":\"effects/scroll\"}]}");
Write("materials/swing.json", "{\"passes\":[{\"shader\":\"effects/swing\"}]}");
Write("materials/twirl.json", "{\"passes\":[{\"shader\":\"effects/twirl\"}]}");
Write("materials/static.json", "{\"passes\":[{\"shader\":\"effects/static\"}]}");
Write("shaders/effects/waterwaves.frag", "uniform float g_Time; uniform float g_Speed; void main(){ float distance = g_Time * g_Speed; float x = sin(distance); }");
Write("shaders/effects/auto_sway.frag", "void main(){}");
Write("shaders/effects/auto_sway.vert", "uniform float g_Time; uniform float g_Speed; void main(){ float thisMotionTime = g_Time * g_Speed; float x = sin(thisMotionTime * M_PI_2); }");
Write("shaders/effects/foliagesway.frag", "uniform float g_Time; float speeduv; void main(){ float x = g_Time * speeduv; }");
Write("shaders/effects/shine_downsample2.vert", "varying vec4 v_NoiseTexCoord; uniform float g_Time; uniform float g_NoiseSpeed; uniform float g_NoiseScale; void main(){v_NoiseTexCoord.xy = a_TexCoord + g_Time * g_NoiseSpeed; v_NoiseTexCoord.wz = vec2(a_TexCoord.y, -a_TexCoord.x) * 0.633 + vec2(-g_Time, g_Time) * 0.5 * g_NoiseSpeed; v_NoiseTexCoord *= g_NoiseScale;}");
Write("shaders/effects/shine_downsample2.frag", "varying vec4 v_NoiseTexCoord; uniform sampler2D g_Texture2; // {\"default\":\"util/clouds_256\"} void main(){float x=texSample2D(g_Texture2, v_NoiseTexCoord.xy).r * texSample2D(g_Texture2, v_NoiseTexCoord.zw).r;}");
Write("shaders/effects/shine_cast.vert", "// [COMBO] {\"combo\":\"EDGES\",\"default\":4}\nuniform float g_Time; uniform float g_Speed; void main(){vec2 baseDirection = rotateVec2(vec2(0, 0.5), g_Time * g_Speed); #if EDGES == 4 vec2 x=rotateVec2(vec2(-baseDirection.y, baseDirection.x), 0); #endif}");
Write("shaders/effects/shine_cast.frag", "void main(){}");
Write("shaders/effects/scroll.vert", "uniform float g_Time; uniform float g_ScrollX; uniform float g_ScrollY; void main(){vec2 scroll=vec2(g_ScrollX,g_ScrollY); scroll = sign(scroll) * pow(vec2(g_ScrollX, g_ScrollY), CAST2(2.0)); v_Scroll = scroll * g_Time;}");
Write("shaders/effects/scroll.frag", "void main(){vec2 uv=frac((v_TexCoord + v_Scroll) * g_Scale);}");
Write("shaders/effects/swing.vert", "// [COMBO] {\"combo\":\"NOISE\",\"type\":\"options\",\"default\":0}\n#if NOISE\n#endif\nuniform float g_Time; uniform float g_Speed; uniform float g_Phase; uniform float g_Amount; void main(){float x=sin(g_Time * g_Speed + g_Phase * 6.28318530718) * g_Amount;}");
Write("shaders/effects/twirl.vert", "// [COMBO] {\"combo\":\"NOISE\",\"type\":\"options\",\"default\":0}\n#if NOISE\n#endif\nuniform float g_Time; uniform float g_Speed; uniform float g_Phase; uniform float g_Amount; void main(){float x=sin(g_Time * g_Speed + g_Phase * 6.28318530718) * g_Amount;}");
Write("shaders/effects/static.frag", "void main(){ float x = 1; }");
string cloudPath = Path.Combine(root, "materials", "util", "clouds_256.tex");
Directory.CreateDirectory(Path.GetDirectoryName(cloudPath)!);
byte[] cloudHeader = new byte[26];
"TEXV0005"u8.CopyTo(cloudHeader); "TEXI0001"u8.CopyTo(cloudHeader.AsSpan(9));
BinaryPrimitives.WriteUInt32LittleEndian(cloudHeader.AsSpan(22), 0); // clampUVs is clear: renderer uses repeat.
File.WriteAllBytes(cloudPath, cloudHeader);
Write("scene.json", """
{
  "objects": [{ "id": 7, "effects": [
    { "file": "effects/water.json", "passes": [{ "constantshadervalues": { "speed": [5] }}]},
    { "file": "effects/sway.json", "passes": [{ "constantshadervalues": { "speed": [0.4] }}]},
    { "file": "effects/foliage.json", "passes": [{ "constantshadervalues": { "speeduv": [1] }}]},
    { "file": "effects/shine.json", "passes": [
      { "constantshadervalues": { "noisespeed": [0.2], "noisescale": [5] }, "textures": [null, null, null] },
      { "constantshadervalues": { "speed": [0.4] }}]}
  ]}, { "id": 8, "effects": [
    { "file": "effects/scroll.json", "passes": [{ "constantshadervalues": { "repeat": "2 4", "speedx": [0.5], "speedy": [-0.25] }}]},
    { "file": "effects/swing.json", "passes": [{ "constantshadervalues": { "speed": [2] }}]},
    { "file": "effects/twirl.json", "passes": [{ "constantshadervalues": { "speed": [0] }}]},
    { "file": "effects/water.json", "visible": false, "passes": [{ "constantshadervalues": { "speed": [5] }}]},
    { "file": "effects/static.json", "passes": [{}]}
  ]}]
}
""");
using var source = new ProjectSource(root);
JsonObject scene = source.ReadJson(source.SceneResource);
var analysis = ShaderPeriodAnalysis.Analyze(scene, source, null, [7, 8]);
Check(analysis.Components.Count == 7, "only source-verified periodic shader mechanisms are extracted");
var water = analysis.Components.Single(x => x.Patch.ConstantKey == "speed" && x.Patch.EffectIndex == 0);
Check(water.Component.AllowRetime && Math.Abs(water.Component.BasePeriod!.Seconds - 2 * Math.PI / 5) < 1e-12 &&
      water.Patch.OwnerLayerId == 7 && water.Patch.PassIndex == 0 && water.Patch.OldValue == 5,
    "water-wave equation yields an analytic retimable component and exact patch location");
var sway = analysis.Components.Single(x => x.Patch.OwnerLayerId == 7 && x.Patch.EffectIndex == 1);
Check(!sway.Component.AllowRetime && sway.Component.BasePeriod!.ExactSeconds == new CommonLoopRational(5, 2),
    "auto-sway M_PI_2 equation yields the exact fixed reciprocal period");
Check(analysis.Unresolved.Single().Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
    "unrecognized foliage timing remains unsupported without a name-based drift claim");
var shineRay = analysis.Components.Single(x => x.Component.Id.EndsWith("ray-speed", StringComparison.Ordinal));
var shineNoise = analysis.Components.Single(x => x.Component.Id.EndsWith("noise-speed", StringComparison.Ordinal));
Check(shineRay.Patch.PassIndex == 1 && Math.Abs(shineRay.Component.BasePeriod!.Seconds - Math.PI / .8) < 1e-12 &&
      shineNoise.Patch.PassIndex == 0 && Math.Abs(shineNoise.Component.BasePeriod!.Seconds - 2) < 1e-12,
    "verified four-direction shine and dual repeat UV paths yield separate retimable speed patches");
var scrollX = analysis.Components.Single(x => x.Component.Id.EndsWith("scroll-x", StringComparison.Ordinal));
var scrollY = analysis.Components.Single(x => x.Component.Id.EndsWith("scroll-y", StringComparison.Ordinal));
Check(scrollX.Patch.SpeedExponent == 2 && Math.Abs(scrollX.Component.BasePeriod!.Seconds - 2) < 1e-12 &&
      Math.Abs(scrollY.Component.BasePeriod!.Seconds - 4) < 1e-12 && scrollY.Patch.OldValue < 0,
    "verified squared scroll speed, direction, and independent repeat axes yield quadratic patches");
var swing = analysis.Components.Single(x => x.Patch.OwnerLayerId == 8 && x.Patch.ConstantKey == "speed");
Check(Math.Abs(swing.Component.BasePeriod!.Seconds - Math.PI) < 1e-12,
    "noise-disabled verified sine timing yields a retimable 2pi-over-speed period");
Check(!analysis.Components.Any(x => x.Patch.OwnerLayerId == 8 && x.Component.Id.Contains("twirl", StringComparison.Ordinal)),
    "static zero sine speed contributes no time component");
Check(analysis.Unresolved.Count == 1, "literal-disabled and time-free effects do not create temporal unresolved entries");
var unselected = ShaderPeriodAnalysis.Analyze(scene, source, null, [999]);
Check(unselected.Components.Count == 0 && unselected.Unresolved.Count == 0, "selection limits analysis to requested layers");
var altered = scene.DeepClone().AsObject();
altered["objects"]![0]!["effects"]![0]!["file"] = "effects/water-bad.json";
var alteredSway = scene.DeepClone().AsObject();
alteredSway["objects"]![0]!["effects"]![1]!["file"] = "effects/sway-bad.json";
foreach ((string label, string shader) in new[] {
    ("scaled", "uniform float g_Time; uniform float g_Speed; void main(){ float distance = g_Time * g_Speed; distance *= 2.0; float x = sin(distance); }"),
    ("additional-clock", "uniform float g_Time; uniform float g_Speed; void main(){ float distance = g_Time * g_Speed; distance += 0.1 * g_Time; float x = sin(distance); }"),
    ("reassigned", "uniform float g_Time; uniform float g_Speed; void main(){ float distance = g_Time * g_Speed; distance = distance * 2.0; float x = sin(distance); }") })
{
    Write("shaders/effects/waterwaves-bad.frag", shader);
    ShaderPeriodAnalysisResult rejected = ShaderPeriodAnalysis.Analyze(altered, source, null, [7]);
    Check(rejected.Components.Count == 3 && rejected.Unresolved.Any(x => x.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism),
        $"water-wave {label} clock rewrite is not accepted");
}
foreach ((string label, string shader) in new[] {
    ("scaled", "uniform float g_Time; uniform float g_Speed; void main(){ float thisMotionTime = g_Time * g_Speed; thisMotionTime *= 2.0; float x = sin(thisMotionTime * M_PI_2); }"),
    ("additional-clock", "uniform float g_Time; uniform float g_Speed; void main(){ float thisMotionTime = g_Time * g_Speed; thisMotionTime += 0.1 * g_Time; float x = sin(thisMotionTime * M_PI_2); }"),
    ("reassigned", "uniform float g_Time; uniform float g_Speed; void main(){ float thisMotionTime = g_Time * g_Speed; thisMotionTime = thisMotionTime * 2.0; float x = sin(thisMotionTime * M_PI_2); }") })
{
    Write("shaders/effects/auto_sway-bad.vert", shader);
    ShaderPeriodAnalysisResult rejected = ShaderPeriodAnalysis.Analyze(alteredSway, source, null, [7]);
    Check(rejected.Components.Count == 3 && rejected.Unresolved.Any(x => x.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism),
        $"auto-sway {label} clock rewrite is not accepted");
}
BinaryPrimitives.WriteUInt32LittleEndian(cloudHeader.AsSpan(22), 0x2);
File.WriteAllBytes(cloudPath, cloudHeader);
var clamped = ShaderPeriodAnalysis.Analyze(scene, source, null, [7]);
Check(clamped.Components.Count == 2 && clamped.Unresolved.Any(x => x.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism),
    "a clampUVs TEX header prevents shine noise from being claimed periodic");
var noisy = scene.DeepClone().AsObject();
noisy["objects"]![1]!["effects"]![1]!["passes"]![0]!["combos"] = new JsonObject { ["NOISE"] = 1 };
var noisyResult = ShaderPeriodAnalysis.Analyze(noisy, source, null, [8]);
Check(noisyResult.Components.Count == 2 && noisyResult.Unresolved.Single().Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
    "enabled sine noise is rejected instead of inferring a simple period");
Console.WriteLine($"passed {passed.Count}: {string.Join(", ", passed)}");
