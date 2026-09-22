using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>全分辨率第一层结论出来之后下一步做什么。</summary>
public enum ResidualStartStep
{
    /// <summary>第一层通过，用这个起点继续淡化与编码。</summary>
    Accept,
    /// <summary>第一层被拒，还有排序靠后的候选起点：清掉这一轮的输出，换下一个起点重渲 master 再测。</summary>
    RetryNextStart,
    /// <summary>第一层被拒，候选起点已经试完（或到了 <see cref="ResidualMasking.MaximumStartAttempts"/>）：拒绝。</summary>
    Reject
}

/// <summary>
/// 残差掩盖路线的起点回退。起点搜索只在降采样样本上看得到 Δ_0 与 Δ_stride，淡化窗口后段的单帧尖峰（例如随机精灵
/// 在两条时间线里出现的时刻不同）要到全分辨率 master 上才测得出来。所以第一层在某个起点上被拒时，按起点排序依次换下一个
/// 候选重渲 master 再测，最多 <see cref="ResidualMasking.MaximumStartAttempts"/> 个；周期不变、阈值不变，只换相位。
/// 每次尝试的起点、样本排序键与各残差组的 max_k / 整幅都记进 bake.json 的 loop_start_attempts。
/// </summary>
public static class ResidualStartFallback
{
    /// <summary>起点搜索记录里的尝试顺序（按排序键排好的前 N 个候选），缺失时退回选中的那一个。</summary>
    public static JsonObject[] Order(JsonObject startSearch)
    {
        ArgumentNullException.ThrowIfNull(startSearch);
        JsonObject[] order = (startSearch["start_attempt_order"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(row => row["start_frame"] is JsonValue).Take(ResidualMasking.MaximumStartAttempts).ToArray();
        if (order.Length > 0) return order;
        return startSearch["selected"] is JsonObject selected ? [selected] : [];
    }

    /// <summary>第 <paramref name="attemptIndex"/> 次（从 0 数）尝试的第一层结论出来之后的下一步。</summary>
    public static ResidualStartStep Next(int attemptIndex, bool firstLayerPassed, int orderCount)
    {
        if (attemptIndex < 0) throw new ArgumentOutOfRangeException(nameof(attemptIndex));
        if (firstLayerPassed) return ResidualStartStep.Accept;
        return attemptIndex + 1 < Math.Min(ResidualMasking.MaximumStartAttempts, orderCount)
            ? ResidualStartStep.RetryNextStart : ResidualStartStep.Reject;
    }

    /// <summary>
    /// 一次尝试的记录：起点、样本上的排序键与整幅，以及这一轮每个测过第一层的组的 max_k 瓦片（带 k 与位置）与 Δ_0 整幅。
    /// <paramref name="groups"/> 是 (组 id, MeasureSeamResidualAsync 的结果)。
    /// </summary>
    public static JsonObject Attempt(int attemptIndex, ulong startFrame, JsonObject? sampled,
        IEnumerable<(string GroupId, JsonObject SeamResidual)> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var rows = new JsonArray();
        bool passed = true;
        foreach ((string groupId, JsonObject seam) in groups)
        {
            JsonNode? layer = seam["first_layer"];
            bool groupPassed = layer?["passed"] is JsonValue flag && flag.TryGetValue(out bool value) && value;
            passed &= groupPassed;
            rows.Add(new JsonObject
            {
                ["group_id"] = groupId,
                ["status"] = seam["status"]?.DeepClone(),
                ["maximum_worst_tile_rgb_mae_255"] = layer?["maximum_worst_tile_rgb_mae_255"]?.DeepClone(),
                ["maximum_worst_tile_frame"] = layer?["maximum_worst_tile_frame"]?.DeepClone(),
                ["maximum_worst_tile_x"] = layer?["maximum_worst_tile_x"]?.DeepClone(),
                ["maximum_worst_tile_y"] = layer?["maximum_worst_tile_y"]?.DeepClone(),
                ["hard_cut_global_rgb_mae_255"] = layer?["hard_cut_global_rgb_mae_255"]?.DeepClone()
            });
        }
        if (rows.Count == 0) passed = false;
        return new JsonObject
        {
            ["attempt_index"] = attemptIndex,
            ["start_frame"] = startFrame,
            ["sampled_sort_key"] = sampled?["sampled_sort_key"]?.DeepClone(),
            ["sampled_global_rgb_mae_255"] = sampled?["sampled_global_rgb_mae_255"]?.DeepClone(),
            ["status"] = passed ? "passed_first_layer" : "rejected_seam_residual",
            ["groups"] = rows
        };
    }

    /// <summary>最终选中的起点：排序里的第几个、起点帧，以及为什么是它。</summary>
    public static JsonObject Selected(int attemptIndex, ulong startFrame) => new()
    {
        ["attempt_index"] = attemptIndex,
        ["start_frame"] = startFrame,
        ["basis"] = attemptIndex == 0
            ? "排序键最小的候选起点在全分辨率 master 上通过了第一层。"
            : $"排序靠前的 {attemptIndex} 个候选起点在全分辨率 master 上第一层被拒，这是排序里的第 {attemptIndex + 1} 个；周期仍来自解析，阈值没有放宽。"
    };

    /// <summary>
    /// 候选起点全部被拒时写进 bake.json 的理由：legacy 英文进 reason，<see cref="Message.Write"/> 同时写中英对照。
    /// 每个起点列出各组的 max_k 瓦片（带 k）与 Δ_0 整幅。
    /// </summary>
    public static Message RejectionReason(JsonArray attempts, int candidateCount, uint crossfadeFrames)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        string Fixed(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number)
            ? number.ToString("0.####", CultureInfo.InvariantCulture) : "n/a";
        string List(bool chinese)
        {
            var parts = new List<string>();
            foreach (JsonObject attempt in attempts.OfType<JsonObject>())
            {
                IEnumerable<string> groups = (attempt["groups"] as JsonArray ?? []).OfType<JsonObject>().Select(group => chinese
                    ? $"{group["group_id"]} 瓦片最大 {Fixed(group["maximum_worst_tile_rgb_mae_255"])}/255（k = {group["maximum_worst_tile_frame"]}），整幅 {Fixed(group["hard_cut_global_rgb_mae_255"])}/255"
                    : $"{group["group_id"]} worst tile {Fixed(group["maximum_worst_tile_rgb_mae_255"])}/255 (k = {group["maximum_worst_tile_frame"]}), whole frame {Fixed(group["hard_cut_global_rgb_mae_255"])}/255");
                parts.Add(chinese
                    ? $"起点 {attempt["start_frame"]}：{string.Join("，", groups)}"
                    : $"start {attempt["start_frame"]}: {string.Join(", ", groups)}");
            }
            return string.Join(chinese ? "；" : "; ", parts);
        }
        string tried = attempts.Count.ToString(CultureInfo.InvariantCulture);
        string candidates = candidateCount.ToString(CultureInfo.InvariantCulture);
        string tile = ResidualMasking.MaximumResidualTileRgbMae255.ToString("0.##", CultureInfo.InvariantCulture);
        string whole = ResidualMasking.MaximumSeamRgbMae255.ToString("0.##", CultureInfo.InvariantCulture);
        string lastK = (crossfadeFrames == 0 ? 0 : crossfadeFrames - 1).ToString(CultureInfo.InvariantCulture);
        return new Message("bake.residual_start_attempts_rejected",
            [tried, candidates, List(chinese: false), tile, whole, lastK],
            [tried, candidates, List(chinese: true), tile, whole, lastK]);
    }
}
