using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 粒子系统的外部输入与随机源判据，都读 emitter / initializer / operator 这三节，对象自身的
/// instanceoverride 一并参与。
/// <para>
/// 音频输入（audioprocessingmode ≠ 0）与着色器音频频谱、粒子 pointer 链接同级：实时外部输入没有源可
/// 证明的周期，这样的层一律留实时、不进烘焙。
/// </para>
/// <para>
/// 随机源判据这一版<b>只决定理由文案</b>：仍按现行分配处理，但"证明不了周期"要说清是湍流噪声场、
/// randomframe、随机初始化器还是发射盒范围，而不是一句笼统的"精灵纹理周期不等于粒子有效周期"。
/// 粒子有效周期的合成（发射间隔、生命期、operator 时间参数与精灵周期的最小公倍数）不在本次范围内。
/// </para>
/// </summary>
internal static class ParticleInputAnalysis
{
    private static readonly string[] Sections = ["emitter", "initializer", "operator"];

    /// <summary>粒子定义（或对象覆盖）的任一 emitter/initializer/operator 节点含非零 audioprocessingmode。</summary>
    internal static bool HasAudioInput(params JsonNode?[] nodes) => SectionNodes(nodes).Any(AudioDriven);

    /// <summary>
    /// 一层粒子为什么证明不出有效周期。返回 (code, detail)：code 供下游读，detail 供用户读。
    /// 顺序固定，先外部输入后随机源，取第一条命中的原因。
    /// <para>
    /// detail 一律经 <see cref="Messages"/> 生成：写进 plan 的仍是逐字不变的英文原文，
    /// <c>unresolved_localized</c> 由 <see cref="Messages.Localize"/> 反查出中文。节点名这一半本身分语言
    /// （未命名节点中英各一种写法），所以走 <see cref="Messages.EmitBilingual"/>。
    /// </para>
    /// </summary>
    internal static (string Code, string Detail) NonperiodicReason(JsonObject owner, ProjectSource source, string? assetsDirectory)
    {
        if (owner["particle"] is not JsonValue particle || !particle.TryGetValue<string>(out string? resource))
            return ("particle_definition_unavailable", Messages.Emit("unresolved.particle_definition_missing"));
        JsonObject definition;
        try { definition = SceneAnalyzer.ReadResourceJson(source, assetsDirectory, resource); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ("particle_definition_unavailable",
                Messages.Emit("unresolved.particle_definition_unreadable", resource, error.Message));
        }

        foreach (JsonObject node in SectionNodes([definition, owner]))
        {
            if (!AudioDriven(node)) continue;
            string mode = node["audioprocessingmode"]!.ToJsonString();
            return ("particle_audio_input", Messages.EmitBilingual("unresolved.particle_audio_input",
                [NameZh(node), mode], [Name(node), mode]));
        }
        foreach (JsonObject node in SectionNodes([definition, owner]))
        {
            string name = RawName(node);
            if (!name.StartsWith("turbulen", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("noise", StringComparison.OrdinalIgnoreCase)) continue;
            return ("particle_nonperiodic_turbulent_velocity", Messages.EmitBilingual("unresolved.particle_turbulent_velocity",
                [NameZh(node)], [Name(node)]));
        }
        foreach (JsonNode node in SceneAnalyzer.Walk(definition))
            if (node is JsonObject animated && animated["animationmode"] is JsonValue mode &&
                mode.TryGetValue<string>(out string? text) && string.Equals(text, "randomframe", StringComparison.OrdinalIgnoreCase))
                return ("particle_nonperiodic_random_frame", Messages.Emit("unresolved.particle_random_frame"));
        foreach (JsonObject node in SectionNodes([definition, owner]))
        {
            if (!RawName(node).EndsWith("random", StringComparison.OrdinalIgnoreCase) || !RangeIsRandom(node["min"], node["max"])) continue;
            string low = node["min"]!.ToJsonString(), high = node["max"]!.ToJsonString();
            return ("particle_nonperiodic_random_initializer", Messages.EmitBilingual("unresolved.particle_random_initializer",
                [NameZh(node), low, high], [Name(node), low, high]));
        }
        foreach (JsonObject node in SectionNodes([definition, owner]))
        {
            if (!node.ContainsKey("distancemax") || Numbers(node["distancemax"]).All(value => value == 0)) continue;
            string extent = node["distancemax"]!.ToJsonString();
            return ("particle_nonperiodic_emitter_extent", Messages.EmitBilingual("unresolved.particle_emitter_extent",
                [NameZh(node), extent], [Name(node), extent]));
        }
        return ("particle_effective_period_not_modelled", Messages.Emit("unresolved.particle_effective_period_not_modelled", resource));
    }

    private static IEnumerable<JsonObject> SectionNodes(IEnumerable<JsonNode?> roots) =>
        roots.SelectMany(root => SceneAnalyzer.Walk(root).OfType<JsonObject>())
            .SelectMany(node => Sections.Where(node.ContainsKey)
                .SelectMany(section => SceneAnalyzer.Walk(node[section]).OfType<JsonObject>()));

    internal static bool AudioDriven(JsonObject node)
    {
        if (node["audioprocessingmode"] is not JsonValue mode) return false;
        if (mode.TryGetValue<double>(out double value)) return value != 0;
        // 读不成数字时按有输入处理：保守方向是留实时。
        return !(mode.TryGetValue<string>(out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed == 0);
    }

    private static string RawName(JsonObject node) =>
        node["name"] is JsonValue name && name.TryGetValue<string>(out string? text) ? text : "";

    private static string Name(JsonObject node) => RawName(node) is { Length: > 0 } name ? $"\"{name}\"" : "an unnamed particle node";

    /// <summary>节点名的中文写法：有名字时与英文相同（名字本身不翻译），没名字时给一句中文。</summary>
    private static string NameZh(JsonObject node) => RawName(node) is { Length: > 0 } name ? $"\"{name}\"" : "一个未命名的粒子节点";

    /// <summary>min 与 max 逐分量相等才是退化的随机源；任一分量不等就是真的在抽随机。</summary>
    private static bool RangeIsRandom(JsonNode? minimum, JsonNode? maximum)
    {
        if (minimum is null || maximum is null) return false;
        double[] low = Numbers(minimum), high = Numbers(maximum);
        return low.Length == 0 || high.Length == 0 || low.Length != high.Length || !low.SequenceEqual(high);
    }

    /// <summary>粒子参数写成数字或 "x y z" 字符串两种形式，都读成分量数组。</summary>
    internal static double[] Numbers(JsonNode? node)
    {
        if (node is not JsonValue value) return [];
        if (value.TryGetValue<double>(out double single)) return [single];
        if (!value.TryGetValue<string>(out string? text) || text is null) return [];
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var numbers = new double[parts.Length];
        for (int index = 0; index < parts.Length; ++index)
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index])) return [];
        return numbers;
    }
}
