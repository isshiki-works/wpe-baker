using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 分析阶段的慢分量闭合预检：视频组里有缓变分量（plan 的 slow_components）所有者层时，按烘焙同一个主渲染请求渲 P+1 帧（只编第 0 帧），
/// 在编码前原帧上过 <see cref="LoopClosureCheck"/>（阈值不动）。P 取 plan 的解析周期，不搜。每组一条记录：所有者层、漂移上界、闭合读数、渲染器墙钟。
/// 没闭合的，调用方把所有者层留实时重新分析；闭合的照常判能，烘焙时接缝门照常复核。
/// </summary>
internal static class SlowClosureProbe
{
    internal static async Task<JsonArray> RunAsync(JsonObject plan, NativeTools tools, string output, CancellationToken token)
    {
        static int[] Layers(JsonObject group) => [.. group["layer_ids"]!.AsArray().Select(SceneGraph.Int).OfType<int>()];
        var records = new JsonArray();
        JsonObject[] groups = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()];
        // 特效前缀路线不烘视频组（只烘前缀缓存），没有要预检的。
        if (plan["route"]?.GetValue<string>() == "effect_prefix" || !groups.Any(group => GroupVerdicts.SlowDrift(plan, Layers(group)) is not null))
            return records;
        using var source = new ProjectSource(plan["source"]!.GetValue<string>());
        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
        JsonObject original = source.ReadJson(source.SceneResource), snapshot = plan["snapshot_properties"]!.DeepClone().AsObject();
        PlanTransforms.ApplyAudioEffectChoice(original, plan);
        string captureProject = Path.Combine(output, "capture-source");
        await CaptureSourceBuilder.PrepareAsync(captureProject, source, original,
            source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject(), snapshot, plan, settings, false,
            new JsonObject(), new StageTiming(), token);
        JsonObject candidate = plan["loop"]!["candidates"]![0]!.AsObject();
        ulong frames = candidate["frames"]!.GetValue<ulong>();
        ulong[] groupFrames = [.. groups.Select(group => candidate["group_frames"]?[group["id"]!.GetValue<string>()]?.GetValue<ulong>() ?? frames)];
        var runner = new NativeRenderRunner(tools);
        await using var scheduler = new GroupRenderScheduler(runner, new HybridBakeRequest(2, plan, output), plan, settings, groups,
            captureProject, output, snapshot, frames, groupFrames, 0, 0, [], 1, PlaybackEncoderSelection.Software, null, token);
        try
        {
            for (int index = 0; index < groups.Length; index++)
            {
                if (GroupVerdicts.SlowDrift(plan, Layers(groups[index])) is not (string degrees, int[] owners)) continue;
                GroupCapture capture = scheduler.Capture(groups[index]);
                ulong period = groupFrames[index];
                JsonObject manifest = await runner.RenderAsync(scheduler.ClosureProbeRequest(index, Path.Combine(output, $"group-{index}")), null, token);
                byte[] first = await LoopClosureCheck.ReadRetainedFrameAsync(manifest, 0, token);
                byte[] wrap = await LoopClosureCheck.ReadRetainedFrameAsync(manifest, period, token);
                JsonObject closure = LoopClosureCheck.Evaluate(first, wrap, (int)capture.PixelWidth, (int)capture.PixelHeight,
                    withAlpha: !capture.SceneClear, period, judged: true, scheduler.TileScale);
                records.Add(new JsonObject { ["group_id"] = groups[index]["id"]!.DeepClone(), ["owner_layer_ids"] = new JsonArray([.. owners.Select(id => (JsonNode)id)]),
                    ["drift_bound_degrees"] = degrees, ["status"] = closure["status"]!.DeepClone(),
                    ["renderer_wall_seconds"] = manifest["native_result"]?["wall_seconds"]?.DeepClone(), ["loop_closure"] = closure });
            }
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return records;
    }
}
