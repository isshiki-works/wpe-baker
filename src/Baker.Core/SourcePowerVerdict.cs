using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 把"烘前实测原作功耗"接进分析结论：采样器报告压成 plan 的 <c>source_power</c> 段，
/// 按实测事实分档，再把"值不值得烘"顶到结论第一行。这里只读采样报告与 plan 已有字段，
/// 不做任何渲染判定，也不改动 plan 现有的英文字段。
/// </summary>
public static class SourcePowerVerdict
{
    /// <summary>plan 里挂实测读数的字段名。</summary>
    public const string Field = "source_power";

    // 分档阈值来自 2026-09-18 笔记本 A/B/B/A 实测（reports-20260916/abba-heavy-rc11.md）：
    // 原作核显域 <1 W 的案怎么烘都省不回来（普查 6 案 0.09–1.1 W）；1–3 W 档没有一个案进过"省电"判定；
    // ≥3 W 里只有整层整幅烘走的那几案给出 −70% 以上，效果前缀 / 保留实时 / 分层路线 8 案里只有 1 案省电。
    public const double LightWatts = 1, HeavyWatts = 3;

    /// <summary>&lt;1 W：原作本来就不费电。</summary>
    public const string NotWorth = "source_power.not_worth";
    /// <summary>1–3 W：能烘，省电有限。</summary>
    public const string Limited = "source_power.limited";
    /// <summary>≥3 W 且整层整幅：能烘，值得。</summary>
    public const string Worth = "source_power.worth";
    /// <summary>≥3 W 但走效果前缀 / 保留实时 / 分层：这条路线省电有限。</summary>
    public const string RouteLimited = "source_power.route_limited";
    /// <summary>这台机器测不了原作功耗。</summary>
    public const string Unavailable = "source_power.unavailable";

    /// <summary>
    /// 采样落盘的目录：必须在分析输出目录**之外**。分析要求自己的输出目录是新的，
    /// 而实测在分析之前跑，采样目录若放在里面，分析会直接以 "Analysis output must be new." 失败。
    /// </summary>
    public static string SampleDirectory(string analysisDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisDirectory);
        return analysisDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-source-power";
    }

    /// <summary>实测被跳过（平台测不了、用户没开）时挂的读数段，说明为什么没有数。</summary>
    public static JsonObject Skipped(string reason) => new()
    {
        ["status"] = "skipped", ["reason"] = reason, ["measured_watts"] = null,
        ["metric"] = "platform_power.igpu_domain_watts.median"
    };

    /// <summary>采样器报告 → plan 里要挂的 <c>source_power</c> 段。只搬读数，不重新判定。</summary>
    public static JsonObject FromSample(JsonObject sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var verdict = sample["verdict"] as JsonObject;
        var power = sample["platform_power"] as JsonObject;
        return new JsonObject
        {
            ["status"] = verdict?["status"]?.DeepClone() ?? "not_measured",
            ["measured_watts"] = verdict?["measured_watts"]?.DeepClone(),
            ["threshold_watts"] = verdict?["threshold_watts"]?.DeepClone(),
            ["metric"] = verdict?["metric"]?.DeepClone() ?? "platform_power.igpu_domain_watts.median",
            ["worth_baking"] = verdict?["worth_baking"]?.DeepClone(),
            ["text"] = verdict?["text"]?.DeepClone(),
            ["igpu_domain_watts"] = power?["igpu_domain_watts"]?.DeepClone(),
            ["package_watts"] = power?["package_watts"]?.DeepClone(),
            ["power_status"] = power?["status"]?.DeepClone(),
            ["display"] = sample["display"]?.DeepClone(),
            ["sampler_status"] = sample["status"]?.DeepClone(),
            ["report_path"] = sample["report_path"]?.DeepClone(),
            // GUI 的结论第一行读的就是这一段（PlainLanguage.Verdict），所以原样留一份。
            ["verdict"] = verdict?.DeepClone()
        };
    }

    /// <summary>plan 里已有的实测读数属于哪一档；没测过返回 null。</summary>
    public static string? Classify(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan[Field] is not JsonObject power) return null;
        if (power["status"]?.GetValue<string>() != "measured" || Watts(power) is not double watts) return Unavailable;
        if (watts < LightWatts) return NotWorth;
        if (watts < HeavyWatts) return Limited;
        return WholeFrameRoute(plan) ? Worth : RouteLimited;
    }

    /// <summary>实测到的核显域功耗；没测到返回 null。</summary>
    public static double? Watts(JsonObject power)
    {
        ArgumentNullException.ThrowIfNull(power);
        // CLR 支撑的 JsonValue 不做跨数值类型转换，所以逐个类型试一遍。
        if (power["measured_watts"] is not JsonValue value) return null;
        if (value.TryGetValue(out double watts) && double.IsFinite(watts)) return watts;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out int small)) return small;
        return null;
    }

    /// <summary>
    /// 这条路线是不是"整幅整层"：整层烘、整幅视频、没有指定保留实时的根。
    /// 效果前缀、分层视频、保留实时三条路线都只烘走一部分工作量，实测省电幅度小得多。
    /// </summary>
    public static bool WholeFrameRoute(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan["route"]?.GetValue<string>() == "effect_prefix") return false;
        if (plan["settings"]?["video_layout"]?.GetValue<string>() is string layout && layout != "full_frame") return false;
        return (plan["settings"]?["retain_live_root_ids"] as JsonArray)?.Count is null or 0;
    }

    /// <summary>
    /// 烘完之后原作与成品各测一次，比出省了多少电。判定口径同 2026-09-18 的 A/B/B/A 实测报告：
    /// 核显域降幅 ≥30% 才算省电，±5% 以内算差不多，升上去就是更费。测不到就不给判断。
    /// </summary>
    public static JsonObject Gain(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        double? original = Watts(before), baked = Watts(after);
        if (before["status"]?.GetValue<string>() != "measured" || after["status"]?.GetValue<string>() != "measured" ||
            original is not > 0 || baked is null)
            return new JsonObject { ["status"] = "not_measured", ["key"] = "measured_gain.unavailable",
                ["metric"] = "platform_power.igpu_domain_watts.median",
                ["source_watts"] = original, ["baked_watts"] = baked,
                ["before"] = before.DeepClone(), ["after"] = after.DeepClone() };
        double change = (baked.Value - original.Value) / original.Value * 100;
        string key = change <= -SavedPercent ? "measured_gain.saved"
            : change > SamePercent ? "measured_gain.worse" : "measured_gain.same";
        return new JsonObject
        {
            ["status"] = "measured", ["key"] = key, ["metric"] = "platform_power.igpu_domain_watts.median",
            ["source_watts"] = original, ["baked_watts"] = baked, ["change_percent"] = change,
            ["saved_percent"] = -change, ["basis"] = "Integrated-GPU RAPL domain median, original versus baked.",
            ["before"] = before.DeepClone(), ["after"] = after.DeepClone()
        };
    }

    /// <summary>算作省电的核显域降幅，与省电判定口径一致。</summary>
    public const double SavedPercent = 30, SamePercent = 5;

    /// <summary>A/B 结论的那一句人话（中英各一句由调用方按语言取）。</summary>
    public static string GainLine(JsonObject gain, string language)
    {
        ArgumentNullException.ThrowIfNull(gain);
        string key = gain["key"]?.GetValue<string>() ?? "measured_gain.unavailable";
        double saved = gain["saved_percent"] is JsonValue value && value.TryGetValue(out double percent) ? percent : 0;
        return key == "measured_gain.saved"
            ? Messages.Get(key, language, saved.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
            : Messages.Get(key, language);
    }

    /// <summary>
    /// 把实测读数写进 plan，并把分档判定顶到结论第一行（中英各一句）。
    /// 重复调用只前置一次；没测过 / 没有结论行时只写字段。
    /// </summary>
    public static void Apply(JsonObject plan, JsonObject sourcePower)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourcePower);
        plan[Field] = sourcePower;
        if (Classify(plan) is not string key || plan["summary"] is not JsonObject summary) return;
        summary["source_power_key"] = key;
        // ≥3 W 但这条路线省不下来：取舍清单要顶到结论正下方并默认展开。
        summary["tradeoff_first"] = key == RouteLimited;
        foreach (string language in new[] { Messages.Chinese, Messages.English })
        {
            string line = Messages.Get(key, language);
            string existing = summary[language]?.GetValue<string>() ?? "";
            summary[language] = existing.StartsWith(line, StringComparison.Ordinal)
                ? existing : line + (language == Messages.Chinese ? "" : " ") + existing;
        }
    }
}
