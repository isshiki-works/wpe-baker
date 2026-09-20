using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 把"烘前实测原作功耗"接进分析结论：采样器报告压成 plan 的 <c>source_power</c> 段，
/// 原作读数只代表本机，不能单独预测生成收益。这里只读采样报告与 plan 已有字段，
/// 不做任何渲染判定，也不改动 plan 现有的英文字段。
/// </summary>
public static class SourcePowerVerdict
{
    /// <summary>plan 里挂实测读数的字段名。</summary>
    public const string Field = "source_power";

    // Legacy report bands, retained for reading old reports only. Source power
    // alone cannot predict savings or classify work on a different device.
    public const double LightWatts = 1, HeavyWatts = 3;

    /// <summary>旧报告的低功耗分档键，仅保留读取兼容。</summary>
    public const string NotWorth = "source_power.not_worth";
    /// <summary>旧报告的中低功耗分档键，仅保留读取兼容。</summary>
    public const string Limited = "source_power.limited";
    /// <summary>旧报告的高功耗整幅路线键，仅保留读取兼容。</summary>
    public const string Worth = "source_power.worth";
    /// <summary>旧报告的部分实时路线键，仅保留读取兼容。</summary>
    public const string RouteLimited = "source_power.route_limited";
    /// <summary>这台机器测不了原作功耗。</summary>
    public const string Unavailable = "source_power.unavailable";
    public const string Observed = "source_power.observed";

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
            ["threshold_watts"] = null,
            ["metric"] = verdict?["metric"]?.DeepClone() ?? "platform_power.igpu_domain_watts.median",
            ["worth_baking"] = null,
            ["text"] = "Source-only power is a local measurement, not a baking-benefit verdict.",
            ["measurement_scope"] = "this_device_only",
            ["igpu_domain_watts"] = power?["igpu_domain_watts"]?.DeepClone(),
            ["package_watts"] = power?["package_watts"]?.DeepClone(),
            ["power_status"] = power?["status"]?.DeepClone(),
            ["display"] = sample["display"]?.DeepClone(),
            ["sampler_status"] = sample["status"]?.DeepClone(),
            ["report_path"] = sample["report_path"]?.DeepClone(),
            // 旧采样结论保留为历史原文，活动判定不再沿用其功耗阈值。
            ["sampler_verdict"] = verdict?.DeepClone(),
            ["verdict"] = new JsonObject { ["status"] = verdict?["status"]?.DeepClone(), ["worth_baking"] = null }
        };
    }

    /// <summary>plan 里是否有有效本机读数；没有采样记录返回 null。</summary>
    public static string? Classify(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan[Field] is not JsonObject power) return null;
        if (power["status"]?.GetValue<string>() != "measured" || Watts(power) is not double) return Unavailable;
        return Observed;
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
    /// 仅描述结构，不据此推断实际功耗。
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
    /// 把本机实测读数写进 plan，并在结论中说明其适用范围（中英各一句）。
    /// 重复调用只前置一次；没测过 / 没有结论行时只写字段。
    /// </summary>
    public static void Apply(JsonObject plan, JsonObject sourcePower)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourcePower);
        plan[Field] = sourcePower;
        if (Classify(plan) is not string key || plan["summary"] is not JsonObject summary) return;
        summary["source_power_key"] = key;
        summary["tradeoff_first"] = false;
        foreach (string language in new[] { Messages.Chinese, Messages.English })
        {
            string line = Messages.Get(key, language);
            string existing = summary[language]?.GetValue<string>() ?? "";
            summary[language] = existing.StartsWith(line, StringComparison.Ordinal)
                ? existing : line + (language == Messages.Chinese ? "" : " ") + existing;
        }
    }
}
