using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 分析阶段的烘焙预检：按烘焙同一个主渲染请求（同一份预热、同一个残差起点）渲出烘焙门要看的那几帧，在分析里就用烘焙的阈值判掉，
/// 烘焙不再为这些情况退回重烘。<see cref="RunAsync"/> 每组一条记录（kind 区分）：
/// <list type="bullet">
/// <item>slow_closure：视频组里有缓变分量（plan 的 slow_components）所有者层时，一次渲染留第 0 帧与第 P 帧，
/// 在编码前原帧上过 <see cref="LoopClosureCheck"/>（阈值不动）。P 是这组录的帧数（按自身周期录的组就是 P_g，与烘焙接缝门同一对帧），不搜。
/// 没闭合的，调用方把所有者层留实时重新分析。没有缓变分量的组按解析周期精确闭合，不渲。</item>
/// <item>residual_start_search：慢分量预检本来就跑的起点搜索（有残差组时它定所有组的预热）顺带判含可掩盖粒子的残差组
/// （<see cref="ResidualRecords"/>，不另起渲染）；搜索证出烘焙第一层必拒的，调用方把组里的粒子留实时重新分析。</item>
/// <item>video_streams：没证出静态的组数越过路数上限时证出哪些组是动态的（<see cref="StreamsAsync"/>，限时），证出越线时点名退回的组。</item>
/// </list>
/// 过了的照常判能，烘焙时各道门照常复核。
/// </summary>
internal static class SlowClosureProbe
{
    /// <summary>同时在飞的预检渲染数，与烘焙 GPU 路线的组并行默认值相同。</summary>
    const int Parallel = 3;

    static int[] Layers(JsonObject group) => [.. group["layer_ids"]!.AsArray().Select(SceneGraph.Int).OfType<int>()];

    /// <summary>分析写下的残差分类（烘焙准入门同一判定，能生成的 plan 不会是拒绝态）；没有残差时 null。</summary>
    static JsonObject? Masking(JsonObject plan) => plan["loop"]?["residual_masking"] is JsonObject masking &&
        masking["status"]?.GetValue<string>() != "no_residual" ? masking : null;

    /// <summary>按 plan 建采集工程与组渲染调度（与烘焙同一个主渲染请求、同一份预热与残差起点）；返回调度器、各组循环帧数、残差组与写出的覆盖 shader 记录。</summary>
    static async Task<(GroupRenderScheduler Scheduler, ulong[] GroupFrames, int[] ResidualGroups, JsonArray Shaders)> OpenAsync(JsonObject plan, JsonObject[] groups,
        NativeRenderRunner runner, string output, CancellationToken token)
    {
        using var source = new ProjectSource(plan["source"]!.GetValue<string>());
        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
        JsonObject original = source.ReadJson(source.SceneResource), snapshot = plan["snapshot_properties"]!.DeepClone().AsObject();
        PlanTransforms.ApplyAudioEffectChoice(original, plan);
        string captureProject = Path.Combine(output, "capture-source");
        var report = new JsonObject();
        await CaptureSourceBuilder.PrepareAsync(captureProject, source, original,
            source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject(), snapshot, plan, settings, false,
            report, token);
        JsonObject candidate = plan["loop"]!["candidates"]![0]!.AsObject();
        ulong frames = candidate["frames"]!.GetValue<ulong>();
        ulong[] groupFrames = [.. groups.Select(group => candidate["group_frames"]?[group["id"]!.GetValue<string>()]?.GetValue<ulong>() ?? frames)];
        // 与烘焙同一份预热（粒子预热 + 入场帧）与残差起点：不带入场帧时，有入场动画的组第 0 帧还在入场里，必然判没闭合。
        var runtime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), token))!.AsObject();
        JsonObject? residual = Masking(plan);
        bool daytimeExport = DaytimeSplit.PrepareDynamicExport(original["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(SceneGraph.Id),
            plan, runtime["runtime_dependencies"]!.AsArray()) is not null;
        var (crossfade, particleWarmup, intro, residualGroups) = HybridBakeService.GroupTiming(plan, settings, residual, runtime, daytimeExport);
        return (new GroupRenderScheduler(runner, new HybridBakeRequest(2, plan, output), plan, settings, groups,
            captureProject, output, snapshot, frames, groupFrames, crossfade, particleWarmup + intro, residualGroups, 1,
            PlaybackEncoderSelection.Software, null, token), groupFrames, residualGroups, report["time_scale_shaders"] as JsonArray ?? []);
    }

    /// <summary>
    /// 一轮预检。要查哪些只看 plan：有缓变分量的组（同 main 的慢分量预检，起点搜索也只在它本来就要跑时跑：有残差组，它定这些组的预热），
    /// 或选中方案没证出静态的组数越过路数上限（<see cref="StreamLimit"/>）。都不用查时不碰源与渲染器。
    /// 残差判定只读起点搜索的结果，不为残差另起渲染。动态组判定是 main 没有的渲染，整段（含只为它建采集工程）限在 <paramref name="streamBudget"/> 内，
    /// 实际墙钟记在 video_streams 记录的 stream_elapsed_seconds，调用方从剩余预算里扣。
    /// </summary>
    internal static async Task<JsonArray> RunAsync(JsonObject plan, NativeTools tools, string output, TimeSpan streamBudget, CancellationToken token)
    {
        var records = new JsonArray();
        JsonObject[] groups = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()];
        // 特效前缀路线不烘视频组（只烘前缀缓存），没有要预检的。
        if (plan["route"]?.GetValue<string>() == "effect_prefix" || groups.Length == 0) return records;
        int[] probed = [.. Enumerable.Range(0, groups.Length).Where(index => GroupVerdicts.SlowDrift(plan, Layers(groups[index])) is not null)];
        int? limit = StreamLimit(plan);
        if (probed.Length == 0 && limit is null) return records;
        var runner = new NativeRenderRunner(tools);
        GroupRenderScheduler? scheduler = null;
        JsonObject?[]? results = null;
        try
        {
            if (probed.Length > 0)
            {
                var (opened, groupFrames, residualGroups, _) = await OpenAsync(plan, groups, runner, output, token);
                scheduler = opened;
                JsonObject? search = residualGroups.Length > 0 ? await LoopStartSelector.SearchAsync(runner, scheduler, Parallel, null, new StageTiming(), token) : null;
                // 每组一次渲染同时留第 0 与第 P 帧；各组互不依赖，最多 Parallel 组同时渲，记录仍按组序。
                results = new JsonObject[probed.Length];
                await System.Threading.Tasks.Parallel.ForEachAsync(Enumerable.Range(0, probed.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = token }, async (slot, cancel) =>
                    {
                        int index = probed[slot];
                        var (degrees, owners) = GroupVerdicts.SlowDrift(plan, Layers(groups[index]))!.Value;
                        GroupCapture capture = scheduler.Capture(groups[index]);
                        ulong period = groupFrames[index];
                        JsonObject render = await runner.RenderAsync(scheduler.ClosureProbeRequest(index, Path.Combine(output, $"group-{index}")), null, cancel);
                        byte[] first = await LoopClosureCheck.ReadRetainedFrameAsync(render, 0, cancel);
                        byte[] wrap = await LoopClosureCheck.ReadRetainedFrameAsync(render, period, cancel);
                        JsonObject closure = LoopClosureCheck.Evaluate(first, wrap, (int)capture.PixelWidth, (int)capture.PixelHeight,
                            withAlpha: !capture.SceneClear, period, judged: true, scheduler.TileScale);
                        results[slot] = new JsonObject { ["kind"] = "slow_closure", ["group_id"] = groups[index]["id"]!.DeepClone(),
                            ["owner_layer_ids"] = new JsonArray([.. owners.Select(id => (JsonNode)id)]), ["group_frames"] = period,
                            ["drift_bound_degrees"] = degrees, ["status"] = closure["status"]!.DeepClone(),
                            ["renderer_wall_seconds"] = render["native_result"]?["wall_seconds"]?.GetValue<double>() ?? 0,
                            ["loop_closure"] = closure };
                    });
                foreach (JsonObject record in results!) records.Add(record);
                if (search is not null)
                    foreach (JsonObject record in ResidualRecords(plan, groups, residualGroups, search)) records.Add(record);
            }
            if (limit is int streamLimit)
            {
                long started = Stopwatch.GetTimestamp();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                JsonObject record;
                try
                {
                    if (streamBudget <= TimeSpan.Zero) throw new OperationCanceledException();
                    budget.CancelAfter(streamBudget);
                    scheduler ??= (await OpenAsync(plan, groups, runner, output, budget.Token)).Scheduler;
                    record = await StreamsAsync(plan, groups, runner, scheduler, streamLimit, output, budget.Token);
                }
                // 预算用完：已证出的动态组照旧算（写在组上），没证出的只是没证出，不当拒因；越线与否按已证出的判。
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    record = await StreamRecordAsync(plan, groups, streamLimit, 0, "budget_exhausted");
                }
                record["budget_seconds"] = Math.Max(0, streamBudget.TotalSeconds);
                record["stream_elapsed_seconds"] = Stopwatch.GetElapsedTime(started).TotalSeconds;
                records.Add(record);
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            records.Clear();
            for (int slot = 0; slot < probed.Length; ++slot)
            {
                var (degrees, owners) = GroupVerdicts.SlowDrift(plan, Layers(groups[probed[slot]]))!.Value;
                records.Add(results?[slot] ?? new JsonObject { ["kind"] = "slow_closure", ["group_id"] = groups[probed[slot]]["id"]!.DeepClone(),
                    ["owner_layer_ids"] = new JsonArray([.. owners.Select(id => (JsonNode)id)]), ["drift_bound_degrees"] = degrees,
                    ["status"] = "probe_failed", ["error_type"] = error.GetType().Name, ["message"] = error.Message });
            }
        }
        finally
        {
            if (scheduler is not null) await scheduler.DisposeAsync();
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return records;
    }

    /// <summary>
    /// 含可掩盖粒子的残差组在起点搜索（<see cref="LoopStartSelector.SearchAsync"/>）里的读数：没有一个起点过整幅 Δ_0 限（2/255），
    /// 且这组在选定起点的样本整幅超限——整幅均值在降采样样本上与全分辨率几乎相同（语料里差不到 0.01/255），烘焙全分辨率第一层必拒，
    /// 这就是证明（status = rejected_residual_above_limits）；调用方把组里的粒子留实时。其余只记读数，瓦片量要全分辨率才判得了，留给烘焙第一层。
    /// 只用已经跑过的搜索，不另起渲染。
    /// </summary>
    internal static IEnumerable<JsonObject> ResidualRecords(JsonObject plan, JsonObject[] groups, int[] residualGroups, JsonObject search)
    {
        HashSet<int> particles = [.. (Masking(plan)?["residual_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["mechanism"]?.GetValue<string>() == "particle_system").Select(item => SceneGraph.Int(item["owner_layer_id"])).OfType<int>()];
        var rows = search["groups"] is JsonArray combined
            ? combined.OfType<JsonObject>().ToDictionary(g => g["group_id"]!.GetValue<string>(), g => g["at_shared_start"] as JsonObject)
            : new Dictionary<string, JsonObject?> { [search["group_id"]?.GetValue<string>() ?? ""] = search["selected"] as JsonObject };
        bool noneWithin = search["candidates_within_global_limit"]?.GetValue<int>() == 0;
        foreach (int index in residualGroups)
        {
            string id = groups[index]["id"]!.GetValue<string>();
            int[] owned = [.. Layers(groups[index]).Where(particles.Contains)];
            if (owned.Length == 0 || !rows.TryGetValue(id, out JsonObject? row) || row?["sampled_global_rgb_mae_255"] is not JsonValue global) continue;
            bool rejected = noneWithin && global.GetValue<double>() > ResidualMasking.MaximumSeamRgbMae255;
            yield return new JsonObject { ["kind"] = "residual_start_search", ["group_id"] = id,
                ["particle_layer_ids"] = new JsonArray([.. owned.Select(layer => (JsonNode)layer)]),
                ["start_frame"] = search["selected_start_frame"]?.DeepClone(), ["candidates_within_global_limit"] = search["candidates_within_global_limit"]?.DeepClone(),
                ["sampled_global_rgb_mae_255"] = global.DeepClone(), ["limit_global_rgb_mae_255"] = ResidualMasking.MaximumSeamRgbMae255,
                ["status"] = rejected ? "rejected_residual_above_limits" : "first_layer_checked_at_bake" };
        }
    }

    /// <summary>
    /// 要不要做动态组判定：整层、可烘、循环多于 1 帧，且没证出静态的组数（<see cref="Admission.GroupCount"/>，上界）越过路数上限
    /// （默认 <see cref="NoBenefit.SavingProvenStreams"/>，显式允许不省电时取 <see cref="Admission.MaxVideoGroups"/>）时返回上限，否则 null。
    /// </summary>
    internal static int? StreamLimit(JsonObject plan)
    {
        if (plan["route"]?.GetValue<string>() != "whole_layer" || !Admission.Bakeable(plan) || plan["video_groups"] is not JsonArray ||
            !(StaticOnlyBake.Count((plan["loop"]?["candidates"] as JsonArray)?.FirstOrDefault()?["frames"]) > 1)) return null;
        int limit = NoBenefit.Allowed(plan) ? Admission.MaxVideoGroups(plan) : NoBenefit.SavingProvenStreams;
        return Admission.GroupCount(plan) > limit ? limit : null;
    }

    /// <summary>
    /// 动态组判定：没证出静态、也还没证出动态的组，按烘焙同一个主渲染请求出录制区间开头 16 帧的降采样样本（<see cref="GroupRenderScheduler.StreamProbeRequest"/>），
    /// 样本有不同即原帧不同，证出动态（组上写 dynamic_verified）；全同只是没证出，不当拒因。渲染失败的组照旧没证出，取消（含预算用完）照常往外抛，
    /// 已证出的留在组上。记录见 <see cref="StreamRecordAsync"/>。
    /// </summary>
    static async Task<JsonObject> StreamsAsync(JsonObject plan, JsonObject[] groups, NativeRenderRunner runner, GroupRenderScheduler scheduler, int limit,
        string output, CancellationToken token)
    {
        int[] unknown = [.. Enumerable.Range(0, groups.Length).Where(index => !Admission.StaticVerified(groups[index]) &&
            groups[index]["dynamic_verified"]?.GetValue<bool>() != true)];
        double wall = 0;
        await System.Threading.Tasks.Parallel.ForEachAsync(Enumerable.Range(0, unknown.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = token }, async (slot, cancel) =>
            {
                try
                {
                    JsonObject manifest = await runner.RenderAsync(scheduler.StreamProbeRequest(unknown[slot], Path.Combine(output, $"stream-{unknown[slot]}")),
                        null, cancel);
                    bool changed = await SamplesDifferAsync(manifest, cancel);
                    lock (groups)
                    {
                        wall += manifest["native_result"]?["wall_seconds"]?.GetValue<double>() ?? 0;
                        if (changed) groups[unknown[slot]]["dynamic_verified"] = true;
                    }
                }
                catch (Exception error) when (!cancel.IsCancellationRequested && error is not OperationCanceledException) { }
            });
        return await StreamRecordAsync(plan, groups, limit, wall, "measured");
    }

    /// <summary>
    /// 动态组判定的记录：上限、上界、已证动态的组、没证出的组。证出的动态组越线时，省下特效渲染（<see cref="BakeValueAssessment.PassCoverage"/>）
    /// 最少的那几组（越线几组就几组）的根记进 retreat_root_ids，调用方一次留实时重新分析。
    /// </summary>
    static async Task<JsonObject> StreamRecordAsync(JsonObject plan, JsonObject[] groups, int limit, double wall, string status)
    {
        int dynamic = Admission.DynamicGroupCount(plan);
        var record = new JsonObject { ["kind"] = "video_streams", ["status"] = status, ["limit"] = limit, ["upper_bound"] = Admission.GroupCount(plan),
            ["dynamic_groups"] = dynamic, ["dynamic_group_ids"] = new JsonArray([.. groups.Where(g => g["dynamic_verified"]?.GetValue<bool>() == true)
                .Select(g => g["id"]!.DeepClone())]),
            ["unproven_group_ids"] = new JsonArray([.. groups.Where(g => !Admission.StaticVerified(g) && g["dynamic_verified"]?.GetValue<bool>() != true)
                .Select(g => g["id"]!.DeepClone())]), ["renderer_wall_seconds"] = wall,
            ["basis"] = "Groups not proven static were rendered at the bake's master warmup; differing downsampled samples in the first 16 frames of the recorded interval prove a video stream. Unproven groups are not a reason to reject." };
        if (dynamic > limit && await RetreatGroupsAsync(plan, dynamic - limit) is { Length: > 0 } retreat)
        {
            record["retreat_group_ids"] = new JsonArray([.. retreat.Select(g => g["id"]!.DeepClone())]);
            int[] kept = [.. (plan["settings"]?["retain_live_root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>()];
            record["retreat_root_ids"] = new JsonArray([.. retreat.SelectMany(g => (g["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>())
                .Except(kept).Distinct().Select(id => (JsonNode)id)]);
            record["retreat_layer_ids"] = new JsonArray([.. retreat.SelectMany(Layers).Distinct().Select(id => (JsonNode)id)]);
        }
        return record;
    }

    /// <summary>已证动态的组里省下特效渲染最少的 <paramref name="count"/> 组（还有没留实时的根的）。</summary>
    static async Task<JsonObject[]> RetreatGroupsAsync(JsonObject plan, int count)
    {
        if (plan["runtime_evidence"]?.GetValue<string>() is not string path || !File.Exists(path)) return [];
        var observed = (JsonNode.Parse(await File.ReadAllTextAsync(path))?["runtime_layers"] as JsonArray ?? [])
            .OfType<JsonObject>().ToLookup(layer => SceneGraph.Int(layer["owner"]));
        HashSet<int> kept = [.. (plan["settings"]?["retain_live_root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>()];
        return [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>().Where(group => group["dynamic_verified"]?.GetValue<bool>() == true &&
                (group["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>().Any(root => !kept.Contains(root)))
            .OrderBy(group => BakeValueAssessment.PassCoverage(plan, Layers(group).Distinct().SelectMany(id => observed[id]))).Take(count)];
    }

    /// <summary>降采样样本里有没有和第一个不同的（样本不同则原帧不同）。</summary>
    internal static async Task<bool> SamplesDifferAsync(JsonObject manifest, CancellationToken token)
    {
        JsonObject samples = manifest["frame_samples"]!.AsObject();
        int frameBytes = checked(samples["width"]!.GetValue<int>() * samples["height"]!.GetValue<int>() * 3);
        ulong count = samples["count"]!.GetValue<ulong>();
        await using var stream = File.OpenRead(samples["path"]!.GetValue<string>());
        if (stream.Length != (long)count * frameBytes) throw new InvalidDataException("动态判定的样本文件长度与清单不一致。");
        byte[] first = new byte[frameBytes], next = new byte[frameBytes];
        await stream.ReadExactlyAsync(first, token);
        for (ulong index = 1; index < count; ++index)
        {
            await stream.ReadExactlyAsync(next, token);
            if (!first.AsSpan().SequenceEqual(next)) return true;
        }
        return false;
    }

    /// <summary>最短时间窗（秒）：从它起每次翻倍，直到循环长度。</summary>
    const double ShortestWindowSeconds = 0.5;
    /// <summary>画面差可测的下限（levels）：最差块的平均差 D 达到它的第一个窗才算速率；更小的差可能只是 8 位量化抹掉了。</summary>
    internal const double MeasurableLevels = 3;

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
            .Select(c => c["id"]!.GetValue<string>())
            .Concat(candidates[0]!["patches"]!.AsArray().OfType<JsonObject>()
                .Where(patch => patch["kind"]?.GetValue<string>() == LoopAnalysis.ParticleFieldPatchKind)
                .Select(patch => patch["component"]!.GetValue<string>()))
            .Distinct().Order(StringComparer.Ordinal)];
        if (relaxed.Length == 0) return null;
        static bool Retimed(JsonNode? patch, string[] relaxed) => relaxed.Contains(patch?["component"]?.GetValue<string>() ?? "");
        // 这些项按原速的 speed 倍跑的一版：旋钮补丁新值 = (分量倍率 / 同 pass 时间倍率)^指数，所以 speed 倍对应 (speed / 时间倍率)^指数；为 1 就去掉这条补丁。
        // speed = 1 是改速前（before）；speed = 2 是对照（control）：它与改速前也分不出时，这些项在画面上本来就不起作用
        JsonObject Variant(double speed)
        {
            JsonObject variant = plan.DeepClone().AsObject();
            JsonArray patches = variant["loop"]!["candidates"]![0]!["patches"]!.AsArray();
            foreach (JsonObject patch in patches.OfType<JsonObject>().Where(patch => Retimed(patch, relaxed)).ToArray())
            {
                double time = patches.OfType<JsonObject>().FirstOrDefault(other => other["constant_key"]?.GetValue<string>() == ShaderPeriodAnalysis.TimeScaleKey &&
                    JsonNode.DeepEquals(other["owner_layer_id"], patch["owner_layer_id"]) && JsonNode.DeepEquals(other["effect_index"], patch["effect_index"]) &&
                    JsonNode.DeepEquals(other["pass_index"], patch["pass_index"]))?["new_value"]?.GetValue<double>() ?? 1;
                double value = Math.Pow(speed / time, patch["speed_exponent"]!.GetValue<double>());
                if (value == 1) patches.Remove(patch);
                else patch["new_value"] = value;
            }
            return variant;
        }
        JsonObject[] retimed = [.. candidates[0]!["patches"]!.AsArray().OfType<JsonObject>().Where(patch => Retimed(patch, relaxed))];
        int[] owners = [.. retimed.Select(patch => patch["owner_layer_id"]!.GetValue<int>()).Distinct()];
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
        GroupRenderScheduler? schedulerControl = null;
        try
        {
            HybridAnalyzeRequest settings = PlanSettings.Of(plan);
            var (openedBefore, _, _, shadersBefore) = await OpenAsync(Variant(1), groups, runner, Path.Combine(output, "before"), token);
            await using var schedulerBefore = openedBefore;
            var (openedAfter, loopFrames, _, shadersAfter) = await OpenAsync(plan, groups, runner, Path.Combine(output, "after"), token);
            await using var schedulerAfter = openedAfter;
            record["time_scale_shaders"] = new JsonObject { ["before"] = shadersBefore.DeepClone(), ["after"] = shadersAfter.DeepClone() };
            // 改速后的采集工程里，每条改速补丁的键都要真的改写进了覆盖 shader（写覆盖时读不到源码或解析不出 pass 着色器会静默跳过）：缺了就是没改速，不必渲染
            if (UnwrittenKeys(shadersAfter, retimed.Select(patch => patch["constant_key"]!.GetValue<string>())) is { Length: > 0 } missing)
            {
                readings.Add(new JsonObject { ["status"] = "not_measured", ["reason"] = "patch_not_written",
                    ["missing_keys"] = new JsonArray([.. missing.Select(key => (JsonNode)key)]) });
                probed = [];
            }
            foreach (int index in probed)
            {
                GroupCapture capture = schedulerAfter.Capture(groups[index]);
                int[] layers = [.. Layers(groups[index]).Intersect(owners)];
                async Task<byte[]> Render(int version, ulong frame)
                {
                    if (version == 2 && schedulerControl is null)
                        (schedulerControl, _, _, _) = await OpenAsync(Variant(2), groups, runner, Path.Combine(output, "control"), token);
                    GroupRenderScheduler scheduler = version switch { 0 => schedulerBefore, 1 => schedulerAfter, _ => schedulerControl! };
                    string name = $"speed-{index}-{frame}-{version switch { 0 => "before", 1 => "after", _ => "control" }}";
                    JsonObject manifest = await runner.RenderAsync(scheduler.SpeedProbeRequest(index, Path.Combine(output, name), frame, layers), null, token);
                    wall += manifest["native_result"]?["wall_seconds"]?.GetValue<double>() ?? 0;
                    return await LoopClosureCheck.ReadRetainedFrameAsync(manifest, 0, token);
                }
                JsonObject reading = await MeasureAsync(Render, (int)capture.PixelWidth, (int)capture.PixelHeight, schedulerAfter.TileScale,
                    settings.FpsNumerator, settings.FpsDenominator, loopFrames[index]);
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
            if (schedulerControl is not null) await schedulerControl.DisposeAsync();
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
    /// 一个组的速度差读数。<paramref name="render"/>(version, frame) 出从时间 0 起的第 frame 帧（RGBA）：version 0 改速前、1 改速后、2 对照（改速项按原速 2 倍）；<paramref name="loopFrames"/> 是这个组的循环长度 L（帧）。
    /// 第 0 帧两版不逐位相同 → not_measured（frame0_differs）；第 0 帧整幅没有梯度 → not_measured（no_texture，所有者层可能没渲出来）。
    /// 否则比较两版在 t 时刻的画面，同时抓位移和亮度变化（分块光流只量位移，亮度类特效怎么改速都会"通过"）：
    /// 约 540p 网格（1080p 下 2×2 平均）上逐格取 d = max_c |after_c − before_c|、g = max_c |∇before_c|（c 取 RGBA，梯度按 1080p 像素），
    /// 块边 16 格（1080p 的 32 px）内求平均 D、G，读数 v = D / (t·G)，单位 px/s；G = 0 而 D > 0 记无穷。
    /// 纯位移 s 时每个通道 |Δc| ≈ |∇c·s| ≤ |∇c|·|s| ≤ g·|s|，所以 D ≤ G·|s|、v ≤ |s|/t = 位移速度差：速度差 ≤ 0.1 px/s 的纯位移必然通过。
    /// 窗口 t 从 0.5 s 起翻倍直到 L（最后一窗取 L），取第一个最差块 D ≥ <see cref="MeasurableLevels"/> 的窗算读数，≤ 0.1 px/s 为 passed，否则 visible：
    /// 更短的窗里差可能被 8 位量化抹成 0（周期几百秒的项改速几十个百分点，8 s 里相位差不到 1%），更长的窗里图案去相关、D 饱和会低估速率，所以只取第一个可测窗。
    /// 到 t = L 两版的差都不可测时自检：对照版与改速前在同样的窗上比（最大块差记 control_change_levels；原速版本自身的变化另记 self_change_levels，只作参考）。
    /// 对照也不可测 → 改速项在所有者层画面上不起作用，改速无害，按 component_not_visible 放行（读数写 null）；
    /// 对照可测、改速后却不可测 → 补丁没生效或渲染有误，记 not_measured（patch_not_effective），调用方按逐项预算重分析。
    /// </summary>
    internal static async Task<JsonObject> MeasureAsync(Func<int, ulong, Task<byte[]>> render, int width, int height, double tileScale,
        uint fpsNumerator, uint fpsDenominator, ulong loopFrames)
    {
        byte[] origin = await render(0, 0), retimed = await render(1, 0);
        if (!origin.AsSpan().SequenceEqual(retimed))
            return new JsonObject { ["status"] = "not_measured", ["reason"] = "frame0_differs" };
        int step = Math.Max(1, (int)Math.Round(2 * tileScale));
        double toReference = step / tileScale;
        if (Blocks(origin, origin, width, height, step, toReference).All(block => block.Gradient == 0))
            return new JsonObject { ["status"] = "not_measured", ["reason"] = "no_texture" };
        double largest = 0, motion = 0;
        var frames = new List<ulong>();
        for (double seconds = ShortestWindowSeconds; ; seconds *= 2)
        {
            ulong frame = Math.Min(loopFrames, Math.Max(1, (ulong)Math.Round(seconds * fpsNumerator / fpsDenominator)));
            frames.Add(frame);
            double t = (double)frame * fpsDenominator / fpsNumerator;
            byte[] original = await render(0, frame);
            var blocks = Blocks(original, await render(1, frame), width, height, step, toReference).ToArray();
            double worst = blocks.Max(block => block.Difference);
            largest = Math.Max(largest, worst);
            motion = Math.Max(motion, Blocks(origin, original, width, height, step, toReference).Max(block => block.Difference));
            if (worst >= MeasurableLevels)
            {
                double peak = blocks.Max(block => block.Difference == 0 ? 0 : block.Gradient == 0 ? double.PositiveInfinity : block.Difference / (t * block.Gradient));
                return new JsonObject { ["status"] = peak <= SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond ? "passed" : "visible",
                    ["basis"] = "first_measurable_window",
                    // 平坦块里有变化时读数是无穷，JSON 里记 null 并标 flat_block_changed
                    ["peak_speed_deviation_px_per_second"] = double.IsFinite(peak) ? peak : null, ["flat_block_changed"] = !double.IsFinite(peak),
                    ["worst_window_seconds"] = t, ["worst_block_difference_levels"] = worst };
            }
            if (frame >= loopFrames)
            {
                // 对照：这些项按原速 2 倍跑的一版与改速前在同样的窗上比。原速版本自己在动（self_change）不够说明问题——动的可能是别的项；
                // 对照也分不出 → 这些项在所有者层画面上本来就不起作用；对照分得出、改速后却分不出 → 补丁没生效或渲染有误
                double control = 0;
                foreach (ulong at in frames)
                    if ((control = Math.Max(control, Blocks(await render(0, at), await render(2, at), width, height, step, toReference)
                        .Max(block => block.Difference))) >= MeasurableLevels) break;
                return control < MeasurableLevels
                    ? new JsonObject { ["status"] = "passed", ["basis"] = "component_not_visible", ["peak_speed_deviation_px_per_second"] = null,
                        ["worst_window_seconds"] = t, ["worst_block_difference_levels"] = largest, ["self_change_levels"] = motion, ["control_change_levels"] = control }
                    : new JsonObject { ["status"] = "not_measured", ["reason"] = "patch_not_effective",
                        ["worst_window_seconds"] = t, ["worst_block_difference_levels"] = largest, ["self_change_levels"] = motion, ["control_change_levels"] = control };
            }
        }
    }

    /// <summary>改速补丁的键里，没有出现在任何覆盖 shader 记录（<see cref="ShaderTextPatch.WriteTimeScaleAsync"/> 的 keys）里的那些。</summary>
    internal static string[] UnwrittenKeys(JsonArray shaders, IEnumerable<string> keys) =>
        [.. keys.Distinct().Except(shaders.OfType<JsonObject>().SelectMany(shader => (shader["keys"] as JsonArray ?? []).Select(key => key!.GetValue<string>())))
            .Order(StringComparer.Ordinal)];

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
