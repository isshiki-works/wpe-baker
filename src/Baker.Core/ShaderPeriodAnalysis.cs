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

/// <summary>一个 (层, shader) 的周期分量；Passes 是能挂时间倍率的作者效果 pass，空表示不能调速。</summary>
public sealed record ShaderPeriodComponent(CommonLoopComponent Component, int OwnerLayerId,
    IReadOnlyList<(int Effect, int Pass)> Passes, string Evidence);

/// <summary>RuledMaterials：已由时间签名裁定过的 (层, shader)，运行时材质不再按"未建模时钟"重复计。</summary>
public sealed record ShaderPeriodAnalysisResult(IReadOnlyList<ShaderPeriodComponent> Components,
    IReadOnlyList<ShaderTemporalUnresolved> Unresolved,
    IReadOnlySet<(int OwnerLayerId, string Shader)> RuledMaterials);

/// <summary>
/// 读引擎在 SPIR-V 上算出的时间签名（runtime_layers[].materials[].time_signature，见 engine ShaderTime.cppm），
/// 按 (层, shader) 合成循环分量。调速是给这些 pass 的 g_Time 乘一个材质常量（<see cref="TimeScaleKey"/>，
/// 捕获时由 <see cref="ShaderTextPatch.WriteTimeScaleAsync"/> 写覆盖 shader），同一材质内各周期等比缩放。
/// </summary>
public static class ShaderPeriodAnalysis
{
    public const string TimeScaleKey = "periodica_time_scale";

    // 引擎里这些原因只说明"分析没推下去"，不是不周期的证明（其余原因码都是证明：漂移、线性时间进非周期运算、按线性时间分支）
    private static readonly string[] NotProof = ["spirv_unreadable", "analysis_not_converged", "sampler_wrap_unknown",
        "time_rate_not_constant", "scroll_rate_not_constant", "drift_rate_not_constant", "too_many_periods"];
    // 时间签名只认 g_Time；用到这些时钟的材质不裁定，交给运行时材质检查报未建模时钟
    private static readonly string[] AlternateClocks = ["g_Runtime", "g_Frametime", "g_DeltaTime"];

    public static ShaderPeriodAnalysisResult Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory,
        JsonObject runtime, IReadOnlyCollection<int> selectedLayerIds)
    {
        var components = new List<ShaderPeriodComponent>();
        var unresolved = new List<ShaderTemporalUnresolved>();
        var ruled = new HashSet<(int, string)>();
        var seen = new HashSet<int>();
        var objects = (scene["objects"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => SceneGraph.Int(item["id"]) is not null).ToDictionary(item => SceneGraph.Int(item["id"])!.Value);
        JsonObject[] dependencies = [.. (runtime["runtime_dependencies"] as JsonArray ?? []).OfType<JsonObject>()];
        var animatedOwners = (runtime["runtime_animation_periods"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(trace => SceneGraph.Int(trace["source_owner_layer_id"])).OfType<int>().ToHashSet();
        foreach (JsonObject layer in (runtime["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (SceneGraph.Int(layer["owner"]) is not int owner || !selectedLayerIds.Contains(owner) || !seen.Add(owner)) continue;
            objects.TryGetValue(owner, out JsonObject? ownerObject);
            AddScriptDrivenUniforms(owner, ownerObject, dependencies, unresolved);
            var groups = (runtime["runtime_layers"] as JsonArray)!.OfType<JsonObject>().Where(x => SceneGraph.Int(x["owner"]) == owner)
                .SelectMany(x => (x["materials"] as JsonArray ?? []).OfType<JsonObject>())
                .Where(m => m["time_signature"] is JsonObject && m["shader"] is JsonValue &&
                    !(m["active_uniforms"] as JsonArray ?? []).Any(u => AlternateClocks.Contains(u?.ToString())))
                .GroupBy(m => m["shader"]!.GetValue<string>());
            foreach (var group in groups)
            {
                ruled.Add((owner, group.Key));
                (int, int)[] passes = ownerObject is null ? [] : EffectPasses(source, assetsDirectory, ownerObject, group.Key);
                var (effect, pass) = passes.FirstOrDefault((-1, -1));
                string resource = "shaders/" + group.Key;
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
                if (!TryJoinPeriods(signatures, out (BigInteger Num, BigInteger Den, bool Pi)[] classes, out double[] loose))
                { Fail(false, "period_class_unknown", "period is not a simple rational: " + string.Join(", ", loose)); continue; }
                if (classes.Length == 0) continue;
                string periods = string.Join(", ", classes.Select(c => $"{c.Num}/{c.Den}{(c.Pi ? "·π" : "")} s"));
                if (classes.Length > 1)
                {
                    Fail(true, "incommensurate_classes", $"periods {periods} have an irrational ratio; one time scale per material keeps the ratio");
                    continue;
                }
                var (num, den, pi) = classes[0];
                double seconds = (double)num / (double)den * (pi ? Math.PI : 1);
                CommonLoopRational? exact = !pi && num <= long.MaxValue && den <= long.MaxValue ? new((long)num, (long)den) : null;
                if (exact is null && passes.Length == 0)
                {
                    Fail(false, "irrational_period_not_retimable", $"period {periods} needs a time scale, but the material has no authored effect pass");
                    continue;
                }
                components.Add(new(new CommonLoopComponent($"shader/{owner}/{group.Key}",
                    new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic, exact), AllowRetime: passes.Length > 0),
                    owner, passes, $"SPIR-V time signature of {resource}: period {periods}"));
            }
        }
        return new(components, unresolved, ruled);
    }

    /// <summary>原因串的码：到第一个空格、冒号或 @ 为止。</summary>
    private static string Code(string reason) => reason.Split([' ', ':', '@'], 2)[0];

    private static bool IsSystemInput(string name) => name.StartsWith("g_AudioSpectrum", StringComparison.Ordinal) ||
        name.StartsWith("g_Pointer", StringComparison.Ordinal) || name is "g_ParallaxPosition" or "g_Daytime";

    /// <summary>
    /// 同一 (层, shader) 的各材质按类合并：类内取有理数 LCM（lcm(a/b, c/d) = lcm(a,c)/gcd(b,d)）。
    /// num = 0 的周期不是简单有理数，类别不明，返回 false。
    /// </summary>
    private static bool TryJoinPeriods(JsonObject[] signatures, out (BigInteger, BigInteger, bool)[] classes, out double[] loose)
    {
        var joined = new Dictionary<bool, (BigInteger Num, BigInteger Den)>();
        var unknown = new List<double>();
        foreach (JsonObject period in signatures.SelectMany(s => (s["periods"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            BigInteger num = period["num"]!.GetValue<long>(), den = period["den"]!.GetValue<long>();
            if (num <= 0 || den <= 0) { unknown.Add(period["seconds"]!.GetValue<double>()); continue; }
            BigInteger g = BigInteger.GreatestCommonDivisor(num, den);
            (num, den) = (num / g, den / g);
            bool pi = period["pi"]?.GetValue<bool>() == true;
            joined[pi] = joined.TryGetValue(pi, out var old)
                ? (old.Num / BigInteger.GreatestCommonDivisor(old.Num, num) * num, BigInteger.GreatestCommonDivisor(old.Den, den))
                : (num, den);
        }
        classes = [.. joined.Select(pair => (pair.Value.Num, pair.Value.Den, pair.Key))];
        loose = [.. unknown];
        return unknown.Count == 0;
    }

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

    /// <summary>本层可见作者效果里用到这个材质 shader 的 (effect, pass)。</summary>
    private static (int, int)[] EffectPasses(ProjectSource source, string? assetsDirectory, JsonObject owner, string shader)
    {
        var found = new List<(int, int)>();
        JsonArray effects = owner["effects"] as JsonArray ?? [];
        for (int effect = 0; effect < effects.Count; ++effect)
        {
            if (effects[effect] is not JsonObject item || item["visible"] is JsonValue shown && shown.TryGetValue(out bool visible) && !visible) continue;
            string[] shaders = [.. EffectMaterialShaders(source, assetsDirectory, item)];
            for (int pass = 0; pass < shaders.Length; ++pass)
                if (shaders[pass] == shader) found.Add((effect, pass));
        }
        return [.. found];
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
