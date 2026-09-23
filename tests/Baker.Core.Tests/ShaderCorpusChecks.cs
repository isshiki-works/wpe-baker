using System.Globalization;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// tests/corpus/shader-corpus.json 的执行器：按 suite 合成工程、跑 ShaderPeriodAnalysis.Analyze、逐条核对 expect。
/// 每条 expect 带原断言的 message；返回分析结果，供还留在代码里的跨模块断言（求解器、运行时投影）继续用。
/// </summary>
internal static class ShaderCorpusChecks
{
    private static readonly Lazy<JsonObject> Corpus = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "corpus", "shader-corpus.json")))!.AsObject());

    internal static ShaderPeriodAnalysisResult Run(Action<bool, string> check, string root, string suiteName)
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
            if (shader["same_as"] is JsonValue same)
            {
                string? baseText = Stage(shaders[same.GetValue<string>()]!.AsObject(), stage);
                if (shader[stage] is JsonArray own) return string.Join('\n', own.Select(line => line!.GetValue<string>()));
                if (baseText is null || stage != "frag") return baseText;
                string Lines(string key) => string.Join('\n', (shader[key]?.AsArray() ?? []).Select(line => line!.GetValue<string>()));
                return (shader["prepend"] is null ? "" : Lines("prepend") + "\n") + baseText + (shader["append"] is null ? "" : "\n" + Lines("append"));
            }
            return shader[stage] is JsonArray lines ? string.Join('\n', lines.Select(line => line!.GetValue<string>())) : null;
        }
        var objects = new JsonArray();
        foreach (JsonObject item in suite["objects"]!.AsArray().OfType<JsonObject>())
        {
            var pass = new JsonObject();
            if (item["constants"] is JsonObject constants) pass["constantshadervalues"] = constants.DeepClone();
            if (item["combos"] is JsonObject combos) pass["combos"] = combos.DeepClone();
            objects.Add(new JsonObject
            {
                ["id"] = item["id"]!.GetValue<int>(),
                ["effects"] = new JsonArray(new JsonObject { ["file"] = $"effects/{item["shader"]!.GetValue<string>()}.json", ["passes"] = new JsonArray(pass) })
            });
        }
        var scene = new JsonObject { ["objects"] = objects };
        Write("scene.json", scene.ToJsonString());
        int[] ids = [.. objects.Select(item => item!["id"]!.GetValue<int>())];
        using var source = new ProjectSource(directory);
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, null, ids, suite["ceiling"]?.GetValue<double>());
        foreach (JsonObject expect in suite["expect"]!.AsArray().OfType<JsonObject>())
            check(Holds(analysis, expect), expect["message"]!.GetValue<string>());
        return analysis;
    }

    private static bool Holds(ShaderPeriodAnalysisResult analysis, JsonObject expect)
    {
        bool ok = true;
        if (expect["components_total"] is JsonValue totalComponents) ok &= analysis.Components.Count == totalComponents.GetValue<int>();
        if (expect["unresolved_total"] is JsonValue totalUnresolved) ok &= analysis.Unresolved.Count == totalUnresolved.GetValue<int>();
        if (expect["layer"] is not JsonValue layerValue) return ok;
        int layer = layerValue.GetValue<int>();
        ShaderPeriodComponent[] components = [.. analysis.Components.Where(item => item.Patch.OwnerLayerId == layer)];
        ShaderTemporalUnresolved[] unresolved = [.. analysis.Unresolved.Where(item => item.OwnerLayerId == layer)];
        if (expect["components"] is JsonValue componentCount) ok &= components.Length == componentCount.GetValue<int>();
        if (expect["unresolved"] is JsonValue unresolvedCount) ok &= unresolved.Length == unresolvedCount.GetValue<int>();
        if (expect["kind"] is JsonValue kind)
            ok &= unresolved.Length > 0 && unresolved.All(item => item.Kind.ToString() == kind.GetValue<string>());
        foreach ((string key, StringComparison comparison) in new[] { ("detail_contains", StringComparison.Ordinal), ("detail_contains_ignore_case", StringComparison.OrdinalIgnoreCase) })
            foreach (string text in (expect[key]?.AsArray() ?? []).Select(item => item!.GetValue<string>()))
                ok &= unresolved.Length > 0 && unresolved.All(item => item.Detail.Contains(text, comparison));
        if (expect["allow_retime"] is JsonValue retime) ok &= components.Length > 0 && components.All(item => item.Component.AllowRetime == retime.GetValue<bool>());
        if (expect["key"] is JsonValue constantKey) ok &= components.Length > 0 && components.All(item => item.Patch.ConstantKey == constantKey.GetValue<string>());
        if (expect["exponent"] is JsonValue exponent) ok &= components.Length > 0 && components.All(item => item.Patch.SpeedExponent == exponent.GetValue<double>());
        if (expect["period_seconds"] is JsonValue seconds)
            ok &= components.Length == 1 && Math.Abs(components[0].Component.BasePeriod!.Seconds - seconds.GetValue<double>()) < 1e-12;
        if (expect["period"] is JsonValue period)
        {
            // "分子/速度"，分子为 2pi 或数字：与实现同一算式求值，容差 1e-12。
            string[] parts = period.GetValue<string>().Split('/');
            double numerator = parts[0] == "2pi" ? 2 * Math.PI : double.Parse(parts[0], CultureInfo.InvariantCulture);
            double expected = numerator / double.Parse(parts[1], CultureInfo.InvariantCulture);
            ok &= components.Length == 1 && Math.Abs(components[0].Component.BasePeriod!.Seconds - expected) < 1e-12;
        }
        return ok;
    }
}
