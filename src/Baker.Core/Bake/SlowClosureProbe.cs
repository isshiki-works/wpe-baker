using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 分析阶段的慢分量闭合预检：视频组里有缓变分量（plan 的 slow_components）所有者层时，按烘焙同一个主渲染请求出第 0 帧与第 P 帧（第 P 帧靠多预热 P 帧，不回读中间帧），
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
        // 特效前缀路线不烘视频组（只烘前缀缓存），前缀缓存在规划时已由 PrefixAsync 预检过。
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
        // 与烘焙同一份预热（粒子预热 + 入场帧）与残差起点：不带入场帧时，有入场动画的组第 0 帧还在入场里，必然判没闭合。
        // 残差分类取分析写下的 loop.residual_masking（烘焙准入门同一判定，能生成的 plan 不会是拒绝态）。
        var runtime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), token))!.AsObject();
        JsonObject? residual = plan["loop"]?["residual_masking"] is JsonObject masking &&
            masking["status"]?.GetValue<string>() != "no_residual" ? masking : null;
        bool daytimeExport = DaytimeSplit.PrepareDynamicExport(original["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(SceneGraph.Id),
            plan, runtime["runtime_dependencies"]!.AsArray()) is not null;
        var (crossfade, particleWarmup, intro, residualGroups) = HybridBakeService.GroupTiming(plan, settings, residual, runtime, daytimeExport);
        var runner = new NativeRenderRunner(tools);
        await using var scheduler = new GroupRenderScheduler(runner, new HybridBakeRequest(2, plan, output), plan, settings, groups,
            captureProject, output, snapshot, frames, groupFrames, crossfade, particleWarmup + intro, residualGroups, 1,
            PlaybackEncoderSelection.Software, null, token);
        try
        {
            if (residualGroups.Length > 0) await LoopStartSelector.SearchAsync(runner, scheduler, 1, null, new StageTiming(), token);
            for (int index = 0; index < groups.Length; index++)
            {
                if (GroupVerdicts.SlowDrift(plan, Layers(groups[index])) is not (string degrees, int[] owners)) continue;
                GroupCapture capture = scheduler.Capture(groups[index]);
                ulong period = groupFrames[index];
                JsonObject start = await runner.RenderAsync(scheduler.ClosureProbeRequest(index, Path.Combine(output, $"group-{index}-0"), 0), null, token);
                JsonObject end = await runner.RenderAsync(scheduler.ClosureProbeRequest(index, Path.Combine(output, $"group-{index}-p"), period), null, token);
                byte[] first = await LoopClosureCheck.ReadRetainedFrameAsync(start, 0, token);
                byte[] wrap = await LoopClosureCheck.ReadRetainedFrameAsync(end, 0, token);
                JsonObject closure = LoopClosureCheck.Evaluate(first, wrap, (int)capture.PixelWidth, (int)capture.PixelHeight,
                    withAlpha: !capture.SceneClear, period, judged: true, scheduler.TileScale);
                records.Add(new JsonObject { ["group_id"] = groups[index]["id"]!.DeepClone(), ["owner_layer_ids"] = new JsonArray([.. owners.Select(id => (JsonNode)id)]),
                    ["drift_bound_degrees"] = degrees, ["status"] = closure["status"]!.DeepClone(),
                    ["renderer_wall_seconds"] = new[] { start, end }.Sum(render => render["native_result"]?["wall_seconds"]?.GetValue<double>() ?? 0),
                    ["loop_closure"] = closure });
            }
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return records;
    }

    /// <summary>
    /// 特效前缀缓存的同一预检：缓存从第 0 帧起录、在自身周期 P 上闭合，没有组预热。按烘焙同一个捕获源
    /// （<see cref="EffectPrefixBakeService.PrepareCaptureSourceAsync"/>）、同一个终端捕获选择与原生捕获尺寸出第 0 帧与第 P 帧，
    /// 过同一个 <see cref="LoopClosureCheck"/>（透明与否按第 0 帧定，同烘焙）。缓存循环没有缓变分量时返回 null，不用预检；
    /// 渲染失败记 probe_failed，调用方只认 closed，按没闭合处理。
    /// </summary>
    internal static async Task<JsonObject?> PrefixAsync(JsonObject cache, NativeTools tools, HybridAnalyzeRequest settings, ProjectSource source,
        JsonObject snapshot, string output, CancellationToken token)
    {
        if (GroupVerdicts.SlowDrift(cache) is not (string degrees, _)) return null;
        int owner = cache["owner_layer_id"]!.GetValue<int>();
        JsonObject loop = cache["loop"]!.AsObject();
        ulong period = loop["candidates"]![0]!["frames"]!.GetValue<ulong>();
        var record = new JsonObject { ["group_id"] = "effect-prefix-" + owner, ["owner_layer_ids"] = new JsonArray(owner),
            ["prefix_effect_count"] = cache["prefix_effect_count"]!.DeepClone(), ["drift_bound_degrees"] = degrees };
        var renders = new List<JsonObject>();
        try
        {
            string captureProject = Path.Combine(output, "capture-source");
            await EffectPrefixBakeService.PrepareCaptureSourceAsync(captureProject, source, settings.Assets, source.ReadJson(source.SceneResource),
                snapshot, loop, token);
            var target = new RenderCaptureSelection(owner, cache["terminal_effect_id"]!.GetValue<int>(), EffectTerminal: true, ExactExtent: true,
                ForceVisibleOwner: cache["preserve_external_visibility"]?.GetValue<bool>() == true ? true : null);
            var runner = new NativeRenderRunner(tools);
            async Task<JsonObject> Render(string name, uint width, uint height, ulong warmup, RenderCaptureSelection capture, bool trace)
            {
                JsonObject rendered = await runner.RenderRawAsync(new(captureProject, settings.Assets, Path.Combine(output, name), width, height,
                    settings.FpsNumerator, settings.FpsDenominator, 1, WarmupFrames: warmup, Seed: 17, CaptureTarget: capture, UserProperties: snapshot,
                    DeviceUuid: settings.DeviceUuid, TraceScene: trace,
                    OfflineVideoRateOverrides: HybridBakeService.SelectVideoRateOverrides(loop, new HashSet<int> { owner })), token);
                renders.Add(rendered);
                return rendered;
            }
            // 捕获尺寸取烘焙元数据探测的同一个读数
            JsonObject extent = (await Render("metadata", 64, 64, 0, target with { ExactExtent = false }, true))["native_result"]?["capture_source"] as JsonObject
                ?? throw new InvalidDataException("Terminal metadata probe omitted its capture source.");
            uint width = extent["width"]?.GetValue<uint>() ?? 0, height = extent["height"]?.GetValue<uint>() ?? 0;
            if (width == 0 || height == 0) throw new InvalidDataException("Terminal metadata probe reported an invalid capture extent.");
            byte[] first = await File.ReadAllBytesAsync((await Render("frame-0", width, height, 0, target, false))["rgba_path"]!.GetValue<string>(), token);
            byte[] wrap = await File.ReadAllBytesAsync((await Render("frame-p", width, height, period, target, false))["rgba_path"]!.GetValue<string>(), token);
            bool packedAlpha = false;
            for (int pixel = 3; pixel < first.Length && !packedAlpha; pixel += 4) packedAlpha = first[pixel] != byte.MaxValue;
            JsonObject closure = LoopClosureCheck.Evaluate(first, wrap, (int)width, (int)height, withAlpha: packedAlpha, period, judged: true,
                SwayRecurrenceSolver.SpeedLimitScale(settings.Width, settings.Height));
            record["status"] = closure["status"]!.DeepClone();
            record["loop_closure"] = closure;
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            record["status"] = "probe_failed";
            record["error"] = error.Message;
        }
        finally
        {
            record["renderer_wall_seconds"] = renders.Sum(render => render["native_result"]?["wall_seconds"]?.GetValue<double>() ?? 0);
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return record;
    }
}
