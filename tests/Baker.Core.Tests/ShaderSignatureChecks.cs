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
            File.WriteAllText(Path.Combine(root, "shaders", "effects", "x.frag"), "void main() { gl_FragColor = vec4(sin(g_Time * 0.5), sin(g_Time * 0.25), 0.0, 1.0); }");
            File.Copy(Path.Combine(root, "shaders", "effects", "x.frag"), Path.Combine(root, "shaders", "effects", "foliagesway.frag"));
            using var source = new ProjectSource(root);
            foreach (string reason in (string[])["analysis_not_converged:op199 @fragment", "analysis_not_converged @fragment", "unsupported_side_effect",
                "names_stripped", "spirv_unreadable", "time_rate_not_constant @fragment", "scroll_rate_not_constant:s @fragment",
                "drift_rate_not_constant @fragment", "too_many_periods", "sampler_wrap_unknown:s @fragment",
                "linear_time_through_glsl8 @fragment mod %121", "linear_time_through_glsl40 @fragment", "linear_time_through_mix @fragment",
                "linear_time_through_sample_coordinate:s @fragment", "linear_time_through_mod @fragment", "nonlinear_time @fragment",
                "branch_on_linear_time: threshold range unknown @fragment", "loop_count_time_dependent @fragment",
                "compare_with_linear_time: threshold range unknown @fragment"])
            {
                var result = Analyze(source, reason);
                check(result.Unresolved.Count == 1 && result.Unresolved[0].Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                    $"engine reason '{reason}' stays not converged");
            }
            foreach (string reason in (string[])["drift @fragment",
                "compare_with_unbounded_time: compare_with_linear_time against tan(linear time), whose poles flip it every period at a drifting phase @fragment"])
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

            // iris 形态（引擎对同构着色器给出的签名）：1 s 步进项走时间倍率，1.9、2.5 两项走字面量旋钮，sin/cos(clock) 这一项走调用旋钮，
            // 源码里 sin(clock)、cos(clock) 恰好是引擎数到的 2 处才改写；多一处（别的写法、预处理分支）就不是只调这一项，仍记未收敛
            string iris = "uniform float g_Time;\nuniform float g_Speed;\nvoid main() {\n  float clock = g_Time * g_Speed + 0.31;\n  float n = floor(clock);\n" +
                "  vec2 a = sin(1.9 * (n + vec2(0, 1)));\n  vec4 b = sin(2.5 * (n + vec4(0, 0, 1, 1)));\n" +
                "  vec2 d = mix(vec2(a.x, b.x), vec2(a.y, b.w), fract(clock)) + vec2(sin(clock), cos( clock )) * 0.25;\n  gl_Position = vec4(d, 0.0, 1.0);\n}\n";
            string irisSignature = """
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[
                  {"seconds":1.0309278046442463,"num":100,"den":97,"pi":0,"knobs":[{"stage":"vert","varying":"v_Look","component":0,"inverse":false},{"stage":"vert","varying":"v_Look","component":1,"inverse":false},{"stage":"vert","uniform":"g_Speed","inverse":false}]},
                  {"seconds":3.409216061150358,"num":2000,"den":1843,"pi":1,"knobs":[{"stage":"vert","varying":"v_Look","component":0,"inverse":false},{"stage":"vert","literal":1.899999976158142,"inverse":false},{"stage":"vert","uniform":"g_Speed","inverse":false}]},
                  {"seconds":6.477510434903635,"num":200,"den":97,"pi":1,"knobs":[{"stage":"vert","call":"clock","sites":2,"inverse":false},{"stage":"vert","varying":"v_Look","component":0,"inverse":false},{"stage":"vert","varying":"v_Look","component":1,"inverse":false},{"stage":"vert","uniform":"g_Speed","inverse":false}]},
                  {"seconds":2.591004173961454,"num":80,"den":97,"pi":1,"knobs":[{"stage":"vert","varying":"v_Look","component":1,"inverse":false},{"stage":"vert","literal":2.5,"inverse":false},{"stage":"vert","uniform":"g_Speed","inverse":false}]}]}
                """;
            JsonObject IrisLoop(string vert)
            {
                File.WriteAllText(Path.Combine(root, "shaders", "effects", "x.vert"), vert);
                using var irisSource = new ProjectSource(root);
                return LoopAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), irisSource, null, Runtime(irisSignature), [10], 30, 1,
                    3, CommonLoopPreference.Balanced, loopLengthMaximumSeconds: 600).ToJson();
            }
            JsonObject closed = IrisLoop(iris);
            string[] irisKeys = [.. closed["candidates"]!.AsArray().FirstOrDefault()?["patches"]!.AsArray().Select(x => x!["constant_key"]!.GetValue<string>()) ?? []];
            check(closed["unresolved"]!.AsArray().Count == 0 && irisKeys.Contains("periodica_k_vert_call2_clock") &&
                irisKeys.Contains("periodica_k_vert_3ff33333") && irisKeys.Contains("periodica_k_vert_40200000") &&
                closed["candidates"]![0]!["components"]!.AsArray().All(x => Math.Abs(x!["speed_multiplier"]!.GetValue<double>() - 1) <= 0.03),
                "iris terms each retime on their own (step grid, 1.9, 2.5, and sin/cos(clock) through a call knob) within the budget");
            check(ShaderTextPatch.KnobUses(iris, "periodica_k_vert_call2_clock").Length == 2 &&
                IrisLoop(iris.Replace("gl_Position = vec4(d", "gl_Position = vec4(d + sin(clock)"))["unresolved"]!.AsArray()
                    .Select(x => x!["mechanism"]!.GetValue<string>()).SequenceEqual(["term_not_retimable"]),
                "a call knob rewrites only when the source has exactly the call sites the engine counted");

            // 完整捕获看到的是已改写 shader：同一 call 旋钮变成两个 g_PeriodicaK 使用处。
            // 只凭原源码查这个合成 uniform 会误拒；已写出的 patch 记录和原来两处 sin/cos 才能恢复身份。
            File.WriteAllText(Path.Combine(root, "shaders", "effects", "x.vert"), iris);
            using (var original = new ProjectSource(root))
            {
                JsonObject patched = Runtime(irisSignature);
                JsonArray terms = patched["runtime_layers"]![0]!["materials"]![0]!["time_signature"]!["terms"]!.AsArray();
                foreach (JsonObject term in terms.OfType<JsonObject>())
                    term["knobs"]!.AsArray().Add(new JsonObject { ["stage"] = "vert", ["uniform"] = "g_PeriodicaTimeScale", ["inverse"] = false });
                JsonArray callKnobs = terms[2]!["knobs"]!.AsArray();
                int callIndex = Enumerable.Range(0, callKnobs.Count).Single(i => callKnobs[i]?["call"] is not null);
                callKnobs[callIndex] = new JsonObject { ["stage"] = "vert", ["uniform"] = "g_PeriodicaK_vert_call2_clock", ["inverse"] = false };
                JsonArray installed = new(new JsonObject { ["resource"] = "shaders/effects/x.vert",
                    ["keys"] = new JsonArray("periodica_k_vert_call2_clock", ShaderPeriodAnalysis.TimeScaleKey) });
                JsonObject restored = ShaderPeriodAnalysis.ReconcileInstalledKnobs(patched, original, null, installed);
                JsonArray restoredTerms = restored["runtime_layers"]![0]!["materials"]![0]!["time_signature"]!["terms"]!.AsArray();
                check(restoredTerms[2]!["knobs"]!.AsArray().Any(k => k?["call"]?.GetValue<string>() == "clock" && k["sites"]?.GetValue<int>() == 2) &&
                    restoredTerms.OfType<JsonObject>().All(t => t["knobs"]!.AsArray().All(k => k?["uniform"]?.GetValue<string>() != "g_PeriodicaTimeScale")) &&
                    callKnobs[callIndex]?["uniform"]?.GetValue<string>() == "g_PeriodicaK_vert_call2_clock" &&
                    restoredTerms[2]!["seconds"]!.GetValue<double>() == terms[2]!["seconds"]!.GetValue<double>(),
                    "a proven installed call knob is restored without changing the original runtime or observed period");
                check(ShaderPeriodAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), original, null,
                    patched, [10], 600, 2).Terms.Any(t => t.Missing?.Contains("not a unique rewritable token", StringComparison.Ordinal) == true) &&
                    !ShaderPeriodAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), original, null,
                    restored, [10], 600, 2).Terms.Any(t => t.Missing?.Contains("not a unique rewritable token", StringComparison.Ordinal) == true) &&
                    JsonNode.DeepEquals(ShaderPeriodAnalysis.ReconcileInstalledKnobs(patched, original, null, new JsonArray()), patched),
                    "the patched-runtime false rejection disappears only with recorded patch provenance");
            }

            // 分量旋钮改写在文件末尾追加新 main：varying 声明在预处理条件块里时条件不成立就不存在，覆盖 shader 编不过（3644280276 waterripple），不当改写目标
            string ripple = "varying vec4 v_Plain;\n#if RIPPLE\nvarying vec4 v_TexCoordRipple;\n#endif\nvoid main() { v_Plain = vec4(g_Time); }\n";
            check(ShaderTextPatch.AxisTarget(ripple, "periodica_k_vert_ax_x_v_TexCoordRipple") is (_, _, ["#if RIPPLE"]) &&
                ShaderTextPatch.AxisTarget(ripple, "periodica_k_vert_ax_x_v_Plain") is (_, _, []) &&
                ShaderTextPatch.AxisTarget(ripple.Replace("#if RIPPLE\n", "#if RIPPLE\n#else\n"), "periodica_k_vert_ax_x_v_TexCoordRipple") is null,
                "a vertex output declared inside a preprocessor conditional stays a rewrite target, carrying its guard; one inside an #else branch does not");

            // 振幅推不出的慢项（90.1 s，旋钮 0.25）：逐项预算（这里 0%）内与 7.1 s 项凑不出循环时，整层路线（有视频组）放开它的改速、
            // 证据带实测标记留给分析收尾实测；特效前缀路线（没有视频组）不放开，照旧无解
            LoopReport Measured(JsonArray? groups, bool budgetOnly = false, string shader = "effects/x", bool unmeasured = false) => LoopAnalysis.Analyze(
                JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null, Runtime("""
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[
                  {"seconds":7.1,"num":71,"den":10,"pi":0,"knobs":[{"stage":"frag","literal":0.5,"inverse":false}]},
                  {"seconds":90.1,"num":901,"den":10,"pi":0,"knobs":[{"stage":"frag","literal":0.25,"inverse":false}]}]}
                """, shader), [10], 30, 1, 0, videoGroups: groups, budgetOnlyRetime: budgetOnly, speedUnmeasured: unmeasured);
            LoopReport whole = Measured(new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(10) }));
            const string slowTerm = "shader/10/0/0/effects/x/periodica_k_frag_3e800000";
            check(whole.Candidates.Count > 0 && Math.Abs(whole.Candidates[0].Components.Single(x => x.ComponentId == slowTerm).DeltaPercent) > 0 &&
                whole.Evidence.Single(x => x.Component.Id == slowTerm).Evidence.EndsWith(ShaderPeriodAnalysis.MeasuredNote, StringComparison.Ordinal),
                "a slow term without a provable amplitude is retimed beyond the budget on the whole-layer route, pending a rendered speed check");
            check(Measured(null).Candidates.Count == 0, "the effect-prefix route never retimes a slow term beyond the budget");
            // 实测没放行后的重分析按逐项预算，与没有这条放宽时同一结果；foliagesway 只走 #209 的解析判据，算不出振幅时不进实测
            check(Measured(new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(10) }), budgetOnly: true).Candidates.Count == 0, "after a failed speed check the whole-layer route falls back to the per-term budget");
            // 测速量不到（speedUnmeasured）：同样按逐项预算、无解，但挡住循环的是没能实测的改速，记未收敛 speed_not_measured，不记不能
            JsonArray Kinds(LoopReport report) => [.. report.ToJson()["unresolved"]!.AsArray().Select(x => (JsonNode)$"{x!["kind"]}/{x["mechanism"]}")];
            JsonArray group = new(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(10) });
            check(Kinds(Measured(group.DeepClone().AsArray(), unmeasured: true)).Select(x => x!.GetValue<string>()).SequenceEqual(["UnsupportedShaderMechanism/speed_not_measured"]) &&
                Kinds(Measured(group.DeepClone().AsArray(), budgetOnly: true)).Select(x => x!.GetValue<string>()).SequenceEqual(["NonPeriodicOrDriftingMechanism/loop_never_repeats_within_limit"]),
                "an unmeasurable speed check leaves the blocked layer not converged; a visible one proves cannot");
            LoopReport sway = Measured(new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(10) }), shader: "effects/foliagesway");
            check(sway.Candidates.Count == 0 && !sway.Evidence.Any(x => x.Evidence.EndsWith(ShaderPeriodAnalysis.MeasuredNote, StringComparison.Ordinal)),
                "foliagesway without a computable amplitude stays on the per-term budget and never enters the speed check");

            // 慢分量（100000 s，唯一旋钮 0.25）：默认照旧不进求解器、标可改挂旋钮；闭合预检没过后的重分析（retimeSlow）改挂旋钮，
            // 离原速最近的圈数是 0（冻结，旋钮取 0），证据带实测标记；实测没放行后（budgetOnly）回到慢分量
            LoopCandidate SlowRetimed(bool retimeSlow, bool budgetOnly = false) => LoopAnalysis.Analyze(
                JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null, Runtime("""
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[
                  {"seconds":7.1,"num":71,"den":10,"pi":0,"knobs":[{"stage":"frag","literal":0.5,"inverse":false}]},
                  {"seconds":100000,"num":100000,"den":1,"pi":0,"knobs":[{"stage":"frag","literal":0.25,"inverse":false}]}]}
                """), [10], 30, 1, videoGroups: new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(10) }),
                budgetOnlyRetime: budgetOnly, retimeSlow: retimeSlow).Candidates[0];
            LoopCandidate kept = SlowRetimed(false), frozen = SlowRetimed(true);
            check(kept.SlowComponents is [{ Retimable: true }] && kept.ToJson()["slow_components"]![0]!["retimable"]!.GetValue<bool>() &&
                frozen.SlowComponents.Count == 0 && frozen.Components.Single(x => x.ComponentId == slowTerm).Cycles == 0 &&
                frozen.Patches.OfType<LoopValuePatch>().Single(x => x.ComponentId == slowTerm).NewValue == 0 &&
                SlowRetimed(true, budgetOnly: true).SlowComponents is [{ Retimable: true }],
                "a slow term with a usable knob stays a slow component until its closure fails, then freezes at the nearest cycle count pending the speed check");
            // 凑不出公共循环时点名留实时的是最不值钱的层（画布占比 × 特效 pass，与 bake_value 同口径），不是排在最后的层：
            // 两层各一个不可调速的源材质周期（7.1 s、9.7 s，最小公倍数 688.7 s 超过 600 s 上限），分量个数相同；
            // 小层（1% 画布、无特效）排在前面，大层（占满画布、2 个特效 pass）必须留在视频里，点名的是小层 10
            JsonObject Source(int owner, double seconds, int num, int effects) => new() { ["owner"] = owner, ["materials"] = new JsonArray([
                new JsonObject { ["shader"] = "effects/x", ["active_uniforms"] = new JsonArray(), ["time_signature"] = JsonNode.Parse($$"""
                    {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[{"seconds":{{seconds}},"num":{{num}},"den":10,"pi":0,"knobs":[]}]}
                    """) },
                .. Enumerable.Range(0, effects).Select(_ => (JsonNode)new JsonObject { ["role"] = "effect", ["shader"] = "effects/y", ["active_uniforms"] = new JsonArray() })]) };
            LoopReport valued = LoopAnalysis.Analyze(JsonNode.Parse("""
                {"general":{"orthogonalprojection":{"width":1000,"height":1000}},
                 "objects":[{"id":10,"size":"100 100"},{"id":11,"size":"1000 1000"}]}
                """)!.AsObject(), source, null, new JsonObject { ["runtime_layers"] = new JsonArray(Source(10, 7.1, 71, 0), Source(11, 9.7, 97, 2)) },
                [10, 11], 30, 1, 2, CommonLoopPreference.Balanced, loopLengthMaximumSeconds: 600);
            check(valued.NoCandidateReason?.RetainLiveOwnerLayerIds is [10],
                "with no common loop the least valuable layer (canvas share times effect passes) is named for live retention, not the last one merged");

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
            // "不能"按层证明：10 层两类项单独独立调频有解，只是和 11 层的 29.9 s 项在 30 s 上限内凑不到一起——不是 10 层不能，记未收敛
            JsonObject pair = Runtime(split);
            pair["runtime_layers"]!.AsArray().Add(new JsonObject { ["owner"] = 11, ["materials"] = new JsonArray(new JsonObject {
                ["shader"] = "effects/x", ["effect"] = 0, ["pass"] = 0, ["active_uniforms"] = new JsonArray(), ["time_signature"] = JsonNode.Parse("""
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[{"seconds":29.9,"num":299,"den":10,"pi":0,"knobs":[]}]}
                """) }) });
            check(LoopAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10},{"id":11}]}""")!.AsObject(), source, null, pair, [10, 11], 30, 1,
                loopLengthMaximumSeconds: 30).ToJson()["unresolved"]!.AsArray().Select(x => $"{x!["owner_layer_id"]}/{x["mechanism"]}")
                .SequenceEqual(["10/term_not_retimable"]), "a layer that loops alone is not cannot just because it does not close together with another layer");
            // 同一类的两项（7 s、3.5 s）没有旋钮，靠 pass 时间倍率一起调：10 层按实际模型单独有解，只是和先入选的 11 层凑不到一起，
            // 挡住它的不是缺调频来源，不记 term_not_retimable
            JsonObject shared = Runtime("""
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[
                  {"seconds":7,"num":7,"den":1,"pi":0,"knobs":[]},{"seconds":3.5,"num":7,"den":2,"pi":0,"knobs":[]}]}
                """);
            shared["runtime_layers"]!.AsArray().Insert(0, pair["runtime_layers"]![1]!.DeepClone());
            check(!LoopAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10},{"id":11}]}""")!.AsObject(), source, null, shared, [10, 11], 30, 1,
                loopLengthMaximumSeconds: 30).ToJson()["unresolved"]!.AsArray().Any(x => x!["mechanism"]?.GetValue<string>() == "term_not_retimable"),
                "terms that share one time-scale class are not flagged as needing independent retiming when the layer loops alone");

            // 阈值有界的比较在 settle 时刻后固定：候选整周期预热 L 帧后起录、plan 记 settle 上界；L 不晚于 settle 记未收敛
            JsonObject Settled(double settle) => LoopAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null,
                Runtime($$"""
                {"kind":"periodic","reasons":[],"external":[],"transient":false,"settle_seconds":{{settle}},"terms":[{"seconds":3,"num":3,"den":1,"pi":0,"knobs":[]}]}
                """), [10], 30, 1).ToJson();
            JsonObject early = Settled(2)["candidates"]![0]!.AsObject();
            check(early["source_period_warmup_frames"]!.GetValue<ulong>() == early["frames"]!.GetValue<ulong>() &&
                early["shader_settle_seconds"]!.GetValue<double>() == 2 &&
                Settled(1000)["unresolved"]!.AsArray().Select(x => $"{x!["kind"]}/{x["mechanism"]}").SequenceEqual(["UnsupportedShaderMechanism/transient_settle_beyond_warmup"]),
                "a bounded-threshold branch warms up one whole period past its settle time, and stays not converged when no period is longer");

            // clamp 轴滚动停在边上：入场预热走过 settle，末态只录一帧；给不出收敛时刻的照旧记未收敛。
            JsonObject clampRuntime = Runtime("""{"kind":"static","reasons":[],"external":[],"transient":false,"settle_seconds":4,"terms":[]}""");
            clampRuntime["status"] = "complete";
            clampRuntime["runtime_dependencies"] = new JsonArray();
            clampRuntime["runtime_animation_periods"] = new JsonArray();
            clampRuntime["runtime_layers"]![0]!["has_mesh"] = false;
            clampRuntime["runtime_layers"]![0]!["materials"]![0]!["role"] = "effect";
            clampRuntime["runtime_layers"]![0]!["materials"]![0]!["uses_audio_spectrum"] = false;
            clampRuntime["runtime_layers"]![0]!["materials"]![0]!["uses_system_media_thumbnail"] = false;
            clampRuntime["runtime_layers"]![0]!["materials"]![0]!["textures"] = new JsonArray();
            JsonObject clamp = LoopAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null, clampRuntime, [10], 30, 1).ToJson();
            check(clamp["unresolved"]!.AsArray().Count == 0 && clamp["candidates"]![0]!["shader_settle_seconds"]!.GetValue<double>() == 4 &&
                clamp["candidates"]![0]!["frames"]!.GetValue<ulong>() == 1 &&
                clamp["candidates"]![0]!["source_period_warmup_frames"]!.GetValue<ulong>() == 1 &&
                ShaderPeriodAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null, Runtime("""
                    {"kind":"static","reasons":[],"external":[],"transient":true,"terms":[]}
                    """), [10], 600, 2).Unresolved is [{ Mechanism: "transient_clamp_scroll" }],
                "a clamp-axis scroll with a reported settle time warms up past it; one without stays not converged");

            // 脚本：不能调速的秒周期、晚于上限才静止，都记在所有者名下（分配回退据此点名），不留零候选零理由
            string[] Scripted(string script) => [.. LoopAnalysis.Analyze(new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 10,
                ["origin"] = new JsonObject { ["value"] = "0 0 0", ["script"] = script } }) }, source, null, JsonNode.Parse("""
                {"status":"complete","runtime_dependencies":[],"runtime_animation_periods":[],"runtime_layers":[{"owner":10,"has_mesh":false,
                  "materials":[{"uses_audio_spectrum":false,"uses_system_media_thumbnail":false,"active_uniforms":[],"textures":[]}]}]}
                """)!.AsObject(), [10], 30, 1,
                loopLengthMaximumSeconds: 30).ToJson()["unresolved"]!.AsArray().Select(x => $"{x!["owner_layer_id"]}/{x["mechanism"]}")];
            check(Scripted("const e = engine;\nexport function update(value) { value.y = Math.sin(e.runtime); return value; }")
                .SequenceEqual(["10/script_period_not_retimable"]), "a script period read through an alias is unresolved under its owner");
            check(Scripted("export function update(value) { value.y = Math.min(engine.runtime / 60, 1); return value; }")
                .SequenceEqual(["10/transient_settle_beyond_warmup"]), "a script that settles after the loop ceiling is unresolved under its owner");
        }
        finally { Directory.Delete(root, true); }
    }

    private static JsonObject Runtime(string signature, string shader = "effects/x") =>
        new() { ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 10, ["materials"] = new JsonArray(new JsonObject {
            ["shader"] = shader, ["effect"] = 0, ["pass"] = 0, ["active_uniforms"] = new JsonArray(), ["time_signature"] = JsonNode.Parse(signature) }) }) };

    private static ShaderPeriodAnalysisResult Analyze(ProjectSource source, string reason, string external = "") =>
        ShaderPeriodAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null,
            Runtime(new JsonObject { ["kind"] = "aperiodic", ["terms"] = new JsonArray(),
                ["reasons"] = reason.Length > 0 ? new JsonArray(reason) : new JsonArray(),
                ["external"] = external.Length > 0 ? new JsonArray(external) : new JsonArray(), ["transient"] = false }.ToJsonString()), [10], 600, 2);
}
