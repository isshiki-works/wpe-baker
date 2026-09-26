using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 分析阶段的慢分量闭合预检：视频组里有缓变分量（plan 的 slow_components）所有者层时，按烘焙同一个主渲染请求出第 0 帧与第 P 帧（第 P 帧靠多预热 P 帧，不回读中间帧），
/// 在编码前原帧上过 <see cref="LoopClosureCheck"/>（阈值不动）。P 取 plan 的解析周期，不搜。每组一条记录：所有者层、漂移上界、闭合读数、渲染器墙钟。
/// 没闭合的，调用方把所有者层留实时重新分析；闭合的照常判能，烘焙时接缝门照常复核。
/// </summary>
internal static class SlowClosureProbe
{
    static int[] Layers(JsonObject group) => [.. group["layer_ids"]!.AsArray().Select(SceneGraph.Int).OfType<int>()];

    /// <summary>按 plan 建采集工程与组渲染调度（与烘焙同一个主渲染请求）；返回调度器与各组的循环帧数。</summary>
    static async Task<(GroupRenderScheduler Scheduler, ulong[] GroupFrames)> OpenAsync(JsonObject plan, JsonObject[] groups, NativeRenderRunner runner,
        string output, CancellationToken token)
    {
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
        return (new GroupRenderScheduler(runner, new HybridBakeRequest(2, plan, output), plan, settings, groups,
            captureProject, output, snapshot, frames, groupFrames, 0, 0, [], 1, PlaybackEncoderSelection.Software, null, token), groupFrames);
    }

    internal static async Task<JsonArray> RunAsync(JsonObject plan, NativeTools tools, string output, CancellationToken token)
    {
        var records = new JsonArray();
        JsonObject[] groups = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()];
        // 特效前缀路线不烘视频组（只烘前缀缓存），没有要预检的。
        if (plan["route"]?.GetValue<string>() == "effect_prefix" || !groups.Any(group => GroupVerdicts.SlowDrift(plan, Layers(group)) is not null))
            return records;
        var runner = new NativeRenderRunner(tools);
        (GroupRenderScheduler opened, ulong[] groupFrames) = await OpenAsync(plan, groups, runner, output, token);
        await using var scheduler = opened;
        try
        {
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
    /// 超出逐项预算的慢项改速（原周期 ≥ 60 s，按至少一圈进求解器）实测看不看得出。看成品的人没有原作对照，能感知的是速度变化，
    /// 所以量两版的速度差，不量同一时刻的累积相位差：改速前（这些项的补丁还原成原值）与改速后按烘焙同一主渲染请求各出第 0 帧和第 t 帧，
    /// 两版第 0 帧同一状态出发，分块求改速后相对改速前的位移，(位移_t − 位移_0)/t 就是这段时间里两版的平均速度差。
    /// t 从 8 s 折半到 0.5 s，每块取位移不超过 1 px（分块光流的线性区）的最长窗；读数取各块最大值，按输出短边折到 1080p 口径，
    /// ≤ 0.1 px/s（1.0.2 摆动慢项同一门限）才放行。返回 null = 首选候选里没有超预算的慢项；status 不是 passed 的，调用方把所有者层留实时重新分析。
    /// </summary>
    internal static async Task<JsonObject?> SpeedAsync(JsonObject plan, NativeTools tools, string output, CancellationToken token)
    {
        if (plan["loop"]?["candidates"] is not JsonArray { Count: > 0 } candidates) return null;
        double budget = plan["loop"]!["retime_budget_percent"]!.GetValue<double>();
        var relaxed = candidates[0]!["components"]!.AsArray().OfType<JsonObject>()
            .Where(c => c["old_period_seconds"]!.GetValue<double>() >= SwayRecurrenceSolver.VisiblePeriodSeconds && Math.Abs(c["delta_percent"]!.GetValue<double>()) > budget + 1e-9)
            .Select(c => c["id"]!.GetValue<string>()).ToHashSet();
        if (relaxed.Count == 0) return null;
        JsonObject before = plan.DeepClone().AsObject();
        JsonObject[] patches = [.. before["loop"]!["candidates"]![0]!["patches"]!.AsArray().OfType<JsonObject>()
            .Where(patch => relaxed.Contains(patch["component"]?.GetValue<string>() ?? ""))];
        foreach (JsonObject patch in patches) patch["new_value"] = patch["old_value"]!.DeepClone();
        int[] owners = [.. patches.Select(patch => patch["owner_layer_id"]!.GetValue<int>()).Distinct()];
        JsonObject[] groups = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()];
        int[] probed = [.. Enumerable.Range(0, groups.Length).Where(index => Layers(groups[index]).Intersect(owners).Any())];
        var record = new JsonObject { ["components"] = new JsonArray([.. relaxed.Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
            ["owner_layer_ids"] = new JsonArray([.. owners.Select(id => (JsonNode)id)]),
            ["limit_px_per_second"] = SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond };
        // 特效前缀路线不烘视频组、或所有者层不全在视频组里：量不到就不放行
        if (plan["route"]?.GetValue<string>() == "effect_prefix" || probed.Length == 0 || owners.Except(probed.SelectMany(index => Layers(groups[index]))).Any())
        {
            record["status"] = "not_measured";
            return record;
        }
        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
        double[] windows = [8, 4, 2, 1, 0.5];
        ulong[] frames = [0, .. windows.Select(seconds => (ulong)Math.Round(seconds * settings.FpsNumerator / settings.FpsDenominator))];
        var runner = new NativeRenderRunner(tools);
        double peak = 0, wall = 0;
        try
        {
            (GroupRenderScheduler openedBefore, _) = await OpenAsync(before, groups, runner, Path.Combine(output, "before"), token);
            await using var schedulerBefore = openedBefore;
            (GroupRenderScheduler openedAfter, _) = await OpenAsync(plan, groups, runner, Path.Combine(output, "after"), token);
            await using var schedulerAfter = openedAfter;
            foreach (int index in probed)
            {
                GroupCapture capture = schedulerAfter.Capture(groups[index]);
                int width = (int)capture.PixelWidth, height = (int)capture.PixelHeight;
                // 分块光流在约 540p 的网格上做（1080p 下 2×2 平均），块边 16 格 = 1080p 的 32 px
                int step = Math.Max(1, (int)Math.Round(2 * schedulerAfter.TileScale));
                double toReference = step / schedulerAfter.TileScale;
                var shifts = new List<(double X, double Y)?[]>();
                foreach (ulong frame in frames)
                {
                    async Task<byte[]> Render(GroupRenderScheduler scheduler, string name)
                    {
                        JsonObject manifest = await runner.RenderAsync(scheduler.ClosureProbeRequest(index, Path.Combine(output, $"speed-{index}-{frame}-{name}"), frame), null, token);
                        wall += manifest["native_result"]?["wall_seconds"]?.GetValue<double>() ?? 0;
                        return await LoopClosureCheck.ReadRetainedFrameAsync(manifest, 0, token);
                    }
                    shifts.Add(Shift(await Render(schedulerBefore, "before"), await Render(schedulerAfter, "after"), width, height, step));
                }
                for (int block = 0; block < shifts[0].Length; block++)
                    for (int window = 0; window < windows.Length; window++)
                    {
                        if (shifts[window + 1][block] is not var (x, y)) continue;
                        var (x0, y0) = shifts[0][block] ?? (0, 0);
                        double moved = Math.Sqrt((x - x0) * (x - x0) + (y - y0) * (y - y0)) * toReference;
                        if (moved > 1 && window < windows.Length - 1) continue;
                        peak = Math.Max(peak, moved / windows[window]);
                        break;
                    }
            }
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        record["peak_speed_deviation_px_per_second"] = peak;
        record["renderer_wall_seconds"] = wall;
        record["status"] = peak <= SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond ? "passed" : "visible";
        return record;
    }

    /// <summary>
    /// 分块 Lucas–Kanade：两帧先按 step×step 平均（亮度 R+2G+B 加透明度），以改速前的帧为参考逐块解 2×2 结构张量，
    /// 得改速后相对改速前的位移（网格格数）；纹理太弱（结构张量小特征值不够）的块为 null。
    /// </summary>
    static (double X, double Y)?[] Shift(byte[] before, byte[] after, int width, int height, int step)
    {
        int w = width / step, h = height / step, size = 16;
        float[] Grid(byte[] rgba)
        {
            var grid = new float[w * h];
            for (int y = 0; y < h * step; y++)
                for (int x = 0; x < w * step; x++)
                {
                    int i = (y * width + x) * 4;
                    grid[y / step * w + x / step] += (rgba[i] + 2 * rgba[i + 1] + rgba[i + 2] + rgba[i + 3]) / (float)(step * step);
                }
            return grid;
        }
        float[] a = Grid(before), b = Grid(after);
        var shifts = new List<(double X, double Y)?>();
        for (int top = 1; top + size < h; top += size)
            for (int left = 1; left + size < w; left += size)
            {
                double xx = 0, xy = 0, yy = 0, xt = 0, yt = 0;
                for (int y = top; y < top + size; y++)
                    for (int x = left; x < left + size; x++)
                    {
                        int i = y * w + x;
                        double gx = (a[i + 1] - a[i - 1]) / 2.0, gy = (a[i + w] - a[i - w]) / 2.0, gt = b[i] - a[i];
                        xx += gx * gx; xy += gx * gy; yy += gy * gy; xt += gx * gt; yt += gy * gt;
                    }
                double det = xx * yy - xy * xy, least = (xx + yy) / 2 - Math.Sqrt((xx - yy) * (xx - yy) / 4 + xy * xy);
                // 每格平均梯度至少 5（亮度满量程 1275）才算有纹理
                shifts.Add(least < size * size * 25.0 ? null : ((xy * yt - yy * xt) / det, (xy * xt - xx * yt) / det));
            }
        return [.. shifts];
    }
}
