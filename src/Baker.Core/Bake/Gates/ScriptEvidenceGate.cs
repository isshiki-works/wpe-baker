using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 源脚本报错证据：计划里没有可用证据（旧计划）时，在 <c>&lt;输出&gt;.analysis-refresh</c> 按同样设置重新分析一遍换掉计划；
/// 刷新后的计划仍需无 blocker、无布局与层级冲突。每条报错都必须落在计划认识的图层上，否则要求重新分析。
/// 只抛异常，不写拒绝报告：缺证据是输入问题，不是这一案的结论。
/// </summary>
internal sealed class ScriptEvidenceGate(NativeTools tools) : IBakeGate
{
    public async Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        JsonArray? errors = HybridBakeService.PlannedSourceScriptErrors(context.Plan);
        if (errors is null)
        {
            context.Progress?.Report(new("refreshing_script_fault_evidence", 0, new Message("progress.refreshing_script_fault_evidence")));
            JsonObject plan = await new HybridScenePlanner(tools).AnalyzeSingleAsync(context.Settings with {
                Source = context.Source.SourcePath, OutputDirectory = context.Layout.AnalysisRefresh, RuntimeTraceFile = null
                }, context.Progress, cancellationToken);
            HybridPlanFormat.Validate(plan);
            if (plan["blockers"] is JsonArray { Count: > 0 })
                throw new InvalidDataException("The refreshed analysis requires resolution before generation.");
            if (LayoutAdmission.FullFrameConflict(plan) is Blocker refreshedLayoutConflict)
                throw refreshedLayoutConflict.ToException();
            if (LayoutAdmission.CompositionHierarchyConflict(plan) is Blocker refreshedHierarchyConflict)
                throw refreshedHierarchyConflict.ToException();
            context.Plan = plan;
            context.Settings = PlanSettings.Of(plan);
            errors = HybridBakeService.PlannedSourceScriptErrors(plan)
                ?? throw new InvalidDataException("The refreshed analysis omitted source script fault evidence.");
        }
        var plannedIds = context.Plan["layers"]!.AsArray().OfType<JsonObject>().Select(SceneGraph.Id).ToHashSet();
        if (errors.OfType<JsonObject>().Any(error => SceneGraph.Int(error["owner_layer_id"]) is not int owner || !plannedIds.Contains(owner)))
            throw new InvalidDataException("A source script fault lacks a known authored owner; analyze the source again before generation.");
        return null;
    }
}
