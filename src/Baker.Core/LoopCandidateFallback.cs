using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 源周期路线的候选回退：编码后接缝校验失败时，沿 plan.loop.candidates 的现有顺序换下一个解析候选重跑主渲染与校验。
/// 周期仍全部来自解析，这里只在解析给出的候选表里往下走——不搜周期、不放宽阈值、不改候选排序规则。
/// 每次尝试的候选与接缝读数都记进 bake.json 的 loop_candidate_attempts，全部失败时 reason 列出各次越线指标。
/// 残差掩盖路线与 effect_prefix 路线不走这里。
/// </summary>
public static class LoopCandidateFallback
{
    /// <summary>最多尝试的解析候选个数（含第一个）。</summary>
    public const int MaximumAttempts = 3;

    /// <summary>第 attemptIndex 次（从 0 数）失败后还能不能换下一个候选。</summary>
    public static bool CanRetry(int attemptIndex, int candidateCount) =>
        attemptIndex >= 0 && attemptIndex + 1 < MaximumAttempts && attemptIndex + 1 < candidateCount;

    /// <summary>
    /// 把第 index 个候选换到候选表首位，其余相对顺序不变。所有读 candidates[0] 的地方（补丁应用、
    /// 视频速率覆盖、GUI 摘要）随之一致。第 k 次尝试调用 Promote(k)：前几次只动过位置 &lt; k 的元素，
    /// 位置 k 仍是原顺序里的第 k 个候选。
    /// </summary>
    public static void Promote(JsonArray candidates, int index)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (index < 0 || index >= candidates.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == 0) return;
        JsonNode node = candidates[index] ?? throw new InvalidDataException("Loop candidate is null.");
        candidates.RemoveAt(index);
        candidates.Insert(0, node);
    }

    /// <summary>一次尝试的记录：候选帧数、秒数、总调速，与该轮每个已校验组的接缝读数摘要。</summary>
    public static JsonObject Attempt(int attemptIndex, JsonObject candidate, string status, JsonArray groups)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(groups);
        var seams = new JsonArray();
        foreach (JsonObject group in groups.OfType<JsonObject>())
            if (group["encoded_loop_validation"] is JsonObject seam)
                seams.Add(SeamSummary(group["id"]?.GetValue<string>() ?? "", seam));
        return new JsonObject
        {
            ["attempt_index"] = attemptIndex,
            ["frames"] = candidate["frames"]?.DeepClone(),
            ["seconds"] = candidate["seconds"]?.DeepClone(),
            ["total_retime_cost_percent"] = candidate["total_retime_cost_percent"]?.DeepClone(),
            ["status"] = status,
            ["seams"] = seams
        };
    }

    /// <summary>最终选中的候选：原顺序里的第几个、帧数、秒数、总调速。</summary>
    public static JsonObject Selected(int attemptIndex, JsonObject candidate) => new()
    {
        ["attempt_index"] = attemptIndex,
        ["frames"] = candidate["frames"]?.DeepClone(),
        ["seconds"] = candidate["seconds"]?.DeepClone(),
        ["total_retime_cost_percent"] = candidate["total_retime_cost_percent"]?.DeepClone(),
        ["basis"] = attemptIndex == 0
            ? "The first analytic candidate passed the encoded seam check."
            : $"Earlier analytic candidates failed the encoded seam check; this is candidate #{attemptIndex} in plan.loop.candidates' original order. The period is still analytic; no threshold was relaxed."
    };

    /// <summary>
    /// 把 EncodedLoopValidator 的记录压成能读的摘要：没过的项、闭合最差瓦片与上限、参照差分图最差瓦片、接缝编码噪声、
    /// 解码帧数与应有帧数。数值都从记录里原样取，这里不另立任何阈值。
    /// </summary>
    public static JsonObject SeamSummary(string groupId, JsonObject seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        JsonNode? closure = seam["loop_closure"]?["rgb"]?["tile_64"];
        JsonNode? rgb = seam["reference_seam"]?["rgb"]?["tile_64"];
        return new JsonObject
        {
            ["group_id"] = groupId,
            ["status"] = seam["status"]?.DeepClone(),
            ["failures"] = seam["failures"]?.DeepClone() ?? new JsonArray(),
            ["closure_status"] = seam["loop_closure"]?["status"]?.DeepClone(),
            ["closure_worst_tile_mae"] = closure?["worst"]?.DeepClone(),
            ["closure_worst_x"] = closure?["worst_x"]?.DeepClone(),
            ["closure_worst_y"] = closure?["worst_y"]?.DeepClone(),
            ["closure_alpha_worst_tile_mae"] = seam["loop_closure"]?["alpha"]?["tile_64"]?["worst"]?.DeepClone(),
            ["closure_limit"] = seam["loop_closure"]?["limit_tile_mae_255"]?.DeepClone(),
            ["reference_difference_worst"] = rgb?["difference_map"]?["worst"]?.DeepClone(),
            ["reference_step_worst"] = rgb?["reference_step"]?["worst"]?.DeepClone(),
            ["encoding_noise_seam_worst"] = rgb?["encoding_noise_seam"]?["worst"]?.DeepClone(),
            ["decoded_frames"] = seam["actual"]?["decoded_frame_count"]?.DeepClone(),
            ["expected_frames"] = seam["expected"]?["frames"]?.DeepClone()
        };
    }

    /// <summary>写进 reason 的一句话：每次尝试的候选、结果与越线指标。</summary>
    public static string Summary(JsonArray attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var parts = new List<string>();
        foreach (JsonObject attempt in attempts.OfType<JsonObject>())
        {
            string cost = Number(attempt["total_retime_cost_percent"]) is double percent
                ? percent.ToString("0.###", CultureInfo.InvariantCulture) + "%" : "n/a";
            var seams = new List<string>();
            foreach (JsonObject seam in (attempt["seams"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string Fixed(JsonNode? node) => Number(node) is double value ? value.ToString("0.####", CultureInfo.InvariantCulture) : "n/a";
                string failures = string.Join("+", (seam["failures"] as JsonArray ?? []).Select(node => node?.GetValue<string>()));
                seams.Add($"{seam["group_id"]}: {(failures.Length == 0 ? "no failure" : failures)}, " +
                    $"closure worst tile {Fixed(seam["closure_worst_tile_mae"])}/{Fixed(seam["closure_limit"])} at ({seam["closure_worst_x"]}, {seam["closure_worst_y"]}), " +
                    $"reference difference {Fixed(seam["reference_difference_worst"])} (original step {Fixed(seam["reference_step_worst"])}, " +
                    $"encoding noise {Fixed(seam["encoding_noise_seam_worst"])}), frames {seam["decoded_frames"]}/{seam["expected_frames"]}");
            }
            parts.Add($"#{attempt["attempt_index"]} {attempt["frames"]} frames (retime {cost}): {attempt["status"]}" +
                (seams.Count == 0 ? "" : " [" + string.Join("; ", seams) + "]"));
        }
        return $"Analytic loop candidates tried ({parts.Count} of at most {MaximumAttempts}): " + string.Join(" | ", parts) + ".";
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        double number;
        if (value.TryGetValue(out number)) { }
        else if (value.TryGetValue(out int integer)) number = integer;
        else if (value.TryGetValue(out long wide)) number = wide;
        else if (value.TryGetValue(out uint unsigned)) number = unsigned;
        else if (value.TryGetValue(out ulong unsignedWide)) number = unsignedWide;
        else if (value.TryGetValue(out float single)) number = single;
        else return null;
        return double.IsFinite(number) ? number : null;
    }
}
