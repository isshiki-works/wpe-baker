using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// feat/retime-budget：档位给观感改动预算、循环长度是求解结果。
/// 覆盖三档映射与来源标记、命令行参数。
/// </summary>
internal static class RetimeBudgetChecks
{
    internal static void Run(Action<bool, string> check)
    {
        PresetChecks(check);
        ArgumentChecks(check);
    }

    private static void PresetChecks(Action<bool, string> check)
    {
        RetimeProfile efficiency = RetimeProfile.Resolve(RetimeProfile.Efficiency, null, null, 2);
        RetimeProfile balanced = RetimeProfile.Resolve(RetimeProfile.Balanced, null, null, 2);
        RetimeProfile quality = RetimeProfile.Resolve(RetimeProfile.Quality, null, null, 2);
        check(efficiency is { BudgetPercent: 5, CommonRetimePercent: 5, LoopMaximumSeconds: 600 } &&
            balanced is { BudgetPercent: 3, CommonRetimePercent: 3, LoopMaximumSeconds: 600 } &&
            quality is { BudgetPercent: null, CommonRetimePercent: 2, LoopMaximumSeconds: 600 } &&
            new[] { efficiency, balanced, quality }.All(profile =>
                profile.BudgetSource == RetimeProfile.FromPreset && profile.LoopMaximumSource == RetimeProfile.FromPreset),
            "retime preset: all presets default to 600 s; their retiming budgets remain independent");
        check(RetimeProfile.Resolve(RetimeProfile.Balanced, 1.5, 900, 2) is
                { BudgetPercent: 1.5, CommonRetimePercent: 1.5, LoopMaximumSeconds: 900, BudgetSource: RetimeProfile.FromOverride,
                  LoopMaximumSource: RetimeProfile.FromOverride } &&
            RetimeProfile.Resolve(RetimeProfile.Quality, 4, null, 2) is { BudgetPercent: 4, CommonRetimePercent: 4, LoopMaximumSeconds: 600 },
            "retime preset: an override beats the preset for both the budget and the length maximum, and is recorded as an override");
        // 没选档（旧 plan、旧接口）：不设预算、600 s 上限、通用预算沿用 MaximumRetimePercent，本次改动之前的行为逐项不变。
        check(RetimeProfile.Resolve(null, null, null, 2) is
                { Preset: null, BudgetPercent: null, CommonRetimePercent: 2, LoopMaximumSeconds: 600,
                  BudgetSource: RetimeProfile.FromDefault, LoopMaximumSource: RetimeProfile.FromDefault } &&
            RetimeProfileJson.Resolve(new HybridAnalyzeRequest(2, "s", "a", "o")).BudgetPercent is null,
            "retime preset: a request without a preset keeps the old behaviour (smallest change, 600 s, --max-retime as the common budget)");
        bool rejected = false;
        try { RetimeProfile.Resolve("ultra", null, null, 2); } catch (InvalidDataException) { rejected = true; }
        check(rejected && !RetimeProfile.IsKnownPreset("ultra") && RetimeProfile.MaximumBudgetPercent == 5,
            "retime preset: an unknown preset name is rejected and the budget tops out at 5%");
        JsonObject record = RetimeProfileJson.ToJson(RetimeProfile.Resolve(RetimeProfile.Efficiency, null, 300, 2));
        check(record["preset"]!.GetValue<string>() == "efficiency" && record["retime_budget_percent"]!.GetValue<double>() == 5 &&
            record["retime_budget_source"]!.GetValue<string>() == "preset" && record["loop_max_seconds"]!.GetValue<double>() == 300 &&
            record["loop_max_seconds_source"]!.GetValue<string>() == "override",
            "retime profile record: the plan carries every effective value with its source");
    }

    private static void ArgumentChecks(Action<bool, string> check)
    {
        // 档位与两个高级覆盖的命令行解析已移到 Baker.Cli/OptionTable（C2.5a）。
        static AnalyzeOptions Read(params string[] options) =>
            Baker.Cli.OptionTable.ReadAnalyze(Baker.Cli.OptionTable.Parse(["analyze", "src", .. options]));
        AnalyzeOptions defaults = Read();
        check(defaults is { Preset: "balanced", RetimeBudgetPercent: null, LoopMaximumSeconds: null },
            "retime arguments: analyze defaults to the balanced preset and no overrides");
        AnalyzeOptions modern = Read("--preset", "quality", "--retime-budget", "4.5", "--loop-max-seconds", "900");
        check(modern is { Preset: "quality", RetimeBudgetPercent: 4.5, LoopMaximumSeconds: 900 },
            "retime arguments: --preset, --retime-budget and --loop-max-seconds are read as given");
        bool tooLarge = false, badPreset = false;
        try { Read("--retime-budget", "6"); }
        catch (ArgumentException) { tooLarge = true; }
        try { Read("--preset", "fast"); }
        catch (ArgumentException) { badPreset = true; }
        check(tooLarge && badPreset,
            "retime arguments: a budget above 5% and an unknown preset are rejected");

        check(RetimeProfile.LoopPreferenceForPreset(RetimeProfile.Efficiency) == "performance" &&
            RetimeProfile.LoopPreferenceForPreset(RetimeProfile.Balanced) == "balanced" &&
            RetimeProfile.LoopPreferenceForPreset(RetimeProfile.Quality) == "quality",
            "loop preference: each preset maps onto the solver's loop preference");
    }
}
