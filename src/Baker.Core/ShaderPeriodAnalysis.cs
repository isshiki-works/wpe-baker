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

/// <summary>RuledMaterials：已由时间签名裁定过的 (层, shader)，运行时材质不再按"未建模时钟"重复计。</summary>
public sealed record ShaderPeriodAnalysisResult(IReadOnlyList<ShaderPeriodComponent> Components,
    IReadOnlyList<ShaderTemporalUnresolved> Unresolved,
    IReadOnlySet<(int OwnerLayerId, string Shader)> RuledMaterials, IReadOnlyList<ShaderSlowComponent> Slow, IReadOnlyList<ShaderTerm> Terms);

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

    // 引擎里这些原因只说明"分析没推下去"，不是不周期的证明（其余原因码都是证明：漂移、线性时间进非周期运算、按线性时间分支）
    private static readonly string[] NotProof = ["spirv_unreadable", "analysis_not_converged", "sampler_wrap_unknown",
        "time_rate_not_constant", "scroll_rate_not_constant", "drift_rate_not_constant", "too_many_periods", "unsupported_side_effect", "names_stripped"];
    // 时间签名只认 g_Time；用到这些时钟的材质不裁定，交给运行时材质检查报未建模时钟
    private static readonly string[] AlternateClocks = ["g_Runtime", "g_Frametime", "g_DeltaTime"];

    public static ShaderPeriodAnalysisResult Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory,
        JsonObject runtime, IReadOnlyCollection<int> selectedLayerIds, double ceilingSeconds, double maximumRetimePercent)
    {
        var components = new List<ShaderPeriodComponent>();
        var slowComponents = new List<ShaderSlowComponent>();
        var looseTerms = new List<ShaderTerm>();
        var unresolved = new List<ShaderTemporalUnresolved>();
        var ruled = new HashSet<(int, string)>();
        var seen = new HashSet<int>();
        var objects = (scene["objects"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => SceneGraph.Int(item["id"]) is not null).ToDictionary(item => SceneGraph.Int(item["id"])!.Value);
        JsonObject[] dependencies = [.. (runtime["runtime_dependencies"] as JsonArray ?? []).OfType<JsonObject>()];
        var animatedOwners = (runtime["runtime_animation_periods"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(trace => SceneGraph.Int(trace["source_owner_layer_id"])).OfType<int>().ToHashSet();
        double stretch = 1 + maximumRetimePercent / 100;
        static IEnumerable<JsonObject> Knobs(JsonObject term) => (term["knobs"] as JsonArray ?? []).OfType<JsonObject>();
        foreach (JsonObject layer in (runtime["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (SceneGraph.Int(layer["owner"]) is not int owner || !selectedLayerIds.Contains(owner) || !seen.Add(owner)) continue;
            objects.TryGetValue(owner, out JsonObject? ownerObject);
            AddScriptDrivenUniforms(owner, ownerObject, dependencies, unresolved);
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
                string[] external = [.. signatures.SelectMany(s => (s["external"] as JsonArray ?? []).Select(r => r!.GetValue<string>())).Distinct()];
                if (reasons.Length > 0)
                {
                    // 有一条证明就够"不能"；全是"没推下去"才记未收敛
                    string? proof = reasons.FirstOrDefault(r => !NotProof.Contains(Code(r)));
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

                JsonObject[] terms = [.. signatures.SelectMany(s => (s["terms"] as JsonArray ?? []).OfType<JsonObject>()).DistinctBy(t => t.ToJsonString())];
                // 同一旋钮被几个项用到：多于一个就分不开，不用它
                var claims = terms.SelectMany(t => Knobs(t).Select(ShaderTextPatch.KnobKey).Distinct()).GroupBy(key => key).ToDictionary(g => g.Key, g => g.Count());
                var stages = new Dictionary<string, string?>();
                bool Usable(JsonObject knob)
                {
                    string stage = knob["stage"]!.GetValue<string>(), key = ShaderTextPatch.KnobKey(knob);
                    if (!stages.TryGetValue(stage, out string? text))
                        stages[stage] = text = TryReadShaderStage(source, assetsDirectory, resource + "." + stage, out string read) ? read : null;
                    return claims[key] == 1 && text is not null && ShaderTextPatch.KnobUses(text, key).Length == 1;
                }
                var classes = new SortedDictionary<int, (BigInteger Num, BigInteger Den)>();
                var knobbed = new List<ShaderPeriodComponent>();
                var slow = new List<ShaderSlowComponent>();
                var unknown = new List<double>();
                // 非慢的已知周期项：最宽松模型里各自独立调频；Missing 是实际模型里它缺独立调频来源的原因（null = 不缺）
                var loose = new List<(CommonLoopComponent Term, string? Missing)>();
                foreach (var (term, index) in terms.Select((term, index) => (term, index)))
                {
                    double seconds = term["seconds"]!.GetValue<double>();
                    BigInteger num = term["num"]!.GetValue<long>(), den = term["den"]!.GetValue<long>();
                    // 慢分量：调速到预算上限也放不进一圈；num=0 时 seconds 是下界，照样成立。同 pass 的时间倍率也乘在它上面，
                    // 记 seconds/stretch（有效周期的下界），漂移 2π·P/T 才是上界
                    if (seconds / stretch > ceilingSeconds) { slow.Add(new($"{id}/slow{slow.Count}", owner, effect, pass, seconds / stretch)); continue; }
                    if (num <= 0 || den <= 0) { unknown.Add(seconds); continue; }
                    var relaxed = new CommonLoopComponent($"{id}/term{index}", new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic), AllowRetime: true);
                    // 旋钮只能挂在作者效果 pass 上（场景里有这个 pass 的 constantshadervalues）
                    if (effect >= 0 && Knobs(term).FirstOrDefault(Usable) is JsonObject knob)
                    {
                        string key = ShaderTextPatch.KnobKey(knob);
                        knobbed.Add(new(new CommonLoopComponent($"{id}/{key}", new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
                            owner, effect, pass, key, knob["inverse"]?.GetValue<bool>() == true,
                            $"SPIR-V time signature of {resource}: period {seconds.ToString("R", CultureInfo.InvariantCulture)} s through {key}"));
                        loose.Add((relaxed, null));
                        continue;
                    }
                    loose.Add((relaxed, effect < 0 ? "source material without an authored effect pass" : !Knobs(term).Any() ? "no knob"
                        : string.Join("; ", Knobs(term).Select(ShaderTextPatch.KnobKey).Distinct().Select(key => claims[key] > 1
                            ? $"knob {key} is shared by {claims[key]} terms" : $"knob {key} is not a unique rewritable token in the source"))));
                    if (term["pi"] is not JsonValue power || !power.TryGetValue(out int pi)) { unknown.Add(seconds); continue; }
                    BigInteger divisor = BigInteger.GreatestCommonDivisor(num, den);
                    (num, den) = (num / divisor, den / divisor);
                    // 类内有理 LCM：lcm(a/b, c/d) = lcm(a,c)/gcd(b,d)
                    classes[pi] = classes.TryGetValue(pi, out var old)
                        ? (old.Num / BigInteger.GreatestCommonDivisor(old.Num, num) * num, BigInteger.GreatestCommonDivisor(old.Den, den))
                        : (num, den);
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
        return new(components, unresolved, ruled, slowComponents, looseTerms);
    }

    /// <summary>原因串的码：到第一个空格、冒号或 @ 为止。</summary>
    private static string Code(string reason) => reason.Split([' ', ':', '@'], 2)[0];

    private static bool IsSystemInput(string name) => name.StartsWith("g_AudioSpectrum", StringComparison.Ordinal) ||
        name.StartsWith("g_Pointer", StringComparison.Ordinal) || name is "g_ParallaxPosition" or "g_Daytime";

    /// <summary>
    /// 作者脚本写的材质常量（constantshadervalues 里带 script 的键）不是常量：运行时依赖里该层对这个键有非初始化的读写，
    /// 就按外部输入处理。读时钟的由 script_time 未解析项覆盖；读真实输入或随机数的输出不周期；其余说不清。
    /// </summary>
    private static void AddScriptDrivenUniforms(int owner, JsonObject? ownerObject, JsonObject[] dependencies, List<ShaderTemporalUnresolved> unresolved)
    {
        if (ownerObject?["effects"] is not JsonArray effects) return;
        for (int effect = 0; effect < effects.Count; ++effect)
        {
            JsonArray passes = effects[effect]?["passes"] as JsonArray ?? [];
            for (int pass = 0; pass < passes.Count; ++pass)
                foreach (var (key, value) in passes[pass]?["constantshadervalues"] as JsonObject ?? [])
                {
                    if (value is not JsonObject { } scripted || scripted["script"] is null) continue;
                    string[] operations = [.. dependencies.Where(d => SceneGraph.Int(d["owner"]) == owner && d["initialization"]?.GetValue<bool>() != true &&
                        d["binding"]?.ToString() == key).Select(d => d["operation"]?.ToString() ?? "").Distinct()];
                    if (operations.Length == 0 || operations.All(op => op == "time")) continue;
                    bool input = operations.Any(op => op is "input" or "random");
                    unresolved.Add(new(owner, effect, pass, key, input ? ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism
                        : ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                        $"Script drives material constant {key} ({string.Join(", ", operations)}).", input ? "script_uniform_input" : "script_uniform_unmodeled"));
                }
        }
    }

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
