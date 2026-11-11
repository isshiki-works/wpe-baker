using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 源脚本报错证据：每条报错都必须落在计划认识的图层上，计划里没有这份证据就要求重新分析。
/// 只抛异常，不写拒绝报告：缺证据是输入问题，不是这一案的结论。
/// </summary>
internal sealed class ScriptEvidenceGate : IBakeGate
{
    public Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        JsonArray errors = HybridBakeService.PlannedSourceScriptErrors(context.Plan)
            ?? throw new InvalidDataException("The plan has no source script fault evidence; analyze the source again before generation.");
        var plannedIds = context.Plan["layers"]!.AsArray().OfType<JsonObject>().Select(SceneGraph.Id).ToHashSet();
        if (errors.OfType<JsonObject>().Any(error => SceneGraph.Int(error["owner_layer_id"]) is not int owner || !plannedIds.Contains(owner)))
            throw new InvalidDataException("A source script fault lacks a known authored owner; analyze the source again before generation.");
        return Task.FromResult<BakeRejection?>(null);
    }
}
