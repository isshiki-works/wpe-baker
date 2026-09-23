using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>合成夹具：分阶段计时的字段完整性、总和一致与缺失阶段的写法。</summary>
internal static class StageTimingChecks
{
    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    internal static void Run(Action<bool, string> check)
    {
        var timing = new StageTiming();
        timing.SetDevice("868080b0040000000002000000000000");
        using (timing.Measure(StageTiming.MasterRender)) Thread.Sleep(200);
        using (timing.Measure(StageTiming.EncodePlayback)) Thread.Sleep(80);
        using (timing.Measure(StageTiming.MasterRender)) Thread.Sleep(60);
        // 两次 master 捕获的回读与编码背压分量要累加，渲染器自报的墙钟同样累加。
        timing.AddMasterBreakdown(new JsonObject
        {
            ["stream_timing"] = new JsonObject { ["readback_seconds"] = 12.5, ["encode_seconds"] = 3.25 },
            ["native_result"] = new JsonObject { ["wall_seconds"] = 20.0 }
        });
        timing.AddMasterBreakdown(new JsonObject
        {
            ["stream_timing"] = new JsonObject { ["readback_seconds"] = 4.5, ["encode_seconds"] = 0.75 },
            ["native_result"] = new JsonObject { ["wall_seconds"] = 6.0 }
        });
        timing.AddMasterBreakdown(null);
        Thread.Sleep(60); // 没有单独计时的一段，必须落到 other 里
        JsonObject json = timing.ToJson(720);
        JsonObject stages = json["stages"]!.AsObject();

        check(StageTiming.ExclusiveStages.All(stage => stages.ContainsKey(stage)) && stages.ContainsKey(StageTiming.Other) &&
            stages.Count == StageTiming.ExclusiveStages.Length + 1,
            "stage_timing 列出全部互斥阶段和 other");

        check(stages[StageTiming.CompositionValidation] is null && stages[StageTiming.SeamCheck] is null &&
            stages[StageTiming.Crossfade] is null && stages[StageTiming.LoopStartSearch] is null &&
            stages[StageTiming.SourceCapture] is null && stages[StageTiming.HardwareDecodeCheck] is null &&
            Number(stages[StageTiming.MasterRender]) > 0 && Number(stages[StageTiming.EncodePlayback]) > 0,
            "没有发生过的阶段写 null 而不是 0");

        double total = Number(json["total_seconds"])!.Value;
        double sum = StageTiming.ExclusiveStages.Append(StageTiming.Other).Sum(stage => Number(stages[stage]) ?? 0);
        check(total > 0 && Math.Abs(sum - total) <= total * 0.01, "各阶段之和与 total_seconds 在 1% 以内一致");
        check(Number(stages[StageTiming.Other]) > 0, "未单独计时的时间落在 other 上");
        check(Number(stages[StageTiming.MasterRender]) >= 0.25, "同一阶段的多段计时累加");

        JsonObject breakdown = json["master_render_breakdown"]!.AsObject();
        check(Number(breakdown[StageTiming.Readback]) == 17.0 && Number(breakdown[StageTiming.EncodeMaster]) == 4.0 &&
            Number(breakdown["renderer_wall_seconds"]) == 26.0,
            "master_render_breakdown 累加回读、编码背压与渲染器自报墙钟");

        double expectedRate = 720 / Number(stages[StageTiming.MasterRender])!.Value;
        check(json["frames"]!.GetValue<ulong>() == 720UL &&
            Math.Abs(Number(json["frames_per_second_render"])!.Value - expectedRate) <= expectedRate * 0.01,
            "frames_per_second_render 是帧数除以主渲染秒数");
        check(json["device_uuid"]!.GetValue<string>() == "868080b0040000000002000000000000", "stage_timing 记录生成设备");

        var report = new JsonObject { ["status"] = "candidate_generated", ["frames"] = 720 };
        timing.Stamp(report);
        check(report["stage_timing"]?["frames"]?.GetValue<ulong>() == 720UL, "报告落盘时按报告自己的帧数打上计时");

        check(StageTiming.Summary(new JsonObject { ["status"] = "failed" }) is null, "没有 stage_timing 的旧报告不生成汇总行");
        var inlineGroup = new JsonObject { ["playback_encode"] = new JsonObject {
            ["encoder_used"] = "software", ["encode_seconds"] = null } };
        check(PlaybackEncoderSelection.Summarize("vulkan", [inlineGroup])["encode_seconds_total"] is null,
            "没有独立计时的管道编码不能汇总成零秒");

        var empty = new StageTiming();
        JsonObject emptyJson = empty.ToJson(0);
        check(emptyJson["frames_per_second_render"] is null &&
            StageTiming.ExclusiveStages.All(stage => emptyJson["stages"]![stage] is null),
            "没有跑过任何阶段时帧率与每个阶段都是 null");
    }
}
