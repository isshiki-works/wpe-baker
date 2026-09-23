using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 合成校验：用同一份计划跑一个短探针，与原作参照配对比较完整画面。不通过就拒绝，报告带上探针、参照与比较结果的路径；
/// 探针里出现候选特有的脚本报错时按 <see cref="CandidateScriptErrorGate"/> 的状态单独拒绝，并附中英文理由。
/// </summary>
internal sealed class CompositionGate(CompositionGate.Validator validate) : IBakeGate
{
    /// <summary>跑短探针并与原作参照比较，返回合成校验记录。</summary>
    internal delegate Task<JsonObject> Validator(BakeGateContext context, CancellationToken cancellationToken);

    public async Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        JsonObject compositionValidation;
        using (context.Timing.Measure(StageTiming.CompositionValidation))
            compositionValidation = await validate(context, cancellationToken);
        context.CompositionValidation = compositionValidation;
        if (compositionValidation["status"]?.GetValue<string>() == "composition_pass") return null;
        bool scriptErrorsRejected = compositionValidation["status"]?.GetValue<string>() == CandidateScriptErrorGate.RejectedCompositionStatus;
        var evidence = new JsonObject
        {
            ["reason"] = compositionValidation["reason"]?.DeepClone(),
            ["metrics"] = compositionValidation["metrics"]?.DeepClone(),
            ["composition_validation"] = compositionValidation,
            ["probe_paths"] = new JsonObject
            {
                ["output"] = compositionValidation["probe_output_path"]?.DeepClone(),
                ["capture_source"] = compositionValidation["probe_capture_source_path"]?.DeepClone(),
                ["reference"] = compositionValidation["comparison_reference_path"]?.DeepClone(),
                ["project"] = compositionValidation["probe_project_path"]?.DeepClone(),
                ["comparison"] = compositionValidation["comparison_report_path"]?.DeepClone()
            }
        };
        var trailer = new JsonObject();
        if (scriptErrorsRejected)
        {
            trailer["reason_zh"] = compositionValidation["reason_zh"]?.DeepClone();
            trailer["reason_en"] = compositionValidation["reason_en"]?.DeepClone();
        }
        return context.Reject(scriptErrorsRejected ? CandidateScriptErrorGate.RejectedBakeStatus : "candidate_rejected_composition",
            context.Plan, "not_performed", evidence, trailer);
    }
}
