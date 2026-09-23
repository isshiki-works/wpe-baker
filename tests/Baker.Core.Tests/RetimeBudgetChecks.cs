using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// feat/retime-budget：档位给观感改动预算、循环长度是求解结果。
/// 覆盖三档映射与来源标记、旧参数别名、预算内取最短 L、圈数下限、可见项速度偏差第二闸、相位差记录与结论行。
/// 期望值取自 27 案扫描表（reports-20260916\sway-budget-scan.md §1.2、§3）与逐项手算，不是跑出来回填的。
/// </summary>
internal static class RetimeBudgetChecks
{
    private sealed record ProtoLayer(double Width, double Height, double Scale, double Speed, double Strength, double Ratio, double Direction);

    // 与 SwayRetimeChecks 同两案：3750317749（P = 483 帧，4 个可见项）与阿米娅 3486806915（P = 6000 帧，15 个可见项）。
    private static readonly ProtoLayer Flowers = new(3840, 2160, 1.09437, 3.55, 0.2, 0.3, 0.0);
    private static readonly ProtoLayer[] Amiya =
    [
        new(739, 1088, 1, 5.0, 0.52999997, 0.30000001, 1.9986103),
        new(1872, 1760, 1, 2.0, 0.63, 0.30000001, -0.90372425),
        new(944, 647, 1, 5.0, 0.60000002, 0.30000001, -2.9296732),
        new(876, 688, 1, 5.0, 0.49000001, 3.02, -0.48429704),
    ];

    private static SwayModel Model(int owner, ProtoLayer layer, double[]? coefficients = null) =>
        new(owner, 0, 0, "effects/foliagesway", 0, "speeduv", (float)layer.Speed, coefficients ?? SwayModel.CanonicalCoefficients,
            layer.Strength, layer.Ratio, layer.Direction, 1, 1, 0.2, layer.Width, layer.Height, layer.Scale, layer.Scale);

    private static SwayRetimeInput[] Inputs(double outputScale, params ProtoLayer[] layers) => layers.Select((layer, index) =>
    {
        SwayModel model = Model(index + 1, layer);
        var amplitude = model.AmplitudeOutputPixels(outputScale, outputScale);
        return new SwayRetimeInput(model, amplitude?.X, amplitude?.Y);
    }).ToArray();

    /// <summary>单项合成输入：周期 period 秒、振幅 amplitude 输出像素，速度 1 时系数就是角频率。</summary>
    private static SwayRetimeInput Single(double period, double amplitude) =>
        new(Model(1, Flowers, [2 * Math.PI / period, 0, 0, 0, 0, 0, 0, 0]) with { Speed = 1 }, amplitude, amplitude);

    internal static void Run(Action<bool, string> check)
    {
        PresetChecks(check);
        ArgumentChecks(check);
        BudgetSolveChecks(check);
        FloorChecks(check);
        SecondGateChecks(check);
        PhaseDriftChecks(check);
        QualityCeilingChecks(check);
    }

    private static void QualityCeilingChecks(Action<bool, string> check)
    {
        // 起因：上限同时决定通用求解器生成哪些候选 P，摆动只能在选中候选的整数倍上闭合。阿米娅 600 s 上限下 0.53%、
        // 1175 s 上限下反而 0.91%，质量档"改动最小"被上限口径破坏。所以质量档两个上限各求一次，取可见改动更小者。
        // 判定的入参是生效上限（已按内嵌视频 2 GiB 收紧），不是档位名义上限。
        static bool Compares(string? preset, double? ceilingOverride, double effectiveSeconds) =>
            RetimeProfile.Resolve(preset, null, ceilingOverride, 2).ComparesQualityCeilings(effectiveSeconds);
        check(RetimeProfile.QualityComparisonSeconds == 600 &&
            Compares(RetimeProfile.Quality, null, 1200) &&
            !Compares(RetimeProfile.Quality, 600, 600) &&
            Compares(RetimeProfile.Quality, 1800, 1800) &&
            !Compares(RetimeProfile.Balanced, null, 600) &&
            !Compares(RetimeProfile.Efficiency, null, 600) &&
            !Compares(null, null, 600),
            "quality ceiling: only the quality preset compares two ceilings, and only when its own ceiling is longer than 600 s");

        // 2 GiB 收紧后的生效上限才算数：3840×2160@60 不透明组只剩 558 s，两个上限是同一次求解，不再白跑第二次；
        // 1080p 含透明的阿米娅生效上限 1175 s，仍然要比。
        double fourK = EmbeddedVideoBudget.LoopLengthLimit(1200, 3840, 2160, packedAlpha: false, 60, 1)!.EffectiveSeconds;
        double amiya = EmbeddedVideoBudget.LoopLengthLimit(1200, 1920, 1080, packedAlpha: true, 60, 1)!.EffectiveSeconds;
        check(fourK == 558 && !Compares(RetimeProfile.Quality, null, fourK) &&
            amiya > RetimeProfile.QualityComparisonSeconds && Compares(RetimeProfile.Quality, null, amiya),
            "quality ceiling: the effective ceiling after the 2 GiB squeeze decides, so 4K stops comparing and 1080p still does");
        // 阿米娅的实测读数：档位上限那次改动更大，取 600 s 那次。
        var atPreset = new RetimeProfile.QualityCeilingReading(1175, 0.905, 42000);
        var atComparison = new RetimeProfile.QualityCeilingReading(600, 0.531, 36000);
        check(RetimeProfile.ChooseQualityCeiling(atPreset, atComparison) is
                { UsePresetCeiling: false, Reason: RetimeProfile.QualityCeilingChoice.SmallerVisibleChange } &&
            RetimeProfile.ChooseQualityCeiling(atComparison, atPreset) is
                { UsePresetCeiling: true, Reason: RetimeProfile.QualityCeilingChoice.SmallerVisibleChange },
            "quality ceiling: the run with the smaller visible sway change wins, whichever ceiling produced it");
        // 改动一样时取更短的循环：同样的观感不必多烘一倍的帧。
        check(RetimeProfile.ChooseQualityCeiling(new(1175, 0.44, 25599), new(600, 0.44, 25599)) is
                { UsePresetCeiling: false, Reason: RetimeProfile.QualityCeilingChoice.ShorterLoop } &&
            RetimeProfile.ChooseQualityCeiling(new(1175, 0.44, 20000), new(600, 0.44, 25599)) is
                { UsePresetCeiling: true, Reason: RetimeProfile.QualityCeilingChoice.ShorterLoop },
            "quality ceiling: an equal visible change falls back to the shorter loop");
        // 只有一侧解出摆动就取那一侧；两侧都没有摆动解时按档位上限走，不因为这条规则改变原来的结果。
        check(RetimeProfile.ChooseQualityCeiling(new(1175, null, 36000), new(600, 0.53, 36000)) is
                { UsePresetCeiling: false, Reason: RetimeProfile.QualityCeilingChoice.OnlySolution } &&
            RetimeProfile.ChooseQualityCeiling(new(1175, 0.53, 36000), new(600, null, null)) is
                { UsePresetCeiling: true, Reason: RetimeProfile.QualityCeilingChoice.OnlySolution } &&
            RetimeProfile.ChooseQualityCeiling(new(1175, null, 36000), new(600, null, 21000)) is
                { UsePresetCeiling: true, Reason: RetimeProfile.QualityCeilingChoice.NoSwaySolution },
            "quality ceiling: a run without a sway solution never beats one with it, and two such runs keep the preset ceiling");
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
        JsonObject record = RetimeProfileJson.ToJson(RetimeProfile.Resolve(RetimeProfile.Efficiency, null, 300, 2), 1);
        check(record["preset"]!.GetValue<string>() == "efficiency" && record["retime_budget_percent"]!.GetValue<double>() == 5 &&
            record["retime_budget_source"]!.GetValue<string>() == "preset" && record["loop_max_seconds"]!.GetValue<double>() == 300 &&
            record["loop_max_seconds_source"]!.GetValue<string>() == "override" &&
            record["minimum_visible_cycles"]!.GetValue<long>() == 3 && record["minimum_loop_seconds"]!.GetValue<double>() == 60 &&
            record["visible_speed_deviation_limit_pixels_per_second"]!.GetValue<double>() == 0.2,
            "retime profile record: the plan carries every effective value with its source, plus the three hard floors");
    }

    private static void ArgumentChecks(Action<bool, string> check)
    {
        // 档位、两个高级覆盖与摆动改频开关的命令行解析已移到 Baker.Cli/OptionTable（C2.5a）。
        static AnalyzeOptions Read(params string[] options) =>
            Baker.Cli.OptionTable.ReadAnalyze(Baker.Cli.OptionTable.Parse(["analyze", "src", .. options]));
        AnalyzeOptions defaults = Read();
        check(defaults is { Preset: "balanced", RetimeBudgetPercent: null, LoopMaximumSeconds: null, SwayRetime: true },
            "retime arguments: analyze defaults to the balanced preset, no overrides and sway retime on");
        // 摆动改频默认开（设计 §3：三档都开），只有显式 off 才关；默认值与界面共用 SwayRetimeOptions.OnByDefault。
        check(SwayRetimeOptions.OnByDefault && Read("--sway-retime", "on").SwayRetime && !Read("--sway-retime", "off").SwayRetime,
            "retime arguments: sway retime is on by default and only an explicit --sway-retime off turns it off");
        bool badSway = false;
        try { Read("--sway-retime", "yes"); }
        catch (ArgumentException) { badSway = true; }
        check(badSway, "retime arguments: --sway-retime only accepts on or off");
        check(Baker.Cli.OptionTable.Usage("analyze", "zh").Split(Environment.NewLine)
                .Any(line => line.Contains("--sway-retime", StringComparison.Ordinal)),
            "retime arguments: analyze --help states that sway retime defaults to on");
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

    private static void BudgetSolveChecks(Action<bool, string> check)
    {
        // 扫描表 §3.2：阿米娅 P = 6000 帧（100 s）。v2（改动最小）取 600 s / 0.53%；3% 与 5% 撞同一个解 300 s / 2.81%。
        SwayRetimeInput[] amiya = Inputs(0.88889, Amiya);
        SwayRetimeSolution minimized = SwayRecurrenceSolver.Solve(amiya, 6000, 60, 1, 600)!;
        SwayRetimeSolution budget3 = SwayRecurrenceSolver.Solve(amiya, 6000, 60, 1, 600, 3)!;
        SwayRetimeSolution budget5 = SwayRecurrenceSolver.Solve(amiya, 6000, 60, 1, 600, 5)!;
        check(minimized.Frames == 36000 && Math.Abs(minimized.MaximumVisibleChangePercent - 0.53) < 0.005 &&
            budget3.Frames == 18000 && Math.Abs(budget3.Seconds - 300) < 1e-9 &&
            Math.Abs(budget3.MaximumVisibleChangePercent - 2.81) < 0.005 && budget3.MaximumVisibleChangePercent <= 3 &&
            budget5.Frames == budget3.Frames,
            "retime budget: Amiya solves to 600 s / 0.53% with no budget, 300 s / 2.81% at 3%, and 5% buys nothing more (scan §3.2)");
        // 扫描表 §3.1：3750317749 P = 483 帧。3% 取 128.8 s（k = 16）；5% 的 88.5 s 解（k = 11）只让最慢可见项走 2 圈，
        // 圈数下限把它挡回 3% 那个解——这是"圈数下限不是旋钮"的真实案例。
        SwayRetimeInput[] flowers = Inputs(0.5, Flowers);
        SwayRetimeSolution flowers3 = SwayRecurrenceSolver.Solve(flowers, 483, 60, 1, 600, 3)!;
        SwayRetimeSolution flowers5 = SwayRecurrenceSolver.Solve(flowers, 483, 60, 1, 600, 5)!;
        SwayRetimeSolution flowers5NoFloor = SwayRecurrenceSolver.Solve(flowers, 483, 60, 1, 600, 5, 0, 1)!;
        check(flowers3.Frames == 7728 && Math.Abs(flowers3.Seconds - 128.8) < 0.05 && flowers3.SlowestVisibleCycles == 3 &&
            flowers5.Frames == flowers3.Frames && flowers5NoFloor.Frames == 5313 && flowers5NoFloor.SlowestVisibleCycles == 2,
            "retime budget: White flowers takes 128.8 s at 3%; the shorter 88.5 s solution the 5% budget allows leaves the slowest visible term at 2 cycles and is refused");
        // 预算越松 L 只会更短或相等，而且实际改动始终在预算内；质量档（无预算）是最长、改动最小的那一端。
        check(budget3.Seconds <= minimized.Seconds && flowers3.Seconds <= SwayRecurrenceSolver.Solve(flowers, 483, 60, 1, 600)!.Seconds &&
            flowers3.MaximumVisibleChangePercent <= 3 && flowers5.MaximumVisibleChangePercent <= 5,
            "retime budget: a budget never lengthens the loop and never exceeds itself");
        // 预算太紧时不回退成"改动最小"，而是没有解，由分析层走未解析路径。
        check(SwayRecurrenceSolver.Solve(amiya, 6000, 60, 1, 600, 0.1) is null && SwayRecurrenceSolver.Solve(amiya, 6000, 60, 1, 600) is not null,
            "retime budget: a budget nothing meets gives no solution instead of silently falling back to the smallest-change answer");
    }

    private static void FloorChecks(Action<bool, string> check)
    {
        // 圈数下限：单个 25 s 的可见项，L ≥ 60 s 且要走满 3 圈就得 L ≥ 2.5 × 25 = 62.5 s；62 s 上限内无解，63 s 上限起有解。
        check(SwayRecurrenceSolver.Solve([Single(25, 1)], 60, 60, 1, 62) is null &&
            SwayRecurrenceSolver.Solve([Single(25, 1)], 60, 60, 1, 63) is { Seconds: 63, SlowestVisibleCycles: 3 },
            "retime floor: the slowest visible term must complete three cycles, so a 25 s term needs a loop past 62.5 s");
        // 秒数下限：5 s 的可见项圈数远够，仍要 L ≥ 60 s。
        check(SwayRecurrenceSolver.Solve([Single(5, 1)], 60, 60, 1, 59) is null &&
            SwayRecurrenceSolver.Solve([Single(5, 1)], 60, 60, 1, 60) is { Seconds: 60 } short60 && short60.SlowestVisibleCycles == 12,
            "retime floor: a loop shorter than 60 s is refused even when every visible term runs plenty of cycles");
        check(SwayRecurrenceSolver.MinimumVisibleCycles == 3 && SwayRecurrenceSolver.MinimumLoopSeconds == 60 &&
            SwayRecurrenceSolver.Solve([Single(25, 1)], 60, 60, 1, 62, null, 0, 1) is { Seconds: 25 },
            "retime floor: the floors are the same for every preset and only a test may relax them");
    }

    private static void SecondGateChecks(Action<bool, string> check)
    {
        // 第二道闸盯的是可见项改频后的峰值速度偏差 a·|2πn/L − ω|，不是百分比。
        // 50 s 的可见项在 L = 160 s 上走 x = 3.2 圈，取 n = 3，δ = −6.25%，偏差 = a·(2π/50)·0.0625 = a·0.007854。
        SwayRetimeSolution loose = SwayRecurrenceSolver.Detail([Single(50, 20)], 60, 160, 60, 1);
        SwayRetimeSolution tight = SwayRecurrenceSolver.Detail([Single(50, 30)], 60, 160, 60, 1);
        double looseDeviation = loose.MaximumVisibleSpeedDeviationPixelsPerSecond!.Value;
        double tightDeviation = tight.MaximumVisibleSpeedDeviationPixelsPerSecond!.Value;
        check(Math.Abs(looseDeviation - 20 * 0.0078539816) < 1e-6 && Math.Abs(tightDeviation - 30 * 0.0078539816) < 1e-6 &&
            looseDeviation is > 0.1 and <= 0.2 && tightDeviation > 0.2 &&
            // 只容 k = 1（P = L = 160 s）时，求解器收下 0.157 px/s 的那版、拒掉 0.236 px/s 的那版：闸门只在 Choose 里判。
            SwayRecurrenceSolver.Solve([Single(50, 20)], 9600, 60, 1, 160) is { Frames: 9600 } &&
            SwayRecurrenceSolver.Solve([Single(50, 30)], 9600, 60, 1, 160) is null,
            "retime second gate: a visible term may deviate past the slow-term 0.1 px/s line but not past 0.2 px/s");
        // 求解器跳过被第二道闸挡下的 L：振幅 30 px 那一版在 L = 126 s 上改 19%、偏差 0.72 px/s，预算 5% 与第二道闸都不收，
        // 于是继续往长里走，直到偏差回到 0.2 px/s 以内。
        SwayRetimeSolution picked = SwayRecurrenceSolver.Solve([Single(50, 30)], 60, 60, 1, 200, 5)!;
        SwayRetimeSolution skipped = SwayRecurrenceSolver.Detail([Single(50, 30)], 60, 126, 60, 1);
        check(SwayRecurrenceSolver.Solve([Single(50, 30)], 7560, 60, 1, 126) is null &&
            skipped.MaximumVisibleSpeedDeviationPixelsPerSecond > 0.2 && picked.Seconds > skipped.Seconds &&
            picked.MaximumVisibleSpeedDeviationPixelsPerSecond <= 0.2 && picked.MaximumVisibleChangePercent <= 5 &&
            picked.SlowestVisibleCycles >= SwayRecurrenceSolver.MinimumVisibleCycles,
            "retime second gate: the solver walks on to the next L instead of accepting one that breaks the visible speed gate");
    }

    private static void PhaseDriftChecks(Action<bool, string> check)
    {
        // 相位差 = |n − x| 圈：50 s 的项在 160 s 上 x = 3.2，取 3 圈，偏 0.2 圈。
        SwayRetimeSolution drift = SwayRecurrenceSolver.Detail([Single(50, 1)], 60, 160, 60, 1);
        check(Math.Abs(drift.MaximumPhaseDriftCycles - 0.2) < 1e-9,
            "retime phase drift: one loop leaves |n − x| cycles of phase behind (3.2 cycles rounded to 3 is 0.2)");
        // 每项取 n = round(x)，所以任何解的相位差恒 < 0.5 圈：放大预算只让相位偏得更快，不让它偏得更多。
        SwayRetimeInput[] amiya = Inputs(0.88889, Amiya);
        SwayRetimeSolution minimized = SwayRecurrenceSolver.Solve(amiya, 6000, 60, 1, 600)!;
        SwayRetimeSolution budget3 = SwayRecurrenceSolver.Solve(amiya, 6000, 60, 1, 600, 3)!;
        double Recomputed(SwayRetimeSolution solution) => solution.Layers.SelectMany(layer => layer.Terms)
            .Where(term => !term.Frozen && term.CoefficientOld != 0)
            .Select(term => Math.Abs(term.Cycles - term.CyclesExact)).DefaultIfEmpty(0).Max();
        check(minimized.MaximumPhaseDriftCycles < 0.5 && budget3.MaximumPhaseDriftCycles < 0.5 &&
            Math.Abs(minimized.MaximumPhaseDriftCycles - Recomputed(minimized)) < 1e-12 &&
            Math.Abs(budget3.MaximumPhaseDriftCycles - Recomputed(budget3)) < 1e-12,
            "retime phase drift: it equals the per-term recomputation and always stays under half a cycle");
        // plan 记录与结论行：报相位差、最慢可见项圈数与预算，百分比只是其中一项。
        JsonObject record = SwayRetimeJson.ToJson(budget3, 600, 60, 1, RetimeProfile.Resolve(RetimeProfile.Balanced, null, null, 2));
        var (driftText, cyclesText, _, deviationText, frozen) = SwayRetimeJson.SummaryNumbers(record);
        check(record["preset"]!.GetValue<string>() == "balanced" && record["retime_budget_percent"]!.GetValue<double>() == 3 &&
            record["retime_budget_source"]!.GetValue<string>() == "preset" &&
            Math.Abs(record["phase_drift_cycles"]!.GetValue<double>() - budget3.MaximumPhaseDriftCycles) < 1e-12 &&
            record["slowest_visible_cycles"]!.GetValue<long>() == budget3.SlowestVisibleCycles &&
            record["max_visible_speed_deviation_pixels_per_second"]!.GetValue<double>() == budget3.MaximumVisibleSpeedDeviationPixelsPerSecond &&
            driftText == budget3.MaximumPhaseDriftCycles.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) &&
            cyclesText == budget3.SlowestVisibleCycles!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            deviationText is not null && frozen == budget3.FrozenCount,
            "retime record: the plan carries the preset, the budget with its source, the phase drift and the slowest visible cycle count");
        JsonObject minimizedRecord = SwayRetimeJson.ToJson(minimized, 600, 60, 1, RetimeProfile.Resolve(RetimeProfile.Quality, null, null, 2));
        check(minimizedRecord["retime_budget_percent"] is null,
            "retime conclusion line: the quality preset reports that it solved for the smallest change instead of a budget");
    }
}
