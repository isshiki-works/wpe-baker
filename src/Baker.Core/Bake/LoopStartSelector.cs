using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 残差掩盖路线的起点：每个残差组都参与低分辨率起点评分，联合挑一个起点共享给所有组，保持原作的组间相位关系；
/// 放在任何组主渲染之前，排在前面的非残差组也用同一个起点。选中的起点再由全分辨率第一层复核（组循环里），
/// 这里同时给出复核的尝试顺序与记录格式。周期不变、阈值不变，只选相位。
/// </summary>
internal static class LoopStartSelector
{
    /// <summary>
    /// 跑各残差组的起点评分并合成共享起点，写进 <paramref name="scheduler"/> 的 <see cref="GroupRenderScheduler.StartFrame"/>
    /// 与各组搜索记录；返回合成后的搜索记录（bake.json 的 loop_start_search）。
    /// 采样步长取 gcd(P, 16)：周期不是 16 的倍数时步长缩小、候选变多，不再因对齐问题抛异常。
    /// </summary>
    internal static async Task<JsonObject> SearchAsync(NativeRenderRunner runner, GroupRenderScheduler scheduler, int parallel,
        IProgress<RenderProgress>? progress, StageTiming timing, CancellationToken cancellationToken)
    {
        uint sampleStride = ResidualMasking.StartSearchStride(scheduler.Frames);
        int[] residualGroups = scheduler.ResidualGroupIndexes;
        // 各组评分互不依赖：请求先按组序建好，最多 parallel 个同时跑；结果仍按组序登记与合成，与逐组串行逐字节相同。
        var requests = residualGroups.Select(groupIndex => scheduler.Groups[groupIndex])
            .Select(group => (Id: group["id"]!.GetValue<string>(), Request: scheduler.StartSearchRequest(group, scheduler.Capture(group), sampleStride)))
            .ToArray();
        var searches = new (JsonObject Search, IReadOnlyList<ResidualStartCandidate> Candidates)[requests.Length];
        JsonObject startSearch;
        using (timing.Measure(StageTiming.LoopStartSearch))
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, requests.Length),
                new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (slot, token) =>
                {
                    progress?.Report(new("searching_loop_start", 0,
                        $"在锁定的解析周期内按接缝残差挑选起点帧（组 {requests[slot].Id}，预热 {scheduler.SearchWarmupFrames} 帧，步长 {sampleStride} 帧，搜索窗 {ResidualMasking.SearchWindowPeriods} 个周期）。"));
                    var scores = new List<ResidualStartCandidate>();
                    JsonObject search = await runner.SearchLoopStartAsync(requests[slot].Request,
                        scheduler.Frames, scheduler.CrossfadeFrames, scheduler.TileScale, progress, token, requests.Length > 1 ? scores : null);
                    search["group_id"] = requests[slot].Id;
                    searches[slot] = (search, scores);
                });
            for (int slot = 0; slot < requests.Length; ++slot)
                scheduler.StartSearches.Add(requests[slot].Id, searches[slot].Search);
            startSearch = NativeRenderRunner.CombineLoopStartSearches(searches);
        }
        scheduler.StartFrame = startSearch["selected_start_frame"]!.GetValue<ulong>();
        return startSearch;
    }

    /// <summary>起点搜索记录里的尝试顺序（按排序键排好的前 N 个候选），缺失时退回选中的那一个。</summary>
    public static JsonObject[] Order(JsonObject startSearch)
    {
        ArgumentNullException.ThrowIfNull(startSearch);
        JsonObject[] order = (startSearch["start_attempt_order"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(row => row["start_frame"] is JsonValue).Take(ResidualMasking.MaximumStartAttempts).ToArray();
        if (order.Length > 0) return order;
        return startSearch["selected"] is JsonObject selected ? [selected] : [];
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
