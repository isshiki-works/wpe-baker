using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 原作功耗只记录本机事实、结论第一行的前置、静态产物的状态，
/// 以及烘完原作 vs 成品的 A/B 比较。
/// </summary>
internal static class SourcePowerVerdictChecks
{
    private static JsonObject Sample(double? watts) => new()
    {
        ["status"] = "sampled",
        ["report_path"] = @"D:\sample\report.json",
        ["platform_power"] = new JsonObject
        {
            ["status"] = watts is null ? "no_graphics_domain" : "sampled",
            ["igpu_domain_watts"] = watts is null ? null : new JsonObject { ["median"] = watts }
        },
        ["verdict"] = new JsonObject
        {
            ["status"] = watts is null ? "unsupported_platform" : "measured",
            ["measured_watts"] = watts, ["threshold_watts"] = 1, ["worth_baking"] = watts >= 1
        }
    };

    private static JsonObject Plan(string route = "whole_layer", string layout = "full_frame", JsonArray? retain = null) => new()
    {
        ["route"] = route,
        ["settings"] = new JsonObject { ["video_layout"] = layout, ["retain_live_root_ids"] = retain },
        ["summary"] = new JsonObject { ["key"] = "summary.bakeable", ["zh"] = "可以烘：找到 5.0 秒循环。", ["en"] = "Bakeable: 5.0 s loop found." }
    };

    private static string? Classify(double? watts, string route = "whole_layer", string layout = "full_frame", JsonArray? retain = null)
    {
        JsonObject plan = Plan(route, layout, retain);
        SourcePowerVerdict.Apply(plan, SourcePowerVerdict.FromSample(Sample(watts)));
        return SourcePowerVerdict.Classify(plan);
    }

    internal static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        // Source-only watts, including the old boundaries, never predict generation benefit.
        check(new[] { 0.55, 0.999, 1, 2.53, 3, 8.1 }.All(watts => Classify(watts) == SourcePowerVerdict.Observed),
            "source power: all valid local readings remain observations, without a 1 W or 3 W benefit boundary");
        check(Classify(8.1, route: "effect_prefix") == SourcePowerVerdict.Observed &&
            Classify(8.1, layout: "layered") == SourcePowerVerdict.Observed &&
            Classify(8.1, retain: [26]) == SourcePowerVerdict.Observed,
            "source power: route names do not turn source-only readings into predicted savings");
        check(Classify(null) == SourcePowerVerdict.Unavailable, "source power: a machine without the graphics domain gives no recommendation");
        check(SourcePowerVerdict.Classify(Plan()) is null, "source power: a wallpaper that was never measured gets no verdict line");

        // ---- ② 结论第一行：判定顶到最前，重复调用不重复前置 ----
        JsonObject plan = Plan();
        JsonObject power = SourcePowerVerdict.FromSample(Sample(0.55));
        SourcePowerVerdict.Apply(plan, power);
        string first = plan["summary"]!["zh"]!.GetValue<string>();
        SourcePowerVerdict.Apply(plan, power);
        check(first.StartsWith(MessageCatalog.Get(SourcePowerVerdict.Observed, MessageCatalog.Chinese), StringComparison.Ordinal) &&
            first.EndsWith("可以烘：找到 5.0 秒循环。", StringComparison.Ordinal) &&
            plan["summary"]!["zh"]!.GetValue<string>() == first &&
            plan["summary"]!["source_power_key"]!.GetValue<string>() == SourcePowerVerdict.Observed,
            "source power: the verdict leads the summary line and is prefixed only once");
        JsonObject routeLimited = Plan(route: "effect_prefix");
        SourcePowerVerdict.Apply(routeLimited, SourcePowerVerdict.FromSample(Sample(8.1)));
        check(!routeLimited["summary"]!["tradeoff_first"]!.GetValue<bool>() &&
            !plan["summary"]!["tradeoff_first"]!.GetValue<bool>(),
            "source power: local watts do not reorder the tradeoff card");
        check(power["worth_baking"] is null && power["threshold_watts"] is null &&
            power["verdict"]!["worth_baking"] is null &&
            power["sampler_verdict"]!["worth_baking"]!.GetValue<bool>() == false &&
            power["measurement_scope"]!.GetValue<string>() == "this_device_only",
            "source power: old sampler verdict is retained as history, not reused as an active benefit verdict");
        check(plan[SourcePowerVerdict.Field]!["measured_watts"]!.GetValue<double>() == 0.55 &&
            plan[SourcePowerVerdict.Field]!["report_path"]!.GetValue<string>() == @"D:\sample\report.json" &&
            SourcePowerVerdict.Skipped("no Wallpaper Engine")["status"]!.GetValue<string>() == "skipped",
            "source power: the reading and its report path are kept in the plan, and a skipped measurement says why");

        // ---- ③ 单帧静态输出也是完成的产物，不能据此判断有无收益 ----
        JsonObject staticBake = new() { ["frames"] = 1, ["video_layers"] = 0, ["static_layers"] = 1 };
        JsonObject videoBake = new() { ["frames"] = 2256, ["video_layers"] = 1, ["static_layers"] = 0 };
        JsonObject mixedBake = new() { ["frames"] = 1, ["video_layers"] = 1, ["static_layers"] = 1 };
        check(StaticOnlyBake.Is(staticBake) && !StaticOnlyBake.Is(videoBake) && !StaticOnlyBake.Is(mixedBake) &&
            !StaticOnlyBake.Is(new JsonObject { ["frames"] = 1 }),
            "static only: one frame with no video layer is the only static result");
        check(StaticOnlyBake.Finished(StaticOnlyBake.Status) && StaticOnlyBake.Finished("candidate_generated") &&
            !StaticOnlyBake.Finished("candidate_rejected_seam") && StaticOnlyBake.Status == "static_only",
            "static only: a static result still counts as finished, rejections do not");

        // ---- ④ 烘完的 A/B：降幅 ≥30% 才算省电 ----
        JsonObject saved = SourcePowerVerdict.Gain(SourcePowerVerdict.FromSample(Sample(8.1)), SourcePowerVerdict.FromSample(Sample(3.478)));
        JsonObject same = SourcePowerVerdict.Gain(SourcePowerVerdict.FromSample(Sample(10.855)), SourcePowerVerdict.FromSample(Sample(10.97)));
        JsonObject worse = SourcePowerVerdict.Gain(SourcePowerVerdict.FromSample(Sample(8.768)), SourcePowerVerdict.FromSample(Sample(11.788)));
        JsonObject unmeasured = SourcePowerVerdict.Gain(SourcePowerVerdict.FromSample(Sample(null)), SourcePowerVerdict.FromSample(Sample(3.478)));
        check(saved["key"]!.GetValue<string>() == "measured_gain.saved" &&
            Math.Abs(saved["saved_percent"]!.GetValue<double>() - 57.06) < 0.1 &&
            same["key"]!.GetValue<string>() == "measured_gain.same" &&
            worse["key"]!.GetValue<string>() == "measured_gain.worse" &&
            unmeasured["key"]!.GetValue<string>() == "measured_gain.unavailable" &&
            unmeasured["status"]!.GetValue<string>() == "not_measured",
            "measured gain: the three verdicts follow the measured rule, and an unmeasurable machine gives none");
        check(SourcePowerVerdict.GainLine(saved, MessageCatalog.Chinese).Contains("57", StringComparison.Ordinal) &&
            SourcePowerVerdict.GainLine(unmeasured, MessageCatalog.Chinese) == MessageCatalog.Get("measured_gain.unavailable", MessageCatalog.Chinese),
            "measured gain: the spoken line carries the percentage, or says this machine cannot measure it");

        // ---- ⑤ 采样目录必须在分析输出目录之外（否则分析会以 "Analysis output must be new." 直接失败）----
        string analysis = Path.Combine(Path.GetTempPath(), "WpeBaker", "analysis-check");
        string sampled = SourcePowerVerdict.SampleDirectory(analysis);
        check(!sampled.StartsWith(analysis + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            sampled != analysis && Path.GetDirectoryName(sampled) == Path.GetDirectoryName(analysis) &&
            SourcePowerVerdict.SampleDirectory(analysis + Path.DirectorySeparatorChar) == sampled,
            "source power: the sample directory sits beside the analysis output, never inside it");
    }
}
