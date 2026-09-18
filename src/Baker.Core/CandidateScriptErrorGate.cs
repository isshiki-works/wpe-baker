using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 合成比对的脚本报错门。原作与候选由同一个离线渲染器、同一组参数各渲一遍，
/// 候选的脚本报错条数多于原作，说明保留下来的脚本依赖了被烘掉或被丢弃的对象或数据
/// （例如经 shared 全局对象传递的函数，查找记录抓不到），候选画面不可信：直接拒绝，不再判定后面的像素指标。
/// 两边计数缺一时记为 not_available，不在这里拒绝，交给其余检查。
/// </summary>
public static class CandidateScriptErrorGate
{
    public const string RejectedValidationStatus = "rejected_script_errors";
    public const string RejectedCompositionStatus = "composition_rejected_script_errors";
    public const string RejectedBakeStatus = "candidate_rejected_script_errors";
    /// <summary>理由里列出的报错条数。</summary>
    public const int ListedErrors = 3;
    private const int MaximumMessageLength = 160;

    public static JsonObject Evaluate(JsonObject comparison)
    {
        JsonObject? sourceResult = comparison["source_native_result"] as JsonObject;
        JsonObject? candidateResult = comparison["candidate_native_result"] as JsonObject;
        int? sourceCount = Count(sourceResult);
        int? candidateCount = Count(candidateResult);
        var result = new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = "not_available",
            ["source_script_error_count"] = sourceCount,
            ["candidate_script_error_count"] = candidateCount,
            ["scope"] = "Script error counts from the same offline renderer and parameters on the original and the candidate over the paired composition frames. More candidate errors reject the candidate before pixel limits are judged."
        };
        if (sourceCount is not int source || candidateCount is not int candidate) return result;
        if (candidate <= source)
        {
            result["status"] = "script_errors_not_increased";
            return result;
        }
        JsonObject[] added = AddedErrors(sourceResult!["source_script_errors"] as JsonArray,
            candidateResult!["source_script_errors"] as JsonArray);
        // 同一对象、同一属性、同一阶段、同一报错原文合成一条并记条数，列出的几条尽量覆盖不同对象。
        var listed = added.GroupBy(Key, StringComparer.Ordinal)
            .Select(group => (Error: group.First(), Count: group.Count())).Take(ListedErrors).ToArray();
        string ListFor(string language) => listed.Length == 0
            ? Messages.Get("bake.script_error_list_unavailable", language)
            : string.Join(language == Messages.Chinese ? "；" : "; ", listed.Select(item => Item(item.Error, item.Count, language)));
        result["status"] = RejectedValidationStatus;
        result["added_script_error_count"] = added.Length;
        result["listed_script_errors"] = new JsonArray(listed.Select(item =>
        {
            var error = item.Error.DeepClone().AsObject();
            error["occurrences"] = item.Count;
            return (JsonNode)error;
        }).ToArray());
        string english = Messages.Get("bake.candidate_script_errors", Messages.English, candidate, source, ListFor(Messages.English));
        result["reason"] = english;
        result["reason_zh"] = Messages.Get("bake.candidate_script_errors", Messages.Chinese, candidate, source, ListFor(Messages.Chinese));
        result["reason_en"] = english;
        return result;
    }

    private static int? Count(JsonObject? native) =>
        native?["source_script_error_count"] is JsonValue value && value.TryGetValue<int>(out int count) && count >= 0 ? count : null;

    private static string Key(JsonObject error) => string.Join("",
        error["owner_layer_id"]?.ToJsonString() ?? "", error["property"]?.ToJsonString() ?? "",
        error["phase"]?.ToJsonString() ?? "", error["message"]?.ToJsonString() ?? "");

    /// <summary>候选里比原作多出来的报错（按对象、属性、阶段、报错原文计数相减）；原作那边没有明细时全部算多出来的。</summary>
    private static JsonObject[] AddedErrors(JsonArray? sourceErrors, JsonArray? candidateErrors)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonObject error in sourceErrors?.OfType<JsonObject>() ?? [])
            remaining[Key(error)] = remaining.GetValueOrDefault(Key(error)) + 1;
        var added = new List<JsonObject>();
        foreach (JsonObject error in candidateErrors?.OfType<JsonObject>() ?? [])
        {
            string key = Key(error);
            if (remaining.GetValueOrDefault(key) > 0) remaining[key]--;
            else added.Add(error);
        }
        return added.ToArray();
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : node?.ToJsonString() ?? "?";

    private static string Item(JsonObject error, int occurrences, string language)
    {
        string message = Text(error["message"]).ReplaceLineEndings(" ").Trim();
        if (message.Length > MaximumMessageLength) message = message[..MaximumMessageLength].TrimEnd() + "…";
        return Messages.Get(occurrences > 1 ? "bake.script_error_item_repeated" : "bake.script_error_item", language,
            Text(error["owner_layer_id"]), Messages.EscapeName(Text(error["owner_name"])), Text(error["property"]),
            Text(error["phase"]), message, occurrences);
    }
}
