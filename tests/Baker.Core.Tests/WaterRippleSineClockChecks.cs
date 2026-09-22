using System.Text;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// waterripple 法线贴图滚动与结构化正弦时钟两条规则。着色器只保留与时间有关的骨架行，计数与官方源一致。
/// </summary>
internal static class WaterRippleSineClockChecks
{
    private const string RippleFragment = """
        uniform sampler2D g_Texture2; // {"label":"ui_editor_properties_water_normal"}
        #if PERSPECTIVE == 0
        varying vec4 v_TexCoordRipple;
        #else
        uniform vec4 g_Texture0Resolution;
        uniform float g_Time;
        uniform float g_AnimationSpeed; // {"material":"animationspeed","default":0.15}
        uniform float g_Scale; // {"material":"scale","default":1}
        uniform float g_ScrollSpeed; // {"material":"scrollspeed","default":0}
        uniform float g_Direction; // {"material":"scrolldirection","default":0}
        uniform float g_Ratio; // {"material":"ratio","default":1}
        varying vec3 v_TexCoordPerspective;
        #endif
        void main() {
        	vec4 rippleCoords;
        #if PERSPECTIVE == 0
        	rippleCoords = v_TexCoordRipple;
        #else
        	vec2 coordsRotated = v_TexCoordPerspective.xy / v_TexCoordPerspective.z;
        	vec2 coordsRotated2 = coordsRotated * 1.333;
        	vec2 scroll = rotateVec2(vec2(0, 1), g_Direction) * g_ScrollSpeed * g_ScrollSpeed * g_Time;
        	rippleCoords.xy = coordsRotated + g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;
        	rippleCoords.zw = coordsRotated2 - g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;
        	rippleCoords *= g_Scale;
        	float rippleTextureAdjustment = (g_Texture0Resolution.x / g_Texture0Resolution.y);
        	rippleCoords.xz *= rippleTextureAdjustment;
        	rippleCoords.yw *= g_Ratio;
        #endif
        	vec3 n1 = texSample2D(g_Texture2, rippleCoords.xy).xyz * 2 - 1;
        	vec3 n2 = texSample2D(g_Texture2, rippleCoords.zw).xyz * 2 - 1;
        }
        """;

    private const string RippleVertex = """
        #if PERSPECTIVE == 0
        varying vec4 v_TexCoordRipple;
        uniform vec4 g_Texture0Resolution;
        uniform float g_Time;
        uniform float g_AnimationSpeed; // {"material":"animationspeed","default":0.15}
        uniform float g_Scale; // {"material":"scale","default":1}
        uniform float g_ScrollSpeed; // {"material":"scrollspeed","default":0}
        uniform float g_Direction; // {"material":"scrolldirection","default":0}
        uniform float g_Ratio; // {"material":"ratio","default":1}
        #endif
        void main() {
        #if PERSPECTIVE == 0
        	vec2 coordsRotated = v_TexCoord.xy;
        	vec2 coordsRotated2 = v_TexCoord.xy * 1.333;
        	vec2 scroll = rotateVec2(vec2(0, 1), g_Direction) * g_ScrollSpeed * g_ScrollSpeed * g_Time;
        	v_TexCoordRipple.xy = coordsRotated + g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;
        	v_TexCoordRipple.zw = coordsRotated2 - g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;
        	v_TexCoordRipple *= g_Scale;
        	float rippleTextureAdjustment = (g_Texture0Resolution.x / g_Texture0Resolution.y);
        	v_TexCoordRipple.xz *= rippleTextureAdjustment;
        	v_TexCoordRipple.yw *= g_Ratio;
        #endif
        }
        """;

    private const string OffsetWaveFragment = """
        // [COMBO] {"material":"Standardized time unit","combo":"STD_TIME","type":"options","default":0}
        #include "common.h"
        uniform float g_Time;
        uniform float g_Speed; // {"material":"speed","default":5}
        uniform float g_Offset; // {"material":"offset","default":0}
        uniform float u_PhaseOffset; // {"material":"globleTimeOffset","default":0}
        void main() {
        #if MASK
        	float mask = texSample2D(g_Texture1, v_TexCoord.zw).r;
        #else
        	float mask = 1.0;
        #endif
        #if STD_TIME
        	float distance = g_Time * g_Speed * M_PI + dot(texCoordMotion, v_Direction) * g_Scale;
        #else
        	float distance = g_Time * g_Speed + dot(texCoordMotion, v_Direction) * g_Scale;
        #endif
        #if DUALWAVES == 1
        	float distance2 = (g_Time + g_Offset2) * g_Speed2 + dot(texCoordMotion, v_Direction2) * g_Scale2;
        #endif
        #if PERSPECTIVE == 1
        	distance *= step(0.0, v_TexCoordPerspective.z);
        #endif
        #if TIMEOFFSET
        	float timeOffset = texSample2D(g_Texture2, v_TexCoord.zw).r * M_PI_2;
        	distance += timeOffset;
        #endif
        	distance += u_PhaseOffset * M_PI_2;
        	float val1 = sin(distance) + g_Offset;
        	float s1 = sign(val1);
        	texCoord += val1 * s1 * offset * mask;
        }
        """;

    private const string OffsetWaveVertex = """
        // [COMBO] {"material":"ui_editor_properties_perspective","combo":"PERSPECTIVE","type":"options","default":0}
        // [COMBO] {"material":"ui_editor_properties_dual_waves","combo":"DUALWAVES","type":"options","default":0}
        #include "common.h"
        uniform float g_Time;
        """;

    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "water-ripple-sine-clock-source");
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        void Effect(string name, string fragment, string vertex)
        {
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
            Write($"materials/{name}.json", $"{{\"passes\":[{{\"shader\":\"{name}\"}}]}}");
            Write($"shaders/{name}.frag", fragment);
            Write($"shaders/{name}.vert", vertex);
        }
        Effect("ripple", RippleFragment, RippleVertex);
        // 多出一处 g_Time 使用就不再是这组方程。
        Effect("ripple-extra-clock", RippleFragment.Replace("vec3 n2 =", "float extra = g_Time; vec3 n2 =", StringComparison.Ordinal), RippleVertex);
        Effect("offset-wave", OffsetWaveFragment, OffsetWaveVertex);
        Effect("offset-wave-cos", OffsetWaveFragment.Replace("float s1 = sign(val1);", "float s1 = cos(distance);", StringComparison.Ordinal), OffsetWaveVertex);
        Effect("offset-wave-drift", OffsetWaveFragment.Replace("distance += u_PhaseOffset * M_PI_2;", "distance += g_Time * 0.125;", StringComparison.Ordinal), OffsetWaveVertex);
        // 去掉 STD_TIME 的默认值声明：两条定义都保留、系数不同，不能裁定。
        Effect("offset-wave-undeclared", OffsetWaveFragment.Replace("// [COMBO] {\"material\":\"Standardized time unit\",\"combo\":\"STD_TIME\",\"type\":\"options\",\"default\":0}", "", StringComparison.Ordinal), OffsetWaveVertex);
        WriteTexture(Path.Combine(sourceDirectory, "materials", "effects", "repeatnormal.tex"), flags: 0);
        WriteTexture(Path.Combine(sourceDirectory, "materials", "effects", "clampnormal.tex"), flags: 2);

        JsonObject Ripple(int id, string size, double animation, double scroll, double scale, double ratio,
            string texture = "effects/repeatnormal", string effect = "ripple", bool fullscreen = false)
        {
            var owner = new JsonObject { ["id"] = id, ["size"] = size, ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{effect}.json", ["passes"] = new JsonArray(new JsonObject {
                    ["textures"] = new JsonArray(null, null, texture),
                    ["constantshadervalues"] = new JsonObject { ["animationspeed"] = animation, ["scrollspeed"] = scroll,
                        ["scale"] = scale, ["ratio"] = ratio, ["scrolldirection"] = -1.019874 } }) }) };
            if (fullscreen) owner["fullscreen"] = true;
            return owner;
        }
        var rippleScene = new JsonObject { ["objects"] = new JsonArray(
            Ripple(1, "1000.00000 1000.00000", 0.5, 0, 2, 0.5),
            Ripple(2, "5120.00000 1340.00000", 0.07, 0, 1, 1),
            Ripple(3, "1920.00000 1080.00000", 0.05, 0.05, 2, 1),
            Ripple(4, "1000.00000 1000.00000", 0.5, 0, 2, 0.5, texture: "effects/clampnormal"),
            Ripple(5, "1000.00000 1000.00000", 0.5, 0, 2, 0.5, fullscreen: true),
            Ripple(6, "1000.00000 1000.00000", 0.5, 0, 2, Math.Sqrt(2)),
            Ripple(7, "1000.00000 1000.00000", 0, 0, 2, 0.5),
            Ripple(8, "1000.00000 1000.00000", 0.5, 0, 2, 0.5, effect: "ripple-extra-clock"),
            Ripple(9, "1000.70000 500.20000", 0.5, 0, 1, 1)) };
        Write("scene.json", rippleScene.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult ripple = ShaderPeriodAnalysis.Analyze(rippleScene, source, null, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        ShaderTemporalUnresolved Unresolved(ShaderPeriodAnalysisResult result, int owner) => result.Unresolved.Single(item => item.OwnerLayerId == owner);

        ShaderPeriodComponent first = ripple.Components.Single(item => item.Patch.OwnerLayerId == 1);
        // 1000/1000 与 ratio 0.5 的有理公约数是 1/2：周期 1/(0.25·2·0.5) = 4 秒。
        check(Math.Abs(first.Component.BasePeriod!.Seconds - 4) < 1e-9 && first.Component.AllowRetime &&
            first.Patch.ConstantKey == "animationspeed" && first.Patch.SpeedExponent == 2,
            "water ripple with scroll off yields 1/(animationspeed²·scale·gcd(W/H, ratio)) retimed through animationspeed²");
        ShaderTemporalUnresolved slow = Unresolved(ripple, 2);
        // 5120/1340 = 256/67，与 ratio 1 的公约数 1/67：周期 67/0.0049 ≈ 13673 秒，超出上限。
        check(slow.Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism && slow.BoundedDisplacement &&
            slow.Mechanism == ShaderPeriodAnalysis.WaterRippleScrollMechanism && slow.Detail.Contains("13673", StringComparison.Ordinal) &&
            !ripple.Components.Any(item => item.Patch.OwnerLayerId == 2),
            "an exact water-ripple period past the loop ceiling stays a bounded-displacement unresolved item, not a solver component");
        check(Unresolved(ripple, 3).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            Unresolved(ripple, 3).Detail.Contains("scrollspeed 0.05", StringComparison.Ordinal),
            "water ripple with scroll on names the scroll term instead of claiming a period");
        check(Unresolved(ripple, 4).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            Unresolved(ripple, 5).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            Unresolved(ripple, 6).Kind == ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
            "clamped normal texture, fullscreen render target and a non-simple ratio are not given a period");
        check(!ripple.Components.Any(item => item.Patch.OwnerLayerId == 7) && !ripple.Unresolved.Any(item => item.OwnerLayerId == 7),
            "water ripple with both speeds zero has no motion and constrains no loop");
        check(Unresolved(ripple, 8).Message?.Key == "unresolved.shader_not_verified_periodic",
            "an extra clock use falls back to the unverified shader verdict");
        // rt 尺寸按 size 截断取整：1000×500，ratio 1 → 公约数 1，周期 1/0.25 = 4 秒。
        check(Math.Abs(ripple.Components.Single(item => item.Patch.OwnerLayerId == 9).Component.BasePeriod!.Seconds - 4) < 1e-9,
            "the effect render-target size is the truncated layer size");

        // 合并 fix/loop-ceiling 后：超上限的判定读本次分析实际用的上限。400 秒周期在 180 秒上限下是未解析项，在 600 秒上限下是分量。
        var mediumScene = new JsonObject { ["objects"] = new JsonArray(Ripple(10, "1000.00000 1000.00000", 0.05, 0, 1, 1)) };
        ShaderPeriodAnalysisResult medium180 = ShaderPeriodAnalysis.Analyze(mediumScene, source, null, [10], 180);
        ShaderPeriodAnalysisResult medium600 = ShaderPeriodAnalysis.Analyze(mediumScene, source, null, [10], 600);
        check(medium180.Components.Count == 0 && Unresolved(medium180, 10).Mechanism == ShaderPeriodAnalysis.WaterRippleScrollMechanism &&
            Unresolved(medium180, 10).Detail.Contains("180-second loop ceiling", StringComparison.Ordinal) &&
            medium600.Unresolved.Count == 0 && Math.Abs(medium600.Components.Single().Component.BasePeriod!.Seconds - 400) < 1e-6,
            "the water-ripple period is compared with the loop ceiling this analysis was given, not a fixed default");

        JsonObject rippleReport = HybridLoopService.Analyze(rippleScene, source, null, new JsonObject(), [1], 60, 1);
        JsonObject ripplePatch = rippleReport["candidates"]!.AsArray().First()!["patches"]!.AsArray().OfType<JsonObject>()
            .Single(patch => patch["constant_key"]!.GetValue<string>() == "animationspeed");
        check(ripplePatch["speed_exponent"]!.GetValue<double>() == 2 && ripplePatch["owner_layer_id"]!.GetValue<int>() == 1,
            "the water-ripple candidate rewrites animationspeed with the squared-speed exponent");

        JsonObject Wave(int id, string effect, double speed, JsonObject? combos = null)
        {
            var pass = new JsonObject { ["constantshadervalues"] = new JsonObject { ["speed"] = speed, ["globleTimeOffset"] = 0.35 } };
            if (combos is not null) pass["combos"] = combos;
            return new JsonObject { ["id"] = id, ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{effect}.json", ["passes"] = new JsonArray(pass) }) };
        }
        var waveScene = new JsonObject { ["objects"] = new JsonArray(
            Wave(11, "offset-wave", 3),
            Wave(12, "offset-wave", 2, new JsonObject { ["STD_TIME"] = 1 }),
            Wave(13, "offset-wave", 3, new JsonObject { ["DUALWAVES"] = 1 }),
            Wave(14, "offset-wave", 3, new JsonObject { ["STD_TIME"] = "on" }),
            Wave(15, "offset-wave-cos", 3),
            Wave(16, "offset-wave-drift", 3),
            Wave(17, "offset-wave-undeclared", 3),
            Wave(18, "offset-wave", 0),
            Wave(19, "offset-wave", 3, new JsonObject { ["MASK"] = 1, ["TIMEOFFSET"] = 1, ["PERSPECTIVE"] = 1 })) };
        ShaderPeriodAnalysisResult wave = ShaderPeriodAnalysis.Analyze(waveScene, source, null, [11, 12, 13, 14, 15, 16, 17, 18, 19]);
        ShaderPeriodComponent Component(int owner) => wave.Components.Single(item => item.Patch.OwnerLayerId == owner);
        check(Math.Abs(Component(11).Component.BasePeriod!.Seconds - 2 * Math.PI / 3) < 1e-12 && Component(11).Patch.ConstantKey == "speed" &&
            Math.Abs(Component(19).Component.BasePeriod!.Seconds - 2 * Math.PI / 3) < 1e-12,
            "a sine clock with static phase offsets, an output bias, a step gate and a texture phase keeps period 2π/speed");
        check(Math.Abs(Component(12).Component.BasePeriod!.Seconds - 1) < 1e-12,
            "the standardized time unit multiplies the clock by π, so the period becomes 2/speed");
        check(wave.Unresolved.Where(item => item.OwnerLayerId is 13 or 14 or 15 or 16 or 17)
                .All(item => item.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism) &&
            wave.Unresolved.Count(item => item.OwnerLayerId is 13 or 14 or 15 or 16 or 17) == 5 &&
            !wave.Components.Any(item => item.Patch.OwnerLayerId is 13 or 14 or 15 or 16 or 17),
            "dual waves, unreadable or undeclared time-unit combos, a non-sine clock use and a clock-driven offset stay unresolved");
        check(!wave.Components.Any(item => item.Patch.OwnerLayerId == 18) && !wave.Unresolved.Any(item => item.OwnerLayerId == 18),
            "a zero-speed sine clock constrains no loop");
    }

    private static void WriteTexture(string path, uint flags)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0"));
        writer.Write(0); writer.Write(flags);
        writer.Write(64); writer.Write(64); writer.Write(64); writer.Write(64); writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("TEXB0001\0"));
        writer.Write(new byte[32]);
    }
}
