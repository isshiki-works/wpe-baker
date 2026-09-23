using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 档位数据与取舍在 Domain 的 <see cref="RetimeProfile"/>；这里只留依赖分析请求与 plan JSON 的两项，
/// 用静态扩展挂回 RetimeProfile 名下，现有调用方写法不变（登记在转发器清单，C3 改为直接调用）。
/// </summary>
public static class RetimeProfileJson
{
    extension(RetimeProfile)
    {
        /// <summary>按分析请求解析（CLI、界面与 bake 前重分析共用这一条路径，三处结果一致）。</summary>
        public static RetimeProfile Resolve(HybridAnalyzeRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return RetimeProfile.Resolve(request.Preset, request.RetimeBudgetPercent, request.LoopLengthMaximumSeconds, request.MaximumRetimePercent);
        }
    }

    extension(RetimeProfile profile)
    {
        /// <summary>
        /// 写进 plan 的 profile 记录：每个值带来源，用户改过哪一项一看便知。两个速度门限写生效值：
        /// <paramref name="speedLimitScale"/> 是输出短边 / 1080（<see cref="SwayRecurrenceSolver.SpeedLimitScale"/>），1080p 为 1。
        /// </summary>
        public JsonObject ToJson(double speedLimitScale) => new()
        {
            ["preset"] = profile.Preset,
            ["retime_budget_percent"] = profile.BudgetPercent,
            ["retime_budget_source"] = profile.BudgetSource,
            ["common_retime_percent"] = profile.CommonRetimePercent,
            ["loop_max_seconds"] = profile.LoopMaximumSeconds,
            ["loop_max_seconds_source"] = profile.LoopMaximumSource,
            ["minimum_visible_cycles"] = SwayRecurrenceSolver.MinimumVisibleCycles,
            ["minimum_loop_seconds"] = SwayRecurrenceSolver.MinimumLoopSeconds,
            ["slow_speed_deviation_limit_pixels_per_second"] = SwayRecurrenceSolver.SlowSpeedDeviationLimit(speedLimitScale),
            ["visible_speed_deviation_limit_pixels_per_second"] = SwayRecurrenceSolver.VisibleSpeedDeviationLimit(speedLimitScale)
        };
    }
}
