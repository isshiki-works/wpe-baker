using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 逐帧步进脚本：update 每帧给 value（或它的一个分量）加/减一个脚本属性常量，越过另一个常量就复位成第三个常量，
/// 不读时钟（工坊共享的"弹幕"位移脚本就是这种）。状态序列按官方的实际行为逐帧模拟：属性按单精度存，每帧读出、
/// 运算、再按单精度写回，所以周期按 float32 精确求，不按实数行程/步长。周期以帧计，按捕获帧率换成秒。
/// 可选的 switch(scriptProperties.K) 按 K 的烘焙值选分支。初值不在循环上（有过渡段）的不认。
/// </summary>
internal static class ScriptFrameStep
{
    internal sealed record Script(int OwnerLayerId, string Binding, ulong? PeriodFrames);

    /// <summary>被烘图层上所有认得的逐帧步进脚本；PeriodFrames 为 null = 在 <paramref name="frameCap"/> 帧内没有闭合。</summary>
    internal static List<Script> Find(JsonObject scene, IReadOnlyCollection<int> bakedLayerIds, ulong frameCap)
    {
        var found = new List<Script>();
        foreach (JsonObject layer in (scene["objects"] as JsonArray ?? []).OfType<JsonObject>())
            if (SceneGraph.Int(layer["id"]) is int id && bakedLayerIds.Contains(id))
                foreach (var (binding, node) in Bindings(layer, ""))
                    if (Step(node) is { } step && Period(step, frameCap) is (true, var frames)) found.Add(new(id, binding, frames));
        return found;
    }

    /// <summary>全部周期的最小公倍数（帧）；有一条没闭合则为 null。</summary>
    internal static BigInteger? JointFrames(IEnumerable<Script> scripts)
    {
        BigInteger joint = 1;
        foreach (Script script in scripts)
        {
            if (script.PeriodFrames is not ulong frames) return null;
            joint = joint / BigInteger.GreatestCommonDivisor(joint, frames) * frames;
        }
        return joint;
    }

    private static IEnumerable<(string Binding, JsonObject Node)> Bindings(JsonObject node, string path)
    {
        foreach (var (key, child) in node)
        {
            string name = path.Length == 0 ? key : path + "." + key;
            if (child is JsonObject value && value["script"] is JsonValue) yield return (name, value);
            else if (child is JsonObject inner) foreach (var item in Bindings(inner, name)) yield return item;
            else if (child is JsonArray array)
                for (int index = 0; index < array.Count; ++index)
                    if (array[index] is JsonObject element) foreach (var item in Bindings(element, name + "." + index)) yield return item;
        }
    }

    private sealed record StepRule(float Initial, bool Below, float End, float Start, float Delta);

    private static readonly Regex Update = new(@"function\s+update\s*\(\s*value\s*\)\s*\{", RegexOptions.CultureInvariant);
    private static readonly Regex Switch = new(@"^if\(scriptProperties\.(?<k>\w+)\)\{switch\(scriptProperties\.\k<k>\)\{(?<cases>.*)\}returnvalue;?\}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Case = new(@"case\(?(?<q>[""']?)(?<v>[^:""'()]*)\k<q>\)?:(?<body>.*?)break;", RegexOptions.CultureInvariant);
    private static readonly Regex Rule = new(@"^if\(value(?:\.(?<c>[xyzw]))?(?<cmp>[<>])scriptProperties\.(?<end>\w+)\)\{value(?:\.\k<c>)?=scriptProperties\.(?<start>\w+);?\}" +
        @"else\{value(?:\.\k<c>)?(?<op>[+-])=scriptProperties\.(?<step>\w+);?\}$", RegexOptions.CultureInvariant);

    /// <summary>认形态：update 体去掉空白后整段必须是"越界复位否则步进"（可包在按一个脚本属性分支的 switch 里）加 return value。</summary>
    private static StepRule? Step(JsonObject binding)
    {
        // 只许一个 update 函数：别的回调（init、点击等）也可能改状态
        if (!binding["script"]!.AsValue().TryGetValue(out string? code) || binding["scriptproperties"] is not JsonObject properties ||
            Regex.Matches(code, @"\bfunction\b").Count != 1 || code.Contains("=>")) return null;
        Match head = Update.Match(code);
        if (!head.Success) return null;
        int depth = 1, end = head.Index + head.Length;
        for (; end < code.Length && depth > 0; ++end) depth += code[end] == '{' ? 1 : code[end] == '}' ? -1 : 0;
        if (depth != 0) return null;
        string body = Regex.Replace(code[(head.Index + head.Length)..(end - 1)], @"\s+", "");
        string? rule = null;
        if (Switch.Match(body) is { Success: true } choice)
        {
            string cases = choice.Groups["cases"].Value, key = Text(properties[choice.Groups["k"].Value]) ?? "";
            MatchCollection all = Case.Matches(cases);
            if (string.Concat(all.Select(item => item.Value)) != cases) return null;
            rule = all.FirstOrDefault(item => item.Groups["v"].Value == key)?.Groups["body"].Value;
        }
        else if (body.EndsWith("returnvalue;", StringComparison.Ordinal)) rule = body[..^"returnvalue;".Length];
        if (rule is null || Rule.Match(rule) is not { Success: true } match) return null;
        bool below = match.Groups["cmp"].Value == "<", minus = match.Groups["op"].Value == "-";
        // 只认朝边界走的方向：小于边界复位的必须递减，大于边界复位的必须递增；否则状态单调漂走，不是这一类。
        if (below != minus || Number(properties[match.Groups["end"].Value]) is not float limit ||
            Number(properties[match.Groups["start"].Value]) is not float start || Number(properties[match.Groups["step"].Value]) is not float delta ||
            !(delta > 0) || Initial(binding["value"], match.Groups["c"].Success ? "xyzw".IndexOf(match.Groups["c"].Value[0]) : -1) is not float initial)
            return null;
        return new(initial, below, limit, start, minus ? -delta : delta);
    }

    /// <summary>从复位值起逐帧模拟到回到复位值 → (认, 周期帧数)。闭合了但初值不在循环上 → 不认；上限内没闭合 → 认，周期 null。</summary>
    private static (bool Recognized, ulong? Frames) Period(StepRule rule, ulong frameCap)
    {
        bool onCycle = false;
        float x = rule.Start;
        for (ulong frames = 1; frames <= frameCap; ++frames)
        {
            onCycle |= BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(rule.Initial);
            if (rule.Below ? x < rule.End : x > rule.End) return (onCycle, frames);
            x = (float)((double)x + rule.Delta);   // JS 按双精度算，写回属性按单精度存
        }
        return (true, null);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue(out string? text) ? text : value.TryGetValue(out double number) ? number.ToString(CultureInfo.InvariantCulture) : null
        : null;

    private static float? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number)
        ? (float)number : null;

    private static float? Initial(JsonNode? node, int component)
    {
        if (component < 0) return Number(node);
        string[] parts = (Text(node) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return component < parts.Length && float.TryParse(parts[component], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value : null;
    }
}
