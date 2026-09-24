using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>一个视频组的捕获几何：源图层、是否含场景底色、正交捕获视口（场景单位）与像素尺寸（向上取偶）。</summary>
internal readonly record struct GroupCapture(int[] Layers, bool SceneClear, double Width, double Height, uint PixelWidth, uint PixelHeight,
    double? HdrScale = null);

/// <summary>
/// 一个组的渲染帧数与预热。残差组多渲一个淡化窗口；其余成品组多渲 1 帧（第 P 帧原帧留作闭合检验与接缝参照）；探针只渲 P 帧。
/// 录制前跳过的帧数 = 精灵 float32 整周期预热（0 或 P，见 SpriteSeamPhase）+ 粒子预热 W + 起点 S。
/// </summary>
internal readonly record struct GroupFraming(bool Residual, bool ClosureJudged, ulong RenderedFrames, ulong SourcePeriodWarmupFrames,
    ulong MasterWarmupFrames);

/// <summary>
/// 组主渲染的调度：捕获几何、帧数与预热、渲染请求（起点搜索样本与组 master），以及提前启动的主渲染登记表。
/// 最多 <c>groupParallel</c> 个组的主渲染同时在飞；判定、编码与写入仍由调用方严格按组序串行。
/// 请求只依赖本轮固定的量（帧数、<see cref="StartFrame"/>、候选表、起点搜索的覆盖度），所以可以提前给后面的组用。
/// 任何出口都经 <see cref="DisposeAsync"/> 取消并等这些渲染器退出，不把进程留在后面。
/// </summary>
internal sealed class GroupRenderScheduler(NativeRenderRunner runner, HybridBakeRequest request, JsonObject plan,
    HybridAnalyzeRequest settings, JsonObject[] groups, string captureProject, string output, JsonObject snapshot, ulong frames,
    uint crossfadeFrames, ulong warmupFrames, int[] residualGroupIndexes, int groupParallel, string playbackKind,
    IProgress<RenderProgress>? progress, CancellationToken cancellationToken) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly JsonObject projection = plan["projection"]!.AsObject();
    private readonly JsonArray loopCandidates = plan["loop"]?["candidates"] as JsonArray ?? new JsonArray();
    private readonly bool probe = request.ProbeFrames > 0;
    private readonly Dictionary<int, Task<JsonObject>> renders = [];
    private readonly CancellationTokenSource renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    internal double CenterX => projection["center_x"]!.GetValue<double>();
    internal double CenterY => projection["center_y"]!.GetValue<double>();
    internal bool PreserveParallax => plan["has_parallax"]?.GetValue<bool>() == true && settings.ViewMode == "preserve";
    internal JsonObject[] Groups => groups;
    /// <summary>按输出短边 / 1080 缩放像素口径（瓦片、样本宽）的倍率，1080p 下恰为 1。</summary>
    internal double TileScale => SwayRecurrenceSolver.SpeedLimitScale(settings.Width, settings.Height);
    internal ulong Frames => frames;
    internal uint CrossfadeFrames => crossfadeFrames;
    internal int[] ResidualGroupIndexes => residualGroupIndexes;

    /// <summary>预热之后、解析周期内的起点相位：残差路线由起点搜索定，源周期路线为 0。</summary>
    internal ulong StartFrame { get; set; }

    /// <summary>各残差组的起点搜索记录；Vulkan 直编的透明组从这里取全片覆盖度，省掉覆盖度预通道。</summary>
    internal Dictionary<string, JsonObject> StartSearches { get; } = new(StringComparer.Ordinal);

    /// <summary>起点搜索与 master 共用的预热基准（精灵整周期预热 + 粒子预热），样本第 s 帧就是 master 起点取 s 时的第 0 帧。</summary>
    internal ulong SearchWarmupFrames => LoopWarmup.BaseFrames(LoopWarmupJson.CandidateSourcePeriodWarmupFrames(loopCandidates), frames, warmupFrames);

    /// <summary>组的捕获几何，并核对像素尺寸与组 id（组 id 要当目录名用）。</summary>
    internal GroupCapture Capture(JsonObject group)
    {
        var viewport = HybridVideoProjection.CaptureViewportForGroup(projection, group, PreserveParallax);
        double visibleWidth = projection["visible_width"]!.GetValue<double>(), visibleHeight = projection["visible_height"]!.GetValue<double>();
        uint pixelWidth = checked((uint)Math.Ceiling(settings.Width * viewport.Width / visibleWidth / 2) * 2);
        uint pixelHeight = checked((uint)Math.Ceiling(settings.Height * viewport.Height / visibleHeight / 2) * 2);
        if (pixelWidth > 8192 || pixelHeight > 8192)
            throw new InvalidDataException("The requested parallax overscan exceeds the supported texture dimensions.");
        string id = group["id"]!.GetValue<string>();
        if (id != Path.GetFileName(id)) throw new InvalidDataException("Video group IDs must be single path components.");
        return new(group["layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray(),
            group["include_scene_clear"]?.GetValue<bool>() == true, viewport.Width, viewport.Height, pixelWidth, pixelHeight, HdrScale(id));
    }

    /// <summary>
    /// 官方 HDR 管线下闭合不成立的组：浮点捕获，编 v/k。k 固定 2：8 bit 还原误差 ≤1 级（低于编码噪声），>2 的部分截断。
    /// 闭合成立或非 HDR 管线的组走原 RGBA8 路径（null）。
    /// </summary>
    private double? HdrScale(string groupId) =>
        plan["hdr_radiance_closure"] is JsonObject closure && closure["hdr"]?.GetValue<bool>() == true &&
        (closure["groups"] as JsonArray ?? []).OfType<JsonObject>().Any(g =>
            g["group_id"]?.GetValue<string>() == groupId && g["status"]?.GetValue<string>() != "closed") ? 2.0 : null;

    /// <summary>一个组的渲染帧数与预热：组循环和提前启动的渲染共用一份，两处算法不会漂开。</summary>
    internal GroupFraming Framing(int index)
    {
        bool residual = !probe && residualGroupIndexes.Contains(index);
        bool closureJudged = !probe && !residual;
        ulong rendered = residual ? checked(frames + crossfadeFrames) : closureJudged ? checked(frames + 1) : frames;
        ulong sourcePeriodWarmup = probe ? 0 : LoopWarmupJson.CandidateSourcePeriodWarmupFrames(loopCandidates);
        return new(residual, closureJudged, rendered, sourcePeriodWarmup,
            LoopWarmup.MasterFrames(sourcePeriodWarmup, frames, warmupFrames, StartFrame));
    }

    /// <summary>
    /// 残差组的低分辨率起点评分样本：搜索窗固定 <see cref="ResidualMasking.SearchWindowPeriods"/> 个周期（候选起点只有 P/stride 个，
    /// 每个比较 (s, s+P)，全部落在前 2P 帧里）；透明组用带覆盖度的样本，两半分别算。
    /// </summary>
    internal RenderRequest StartSearchRequest(JsonObject group, GroupCapture capture, uint sampleStride) =>
        new(captureProject, settings.Assets,
            ProjectSource.ContainedPath(output, $"{group["id"]!.GetValue<string>()}.start-search"), capture.PixelWidth, capture.PixelHeight,
            settings.FpsNumerator, settings.FpsDenominator, checked(frames * (ulong)ResidualMasking.SearchWindowPeriods),
            WarmupFrames: SearchWarmupFrames,
            Seed: 17, UserProperties: snapshot, PixelPacking: capture.SceneClear ? "rgb" : "rgba_side_by_side",
            DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid,
            Input: new JsonObject { ["cursor_x"] = .5, ["cursor_y"] = .5, ["cursor_in_window"] = true },
            OrthographicCaptureViewport: new(CenterX, CenterY, capture.Width, capture.Height),
            LayerSelection: new(capture.Layers, TransparentBackground: !capture.SceneClear, IncludePostprocessing: false),
            FrameSampleStride: sampleStride,
            FrameSampleWidth: (uint)Math.Round(ResidualMasking.StartSearchSampleWidth * TileScale),
            FrameSamplesOnly: true,
            EffectRenderScale: request.EffectRenderScale,
            MatchEffectResolution: request.MatchEffectResolution, HdrScale: capture.HdrScale,
            CollectSamplingCoverage: PlaybackEncoderSelection.Normalize(request.PlaybackEncoder) == PlaybackEncoderSelection.Vulkan,
            FrameSampleIncludeAlpha: !capture.SceneClear,
            OfflineVideoRateOverrides: HybridBakeService.SelectVideoRateOverrides(plan["loop"]!.AsObject(), capture.Layers.ToHashSet()));

    /// <summary>这个组的主渲染是否已经提前启动（启动时它自己查过输出目录是新的）。</summary>
    internal bool Started(int index) => renders.ContainsKey(index);

    /// <summary>取这个组的主渲染，并按 groupParallel 把后面几组的渲染提前挂上去。</summary>
    internal Task<JsonObject> RenderAsync(int index)
    {
        if (!renders.TryGetValue(index, out Task<JsonObject>? render))
            renders[index] = render = StartAsync(index);
        for (int ahead = index + 1; ahead < Math.Min(groups.Length, index + groupParallel); ++ahead)
            if (!renders.ContainsKey(ahead)) renders[ahead] = StartAsync(ahead);
        return render;
    }

    /// <summary>取消还在飞的主渲染并等渲染器退出；这些结果已经不要了，取消、失败都咽掉（真正的失败早已从主流程抛出过一次）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (renders.Count > 0)
        {
            await renderCancellation.CancelAsync();
            foreach (var pending in renders.Values) try { await pending; } catch { }
            renders.Clear();
        }
        renderCancellation.Dispose();
    }

    /// <summary>
    /// 一个组的主渲染请求。直编组不写无损 master：渲染器出帧直接进播放档编码器（判定与组循环走同一个
    /// <see cref="HybridBakeService.AllowsDirectPlayback"/>）；Vulkan 档位在裁剪已知时改走 GPU 编码整条管线。
    /// 淡化窗口末尾的强制 IDR、源周期路线多留的原帧都在这里定。
    /// </summary>
    private RenderRequest MasterRequest(int index, bool allowGpu = true, JsonObject? coverage = null)
    {
        var group = groups[index];
        string groupId = group["id"]!.GetValue<string>();
        var capture = Capture(group);
        var framing = Framing(index);
        bool direct = HybridBakeService.AllowsDirectPlayback(capture.SceneClear, framing.Residual, request.ProbeFrames);
        var render = new RenderRequest(captureProject, settings.Assets, Path.Combine(ProjectSource.ContainedPath(output, groupId), "master"),
            capture.PixelWidth, capture.PixelHeight, settings.FpsNumerator, settings.FpsDenominator,
            framing.RenderedFrames, WarmupFrames: framing.MasterWarmupFrames,
            Seed: 17, UserProperties: snapshot, PixelPacking: capture.SceneClear ? "rgb" : "rgba_side_by_side",
            // 直编组是不透明整幅；硬件档位预判超 2 GiB 时改用软件编码（原因由 GroupEncoder 记）。
            LosslessTest: !direct, PlaybackEncoderKind: direct ?
                playbackKind == PlaybackEncoderSelection.Vulkan ||
                EmbeddedVideoBudget.HardwareOverBudget(frames, capture.PixelWidth, capture.PixelHeight, packedAlpha: false)
                    ? PlaybackEncoderSelection.Software : playbackKind : null,
            DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid, CollectAlphaBounds: true, BoundsIncludeRgb: true,
            EffectRenderScale: request.EffectRenderScale,
            MatchEffectResolution: request.MatchEffectResolution, HdrScale: capture.HdrScale,
            Input: new JsonObject { ["cursor_x"] = .5, ["cursor_y"] = .5, ["cursor_in_window"] = true },
            OrthographicCaptureViewport: new(CenterX, CenterY, capture.Width, capture.Height),
            LayerSelection: new(capture.Layers, TransparentBackground: !capture.SceneClear, IncludePostprocessing: false),
            TraceScene: !probe,
            ForceKeyFrameFrame: framing.Residual ? crossfadeFrames : null,
            OfflineVideoRateOverrides: probe ? null : HybridBakeService.SelectVideoRateOverrides(plan["loop"]!.AsObject(), capture.Layers.ToHashSet()),
            EncodedFrames: framing.ClosureJudged ? frames : null,
            RetainFrames: probe ? null : LoopClosureCheck.ReferenceFrameIndices(frames));
        if (allowGpu && playbackKind == PlaybackEncoderSelection.Vulkan && !probe &&
            render.Width % 2 == 0 && render.Height % 2 == 0)
        {
            (CacheRegion Crop, bool Packed)? known = capture.SceneClear
                ? (new CacheRegion((int)render.Width, (int)render.Height, 0, 0, (int)render.Width, (int)render.Height), false)
                : (StartSearches.TryGetValue(groupId, out JsonObject? groupSearch)
                    ? NativeRenderRunner.SamplingCrop(groupSearch["sampling_coverage"] as JsonObject, render) : null)
                    ?? NativeRenderRunner.SamplingCrop(coverage, render);
            if (known is { } layout)
            {
                string codec = PlaybackEncodeProfile.HardwareEncoder(PlaybackEncodeProfile.SelectPlaybackEncoder(
                    (uint)layout.Crop.Width * (layout.Packed ? 2u : 1u), (uint)layout.Crop.Height,
                    render.FpsNumerator, render.FpsDenominator), PlaybackEncoderSelection.Vulkan);
                render = render with { LosslessTest = false, PlaybackEncoderKind = null, ForceKeyFrameFrame = null,
                    EncodedFrames = frames, PixelPacking = layout.Packed ? "rgba_side_by_side" : "rgb",
                    GpuEncoding = new(codec, Qp: coverage is null ? 18 : 12, CrossfadeFrames: framing.Residual ? crossfadeFrames : 0,
                        Crop: layout.Crop, RetainLoopWindow: framing.Residual, RetainQualitySamples: true) };
            }
        }
        // 其余档位的透明组：无损 master 只编覆盖度预通道给出的内容框（与成品裁剪同一套取整与硬件解码扩边）。
        else if (render.LosslessTest && NativeRenderRunner.SamplingCrop(coverage, render) is ({ } crop, true) &&
                 (crop.Width != crop.CaptureWidth || crop.Height != crop.CaptureHeight))
            render = render with { MasterCrop = crop };
        return render;
    }

    /// <summary>
    /// 异步启动一个组的主渲染：构造请求时抛出的异常留在任务里，等轮到这个组时才浮出来，不会打乱前面组的判定顺序。
    /// 透明组的裁剪未知时先跑一遍 GPU 覆盖度预通道拿全片裁剪：Vulkan 档再直编（全片全零时直接走空组路径），其余档位的无损 master 只编内容框。
    /// GPU 编码初始化失败时把 master 挪开，按 CPU 路线重渲一遍。
    /// </summary>
    private async Task<JsonObject> StartAsync(int index)
    {
        RenderRequest render = MasterRequest(index);
        if (Directory.Exists(render.OutputDirectory) || File.Exists(render.OutputDirectory))
            throw new IOException("A group master output must be new; existing files will not be cleaned.");
        JsonObject? coveragePass = null;
        if (render.GpuEncoding is null && !probe && render.Width % 2 == 0 && render.Height % 2 == 0 &&
            (playbackKind == PlaybackEncoderSelection.Vulkan || render is { LosslessTest: true, PixelPacking: "rgba_side_by_side" }))
        {
            // An unknown crop used to require a full RGBA lossless master
            // followed by decoding and encoding again. A GPU-only bounds
            // pass supplies the same all-frame crop before direct encoding.
            string boundsOutput = Path.Combine(Path.GetDirectoryName(render.OutputDirectory)!, "capture-bounds");
            var boundsRequest = render with { OutputDirectory = boundsOutput, GpuEncoding = null,
                LosslessTest = false, PlaybackEncoderKind = null, ForceKeyFrameFrame = null,
                CollectAlphaBounds = false, EncodedFrames = null, RetainFrames = null,
                FrameSamplesOnly = true, FrameSampleStride = checked((uint)render.Frames),
                FrameSampleWidth = 64, FrameSampleIncludeAlpha = true, CollectSamplingCoverage = true };
            JsonObject measured = await runner.RenderAsync(boundsRequest, progress, renderCancellation.Token);
            render = MasterRequest(index, coverage: measured["sampling_coverage"] as JsonObject);
            coveragePass = new JsonObject { ["status"] = measured["sampling_coverage_status"]?.DeepClone(),
                ["manifest_path"] = Path.Combine(boundsOutput, "manifest.json"),
                ["frames"] = render.Frames, ["readback_frames"] = measured["readback_frames"]?.DeepClone(),
                ["renderer_wall_seconds"] = measured["native_result"]?["wall_seconds"]?.DeepClone() };
            if (playbackKind == PlaybackEncoderSelection.Vulkan && measured["sampling_coverage"] is JsonObject emptyCoverage &&
                emptyCoverage["has_content"]?.GetValue<bool>() == false)
            {
                // Every full-resolution RGBA pixel was zero over the
                // complete interval. Reuse its full dependency trace and
                // take the existing empty-group path without encoding it.
                measured["alpha_bounds"] = emptyCoverage.DeepClone();
                measured["gpu_bounds_prepass"] = coveragePass;
                await File.WriteAllTextAsync(Path.Combine(boundsOutput, "manifest.json"),
                    measured.ToJsonString(JsonOptions), renderCancellation.Token);
                return measured;
            }
        }
        try
        {
            JsonObject rendered = await runner.RenderAsync(render, progress, renderCancellation.Token);
            rendered["gpu_bounds_prepass"] = coveragePass;
            return rendered;
        }
        catch (GpuEncodeUnavailableException error) when (render.GpuEncoding is not null && !renderCancellation.IsCancellationRequested)
        {
            string parent = Path.GetDirectoryName(render.OutputDirectory)!;
            string failed = ProjectSource.ContainedPath(parent, "master.gpu-unavailable");
            Directory.Move(render.OutputDirectory, failed);
            JsonObject fallback = await runner.RenderAsync(MasterRequest(index, allowGpu: false), progress, renderCancellation.Token);
            fallback["gpu_pipeline_fallback_reason"] = error.Message;
            fallback["gpu_bounds_prepass"] = coveragePass;
            return fallback;
        }
    }
}
