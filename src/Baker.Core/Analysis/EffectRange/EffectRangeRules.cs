using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Baker.Core.Analysis.ShaderClock;

namespace Baker.Core.Analysis.EffectRange;

/// <summary>
/// HDR 闭合 R1 的特效值域规则表（嵌入资源 effect-range-rules.json）：输出只是输入纹理凸组合采样的特效着色器，
/// 按片元着色器归一化源码的 sha256 认领（不按特效名），每条带着色器层面的证明文字。
/// 认领了的特效不会把层输出推出 [0,1]，层上其余判据（R2–R5）照常求值。
/// 带条件的规则（如 pulse）另列 combo 允许集、常量界与证明所依赖头文件的指纹，逐项在作者取值上核对。
/// </summary>
internal sealed class EffectRangeRules
{
    /// <summary>combo 允许集：未设时取着色器注解里的默认值（被指纹钉住）。</summary>
    private sealed record ComboChoice(int Default, HashSet<int> Allowed);
    /// <summary>常量界：min/max 逐分量闭区间，above 是严格下界，increasing 要求分量严格递增。</summary>
    private sealed record ConstantBound(JsonNode? Default, double? Min, double? Max, double? Above, bool Increasing);
    /// <summary>若干标量常量之和的上界。</summary>
    private sealed record SumBound(string[] Terms, double Max);
    private sealed record Rule(string Id, Dictionary<string, HashSet<int>> ForbidCombos, Dictionary<string, ComboChoice> AllowCombos,
        Dictionary<string, string> Includes, Dictionary<string, ConstantBound> Constants, SumBound[] Sums);

    /// <summary>随程序集嵌入的规则表。</summary>
    public static EffectRangeRules Default { get; } = Parse(ReadEmbedded());

    private readonly Dictionary<string, Rule> table;

    private EffectRangeRules(Dictionary<string, Rule> table) => this.table = table;

    /// <summary>片元着色器指纹：归一化源码（去注释、压空白）的 UTF-8 sha256，小写十六进制。</summary>
    public static string Fingerprint(string fragment) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ShaderSource.Normalize(fragment))));

    private static string ReadEmbedded()
    {
        using Stream stream = typeof(EffectRangeRules).Assembly.GetManifestResourceStream("Baker.Core.effect-range-rules.json")
            ?? throw new InvalidDataException("Effect range rule table resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static double? Bound(JsonNode? node) => node is JsonValue value ? value.GetValue<double>() : null;

    public static EffectRangeRules Parse(string json)
    {
        JsonObject root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Effect range rule table is empty.");
        var rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject rule in root["rules"]!.AsArray().OfType<JsonObject>())
        {
            var forbid = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (combo, values) in rule["forbid_combos"] as JsonObject ?? [])
                forbid[combo] = [.. values!.AsArray().Select(value => value!.GetValue<int>())];
            var allow = new Dictionary<string, ComboChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (var (combo, spec) in rule["allow_combos"] as JsonObject ?? [])
                allow[combo] = new(spec!["default"]!.GetValue<int>(), [.. spec["values"]!.AsArray().Select(value => value!.GetValue<int>())]);
            var includes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (include, sha) in rule["includes"] as JsonObject ?? []) includes[include] = sha!.GetValue<string>();
            var constants = new Dictionary<string, ConstantBound>(StringComparer.Ordinal);
            foreach (var (name, spec) in rule["constants"] as JsonObject ?? [])
                constants[name] = new(spec!["default"]?.DeepClone(), Bound(spec["min"]), Bound(spec["max"]), Bound(spec["above"]),
                    spec["increasing"] is JsonValue increasing && increasing.GetValue<bool>());
            SumBound[] sums = [.. (rule["sums"] as JsonArray ?? []).OfType<JsonObject>().Select(sum => new SumBound(
                [.. sum["terms"]!.AsArray().Select(term => term!.GetValue<string>())], sum["max"]!.GetValue<double>()))];
            if (sums.SelectMany(sum => sum.Terms).FirstOrDefault(term => !constants.ContainsKey(term)) is string missing)
                throw new InvalidDataException($"Effect range rule sum term '{missing}' has no constant bound.");
            rules.Add(rule["fragment_sha256"]!.GetValue<string>(),
                new(rule["id"]!.GetValue<string>(), forbid, allow, includes, constants, sums));
        }
        return new(rules);
    }

    /// <summary>
    /// 这一个作者特效（场景里 effects[] 的一项）是否每个 pass 都被规则表证明为凸组合采样。
    /// 认领时 <paramref name="detail"/> 给出命中的规则 id，<paramref name="shaders"/> 追加各 pass 的着色器名（运行时材质按它对账）；不认领时给出第一处认不出的原因（英文，写进 checks）。
    /// <paramref name="properties"/> 用来解开常量上的 {user, value} 绑定。
    /// </summary>
    public bool IsRangeClosed(JsonObject effect, JsonObject properties, ProjectSource source, string? assets, ICollection<string> shaders, out string detail)
    {
        string resource = effect["file"] is JsonValue file && file.TryGetValue(out string? path) && path is not null ? path : "";
        if (resource.Length == 0) { detail = "an authored effect has no definition path"; return false; }
        JsonObject definition;
        try { definition = SceneAnalyzer.ReadResourceJson(source, assets, resource); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            detail = $"effect \"{resource}\" definition could not be read";
            return false;
        }
        JsonArray authoredPasses = effect["passes"] as JsonArray ?? [];
        var matched = new List<string>();
        int passIndex = 0;
        foreach (JsonObject definitionPass in (definition["passes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string? materialResource = definitionPass["material"] is JsonValue m && m.TryGetValue(out string? text) ? text : null;
            if (string.IsNullOrWhiteSpace(materialResource))
            {
                detail = $"effect \"{resource}\" has a pass that is not a material pass";
                return false;
            }
            JsonObject? authored = passIndex < authoredPasses.Count ? authoredPasses[passIndex] as JsonObject : null;
            passIndex++;
            if (!PassClosed(materialResource, authored, properties, source, assets, out string ruleId, out string shader, out string condition))
            {
                detail = condition.Length == 0 ? $"effect \"{resource}\" shader is not proven to be a convex resampling of its input"
                    : $"effect \"{resource}\" matches rule {ruleId} but {condition}";
                return false;
            }
            matched.Add(ruleId);
            shaders.Add(shader);
        }
        if (matched.Count == 0) { detail = $"effect \"{resource}\" declares no pass"; return false; }
        detail = $"effect \"{resource}\" is a convex resampling ({string.Join(", ", matched.Distinct(StringComparer.Ordinal))})";
        return true;
    }

    private bool PassClosed(string materialResource, JsonObject? authoredPass, JsonObject properties, ProjectSource source, string? assets,
        out string ruleId, out string shaderName, out string condition)
    {
        ruleId = "";
        shaderName = "";
        condition = "";
        try
        {
            JsonObject material = SceneAnalyzer.ReadResourceJson(source, assets, materialResource);
            if (material["passes"] is not JsonArray { Count: 1 } passes || passes[0] is not JsonObject pass) return false;
            // 特效 pass 写进本层的特效链；普通/半透明混合在已清空的目标上是覆盖写，其它混合可能叠加抬高上界。
            string? blending = pass["blending"] is JsonValue b && b.TryGetValue(out string? blend) ? blend : null;
            if (blending is not null && !Periodica.Domain.SdrRadianceCriteria.IsRangePreservingBlend(blending)) return false;
            string? shader = pass["shader"] is JsonValue s && s.TryGetValue(out string? name) ? name : null;
            if (string.IsNullOrWhiteSpace(shader) ||
                !ShaderPeriodAnalysis.TryReadShaderStage(source, assets, "shaders/" + shader + ".frag", out string fragment)) return false;
            if (!table.TryGetValue(Fingerprint(fragment), out Rule? rule)) return false;
            foreach (var (combo, forbidden) in rule.ForbidCombos)
                if (ComboValue(authoredPass, combo) is int value ? forbidden.Contains(value)
                    : ComboValue(pass, combo) is int materialValue && forbidden.Contains(materialValue)) return false;
            ruleId = rule.Id;
            if (!ConditionsHold(rule, authoredPass, pass, properties, source, assets, out condition)) return false;
            shaderName = shader;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>带条件规则的逐项核对：combo 允许集、证明所依赖的头文件、常量界与和界。不成立时给出英文原因。</summary>
    private static bool ConditionsHold(Rule rule, JsonObject? authoredPass, JsonObject materialPass, JsonObject properties,
        ProjectSource source, string? assets, out string condition)
    {
        condition = "";
        foreach (var (combo, choice) in rule.AllowCombos)
        {
            int value = ComboValue(authoredPass, combo) ?? ComboValue(materialPass, combo) ?? choice.Default;
            if (choice.Allowed.Contains(value)) continue;
            condition = $"combo {combo}={value} is outside the proven set";
            return false;
        }
        // 证明用到头文件里的函数（如 common_blending.h 的 ApplyBlending）：解析到的头文件须与被审读的那份逐记号相同。
        foreach (var (include, sha) in rule.Includes)
        {
            if (ShaderPeriodAnalysis.TryReadShaderStage(source, assets, "shaders/" + include, out string header) &&
                string.Equals(Fingerprint(header), sha, StringComparison.OrdinalIgnoreCase)) continue;
            condition = $"include \"{include}\" is missing or differs from the reviewed text";
            return false;
        }
        var scalars = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, bound) in rule.Constants)
        {
            JsonNode? raw = ConstantValue(authoredPass, name) ?? ConstantValue(materialPass, name) ?? bound.Default;
            var components = new List<double>();
            bool literal = SceneGraph.Resolve(raw, properties) switch
            {
                JsonValue number when number.TryGetValue(out double single) => Add(components, single),
                JsonValue text when text.TryGetValue(out string? list) => Periodica.Domain.SdrRadianceCriteria.TryParseComponents(list, components),
                _ => false,
            };
            if (!literal || components.Count == 0 || components.Any(component => !double.IsFinite(component)))
            {
                condition = $"constant {name} is not a decidable numeric literal (script, animation or non-numeric binding)";
                return false;
            }
            bool within = components.All(component =>
                (bound.Min is not double min || component >= min) &&
                (bound.Max is not double max || component <= max) &&
                (bound.Above is not double above || component > above));
            if (within && bound.Increasing)
                within = components.Count >= 2 && components.Zip(components.Skip(1)).All(pair => pair.First < pair.Second);
            if (!within)
            {
                condition = $"constant {name} = {string.Join(' ', components.Select(Format))} is outside its proven bound";
                return false;
            }
            if (components.Count == 1) scalars[name] = components[0];
        }
        foreach (SumBound sum in rule.Sums)
        {
            string terms = string.Join(" + ", sum.Terms);
            if (sum.Terms.Any(term => !scalars.ContainsKey(term)))
            {
                condition = $"constants {terms} are not all scalars";
                return false;
            }
            double total = sum.Terms.Sum(term => scalars[term]);
            if (total <= sum.Max) continue;
            condition = $"constants {terms} = {Format(total)} exceed {Format(sum.Max)}";
            return false;
        }
        return true;

        static bool Add(List<double> list, double value) { list.Add(value); return true; }
        static string Format(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>常量取值：constantshadervalues 按材质字段名精确匹配（渲染器按 std::map 键查）。</summary>
    private static JsonNode? ConstantValue(JsonObject? pass, string name) =>
        pass?["constantshadervalues"] is JsonObject values && values.TryGetPropertyValue(name, out JsonNode? value) ? value : null;

    /// <summary>combo 取值：场景 pass 覆盖材质 pass；读不出整数的按未设处理（着色器默认值，规则表的证明对默认值成立）。</summary>
    private static int? ComboValue(JsonObject? pass, string combo)
    {
        if (pass?["combos"] is not JsonObject combos) return null;
        foreach (var (key, value) in combos)
            if (string.Equals(key, combo, StringComparison.OrdinalIgnoreCase) && value is JsonValue v)
            {
                if (v.TryGetValue(out int asInt)) return asInt;
                if (v.TryGetValue(out double asDouble)) return (int)asDouble;
            }
        return null;
    }
}
