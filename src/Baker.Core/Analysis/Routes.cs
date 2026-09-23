using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 路线：整层 → 特效前缀 → 布局准入（含全幅尾组降级，见 <see cref="LayoutAdmission"/>）→ 仍被布局挡住时再试一次特效前缀，
/// 按这个固定顺序各试一次。更小分配（loop_allocation_fallback）排在这之后，仍由 HybridScenePlanner 递归整次分析取证：
/// 改成内存迭代会改变该字段的语义与子 plan 的采纳时机，是行为变化，不在本步做。
/// </summary>
internal sealed class Routes
{
    private readonly Func<Task<JsonArray>> prefixCaches;

    /// <summary>当前是否已改走特效前缀路线。</summary>
    internal bool EffectPrefix { get; private set; }

    private Routes(bool effectPrefix, Func<Task<JsonArray>> prefixCaches) => (EffectPrefix, this.prefixCaches) = (effectPrefix, prefixCaches);

    /// <param name="plan">整层路线的 plan（route/status/blockers/whole_layer 已按初判写好）。</param>
    /// <param name="effectPrefix">初判是否已走特效前缀。</param>
    /// <param name="prefixCaches">求本场景可用的特效前缀缓存；同一捕获点只探测一次由调用方保证。</param>
    /// <returns>定稿的 plan（尾组降级时是新对象）与是否走特效前缀。</returns>
    internal static async Task<(JsonObject Plan, bool EffectPrefix)> SettleAsync(JsonObject plan, bool effectPrefix, string requestedLayout,
        int groupCount, JsonArray dependencies, Func<Task<JsonArray>> prefixCaches, Func<JsonObject, JsonObject> resolveDemotedLoop)
    {
        var routes = new Routes(effectPrefix, prefixCaches);
        // 整层被阻断：先看特效前缀能不能接手。
        if (!routes.EffectPrefix && plan["status"]?.GetValue<string>() == "requires_resolution") await routes.TryEffectPrefixAsync(plan);
        var layout = LayoutAdmission.Evaluate(plan, requestedLayout, routes.EffectPrefix, groupCount, dependencies, resolveDemotedLoop);
        // 布局冲突连尾组降级也化解不了：再试一次特效前缀（前缀路线不重排整层分组，布局不再适用）。
        if (layout.Conflict is not null && !routes.EffectPrefix) await routes.TryEffectPrefixAsync(layout.Plan);
        layout.Record(routes.EffectPrefix);
        return (layout.Plan, routes.EffectPrefix);
    }

    /// <summary>有可用前缀缓存就改走特效前缀：换路线、换缓存、清 blockers，状态回到 requires_loop_analysis。</summary>
    private async Task TryEffectPrefixAsync(JsonObject plan)
    {
        JsonArray fallback = await prefixCaches();
        if (fallback.Count == 0) return;
        EffectPrefix = true;
        plan["route"] = "effect_prefix";
        plan["effect_prefix_caches"] = fallback;
        plan["blockers"] = new JsonArray();
        plan["status"] = "requires_loop_analysis";
    }

    /// <summary>整层路线的循环是否完整：零未解析项且至少一个候选。</summary>
    internal static bool WholeLoopComplete(JsonObject loop) =>
        loop["unresolved"] is JsonArray { Count: 0 } && loop["candidates"] is JsonArray { Count: > 0 };

    /// <summary>whole_layer 副本按 plan 当前的 blockers 与 loop 重写：各拷一份，零阻断且循环完整才 available。</summary>
    internal static void RefreshWholeLayer(JsonObject plan)
    {
        JsonObject wholeLayer = plan["whole_layer"]!.AsObject();
        wholeLayer["blockers"] = plan["blockers"]!.DeepClone();
        wholeLayer["loop"] = plan["loop"]!.DeepClone();
        wholeLayer["status"] = wholeLayer["blockers"]!.AsArray().Count == 0 &&
            WholeLoopComplete(wholeLayer["loop"]!.AsObject()) ? "available" : "unavailable";
    }
}
