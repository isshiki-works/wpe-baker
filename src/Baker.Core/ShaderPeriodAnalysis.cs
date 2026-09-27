using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Baker.Core;

public enum ShaderTemporalUnresolvedKind
{
    /// <summary>分析没有推导办法（未收敛），Mechanism 是原因码。</summary>
    UnsupportedShaderMechanism,
    /// <summary>证明不周期（结论"不能"），Mechanism 是原因码。</summary>
    NonPeriodicOrDriftingMechanism
}

/// <summary>一条未被时间签名解成周期的着色器时间分量；Mechanism 是原因码，Detail 带出处。</summary>
public sealed record ShaderTemporalUnresolved(int OwnerLayerId, int EffectIndex, int PassIndex,
    string Resource, ShaderTemporalUnresolvedKind Kind, string Detail, string Mechanism = "");

/// <summary>
/// 一个着色器周期分量，属于一个作者效果 pass（EffectIndex/PassIndex &lt; 0 是 source 材质，不能调速）。
/// ConstantKey 是调速挂的材质常量：<see cref="ShaderPeriodAnalysis.TimeScaleKey"/>（整个 pass 的时间倍率）或旋钮键；
/// Inverse：旋钮值与该项速度成反比。
/// </summary>
public sealed record ShaderPeriodComponent(CommonLoopComponent Component, int OwnerLayerId, int EffectIndex, int PassIndex,
    string ConstantKey, bool Inverse, string Evidence);

/// <summary>慢分量：周期/(1+预算) 仍超过循环上限，不进求解器、不调频；接缝漂移上界 2π·P/T 由烘焙侧接缝门复核。</summary>
public sealed record ShaderSlowComponent(string Id, int OwnerLayerId, int EffectIndex, int PassIndex, double PeriodSeconds);

/// <summary>与线性时间比较的分支在 Seconds（上界）之后固定：录制起点要放到它之后。</summary>
public sealed record ShaderSettle(int OwnerLayerId, int EffectIndex, int PassIndex, string Resource, double Seconds);

/// <summary>RuledMaterials：已由时间签名裁定过的 (层, shader)，运行时材质不再按"未建模时钟"重复计。Settle：各 pass 里最晚的一个。</summary>
public sealed record ShaderPeriodAnalysisResult(IReadOnlyList<ShaderPeriodComponent> Components,
    IReadOnlyList<ShaderTemporalUnresolved> Unresolved,
    IReadOnlySet<(int OwnerLayerId, string Shader)> RuledMaterials, IReadOnlyList<ShaderSlowComponent> Slow, IReadOnlyList<ShaderTerm> Terms,
    ShaderSettle? Settle = null);

/// <summary>
/// 一个非慢的已知周期项。Relaxed 是它在最宽松模型里的分量（预算内独立调频，"不能"的证明只认这个模型也无解）；
/// Split：所在 pass 剩多个 π 类，在实际模型里不成分量；Missing：实际模型里它缺独立调频来源的原因（null = 有可用旋钮，或是 pass 时间倍率上唯一的项）。
/// </summary>
public sealed record ShaderTerm(CommonLoopComponent Relaxed, int OwnerLayerId, int EffectIndex, int PassIndex, string Resource, bool Split, string? Missing);

/// <summary>
/// 读引擎在 SPIR-V 上算出的时间签名（runtime_layers[].materials[].time_signature.terms，见 engine ShaderTime.cppm），
/// 按 (层, 效果, pass) 合成循环分量；没有 effect/pass 的 source 材质按 (层, shader) 合成，不能调速。一个 pass 内：
/// 周期/(1+预算) 仍超过上限的项是慢分量；带旋钮、且旋钮 token 在该 stage 源码里恰好出现一次的项单独成分量，
/// 捕获时改写那一处 token 调频（<see cref="ShaderTextPatch.KnobUses"/>）；其余项按 π 次数分类、类内取有理 LCM，
/// 合成一个挂时间倍率（<see cref="TimeScaleKey"/>，给 g_Time 乘材质常量）的分量。剩两类以上时周期比是无理数，
/// 同乘一个倍率保不住整数比，这个 pass 不成分量，由 LoopAnalysis 按最宽松模型（每项独立调频）判不能或未收敛。
/// </summary>
public static class ShaderPeriodAnalysis
{
    public const string TimeScaleKey = "periodica_time_scale";

    // 引擎原因码里只有这两条是不周期的证明：已知非零系数的线性时间直达输出；与线性时间比较而阈值无界（tan 极点，永不固定）。
    // 阈值有界的比较是暂态（签名给 settle_seconds），阈值范围说不清的比较与分支（compare_with_linear_time、branch_on_linear_time、
    // loop_count_time_dependent）、线性时间进了没有专门规则的运算、与别的时间量相乘、系数不定、分析没推下去，只记未收敛
    private static readonly string[] Proof = ["drift", "compare_with_unbounded_time"];
    // 时间签名只认 g_Time；用到这些时钟的材质不裁定，交给运行时材质检查报未建模时钟
    private static readonly string[] AlternateClocks = ["g_Runtime", "g_Frametime", "g_DeltaTime"];

    /// <summary>
    /// 振幅推不出的慢项（原周期 ≥ 60 s、不是慢分量、有可用旋钮、不是 foliagesway）在求解器里的调速上限：实际等于不设限（取离原速最近的圈数），
    /// 看不看得出由分析收尾实测（<see cref="SlowClosureProbe.SpeedAsync"/>）；只在整层路线、逐项预算内无解时启用（见 LoopAnalysis）。
    /// </summary>
    internal const double MeasuredRetimePercent = 1e4;
    /// <summary>这类项在 loop.evidence 里的证据后缀；实测按它认项，不按 id 猜。</summary>
    internal const string MeasuredNote = "; retimed beyond the budget only after a rendered speed check";

    public static ShaderPeriodAnalysisResult Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory,
        JsonObject runtime, IReadOnlyCollection<int> selectedLayerIds, double ceilingSeconds, double maximumRetimePercent)
    {
        var components = new List<ShaderPeriodComponent>();
        var slowComponents = new List<ShaderSlowComponent>();
        var looseTerms = new List<ShaderTerm>();
        var unresolved = new List<ShaderTemporalUnresolved>();
        var ruled = new HashSet<(int, string)>();
        var seen = new HashSet<int>();
        ShaderSettle? settle = null;
        var animatedOwners = (runtime["runtime_animation_periods"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(trace => SceneGraph.Int(trace["source_owner_layer_id"])).OfType<int>().ToHashSet();
        double stretch = 1 + maximumRetimePercent / 100;
        var objects = new Dictionary<int, JsonObject>();
        foreach (JsonObject o in (scene["objects"] as JsonArray ?? []).OfType<JsonObject>())
            if (SceneGraph.Int(o["id"]) is int oid) objects.TryAdd(oid, o);
        double? canvasShortEdge;
        try
        {
            var canvas = HybridVideoProjection.AuthoredCanvas(scene, new JsonObject());
            canvasShortEdge = canvas.Width is double cw && canvas.Height is double ch && cw > 0 && ch > 0 ? Math.Min(cw, ch) : null;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException) { canvasShortEdge = null; }
        static IEnumerable<JsonObject> Knobs(JsonObject term) => (term["knobs"] as JsonArray ?? []).OfType<JsonObject>();
        foreach (JsonObject layer in (runtime["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (SceneGraph.Int(layer["owner"]) is not int owner || !selectedLayerIds.Contains(owner) || !seen.Add(owner)) continue;
            var groups = (runtime["runtime_layers"] as JsonArray)!.OfType<JsonObject>().Where(x => SceneGraph.Int(x["owner"]) == owner)
                .SelectMany(x => (x["materials"] as JsonArray ?? []).OfType<JsonObject>())
                .Where(m => m["time_signature"] is JsonObject && m["shader"] is JsonValue &&
                    !(m["active_uniforms"] as JsonArray ?? []).Any(u => AlternateClocks.Contains(u?.ToString())))
                .GroupBy(m => (Effect: SceneGraph.Int(m["effect"]) ?? -1, Pass: SceneGraph.Int(m["pass"]) ?? -1, Shader: m["shader"]!.GetValue<string>()));
            foreach (var group in groups)
            {
                var (effect, pass, shader) = group.Key;
                ruled.Add((owner, shader));
                string resource = "shaders/" + shader;
                string id = effect < 0 ? $"shader/{owner}/{shader}" : $"shader/{owner}/{effect}/{pass}/{shader}";
                void Fail(bool proof, string code, string detail) => unresolved.Add(new(owner, effect, pass, resource,
                    proof ? ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism : ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                    "SPIR-V time signature: " + detail, code));
                JsonObject[] signatures = [.. group.Select(m => m["time_signature"]!.AsObject())];
                string[] reasons = [.. signatures.SelectMany(s => (s["reasons"] as JsonArray ?? []).Select(r => r!.GetValue<string>())).Distinct()];
                // 烘进视频的层里视差位置是定值：保留视差时读它的层已由 Liveness 留作实时
                string[] external = [.. signatures.SelectMany(s => (s["external"] as JsonArray ?? []).Select(r => r!.GetValue<string>()))
                    .Where(name => name != "g_ParallaxPosition").Distinct()];
                if (reasons.Length > 0)
                {
                    // 有一条证明就够"不能"；全是"没推下去"才记未收敛
                    string? proof = reasons.FirstOrDefault(r => Proof.Contains(Code(r)));
                    Fail(proof is not null, Code(proof ?? reasons[0]), string.Join("; ", reasons));
                    continue;
                }
                string[] inputs = [.. external.Where(IsSystemInput)];
                if (inputs.Length > 0) { Fail(true, "external_input", "output depends on live input " + string.Join(", ", inputs)); continue; }
                // 带值动画的材质常量：它的周期由本层的运行时动画轨道进求解器；本层没有轨道就说不清它怎么变
                if (external.Length > 0 && !animatedOwners.Contains(owner))
                { Fail(false, "external_uniform_unmodeled", "animated uniforms " + string.Join(", ", external) + " have no runtime track"); continue; }
                if (signatures.Any(s => s["transient"]?.GetValue<bool>() == true))
                { Fail(false, "transient_clamp_scroll", "clamp-axis scroll settles at a time the signature does not report"); continue; }
                if (signatures.Max(s => s["settle_seconds"]?.GetValue<double>() ?? -1) is double at and >= 0 && (settle is null || at > settle.Seconds))
                    settle = new(owner, effect, pass, resource, at);

                JsonObject[] terms = [.. signatures.SelectMany(s => (s["terms"] as JsonArray ?? []).OfType<JsonObject>()).DistinctBy(t => t.ToJsonString())];
                // 同一旋钮被几个项用到：多于一个就分不开，不用它
                var claims = terms.SelectMany(t => Knobs(t).Select(ShaderTextPatch.KnobKey).Distinct()).GroupBy(key => key).ToDictionary(g => g.Key, g => g.Count());
                var stages = new Dictionary<string, string?>();
                string? Text(string stage)
                {
                    if (!stages.TryGetValue(stage, out string? text))
                        stages[stage] = text = TryReadShaderStage(source, assetsDirectory, resource + "." + stage, out string read) ? read : null;
                    return text;
                }
                // 调用旋钮改写 sites 处调用（KnobUses 已核对处数），其余旋钮只改唯一的一处
                bool Usable(JsonObject knob) => knob["varying"] is null && claims[ShaderTextPatch.KnobKey(knob)] == 1 &&
                    Text(knob["stage"]!.GetValue<string>()) is string text && ShaderTextPatch.KnobUses(text, ShaderTextPatch.KnobKey(knob)).Length is int uses &&
                    (uses == 1 || knob["call"] is not null && uses > 0);
                // 没有可用旋钮时退到顶点输出分量：这一项经过的分量只归它、同在一个 varying 上，且顶点程序能整段换时间重算
                string? AxisKey(JsonObject term) => Knobs(term).Where(k => k["varying"] is not null).ToArray() is { Length: > 0 } axes &&
                    axes.All(k => claims[ShaderTextPatch.KnobKey(k)] == 1) && ShaderTextPatch.AxisKey(axes) is string key &&
                    Text("vert") is string vert && ShaderTextPatch.AxisTarget(vert, key) is not null ? key : null;
                var knobbed = new List<ShaderPeriodComponent>();
                var slow = new List<ShaderSlowComponent>();
                var unknown = new List<double>();
                // 非慢的已知周期项：最宽松模型里各自独立调频；Missing 是实际模型里它缺独立调频来源的原因（null = 不缺）
                var loose = new List<(CommonLoopComponent Term, string? Missing)>();
                // 缺旋钮、π 次数已知的项：挂 pass 时间倍率，带它可用的分量旋钮键（null = 没有）
                var timed = new List<(int Loose, int Pi, BigInteger Num, BigInteger Den, double Seconds, string? Axis)>();
                // 1.0.2 摆动改频的口径：原周期 ≥ 60 s 的慢项看峰值速度偏差 K·|δ|（K = 改速 100% 时的峰值速度偏差，1080p 口径 px/s），
                // ≤ 0.1 px/s 就允许超过逐项预算（不比预算更紧；上限 ≥ 100% 且周期超过循环上限时可冻结）。K 能从材质常量算出的只有：
                // 官方 foliagesway（K = a·2π/T，a 见 SwayAmplitude）与官方 waterwaves（按钉住的片元指纹认领，K 见 WaveSpeed），其余着色器照旧按逐项预算
                (string Note, double Percent)? Cap(JsonObject term, double seconds)
                {
                    if (effect < 0 || seconds < SwayRecurrenceSolver.VisiblePeriodSeconds || canvasShortEdge is not double edge ||
                        objects.GetValueOrDefault(owner) is not JsonObject obj) return null;
                    (double Speed, string Note)? peak =
                        shader == "effects/foliagesway" && Knobs(term).Select(k => SwayAmplitude(obj, objects, edge, effect, pass, k)).FirstOrDefault(x => x > 0) is double a
                            ? (a * 2 * Math.PI / seconds, $"; sway amplitude {a:0.###} px at 1080p")
                        : shader == "effects/waterwaves" && Text("frag") is string frag && Analysis.EffectRange.EffectRangeRules.Fingerprint(frag) == WaterWavesFragment &&
                            WaveSpeed(obj, objects, edge, effect, pass, terms.Any(t => Knobs(t).Any(k => k["uniform"]?.GetValue<string>() == "g_Speed2"))) is double k
                            ? (k, $"; water-wave peak speed {k:0.###} px/s at 1080p")
                        : null;
                    if (peak is not var (speed, note)) return null;
                    double percent = Math.Max(maximumRetimePercent, 100 * SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond / speed);
                    return (note + $", slow-term speed limit allows {percent:0.##}%", percent);
                }
                ShaderPeriodComponent Through(string key, double seconds, bool inverse, (string Note, double Percent)? cap = null, bool measured = false) =>
                    new(new CommonLoopComponent($"{id}/{key}", new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic), AllowRetime: true,
                        measured ? MeasuredRetimePercent : cap?.Percent),
                    owner, effect, pass, key, inverse, $"SPIR-V time signature of {resource}: period {seconds.ToString("R", CultureInfo.InvariantCulture)} s through {key}" +
                    (cap?.Note ?? (measured ? MeasuredNote : "")));
                foreach (var (term, index) in terms.Select((term, index) => (term, index)))
                {
                    double seconds = term["seconds"]!.GetValue<double>();
                    BigInteger num = term["num"]!.GetValue<long>(), den = term["den"]!.GetValue<long>();
                    // 慢分量：调速到预算上限也放不进一圈；num=0 时 seconds 是下界，照样成立。同 pass 的时间倍率也乘在它上面，
                    // 记 seconds/stretch（有效周期的下界），漂移 2π·P/T 才是上界
                    // 带振幅的摆动项能单独调频时按它自己的上限算：1.0.2 里周期超过上限的极慢项也在速度偏差门限内改频（或冻结），不留给接缝门
                    var cap = Cap(term, seconds);
                    double own = 1 + (cap is (_, double percent) && effect >= 0 && Knobs(term).Any(Usable) ? percent : maximumRetimePercent) / 100;
                    if (seconds / own > ceilingSeconds) { slow.Add(new($"{id}/slow{slow.Count}", owner, effect, pass, seconds / stretch)); continue; }
                    if (num <= 0 || den <= 0) { unknown.Add(seconds); continue; }
                    var relaxed = new CommonLoopComponent($"{id}/term{index}", new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic), AllowRetime: true, cap?.Percent);
                    // 旋钮只能挂在作者效果 pass 上（场景里有这个 pass 的 constantshadervalues）
                    if (effect >= 0 && Knobs(term).FirstOrDefault(Usable) is JsonObject knob)
                    {
                        // 振幅推不出的慢项：超预算的改速先挂上，放不放行看实测（MeasuredRetimePercent）；最宽松模型的"不能"证明不带它。
                        // foliagesway、waterwaves 只走解析判据：算不出（如顶点模式 directionweights 的这一轴为 0、waterwaves 指数 < 1 或源码不是钉住的版本）
                        // 就按逐项预算，不进实测
                        knobbed.Add(Through(ShaderTextPatch.KnobKey(knob), seconds, knob["inverse"]?.GetValue<bool>() == true, cap,
                            measured: cap is null && shader is not ("effects/foliagesway" or "effects/waterwaves") && seconds >= SwayRecurrenceSolver.VisiblePeriodSeconds));
                        loose.Add((relaxed, null));
                        continue;
                    }
                    loose.Add((relaxed, effect < 0 ? "source material without an authored effect pass" : !Knobs(term).Any() ? "no knob"
                        : string.Join("; ", Knobs(term).Select(ShaderTextPatch.KnobKey).Distinct().Select(key => claims[key] > 1
                            ? $"knob {key} is shared by {claims[key]} terms" : key.Contains("_ax_", StringComparison.Ordinal)
                            ? $"knob {key} cannot be retimed on its own (the term also runs through other outputs, or the vertex program cannot be rerun)"
                            : $"knob {key} is not a unique rewritable token in the source"))));
                    if (term["pi"] is not JsonValue power || !power.TryGetValue(out int pi)) { unknown.Add(seconds); continue; }
                    BigInteger divisor = BigInteger.GreatestCommonDivisor(num, den);
                    timed.Add((loose.Count - 1, pi, num / divisor, den / divisor, seconds, effect >= 0 ? AxisKey(term) : null));
                }
                // 类内有理 LCM：lcm(a/b, c/d) = lcm(a,c)/gcd(b,d)
                static SortedDictionary<int, (BigInteger Num, BigInteger Den)> Classes(IEnumerable<(int Loose, int Pi, BigInteger Num, BigInteger Den, double Seconds, string? Axis)> items)
                {
                    var classes = new SortedDictionary<int, (BigInteger Num, BigInteger Den)>();
                    foreach (var (_, pi, num, den, _, _) in items)
                        classes[pi] = classes.TryGetValue(pi, out var old)
                            ? (old.Num / BigInteger.GreatestCommonDivisor(old.Num, num) * num, BigInteger.GreatestCommonDivisor(old.Den, den))
                            : (num, den);
                    return classes;
                }
                var classes = Classes(timed);
                // 一个时间倍率兜不住（剩多个 π 类，或类周期调速到预算上限仍超上限）时，有分量旋钮的项改挂各自的分量旋钮
                if (classes.Count > 1 || classes.Any(c => (double)c.Value.Num / (double)c.Value.Den * Math.Pow(Math.PI, c.Key) / stretch > ceilingSeconds))
                {
                    foreach (var item in timed.Where(x => x.Axis is not null))
                    {
                        knobbed.Add(Through(item.Axis!, item.Seconds, false));
                        loose[item.Loose] = (loose[item.Loose].Term, null);
                    }
                    classes = Classes(timed.Where(x => x.Axis is null));
                }
                string periods = string.Join(", ", classes.Select(c => $"{c.Value.Num}/{c.Value.Den}{(c.Key == 0 ? "" : c.Key == 1 ? "·π" : $"·π^{c.Key}")} s"));
                if (unknown.Count > 0)
                {
                    Fail(false, "period_class_unknown", "period is not a rational multiple of a known power of π: " +
                        string.Join(", ", unknown.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
                    continue;
                }
                // pass 时间倍率上只挂一个项时，倍率就是它独立的调频来源
                if (effect >= 0 && loose.Count(x => x.Missing is not null) == 1) loose = [.. loose.Select(x => (x.Term, (string?)null))];
                // 剩多个 π 类：一个时间倍率同乘保不住无理比，这个 pass 在实际模型里不成分量，交给 LoopAnalysis 的最宽松模型判定
                bool split = classes.Count > 1;
                if (classes.Count == 1)
                {
                    var (pi, (num, den)) = classes.Single();
                    double seconds = (double)num / (double)den * Math.Pow(Math.PI, pi);
                    CommonLoopRational? exact = pi == 0 && num <= long.MaxValue && den <= long.MaxValue ? new((long)num, (long)den) : null;
                    if (exact is null && effect < 0)
                    {
                        Fail(false, "irrational_period_not_retimable", $"period {periods} needs a time scale, but the material has no authored effect pass");
                        continue;
                    }
                    components.Add(new(new CommonLoopComponent(id, new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic, exact), AllowRetime: effect >= 0),
                        owner, effect, pass, TimeScaleKey, false, $"SPIR-V time signature of {resource}: period {periods}"));
                }
                if (!split) components.AddRange(knobbed);
                slowComponents.AddRange(slow);
                looseTerms.AddRange(loose.Select(x => new ShaderTerm(x.Term, owner, effect, pass, resource, split, x.Missing)));
            }
        }
        return new(components, unresolved, ruled, slowComponents, looseTerms, settle);
    }

    /// <summary>官方 waterwaves.frag 归一化源码的指纹（与 effect-range-rules.json 的 uv_displace_waterwaves 同一份）：只有它按 <see cref="WaveSpeed"/> 放行慢项改速。</summary>
    internal const string WaterWavesFragment = "ff838f0fbe51919c6cb12385008a63a8d074bdd69555cbaae7b8199532fe9b2a";

    static JsonObject? At(JsonNode? array, int index) => array is JsonArray items && index < items.Count ? items[index] as JsonObject : null;
    static JsonObject? Pass(JsonObject obj, int effect, int pass) => At(At(obj["effects"], effect)?["passes"], pass);
    /// <summary>材质常量的有限数值（绑定取 value）；没写或不是数时为 null。</summary>
    static double? Constant(JsonObject? constants, string key) =>
        (constants?[key] is JsonObject binding ? binding["value"] : constants?[key]) is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : null;

    /// <summary>图层连同各级父层的缩放之积。</summary>
    static (double X, double Y) ChainScale(JsonObject obj, IReadOnlyDictionary<int, JsonObject> objects)
    {
        double scaleX = 1, scaleY = 1;
        var seen = new HashSet<int>();
        for (JsonObject? current = obj; current is not null && seen.Add(SceneGraph.Int(current["id"]) ?? -1);
            current = SceneGraph.Int(current["parent"]) is int parent ? objects.GetValueOrDefault(parent) : null)
        {
            var scale = HybridVideoProjection.Vector(current["scale"], (1, 1));
            (scaleX, scaleY) = (scaleX * scale.X, scaleY * scale.Y);
        }
        return (scaleX, scaleY);
    }

    /// <summary>
    /// 官方 waterwaves 一个 pass 的峰值速度系数 K（1080p 短边口径 px/s）：该 pass 的项改速 |δ|（各项取最大）时，画面内容的峰值速度偏差 ≤ K·|δ|。
    /// 采样坐标的位移 = s₁(θ₁) [· s₂(θ₂)] · 单位方向 · strength² · 遮罩，sᵢ(θ) = sign(sin θ)·|sin θ|^eᵢ，θᵢ = 时间·speedᵢ + 与时间无关的相位：
    /// 位移幅度 ≤ strength²（遮罩 ≤ 1、方向为单位向量），e ≥ 1 时 |s'(θ)| ≤ e，
    /// 所以位移速度 ≤ strength²·(e₁|speed₁| [+ e₂|speed₂|])（纹理坐标/秒），乘纹理坐标到画面的像素数（宽·scaleX 与 高·scaleY 取大）再折到 1080p。
    /// 任一指数 &lt; 1（过零点斜率无界）、双波缺 speed2/exponent2、读不到图层尺寸时为 null。默认值取官方注释：speed 5、exponent 1、strength 0.1。
    /// </summary>
    internal static double? WaveSpeed(JsonObject obj, IReadOnlyDictionary<int, JsonObject> objects, double canvasShortEdge, int effect, int pass, bool dual)
    {
        JsonObject? material = Pass(obj, effect, pass);
        JsonObject? constants = material?["constantshadervalues"] as JsonObject;
        dual |= (material?["combos"]?["DUALWAVES"] is JsonValue combo && combo.TryGetValue(out double on) ? on : 0) != 0;
        double strength = Constant(constants, "strength") ?? 0.1, exponent = Constant(constants, "exponent") ?? 1;
        if (!(exponent >= 1)) return null;
        double rate = exponent * Math.Abs(Constant(constants, "speed") ?? 5);
        if (dual)
        {
            if (Constant(constants, "speed2") is not double speed2 || Constant(constants, "exponent2") is not double exponent2 || !(exponent2 >= 1)) return null;
            rate += exponent2 * Math.Abs(speed2);
        }
        try
        {
            var (scaleX, scaleY) = ChainScale(obj, objects);
            var size = HybridVideoProjection.Vector(obj["size"], (0, 0));
            if (!(size.X > 0 && size.Y > 0)) return null;
            double pixels = Math.Max(Math.Abs(size.X * scaleX), Math.Abs(size.Y * scaleY));
            return strength * strength * rate * pixels * SwayRecurrenceSolver.ReferenceShortEdgePixels / canvasShortEdge;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException) { return null; }
    }

    // stock foliagesway 的 8 个频率字面量：前 4 项进 x（sines），后 4 项进 y（csines）
    private static readonly float[] SwaySines = [1f, -0.16161616f, 0.0083333f, -0.00019841f];
    private static readonly float[] SwayCoSines = [-0.5f, 0.041666666f, -0.0013888889f, 0.000024801587f];

    /// <summary>
    /// foliagesway 一项在输出画面上的振幅（1080p 短边口径像素，与 0.1 px/s 门限同一口径）；旋钮不是 stock 频率字面量、读不到图层尺寸时为 null。
    /// UV 模式（frag）：amp = strength²·0.005，aspect = 宽/高·ratio，(z, w) = rotateVec2((1/aspect, aspect), scrolldirection)，
    /// A_x = |z|·amp·宽·scaleX；顶点模式（vert）：A_x = |strength·100·directionweights.x|·scaleX（角权重、遮罩按最坏值 1）；y 同理。
    /// </summary>
    private static double? SwayAmplitude(JsonObject obj, IReadOnlyDictionary<int, JsonObject> objects, double canvasShortEdge, int effect, int pass, JsonObject knob)
    {
        if (knob["literal"] is not JsonValue literal || !literal.TryGetValue(out double value)) return null;
        int axis = Array.IndexOf(SwaySines, (float)value) >= 0 ? 0 : Array.IndexOf(SwayCoSines, (float)value) >= 0 ? 1 : -1;
        if (axis < 0) return null;
        JsonObject? constants = Pass(obj, effect, pass)?["constantshadervalues"] as JsonObject;
        double Constant(string key, double fallback) => ShaderPeriodAnalysis.Constant(constants, key) ?? fallback;
        try
        {
            var (scaleX, scaleY) = ChainScale(obj, objects);
            double strength = Constant("strength", 0.4), amplitude;
            if (knob["stage"]?.GetValue<string>() == "vert")
            {
                var weights = HybridVideoProjection.Vector(constants?["directionweights"], (1, 0.2));
                amplitude = Math.Abs(strength * 100 * (axis == 0 ? weights.X * scaleX : weights.Y * scaleY));
            }
            else
            {
                var size = HybridVideoProjection.Vector(obj["size"], (0, 0));
                double aspect = size.X / size.Y * Constant("ratio", 0.3), direction = Constant("scrolldirection", 0);
                if (!(size.X > 0 && size.Y > 0) || !double.IsFinite(aspect) || aspect == 0) return null;
                double rotated = axis == 0 ? Math.Cos(direction) / aspect - aspect * Math.Sin(direction) : Math.Sin(direction) / aspect + aspect * Math.Cos(direction);
                amplitude = Math.Abs(rotated * strength * strength * 0.005 * (axis == 0 ? size.X * scaleX : size.Y * scaleY));
            }
            return amplitude * SwayRecurrenceSolver.ReferenceShortEdgePixels / canvasShortEdge;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>原因串的码：到第一个空格、冒号或 @ 为止。</summary>
    private static string Code(string reason) => reason.Split([' ', ':', '@'], 2)[0];

    private static bool IsSystemInput(string name) => name.StartsWith("g_AudioSpectrum", StringComparison.Ordinal) ||
        name.StartsWith("g_Pointer", StringComparison.Ordinal) || name is "g_ParallaxPosition" or "g_Daytime";

    /// <summary>
    /// 一个作者效果引用到的材质 shader 名（与 runtime.json 里 materials[].shader 同一写法），按有材质的 pass 顺序。
    /// 读不出的资源直接跳过。
    /// </summary>
    public static IEnumerable<string> EffectMaterialShaders(ProjectSource source, string? assetsDirectory, JsonObject effect)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(effect);
        string resource = effect["file"] is JsonValue file && file.TryGetValue(out string? path) && path is not null ? path : "";
        if (string.IsNullOrWhiteSpace(resource)) yield break;
        JsonObject? definition = TryReadResource(source, assetsDirectory, resource);
        if (definition is null) yield break;
        foreach (JsonObject pass in (definition["passes"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            string? material = pass["material"] is JsonValue value && value.TryGetValue(out string? name) ? name : null;
            if (string.IsNullOrWhiteSpace(material)) continue;
            string? shader = TryReadResource(source, assetsDirectory, material)?["passes"]?.AsArray().FirstOrDefault()?["shader"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(shader)) yield return shader;
        }
    }

    private static JsonObject? TryReadResource(ProjectSource source, string? assetsDirectory, string resource)
    {
        try { return SceneAnalyzer.ReadResourceJson(source, assetsDirectory, resource); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
    }

    internal static bool TryReadShaderStage(ProjectSource source, string? assetsDirectory, string resource, out string text)
    {
        text = "";
        if (source.Contains(resource)) { text = Encoding.UTF8.GetString(source.Read(resource)); return true; }
        if (assetsDirectory is null) return false;
        string path = ProjectSource.ContainedPath(assetsDirectory, resource);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Shader source exceeds the analysis limit.");
        text = File.ReadAllText(path, Encoding.UTF8);
        return true;
    }
}
