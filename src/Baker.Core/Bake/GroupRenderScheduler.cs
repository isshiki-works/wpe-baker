using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>一个视频组的捕获几何：源图层、是否含场景底色、正交捕获视口（场景单位）与像素尺寸（向上取偶）。</summary>
internal readonly record struct GroupCapture(int[] Layers, bool SceneClear, double Width, double Height, uint PixelWidth, uint PixelHeight,
    double? HdrScale = null);

/// <summary>
/// 一个组的渲染帧数与预热（P 是这个组自己的周期 P_g）。残差组多渲一个淡化窗口；其余成品组多渲 1 帧（第 P 帧原帧留作闭合检验与接缝参照）；探针只渲 P 帧。
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
    HybridAnalyzeRequest settings, JsonObject[] groups, string captureProject, string output, JsonObject snapshot, ulong frames, ulong[] groupFrames,
    uint crossfadeFrames, ulong warmupFrames, int[] residualGroupIndexes, int groupParallel, string playbackKind,
    IProgress<RenderProgress>? progress, CancellationToken cancellationToken) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly JsonObject projection = plan["projection"]!.AsObject();
    private readonly JsonArray loopCandidates = plan["loop"]?["candidates"] as JsonArray ?? new JsonArray();
    private readonly bool probe = request.ProbeFrames > 0;
    /// <summary>NVIDIA 上 GPU 直编按本机偏好先试的格式（AV1 → HEVC），见 <see cref="PlaybackEncoderSelection.PreferredGpuCodecs"/>。</summary>
    internal string[] PreferredCodecs { get; } = playbackKind == PlaybackEncoderSelection.Vulkan && request.ProbeFrames == 0
        ? PlaybackEncoderSelection.PreferredGpuCodecs(request.DeviceUuid ?? settings.DeviceUuid) : [];
    /// <summary>渲染器报这块卡开不了 NVENC AV1（如没有 AV1 编码单元）后，其余组不再试 AV1。</summary>
    private volatile bool av1Unavailable;
    private readonly Dictionary<int, Task<JsonObject>> renders = [];
    private int nextStart;
    private bool closed;
    private readonly CancellationTokenSource renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    /// <summary>GPU 长组分段渲染时同时在跑的段进程上限（与组并行数相同），各组的段排同一个队。</summary>
    private readonly SemaphoreSlim segmentSlots = new(groupParallel);

    internal double CenterX => projection["center_x"]!.GetValue<double>();
    internal double CenterY => projection["center_y"]!.GetValue<double>();
    internal bool PreserveParallax => plan["has_parallax"]?.GetValue<bool>() == true && settings.ViewMode == "preserve";
    internal JsonObject[] Groups => groups;
    /// <summary>按输出短边 / 1080 缩放像素口径（瓦片、样本宽）的倍率，1080p 下恰为 1。</summary>
    internal double TileScale => SwayRecurrenceSolver.SpeedLimitScale(settings.Width, settings.Height);
    /// <summary>第 index 组录制的帧数 P_g（plan 候选的 group_frames，缺省为 L；探针为探针帧数）。</summary>
    internal ulong Frames(int index) => groupFrames[index];
    internal uint CrossfadeFrames => crossfadeFrames;
    internal int[] ResidualGroupIndexes => residualGroupIndexes;

    /// <summary>预热之后、解析周期内的起点相位：残差路线由起点搜索定，源周期路线为 0。</summary>
    internal ulong StartFrame { get; set; }

    /// <summary>
    /// 主渲染前的内嵌视频体积外推（bake.json 的 embedded_video_estimate，合成校验道给出；探针与读不到时 null）。
    /// 每组首次主渲染前等它，按其中的 quantizer_offset 抬高所有播放档的量化值；编码后实际仍超上限就按实际字节再抬一次。
    /// </summary>
    internal Task<JsonObject?> SizeEstimate { get; set; } = Task.FromResult<JsonObject?>(null);

    // 各组当前的体积量化值增量与"不抬量化值时"的字节估计（拒绝理由里的"需要多大"）。
    private readonly Dictionary<int, (int Offset, double Unconstrained)> sizeBudgets = [];

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
        ulong period = groupFrames[index];
        ulong rendered = residual ? checked(period + crossfadeFrames) : closureJudged ? checked(period + 1) : period;
        ulong sourcePeriodWarmup = probe ? 0 : LoopWarmupJson.CandidateSourcePeriodWarmupFrames(loopCandidates);
        return new(residual, closureJudged, rendered, sourcePeriodWarmup,
            LoopWarmup.MasterFrames(sourcePeriodWarmup, frames, warmupFrames, StartFrame));
    }

    /// <summary>
    /// 残差组的低分辨率起点评分样本：搜索窗是本组周期 P 再加 <paramref name="candidateFrames"/> 帧（候选起点 s 落在这段里，
    /// 每个比较 (s, s+P)），最多 <see cref="ResidualMasking.SearchWindowPeriods"/> 个周期；透明组用带覆盖度的样本，两半分别算。
    /// </summary>
    internal RenderRequest StartSearchRequest(int index, uint sampleStride, ulong candidateFrames) =>
        StartSearchRequest(groups[index], Capture(groups[index]), sampleStride, checked(groupFrames[index] + candidateFrames));

    private RenderRequest StartSearchRequest(JsonObject group, GroupCapture capture, uint sampleStride, ulong windowFrames) =>
        new(captureProject, settings.Assets,
            ProjectSource.ContainedPath(output, $"{group["id"]!.GetValue<string>()}.start-search"), capture.PixelWidth, capture.PixelHeight,
            settings.FpsNumerator, settings.FpsDenominator, windowFrames,
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
            // 覆盖度只给透明组定直编的裁剪（不透明组裁剪恒为整幅），只量取样帧、外加取样边距；
            // 取样帧之间漏掉的内容由主渲染的全片覆盖度兜底（StartAsync 核对，超出就按实测重渲）。
            CollectSamplingCoverage: !capture.SceneClear, SampledCoverageOnly: true,
            FrameSampleIncludeAlpha: !capture.SceneClear,
            OfflineVideoRateOverrides: HybridBakeService.SelectVideoRateOverrides(plan["loop"]!.AsObject(), capture.Layers.ToHashSet()));

    /// <summary>主渲染输出目录是新的：已启动的组启动时自己查过；没启动的在这里查，和补启动互斥，不会把刚补启动建的目录当成旧文件。</summary>
    internal bool FreshOutput(int index, string masterPath)
    {
        lock (renders) return renders.ContainsKey(index) || !(Directory.Exists(masterPath) || File.Exists(masterPath));
    }

    /// <summary>分析阶段的慢分量闭合预检（<see cref="SlowClosureProbe"/>）：同一个主渲染请求走 CPU 路线，只编第 0 帧，第 0、P 帧原帧留给闭合检验。</summary>
    internal RenderRequest ClosureProbeRequest(int index, string outputDirectory) =>
        MasterRequest(index, allowGpu: false) with { OutputDirectory = outputDirectory, EncodedFrames = 1, TraceScene = false };

    /// <summary>取这个组的主渲染（没启动就现在启动），并把在飞的主渲染补到 groupParallel 个。</summary>
    internal Task<JsonObject> RenderAsync(int index)
    {
        lock (renders)
        {
            if (!renders.ContainsKey(index)) Start(index);
            Fill();
            return renders[index];
        }
    }

    /// <summary>
    /// 在飞（未完成）的主渲染不足 groupParallel 个时按组序补启动，每个主渲染完成时再补一次。
    /// 窗口按在飞数算、不按调用方读到第几组算：前面的大组还没被读走时后面的组不必干等
    /// （3803167460 的 group-5 原来要等 group-2 编完被读走才启动，两个约 55 s 的编码串成了一条）。
    /// </summary>
    private void Fill()
    {
        for (; !closed && nextStart < groups.Length && renders.Values.Count(render => !render.IsCompleted) < groupParallel; ++nextStart)
            if (!renders.ContainsKey(nextStart)) Start(nextStart);
    }

    private void Start(int index)
    {
        Task<JsonObject> render = renders[index] = StartAsync(index);
        _ = render.ContinueWith(_ => { lock (renders) Fill(); }, TaskScheduler.Default);
    }

    /// <summary>取消还在飞的主渲染并等渲染器退出；这些结果已经不要了，取消、失败都咽掉（真正的失败早已从主流程抛出过一次）。</summary>
    public async ValueTask DisposeAsync()
    {
        Task<JsonObject>[] pending;
        lock (renders) { closed = true; pending = [.. renders.Values]; renders.Clear(); }
        if (pending.Length > 0)
        {
            await renderCancellation.CancelAsync();
            foreach (var render in pending) try { await render; } catch { }
        }
        renderCancellation.Dispose();
    }

    /// <summary>
    /// 一个组的主渲染请求。直编组不写无损 master：渲染器出帧直接进播放档编码器（判定与组循环走同一个
    /// <see cref="HybridBakeService.AllowsDirectPlayback"/>）；裁剪已知时，Vulkan 档位改走 GPU 编码整条管线，
    /// 其余档位的透明组、残差组在 CPU 管道里按同一裁剪直编（淡化见 <see cref="RenderRequest.DirectCrossfadeFrames"/>）。
    /// 淡化窗口末尾的强制 IDR、源周期路线多留的原帧都在这里定。
    /// </summary>
    private RenderRequest MasterRequest(int index, bool allowGpu = true, JsonObject? coverage = null,
        IReadOnlySet<string>? failedCodecs = null)
    {
        var group = groups[index];
        string groupId = group["id"]!.GetValue<string>();
        var capture = Capture(group);
        var framing = Framing(index);
        ulong period = groupFrames[index];
        bool direct = HybridBakeService.AllowsDirectPlayback(capture.SceneClear, framing.Residual, request.ProbeFrames);
        // CPU 直编的档位：Vulkan 档位（GPU 管线不可用时才走到这里）用软件编码；其余硬件档先用硬件，
        // 编完超 2 GiB 或画质门不过由 StartAsync 把这一组改软件重渲。
        string cpuKind = playbackKind == PlaybackEncoderSelection.Vulkan ? PlaybackEncoderSelection.Software : playbackKind;
        var render = new RenderRequest(captureProject, settings.Assets, Path.Combine(ProjectSource.ContainedPath(output, groupId), "master"),
            capture.PixelWidth, capture.PixelHeight, settings.FpsNumerator, settings.FpsDenominator,
            framing.RenderedFrames, WarmupFrames: framing.MasterWarmupFrames,
            Seed: 17, UserProperties: snapshot, PixelPacking: capture.SceneClear ? "rgb" : "rgba_side_by_side",
            // 直编组是不透明整幅。
            LosslessTest: !direct, PlaybackEncoderKind: direct ? cpuKind : null, QuantizerOffset: SizeOffset(index),
            DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid, CollectAlphaBounds: true, BoundsIncludeRgb: true,
            EffectRenderScale: request.EffectRenderScale,
            MatchEffectResolution: request.MatchEffectResolution, HdrScale: capture.HdrScale,
            Input: new JsonObject { ["cursor_x"] = .5, ["cursor_y"] = .5, ["cursor_in_window"] = true },
            OrthographicCaptureViewport: new(CenterX, CenterY, capture.Width, capture.Height),
            LayerSelection: new(capture.Layers, TransparentBackground: !capture.SceneClear, IncludePostprocessing: false),
            TraceScene: !probe,
            ForceKeyFrameFrame: framing.Residual ? crossfadeFrames : null,
            OfflineVideoRateOverrides: probe ? null : HybridBakeService.SelectVideoRateOverrides(plan["loop"]!.AsObject(), capture.Layers.ToHashSet()),
            EncodedFrames: framing.ClosureJudged ? period : null,
            RetainFrames: probe ? null : LoopClosureCheck.ReferenceFrameIndices(period));
        if (!probe && render.Width % 2 == 0 && render.Height % 2 == 0)
        {
            (CacheRegion Crop, bool Packed)? known = capture.SceneClear
                ? (new CacheRegion((int)render.Width, (int)render.Height, 0, 0, (int)render.Width, (int)render.Height), false)
                : NativeRenderRunner.SamplingCrop(coverage, render) ??
                    (StartSearches.TryGetValue(groupId, out JsonObject? groupSearch)
                        ? NativeRenderRunner.SamplingCrop(groupSearch["sampling_coverage"] as JsonObject, render) : null);
            // 直编的透明打包只有左右并排（画质门与接缝参照按它排）；越宽度上限要上下并排的透明组走 master 路线。
            if (known is { } layout && !HardwareDecodeDimensions.StackedVertically(layout.Packed, layout.Crop.Width))
            {
                // 没有 GPU 直编：透明组、残差组也边渲染边编码成品，不写无损 master（核显上重型作品原要几百 GiB 临时空间）。
                if (!(allowGpu && playbackKind == PlaybackEncoderSelection.Vulkan))
                    return direct ? render : render with { LosslessTest = false, ForceKeyFrameFrame = null, EncodedFrames = period,
                        PixelPacking = layout.Packed ? "rgba_side_by_side" : "rgb", DirectCrop = layout.Crop,
                        DirectCrossfadeFrames = framing.Residual ? crossfadeFrames : null,
                        PlaybackEncoderKind = cpuKind };
                // Vulkan 编码有最小编码尺寸：RTX 5090 驱动 610.62 报 H.264/HEVC minCodedExtent 160x64（vulkaninfo --show-video-props）。
                // FFmpeg 拿按 16 对齐后的尺寸比：72 宽透明组打包成 144 被拒、整组落到 CPU 无损路线（3757825891 group-4 读回约 200 s）；
                // 152 对齐成 160 放行，但实际尺寸低于下限。不足时在捕获范围内居中加宽/加高裁剪，多出的是透明像素；打包的每半幅算一半宽。
                // NVIDIA 上先取本机偏好里这一组还没失败过的第一档（AV1 → HEVC），都用完或没有偏好时照原规则取 H.264/HEVC。
                string? preferred = PreferredCodecs.FirstOrDefault(c => failedCodecs?.Contains(c) != true &&
                    !(c == PlaybackEncoderSelection.Av1Nvenc && av1Unavailable));
                // NVDEC 解 AV1 要高 ≥128（本机 5090 D3D11VA 实测 96 高失败、128 通过）；NVENC AV1 要宽 >128，下面的 160 已满足。
                int minimum = layout.Packed ? 80 : 160, minimumHeight = preferred == PlaybackEncoderSelection.Av1Nvenc ? 128 : 64;
                CacheRegion crop = layout.Crop;
                if (crop.Width < minimum && crop.CaptureWidth >= minimum)
                    crop = crop with { X = Math.Clamp((crop.X - (minimum - crop.Width) / 2) & ~1, 0, crop.CaptureWidth - minimum), Width = minimum };
                if (crop.Height < minimumHeight && crop.CaptureHeight >= minimumHeight)
                    crop = crop with { Y = Math.Clamp((crop.Y - (minimumHeight - crop.Height) / 2) & ~1, 0, crop.CaptureHeight - minimumHeight),
                        Height = minimumHeight };
                int sizeOffset = SizeOffset(index);
                string codec = preferred ?? PlaybackEncodeProfile.HardwareEncoder(PlaybackEncodeProfile.SelectPlaybackEncoder(
                    (uint)crop.Width * (layout.Packed ? 2u : 1u), (uint)crop.Height,
                    render.FpsNumerator, render.FpsDenominator), PlaybackEncoderSelection.Vulkan);
                // 不按固定码率系数预判 2 GiB：ARCH2 实测 Vulkan 成品相对参考码率 0.02–2.0 倍，随内容变、不随格式定。
                // 成品实际超限时由 StartAsync 按软件档重渲。
                render = render with { LosslessTest = false, PlaybackEncoderKind = null, ForceKeyFrameFrame = null,
                    EncodedFrames = period, PixelPacking = layout.Packed ? "rgba_side_by_side" : "rgb",
                    GpuEncoding = new(codec, Qp: Math.Min(51, 18 + sizeOffset), CrossfadeFrames: framing.Residual ? crossfadeFrames : 0,
                        Crop: crop, RetainLoopWindow: framing.Residual, RetainQualitySamples: true) };
            }
        }
        return render;
    }

    /// <summary>
    /// 异步启动一个组的主渲染：构造请求时抛出的异常留在任务里，等轮到这个组时才浮出来，不会打乱前面组的判定顺序。
    /// Vulkan 透明组的裁剪未知时先跑一遍 GPU 覆盖度预通道拿全片裁剪，再直编；全片全零时直接走空组路径。
    /// GPU 直编的画质门在这里过：不过先降 QP 在 GPU 上重渲一次，还不过与 GPU 编码初始化失败一样，把 master 挪开按 CPU 路线重渲。
    /// 按本机偏好选的 AV1/HEVC 下面还有一档时，另过一次本机硬解实测；开不了编码器、任一道门不过都降一档重编，末档照原样。
    /// </summary>
    private async Task<JsonObject> StartAsync(int index)
    {
        JsonObject? estimate = await SizeEstimate;
        // 外推来自 libx264 crf16 的试编，增量按软件档的码率律算；GPU 编码器的码率律不同，改走 CPU 路线时回到这个起点重新校正。
        (int Offset, double Unconstrained) predicted = (estimate?["quantizer_offset"]?.GetValue<int>() ?? 0,
            (estimate?["groups"] as JsonArray ?? []).Max(group => group?["predicted_bytes"]?.GetValue<long>()) ?? 0);
        lock (sizeBudgets) sizeBudgets[index] = predicted;
        bool sizeRetried = false;
        RenderRequest render = MasterRequest(index);
        if (Directory.Exists(render.OutputDirectory) || File.Exists(render.OutputDirectory))
            throw new IOException("A group master output must be new; existing files will not be cleaned.");
        // 预热帧照样逐帧软解视频纹理，分段时第 k 段要把段起点前的视频从头解一遍，解码又已吃满 CPU：
        // 3462279189 的视频组（3480x2250 H.264）三段实测 59/91/105 s，不分段的覆盖度预通道 62 s。有视频纹理的组不分段。
        var layers = Capture(groups[index]).Layers;
        int segments = loopCandidates.FirstOrDefault()?["components"]?.AsArray().Any(component =>
            component?["id"]?.GetValue<string>().Split('/') is ["video", var owner, ..] &&
            int.TryParse(owner, out int layer) && layers.Contains(layer)) == true ? 1 : groupParallel;
        JsonObject? coveragePass = null, coverage = null;
        // 透明组的裁剪未知（还在走 master）时先跑覆盖度预通道；起点搜索已给出裁剪却没取直编（须上下并排）时，预通道的裁剪同样用不上，不跑。
        if (render.LosslessTest && !probe && render.PixelPacking != "rgb" && render.Width % 2 == 0 && render.Height % 2 == 0 &&
            !(StartSearches.TryGetValue(groups[index]["id"]!.GetValue<string>(), out JsonObject? search) &&
                NativeRenderRunner.SamplingCrop(search["sampling_coverage"] as JsonObject, render) is not null))
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
            coverage = measured["sampling_coverage"] as JsonObject;
            render = MasterRequest(index, coverage: coverage);
            coveragePass = new JsonObject { ["status"] = measured["sampling_coverage_status"]?.DeepClone(),
                ["manifest_path"] = Path.Combine(boundsOutput, "manifest.json"),
                ["frames"] = render.Frames, ["readback_frames"] = measured["readback_frames"]?.DeepClone(),
                ["renderer_wall_seconds"] = measured["native_result"]?["wall_seconds"]?.DeepClone() };
            if (measured["sampling_coverage"] is JsonObject emptyCoverage &&
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
        var failedCodecs = new HashSet<string>();
        var codecFallbacks = new JsonArray();
        JsonObject? coverageRerender = null;
        string? fallbackReason = null;
        bool gpuAllowed = true;
        for (; ; )
        {
            RenderRequest? lower = render.GpuEncoding is { } current &&
                MasterRequest(index, coverage: coverage, failedCodecs: new HashSet<string>(failedCodecs) { current.Codec }) is
                    { GpuEncoding.Codec: var next } candidate && next != current.Codec ? candidate : null;
            try
            {
                JsonObject rendered = await runner.RenderSegmentsAsync(render, segments, segmentSlots, progress, renderCancellation.Token);
                if ((render.GpuEncoding?.Crop ?? render.DirectCrop) is { } crop && !Capture(groups[index]).SceneClear &&
                    rendered["alpha_bounds"] is JsonObject all && !CoverageFits(all, crop, render.PixelPacking == "rgba_side_by_side"))
                {
                    // 起点搜索的裁剪只量了取样帧：主渲染逐帧累计的全片覆盖度超出裁剪、或有半透明却没打包时，按实测覆盖度重渲这一组。
                    // 实测覆盖度就是这次渲染的全部帧，重渲后必然落在裁剪内；再不符只能是渲染不确定，停下不交出漏内容的成品。
                    if (coverage?["basis"]?.GetValue<string>() == MeasuredCoverageBasis)
                        throw new InvalidDataException("GPU master coverage still exceeds the crop derived from its own measured coverage.");
                    Directory.Move(render.OutputDirectory, ProjectSource.ContainedPath(Path.GetDirectoryName(render.OutputDirectory)!, "master.coverage-miss"));
                    coverage = all.DeepClone().AsObject();
                    coverage["status"] = "complete"; coverage["basis"] = MeasuredCoverageBasis;
                    coverage["first_simulation_frame"] = render.WarmupFrames; coverage["frames"] = render.Frames;
                    coverage["render_request"] = JsonSerializer.SerializeToNode(render, JsonOptions);
                    coverageRerender = new JsonObject { ["crop"] = JsonSerializer.SerializeToNode(crop, JsonOptions),
                        ["packed_alpha"] = render.PixelPacking == "rgba_side_by_side", ["measured"] = all.DeepClone() };
                    render = MasterRequest(index, gpuAllowed, coverage, failedCodecs);
                    continue;
                }
                if (render.GpuEncoding is { } gpu && !await GpuQualityPassesAsync(rendered, render))
                {
                    // 体积限制下不降 QP（降了会超上限）：直接改走 CPU 路线，软件档同一量化值再过一次画质门。
                    if (SizeOffset(index) > 0)
                        throw new GpuEncodeUnavailableException($"GPU playback quality gate failed at QP {gpu.Qp} under the embedded-video size budget " +
                            $"(SSIM {rendered["playback_quality_gate"]?["measured_ssim"]?.ToJsonString()}).");
                    Directory.Move(render.OutputDirectory, ProjectSource.ContainedPath(
                        Path.GetDirectoryName(render.OutputDirectory)!, $"master.{gpu.Codec}-qp{gpu.Qp}"));
                    render = render with { GpuEncoding = gpu with { Qp = gpu.Qp - 6 } };
                    rendered = await runner.RenderSegmentsAsync(render, segments, segmentSlots, progress, renderCancellation.Token);
                    if (!await GpuQualityPassesAsync(rendered, render))
                        throw new GpuEncodeUnavailableException($"GPU playback quality gate failed at QP {gpu.Qp} and {gpu.Qp - 6}.");
                }
                // 直编成品按实际字节判内嵌视频上限：第一次超了按实际字节再抬量化值、同一档重渲；再超则硬件档改软件重渲
                // （GPU 组走 CPU 路线），软件档交给整案按体积拒绝。
                bool cpuHardware = render.GpuEncoding is null && render.PlaybackEncoderKind is { } kind && kind != PlaybackEncoderSelection.Software;
                if ((render.GpuEncoding is not null || render.PlaybackEncoderKind is not null) &&
                    new FileInfo(Path.Combine(render.OutputDirectory, "preview.mp4")).Length is var bytes && bytes > EmbeddedVideoBudget.MaximumBytes)
                {
                    if (!sizeRetried)
                    {
                        sizeRetried = true;
                        lock (sizeBudgets)
                        {
                            var (offset, unconstrained) = sizeBudgets[index];
                            sizeBudgets[index] = (offset + EmbeddedVideoBudget.QuantizerOffset(bytes),
                                Math.Max(unconstrained, bytes * Math.Pow(2, offset / 6d)));
                        }
                        Directory.Move(render.OutputDirectory, ProjectSource.ContainedPath(Path.GetDirectoryName(render.OutputDirectory)!, "master.over-size"));
                        render = MasterRequest(index, gpuAllowed, coverage, failedCodecs);
                        continue;
                    }
                    if (render.GpuEncoding is not null || cpuHardware)
                        throw new GpuEncodeUnavailableException(NativeRenderRunner.HardwareOverLimitReason(
                            render.GpuEncoding?.Codec ?? render.PlaybackEncoderKind!, bytes));
                }
                // CPU 硬件档（mf/nvenc/amf）直编没有母版可升档重编：画质门不过也只让这一组改软件重渲，不整张拒。
                if (cpuHardware && !await GpuQualityPassesAsync(rendered, render))
                    throw new GpuEncodeUnavailableException($"{render.PlaybackEncoderKind} 直编成品画质门未过（SSIM " +
                        $"{rendered["playback_quality_gate"]?["measured_ssim"]?.ToJsonString()}），这一组改用软件编码。");
                // 按体积抬了量化值的软件直编也要过画质门；不过时读数留在结果上，由整案按"体积限制下画质门未过"拒绝。
                if (render.PlaybackEncoderKind == PlaybackEncoderSelection.Software && SizeOffset(index) > 0)
                    await GpuQualityPassesAsync(rendered, render);
                if (SizeOffset(index) > 0)
                    lock (sizeBudgets) rendered["size_budget"] = new JsonObject { ["quantizer_offset"] = sizeBudgets[index].Offset,
                        ["unconstrained_bytes"] = Math.Round(sizeBudgets[index].Unconstrained), ["target_bytes"] = EmbeddedVideoBudget.TargetBytes };
                if (lower is not null)
                {
                    // 播放机就是本机：成品要在本机播放用的显卡上能硬解（WPE 经 MF 放视频，扩展装了但显卡解不了一样放不动）。
                    JsonObject decode = await runner.ProbeHardwareDecodeAsync(Path.Combine(render.OutputDirectory, "preview.mp4"),
                        Path.Combine(render.OutputDirectory, "hardware-decode"), Math.Min(groupFrames[index], 5), renderCancellation.Token, render.DeviceUuid);
                    if (decode["all_adapters_passed"]?.GetValue<bool>() != true)
                        throw new GpuEncodeUnavailableException($"{render.GpuEncoding!.Codec} did not pass hardware decoding on this machine's playback adapter.");
                    rendered["hardware_decode"] = decode;
                }
                rendered["gpu_bounds_prepass"] = coveragePass;
                if (coverageRerender is not null) rendered["coverage_rerender"] = coverageRerender;
                if (codecFallbacks.Count > 0) rendered["gpu_codec_fallbacks"] = codecFallbacks;
                if (fallbackReason is not null) rendered["gpu_pipeline_fallback_reason"] = fallbackReason;
                return rendered;
            }
            // ffmpeg 硬件档（mf/nvenc/amf）直编的组编码器开不了、中途退出、超 2 GiB 或画质门不过：这一组改用软件编码重渲，原因带给 GroupEncoder。
            catch (GpuEncodeUnavailableException error) when (render.GpuEncoding is null && !renderCancellation.IsCancellationRequested)
            {
                Directory.Move(render.OutputDirectory, ProjectSource.ContainedPath(Path.GetDirectoryName(render.OutputDirectory)!, "master.hardware-failed"));
                fallbackReason = error.Message;
                render = render with { PlaybackEncoderKind = PlaybackEncoderSelection.Software };
            }
            catch (GpuEncodeUnavailableException error) when (render.GpuEncoding is not null && !renderCancellation.IsCancellationRequested)
            {
                string parent = Path.GetDirectoryName(render.OutputDirectory)!;
                if (lower is not null)
                {
                    string codec = render.GpuEncoding.Codec;
                    if (error.Message.Contains("NVENC AV1 is unavailable", StringComparison.Ordinal)) av1Unavailable = true;
                    if (Directory.Exists(render.OutputDirectory))
                        Directory.Move(render.OutputDirectory, ProjectSource.ContainedPath(parent, $"master.{codec}-failed"));
                    codecFallbacks.Add(new JsonObject { ["codec"] = codec, ["reason"] = error.Message });
                    failedCodecs.Add(codec);
                    render = lower;
                    continue;
                }
                // GPU 管线用不了（含画质门降 QP 仍不过，原因里带 QP）：这一组按同一裁剪改走 CPU 直编的软件档，原因写进 bake.json。
                Directory.Move(render.OutputDirectory, ProjectSource.ContainedPath(parent, "master.gpu-unavailable"));
                fallbackReason = error.Message;
                gpuAllowed = false;
                lock (sizeBudgets) sizeBudgets[index] = (predicted.Offset, Math.Max(predicted.Unconstrained, sizeBudgets[index].Unconstrained));
                sizeRetried = false;
                render = MasterRequest(index, allowGpu: false, coverage: coverage);
            }
        }
    }

    private int SizeOffset(int index) { lock (sizeBudgets) return sizeBudgets.TryGetValue(index, out var budget) ? budget.Offset : 0; }

    private const string MeasuredCoverageBasis = "GPU master alpha bounds over every encoded frame.";

    /// <summary>主渲染逐帧累计的覆盖度落在裁剪内，且有半透明像素时成品是打包的。</summary>
    private static bool CoverageFits(JsonObject all, CacheRegion crop, bool packed) =>
        all["has_content"]?.GetValue<bool>() != true ||
        (all["x"]!.GetValue<int>() >= crop.X && all["y"]!.GetValue<int>() >= crop.Y &&
         all["x"]!.GetValue<int>() + all["width"]!.GetValue<int>() <= crop.X + crop.Width &&
         all["y"]!.GetValue<int>() + all["height"]!.GetValue<int>() <= crop.Y + crop.Height &&
         (packed || all["minimum_alpha"]!.GetValue<int>() == 255));

    /// <summary>硬件直编成品的画质门；结果挂在渲染结果上，GroupEncoder 接管成品时直接取用。CPU 直编的不透明组是整幅。</summary>
    private async Task<bool> GpuQualityPassesAsync(JsonObject rendered, RenderRequest render)
    {
        JsonObject gate = await runner.GpuPlaybackQualityAsync(rendered, Path.Combine(render.OutputDirectory, "preview.mp4"),
            render.GpuEncoding?.Crop ?? render.DirectCrop ?? new CacheRegion((int)render.Width, (int)render.Height, 0, 0, (int)render.Width, (int)render.Height),
            render.PixelPacking == "rgba_side_by_side", render.OutputDirectory, renderCancellation.Token);
        rendered["playback_quality_gate"] = gate;
        return gate["passed"]?.GetValue<bool>() == true;
    }
}
