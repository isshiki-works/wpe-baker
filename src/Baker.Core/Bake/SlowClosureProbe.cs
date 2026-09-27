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

    /// <summary>按 plan 建采集工程与组渲染调度（与烘焙同一个主渲染请求、同一份预热与残差起点）；返回调度器、各组循环帧数与残差组。</summary>
    static async Task<(GroupRenderScheduler Scheduler, ulong[] GroupFrames, int[] ResidualGroups)> OpenAsync(JsonObject plan, JsonObject[] groups,
        NativeRenderRunner runner, string output, CancellationToken token)
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
        // 与烘焙同一份预热（粒子预热 + 入场帧）与残差起点：不带入场帧时，有入场动画的组第 0 帧还在入场里，必然判没闭合。
        // 残差分类取分析写下的 loop.residual_masking（烘焙准入门同一判定，能生成的 plan 不会是拒绝态）。
        var runtime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), token))!.AsObject();
        JsonObject? residual = plan["loop"]?["residual_masking"] is JsonObject masking &&
            masking["status"]?.GetValue<string>() != "no_residual" ? masking : null;
        bool daytimeExport = DaytimeSplit.PrepareDynamicExport(original["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(SceneGraph.Id),
            plan, runtime["runtime_dependencies"]!.AsArray()) is not null;
        var (crossfade, particleWarmup, intro, residualGroups) = HybridBakeService.GroupTiming(plan, settings, residual, runtime, daytimeExport);
        return (new GroupRenderScheduler(runner, new HybridBakeRequest(2, plan, output), plan, settings, groups,
            captureProject, output, snapshot, frames, groupFrames, crossfade, particleWarmup + intro, residualGroups, 1,
            PlaybackEncoderSelection.Software, null, token), groupFrames, residualGroups);
    }

    internal static async Task<JsonArray> RunAsync(JsonObject plan, NativeTools tools, string output, CancellationToken token)
    {
        var records = new JsonArray();
        JsonObject[] groups = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()];
        // 特效前缀路线不烘视频组（只烘前缀缓存），没有要预检的。
        if (plan["route"]?.GetValue<string>() == "effect_prefix" || !groups.Any(group => GroupVerdicts.SlowDrift(plan, Layers(group)) is not null))
            return records;
        var runner = new NativeRenderRunner(tools);
        var (opened, groupFrames, residualGroups) = await OpenAsync(plan, groups, runner, output, token);
        await using var scheduler = opened;
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

    /// <summary>实测的时间窗（秒）：都量，读数取最大（长窗灵敏，短窗兜住位移超出线性区时的低估）。</summary>
    static readonly double[] Windows = [8, 4, 2, 1, 0.5];

    /// <summary>
    /// 振幅推不出的慢项超预算改速（evidence 带 <see cref="ShaderPeriodAnalysis.MeasuredNote"/>、首选候选里 |δ| 超过逐项预算）实测看不看得出。
    /// 看成品的人没有原作对照，能感知的是速度变化，所以量两版的速度差：这些项的补丁还原成原速（before）与改速后（after）
    /// 各只渲所有者层，从时间 0 起出第 0 帧和第 t 帧。两版第 0 帧必须逐位相同（同一状态出发；不同说明所有者层里有随机内容，读数不可信）。
    /// 读数（见 <see cref="MeasureAsync"/>，位移与亮度变化都算）按 1080p 口径，≤ 0.1 px/s（1.0.2 摆动慢项同一门限）才放行；渲染或准备失败记 not_measured（probe_failed），不抛出。
    /// 返回 null = 特效前缀路线，或首选候选里没有这类项；status 不是 passed 的，调用方按逐项预算重新分析。改速后的循环照常过接缝门。
    /// </summary>
    internal static async Task<JsonObject?> SpeedAsync(JsonObject plan, NativeTools tools, string output, CancellationToken token)
    {
        if (plan["route"]?.GetValue<string>() == "effect_prefix" || plan["loop"]?["candidates"] is not JsonArray { Count: > 0 } candidates) return null;
        double budget = plan["loop"]!["retime_budget_percent"]!.GetValue<double>();
        HashSet<string> measured = [.. (plan["loop"]!["evidence"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["detail"]?.GetValue<string>().EndsWith(ShaderPeriodAnalysis.MeasuredNote, StringComparison.Ordinal) == true)
            .Select(item => item["component"]!.GetValue<string>())];
        string[] relaxed = [.. candidates[0]!["components"]!.AsArray().OfType<JsonObject>()
            .Where(c => measured.Contains(c["id"]!.GetValue<string>()) && Math.Abs(c["delta_percent"]!.GetValue<double>()) > budget + 1e-9)
            .Select(c => c["id"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
        if (relaxed.Length == 0) return null;
        JsonObject before = plan.DeepClone().AsObject();
        JsonArray patches = before["loop"]!["candidates"]![0]!["patches"]!.AsArray();
        JsonObject[] retimed = [.. patches.OfType<JsonObject>().Where(patch => relaxed.Contains(patch["component"]?.GetValue<string>() ?? ""))];
        int[] owners = [.. retimed.Select(patch => patch["owner_layer_id"]!.GetValue<int>()).Distinct()];
        // 还原成原速：旋钮补丁新值 = (分量倍率 / 同 pass 时间倍率)^指数，原速对应 (1 / 时间倍率)^指数；为 1 就去掉这条补丁
        foreach (JsonObject patch in retimed)
        {
            double time = patches.OfType<JsonObject>().FirstOrDefault(other => other["constant_key"]?.GetValue<string>() == ShaderPeriodAnalysis.TimeScaleKey &&
                JsonNode.DeepEquals(other["owner_layer_id"], patch["owner_layer_id"]) && JsonNode.DeepEquals(other["effect_index"], patch["effect_index"]) &&
                JsonNode.DeepEquals(other["pass_index"], patch["pass_index"]))?["new_value"]?.GetValue<double>() ?? 1;
            double original = Math.Pow(1 / time, patch["speed_exponent"]!.GetValue<double>());
            if (original == 1) patches.Remove(patch);
            else patch["new_value"] = original;
        }
        JsonObject[] groups = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()];
        int[] probed = [.. Enumerable.Range(0, groups.Length).Where(index => Layers(groups[index]).Intersect(owners).Any())];
        var record = new JsonObject { ["components"] = new JsonArray([.. relaxed.Select(id => (JsonNode)id)]),
            ["owner_layer_ids"] = new JsonArray([.. owners.Select(id => (JsonNode)id)]),
            ["limit_px_per_second"] = SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond };
        // 所有者层不全在视频组里：量不到就不放行
        if (owners.Except(probed.SelectMany(index => Layers(groups[index]))).Any())
        {
            record["status"] = "not_measured";
            return record;
        }
        var runner = new NativeRenderRunner(tools);
        double wall = 0;
        var readings = new JsonArray();
        try
        {
            HybridAnalyzeRequest settings = PlanSettings.Of(plan);
            var (openedBefore, _, _) = await OpenAsync(before, groups, runner, Path.Combine(output, "before"), token);
            await using var schedulerBefore = openedBefore;
            var (openedAfter, _, _) = await OpenAsync(plan, groups, runner, Path.Combine(output, "after"), token);
            await using var schedulerAfter = openedAfter;
            foreach (int index in probed)
            {
                GroupCapture capture = schedulerAfter.Capture(groups[index]);
                int[] layers = [.. Layers(groups[index]).Intersect(owners)];
                async Task<byte[]> Render(bool after, ulong frame)
                {
                    GroupRenderScheduler scheduler = after ? schedulerAfter : schedulerBefore;
                    string name = $"speed-{index}-{frame}-{(after ? "after" : "before")}";
                    JsonObject manifest = await runner.RenderAsync(scheduler.SpeedProbeRequest(index, Path.Combine(output, name), frame, layers), null, token);
                    wall += manifest["native_result"]?["wall_seconds"]?.GetValue<double>() ?? 0;
                    return await LoopClosureCheck.ReadRetainedFrameAsync(manifest, 0, token);
                }
                JsonObject reading = await MeasureAsync(Render, (int)capture.PixelWidth, (int)capture.PixelHeight, schedulerAfter.TileScale,
                    settings.FpsNumerator, settings.FpsDenominator);
                reading["group_id"] = groups[index]["id"]!.DeepClone();
                readings.Add(reading);
            }
        }
        // 建采集工程、构造请求或渲染失败：记量不到（调用方按逐项预算重分析），不让整次分析失败；取消照常往外抛
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            readings.Add(new JsonObject { ["status"] = "not_measured", ["reason"] = "probe_failed",
                ["error_type"] = error.GetType().Name, ["message"] = error.Message });
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        JsonObject[] all = [.. readings.OfType<JsonObject>()];
        record["groups"] = readings;
        record["renderer_wall_seconds"] = wall;
        record["status"] = all.All(r => r["status"]!.GetValue<string>() == "passed") ? "passed"
            : all.Any(r => r["status"]!.GetValue<string>() == "visible") ? "visible" : "not_measured";
        return record;
    }

    /// <summary>
    /// 一个组的速度差读数。<paramref name="render"/>(after, frame) 出改速前/后从时间 0 起的第 frame 帧（RGBA）。
    /// 第 0 帧两版不逐位相同 → not_measured（frame0_differs）；第 0 帧整幅没有梯度 → not_measured（no_texture，所有者层可能没渲出来）。
    /// 否则比较两版在 t 时刻的画面差，同时抓位移和亮度变化（分块光流只量位移，亮度类特效怎么改速都会"通过"）：
    /// 约 540p 网格（1080p 下 2×2 平均）上逐格取 d = max_c |after_c − before_c|、g = max_c |∇before_c|（c 取 RGBA，梯度按 1080p 像素），
    /// 块边 16 格（1080p 的 32 px）内求平均 D、G，读数 v = D / (t·G)，单位 px/s。
    /// 纯位移 s 时每个通道 |Δc| ≈ |∇c·s| ≤ |∇c|·|s| ≤ g·|s|，所以 D ≤ G·|s|、v ≤ |s|/t = 位移速度差：速度差 ≤ 0.1 px/s 的纯位移必然通过，
    /// 与原来的位移判据同一口径；亮度变化在 G ≈ 0 的平坦块里直接让 v 变大（G = 0 而 D > 0 记无穷）。
    /// 5 个时间窗都量，读数取各块、各窗的最大值（长窗的位移可能超出线性区而低估，短窗兜住），≤ 0.1 px/s 为 passed，否则 visible。
    /// </summary>
    internal static async Task<JsonObject> MeasureAsync(Func<bool, ulong, Task<byte[]>> render, int width, int height, double tileScale,
        uint fpsNumerator, uint fpsDenominator)
    {
        byte[] origin = await render(false, 0), retimed = await render(true, 0);
        if (!origin.AsSpan().SequenceEqual(retimed))
            return new JsonObject { ["status"] = "not_measured", ["reason"] = "frame0_differs" };
        int step = Math.Max(1, (int)Math.Round(2 * tileScale));
        double toReference = step / tileScale;
        if (Blocks(origin, origin, width, height, step, toReference).All(block => block.Gradient == 0))
            return new JsonObject { ["status"] = "not_measured", ["reason"] = "no_texture" };
        double peak = 0, worstWindow = 0;
        foreach (double seconds in Windows)
        {
            ulong frame = (ulong)Math.Round(seconds * fpsNumerator / fpsDenominator);
            foreach (var (difference, gradient) in Blocks(await render(false, frame), await render(true, frame), width, height, step, toReference))
            {
                double reading = difference == 0 ? 0 : gradient == 0 ? double.PositiveInfinity : difference / (seconds * gradient);
                if (reading > peak) (peak, worstWindow) = (reading, seconds);
            }
        }
        return new JsonObject { ["status"] = peak <= SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond ? "passed" : "visible",
            // 平坦块里有变化时读数是无穷，JSON 里记 null 并标 flat_block_changed
            ["peak_speed_deviation_px_per_second"] = double.IsFinite(peak) ? peak : null, ["flat_block_changed"] = !double.IsFinite(peak),
            ["worst_window_seconds"] = worstWindow };
    }

    /// <summary>
    /// 两帧先按 step×step 平均到网格，逐块（16×16 格，去掉最外一圈格子）给出平均差 D（levels）与改速前那帧的平均梯度幅值 G（levels / 1080p 像素）；
    /// 每格取 RGBA 四个通道里最大的差与最大的梯度幅值（中心差分）。
    /// </summary>
    static IEnumerable<(double Difference, double Gradient)> Blocks(byte[] before, byte[] after, int width, int height, int step, double toReference)
    {
        int w = width / step, h = height / step, size = 16;
        float[][] Grid(byte[] rgba)
        {
            var grid = new float[4][];
            for (int c = 0; c < 4; c++) grid[c] = new float[w * h];
            for (int y = 0; y < h * step; y++)
                for (int x = 0; x < w * step; x++)
                    for (int c = 0; c < 4; c++)
                        grid[c][y / step * w + x / step] += rgba[(y * width + x) * 4 + c] / (float)(step * step);
            return grid;
        }
        float[][] a = Grid(before), b = Grid(after);
        for (int top = 1; top + size < h; top += size)
            for (int left = 1; left + size < w; left += size)
            {
                double difference = 0, gradient = 0;
                for (int y = top; y < top + size; y++)
                    for (int x = left; x < left + size; x++)
                    {
                        int i = y * w + x;
                        double d = 0, g = 0;
                        for (int c = 0; c < 4; c++)
                        {
                            float[] ac = a[c];
                            double gx = (ac[i + 1] - ac[i - 1]) / 2.0, gy = (ac[i + w] - ac[i - w]) / 2.0;
                            d = Math.Max(d, Math.Abs(b[c][i] - ac[i]));
                            g = Math.Max(g, Math.Sqrt(gx * gx + gy * gy));
                        }
                        difference += d;
                        gradient += g;
                    }
                yield return (difference / (size * size), gradient / (size * size) / toReference);
            }
    }
}
