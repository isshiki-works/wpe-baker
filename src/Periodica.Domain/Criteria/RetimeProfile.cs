namespace Periodica.Domain;

/// <summary>
/// 三档预设：档位给的是"允许多大的观感改动"，循环长度是求解结果（用户 2026-09-18 03:15 定，样片见 sway-budget-scan.md §3）。
/// 一个百分比当通用分量调速预算；长度上限只是兜底，真正压短 L 的是预算。
/// 预设值来自 27 案五档扫描（同报告 §1.2、§4）：
/// - 效率 5%：相对 3% 中位再缩 1.25 倍，单槽 9.7 → 7.4 h；只有 13 案真能吃到 4% 以上。
/// - 平衡 3%：第一条能对全部 27 案兑现的线（最难的 3516174947 在 600 s 内的最小可见改动就是 2.71%）。
/// - 质量：不设百分比门槛，求解器按"可见项改动最小"取解（规则 v2 的原目标）。
/// - v1.0.2 三档默认长度上限统一为 600 s；L 只能取 base 周期的整数倍，19 案的 P 本身就 50–180 s，
///   60 s 上限下 27 案只有 1 案有解。
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
    /// <summary>
    /// 兼容档（实验性，只能手动选，不进自动回退链）：效率档翻倍，预算 10%、循环上限 1200 s。
    /// 10% 没有扫描数据支持，观感变化未验证；接缝、合成、画质门与内嵌视频 2 GiB 判定不放宽。
    /// </summary>
    public const string Compatibility = "compatibility";

    /// <summary>值的来源：档位给的、用户覆盖的，或调用方没选档时的旧默认。</summary>
    public const string FromPreset = "preset";
    public const string FromOverride = "override";
    public const string FromDefault = "default";

    /// <summary>
    /// --retime-budget 允许的最大值（百分比）：效率档 5% 是扫描过的最高档，再往上没有数据支持。
    /// </summary>
    public const double MaximumBudgetPercent = 5;

    /// <summary>通用求解器分量调速预算的上限（兼容档的 10%；<see cref="CommonLoopSolver"/> 的请求校验读这一个常量）。</summary>
    public const double MaximumCommonRetimePercent = 10;

    /// <summary>没选档时的循环长度上限（秒），与 --loop-max-seconds 的默认一致。</summary>
    public const double DefaultLoopMaximumSeconds = CommonLoopSolver.DefaultLoopLengthMaximumSeconds;

    public static bool IsKnownPreset(string value) => value is Efficiency or Balanced or Quality or Compatibility;

    /// <summary>档位对应的循环取向（求解器认的还是这三个名字）。</summary>
    public static string LoopPreferenceForPreset(string preset) => preset switch
    {
        Efficiency or Compatibility => "performance",
        Balanced => "balanced",
        Quality => "quality",
        _ => throw new InvalidDataException("A preset must be efficiency, balanced, quality, or compatibility.")
    };

    /// <summary>档位的观感改动预算（百分比）；质量档为 null（不设门槛，取改动最小）。</summary>
    public static double? PresetBudgetPercent(string preset) => preset switch
    {
        Efficiency => 5,
        Balanced => 3,
        Quality => null,
        Compatibility => MaximumCommonRetimePercent,
        _ => throw new InvalidDataException("A preset must be efficiency, balanced, quality, or compatibility.")
    };

    /// <summary>档位的循环长度上限（秒，兜底值）。</summary>
    public static double PresetLoopMaximumSeconds(string preset) => preset switch
    {
        Efficiency => 600,
        Balanced => 600,
        Quality => 600,
        Compatibility => 1200,
        _ => throw new InvalidDataException("A preset must be efficiency, balanced, quality, or compatibility.")
    };

    /// <summary>
    /// 把档位与高级覆盖合成一份生效值。覆盖优先于档位；没选档时预算为 null、上限 600 s、通用预算用 <paramref name="commonFallbackPercent"/>，
    /// 与本次改动之前的行为逐项相同。
    /// </summary>
    public static RetimeProfile Resolve(string? preset, double? budgetOverride, double? loopMaximumOverride, double commonFallbackPercent)
    {
        if (preset is not null && !IsKnownPreset(preset)) throw new InvalidDataException("A preset must be efficiency, balanced, quality, or compatibility.");
        double? budget = budgetOverride ?? (preset is null ? null : PresetBudgetPercent(preset));
        double maximum = loopMaximumOverride ?? (preset is null ? DefaultLoopMaximumSeconds : PresetLoopMaximumSeconds(preset));
        string budgetSource = budgetOverride is not null ? FromOverride : preset is null ? FromDefault : FromPreset;
        string maximumSource = loopMaximumOverride is not null ? FromOverride : preset is null ? FromDefault : FromPreset;
        return new(preset, budget, budget ?? commonFallbackPercent, maximum, budgetSource, maximumSource);
    }
}
