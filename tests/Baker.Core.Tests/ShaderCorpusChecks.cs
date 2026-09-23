using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>一个 suite 的执行结果：默认上限下的分析、合成工程目录与场景，供留在代码里的跨模块断言复用。</summary>
internal sealed record ShaderCorpusRun(ShaderPeriodAnalysisResult Analysis, string Directory, JsonObject Scene);

/// <summary>
/// tests/corpus/shader-corpus.json 的执行器：按 suite 合成工程、跑 ShaderPeriodAnalysis.Analyze、逐条核对 expect。
/// 每条 expect 带原断言的 message。
/// </summary>
internal static class ShaderCorpusChecks
{
    private static readonly Lazy<JsonObject> Corpus = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "corpus", "shader-corpus.json")))!.AsObject());

    internal static ShaderCorpusRun Run(Action<bool, string> check, string root, string suiteName)
    {
        JsonObject suite = Corpus.Value["suites"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["name"]!.GetValue<string>() == suiteName);
        string directory = Path.Combine(root, "shader-corpus-" + suiteName);
        void Write(string relative, string text)
        {
            string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        static string Lines(JsonNode? lines) => string.Join('\n', (lines?.AsArray() ?? []).Select(line => line!.GetValue<string>()));
        Write("project.json", "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        JsonObject shaders = suite["shaders"]!.AsObject();
        foreach ((string name, JsonNode? node) in shaders)
        {
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
            Write($"materials/{name}.json", $"{{\"passes\":[{{\"shader\":\"{name}\"}}]}}");
            foreach (string stage in new[] { "frag", "vert" })
                if (Stage(node!.AsObject(), stage) is string text) Write($"shaders/{name}.{stage}", text);
        }
        string? Stage(JsonObject shader, string stage)
        {
            if (shader[stage] is JsonArray own) return Lines(own);
            if (shader["same_as"] is not JsonValue same) return null;
            string? baseText = Stage(shaders[same.GetValue<string>()]!.AsObject(), stage);
            if (baseText is null || stage != "frag") return baseText;
            return (shader["prepend"] is null ? "" : Lines(shader["prepend"]) + "\n") + baseText + (shader["append"] is null ? "" : "\n" + Lines(shader["append"]));
        }
        foreach ((string path, JsonNode? lines) in suite["files"]?.AsObject() ?? []) Write(path, Lines(lines));
        // 贴图写法：数字 = flags；{"flags":…, "bytes":…} = 同样的头、再补零到指定总字节数（模拟真实尺寸的贴图）。
        foreach ((string path, JsonNode? texture) in suite["textures"]?.AsObject() ?? [])
            WriteTexture(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)),
                texture is JsonObject spec ? spec["flags"]!.GetValue<uint>() : texture!.GetValue<uint>(),
                texture is JsonObject sized ? sized["bytes"]!.GetValue<int>() : 0);
        var objects = new JsonArray();
        foreach (JsonObject item in suite["objects"]!.AsArray().OfType<JsonObject>())
        {
            if (item["raw"] is JsonObject raw) { objects.Add(Doubles(raw)); continue; }
            var pass = new JsonObject();
            if (item["pass_textures"] is JsonArray textures) pass["textures"] = textures.DeepClone();
            if (item["constants"] is JsonObject constants) pass["constantshadervalues"] = Doubles(constants, inConstants: true);
            if (item["combos"] is JsonObject combos) pass["combos"] = combos.DeepClone();
            var owner = new JsonObject { ["id"] = item["id"]!.GetValue<int>() };
            if (item["size"] is JsonValue size) owner["size"] = size.DeepClone();
            owner["effects"] = new JsonArray(new JsonObject { ["file"] = $"effects/{item["shader"]!.GetValue<string>()}.json", ["passes"] = new JsonArray(pass) });
            if (item["fullscreen"] is JsonValue fullscreen) owner["fullscreen"] = fullscreen.DeepClone();
            objects.Add(owner);
        }
        var scene = new JsonObject { ["objects"] = objects };
        Write("scene.json", scene.ToJsonString());
        int[] ids = [.. objects.Select(item => item!["id"]!.GetValue<int>())];
        using var source = new ProjectSource(directory);
        // 同一 (上限, 调速预算) 只分析一次；expect 的 retime_percent 是交给 Analyze 的调速预算（百分比），缺省用 Analyze 自己的缺省。
        var analyses = new Dictionary<(double, double), ShaderPeriodAnalysisResult>();
        double? defaultCeiling = suite["ceiling"]?.GetValue<double>();
        ShaderPeriodAnalysisResult Analyze(double? ceiling, double? retimePercent)
        {
            var key = (ceiling ?? defaultCeiling ?? double.NaN, retimePercent ?? ShaderPeriodAnalysis.DefaultRetimePercent);
            if (!analyses.TryGetValue(key, out ShaderPeriodAnalysisResult? result))
                analyses[key] = result = ShaderPeriodAnalysis.Analyze(scene, source, null, ids, ceiling ?? defaultCeiling, key.Item2);
            return result;
        }
        foreach (JsonObject expect in suite["expect"]!.AsArray().OfType<JsonObject>())
            check(Holds(Analyze(expect["ceiling"]?.GetValue<double>(), expect["retime_percent"]?.GetValue<double>()), expect),
                expect["message"]!.GetValue<string>());
        return new(Analyze(null, null), directory, scene);
    }

    /// <summary>
    /// 原测试用 C# double 建常量，数值记号按 double 输出（-30.0 写成 -30），会进 evidence 文案；这里把常量里的每个
    /// JSON 数字换成 double 值，其余（combo 的 int/1.0/"1" 等写法）保持原样。
    /// </summary>
    private static JsonNode? Doubles(JsonNode? node, bool inConstants = false) => node switch
    {
        JsonObject values => new JsonObject(values.Select(pair => KeyValuePair.Create(pair.Key,
            Doubles(pair.Value, inConstants || pair.Key == "constantshadervalues")))),
        JsonArray items => new JsonArray([.. items.Select(item => Doubles(item, inConstants))]),
        JsonValue value when inConstants && value.GetValueKind() == JsonValueKind.Number => JsonValue.Create(value.GetValue<double>()),
        _ => node?.DeepClone(),
    };

    private static bool Holds(ShaderPeriodAnalysisResult analysis, JsonObject expect)
    {
        bool ok = true;
        int[]? layers = expect["layers"] is JsonArray set ? [.. set.Select(item => item!.GetValue<int>())] : null;
        IEnumerable<ShaderPeriodComponent> scopeComponents = analysis.Components.Where(item => layers is null || layers.Contains(item.Patch.OwnerLayerId));
        IEnumerable<ShaderTemporalUnresolved> scopeUnresolved = analysis.Unresolved.Where(item => layers is null || layers.Contains(item.OwnerLayerId));
        if (expect["components_total"] is JsonValue totalComponents) ok &= scopeComponents.Count() == totalComponents.GetValue<int>();
        if (expect["unresolved_total"] is JsonValue totalUnresolved) ok &= scopeUnresolved.Count() == totalUnresolved.GetValue<int>();
        if (expect["layer"] is not JsonValue layerValue) return ok;
        int layer = layerValue.GetValue<int>();
        ShaderPeriodComponent[] components = [.. analysis.Components.Where(item => item.Patch.OwnerLayerId == layer &&
            (expect["key_filter"] is not JsonValue filter || item.Patch.ConstantKey == filter.GetValue<string>()))];
        ShaderTemporalUnresolved[] unresolved = [.. analysis.Unresolved.Where(item => item.OwnerLayerId == layer &&
            (expect["pass_index"] is not JsonValue pass || item.PassIndex == pass.GetValue<int>()))];
        if (expect["components"] is JsonValue componentCount) ok &= components.Length == componentCount.GetValue<int>();
        if (expect["unresolved"] is JsonValue unresolvedCount) ok &= unresolved.Length == unresolvedCount.GetValue<int>();

        bool AllUnresolved(Func<ShaderTemporalUnresolved, bool> predicate) => unresolved.Length > 0 && unresolved.All(predicate);
        bool AllComponents(Func<ShaderPeriodComponent, bool> predicate) => components.Length > 0 && components.All(predicate);
        IEnumerable<string> Texts(string key) => (expect[key]?.AsArray() ?? []).Select(item => item!.GetValue<string>());
        if (expect["kind"] is JsonValue kind) ok &= AllUnresolved(item => item.Kind.ToString() == kind.GetValue<string>());
        if (expect["detail"] is JsonValue detail) ok &= AllUnresolved(item => item.Detail == detail.GetValue<string>());
        foreach (string text in Texts("detail_contains")) ok &= AllUnresolved(item => item.Detail.Contains(text, StringComparison.Ordinal));
        foreach (string text in Texts("detail_contains_ignore_case")) ok &= AllUnresolved(item => item.Detail.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (string text in Texts("detail_not_contains")) ok &= AllUnresolved(item => !item.Detail.Contains(text, StringComparison.Ordinal));
        if (expect["mechanism"] is JsonValue mechanism) ok &= AllUnresolved(item => item.Mechanism == mechanism.GetValue<string>());
        if (expect["bounded"] is JsonValue bounded) ok &= AllUnresolved(item => item.BoundedDisplacement == bounded.GetValue<bool>());
        if (expect["message_key"] is JsonValue messageKey) ok &= AllUnresolved(item => item.Message?.Key == messageKey.GetValue<string>());

        if (expect["allow_retime"] is JsonValue retime) ok &= AllComponents(item => item.Component.AllowRetime == retime.GetValue<bool>());
        if (expect["key"] is JsonValue constantKey) ok &= AllComponents(item => item.Patch.ConstantKey == constantKey.GetValue<string>());
        if (expect["exponent"] is JsonValue exponent) ok &= AllComponents(item => item.Patch.SpeedExponent == exponent.GetValue<double>());
        if (expect["old_value"] is JsonValue oldValue) ok &= AllComponents(item => item.Patch.OldValue == oldValue.GetValue<double>());
        if (expect.ContainsKey("companion"))
            ok &= AllComponents(item => item.Patch.CompanionConstantKey == expect["companion"]?.GetValue<string>());
        if (expect["evidence"] is JsonValue evidence) ok &= AllComponents(item => item.Component.BasePeriod!.Evidence.ToString() == evidence.GetValue<string>());
        if (expect["id_suffix"] is JsonValue suffix) ok &= AllComponents(item => item.Component.Id.EndsWith(suffix.GetValue<string>(), StringComparison.Ordinal));
        foreach (string text in Texts("evidence_contains")) ok &= AllComponents(item => item.Evidence.Contains(text, StringComparison.Ordinal));
        if (expect["exact_seconds"] is JsonValue exact)
        {
            string[] parts = exact.GetValue<string>().Split('/');
            var rational = parts.Length == 1 ? new CommonLoopRational(long.Parse(parts[0], CultureInfo.InvariantCulture))
                : new CommonLoopRational(long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
            ok &= AllComponents(item => item.Component.BasePeriod!.ExactSeconds == rational);
        }
        double tolerance = expect["tolerance"]?.GetValue<double>() ?? 1e-12;
        double? expected = expect["period_seconds"]?.GetValue<double>();
        if (expect["period"] is JsonValue period)
        {
            // "分子/速度"，分子为 2pi 或数字：与实现同一算式求值。
            string[] parts = period.GetValue<string>().Split('/');
            double numerator = parts[0] == "2pi" ? 2 * Math.PI : double.Parse(parts[0], CultureInfo.InvariantCulture);
            expected = numerator / double.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        if (expected is double seconds) ok &= AllComponents(item => Math.Abs(item.Component.BasePeriod!.Seconds - seconds) <= tolerance);
        return ok;
    }

    /// <summary>TEXV0005/TEXI0001 头、flags 在偏移 22 的最小 .tex：flags 0 为 repeat 寻址的静止贴图，2 为 ClampUVs。</summary>
    private static void WriteTexture(string path, uint flags, int totalBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0"));
        writer.Write(0); writer.Write(flags);
        writer.Write(64); writer.Write(64); writer.Write(64); writer.Write(64); writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("TEXB0001\0"));
        writer.Write(new byte[32]);
        writer.Flush();
        if (writer.BaseStream.Length < totalBytes) writer.Write(new byte[totalBytes - writer.BaseStream.Length]);
    }
}
