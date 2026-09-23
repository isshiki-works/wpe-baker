using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 烘焙前闸门链的一道。放行返回 null；拒绝返回 <see cref="BakeRejection"/>，由调用方写成 bake.json 后结束这一案；
/// 输入本身不合法（旧计划刷新失败、缺证据）时直接抛异常，与拒绝区分开。闸门可以改写上下文里的 plan 与判定结果，
/// 后一道看到的是前一道留下的状态，所以顺序是契约的一部分（见 <see cref="BakeGates.Preflight"/>）。
/// </summary>
internal interface IBakeGate
{
    Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken);
}

/// <summary>闸门之间传递的状态。只读部分是这一案的输入；可写部分由闸门填好，全部放行后交给主渲染。</summary>
internal sealed class BakeGateContext(HybridBakeRequest request, ProjectSource source, string sourceHash, WorkLayout layout,
    IProgress<RenderProgress>? progress, StageTiming timing)
{
    internal HybridBakeRequest Request => request;
    internal ProjectSource Source => source;
    internal WorkLayout Layout => layout;
    internal IProgress<RenderProgress>? Progress => progress;
    internal StageTiming Timing => timing;

    /// <summary>这一案的计划：旧计划刷新脚本报错证据时整份换掉，循环复核时就地改写。</summary>
    internal required JsonObject Plan { get; set; }
    internal required HybridAnalyzeRequest Settings { get; set; }
    /// <summary>成品帧数：首个循环候选的帧数。</summary>
    internal ulong Frames { get; set; }
    /// <summary>残差掩盖判定；null = 源周期路线（没有需要淡化的未解析分量）。</summary>
    internal JsonObject? ResidualMasking { get; set; }
    /// <summary>合成校验结果（短探针与原作参照的配对比较），也是内嵌视频外推的输入。</summary>
    internal JsonObject? CompositionValidation { get; set; }
    internal JsonObject? EmbeddedVideoEstimate { get; set; }

    /// <summary>拒绝报告：状态、证据与尾随字段由闸门给，身份与效果分辨率设置取自这一案。</summary>
    internal BakeRejection Reject(string status, JsonObject plan, string loopValidation, JsonObject evidence, JsonObject trailer) =>
        new(status, sourceHash, plan, request.EffectRenderScale, request.MatchEffectResolution, loopValidation, evidence, trailer);

    /// <summary>
    /// 找不到可用循环的拒绝（无候选、未解析分量不可掩盖、布局淡化不了、锁定周期粒子不同相位）：
    /// 有残差判定时用它的理由并附上判定，否则按有无未解析分量给统一文案。plan 取此刻的副本。
    /// </summary>
    internal BakeRejection NoLoop(JsonArray? unresolved, JsonObject? residual = null, string status = "candidate_rejected_no_loop")
    {
        bool layoutRejected = status == Baker.Core.ResidualMasking.LayoutRejectedStatus;
        var trailer = new JsonObject();
        if (residual?["reason"]?.GetValue<string>() is string residualReason) trailer["reason"] = residualReason;
        else new Message(unresolved is { Count: > 0 } ? "bake.loop_unresolved" : "bake.no_loop_candidate").Write(trailer, "reason");
        if (layoutRejected)
        {
            trailer["reason_zh"] = residual?["reason_zh"]?.DeepClone();
            trailer["reason_en"] = residual?["reason_en"]?.DeepClone();
        }
        if (residual is not null) trailer["residual_masking"] = residual.DeepClone();
        return Reject(status, Plan.DeepClone().AsObject(), layoutRejected ? "not_performed" : "no_suitable_loop", new(), trailer);
    }
}

internal static class BakeGates
{
    /// <summary>
    /// 成品烘焙（非探针）在任何渲染之前依次过的闸门：旧计划补脚本报错证据 → 循环从源与运行时证据重建并准入 →
    /// 定帧数 → 磁盘峰值 → 锁定周期粒子相位 → 合成校验（含候选脚本报错门）→ 内嵌视频 2 GiB 外推。
    /// 合成校验要跑短探针，所以排在所有只读判定之后；内嵌视频外推读的是探针的试编码。
    /// </summary>
    internal static IBakeGate[] Preflight(NativeTools tools, CompositionGate.Validator validate, EmbeddedVideoGate.Estimator estimate) =>
    [
        new ScriptEvidenceGate(tools), new LoopAdmissionGate(), new LoopFramesGate(), new DiskBudgetGate(),
        new ParticleCycleGate(), new CompositionGate(validate), new EmbeddedVideoGate(estimate)
    ];

    /// <summary>依次过闸，第一道拒绝即停；全部放行返回 null。</summary>
    internal static async Task<BakeRejection?> FirstRejectionAsync(IEnumerable<IBakeGate> gates, BakeGateContext context,
        CancellationToken cancellationToken)
    {
        foreach (IBakeGate gate in gates)
            if (await gate.CheckAsync(context, cancellationToken) is BakeRejection rejection) return rejection;
        return null;
    }
}
