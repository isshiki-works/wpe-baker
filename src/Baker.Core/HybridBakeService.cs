using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record HybridBakeRequest(int SchemaVersion, JsonObject Plan, string OutputDirectory,
    ulong ProbeFrames = 0, string? DeviceUuid = null, string? ProjectDirectory = null,
    // 播放版编码档位：software（默认）/ vulkan / nvenc / qsv / amf / auto；Vulkan 可直接生成成品。
    string? PlaybackEncoder = null,
    // 成品编码的跨进程槽位配额：0 = 不限。多槽并行跑批时用它压住 ffmpeg 抢核，渲染不受限制。
    int EncodeSlots = 0,
    // 单案内同时在飞的组主渲染数：1 = 与逐组串行完全一致。组的判定、编码与写入始终按组序串行。
    int GroupParallel = 1,
    // 开发用：保留中间产物（capture-source、各组 master、合成探针与参照、分析刷新目录）。
    // 默认 false —— 正常结束、拒绝与失败都会删掉它们，只留成品工程、bake.json、失败诊断与日志。
    // 开启时还记录编码接缝差分并导出成功任务的接缝预览。
    bool KeepIntermediates = false, double EffectRenderScale = 1.0, bool MatchEffectResolution = false);

/// <summary>Replaces rendered scene groups with videos and retains the original live hierarchies.</summary>
/// <summary>1 帧、0 个视频层的结果：烘完等于一张静态图加全部实时，不省电，所以不算成品（fix/verdict-flow）。</summary>
public static class StaticOnlyBake
{
    public const string Status = "static_only";

    /// <summary>这份烘焙结果是不是"只剩一张静态图"：帧数 ≤ 1 且一个视频层都没有。</summary>
    public static bool Is(JsonObject report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Count(report["video_layers"]) == 0 && Count(report["frames"]) is double frames && frames <= 1;
    }

    /// <summary>报告里的计数：int / long / ulong / JSON 数字都按同一条路读，读不到就是 null。</summary>
    private static double? Count(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out double number) && double.IsFinite(number)) return number;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out ulong unsigned)) return unsigned;
        if (value.TryGetValue(out int small)) return small;
        return null;
    }

    /// <summary>成品与"只剩一张静态图"都算烘完了：工程照样写出去，只是结论分开说。</summary>
    public static bool Finished(string? status) => status is "candidate_generated" or Status;
}

public sealed class HybridBakeService(NativeTools tools)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static JsonArray? SourceScriptErrors(JsonObject evidence)
    {
        if (evidence["source_script_error_count"] is JsonValue count && count.TryGetValue<int>(out int errorCount) &&
            errorCount >= 0 && evidence["source_script_errors"] is JsonArray errors && errors.Count == errorCount &&
            errors.All(error => error is JsonObject)) return errors;
        return null;
    }

    /// <summary>
    /// 这个视频组的成品能不能在渲染那一遍直接编出来，不写无损 master。三个条件都是必要的：
    /// <list type="bullet">
    /// <item>不透明（<c>include_scene_clear</c>）：裁剪范围先验就是整幅，编码参数在渲染前已知。透明组的裁剪区是全片
    /// alpha 并集，渲完才知道，必须先落 master。</item>
    /// <item>不是残差组：残差组的成品是"淡化改写后的 master 再整片编一遍"，任何分段编码都会改变 x264 状态。</item>
    /// <item>不是探针：探针只量字节，走原路线更省事，也不必为它引入第二条编码链。</item>
    /// </list>
    /// </summary>
    public static bool AllowsDirectPlayback(bool includeSceneClear, bool residualGroup, ulong probeFrames) =>
        includeSceneClear && !residualGroup && probeFrames == 0;

    /// <summary>
    /// "这一案还没跑完"的状态。硬超时或硬杀之后留在 bake.json 里的就是它：候选回退与分配回退都要跑几十分钟，
    /// 期间磁盘上不能留着上一段的结论（那会被下游当成最终结果，把整轮工作量记成上一段的零点几秒）。
    /// 不在界面的"已完成"白名单里，也不是任何拒绝状态。
    /// </summary>
    public const string InProgressStatus = "in_progress";

    /// <summary>
    /// 把报告标成"还在跑"，记下这是第几个候选与处在哪一段回退。正常结束时再由各自的出口写最终状态。
    /// </summary>
    internal static void MarkInProgress(JsonObject report, int candidateAttempt, string stage)
    {
        ArgumentNullException.ThrowIfNull(report);
        report["status"] = InProgressStatus;
        report["in_progress_stage"] = stage;
        report["candidate_attempt"] = candidateAttempt;
        report["loop_validation"] = "not_performed";
        report.Remove("reason");
        report.Remove("reason_localized");
    }

    internal static JsonArray? PlannedSourceScriptErrors(JsonObject plan) =>
        plan["source_script_error_evidence"]?["status"]?.GetValue<string>() == "available" ? SourceScriptErrors(plan) : null;

    private static void AttachPlanProvenance(JsonObject report, JsonObject plan)
    {
        if (PlannedSourceScriptErrors(plan) is JsonArray errors)
        {
            report["source_script_error_count"] = plan["source_script_error_count"]!.DeepClone();
            report["source_script_errors"] = errors.DeepClone();
        }
        else report["source_script_error_evidence"] = "not_available";
        // 事后核对用：成品原样带上分析设备，旧计划没有这一项时不补造。
        if (plan["analysis_device"] is JsonNode device) report["analysis_device"] = device.DeepClone();
    }

    /// <summary>
    /// 一次烘焙的外层入口。中间产物（捕获副本、各组无损 master、合成探针与参照、分析刷新目录等，登记表见 WorkLayout）在这里统一清理：
    /// 成功、拒绝、失败、取消都清，只留成品工程、bake.json、接缝预览与日志。
    /// <c>--keep-intermediates</c>（<see cref="HybridBakeRequest.KeepIntermediates"/>）给开发者留原样；
    /// 合成校验被拒时保留探针与参照，报告里的 probe_paths 才有东西可看。
    /// 短探针那次调用不清理：它的产物由外层这次调用连目录一起删。
    /// </summary>
    public async Task<JsonObject> BakeAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProbeFrames > 0 || request.KeepIntermediates)
            return await BakeRunAsync(request, progress, cancellationToken);
        var layout = new WorkLayout(request.OutputDirectory);
        JsonObject? result = null;
        try { return result = await BakeRunAsync(request, progress, cancellationToken); }
        finally
        {
            // 按 WorkLayout 的登记表清理；合成被拒时探针与参照留给报告指路。
            JsonArray? cleanupErrors = layout.RemoveIntermediates(WorkLayout.KeepsCompositionProbe(result));
            if (result is not null)
            {
                result["intermediates_removed"] = true;
                if (cleanupErrors is not null) result["intermediate_cleanup_errors"] = cleanupErrors;
                try { await BakeReportWriter.SaveAsync(layout.Report, result, null, CancellationToken.None); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<JsonObject> BakeRunAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timing = new StageTiming(progress);
        if (!double.IsFinite(request.EffectRenderScale) || request.EffectRenderScale is <= 0 or > 1)
            throw new ArgumentException("EffectRenderScale must be finite and in (0, 1].");
        if (request.MatchEffectResolution && request.EffectRenderScale != 1.0)
            throw new ArgumentException("Adaptive effect resolution cannot be combined with EffectRenderScale other than 1.");
        if ((request.EffectRenderScale != 1.0 || request.MatchEffectResolution) && request.Plan["route"]?.GetValue<string>() == "effect_prefix")
            throw new ArgumentException("Internal effect resolution changes currently apply to whole-layer baking; effect-prefix captures require the original resolution.");
        progress?.Report(new("preflight", null, "Verifying the generation plan and source files."));
        timing.SetDevice(request.DeviceUuid ?? request.Plan["settings"]?["device_uuid"]?.GetValue<string>());
        if (request.Plan["route"]?.GetValue<string>() == "effect_prefix")
            return await BakeEffectPrefixesAsync(request, progress, timing, cancellationToken);
        return await BakeOnceAsync(request, progress, timing, cancellationToken);
    }

    private async Task<JsonObject> BakeEffectPrefixesAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        StageTiming timing, CancellationToken cancellationToken)
    {
        HybridPlanFormat.Validate(request.Plan);
        if (request.Plan["blockers"] is not JsonArray { Count: 0 })
            throw new InvalidDataException("Resolve the plan's listed blockers before generating it.");
        using var source = new ProjectSource(request.Plan["source"]!.GetValue<string>());
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
        EnsureNewDerivedOutput(source, output, "Effect-prefix output");
        string? destination = request.ProjectDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ProjectDirectory));
        if (destination is not null)
        {
            EnsureNewDerivedOutput(source, destination, "Destination project");
            if (destination.Equals(output, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                source.DirectoryPath.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The destination project must be separate from source and working directories.");
        }
        JsonObject result = await new EffectPrefixBakeService(tools).BakeAsync(request, progress, timing, cancellationToken);
        AttachPlanProvenance(result, request.Plan);
        if (destination is not null && StaticOnlyBake.Finished(result["status"]?.GetValue<string>()))
        {
            using (timing.Measure(StageTiming.ProjectAssembly))
            {
                using var generated = new ProjectSource(result["project_path"]!.GetValue<string>());
                await generated.ExtractAsync(destination, cancellationToken);
            }
            result["project_path"] = destination;
            result["work_directory"] = output;
            timing.Stamp(result);
            await VideoSceneBuilder.WriteJsonAsync(Path.Combine(destination, "bake.json"), result, cancellationToken);
        }
        timing.Stamp(result);
        await File.WriteAllTextAsync(Path.Combine(output, "bake.json"), result.ToJsonString(JsonOptions), cancellationToken);
        return result;
    }

    private async Task<JsonObject> BakeOnceAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        StageTiming timing, CancellationToken cancellationToken)
    {
        var plan = request.Plan.DeepClone().AsObject();
        if (request.SchemaVersion != 2)
            throw new InvalidDataException("A version 2 hybrid-video bake request is required.");
        HybridPlanFormat.Validate(plan);
        bool probe = request.ProbeFrames > 0;
        if (!probe && plan["blockers"] is JsonArray previousBlockers)
        {
            BlockerCode[] previousCodes = PlanBlockers.Codes(plan).ToArray();
            for (int i = previousBlockers.Count - 1; i >= 0; --i)
                if (previousCodes[i] == BlockerCode.MissingScriptFaultEvidence) previousBlockers.RemoveAt(i);
        }
        if (plan["blockers"] is JsonArray { Count: > 0 }) throw new InvalidDataException("Resolve the plan's listed blockers before generating it.");
        if (LayoutAdmission.FullFrameConflict(plan) is Blocker initialLayoutConflict)
            throw initialLayoutConflict.ToException();
        if (LayoutAdmission.CompositionHierarchyConflict(plan) is Blocker initialHierarchyConflict)
            throw initialHierarchyConflict.ToException();
        if (request.DeviceUuid is not null)
        {
            if (plan["settings"] is not JsonObject captureSettings) throw new InvalidDataException("Hybrid capture settings are missing.");
            captureSettings["device_uuid"] = request.DeviceUuid;
        }
        using var source = new ProjectSource(plan["source"]!.GetValue<string>());
        string sourceHash = await source.SourceHashAsync(cancellationToken);
        if (sourceHash != plan["source_sha256"]!.GetValue<string>()) throw new InvalidDataException("Source changed; analyze it again.");
        var layout = new WorkLayout(request.OutputDirectory);
        string output = layout.Output;
        EnsureNewDerivedOutput(source, output, "Hybrid output");
        string? destination = ProjectPublisher.Destination(request.ProjectDirectory, source, output);
        // 成品烘焙在任何渲染之前过只读的闸门链（BakeGates.Preflight），第一道拒绝就写 bake.json 结束；
        // 探针（合成校验的短烘焙）由外层这一案发起，不过闸。
        var preflight = new BakeGateContext(request, source, sourceHash, layout, progress) { Plan = plan, Settings = PlanSettings.Of(plan) };
        if (!probe && await BakeGates.FirstRejectionAsync(BakeGates.Preflight(tools), preflight, cancellationToken) is BakeRejection rejection)
            return await WriteRejectionAsync(rejection, preflight.Plan, layout, timing, cancellationToken);
        plan = preflight.Plan;
        HybridAnalyzeRequest settings = preflight.Settings;
        ulong frames = probe ? request.ProbeFrames : preflight.Frames;
        JsonObject? residualMasking = preflight.ResidualMasking;
        // P4：合成校验道（48 帧探针烘焙 + 原作参照配对比较 + 内嵌视频 2 GiB 外推，BakeGates.Validation）与下面的捕获副本准备、
        // 起点搜索、首批组主渲染同时跑。它不改计划，读自己的计划副本；它的拒绝与异常优先于主道这段时间里的任何结果，
        // 与串行时它排在一切渲染之前等价。它自己的墙钟记在 stage_timing.overlapped，不进互斥阶段。
        var lane = new BakeGateContext(request, source, sourceHash, layout, progress)
            { Plan = plan.DeepClone().AsObject(), Settings = settings, Frames = frames };
        Task<BakeRejection?>? validation = probe ? null : Task.Run(async () =>
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                return await BakeGates.FirstRejectionAsync(BakeGates.Validation(
                    (context, token) => new ProbeBake(tools).ValidateAsync(this, context.Request, context.Plan, context.Settings,
                        context.Source, context.Layout.Output, context.Progress, token),
                    EstimateEmbeddedVideoAsync), lane, cancellationToken);
            }
            finally { timing.AddOverlapped(StageTiming.CompositionValidation, Stopwatch.GetElapsedTime(started).TotalSeconds); }
        });
        var original = source.ReadJson(source.SceneResource);
        PlanTransforms.ApplyAudioEffectChoice(original, plan);
        var metadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        var originalObjects = original["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(SceneGraph.Id);
        var groups = plan["video_groups"]!.AsArray().OfType<JsonObject>().ToArray();
        var plannedLiveIds = (plan["live_layer_ids"] as JsonArray ?? []).Select(node => node!.GetValue<int>()).ToHashSet();
        // 组内并行只提前跑主渲染：最多这么多个组的渲染同时在飞，判定、编码与写入仍严格按组序串行。
        // 1 = 与改动前逐组串行完全一致，核显机器上默认不变。
        int groupParallel = Math.Clamp(request.GroupParallel, 1, Math.Max(1, groups.Length));
        Directory.CreateDirectory(output);
        var report = BakeReportWriter.Running(request, sourceHash, frames, plan.DeepClone().AsObject(), groupParallel,
            residualMasking is null ? "source_period_no_repair" : ResidualMasking.SeamPolicy);
        if (residualMasking is not null)
        {
            report["residual_masking"] = residualMasking.DeepClone();
            report["crossfade_frames"] = ResidualMasking.CrossfadeFrames(settings.FpsNumerator, settings.FpsDenominator);
        }
        AttachPlanProvenance(report, plan);
        // 合成校验道的两份结果在它放行后填回这两个位置，键序与串行时相同。
        if (!probe)
        {
            report["composition_validation"] = null;
            report["embedded_video_estimate"] = null;
        }
        var fullCaptureScriptErrors = new JsonArray();
        var fullCaptureScriptErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        if (!probe)
        {
            report["full_capture_source_script_error_count"] = 0;
            report["full_capture_source_script_errors"] = fullCaptureScriptErrors;
        }
        Task Save() => BakeReportWriter.SaveAsync(layout.Report, report, timing, CancellationToken.None);
        // 阶段计时占在这个位置（串行时第一次写盘就在这里），合成校验道放行后第一次写盘才落到磁盘上。
        timing.Stamp(report);
        // 主道这一段（SetupAsync）在合成校验放行前就开跑，产物全部落在这一案新建的输出目录里。
        // 它在另一线程上写报告与下面这些局部量；主线程等它结束后才读。
        var runner = new NativeRenderRunner(tools);
        string captureProject = layout.CaptureSource;
        string project = Path.Combine(output, "project");
        JsonObject snapshot = null!;
        DaytimeSplit.DynamicExport? daytimeExport = null;
        JsonArray finalDependencies = null!;
        // 残差掩盖打开时：起点帧由解析周期内的接缝残差决定，成品在接缝处做固定窗口的整帧交叉淡化。
        // 起点是预热之后、解析周期内的相位；渲染器实际跳过的帧数是 warmupFrames + 起点。
        uint crossfadeFrames = 0;
        ulong warmupFrames = 0;
        int[] residualGroupIndexes = [];
        string playbackKind = PlaybackEncoderSelection.Software;
        string? playbackFallbackReason = null;
        GroupRenderScheduler? scheduler = null;
        JsonObject? startSearch = null;
        JsonObject[] startOrder = [];
        using var speculation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task SetupAsync(CancellationToken token)
        {
            var initialRuntime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), token))!.AsObject();
            var runtimeDependencies = initialRuntime["runtime_dependencies"]?.AsArray()
                ?? throw new InvalidDataException("Runtime dependencies are missing.");
            daytimeExport = DaytimeSplit.PrepareDynamicExport(originalObjects, plan, runtimeDependencies);
            finalDependencies = SceneAssembler.MergeRuntimeDependencies(runtimeDependencies, new JsonArray());
            snapshot = plan["snapshot_properties"]!.DeepClone().AsObject();
            await CaptureSourceBuilder.PrepareAsync(captureProject, source, original, metadata, snapshot, plan, settings, probe,
                report, timing, token);
            using (timing.Measure(StageTiming.SourceCapture)) await source.ExtractAsync(project, token);
            crossfadeFrames = residualMasking is null ? 0 : ResidualMasking.CrossfadeFrames(settings.FpsNumerator, settings.FpsDenominator);
            // 平稳随机粒子要跑过预热（所有可掩盖粒子层 warmup_seconds 的最大值）才进入与时间无关的分布；没有粒子层时为 0。
            warmupFrames = residualMasking is null ? 0 : ResidualMasking.WarmupFrames(residualMasking, settings.FpsNumerator, settings.FpsDenominator);
            if (residualMasking is not null)
            {
                report["particle_warmup_frames"] = warmupFrames;
                report["particle_warmup_seconds"] = residualMasking["max_warmup_seconds"]?.DeepClone();
            }
            report["full_render_attempt_limit"] = probe ? 0 : 1;
            report["automatic_full_render_retries"] = false;
            // 含可掩盖残差层的组：它们的 master 多渲一个淡化窗口、各自测第一层并淡化；其余组照常渲 P 帧，相位同样是 warmup + S。
            // 布局门保证每个残差层都在某个组里，所以残差路线下这个列表非空。
            residualGroupIndexes = residualMasking is null ? [] : ResidualMasking.ResidualGroupIndexes(plan, residualMasking);
            if (residualMasking is not null && residualGroupIndexes.Length == 0)
                throw new InvalidOperationException("残差掩盖路线没有任何视频组含可掩盖残差层；布局门本应先拒绝。");
            if (residualMasking is not null)
                report["residual_group_ids"] = new JsonArray([.. residualGroupIndexes.Select(index => groups[index]["id"]!.DeepClone())]);
            // 直编组要在渲染开始前就定下播放档位（编码器随渲染一起启动），所以档位解析提到所有渲染之前，整次烘焙只解析一次。
            // master 路线仍把原字符串交给 EncodeCroppedRgbaAsync 自己解析，行为不变。软件档位不起额外进程。
            (playbackKind, playbackFallbackReason) = await runner.ResolvePlaybackEncoderAsync(request.PlaybackEncoder, output, token);
            scheduler = new GroupRenderScheduler(runner, request, plan, settings, groups, captureProject, output, snapshot, frames,
                crossfadeFrames, warmupFrames, residualGroupIndexes, groupParallel, playbackKind, progress, token);
            if (residualMasking is not null && !probe)
            {
                startSearch = await LoopStartSelector.SearchAsync(runner, scheduler, progress, timing, token);
                startOrder = LoopStartSelector.Order(startSearch);
                report["loop_start_search"] = startSearch.DeepClone();
                report["source_start_frame"] = checked(warmupFrames + scheduler.StartFrame);
                report["loop_start_phase_frame"] = scheduler.StartFrame;
            }
            // 首批组主渲染（第 0 组及 groupParallel 允许的后续组）提前启动。
            if (groups.Length > 0) _ = scheduler.RenderAsync(0);
        }
        Task setup = Task.Run(() => SetupAsync(speculation.Token));
        if (validation is not null)
        {
            // 主道先做完时，剩下等合成校验的时间记 composition_validation；合成校验先完成时它整段被主道重叠，不记。
            if (await Task.WhenAny(setup, validation) == setup && !validation.IsCompleted)
                using (timing.Measure(StageTiming.CompositionValidation)) await Task.WhenAny(validation);
            if (!validation.IsCompletedSuccessfully || validation.Result is not null)
            {
                // 合成校验没放行：主道提前做的全部作废。取消并等它与提前启动的渲染器退出，删掉整个输出目录（这一案新建的，
                // 串行时此刻还不存在），再按串行时的出口结束：拒绝写 bake.json，异常原样抛出。
                await speculation.CancelAsync();
                try { await setup; } catch { }
                if (scheduler is not null) await scheduler.DisposeAsync();
                DiscardSpeculativeOutput(output);
                BakeRejection compositionRejection = (await validation)!;
                return await WriteRejectionAsync(compositionRejection, plan, layout, timing, cancellationToken);
            }
        }
        try
        {
            Exception? setupError = null;
            try { await setup; }
            catch (Exception error) { setupError = error; }
            if (!probe)
            {
                FillOrRemove(report, "composition_validation", lane.CompositionValidation);
                FillOrRemove(report, "embedded_video_estimate", lane.EmbeddedVideoEstimate);
            }
            await Save();
            try
            {
                if (setupError is not null) ExceptionDispatchInfo.Throw(setupError);
                var groupScheduler = scheduler!;
                var scene = original.DeepClone().AsObject();
                if (settings.ViewMode == "fixed_view") scene["general"]!["cameraparallax"] = false;
                int nextId = checked(originalObjects.Keys.Max() + 1);
                var replacements = new Dictionary<string, JsonObject>();
                int staticLayers = 0;
                var encoder = new GroupEncoder(runner, request, playbackKind, playbackFallbackReason, progress, timing);
                var startAttempts = new JsonArray();
                // 这一轮各残差组的第一层读数，组成本次起点尝试的记录。
                var roundResiduals = new List<(string GroupId, JsonObject SeamResidual)>();
                for (int i = 0; i < groups.Length; ++i)
                {
                    var group = groups[i];
                    string id = group["id"]!.GetValue<string>();
                    double depthX = groupScheduler.PreserveParallax ? group["parallax_depth"]![0]!.GetValue<double>() : 0;
                    double depthY = groupScheduler.PreserveParallax ? group["parallax_depth"]![1]!.GetValue<double>() : 0;
                    GroupCapture capture = groupScheduler.Capture(group);
                    int[] layers = capture.Layers;
                    string work = ProjectSource.ContainedPath(output, id);
                    progress?.Report(new("rendering_group", (double)i / groups.Length, $"Video group {i + 1}/{groups.Length}: {layers.Length} source layers"));
                    string masterPath = Path.Combine(work, "master");
                    // 提前启动的渲染已经建好这个目录并自己查过一次；没有提前启动时照旧在这里查。
                    if (!groupScheduler.Started(i) && (Directory.Exists(masterPath) || File.Exists(masterPath)))
                        throw new IOException("A group master output must be new; existing files will not be cleaned.");
                    // 残差组（含可掩盖残差层）多渲一个淡化窗口，测第一层，通过后在接缝处淡化；不淡化的组多渲 1 帧，
                    // 编码只取前 P 帧，第 P 帧原帧留作闭合检验与接缝参照，接缝由闭合检验裁决。
                    GroupFraming framing = groupScheduler.Framing(i);
                    bool residualGroup = framing.Residual;
                    // 这个组的成品能不能在渲染时直接编出来，不写无损 master。
                    bool directPlayback = AllowsDirectPlayback(capture.SceneClear, residualGroup, request.ProbeFrames);
                    JsonObject? groupSeamResidual = null, groupCrossfade = null;
                    bool groupFailed = false;
                    try
                    {
                        // 正常走不到：布局淡化不了时循环准入闸门已经干净拒绝。这里只留作内部不变量。
                        if (residualMasking is not null && !ResidualMasking.LayoutAllowsMasking(plan, residualMasking))
                            throw new InvalidOperationException("残差掩盖要求每个可掩盖残差层都在某个视频组里，这个计划里有残差层不在任何组中。");
                        // 整周期预热不改精确周期分量的相位，所以 source_start_frame 不含它：源周期路线是 S，残差路线是 W + S。
                        if (!probe) report["source_period_warmup_frames"] = framing.SourcePeriodWarmupFrames;
                        // 提前启动过就直接等它，master_render 只计真正等的那段墙钟，与后面的编码不重叠。
                        JsonObject master;
                        using (timing.Measure(StageTiming.MasterRender))
                            master = await groupScheduler.RenderAsync(i);
                        masterPath = master["request"]!["output_directory"]!.GetValue<string>();
                        timing.AddMasterBreakdown(master);
                        bool gpuDirect = master["native_frame_transport"]?.GetValue<string>() == "gpu_nv12";
                        directPlayback |= gpuDirect;
                        JsonObject? lateDependencyValidation = null;
                        if (!probe)
                        {
                            JsonArray lateDependencies = HybridExportSafety.LateExternalDependencies(master, layers.ToHashSet(), originalObjects);
                            JsonArray captureErrors = master["native_result"] is JsonObject nativeResult ?
                                SourceScriptErrors(nativeResult) ?? throw new InvalidDataException("A full group capture omitted source script fault metadata.") :
                                throw new InvalidDataException("A full group capture omitted its native result.");
                            finalDependencies = SceneAssembler.MergeRuntimeDependencies(finalDependencies, nativeResult["runtime_dependencies"]!.AsArray());
                            foreach (var error in captureErrors.OfType<JsonObject>())
                                if (fullCaptureScriptErrorKeys.Add(error.ToJsonString())) fullCaptureScriptErrors.Add(error.DeepClone());
                            report["full_capture_source_script_error_count"] = fullCaptureScriptErrors.Count;
                            JsonObject classifiedErrors = HybridExportSafety.ClassifyLateSourceScriptErrors(captureErrors, layers.ToHashSet(), plannedLiveIds, originalObjects);
                            lateDependencyValidation = GroupVerdicts.LateDependency(id, layers, lateDependencies, classifiedErrors["unsafe"]!.AsArray(),
                                classifiedErrors["external_live"]!.AsArray(), framing.RenderedFrames, framing.MasterWarmupFrames, masterPath);
                            if (GroupVerdicts.RejectLateDependency(report, id, layers, lateDependencyValidation))
                            {
                                await Save();
                                return report;
                            }
                        }
                        if (master["alpha_bounds"]?["has_content"]?.GetValue<bool>() != true)
                        {
                            report["groups"]!.AsArray().Add(GroupVerdicts.Empty(id, layers, lateDependencyValidation, master));
                            await Save(); continue;
                        }
                        if (residualGroup)
                        {
                            // 全分辨率复核第一层：降采样只用来排序候选起点，放行与否看这里的数字。
                            progress?.Report(new("checking_seam_residual", (double)i / groups.Length,
                                "在全分辨率下测量淡化窗口内每一帧的接缝残差。"));
                            JsonObject wrap;
                            using (timing.Measure(StageTiming.SeamCheck))
                                wrap = await runner.MeasureSeamResidualAsync(masterPath, frames, crossfadeFrames, cancellationToken);
                            wrap["limits"] = ResidualMasking.Thresholds();
                            bool firstLayer = wrap["first_layer"]!["passed"]!.GetValue<bool>();
                            wrap["group_id"] = id;
                            groupSeamResidual = wrap;
                            // 顶层 seam_residual 记本轮读数最差的残差组（被拒的组一定记）；每组自己的读数记在组记录里。
                            if (!firstLayer || report["seam_residual"] is not JsonObject previousResidual ||
                                wrap["first_layer"]!["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() >=
                                (previousResidual["first_layer"]?["maximum_worst_tile_rgb_mae_255"]?.GetValue<double>() ?? 0))
                                report["seam_residual"] = wrap.DeepClone();
                            roundResiduals.Add((id, wrap));
                            // 本轮最后一个残差组测完（或任何一组被拒）时，这次起点尝试就有了结论。
                            if (!firstLayer || i == residualGroupIndexes[^1])
                            {
                                startAttempts.Add(LoopStartSelector.Attempt(0, groupScheduler.StartFrame, startOrder.FirstOrDefault(), roundResiduals));
                                if (firstLayer) report["selected_loop_start"] = LoopStartSelector.Selected(0, groupScheduler.StartFrame);
                            }
                            if (!firstLayer)
                            {
                                // 残差被拒时还没有编码成品：从尚未清理的无损 master 里 seek 出硬切接缝两侧各 N 帧。
                                // 预览写在组目录，finally 只删 master 里点名的中间文件。
                                JsonObject residualPreview;
                                progress?.Report(new("exporting_seam_preview", (double)i / groups.Length,
                                    MessageCatalog.Get("progress.exporting_seam_preview", MessageCatalog.DefaultLanguage(),
                                        SeamPreview.WindowFrames(frames, settings.FpsNumerator, settings.FpsDenominator))));
                                using (timing.Measure(StageTiming.SeamCheck))
                                    residualPreview = await SeamPreview.ExportOrWarnAsync(report, id, SeamPreview.RejectedOutcome,
                                        token => runner.ExportSeamPreviewAsync(Path.Combine(masterPath, "preview.mp4"),
                                            Path.Combine(work, SeamPreview.FileName), frames, settings.FpsNumerator, settings.FpsDenominator,
                                            gpuDirect ? "gpu_candidate_after_crossfade" : "lossless_master_hard_cut", token), cancellationToken);
                                // 选定起点被拒即停止；记录本次各组的残差，不启动整案重渲。
                                GroupVerdicts.RejectResidual(report, id, layers, lateDependencyValidation, wrap, residualPreview, startAttempts,
                                    startSearch?["candidate_count"]?.GetValue<int>() ?? startOrder.Length, crossfadeFrames);
                                if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
                                await Save();
                                return report;
                            }
                            progress?.Report(new("applying_crossfade", (double)i / groups.Length,
                                "在接缝处做固定窗口的整帧交叉淡化。"));
                            using (timing.Measure(StageTiming.Crossfade))
                                groupCrossfade = gpuDirect ? master["loop_crossfade"]!.DeepClone().AsObject()
                                    : await new MasterRewrite(new FfmpegTool(tools)).CrossfadeAsync(masterPath, frames, crossfadeFrames, cancellationToken);
                            groupCrossfade["group_id"] = id;
                            // 顶层 loop_crossfade 记第一个残差组；每组自己的淡化记录（含自检）在组记录里。
                            if (report["loop_crossfade"] is null) report["loop_crossfade"] = groupCrossfade.DeepClone();
                            await Save();
                        }
                        bool packedAlpha = gpuDirect ? master["pixel_packing"]?.GetValue<string>() == "rgba_side_by_side"
                            : !capture.SceneClear && master["alpha_bounds"]?["minimum_alpha"]?.ToJsonString() != "255";
                        bool isStatic = master["alpha_bounds"]?["pixel_identical_in_generated_interval"]?.GetValue<bool>() == true;
                        if (!probe && GroupVerdicts.RejectStaticProof(report, group, id, layers, lateDependencyValidation, isStatic))
                        {
                            await Save(); return report;
                        }
                        if (settings.VideoLayout == "full_frame" && (packedAlpha || master["alpha_bounds"]?["minimum_alpha"]?.ToJsonString() != "255"))
                            throw new InvalidDataException("Full-frame output was not opaque. The candidate was stopped instead of switching to transparent video.");
                        var (encoded, crop, video) = await encoder.EncodeAsync(master, masterPath, work, capture, isStatic, directPlayback,
                            gpuDirect, packedAlpha, cancellationToken);
                        if (isStatic) ++staticLayers;
                        long encodedBytes = isStatic ? 0 : new FileInfo(video).Length;
                        if (!probe && encodedBytes > EmbeddedVideoBudget.MaximumBytes)
                        {
                            GroupVerdicts.RejectEmbeddedSize(report, id, layers, packedAlpha, encoded, video, encodedBytes,
                                lateDependencyValidation, frames, settings);
                            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
                            await Save();
                            return report;
                        }
                        double x = ((crop.X + crop.Width / 2d) / capture.PixelWidth - .5) * capture.Width;
                        if (settings.VideoLayout == "full_frame" && (crop.X != 0 || crop.Y != 0 || crop.Width != capture.PixelWidth || crop.Height != capture.PixelHeight))
                            throw new InvalidDataException("Full-frame output did not cover the complete capture. The candidate was stopped instead of selecting a smaller video region.");
                        double y = (.5 - (crop.Y + crop.Height / 2d) / capture.PixelHeight) * capture.Height;
                        JsonObject? seam = null;
                        if (!probe)
                        {
                            using (timing.Measure(StageTiming.SeamCheck))
                                seam = await GroupVerdicts.SeamAsync(tools, master, masterPath, video, crop, capture, isStatic, packedAlpha,
                                    directPlayback, residualGroup, request.KeepIntermediates, frames, crossfadeFrames, settings, cancellationToken);
                        }
                        else if (isStatic) seam = GroupVerdicts.ProbeStatic();
                        // 成功任务仅在显式保留诊断时导出；拒绝仍提供预览帮助定位。
                        JsonObject? seamPreview = null;
                        if (SeamPreview.ShouldExport(probe, isStatic, seam, request.KeepIntermediates))
                        {
                            progress?.Report(new("exporting_seam_preview", (double)i / groups.Length,
                                MessageCatalog.Get("progress.exporting_seam_preview", MessageCatalog.DefaultLanguage(),
                                    SeamPreview.WindowFrames(frames, settings.FpsNumerator, settings.FpsDenominator))));
                            using (timing.Measure(StageTiming.SeamCheck))
                                seamPreview = await SeamPreview.ExportOrWarnAsync(report, id,
                                    SeamPreview.Outcome(seam!["status"]?.GetValue<string>()),
                                    token => runner.ExportSeamPreviewAsync(video, Path.Combine(work, SeamPreview.FileName), frames,
                                        settings.FpsNumerator, settings.FpsDenominator, "encoded_video", token), cancellationToken);
                        }
                        JsonObject? hardwareDecode = null;
                        if (!probe && seam?["status"]?.GetValue<string>() != "observed_seam_pass")
                        {
                            GroupVerdicts.RejectSeam(report, id, layers, packedAlpha, encoded, video, lateDependencyValidation, seam, seamPreview);
                            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
                            await Save();
                            return report;
                        }
                        if (!isStatic && !probe)
                        {
                            progress?.Report(new("checking_hardware_decode", (double)i / groups.Length,
                                "Checking this actual video on the installed hardware decoders."));
                            using (timing.Measure(StageTiming.HardwareDecodeCheck))
                                hardwareDecode = await runner.ProbeHardwareDecodeAsync(video, Path.Combine(work, "hardware-decode"),
                                    Math.Min(frames, 5), cancellationToken);
                        }
                        JsonObject layer;
                        using (timing.Measure(StageTiming.ProjectAssembly))
                        layer = await ProjectWriter.WriteLayerAsync(project, id, video,
                            (uint)crop.Width * (packedAlpha && !isStatic ? 2u : 1u), (uint)crop.Height, nextId++,
                            groupScheduler.CenterX, groupScheduler.CenterY,
                            crop.Width * capture.Width / capture.PixelWidth, crop.Height * capture.Height / capture.PixelHeight,
                            cancellationToken, packedAlpha, depthX, depthY, x, y, isStatic,
                            capturedColor: SceneGraph.Int(group["parent_id"]) is not null, hdrScale: capture.HdrScale ?? 1);
                        HybridVideoProjection.AttachToParent(layer, group);
                        if (daytimeExport is not null)
                            daytimeExport.BindReplacement(layer, originalObjects[daytimeExport.ReplacementTargets[id]], isStatic);
                        replacements[id] = layer;
                        report["groups"]!.AsArray().Add(GroupVerdicts.Encoded(id, layers, layer, isStatic, packedAlpha, encoded, video, capture,
                            lateDependencyValidation, seam, hardwareDecode, master, gpuDirect, directPlayback, groupSeamResidual, groupCrossfade,
                            seamPreview));
                        await Save();
                    }
                    catch
                    {
                        groupFailed = true;
                        throw;
                    }
                    finally
                    {
                        TemporaryCaptureFiles.Delete(report, masterPath,
                            "preview.mp4", "video.partial.mp4", "first-frame.rgba", "frame-samples.rgb",
                            NativeRenderRunner.RetainedFramesFile, "native/audio.f32le", "native/audio.f32le.partial");
                        try { await Save(); }
                        catch when (groupFailed) { }
                    }
                }
                JsonArray finalObjects;
                using (timing.Measure(StageTiming.ProjectAssembly))
                {
                    finalObjects = SceneAssembler.AssembleObjects(originalObjects, plan, replacements, finalDependencies);
                    // 记下按"不绘制但带脚本"规则额外保留的根对象，事后核对用。
                    report["retained_script_root_ids"] = JsonSerializer.SerializeToNode(SceneAssembler.ScriptRootIds(originalObjects, plan));
                    if (daytimeExport is not null)
                        report["daytime_dynamic_export"] = new JsonObject {
                            ["status"] = "preserved", ["captured_state"] = daytimeExport.State.Name,
                            ["replaced_source_layer_ids"] = JsonSerializer.SerializeToNode(daytimeExport.ReplacementTargets.Values),
                            ["retained_original_state_layer_ids"] = JsonSerializer.SerializeToNode(daytimeExport.Detection.ControlledLayerIds.Except(daytimeExport.ReplacementTargets.Values)),
                            ["adjustable_property_keys"] = JsonSerializer.SerializeToNode(daytimeExport.PropertyKeys) };
                    scene["objects"] = finalObjects;
                    PlanTransforms.ApplyTextEffectChoice(scene, plan);
                    ProjectWriter.ApplyVisibilityFallbacks(scene, snapshot);
                    if (replacements.Count == 0) throw new InvalidDataException("No video group produced visible output; this is not a hybrid candidate.");
                    await ProjectWriter.WriteAsync(project, source.SceneResource, scene, metadata, daytimeExport, cancellationToken);
                }
                if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
                JsonObject[] encodedGroups = report["groups"]!.AsArray().OfType<JsonObject>()
                    .Where(g => g["status"]?.GetValue<string>() == "encoded").ToArray();
                bool seamsPass = !probe && encodedGroups.All(g =>
                    g["encoded_loop_validation"]?["status"]?.GetValue<string>() == "observed_seam_pass");
                report["video_layers"] = replacements.Count - staticLayers;
                report["static_layers"] = staticLayers;
                // 1 帧、0 个视频层的结果单列 static_only：candidate_generated 只给真的含视频循环的成品。
                report["status"] = probe ? "probe_generated"
                    : !seamsPass ? "candidate_rejected_seam"
                    : StaticOnlyBake.Is(report) ? StaticOnlyBake.Status : "candidate_generated";
                report["loop_validation"] = probe ? "not_performed" : seamsPass ? "encoded_seams_passed" : "encoded_seam_failed";
                report["project_path"] = project;
                // 播放版编码的汇总：请求档位、实际档位、回退理由与编码总秒数，方便直接和软件档位对比。
                report["playback_encoder"] = PlaybackEncoderSelection.Summarize(request.PlaybackEncoder,
                    report["groups"]!.AsArray().OfType<JsonObject>());
                report["retained_object_count"] = finalObjects.Count - replacements.Count;
                report["source_draw_objects_removed"] = groups.Sum(g => g["layer_ids"]!.AsArray().Count);
                await ProjectPublisher.PublishAsync(report, project, destination, layout, timing, progress, cancellationToken);
                await Save();
                return report;
            }
            catch (Exception error)
            {
                report["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
                report["error_type"] = error.GetType().Name; report["error"] = error.Message;
                await Save(); throw;
            }
        }
        finally
        {
            // 任何出口（拒绝、异常、取消）都不能把提前启动的渲染器进程留在后面。
            if (scheduler is not null) await scheduler.DisposeAsync();
        }
    }

    /// <summary>闸门拒绝：写成新的 bake.json（附计划来源），这一案到此结束。</summary>
    private static Task<JsonObject> WriteRejectionAsync(BakeRejection rejection, JsonObject plan, WorkLayout layout, StageTiming timing,
        CancellationToken cancellationToken)
    {
        JsonObject rejected = rejection.ToJson();
        AttachPlanProvenance(rejected, plan);
        return BakeReportWriter.WriteNewAsync(layout.Report, rejected, timing, cancellationToken);
    }

    /// <summary>开跑时留的占位：有值就原位填上，没有就去掉（与串行时"有才写"一致）。</summary>
    private static void FillOrRemove(JsonObject report, string key, JsonObject? value)
    {
        if (value is null) report.Remove(key);
        else report[key] = value;
    }

    /// <summary>
    /// 合成校验没放行时删掉主道提前产出的整个输出目录：它由这一案新建（<see cref="EnsureNewDerivedOutput"/> 查过不存在），
    /// 串行时此刻还没有它。删不掉的留给外层按 WorkLayout 登记表清理。
    /// </summary>
    private static void DiscardSpeculativeOutput(string output)
    {
        try { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 主渲染前按 composition probe 的试编码外推每个视频组的成品大小（EmbeddedVideoBudget.EvaluateProbe）。
    /// 读不到试编码时记 not_estimated、不拒绝：编码后还有一次按实际字节的检查。
    /// </summary>
    private async Task<JsonObject> EstimateEmbeddedVideoAsync(JsonObject compositionValidation, ulong frames,
        HybridAnalyzeRequest settings, CancellationToken cancellationToken)
    {
        var samples = new List<EmbeddedVideoBudget.ProbeGroup>();
        Message? reason = null;
        try
        {
            string probeOutput = compositionValidation["probe_output_path"]?.GetValue<string>()
                ?? throw new InvalidDataException("The composition validation omitted its probe output path.");
            JsonObject probe = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(probeOutput, "bake.json"), cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("The composition probe report is empty.");
            ulong probeFrames = probe["frames"]?.GetValue<ulong>() ?? 0;
            foreach (JsonObject group in probe["groups"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (group["storage"]?.GetValue<string>() != "video" || group["video_path"]?.GetValue<string>() is not string video || !File.Exists(video))
                    continue;
                bool packed = group["packed_alpha"]?.GetValue<bool>() == true;
                uint width = group["crop"]?["width"]?.GetValue<uint>() ?? 0, height = group["crop"]?["height"]?.GetValue<uint>() ?? 0;
                samples.Add(new(group["id"]!.GetValue<string>(), packed, width * (packed ? 2u : 1u), height, probeFrames,
                    new FileInfo(video).Length, await EmbeddedVideoBudgetJson.ReadPacketsAsync(tools, video, cancellationToken)));
            }
            if (samples.Count == 0) reason = new Message("bake.probe_all_static");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or
            System.ComponentModel.Win32Exception)
        {
            samples.Clear();
            reason = new Message("bake.probe_unreadable", [error.Message]);
        }
        return EmbeddedVideoBudgetJson.EvaluateProbe(samples, frames, settings.FpsNumerator, settings.FpsDenominator, reason);
    }

    internal static void EnsureNewDerivedOutput(ProjectSource source, string destination, string description)
    {
        destination = Path.GetFullPath(destination);
        ProjectSource.EnsureNoReparsePoints(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException($"{description} must be new.");
        string sourcePrefix = Path.TrimEndingDirectorySeparator(source.DirectoryPath) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{description} must be outside the source project.");
    }

    internal static JsonArray? SelectVideoRateOverrides(JsonObject loop, IReadOnlySet<int> groupLayerIds)
    {
        JsonObject? candidate = loop["candidates"]?.AsArray().FirstOrDefault()?.AsObject();
        if (candidate is null) return null;
        var owners = new HashSet<int>();
        var overrides = new JsonArray();
        foreach (JsonObject patch in candidate["patches"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (patch["kind"]?.GetValue<string>() != "video_rate") continue;
            int? owner = SceneGraph.Int(patch["owner_layer_id"]);
            if (owner is null || !groupLayerIds.Contains(owner.Value)) continue;
            if (!owners.Add(owner.Value) || patch["rate_numerator"] is null || patch["rate_denominator"] is null)
                throw new InvalidDataException("Video rate patches must provide one exact override per captured owner.");
            overrides.Add(new JsonObject { ["owner_layer_id"] = owner.Value,
                ["rate_numerator"] = patch["rate_numerator"]!.DeepClone(),
                ["rate_denominator"] = patch["rate_denominator"]!.DeepClone() });
        }
        return overrides.Count == 0 ? null : overrides;
    }
}
