using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>引擎时间签名到结论与调速的映射：分析没推下去的只记未收敛；旋钮 token 唯一才调频；旋钮补差与慢分量漂移上界的算式。</summary>
internal static class ShaderSignatureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Directory.CreateTempSubdirectory("shader-signature-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "scene.json"), """{"objects":[{"id":10}]}""");
            Directory.CreateDirectory(Path.Combine(root, "shaders", "effects"));
            File.WriteAllText(Path.Combine(root, "shaders", "effects", "x.frag"), "void main() { gl_FragColor = vec4(sin(g_Time * 0.5)); }");
            using var source = new ProjectSource(root);
            foreach (string reason in (string[])["analysis_not_converged:op199 @fragment", "analysis_not_converged @fragment", "unsupported_side_effect",
                "names_stripped", "spirv_unreadable", "time_rate_not_constant @fragment", "scroll_rate_not_constant:s @fragment",
                "drift_rate_not_constant @fragment", "too_many_periods", "sampler_wrap_unknown:s @fragment",
                "linear_time_through_glsl8 @fragment mod %121", "linear_time_through_glsl40 @fragment", "linear_time_through_mix @fragment",
                "linear_time_through_sample_coordinate:s @fragment", "linear_time_through_mod @fragment", "nonlinear_time @fragment"])
            {
                var result = Analyze(source, reason);
                check(result.Unresolved.Count == 1 && result.Unresolved[0].Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                    $"engine reason '{reason}' stays not converged");
            }
            foreach (string reason in (string[])["drift @fragment", "branch_on_linear_time @fragment", "loop_count_time_dependent @fragment",
                "compare_with_linear_time @fragment"])
            {
                var result = Analyze(source, reason);
                check(result.Unresolved.Count == 1 && result.Unresolved[0].Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                    $"engine reason '{reason}' is a proof of cannot");
            }
            // 视差位置在烘焙里是定值，不算外部输入；指针是
            check(Analyze(source, "", "g_ParallaxPosition").Unresolved.Count == 0, "parallax position on a baked layer is not a live input");
            check(Analyze(source, "", "g_PointerPosition").Unresolved is [{ Kind: ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism }],
                "pointer position is a live input");

            // 旋钮 token：字面量按 float32 值、忽略符号、不算注释；uniform 不算声明行
            string text = "uniform float g_Speed; // {\"material\":\"speed\",\"default\":0.5}\nfloat a = sin(g_Time * g_Speed) * -0.16161616; /* 0.16161616 */";
            string literal = ShaderTextPatch.KnobKey(new JsonObject { ["stage"] = "frag", ["literal"] = -0.16161616 });
            check(ShaderTextPatch.KnobUses(text, literal).Length == 1 && ShaderTextPatch.KnobUses(text + "\nfloat b = 0.16161616f;", literal).Length == 2 &&
                ShaderTextPatch.KnobUses(text, "periodica_k_frag_g_Speed").Length == 1 &&
                ShaderTextPatch.KnobUses(text, ShaderTextPatch.KnobKey(new JsonObject { ["stage"] = "frag", ["literal"] = 0.5 })).Length == 0,
                "a knob token is matched by float32 value outside comments, and a uniform knob outside its declaration");

            // 同一 pass：3 s 项走时间倍率，7.1 s 项有唯一旋钮 0.5 单独调频，100000 s 项是慢分量
            JsonObject loop = LoopAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null, Runtime("""
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[
                  {"seconds":3,"num":3,"den":1,"pi":0,"knobs":[]},
                  {"seconds":7.1,"num":71,"den":10,"pi":0,"knobs":[{"stage":"frag","literal":0.5,"inverse":false}]},
                  {"seconds":100000,"num":100000,"den":1,"pi":0,"knobs":[]}]}
                """), [10], 30, 1).ToJson();
            JsonObject candidate = loop["candidates"]![0]!.AsObject();
            double Speed(string id) => candidate["components"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == id)!["speed_multiplier"]!.GetValue<double>();
            double? Patch(string key) => candidate["patches"]!.AsArray().SingleOrDefault(x => x!["constant_key"]!.GetValue<string>() == key)?["new_value"]!.GetValue<double>();
            double time = Speed("shader/10/0/0/effects/x"), knob = Speed("shader/10/0/0/effects/x/periodica_k_frag_3f000000");
            check(time != 1 && Patch(ShaderPeriodAnalysis.TimeScaleKey) == time &&
                Math.Abs(Patch("periodica_k_frag_3f000000")!.Value - knob / time) < 1e-12,
                "a knob patch carries the component multiplier divided by the pass time multiplier");
            JsonObject slow = candidate["slow_components"]![0]!.AsObject();
            // 有效周期下界 T = 100000/(1+2%)（同 pass 时间倍率也乘在它上面）
            double slowPeriod = 100000 / 1.02;
            check(Math.Abs(slow["period_seconds"]!.GetValue<double>() - slowPeriod) < 1e-9 &&
                Math.Abs(slow["drift_bound_radians"]!.GetValue<double>() - 2 * Math.PI * candidate["seconds"]!.GetValue<double>() / slowPeriod) < 1e-12 &&
                !candidate["components"]!.AsArray().Any(x => x!["id"]!.GetValue<string>().Contains("slow", StringComparison.Ordinal)),
                "a slow component stays out of the solver and reports a 2πP/T drift bound");

            // 同一 pass 剩 7 s 与 3π s 两类且没有旋钮：每项独立调频有解（上限 600 s）记未收敛 term_not_retimable；
            // 上限 10 s 时独立调频也无解，才是"不能"
            string split = """
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[
                  {"seconds":7,"num":7,"den":1,"pi":0,"knobs":[]},
                  {"seconds":9.42477796076938,"num":3,"den":1,"pi":1,"knobs":[]}]}
                """;
            string[] Landing(double ceiling) => [.. LoopAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null,
                Runtime(split), [10], 30, 1, loopLengthMaximumSeconds: ceiling).ToJson()["unresolved"]!.AsArray()
                .Select(x => $"{x!["kind"]}/{x["mechanism"]}")];
            check(Landing(600) is ["UnsupportedShaderMechanism/term_not_retimable"] &&
                Landing(10) is ["NonPeriodicOrDriftingMechanism/loop_never_repeats_within_limit"],
                "irrational classes in one pass are cannot only when independent retiming of every term also has no loop");
        }
        finally { Directory.Delete(root, true); }
    }

    private static JsonObject Runtime(string signature) =>
        new() { ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 10, ["materials"] = new JsonArray(new JsonObject {
            ["shader"] = "effects/x", ["effect"] = 0, ["pass"] = 0, ["active_uniforms"] = new JsonArray(), ["time_signature"] = JsonNode.Parse(signature) }) }) };

    private static ShaderPeriodAnalysisResult Analyze(ProjectSource source, string reason, string external = "") =>
        ShaderPeriodAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null,
            Runtime(new JsonObject { ["kind"] = "aperiodic", ["terms"] = new JsonArray(),
                ["reasons"] = reason.Length > 0 ? new JsonArray(reason) : new JsonArray(),
                ["external"] = external.Length > 0 ? new JsonArray(external) : new JsonArray(), ["transient"] = false }.ToJsonString()), [10], 600, 2);
}
