using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 三档预设：档位给的是"允许多大的观感改动"，循环长度是求解结果（用户 2026-09-18 03:15 定，样片见 sway-budget-scan.md §3）。
/// 一个百分比同时当摆动可见项预算与通用分量调速预算；长度上限只是兜底，真正压短 L 的是预算。
/// 预设值来自 27 案五档扫描（同报告 §1.2、§4）：
/// - 效率 5%：相对 3% 中位再缩 1.25 倍，单槽 9.7 → 7.4 h；只有 13 案真能吃到 4% 以上。
/// - 平衡 3%：第一条能对全部 27 案兑现的线（最难的 3516174947 在 600 s 内的最小可见改动就是 2.71%）。
/// - 质量：不设百分比门槛，求解器按"可见项改动最小"取解（规则 v2 的原目标）。
/// - 长度上限 600 / 600 / 1200 s：原设计的 60 s、300 s 作废——L 只能取 base 周期的整数倍，19 案的 P 本身就 50–180 s，
///   60 s 上限下 27 案只有 1 案有解。
/// 圈数下限（最慢可见项 ≥ 3 圈且 L ≥ 60 s）与速度偏差两道闸不是档位旋钮，写在 <see cref="SwayRecurrenceSolver"/> 里，三档相同。
/// </summary>
/// <param name="Preset">档位名；null 表示调用方没选档（旧 plan、旧接口），按"不设预算 + 600 s"的旧行为走。</param>
/// <param name="BudgetPercent">观感改动预算（百分比）；null = 不设门槛，取改动最小的解。</param>
/// <param name="CommonRetimePercent">通用分量调速预算：与观感预算同一个数；没有预算时回落到请求里的 MaximumRetimePercent。</param>
public sealed record RetimeProfile(string? Preset, double? BudgetPercent, double CommonRetimePercent,
    double LoopMaximumSeconds, string BudgetSource, string LoopMaximumSource)
{
    public const string Efficiency = "efficiency";
    public const string Balanced = "balanced";
    public const string Quality = "quality";

    /// <summary>值的来源：档位给的、用户覆盖的，或调用方没选档时的旧默认。</summary>
    public const string FromPreset = "preset";
    public const string FromOverride = "override";
    public const string FromDefault = "default";

    /// <summary>--retime-budget 允许的最大值（百分比）：效率档 5% 是扫描过的最高档，再往上没有数据支持。</summary>
    public const double MaximumBudgetPercent = 5;

    /// <summary>没选档时的循环长度上限（秒），与 --loop-length-max 的旧默认一致。</summary>
    public const double DefaultLoopMaximumSeconds = SwayRetimeOptions.DefaultLoopLengthMaximumSeconds;

    /// <summary>
    /// 质量档要额外试一次的循环长度上限（秒）。
    /// 起因（2026-09-18 27 案实测）：上限不只是摆动求解器的 Lmax，它同时决定通用求解器生成哪些候选 P，
    /// 而摆动只能在选中候选的整数倍上闭合。阿米娅在 600 s 上限下有 4 个候选（P = 300 s）、可见改动 0.53%，
    /// 放到 1200 s 上限后只剩 1 个候选（P = 700 s）、可见改动反而涨到 0.91%——质量档"改动最小"的目标被上限口径破坏了。
    /// 所以质量档在这个上限与档位上限下各求一次，取可见改动更小的那次。
    /// </summary>
    public const double QualityComparisonSeconds = 600;

    /// <summary>
    /// 这一档要不要在两个上限下各求一次：只有质量档，且这一案的<b>生效</b>上限
    /// （<see cref="LoopMaximumSeconds"/> 再按内嵌视频 2 GiB 收紧后的值）确实比 600 s 长时才值得。
    /// 原先用的是档位名义上限（质量档 1200 s），而 3840×2160@60 不透明组的生效上限只有 558 s，
    /// 两次求解的上限都是 558，逐位相同：白跑一次完整求解，还会走 ShorterLoop 分支写出
    /// source=quality_comparison 的误导记录。生效上限相等时调用方不跑第二次。
    /// </summary>
    public bool ComparesQualityCeilings(double effectiveMaximumSeconds) =>
        Preset == Quality && effectiveMaximumSeconds > QualityComparisonSeconds + 1e-9;

    public static bool IsKnownPreset(string value) => value is Efficiency or Balanced or Quality;

    /// <summary>
    /// 旧开关 --loop-preference 的取值与档位一一对应（README.zh-CN 早已把它写成别名，只是实现漏了）：
    /// performance = 效率、balanced = 平衡、quality = 质量。
    /// </summary>
    public static string PresetForLoopPreference(string preference) => preference switch
    {
        "performance" => Efficiency,
        "balanced" => Balanced,
        "quality" => Quality,
        _ => throw new ArgumentException("--loop-preference must be performance, balanced, or quality.")
    };

    /// <summary>档位对应的循环取向（求解器认的还是这三个名字）。</summary>
    public static string LoopPreferenceForPreset(string preset) => preset switch
    {
        Efficiency => "performance",
        Balanced => "balanced",
        Quality => "quality",
        _ => throw new InvalidDataException("A preset must be efficiency, balanced, or quality.")
    };

    /// <summary>档位的观感改动预算（百分比）；质量档为 null（不设门槛，取改动最小）。</summary>
    public static double? PresetBudgetPercent(string preset) => preset switch
    {
        Efficiency => 5,
        Balanced => 3,
        Quality => null,
        _ => throw new InvalidDataException("A preset must be efficiency, balanced, or quality.")
    };

    /// <summary>档位的循环长度上限（秒，兜底值）；实际上限还要按内嵌视频 2 GiB 收紧。</summary>
    public static double PresetLoopMaximumSeconds(string preset) => preset switch
    {
        Efficiency => 600,
        Balanced => 600,
        Quality => 1200,
        _ => throw new InvalidDataException("A preset must be efficiency, balanced, or quality.")
    };

    /// <summary>
    /// 把档位与高级覆盖合成一份生效值。覆盖优先于档位；没选档时预算为 null、上限 600 s、通用预算用 <paramref name="commonFallbackPercent"/>，
    /// 与本次改动之前的行为逐项相同。
    /// </summary>
    public static RetimeProfile Resolve(string? preset, double? budgetOverride, double? loopMaximumOverride, double commonFallbackPercent)
    {
        if (preset is not null && !IsKnownPreset(preset)) throw new InvalidDataException("A preset must be efficiency, balanced, or quality.");
        double? budget = budgetOverride ?? (preset is null ? null : PresetBudgetPercent(preset));
        double maximum = loopMaximumOverride ?? (preset is null ? DefaultLoopMaximumSeconds : PresetLoopMaximumSeconds(preset));
        string budgetSource = budgetOverride is not null ? FromOverride : preset is null ? FromDefault : FromPreset;
        string maximumSource = loopMaximumOverride is not null ? FromOverride : preset is null ? FromDefault : FromPreset;
        return new(preset, budget, budget ?? commonFallbackPercent, maximum, budgetSource, maximumSource);
    }

    /// <summary>按分析请求解析（CLI、界面与 bake 前重分析共用这一条路径，三处结果一致）。</summary>
    public static RetimeProfile Resolve(HybridAnalyzeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(request.Preset, request.RetimeBudgetPercent, request.LoopLengthMaximumSeconds, request.MaximumRetimePercent);
    }

    /// <summary>
    /// 命令行上与档位有关的值：档位本身、两个高级覆盖（没给时为 null），以及摆动改频开关
    /// （默认 <see cref="SwayRetimeOptions.OnByDefault"/>，档位的观感预算管的就是它）。
    /// </summary>
    public sealed record Arguments(string Preset, double? BudgetPercent, double? LoopMaximumSeconds, bool SwayRetime);

    /// <summary>一次求解在某个上限下的读数：生效上限秒数、选中候选的可见摆动改动（没有摆动解时为 null）与帧数。</summary>
    public readonly record struct QualityCeilingReading(double CeilingSeconds, double? VisibleChangePercent, ulong? Frames);

    /// <summary>选中的那次求解，以及为什么选它。</summary>
    public readonly record struct QualityCeilingChoice(bool UsePresetCeiling, string Reason)
    {
        /// <summary>可见改动更小。</summary>
        public const string SmallerVisibleChange = "smaller_visible_change";
        /// <summary>可见改动相同，取更短的循环。</summary>
        public const string ShorterLoop = "shorter_loop";
        /// <summary>只有这一侧解出了摆动改频。</summary>
        public const string OnlySolution = "only_solution";
        /// <summary>两侧都没有摆动解，可见改动无从比较，按档位上限走。</summary>
        public const string NoSwaySolution = "no_sway_solution";
    }

    /// <summary>
    /// 质量档两个上限之间的取舍：可见改动更小者胜；一样小就取更短的循环（同样的观感下不必多烘一倍的帧）；
    /// 只有一侧解出摆动就取那一侧；两侧都没有摆动解时按档位上限走，不因为这条规则改变原本的结果。
    /// </summary>
    public static QualityCeilingChoice ChooseQualityCeiling(QualityCeilingReading atPreset, QualityCeilingReading atComparison)
    {
        if (atPreset.VisibleChangePercent is not double preset)
            return new(atComparison.VisibleChangePercent is null, atComparison.VisibleChangePercent is null
                ? QualityCeilingChoice.NoSwaySolution : QualityCeilingChoice.OnlySolution);
        if (atComparison.VisibleChangePercent is not double comparison) return new(true, QualityCeilingChoice.OnlySolution);
        if (Math.Abs(preset - comparison) > 1e-9)
            return new(preset < comparison, QualityCeilingChoice.SmallerVisibleChange);
        ulong presetFrames = atPreset.Frames ?? ulong.MaxValue, comparisonFrames = atComparison.Frames ?? ulong.MaxValue;
        return new(presetFrames < comparisonFrames, QualityCeilingChoice.ShorterLoop);
    }

    /// <summary>
    /// 从已解析的命令行选项里读档位、两个高级覆盖与摆动改频开关。旧名 --max-retime、--loop-length-max 保留为别名，
    /// 用到时通过 <paramref name="deprecated"/> 提示改名；新旧名同时给视为写错命令，直接报错。
    /// </summary>
    public static Arguments ReadArguments(IReadOnlyDictionary<string, string> options, Action<string>? deprecated = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string preset = options.TryGetValue("--preset", out string? name) ? name : Balanced;
        if (!IsKnownPreset(preset)) throw new ArgumentException("--preset must be efficiency, balanced, or quality.");
        // --loop-preference 是 --preset 的旧名（README 早已这么写）：单独给时按对应档位走，
        // 与 --preset 同时给且指的不是同一档就是写错了命令，直接报错，不再解出两套循环。
        if (options.TryGetValue("--loop-preference", out string? preference))
        {
            string aliased = PresetForLoopPreference(preference);
            if (options.ContainsKey("--preset") && aliased != preset)
                throw new ArgumentException($"--loop-preference {preference} is the old name for --preset {aliased}; " +
                    $"it cannot be combined with --preset {preset}. Give only --preset.");
            deprecated?.Invoke($"--loop-preference is now an alias for --preset ({preference} = {aliased}); the old name still works.");
            preset = aliased;
        }
        if (options.ContainsKey("--retime-budget") && options.ContainsKey("--max-retime"))
            throw new ArgumentException("Give either --retime-budget or its old name --max-retime, not both.");
        if (options.ContainsKey("--loop-max-seconds") && options.ContainsKey("--loop-length-max"))
            throw new ArgumentException("Give either --loop-max-seconds or its old name --loop-length-max, not both.");
        if (options.ContainsKey("--max-retime"))
            deprecated?.Invoke("--max-retime is now --retime-budget (the same percentage also caps visible sway change); the old name still works.");
        if (options.ContainsKey("--loop-length-max"))
            deprecated?.Invoke("--loop-length-max is now --loop-max-seconds; the old name still works.");
        double? budget = null;
        if (options.TryGetValue("--retime-budget", out string? budgetText) || options.TryGetValue("--max-retime", out budgetText))
        {
            if (!double.TryParse(budgetText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
                !double.IsFinite(parsed) || parsed < 0 || parsed > MaximumBudgetPercent)
                throw new ArgumentException("--retime-budget must be a percentage from 0 to 5.");
            budget = parsed;
        }
        double? maximum = null;
        if (options.TryGetValue("--loop-max-seconds", out string? lengthText) || options.TryGetValue("--loop-length-max", out lengthText))
        {
            if (!double.TryParse(lengthText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
                !double.IsFinite(parsed) || parsed <= 0 || parsed > SwayRetimeOptions.MaximumLoopLengthSeconds)
                throw new ArgumentException("--loop-max-seconds must be a number of seconds above 0 and at most 3600.");
            maximum = parsed;
        }
        // 摆动改频默认开（三档的观感预算管的就是摆动求解），只有显式 --sway-retime off 才关。
        string sway = options.TryGetValue("--sway-retime", out string? swayText)
            ? swayText : SwayRetimeOptions.OnByDefault ? "on" : "off";
        if (sway is not ("off" or "on")) throw new ArgumentException("--sway-retime must be off or on.");
        return new(preset, budget, maximum, sway == "on");
    }

    /// <summary>写进 plan 的 profile 记录：每个值带来源，用户改过哪一项一看便知。</summary>
    public JsonObject ToJson() => new()
    {
        ["preset"] = Preset,
        ["retime_budget_percent"] = BudgetPercent,
        ["retime_budget_source"] = BudgetSource,
        ["common_retime_percent"] = CommonRetimePercent,
        ["loop_max_seconds"] = LoopMaximumSeconds,
        ["loop_max_seconds_source"] = LoopMaximumSource,
        ["minimum_visible_cycles"] = SwayRecurrenceSolver.MinimumVisibleCycles,
        ["minimum_loop_seconds"] = SwayRecurrenceSolver.MinimumLoopSeconds,
        ["slow_speed_deviation_limit_pixels_per_second"] = SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond,
        ["visible_speed_deviation_limit_pixels_per_second"] = SwayRecurrenceSolver.MaximumVisibleSpeedDeviationPixelsPerSecond
    };
}
