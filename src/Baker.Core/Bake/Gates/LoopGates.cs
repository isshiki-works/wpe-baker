using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 循环准入：不信任计划里保存的切点与起点，从源与当前运行时证据重建循环，再走与分析同一个准入判定。
/// 无候选按无循环拒绝；留着的未解析分量逐条判定能否被接缝淡化掩盖，拒绝要说清是哪一层、哪个机制、缺什么证明。
/// </summary>
internal sealed class LoopAdmissionGate : IBakeGate
{
    public async Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        JsonObject plan = context.Plan;
        ProjectSource source = context.Source;
        var runtime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), cancellationToken))!.AsObject();
        // Rebuild from source and current runtime evidence; never accept a saved observed cut or start.
        HybridScenePlanner.RefreshLoop(plan, source, runtime, context.Settings);
        JsonArray? unresolved = plan["loop"]?["unresolved"] as JsonArray;
        context.ResidualMasking = null;
        AdmissionVerdict admission = Admission.Evaluate(plan, source.ReadJson(source.SceneResource),
            ResidualMasking.ResourceReader(source, context.Settings.Assets));
        if (admission.Rejection == AdmissionRejection.NoLoop) return context.NoLoop(unresolved);
        JsonObject classification = admission.Residual!;
        if (classification["status"]?.GetValue<string>() == "no_residual") return null;
        plan["loop"]!["residual_masking"] = classification.DeepClone();
        if (admission.Rejection == AdmissionRejection.ResidualNotMaskable)
            return context.NoLoop(unresolved, classification);
        // 防御：透明组与多组都能淡化，布局淡化不了的只有"可掩盖分量不在任何视频组里"；analyze 已把它写成 blocker，
        // 界面改过分配的计划仍可能走到这里。在合成校验与任何渲染之前干净拒绝，写 bake.json，不抛异常，也不自动换分配。
        if (admission.Rejection == AdmissionRejection.ResidualLayout)
        {
            JsonObject layout = admission.LayoutGate!;
            classification["status"] = "rejected_layout";
            classification["reason"] = layout["reason"]!.DeepClone();
            classification["reason_zh"] = layout["reason_zh"]!.DeepClone();
            classification["reason_en"] = layout["reason_en"]!.DeepClone();
            classification["layout_gate"] = layout;
            plan["loop"]!["residual_masking"] = classification.DeepClone();
            return context.NoLoop(unresolved, classification, ResidualMasking.LayoutRejectedStatus);
        }
        context.ResidualMasking = classification;
        return null;
    }
}

/// <summary>成品帧数取首个循环候选；没有候选（或帧数为 0）按无循环拒绝。</summary>
internal sealed class LoopFramesGate : IBakeGate
{
    public Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        context.Frames = (context.Plan["loop"]?["candidates"] as JsonArray)?.FirstOrDefault()?["frames"]?.GetValue<ulong>() ?? 0;
        return Task.FromResult(context.Frames == 0 ? context.NoLoop(null) : null);
    }
}

/// <summary>
/// 锁定周期的粒子层（封顶 + 确定寿命）只在循环长度是替换周期的整数倍时同相位：analyze 的求解器已保证，
/// 这里防御旧版或改过的计划。没有残差掩盖时放行。
/// </summary>
internal sealed class ParticleCycleGate : IBakeGate
{
    public Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        if (context.ResidualMasking is not JsonObject residual ||
            ResidualMasking.LockedCycleMismatch(residual, context.Frames, context.Settings.FpsNumerator, context.Settings.FpsDenominator)
                is not JsonObject cycleMismatch)
            return Task.FromResult<BakeRejection?>(null);
        residual["status"] = "rejected_particle_cycle";
        residual["reason"] = cycleMismatch["reason"]!.DeepClone();
        residual["particle_cycle_mismatch"] = cycleMismatch;
        context.Plan["loop"]!["residual_masking"] = residual.DeepClone();
        return Task.FromResult<BakeRejection?>(context.NoLoop(context.Plan["loop"]?["unresolved"] as JsonArray, residual));
    }
}
