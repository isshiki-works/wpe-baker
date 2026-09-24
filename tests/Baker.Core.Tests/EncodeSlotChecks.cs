using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 成品编码槽位配额：参数解析（bake 请求 JSON 的 encode_slots / group_parallel）、配额边界、
/// 跨进程槽位的实际排队与等待计时，以及等待时间单独成一个阶段、不混进 encode_playback。
/// </summary>
internal static class EncodeSlotChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string RequestJson(string extra) => $$"""
        {
          "schema_version": 2,
          "plan": { "kind": "hybrid_video" },
          "output_directory": "D:\\nowhere\\out"{{extra}}
        }
        """;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    internal static async Task RunAsync(Action<bool, string> check)
    {
        // 参数解析：两项都不给时是默认值（不限并发、组并行自动），给了就按给的走。
        var defaults = JsonSerializer.Deserialize<HybridBakeRequest>(RequestJson(""), JsonOptions)!;
        check(defaults.EncodeSlots == 0 && defaults.GroupParallel == 0,
            "bake 请求不写 encode_slots / group_parallel 时默认不限并发、组并行自动");
        var chosen = JsonSerializer.Deserialize<HybridBakeRequest>(
            RequestJson(",\n  \"encode_slots\": 3,\n  \"group_parallel\": 4"), JsonOptions)!;
        check(chosen.EncodeSlots == 3 && chosen.GroupParallel == 4,
            "bake 请求里的 encode_slots / group_parallel 按 snake_case 读进来");

        // 配额边界：0 是不限；负数和超过 WaitAny 上限都当场拒绝，不静默改值。
        check(EncodeSlots.Create(0).Quota == 0 && ReferenceEquals(EncodeSlots.Create(0), EncodeSlots.Unlimited),
            "配额 0 = 不限并发");
        bool negativeRejected = false, oversizeRejected = false;
        try { EncodeSlots.Create(-1); } catch (ArgumentOutOfRangeException) { negativeRejected = true; }
        try { EncodeSlots.Create(EncodeSlots.MaximumQuota + 1); } catch (ArgumentOutOfRangeException) { oversizeRejected = true; }
        check(negativeRejected && oversizeRejected, "负数配额与超过 64 的配额都被拒绝");
        check(EncodeSlots.Create(2).HandleName(1).StartsWith(@"Global\", StringComparison.Ordinal) &&
            EncodeSlots.Create(2).HandleName(2).EndsWith("-2-of-2", StringComparison.Ordinal),
            "槽位内核对象名带 Global\\ 前缀，并把配额数写进名字里（配额不同的两批不混用槽位）");

        // 不限并发：立即拿到，等待 0 秒，Dispose 可以重复调。
        using (var free = await EncodeSlots.Unlimited.AcquireAsync())
        {
            check(free.WaitSeconds == 0, "不限并发时取槽位不等待");
            free.Dispose();
        }

        // 真排队：配额 1，第二个租约必须等第一个归还，且等待秒数记到了租约上。
        string name = "WpeBakerEncodeSlotChecks-" + Guid.NewGuid().ToString("N");
        var slots = EncodeSlots.Create(1, name);
        var first = await slots.AcquireAsync();
        check(first.WaitSeconds < 0.5, "空闲配额下第一个租约几乎不等待");
        Task<EncodeSlots.Lease> second = slots.AcquireAsync();
        await Task.Delay(400);
        check(!second.IsCompleted, "配额用满时第二个租约在等，不会超发");
        long releasedAt = Stopwatch.GetTimestamp();
        first.Dispose();
        var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(30));
        check(Stopwatch.GetElapsedTime(releasedAt).TotalSeconds < 10, "归还之后等待方及时拿到槽位");
        check(secondLease.WaitSeconds >= 0.3, "等槽位的墙钟秒记在租约上（这次至少等了 0.3 秒）");
        secondLease.Dispose();
        // 归还干净：同一组槽位还能再取到。
        using (var third = await slots.AcquireAsync().WaitAsync(TimeSpan.FromSeconds(30)))
            check(third.WaitSeconds < 2, "租约 Dispose 之后槽位真的还回去了");

        // 取消：配额占满时等待可以取消，不会把调用方卡死。
        var busy = EncodeSlots.Create(1, "WpeBakerEncodeSlotChecks-" + Guid.NewGuid().ToString("N"));
        using (await busy.AcquireAsync())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            bool cancelled = false;
            try { (await busy.AcquireAsync(cancellation.Token)).Dispose(); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "等槽位可以取消");
        }

        // 等待时间单独成阶段：算在互斥阶段里参与求和，但不混进 encode_playback。
        var timing = new StageTiming();
        timing.Add(StageTiming.EncodeSlotWait, 4.5);
        timing.Add(StageTiming.EncodePlayback, 2.25);
        var stages = timing.ToJson(600)["stages"]!.AsObject();
        check(StageTiming.ExclusiveStages.Contains(StageTiming.EncodeSlotWait) &&
            Number(stages[StageTiming.EncodeSlotWait]) == 4.5 && Number(stages[StageTiming.EncodePlayback]) == 2.25,
            "encode_slot_wait 是独立的互斥阶段，不计入 encode_playback");
    }
}
