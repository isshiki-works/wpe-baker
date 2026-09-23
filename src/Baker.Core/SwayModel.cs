namespace Baker.Core;

/// <summary>
/// 摆动改频的开关参数。输出比例 = 输出像素 / 场景可见单位，只影响振幅换算（冻结项漂移的次序与报告）。
/// LoopLengthMaximumSeconds 是生效上限；VideoLimit 非空时它已按内嵌视频 2 GiB 上限收紧过，记录用户原值与收紧依据。
/// SpeedLimitScale 是速度偏差门限倍率（最终输出画布短边 / 1080，见 <see cref="SwayRecurrenceSolver.SpeedLimitScale"/>）。
/// </summary>
public sealed record SwayRetimeOptions(double LoopLengthMaximumSeconds, double OutputPerSceneX = 1, double OutputPerSceneY = 1,
    EmbeddedVideoLoopLimit? VideoLimit = null, RetimeProfile? Profile = null, double SpeedLimitScale = 1)
{
    /// <summary>档位给的观感改动预算（百分比）；null = 质量档或没选档，求解器取改动最小的解。</summary>
    public double? BudgetPercent => Profile?.BudgetPercent;

    /// <summary>
    /// 摆动改频的默认状态：三档都开（设计 `design-presets-simplify.md` §3——档位的观感预算管的就是摆动求解，
    /// 默认关等于默认没有档位）。CLI 的 `--sway-retime` 与界面高级区的勾选框都从这里取默认值，
    /// 要关掉就显式传 `--sway-retime off` 或取消勾选。
    /// </summary>
    public const bool OnByDefault = true;
}
