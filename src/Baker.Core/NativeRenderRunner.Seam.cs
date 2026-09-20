using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    /// <summary>已完成 master 超过这个大小就必须分段改写，不允许整片解码再整片重编码。</summary>
    public const long SegmentedRewriteMinimumBytes = 512L * 1024 * 1024;

    /// <summary>分段改写允许真正重编码的最大帧数：一个淡化窗口加一个 GOP 的余量。</summary>
    public const ulong MaximumRewriteReencodedFrames = 1024;

    /// <summary>回读接缝帧与期望混合值允许的最大逐像素偏差，只留给 8 位取整。</summary>
    private const double MaximumCrossfadeWeightDeviation = 2.0;

    /// <summary>找接缝切点时最多翻看的包数，够覆盖一个正常 GOP。</summary>
    private const ulong KeyFrameProbeFrames = 1024;

    /// <summary>
    /// 任何对已完成 master 的 ffmpeg 改写都要先过这道闸。master 超过 512 MiB 时，真正重编码的帧数
    /// 必须既小于全长、又不超过分段上限，其余部分只能 stream copy。2026-09-16 的整机假死就是
    /// 「为了 24 帧淡化把 4 GB 无损 master 整片重编码」造成的，这里让同类写法在开发期直接抛异常，
    /// 而不是在用户机器上把内存和磁盘吃光。
    /// </summary>
    public static void RequireSegmentedMasterRewrite(string videoPath, ulong totalFrames, ulong reencodedFrames, string operation)
    {
        long bytes = new FileInfo(videoPath).Length;
        if (bytes < SegmentedRewriteMinimumBytes) return;
        if (reencodedFrames >= totalFrames)
            throw new InvalidOperationException($"{operation}：{bytes:N0} 字节的 master 不允许全长重编码" +
                $"（{reencodedFrames}/{totalFrames} 帧）。只能重编码接缝所在的片段，其余用 concat demuxer 加 -c copy 原样拼接。");
        if (reencodedFrames > MaximumRewriteReencodedFrames)
            throw new InvalidOperationException($"{operation}：{bytes:N0} 字节的 master 上要重编码 {reencodedFrames} 帧，" +
                $"超过分段上限 {MaximumRewriteReencodedFrames} 帧。把切点对齐到接缝附近的 IDR，其余走 stream copy。");
    }

    /// <summary>
    /// 帧号换算成 -ss/-t 用的秒数。不要靠半帧偏移让 -ss 落在"两帧中间"来取第 K 帧：自带 ffmpeg 会先截断到微秒、
    /// 再按流时基舍入，偏移随帧率与时基变。解码取帧一律走 <see cref="ExactFrameRange"/>。
    /// </summary>
    private static string InputSeconds(double frames, uint numerator, uint denominator) =>
        ExactFrameRange.Seconds(frames, numerator, denominator);

    private static async Task ReadFrameAsync(FileStream stream, byte[] buffer, int index, int frameBytes, CancellationToken token)
    {
        stream.Seek((long)index * frameBytes, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, token);
    }

    /// <summary>
    /// 在已经锁定的解析周期 P 内挑选起点帧。渲染 2 个周期的降采样样本，对每个候选起点在样本上估计第一层的量
    /// max_k M(Δ_k)：样本只落在 k=0 与 k=stride 上，排序键取 max(M(Δ_0), M(Δ_stride))（stride 不在淡化窗口内时只有 Δ_0）。
    /// 先准入（整幅 ≤ 2/255），再按排序键从小到大排；排序键只排序、不准入。前几个候选记成全分辨率回退的尝试顺序。
    /// 周期本身不因此改变，这里只决定相位。样本目录在算完之后立即删除。
    /// </summary>
    public async Task<JsonObject> SearchLoopStartAsync(RenderRequest sampleRequest, ulong periodFrames,
        uint crossfadeFrames, IProgress<RenderProgress>? progress = null, CancellationToken cancellationToken = default,
        ICollection<ResidualStartCandidate>? scoredCandidates = null)
    {
        if (!sampleRequest.FrameSamplesOnly || sampleRequest.FrameSampleStride == 0)
            throw new ArgumentException("起点搜索必须使用只出样本的渲染请求并给出采样步长。");
        // 透明组的样本带覆盖度（左半预乘色、右半覆盖度），两半分别算残差再取大，与全分辨率第一层同一口径。
        bool packed = sampleRequest.FrameSampleIncludeAlpha;
        if (crossfadeFrames == 0) throw new ArgumentException("起点搜索需要知道淡化窗口，才能判断 Δ_stride 是否落在窗口内。");
        uint stride = sampleRequest.FrameSampleStride;
        if (periodFrames == 0 || periodFrames % stride != 0)
            throw new ArgumentException("解析周期必须是采样步长的整数倍，否则候选起点无法对齐到样本。");
        if (sampleRequest.Frames % periodFrames != 0)
            throw new ArgumentException("起点搜索窗口必须是整数个解析周期。");
        int windowPeriods = checked((int)(sampleRequest.Frames / periodFrames));
        // 窗口固定 2 个周期：候选起点只有 P/stride 个，每个比较的是 (s, s+P)，全部落在前 2P 帧里；
        // 更宽的窗口不会引入任何新候选，只会多渲染帧。
        if (windowPeriods != ResidualMasking.SearchWindowPeriods)
            throw new ArgumentException("起点搜索窗口固定为 2 个解析周期：候选只由周期与步长决定，更宽的窗口不会引入新候选。");
        int perPeriod = checked((int)(periodFrames / stride));
        int width, height, tileSize;
        ulong count;
        var scored = new List<ResidualStartCandidate>(perPeriod);
        string sampleDirectory = Path.GetFullPath(sampleRequest.OutputDirectory);
        JsonObject? samplingCoverage = null;
        try
        {
            JsonObject manifest = await RenderAsync(sampleRequest, progress, cancellationToken);
            samplingCoverage = manifest["sampling_coverage"]?.DeepClone().AsObject();
            if (samplingCoverage is not null)
            {
                samplingCoverage["source_sha256"] = manifest["source_sha256"]!.DeepClone();
                samplingCoverage["renderer_sha256"] = manifest["renderer_sha256"]!.DeepClone();
            }
            JsonObject samples = manifest["frame_samples"]?.AsObject()
                ?? throw new InvalidDataException("样本渲染没有产出帧样本清单。");
            string path = samples["path"]!.GetValue<string>();
            width = samples["width"]!.GetValue<int>(); height = samples["height"]!.GetValue<int>();
            count = samples["count"]!.GetValue<ulong>();
            if (count != sampleRequest.Frames / stride) throw new InvalidDataException("帧样本数量与采样步长不一致。");
            if ((samples["includes_packed_alpha"]?.GetValue<bool>() == true) != packed)
                throw new InvalidDataException("帧样本是否带覆盖度与请求不一致。");
            int frameBytes = checked(width * height * 3);
            if (new FileInfo(path).Length != (long)count * frameBytes)
                throw new InvalidDataException("帧样本文件长度与清单不一致。");
            int total = checked((int)count);
            // packed 样本的瓦片边长按单半幅（logical_width）相对画布宽度换算。
            int logicalWidth = packed ? samples["logical_width"]!.GetValue<int>() : width;
            if (packed && logicalWidth * 2 != width) throw new InvalidDataException("带覆盖度的帧样本宽度不是逻辑宽度的两倍。");
            tileSize = Math.Max(4, (int)Math.Round((double)ResidualMasking.SeamTileSize * logicalWidth /
                Math.Max(1, sampleRequest.Width)));
            // 样本文件是 2 个周期 ÷ 步长 × 采样帧大小，高窄画布上能到几个 GB，所以按需读两帧，不整份载入。
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            byte[] left = new byte[frameBytes], right = new byte[frameBytes];
            async Task<LoopWrapResidual> ResidualAsync(int a, int b)
            {
                await ReadFrameAsync(file, left, a, frameBytes, cancellationToken);
                await ReadFrameAsync(file, right, b, frameBytes, cancellationToken);
                return packed
                    ? LoopSeamMetrics.PackedResidual(left, right, logicalWidth, height, tileSize).Combined(logicalWidth)
                    : LoopSeamMetrics.WrapResidual(left, right, width, height, tileSize);
            }
            LoopWrapResidual? next = null;
            for (int candidate = 0; candidate < perPeriod; ++candidate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LoopWrapResidual wrap = next ?? await ResidualAsync(candidate, candidate + perPeriod);
                // Δ_stride 只有在 stride 落在淡化窗口 [0, C) 内、且样本够到 s+stride+P 时才属于第一层的量。
                next = candidate + 1 + perPeriod < total && stride < crossfadeFrames
                    ? await ResidualAsync(candidate + 1, candidate + 1 + perPeriod) : null;
                scored.Add(new((ulong)candidate * stride, wrap.GlobalRgbMae, wrap.WorstTileRgbMae, next?.WorstTileRgbMae));
            }
        }
        finally
        {
            // 样本目录只为这次搜索存在：成功、渲染失败、取消都删，几 GB 的样本不留在 output 下。
            try { Directory.Delete(sampleDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        int withinGlobal = scored.Count(ResidualMasking.StartCandidateAdmitted);
        // Joint selection needs every phase and its original precision, not the rounded
        // handful of alternatives kept in the diagnostic report.
        if (scoredCandidates is not null)
            foreach (ResidualStartCandidate candidate in scored) scoredCandidates.Add(candidate);
        // 准入（整幅）在前、排序键在后；一个都没准入时仍给出最优候选，让全分辨率复核报出真实数值再拒。
        ResidualStartCandidate[] ordered = ResidualMasking.OrderStartCandidates(scored);
        ResidualStartCandidate best = ordered[0];
        ResidualStartCandidate unshifted = scored[0];
        return new JsonObject
        {
            ["sampling_coverage"] = samplingCoverage,
            ["schema_version"] = 4,
            ["status"] = withinGlobal > 0 ? "selected_by_analytic_period_phase" : "selected_without_global_limit_candidate",
            ["pixel_packing"] = packed ? "rgba_side_by_side" : "rgb",
            ["warmup_frames"] = sampleRequest.WarmupFrames,
            ["period_frames"] = periodFrames,
            ["search_window_periods"] = windowPeriods,
            ["search_window_frames"] = sampleRequest.Frames,
            ["stride_frames"] = stride,
            ["crossfade_frames"] = crossfadeFrames,
            ["candidate_count"] = perPeriod,
            ["candidates_within_global_limit"] = withinGlobal,
            ["sample_width"] = width,
            ["sample_height"] = height,
            ["sample_tile_size"] = tileSize,
            ["sample_count"] = count,
            ["selected_start_frame"] = best.Start,
            ["selected"] = LoopStartRow(best),
            ["unshifted"] = LoopStartRow(unshifted),
            ["best_alternatives"] = new JsonArray(ordered.Skip(1).Take(4).Select(x => (JsonNode)LoopStartRow(x)).ToArray()),
            // 全分辨率第一层被拒时按这个顺序换起点重测（ResidualStartFallback），最多 MaximumStartAttempts 个。
            ["maximum_start_attempts"] = ResidualMasking.MaximumStartAttempts,
            ["start_attempt_order"] = new JsonArray(ordered.Take(ResidualMasking.MaximumStartAttempts).Select(x => (JsonNode)LoopStartRow(x)).ToArray()),
            ["basis"] = "周期来自解析，起点只在该周期内的采样相位上选择。排序键是样本上能看到的第一层量 " +
                "max(M(Δ_0), M(Δ_stride))，只用于排序；准入只看整幅 Δ_0 ≤ 2/255。这里都是降采样估计，" +
                "第一层的判定在全分辨率 master 上对淡化窗口内每个 k 重做，被拒时停止本次生成，不自动整案重渲。"
        };
    }

    internal static JsonObject CombineLoopStartSearches(
        IReadOnlyList<(JsonObject Search, IReadOnlyList<ResidualStartCandidate> Candidates)> searches)
    {
        if (searches.Count == 0) throw new ArgumentException("A shared start needs at least one residual group.");
        if (searches.Count == 1) return searches[0].Search;
        var first = searches[0];
        if (first.Candidates.Count == 0) throw new InvalidDataException("The shared phase grid is empty.");
        foreach (var group in searches.Skip(1))
        {
            if (group.Candidates.Count != first.Candidates.Count ||
                !group.Candidates.Select(candidate => candidate.Start).SequenceEqual(first.Candidates.Select(candidate => candidate.Start)) ||
                new[] { "warmup_frames", "period_frames", "stride_frames", "crossfade_frames" }.Any(key =>
                    !JsonNode.DeepEquals(group.Search[key], first.Search[key])))
                throw new InvalidDataException("Residual groups must use the same phase grid, period and warmup.");
        }
        // max(global) <= limit iff every group meets the existing global admission rule.
        // The tile maxima reuse the existing minimax ranking, keeping all groups at one phase.
        ResidualStartCandidate[] combined = Enumerable.Range(0, first.Candidates.Count).Select(index =>
            new ResidualStartCandidate(first.Candidates[index].Start,
                searches.Max(group => group.Candidates[index].Global),
                searches.Max(group => group.Candidates[index].WorstTile),
                searches.Max(group => group.Candidates[index].StrideWorstTile))).ToArray();
        ResidualStartCandidate[] ordered = ResidualMasking.OrderStartCandidates(combined);
        int admitted = combined.Count(ResidualMasking.StartCandidateAdmitted);
        JsonObject result = first.Search.DeepClone().AsObject();
        foreach (string key in new[] { "group_id", "sampling_coverage", "pixel_packing", "sample_width", "sample_height", "sample_tile_size", "sample_count" })
            result.Remove(key);
        result["schema_version"] = 5;
        result["selection_scope"] = "all_residual_groups";
        result["status"] = admitted > 0 ? "selected_by_joint_analytic_period_phase" : "selected_without_joint_global_limit_candidate";
        result["candidates_within_global_limit"] = admitted;
        result["selected_start_frame"] = ordered[0].Start;
        result["selected"] = LoopStartRow(ordered[0]);
        result["unshifted"] = LoopStartRow(combined[0]);
        result["best_alternatives"] = new JsonArray(ordered.Skip(1).Take(4).Select(x => (JsonNode)LoopStartRow(x)).ToArray());
        result["maximum_start_attempts"] = 1;
        result["start_attempt_order"] = new JsonArray(LoopStartRow(ordered[0]));
        int selectedIndex = Array.FindIndex(combined, candidate => candidate.Start == ordered[0].Start);
        result["groups"] = new JsonArray(searches.Select(group =>
        {
            JsonObject item = group.Search.DeepClone().AsObject();
            item["at_shared_start"] = LoopStartRow(group.Candidates[selectedIndex]);
            return (JsonNode)item;
        }).ToArray());
        result["basis"] = "所有残差组在相同预热、周期和采样相位上分别评分。逐相位取各组整幅、瓦片及下一采样瓦片残差的最大值，" +
            "沿用整幅 ≤ 2/255 的准入和原瓦片排序；因此准入要求每个组都满足整幅限制，起点仍由所有组共享。" +
            "降采样只用于选起点；全分辨率原质量门不变，拒绝后不自动整案重渲。";
        return result;
    }

    private static JsonObject LoopStartRow(ResidualStartCandidate x) => new()
    {
        ["start_frame"] = x.Start,
        ["sampled_global_rgb_mae_255"] = Math.Round(x.Global, 4),
        ["sampled_worst_tile_rgb_mae_255"] = Math.Round(x.WorstTile, 4),
        ["sampled_stride_worst_tile_rgb_mae_255"] = x.StrideWorstTile is double stride ? Math.Round(stride, 4) : null,
        ["sampled_sort_key"] = Math.Round(x.SortKey, 4)
    };

    /// <summary>
    /// 在全分辨率无损 master 上测残差掩盖的第一层：淡化窗口内每个 k∈[0,C) 的残差 Δ_k = f[P+k] − f[k]。
    /// 第一层是唯一的裁决（<see cref="ResidualMasking.FirstLayer"/>）：max_k 最差 64px 瓦片 ≤ 48/255，且 Δ_0 整幅 ≤ 2/255。
    /// 淡化之后观众能看到的重影峰值 min(w,1−w)·|Δ_k| 与每步附加步进都由 |Δ_k| 直接决定，与场景普通帧间步进的快慢无关，
    /// 所以这里不统计普通步进。原来的第二层改成淡化实现自检，在 <see cref="ApplyLoopCrossfadeAsync"/> 里对成品做。
    /// </summary>
    public async Task<JsonObject> MeasureSeamResidualAsync(string masterDirectory, ulong loopFrames,
        uint crossfadeFrames, CancellationToken cancellationToken = default)
    {
        string master = Path.GetFullPath(masterDirectory);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(master, "manifest.json"), cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("master 清单无效。");
        string packing = manifest["pixel_packing"]?.GetValue<string>() ?? "";
        JsonObject? gpuWindow = manifest["gpu_loop_window"] as JsonObject;
        if (manifest["status"]?.GetValue<string>() != "completed" ||
            (manifest["lossless_test_encoding"]?.GetValue<bool>() != true && gpuWindow is null) ||
            packing is not ("rgb" or "rgba_side_by_side"))
            throw new InvalidDataException("接缝残差测量需要一个完成的无损 master（不透明 RGB 或左右并排的预乘色 + 覆盖度）。");
        // 透明组 master 是 2W 宽：左半预乘色、右半覆盖度。两半分别算、RGB 半带掩码，第一层取两半里更大的读数。
        bool packed = packing == "rgba_side_by_side";
        JsonObject request = manifest["request"]!.AsObject();
        int halfWidth = request["width"]!.GetValue<int>(), height = request["height"]!.GetValue<int>();
        int width = packed ? checked(halfWidth * 2) : halfWidth;
        uint numerator = request["fps_numerator"]!.GetValue<uint>(), denominator = request["fps_denominator"]!.GetValue<uint>();
        ulong captured = request["frames"]!.GetValue<ulong>();
        if (loopFrames == 0 || crossfadeFrames == 0 || captured != checked(loopFrames + crossfadeFrames))
            throw new InvalidDataException("测量淡化窗口内的残差需要 master 正好多采集一个淡化窗口。");
        if (crossfadeFrames >= loopFrames) throw new InvalidDataException("淡化窗口必须短于循环周期。");
        int window = checked((int)crossfadeFrames);
        string video = Path.Combine(master, "preview.mp4");
        string pack = gpuWindow?["path"]?.GetValue<string>() ?? Path.Combine(master, "seam-frames.rgb");
        if (gpuWindow is null && File.Exists(pack)) File.Delete(pack);
        int frameBytes = checked(width * height * 3);
        int storedFrameBytes = gpuWindow is null ? frameBytes : checked(halfWidth * height * 4);
        long expected = (long)storedFrameBytes * 2 * window;
        // 帧包是 2C 帧 raw（4K 下 24 帧一侧约 1.2 GiB），与无损 master 同盘，落盘前按预期字节预检。
        if (gpuWindow is null) TemporaryCaptureFiles.RequireFreeSpace(pack, (ulong)expected);
        else if (gpuWindow["width"]?.GetValue<int>() != halfWidth || gpuWindow["height"]?.GetValue<int>() != height ||
            gpuWindow["crossfade_frames"]?.GetValue<uint>() != crossfadeFrames || gpuWindow["loop_frames"]?.GetValue<ulong>() != loopFrames)
            throw new InvalidDataException("GPU original window does not match the residual measurement.");
        // 两个输入各自定位：f[0..C-1] 从第 0 帧起，f[P..P+C-1] 从第 P 帧起，都按帧号精确取（见 ExactFrameRange）。
        // 不要让一个输入从头解码到第 P 帧——那是整片解码，master 越长越慢。
        ExactFrameRange.FfmpegInput head = ExactFrameRange.Build(video, 0, crossfadeFrames, numerator, denominator);
        ExactFrameRange.FfmpegInput wrapWindow = ExactFrameRange.Build(video, loopFrames, crossfadeFrames, numerator, denominator);
        string filter = $"[0:v]{head.Filter}[head];[1:v]{wrapWindow.Filter}[wrap];[head][wrap]concat=n=2:v=1:a=0[pair]";
        try
        {
            // ffmpeg 失败、取消或磁盘告急时帧包已经开始写了，放在 try 里让 finally 一并清掉。
            if (gpuWindow is null) _ = await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", .. head.Arguments, .. wrapWindow.Arguments,
                "-filter_complex", filter, "-map", "[pair]", "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", pack],
                Path.Combine(master, "seam-frames.stderr.log"), cancellationToken);
            if (new FileInfo(pack).Length != expected) throw new InvalidDataException("接缝帧包的长度与 master 尺寸不一致。");
            byte[] left = new byte[frameBytes], right = new byte[frameBytes];
            byte[]? rgba = gpuWindow is null ? null : new byte[storedFrameBytes];
            await using var stream = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);

            var residuals = new List<LoopWrapResidual>(window);
            var perFrame = new JsonArray();
            double ghostPeak = 0, maskFraction = 0;
            int ghostPeakFrame = 0;
            for (int k = 0; k < window; ++k)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rgba is null)
                {
                    await ReadFrameAsync(stream, left, k, frameBytes, cancellationToken);
                    await ReadFrameAsync(stream, right, window + k, frameBytes, cancellationToken);
                }
                else
                {
                    await ReadFrameAsync(stream, rgba, k, storedFrameBytes, cancellationToken);
                    left = LoopClosureCheck.EncodedLayout(rgba,halfWidth,height,0,0,halfWidth,height,packed);
                    await ReadFrameAsync(stream, rgba, window+k, storedFrameBytes, cancellationToken);
                    right = LoopClosureCheck.EncodedLayout(rgba,halfWidth,height,0,0,halfWidth,height,packed);
                }
                PackedWrapResidual? halves = packed
                    ? LoopSeamMetrics.PackedResidual(left, right, halfWidth, height, ResidualMasking.SeamTileSize) : null;
                LoopWrapResidual residual = halves is not null ? halves.Combined(halfWidth)
                    : LoopSeamMetrics.WrapResidual(left, right, width, height, ResidualMasking.SeamTileSize);
                residuals.Add(residual);
                // 重影：g[k] 离最近一条原作时间线的距离是 min(w_k, 1−w_k)·|Δ_k|，w_k 是标量，瓦片 MAE 直接按比例缩。
                double weight = (double)(crossfadeFrames - (uint)k) / (crossfadeFrames + 1);
                double ghost = Math.Min(weight, 1 - weight) * residual.WorstTileRgbMae;
                if (ghost > ghostPeak) { ghostPeak = ghost; ghostPeakFrame = k; }
                var row = new JsonObject
                {
                    ["k"] = k,
                    ["blend_weight"] = Math.Round(weight, 6),
                    ["worst_tile_rgb_mae_255"] = Math.Round(residual.WorstTileRgbMae, 4),
                    ["worst_tile_x"] = residual.WorstTileX,
                    ["worst_tile_y"] = residual.WorstTileY,
                    ["global_rgb_mae_255"] = Math.Round(residual.GlobalRgbMae, 4),
                    ["ghost_worst_tile_rgb_mae_255"] = Math.Round(ghost, 4)
                };
                if (halves is not null)
                {
                    row["color_worst_tile_rgb_mae_255"] = Math.Round(halves.Color.WorstTileRgbMae, 4);
                    row["color_global_rgb_mae_255"] = Math.Round(halves.Color.GlobalRgbMae, 4);
                    row["coverage_worst_tile_mae_255"] = Math.Round(halves.Coverage.WorstTileRgbMae, 4);
                    row["coverage_global_mae_255"] = Math.Round(halves.Coverage.GlobalRgbMae, 4);
                    row["color_mask_fraction"] = Math.Round(halves.MaskFraction, 6);
                    maskFraction = Math.Max(maskFraction, halves.MaskFraction);
                }
                perFrame.Add(row);
            }
            ResidualFirstLayer firstLayer = ResidualMasking.FirstLayer(residuals);
            LoopWrapResidual hardCut = residuals[0];
            return new JsonObject
            {
                ["schema_version"] = 3,
                ["status"] = firstLayer.Passed ? "observed_within_residual_limits" : "rejected_residual_above_limits",
                ["loop_frames"] = loopFrames,
                ["captured_frames"] = captured,
                ["crossfade_frames"] = crossfadeFrames,
                ["width"] = halfWidth,
                ["height"] = height,
                ["pixel_packing"] = packing,
                ["tile_size"] = ResidualMasking.SeamTileSize,
                ["packed_measurement"] = !packed ? null : new JsonObject
                {
                    ["maximum_color_mask_fraction"] = Math.Round(maskFraction, 6),
                    ["basis"] = "左半预乘色与右半覆盖度分别按 64px 瓦片算 Δ_k。RGB 半只在掩码内累加（任一帧覆盖度 > 0，或任一帧 RGB 非零——" +
                        "加性粒子不写覆盖度），瓦片分母仍是全部像素；覆盖度半单通道。合成 out = C + (1−α)·B 里 ΔC 与 Δα 是两个独立分量，" +
                        "各自都要在第一层限内，所以第一层取两半里更大的最差瓦片与整幅；覆盖度半的瓦片 x 记在 packed 图坐标里（加半宽）。"
                },
                ["first_layer"] = new JsonObject
                {
                    ["passed"] = firstLayer.Passed,
                    ["maximum_worst_tile_rgb_mae_255"] = Math.Round(firstLayer.MaximumWorstTileRgbMae, 4),
                    ["maximum_worst_tile_frame"] = firstLayer.MaximumWorstTileFrame,
                    ["maximum_worst_tile_x"] = firstLayer.WorstTileX,
                    ["maximum_worst_tile_y"] = firstLayer.WorstTileY,
                    ["hard_cut_global_rgb_mae_255"] = Math.Round(firstLayer.HardCutGlobalRgbMae, 4),
                    ["hard_cut_worst_tile_rgb_mae_255"] = Math.Round(hardCut.WorstTileRgbMae, 4),
                    ["hard_cut_worst_tile_x"] = hardCut.WorstTileX,
                    ["hard_cut_worst_tile_y"] = hardCut.WorstTileY,
                    ["hard_cut_maximum_channel_difference"] = hardCut.MaximumChannelDifference,
                    ["limit_worst_tile_rgb_mae_255"] = ResidualMasking.MaximumResidualTileRgbMae255,
                    ["limit_global_rgb_mae_255"] = ResidualMasking.MaximumSeamRgbMae255,
                    ["ghost_peak_worst_tile_rgb_mae_255"] = Math.Round(ghostPeak, 4),
                    ["ghost_peak_frame"] = ghostPeakFrame,
                    ["seam_extra_step_worst_tile_rgb_mae_255"] = Math.Round(hardCut.WorstTileRgbMae / (crossfadeFrames + 1), 4),
                    ["per_frame"] = perFrame,
                    ["basis"] = "淡化窗口内每个 k∈[0,C) 的残差 Δ_k = f[P+k] − f[k] 取 64px 最差瓦片，最大值 ≤ 48/255；" +
                        "硬切残差 Δ_0 整幅 ≤ 2/255。淡化后重影峰值 min(w,1−w)·|Δ_k|、接缝附加步进 |Δ_0|/(C+1) 都由它决定，" +
                        "与场景普通帧间步进无关。ghost_* 与 seam_extra_step_* 只记录，不裁决。"
                },
                ["crossfade_self_check"] = new JsonObject
                {
                    ["status"] = gpuWindow is not null ? "not_performed_gpu_path" : firstLayer.Passed ? "performed_after_crossfade" : "not_performed",
                    ["record"] = gpuWindow is null ? "loop_crossfade.step_self_check" : "loop_crossfade.method",
                    ["basis"] = gpuWindow is not null ? "GPU 路径按整数权重在编码前混合；此处核对原始残差，编码后的接缝另行核对，不将实现对照冒充逐片自检。" :
                        "原来的第二层改为淡化实现自检：对成品淡化段的每一步，实测相对原作参照步进多出的量不得超过由 Δ_k " +
                        "推导的上界加 1/255 取整余量；超出是内部错误，直接抛出，不作为可调判据。"
                },
                ["basis"] = "全分辨率第一层：残差 Δ_k 本身的大小是残差掩盖唯一需要裁决的量。"
            };
        }
        finally
        {
            if (gpuWindow is null)
                try { File.Delete(pack); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 在接缝处做整帧交叉淡化：master 采集了 P + C 帧，把第 P..P+C-1 帧按线性权重混进第 0..C-1 帧，
    /// 再截成 P 帧。周期分量两端逐像素相同，混合后不变；只有残差分量被这段窗口摊开。
    /// 真正改变的只有前 C 帧，所以只重编码「接缝所在的那一个 GOP」：切点取第一个不小于 C 的 IDR，
    /// [cut, P) 一律 stream copy，代价只随淡化窗口和 GOP 长度走，与 master 多长无关。
    /// </summary>
    public async Task<JsonObject> ApplyLoopCrossfadeAsync(string masterDirectory, ulong loopFrames, uint crossfadeFrames,
        CancellationToken cancellationToken = default)
    {
        string master = Path.GetFullPath(masterDirectory);
        string manifestPath = Path.Combine(master, "manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("master 清单无效。");
        if (manifest["status"]?.GetValue<string>() != "completed" || manifest["lossless_test_encoding"]?.GetValue<bool>() != true)
            throw new InvalidDataException("交叉淡化需要一个完成的无损 master。");
        JsonObject request = manifest["request"]!.AsObject();
        // 透明组 master 左右并排（预乘色 + 覆盖度），视频宽度是请求宽度的两倍。blend 表达式在 packed 域逐通道线性混合，
        // 合成 out = C + (1−α)·B 对 (C, α) 线性，这就是预乘空间里正确的淡化，与不透明组同一个公式；权重校验与自检也逐像素照做。
        string packing = manifest["pixel_packing"]?.GetValue<string>() ?? "rgb";
        if (packing is not ("rgb" or "rgba_side_by_side")) throw new InvalidDataException("交叉淡化不认识这个 master 的像素打包方式。");
        uint width = checked(request["width"]!.GetValue<uint>() * (packing == "rgba_side_by_side" ? 2u : 1u));
        uint height = request["height"]!.GetValue<uint>();
        uint numerator = request["fps_numerator"]!.GetValue<uint>(), denominator = request["fps_denominator"]!.GetValue<uint>();
        ulong captured = request["frames"]!.GetValue<ulong>();
        if (crossfadeFrames == 0 || loopFrames == 0 || captured != checked(loopFrames + crossfadeFrames))
            throw new InvalidDataException("交叉淡化要求 master 正好多采集了一个淡化窗口的帧。");
        if (crossfadeFrames >= loopFrames) throw new InvalidDataException("淡化窗口必须短于循环周期。");
        string video = Path.Combine(master, "preview.mp4");
        string partial = Path.Combine(master, "preview.crossfade.partial.mp4");
        string headSegment = Path.Combine(master, "preview.crossfade.head.mp4");
        string tailSegment = Path.Combine(master, "preview.crossfade.tail.mp4");
        string concatList = Path.Combine(master, "preview.crossfade.concat.txt");
        // 这一步自己产出的中间件和日志都可以重跑，先清掉上一次的残留。
        string[] scratch = [partial, headSegment, tailSegment, concatList,
            Path.Combine(master, "crossfade.stderr.log"), Path.Combine(master, "crossfade.ffprobe.stderr.log"),
            Path.Combine(master, "crossfade-keyframes.stderr.log"), Path.Combine(master, "crossfade-tail.stderr.log"),
            Path.Combine(master, "crossfade-tail-check-master.stderr.log"), Path.Combine(master, "crossfade-tail-check-copy.stderr.log"),
            Path.Combine(master, "crossfade-concat.stderr.log"), Path.Combine(master, "crossfade-verify-source.stderr.log"),
            Path.Combine(master, "crossfade-verify-faded.stderr.log"), Path.Combine(master, "crossfade-steps-source.stderr.log"),
            Path.Combine(master, "crossfade-steps-faded.stderr.log")];
        foreach (string stale in scratch) if (File.Exists(stale)) File.Delete(stale);
        ulong cut = await FirstKeyFrameAtOrAfterAsync(video, crossfadeFrames, loopFrames, numerator, denominator,
            master, cancellationToken);
        RequireSegmentedMasterRewrite(video, captured, cut, "接缝交叉淡化");
        var profile = PlaybackEncodeProfile.Create(width, height, numerator, denominator, losslessTest: true);
        JsonObject verification;
        try
        {
            // 播放到第 P-1 帧后回到第 0 帧；真实的下一帧是第 P 帧，所以窗口起点几乎全取第 P 帧，
            // 再在 C 帧内线性交回原始帧，接缝两侧的一阶连续性由此成立。
            // blend 表达式里的 N 从 1 开始数，写成 C+1-N 才等于记录里的 w = (C-i)/(C+1)；
            // enable 的 n 从 0 开始，窗口之外整帧直通，不进逐像素表达式，也就不花时间。
            string weight = $"(max(0,({crossfadeFrames}+1-N))/{crossfadeFrames + 1})";
            // 两路都按帧号精确取：底片是第 0..cut-1 帧，混入的是第 P..P+C-1 帧（见 ExactFrameRange）。
            ExactFrameRange.FfmpegInput baseFrames = ExactFrameRange.Build(video, 0, cut, numerator, denominator);
            ExactFrameRange.FfmpegInput wrapFrames = ExactFrameRange.Build(video, loopFrames, crossfadeFrames, numerator, denominator);
            string filter = $"[0:v]{baseFrames.Filter}[base];" +
                $"[1:v]{wrapFrames.Filter},tpad=stop={cut + 8}:stop_mode=clone[wrap];" +
                $"[base][wrap]blend=all_expr='A*(1-{weight})+B*{weight}':shortest=1:enable='lt(n,{crossfadeFrames})'[out]";
            string[] headArguments = ["-hide_banner", "-nostdin", "-n", .. baseFrames.Arguments, .. wrapFrames.Arguments,
                "-filter_complex", filter, "-map", "[out]", "-an",
                "-frames:v", cut.ToString(CultureInfo.InvariantCulture),
                .. profile.OutputArguments(numerator, denominator, headSegment)];
            _ = await RunTextAsync(tools.Ffmpeg, headArguments, Path.Combine(master, "crossfade.stderr.log"), cancellationToken);
            verification = await VerifyCrossfadeWeightsAsync(video, headSegment, loopFrames, crossfadeFrames,
                width, height, numerator, denominator, master, cancellationToken);
            ulong copied = loopFrames - cut;
            string finished = headSegment;
            if (copied > 0)
            {
                // -c copy 全程不解码，没法用帧号窗口，只能靠 -ss 正好落在第 cut 帧的时间戳上命中那个 IDR。
                // 原来的 cut+0.25 帧在 59.94 这类细时基下会把 IDR 当成 seek 点之前的包丢掉，所以改成整帧边界，
                // 再把尾段首帧和 master 第 cut 帧逐字节比一次：seek 语义以后再变，这里会直接失败而不是悄悄错位。
                _ = await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n",
                    "-ss", InputSeconds(cut, numerator, denominator), "-i", video,
                    "-map", "0:v:0", "-an", "-c", "copy", "-frames:v", copied.ToString(CultureInfo.InvariantCulture),
                    // 尾段与拼接结果都是本地中间文件、只被顺序整读，faststart 会把接近整个 master 再读写一遍，纯浪费。
                    "-video_track_timescale", numerator.ToString(CultureInfo.InvariantCulture), tailSegment],
                    Path.Combine(master, "crossfade-tail.stderr.log"), cancellationToken);
                await RequireTailStartsAtCutAsync(video, tailSegment, cut, numerator, denominator, master, cancellationToken);
                await File.WriteAllTextAsync(concatList,
                    $"file '{ConcatEntry(headSegment)}'\nfile '{ConcatEntry(tailSegment)}'\n", cancellationToken);
                _ = await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", "-f", "concat", "-safe", "0",
                    "-i", concatList, "-map", "0:v:0", "-an", "-c", "copy", "-fps_mode", "passthrough",
                    "-movie_timescale", numerator.ToString(CultureInfo.InvariantCulture),
                    "-video_track_timescale", numerator.ToString(CultureInfo.InvariantCulture), partial],
                    Path.Combine(master, "crossfade-concat.stderr.log"), cancellationToken);
                finished = partial;
            }
            // 本进程刚完成重封装，只核容器帧数/尺寸/时基；异常才解码确认。
            string probeText = await RunTextAsync(tools.Ffprobe, ["-v", "error", "-threads", "4", "-select_streams", "v:0",
                "-show_entries", "stream=width,height,nb_frames,avg_frame_rate,time_base,duration_ts",
                "-of", "json", finished], Path.Combine(master, "crossfade.ffprobe.stderr.log"), cancellationToken);
            JsonObject stream = JsonNode.Parse(probeText)!["streams"]!.AsArray().Single()!.AsObject();
            var count = await EncodedLoopValidator.FrameCountAsync(finished, tools, stream, loopFrames, cancellationToken);
            if (stream["width"]!.GetValue<uint>() != width || stream["height"]!.GetValue<uint>() != height ||
                count.Count != loopFrames)
                throw new InvalidDataException("交叉淡化后的 master 没有给出请求的尺寸与帧数。");
            // 拼接是容器层操作，时间戳漂了不会报错，所以这里连时基和总时长一起核。
            ConfirmEncodedDuration(stream, loopFrames, numerator, denominator);
            // 淡化实现自检：拿拼好的成品对原 master 逐步核恒等式，超出推导上界 + 取整余量就抛内部错误。
            // 放在删除原 master 之前，失败时原 master 还在。
            JsonObject stepCheck = await VerifyCrossfadeStepsAsync(video, finished, loopFrames, crossfadeFrames,
                (int)width, (int)height, numerator, denominator, master, cancellationToken);
            File.Delete(video);
            File.Move(finished, video);
            await using (FileStream stored = File.OpenRead(video))
                manifest["video_sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(stored, cancellationToken));
            request["frames"] = loopFrames;
            var record = new JsonObject
            {
                ["status"] = "applied",
                ["policy"] = ResidualMasking.SeamPolicy,
                ["pixel_packing"] = packing,
                ["crossfade_frames"] = crossfadeFrames,
                ["crossfade_seconds"] = Math.Round((double)crossfadeFrames * denominator / numerator, 6),
                ["captured_frames"] = captured,
                ["loop_frames"] = loopFrames,
                ["reencoded_frames"] = cut,
                ["copied_frames"] = copied,
                ["segment_cut_frame"] = cut,
                ["weight_expression"] = $"out[i] = (1-w)*f[i] + w*f[P+i], w = (C-i)/(C+1), C = {crossfadeFrames}",
                ["weight_verification"] = verification,
                ["step_self_check"] = stepCheck,
                ["scope"] = "整帧线性交叉淡化，窗口固定。周期分量在两端逐像素相同因此不受影响；" +
                    "残差分量被摊开在窗口内。没有做任何局部接缝修复。",
                ["method"] = $"只重编码 [0, {cut}) 这一个包含接缝的 GOP，[{cut}, {loopFrames}) 用 concat demuxer 加 " +
                    "-c copy 原样拼接。开销只随淡化窗口与 GOP 长度增长，与 master 总长无关。"
            };
            manifest["loop_crossfade"] = record.DeepClone();
            manifest["color"] = "Lossless H.264 RGB master with the fixed loop crossfade applied; hardware playback is not assumed.";
            await WriteJsonAsync(manifestPath, manifest, cancellationToken);
            return record;
        }
        finally
        {
            foreach (string leftover in new[] { partial, headSegment, tailSegment, concatList })
                try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string ConcatEntry(string path) => path.Replace('\\', '/').Replace("'", @"'\''");

    /// <summary>
    /// 找第一个不小于 <paramref name="minimum"/> 的 IDR，作为「只重编码接缝片段」的切点。
    /// 只翻包头不解码，读取窗口有上限，所以 master 再长也是常数开销。
    /// </summary>
    private async Task<ulong> FirstKeyFrameAtOrAfterAsync(string video, ulong minimum, ulong limit,
        uint numerator, uint denominator, string logDirectory, CancellationToken token)
    {
        ulong window = Math.Min(limit, minimum + KeyFrameProbeFrames);
        string text = await RunTextAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags", "-read_intervals", $"%+#{window}", "-of", "csv=p=0", video],
            Path.Combine(logDirectory, "crossfade-keyframes.stderr.log"), token);
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Trim().Split(',');
            if (parts.Length < 2 || !parts[1].StartsWith('K')) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) continue;
            ulong frame = (ulong)Math.Round(seconds * numerator / denominator);
            if (frame >= minimum && frame < limit) return frame;
        }
        // 整个循环就是一个 GOP 时只能全段重编码；master 大到超过分段闸门时那道闸会拦下来。
        if (window >= limit) return limit;
        throw new InvalidDataException($"master 在第 {minimum} 帧之后的 {window} 帧里找不到 IDR，无法只重编码接缝片段。" +
            "给 master 编码加上淡化窗口处的强制关键帧再重试。");
    }

    /// <summary>
    /// stream copy 出来的尾段首帧必须就是 master 的第 cut 帧。帧数核对查不出"从更早的关键帧开始拷、再被 -frames:v 截断"
    /// 这种整体错位，所以两边各解一帧比 MD5。尾段首帧本身是 IDR，master 那一帧按帧号精确取，都不随 master 变长。
    /// </summary>
    private async Task RequireTailStartsAtCutAsync(string video, string tailSegment, ulong cut, uint numerator, uint denominator,
        string master, CancellationToken token)
    {
        ExactFrameRange.FfmpegInput expected = ExactFrameRange.Build(video, cut, 1, numerator, denominator);
        string sourceHash = (await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", .. expected.Arguments,
            "-map", "0:v:0", "-vf", expected.Filter, "-frames:v", "1", "-an", "-pix_fmt", "rgb24", "-f", "md5", "-"],
            Path.Combine(master, "crossfade-tail-check-master.stderr.log"), token)).Trim();
        string copiedHash = (await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-i", tailSegment,
            "-map", "0:v:0", "-frames:v", "1", "-an", "-pix_fmt", "rgb24", "-f", "md5", "-"],
            Path.Combine(master, "crossfade-tail-check-copy.stderr.log"), token)).Trim();
        if (!sourceHash.StartsWith("MD5=", StringComparison.Ordinal) || sourceHash != copiedHash)
            throw new InvalidDataException($"stream copy 的尾段首帧不是 master 第 {cut} 帧（{copiedHash} ≠ {sourceHash}）；" +
                "-ss 没有正好落在切点 IDR 上，拼出来的 master 会整体错位。");
    }

    /// <summary>
    /// 回读淡化窗口两端的整帧，和「用 master 原帧算出来的期望混合值」逐像素比。ffmpeg 的 blend
    /// 表达式帧号从 1 开始，这种偏移不会报错、只会悄悄把权重错开一帧，所以用真实像素把公式钉死。
    /// 源帧故意不用淡化本身的取帧办法（<see cref="ExactFrameRange"/>：整帧边界 seek + 时间戳窗口），
    /// 而走 <see cref="EncodedQualityValidator"/> 的逐帧选取（整秒锚点 + 帧序号计数）。两套定位互相独立，
    /// 任何一边错开一帧都会在这里失败；以前两边用同一个 -ss 写法、同偏同错，这道校验查不出来。
    /// 只取四帧，开销与 master 长度无关。
    /// </summary>
    private async Task<JsonObject> VerifyCrossfadeWeightsAsync(string video, string headSegment, ulong loopFrames,
        uint crossfadeFrames, uint width, uint height, uint numerator, uint denominator, string master, CancellationToken token)
    {
        int frameBytes = checked((int)width * (int)height * 3);
        uint last = crossfadeFrames - 1;
        string select = $"select='eq(n,0)+eq(n,{last})',setpts=N";
        string fadedPack = Path.Combine(master, "crossfade-verify-faded.rgb");
        if (File.Exists(fadedPack)) File.Delete(fadedPack);
        try
        {
            // 成片的淡化段从第 0 帧开始，按帧序号选取不需要 seek。
            _ = await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", "-i", headSegment,
                "-vf", select, "-map", "0:v:0", "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", fadedPack],
                Path.Combine(master, "crossfade-verify-faded.stderr.log"), token);
            int samples = last == 0 ? 1 : 2;
            if (new FileInfo(fadedPack).Length != (long)frameBytes * samples)
                throw new InvalidDataException("接缝抽样帧的数量或尺寸与 master 不一致。");
            ulong[] indices = last == 0 ? [0, loopFrames] : [0, last, loopFrames, loopFrames + last];
            var original = new Dictionary<ulong, byte[]>(2);
            byte[] faded = new byte[frameBytes];
            double worst = 0;
            uint worstFrame = 0;
            int compared = 0;
            await using (var mixed = new FileStream(fadedPack, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true))
                await foreach ((ulong index, byte[] frame) in EncodedQualityValidator.DecodeSelectedRgb24Async(video, tools,
                    (int)width, (int)height, indices, null, Path.Combine(master, "crossfade-verify-source.stderr.log"), token))
                {
                    token.ThrowIfCancellationRequested();
                    if (frame.Length != frameBytes) throw new InvalidDataException("接缝抽样帧的数量或尺寸与 master 不一致。");
                    if (index < loopFrames) { original[index] = frame; continue; }
                    // 到了第 P+i 帧就和第 i 帧、成片第 i 帧一起比，比完立刻放掉第 i 帧，内存里最多同时留三帧。
                    uint sample = checked((uint)(index - loopFrames));
                    if (!original.Remove(sample, out byte[]? before))
                        throw new InvalidDataException("接缝抽样帧没有按帧号顺序解出。");
                    await ReadFrameAsync(mixed, faded, sample == 0 ? 0 : 1, frameBytes, token);
                    double w = (double)(crossfadeFrames - sample) / (crossfadeFrames + 1);
                    for (int k = 0; k < frameBytes; ++k)
                    {
                        double deviation = Math.Abs(faded[k] - ((1 - w) * before[k] + w * frame[k]));
                        if (deviation > worst) { worst = deviation; worstFrame = sample; }
                    }
                    ++compared;
                }
            if (compared != samples) throw new InvalidDataException("接缝抽样帧的数量或尺寸与 master 不一致。");
            if (worst > MaximumCrossfadeWeightDeviation)
                throw new InvalidDataException($"淡化窗口第 {worstFrame} 帧与 w = (C-i)/(C+1) 的期望混合值最大差 " +
                    $"{worst:0.###}/255，超过 8 位取整容差 {MaximumCrossfadeWeightDeviation}；" +
                    "实际施加的混合权重与记录的公式不一致。");
            return new JsonObject
            {
                ["status"] = "verified_against_source_frames",
                ["checked_frames"] = new JsonArray(0, last),
                ["maximum_deviation_255"] = Math.Round(worst, 4),
                ["tolerance_255"] = MaximumCrossfadeWeightDeviation,
                ["source_frame_access"] = "EncodedQualityValidator 逐帧选取（整秒锚点 + 帧序号），独立于淡化本身的取帧",
                ["basis"] = "把成片淡化窗口的首末帧解出来，与 master 原帧按 w = (C-i)/(C+1) 算的期望值逐像素比，" +
                    "只允许 8 位取整的差。权重错开一帧、或混入的源帧错开一帧，都会在这里直接失败。"
            };
        }
        finally
        {
            try { File.Delete(fadedPack); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 淡化实现自检（原残差掩盖第二层）：解出原 master 的 f[0..C] 与 f[P−1..P+C−1]、成品的 g[0..C]，对第 0..C 步逐瓦片核
    /// <see cref="CrossfadeStepCheck"/> 的恒等式——成品相对原作参照步进多出的量不得超过由 Δ_k 推导的上界加 1/255 取整余量。
    /// 超出就是淡化实现（权重、混入帧、拼接）错了，抛 <see cref="InvalidOperationException"/>。所有取帧按帧号走
    /// <see cref="ExactFrameRange"/>；只解 3C+3 帧，开销与 master 长度无关。帧包写在 <paramref name="workDirectory"/>，结束即删。
    /// </summary>
    public async Task<JsonObject> VerifyCrossfadeStepsAsync(string sourceVideo, string fadedVideo, ulong loopFrames,
        uint crossfadeFrames, int width, int height, uint numerator, uint denominator, string workDirectory,
        CancellationToken cancellationToken = default)
    {
        if (crossfadeFrames == 0 || crossfadeFrames >= loopFrames) throw new ArgumentException("淡化自检需要 0 < C < P。");
        int window = checked((int)crossfadeFrames);
        int frameBytes = checked(width * height * 3);
        string sourcePack = Path.Combine(workDirectory, "crossfade-steps-source.rgb");
        string fadedPack = Path.Combine(workDirectory, "crossfade-steps-faded.rgb");
        string sourceLog = Path.Combine(workDirectory, "crossfade-steps-source.stderr.log");
        string fadedLog = Path.Combine(workDirectory, "crossfade-steps-faded.stderr.log");
        foreach (string stale in new[] { sourcePack, fadedPack, sourceLog, fadedLog }) if (File.Exists(stale)) File.Delete(stale);
        long sourceBytes = (long)frameBytes * (2 * window + 2), fadedBytes = (long)frameBytes * (window + 1);
        TemporaryCaptureFiles.RequireFreeSpace(sourcePack, (ulong)(sourceBytes + fadedBytes));
        try
        {
            ulong side = crossfadeFrames + 1UL;
            ExactFrameRange.FfmpegInput head = ExactFrameRange.Build(sourceVideo, 0, side, numerator, denominator);
            ExactFrameRange.FfmpegInput wrap = ExactFrameRange.Build(sourceVideo, loopFrames - 1, side, numerator, denominator);
            _ = await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", .. head.Arguments, .. wrap.Arguments,
                "-filter_complex", $"[0:v]{head.Filter}[head];[1:v]{wrap.Filter}[wrap];[head][wrap]concat=n=2:v=1:a=0[pair]",
                "-map", "[pair]", "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", sourcePack], sourceLog, cancellationToken);
            ExactFrameRange.FfmpegInput faded = ExactFrameRange.Build(fadedVideo, 0, side, numerator, denominator);
            _ = await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", .. faded.Arguments, "-map", "0:v:0",
                "-vf", faded.Filter, "-fps_mode", "passthrough", "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", fadedPack], fadedLog, cancellationToken);
            if (new FileInfo(sourcePack).Length != sourceBytes || new FileInfo(fadedPack).Length != fadedBytes)
                throw new InvalidDataException("淡化自检的帧包长度与 master 尺寸或帧数不一致。");

            // 源帧包：第 i 项是 f[i]（i ≤ C），第 C+1+j 项是 f[P−1+j]（j ≤ C）。成品帧包：第 k 项是 g[k]。
            byte[] shownPrevious = new byte[frameBytes], shownCurrent = new byte[frameBytes];
            byte[] headPrevious = new byte[frameBytes], headCurrent = new byte[frameBytes];
            byte[] wrapPrevious = new byte[frameBytes], wrapCurrent = new byte[frameBytes];
            var steps = new JsonArray();
            CrossfadeStepReading? worst = null;
            double worstStep = 0, worstSecondOrder = 0;
            await using var source = new FileStream(sourcePack, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            await using var product = new FileStream(fadedPack, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            for (int k = 0; k <= window; ++k)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (k == 0) await ReadFrameAsync(source, shownPrevious, window + 1, frameBytes, cancellationToken);
                else await ReadFrameAsync(product, shownPrevious, k - 1, frameBytes, cancellationToken);
                await ReadFrameAsync(product, shownCurrent, k, frameBytes, cancellationToken);
                if (k > 0) await ReadFrameAsync(source, headPrevious, k - 1, frameBytes, cancellationToken);
                await ReadFrameAsync(source, headCurrent, k, frameBytes, cancellationToken);
                await ReadFrameAsync(source, wrapPrevious, window + 1 + k, frameBytes, cancellationToken);
                if (k < window) await ReadFrameAsync(source, wrapCurrent, window + 2 + k, frameBytes, cancellationToken);
                CrossfadeStepReading reading = CrossfadeStepCheck.Verify(k, crossfadeFrames, width, height, ResidualMasking.SeamTileSize,
                    shownPrevious, shownCurrent, k > 0 ? headPrevious : [], headCurrent, wrapPrevious, k < window ? wrapCurrent : []);
                if (worst is null || reading.MaximumExcessOverBound > worst.MaximumExcessOverBound) worst = reading;
                worstStep = Math.Max(worstStep, reading.StepWorstTile);
                worstSecondOrder = Math.Max(worstSecondOrder, reading.SecondOrderWorstTile);
                steps.Add(new JsonObject
                {
                    ["k"] = k,
                    ["step_worst_tile_rgb_mae_255"] = Math.Round(reading.StepWorstTile, 4),
                    ["reference_worst_tile_rgb_mae_255"] = Math.Round(reading.ReferenceWorstTile, 4),
                    ["extra_step_worst_tile_rgb_mae_255"] = Math.Round(reading.ExtraWorstTile, 4),
                    ["first_order_worst_tile_rgb_mae_255"] = Math.Round(reading.FirstOrderWorstTile, 4),
                    ["second_order_worst_tile_rgb_mae_255"] = Math.Round(reading.SecondOrderWorstTile, 4),
                    ["excess_over_bound_255"] = Math.Round(reading.MaximumExcessOverBound, 4)
                });
            }
            return new JsonObject
            {
                ["status"] = "verified_within_derived_bound",
                ["checked_steps"] = window + 1,
                ["tile_size"] = ResidualMasking.SeamTileSize,
                ["rounding_tolerance_255"] = ResidualMasking.CrossfadeSelfCheckRounding255,
                ["maximum_excess_over_bound_255"] = Math.Round(worst!.MaximumExcessOverBound, 4),
                ["maximum_excess_step"] = worst.K,
                ["maximum_excess_tile_x"] = worst.ExcessTileX,
                ["maximum_excess_tile_y"] = worst.ExcessTileY,
                ["maximum_excess_reference"] = worst.ExcessReference,
                ["maximum_step_worst_tile_rgb_mae_255"] = Math.Round(worstStep, 4),
                ["maximum_second_order_worst_tile_rgb_mae_255"] = Math.Round(worstSecondOrder, 4),
                ["per_step"] = steps,
                ["frame_access"] = "ExactFrameRange：原 master f[0..C] 与 f[P−1..P+C−1]，成品 g[0..C]",
                ["basis"] = "淡化实现自检，不是判据。第 k 步成品 g[k]−g[k−1]（k=0 时是 g[0]−f[P−1]）相对原作延续线 f[P+k]−f[P+k−1] " +
                    "与回归线 f[k]−f[k−1] 多出的量，逐瓦片不超过 M(Δ)/(C+1) + (k 或 C−k)/(C+1)·M(Δ_k−Δ_{k−1}) 加 1/255 取整余量；" +
                    "超出就是淡化实现错了，直接抛内部错误。"
            };
        }
        finally
        {
            foreach (string leftover in new[] { sourcePack, fadedPack })
                try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 导出接缝预览（见 <see cref="SeamPreview"/>）：循环末尾 N 帧接开头 N 帧，原速一遍、0.25 倍速一遍，顶部标签条写帧号。
    /// 只按时间段 seek 解码这 2N 帧；输入可以是编码好的候选视频，也可以是残差被拒时尚未清理的无损 master
    /// （此时看到的是没有淡化的硬切接缝）。预览写在组目录下，不在 master 目录里，清理 master 不会删到它。
    /// </summary>
    public async Task<JsonObject> ExportSeamPreviewAsync(string videoPath, string outputPath, ulong loopFrames,
        uint numerator, uint denominator, string sourceKind, CancellationToken cancellationToken = default)
    {
        string video = Path.GetFullPath(videoPath);
        if (!File.Exists(video)) throw new FileNotFoundException("接缝预览的输入视频不存在。", video);
        string output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string partial = output + ".partial.mp4", labels = output + ".labels.rgb";
        string encodeLog = output + ".stderr.log", probeLog = output + ".ffprobe.stderr.log", verifyLog = output + ".verify.stderr.log";
        foreach (string stale in new[] { output, partial, labels, encodeLog, probeLog, verifyLog })
            if (File.Exists(stale)) File.Delete(stale);
        try
        {
            // 只读容器头拿尺寸，不数帧、不解码。
            string probeText = await RunTextAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height", "-of", "json", video], probeLog, cancellationToken);
            JsonObject stream = JsonNode.Parse(probeText)?["streams"]?.AsArray().OfType<JsonObject>().SingleOrDefault()
                ?? throw new InvalidDataException("ffprobe 没有报告接缝预览输入的视频流。");
            SeamPreviewPlan plan = SeamPreview.Plan(video, output, loopFrames, numerator, denominator,
                stream["width"]!.GetValue<int>(), stream["height"]!.GetValue<int>());
            TemporaryCaptureFiles.RequireFreeSpace(labels, checked((ulong)plan.LabelWidth * SeamPreview.LabelRows * 3 * (ulong)plan.OutputFrames));
            await using (var labelStream = new FileStream(labels, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                await SeamPreview.WriteLabelsAsync(plan, labelStream, cancellationToken);
            _ = await RunTextAsync(tools.Ffmpeg, SeamPreview.FfmpegArguments(plan), encodeLog, cancellationToken);
            string verifyText = await RunTextAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height,nb_frames", "-of", "json", partial], verifyLog, cancellationToken);
            JsonObject encoded = JsonNode.Parse(verifyText)?["streams"]?.AsArray().OfType<JsonObject>().SingleOrDefault()
                ?? throw new InvalidDataException("ffprobe 没有报告接缝预览的视频流。");
            int expectedHeight = plan.OutputHeight + plan.LabelHeight;
            var previewCount = await EncodedLoopValidator.FrameCountAsync(partial, tools, encoded, (ulong)plan.OutputFrames, cancellationToken);
            if (encoded["width"]?.GetValue<int>() != plan.OutputWidth || encoded["height"]?.GetValue<int>() != expectedHeight ||
                previewCount.Count != (ulong)plan.OutputFrames)
                throw new InvalidDataException($"接缝预览应为 {plan.OutputWidth}x{expectedHeight}、{plan.OutputFrames} 帧，" +
                    $"实际 {encoded["width"]}x{encoded["height"]}、{previewCount.Count} 帧。");
            File.Move(partial, output);
            ulong n = plan.WindowFrames;
            return new JsonObject
            {
                ["status"] = "exported",
                ["frame_count_source"] = previewCount.Source,
                ["frame_count_fallback_reason"] = previewCount.FallbackReason,
                ["path"] = output,
                ["source_video"] = video,
                ["source_kind"] = sourceKind,
                ["loop_frames"] = loopFrames,
                ["frames_per_side"] = n,
                ["tail_loop_frames"] = new JsonArray(plan.TailStartFrame, loopFrames - 1),
                ["head_loop_frames"] = new JsonArray(0UL, n - 1),
                ["segments"] = new JsonArray(
                    new JsonObject { ["speed"] = 1.0, ["frames"] = 2 * n },
                    new JsonObject { ["speed"] = 1.0 / SeamPreview.SlowFactor, ["frames"] = 2 * n * SeamPreview.SlowFactor }),
                ["output_frames"] = plan.OutputFrames,
                ["output_width"] = plan.OutputWidth,
                ["output_height"] = expectedHeight,
                ["label_height"] = plan.LabelHeight,
                ["source_width"] = plan.SourceWidth,
                ["source_height"] = plan.SourceHeight,
                ["decode_scope"] = $"尾段输入 -ss 到第 {plan.TailStartFrame - plan.SeekLeadFrames} 帧、最多读 {plan.SeekLeadFrames + n + 2} 帧；" +
                    $"开头段最多读 {n + 2} 帧。解码只从 seek 点前最近的关键帧开始，没有整片解码。",
                ["basis"] = "循环末尾 N 帧接开头 N 帧，先原速、再 0.25 倍速（每帧重复 4 次）。顶部标签条写循环帧号，" +
                    "末尾段红底、开头段绿底，颜色切换处就是接缝。N 与接缝淡化窗口一致。只用于肉眼检查，不参与任何判定。"
            };
        }
        finally
        {
            foreach (string leftover in new[] { partial, labels })
                try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
