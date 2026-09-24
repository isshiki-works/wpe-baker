using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 磁盘闸门：中间产物峰值按计划预估，空间不够就在任何渲染开始前干净拒绝（见 <see cref="BakeDiskBudget"/>）。
/// 探针只有几十帧，不过这道闸；真正的量在主渲染上。
/// </summary>
internal sealed class DiskBudgetGate : IBakeGate
{
    public Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        // auto/vulkan 走 GPU 直编（不落 master）；GPU 组回退或 auto 落到软件档时，每份 master 开写前另有同口径检查（NativeRenderRunner）。
        bool gpu = PlaybackEncoderSelection.Normalize(context.Request.PlaybackEncoder) is PlaybackEncoderSelection.Auto or PlaybackEncoderSelection.Vulkan;
        int[] residualGroups = context.ResidualMasking is JsonObject masking ? ResidualMasking.ResidualGroupIndexes(context.Plan, masking) : [];
        if (BakeDiskBudget.Reject(context.Plan, context.Frames, Math.Max(1, context.Request.GroupParallel), gpu, residualGroups,
                context.Layout.Output) is not JsonObject diskRejection)
            return Task.FromResult<BakeRejection?>(null);
        return Task.FromResult<BakeRejection?>(context.Reject(BakeDiskBudget.RejectedBakeStatus, context.Plan, "not_performed", new() {
            ["reason"] = diskRejection["reason"]?.DeepClone(), ["reason_localized"] = diskRejection["reason_localized"]?.DeepClone(),
            ["disk_budget"] = diskRejection }, new()));
    }
}

/// <summary>
/// 内嵌视频大小（WPE 实测 2 GiB 上限，见 <see cref="EmbeddedVideoBudget"/>）：起点搜索与主渲染之前按合成探针的试编码外推，
/// 超限就干净拒绝，不再跑完几个小时才在装配时失败。外推读不到时只记录、不拒绝，编码后还有一次按实际字节的检查。
/// 没有合成校验结果（探针）时不外推。
/// </summary>
internal sealed class EmbeddedVideoGate(EmbeddedVideoGate.Estimator estimate) : IBakeGate
{
    /// <summary>按合成校验的探针试编码外推每个视频组的成品大小。</summary>
    internal delegate Task<JsonObject> Estimator(JsonObject compositionValidation, ulong frames, HybridAnalyzeRequest settings,
        CancellationToken cancellationToken);

    public async Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        if (context.CompositionValidation is not JsonObject compositionValidation) return null;
        JsonObject embeddedVideoEstimate = await estimate(compositionValidation, context.Frames, context.Settings, cancellationToken);
        context.EmbeddedVideoEstimate = embeddedVideoEstimate;
        if (embeddedVideoEstimate["status"]?.GetValue<string>() != "predicted_over_limit") return null;
        return context.Reject(EmbeddedVideoBudget.RejectedBakeStatus, context.Plan, "not_performed", new() {
            ["reason"] = embeddedVideoEstimate["reason"]?.DeepClone(),
            ["reason_localized"] = embeddedVideoEstimate["reason_localized"]?.DeepClone(),
            ["composition_validation"] = compositionValidation, ["embedded_video_estimate"] = embeddedVideoEstimate }, new());
    }
}
