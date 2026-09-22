using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>bake 第一步复核循环时的拒绝种类，按 bake 的检查顺序排列。</summary>
public enum AdmissionRejection
{
    None,
    /// <summary>整层路线没有解析候选。</summary>
    NoLoop,
    /// <summary>留下的未解析分量里有不可掩盖的项（写 blocker.bake_allocation）。</summary>
    ResidualNotMaskable,
    /// <summary>残差都可掩盖，但有残差层不在任何视频组里，布局替它淡化不了（写残差布局 blocker）。</summary>
    ResidualLayout
}

/// <summary>
/// <see cref="Admission.Evaluate"/> 的结论。Residual 是 loop.residual_masking 的内容（整层路线才有）；
/// Blocker 是分析要写进 plan 的那一条（残差不可掩盖或残差布局），没有则为 null；LayoutGate 是 residual_layout_gate 的取证。
/// </summary>
public sealed record AdmissionVerdict(string Route, AdmissionRejection Rejection, JsonObject? Residual, Blocker? Blocker, JsonObject? LayoutGate)
{
    public bool Admitted => Rejection == AdmissionRejection.None;
}

/// <summary>
/// 生成准入的唯一判定。analyze（残差布局前提、更小分配取证）、PresetCascade（生成准入、组数上限、可烘判定）、
/// bake 第一步（重算循环后复核）都调这里，同一份输入、同一个结论，各处只决定把结论写到哪里。
/// 依赖 trace/运行时证据的判据（HDR、透视、前缀安全、视频外壳、公共图层查询、全幅/层级冲突）由分析写成 blocker，
/// bake 以"plan 没有 blocker"复核，不在这里重判。
/// </summary>
public static class Admission
{
    // 2–4 组省电；6 组核显功耗 +106%，9 组封装功耗 +10.9%。
    public const int MaxVideoGroups = 4;

    /// <summary>
    /// 整层路线的循环准入：未解析分量（去掉说明性条目）逐条判定能否被接缝淡化掩盖，再看可掩盖分量是否都落在视频组里。
    /// 效果前缀路线不适用，直接通过（路线只有整层与效果前缀两种）。纯函数：只读 plan、源场景与资源，不改 plan。
    /// </summary>
    public static AdmissionVerdict Evaluate(JsonObject plan, JsonObject scene, Func<string, JsonObject?> readResource)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string route = plan["route"]?.GetValue<string>() ?? "";
        if (route == "effect_prefix") return new(route, AdmissionRejection.None, null, null, null);
        // 说明性条目（更小分配取证）不是时间机制，不能当成识别不了的机制去判定。
        var input = plan.DeepClone().AsObject();
        if (input["loop"] is JsonObject loop && loop["unresolved"] is JsonArray unresolved)
            loop["unresolved"] = new JsonArray(unresolved.OfType<JsonObject>()
                .Where(item => item["kind"]?.GetValue<string>() != ResidualMasking.AllocationFallbackKind).Select(item => item.DeepClone()).ToArray());
        JsonObject residual = ResidualMasking.Classify(input, scene, readResource);
        bool hasCandidates = plan["loop"]?["candidates"] is JsonArray { Count: > 0 };
        if (residual["status"]?.GetValue<string>() == "rejected")
        {
            // 无候选时 bake 先按"无循环"拒绝；分析照样记下残差不可掩盖，两边都拒。
            string reason = residual["reason_en"]?.GetValue<string>() ?? residual["reason"]?.GetValue<string>() ?? "Unresolved content must remain live.";
            return new(route, hasCandidates ? AdmissionRejection.ResidualNotMaskable : AdmissionRejection.NoLoop, residual,
                new Blocker(BlockerCode.BakeAllocation, [reason]), null);
        }
        if (!hasCandidates) return new(route, AdmissionRejection.NoLoop, residual, null, null);
        // 布局本身有冲突时那条 blocker 已经让用户先选布局，这里不叠加。
        if (residual["status"]?.GetValue<string>() == "residual_maskable" && plan["whole_layer"]?["layout_conflict"] is null &&
            !ResidualMasking.LayoutAllowsMasking(plan, residual))
        {
            JsonObject gate = ResidualMasking.LayoutRejection(plan, residual, out Blocker blocker);
            return new(route, AdmissionRejection.ResidualLayout, residual, blocker, gate);
        }
        return new(route, AdmissionRejection.None, residual, null, null);
    }

    /// <summary>
    /// analyze 写残差布局前提：<see cref="Evaluate(JsonObject, JsonObject, Func{string, JsonObject?})"/> 判为 ResidualLayout 时，
    /// 取证放 residual_layout_gate，blocker 同时进 blockers 与 whole_layer.blockers，status 改为 requires_resolution。
    /// 不适用时什么都不改，返回 null。
    /// </summary>
    public static JsonObject? ApplyResidualLayoutGate(JsonObject plan, JsonObject scene, Func<string, JsonObject?> readResource)
    {
        AdmissionVerdict verdict = Evaluate(plan, scene, readResource);
        if (verdict.Rejection != AdmissionRejection.ResidualLayout) return null;
        plan["residual_layout_gate"] = verdict.LayoutGate;
        foreach (JsonNode? node in new[] { plan["blockers"], plan["whole_layer"]?["blockers"] })
            if (node is JsonArray blockers) PlanBlockers.Add(blockers, verdict.Blocker!);
        plan["status"] = "requires_resolution";
        if (plan["whole_layer"] is JsonObject wholeLayer) wholeLayer["status"] = "unavailable";
        return verdict.LayoutGate;
    }

    /// <summary>从 plan 自带的源与 assets 读场景后判定（cascade 与 CLI 分状态子 plan 用）。</summary>
    public static AdmissionVerdict Evaluate(JsonObject plan)
    {
        using var source = new ProjectSource(plan["source"]!.GetValue<string>());
        return Evaluate(plan, source.ReadJson(source.SceneResource),
            ResidualMasking.ResourceReader(source, plan["settings"]?["assets"]?.GetValue<string>()));
    }

    /// <summary>
    /// 把生成准入写进整层 plan：loop.residual_masking 记分类，残差不可掩盖时追加 blocker.bake_allocation 并重算裁定与结论。
    /// 所有产出最终 plan 的分析路径（预设级联的每次尝试、CLI 的分状态子 plan）都要过这一步，分析说能生成的 bake 第一步才不会拒。
    /// </summary>
    public static void ApplyGenerationAdmission(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan["kind"]?.GetValue<string>() != "hybrid_video" || plan["route"]?.GetValue<string>() != "whole_layer") return;
        AdmissionVerdict verdict = Evaluate(plan);
        plan["loop"]!["residual_masking"] = verdict.Residual;
        if (verdict.Blocker is not { Code: BlockerCode.BakeAllocation } blocker) return;
        plan["blockers"]!.AsArray().Add(blocker.ToNode());
        plan["status"] = "requires_resolution";
        plan["suitability"] = HybridScenePlanner.Suitability(plan);
        PlanNarrative.Attach(plan);
    }

    /// <summary>
    /// 可烘：与 PlanNarrative 写 summary.bakeable* 的条件同一来源——没有"重分配后无独立内容"的裁定、没有 blocker、有首个候选。
    /// </summary>
    public static bool Bakeable(JsonObject plan) =>
        plan["suitability"]?["rule"]?.GetValue<string>() != HybridSuitability.NoIndependentContentRule &&
        plan["blockers"] is not JsonArray { Count: > 0 } && FirstCandidate(plan) is not null;

    /// <summary>整层取 loop.candidates[0]，否则取第一个有候选的效果前缀缓存的首个候选。</summary>
    internal static JsonObject? FirstCandidate(JsonObject plan)
    {
        if ((plan["loop"]?["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is { } whole) return whole;
        return (plan["effect_prefix_caches"] as JsonArray)?.OfType<JsonObject>()
            .Select(cache => (cache["loop"]?["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault())
            .FirstOrDefault(candidate => candidate is not null);
    }

    /// <summary>要编码的动态视频数：效果前缀按缓存数，整层按未被静态证明的视频组数。</summary>
    public static int GroupCount(JsonObject plan) => plan["route"]?.GetValue<string>() == "effect_prefix"
        ? (plan["effect_prefix_caches"] as JsonArray)?.Count ?? 0
        : (plan["video_groups"] as JsonArray)?.OfType<JsonObject>().Count(group => !StaticVerified(group)) ?? 0;

    public static int StaticGroupCount(JsonObject plan) => plan["route"]?.GetValue<string>() == "whole_layer"
        ? (plan["video_groups"] as JsonArray)?.OfType<JsonObject>().Count(StaticVerified) ?? 0 : 0;

    private static bool StaticVerified(JsonObject group) => group["static_verified"]?.GetValue<bool>() == true &&
        group["static_verification"]?["basis"]?.GetValue<string>() == "source_and_runtime_static_proof";

    /// <summary>预设级联的接受条件：可烘且动态视频数不超过上限。</summary>
    public static bool Accepted(JsonObject plan) => Bakeable(plan) && GroupCount(plan) <= MaxVideoGroups;
}
