using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 档位数据与取舍在 Domain 的 <see cref="RetimeProfile"/>；这里只留依赖分析请求与 plan JSON 的两项。
/// </summary>
public static class RetimeProfileJson
{
    /// <summary>按分析请求解析（CLI、界面与 bake 前重分析共用这一条路径，三处结果一致）。</summary>
    public static RetimeProfile Resolve(HybridAnalyzeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RetimeProfile.Resolve(request.Preset, request.RetimeBudgetPercent, request.LoopLengthMaximumSeconds, request.MaximumRetimePercent);
    }

    /// <summary>
    /// 写进 plan 的 profile 记录：每个值带来源，用户改过哪一项一看便知。
    /// </summary>
    public static JsonObject ToJson(RetimeProfile profile) => new()
    {
        ["preset"] = profile.Preset,
        ["retime_budget_percent"] = profile.BudgetPercent,
        ["retime_budget_source"] = profile.BudgetSource,
        ["common_retime_percent"] = profile.CommonRetimePercent,
        ["loop_max_seconds"] = profile.LoopMaximumSeconds,
        ["loop_max_seconds_source"] = profile.LoopMaximumSource
    };
}
