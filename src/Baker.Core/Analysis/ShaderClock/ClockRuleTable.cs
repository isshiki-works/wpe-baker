using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core.Analysis.ShaderClock;

/// <summary>
/// 着色器时钟规则表：<see cref="ClockRules.Json"/> 解析后的只读视图。
/// patterns 是具名指纹（在哪个视图上查、必须含哪些官方源码行、每个标识符恰好出现几次）；
/// rules 按试探顺序排列，第一条认领的规则给出裁定。每个指纹带 normalize 属性，保持各规则现有的空白敏感性口径。
/// </summary>
internal sealed class ClockRuleTable
{
    public static ClockRuleTable Default { get; } = Parse(ClockRules.Json);

    private readonly Dictionary<string, ClockPattern> patterns;

    private ClockRuleTable(Dictionary<string, ClockPattern> patterns, IReadOnlyList<ClockRule> rules) =>
        (this.patterns, Rules) = (patterns, rules);

    public IReadOnlyList<ClockRule> Rules { get; }

    public ClockPattern Pattern(string name) => patterns.TryGetValue(name, out ClockPattern? pattern)
        ? pattern : throw new InvalidDataException($"Shader clock pattern '{name}' is not in the rule table.");

    public bool Matches(string name, ShaderSource source) => Pattern(name).IsSatisfiedBy(source);

    public static ClockRuleTable Parse(string json)
    {
        JsonObject root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Shader clock rule table is empty.");
        var patterns = new Dictionary<string, ClockPattern>(StringComparer.Ordinal);
        foreach ((string name, JsonNode? node) in root["patterns"]!.AsObject())
            patterns.Add(name, ClockPattern.Parse(node!.AsObject()));
        var rules = new List<ClockRule>();
        foreach (JsonObject rule in root["rules"]!.AsArray().OfType<JsonObject>())
        {
            string[] match = rule["match"] is JsonArray names ? [.. names.Select(item => item!.GetValue<string>())] : [];
            foreach (string name in match)
                if (!patterns.ContainsKey(name)) throw new InvalidDataException($"Rule '{rule["id"]}' names unknown pattern '{name}'.");
            rules.Add(new(rule["id"]!.GetValue<string>(), rule["action"]!.GetValue<string>(), match, rule));
        }
        return new(patterns, rules);
    }
}

/// <summary>
/// 一条规则：<paramref name="Action"/> 是裁定处理器名，<paramref name="Match"/> 是必须全部成立的指纹名，
/// <paramref name="Data"/> 是规则原始 JSON（速度键、周期式、门控 combo、文案等由处理器读取）。
/// </summary>
internal sealed record ClockRule(string Id, string Action, IReadOnlyList<string> Match, JsonObject Data)
{
    public string Text(string key) => Data[key]?.GetValue<string>() ?? throw new InvalidDataException($"Rule '{Id}' lacks '{key}'.");
}

/// <summary>具名指纹：在原文或归一化文本上，任一子句成立即成立；forbid_alternate_clock 另要求该视图上没有备用时钟。</summary>
internal sealed record ClockPattern(bool Normalize, bool ForbidAlternateClock, IReadOnlyList<ClockClause> AnyOf)
{
    public bool IsSatisfiedBy(ShaderSource source) =>
        AnyOf.Any(clause => clause.IsSatisfiedBy(source, Normalize)) &&
        (!ForbidAlternateClock || !ShaderSource.HasAlternateClock(source.View(Normalize)));

    public static ClockPattern Parse(JsonObject node)
    {
        bool normalize = node["normalize"]?.GetValue<bool>() ?? throw new InvalidDataException("Pattern must state normalize.");
        bool forbid = node["forbid_alternate_clock"]?.GetValue<bool>() ?? false;
        ClockClause[] clauses = node["any"] is JsonArray any
            ? [.. any.Select(item => ClockClause.Parse(item!.AsObject()))]
            : [ClockClause.Parse(node)];
        return new(normalize, forbid, clauses);
    }
}

/// <summary>
/// 一个子句的全部条件同时成立才成立：contains 每行都在；count 里 "种类:标识符" 的计数满足给定值；
/// regex 里每个正则的匹配数满足给定值。计数值可以是整数（恰好等于），也可以是字符串：
/// "&gt;0" 表示大于，"U+S+2" 这类和式引用 vars 里命名正则的匹配数；require 对 vars 本身设条件。
/// </summary>
internal sealed record ClockClause(IReadOnlyList<string> Contains, IReadOnlyList<(string Kind, string Identifier, string Spec)> Counts,
    IReadOnlyList<(string Pattern, string Spec)> Patterns, IReadOnlyDictionary<string, string> Vars, IReadOnlyDictionary<string, string> Require)
{
    public bool IsSatisfiedBy(ShaderSource source, bool normalize)
    {
        string view = source.View(normalize);
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string name, string pattern) in Vars) values[name] = Regex.Matches(view, pattern, RegexOptions.CultureInvariant).Count;
        return Require.All(item => Holds(values[item.Key], item.Value, values)) &&
            Contains.All(line => view.Contains(line, StringComparison.Ordinal)) &&
            Counts.All(item => Holds(source.Count(normalize, item.Kind, item.Identifier), item.Spec, values)) &&
            Patterns.All(item => Holds(Regex.Matches(view, item.Pattern, RegexOptions.CultureInvariant).Count, item.Spec, values));
    }

    private static bool Holds(int actual, string spec, IReadOnlyDictionary<string, int> values) => spec.StartsWith('>')
        ? actual > Sum(spec[1..], values)
        : actual == Sum(spec, values);

    private static int Sum(string expression, IReadOnlyDictionary<string, int> values) => expression.Split('+').Sum(term =>
        int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number
            : values.TryGetValue(term, out int value) ? value
            : throw new InvalidDataException($"Unknown count variable '{term}'."));

    public static ClockClause Parse(JsonObject node)
    {
        static string Spec(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue(out int number)
            ? number.ToString(CultureInfo.InvariantCulture) : value!.GetValue<string>();
        string[] contains = node["contains"] is JsonArray lines ? [.. lines.Select(line => line!.GetValue<string>())] : [];
        var counts = new List<(string, string, string)>();
        foreach ((string key, JsonNode? value) in node["count"]?.AsObject() ?? [])
        {
            int colon = key.IndexOf(':');
            counts.Add((key[..colon], key[(colon + 1)..], Spec(value)));
        }
        var patterns = new List<(string, string)>();
        foreach (JsonArray pair in (node["regex"]?.AsArray() ?? []).OfType<JsonArray>())
            patterns.Add((pair[0]!.GetValue<string>(), Spec(pair[1])));
        Dictionary<string, string> Map(string name) => (node[name]?.AsObject() ?? []).ToDictionary(
            item => item.Key, item => Spec(item.Value), StringComparer.Ordinal);
        return new(contains, counts, patterns, Map("vars"), Map("require"));
    }
}
