using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    private static async Task ReadFrameAsync(FileStream stream, byte[] buffer, int index, int frameBytes, CancellationToken token)
    {
        stream.Seek((long)index * frameBytes, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, token);
    }

    /// <summary>
    /// 在已经锁定的解析周期 P 内挑选起点帧。渲染 P 加候选段（最多再一个周期）的降采样样本，对每个候选起点在样本上估计第一层的量
    /// max_k M(Δ_k)：样本只落在 k=0 与 k=stride 上，排序键取 max(M(Δ_0), M(Δ_stride))（stride 不在淡化窗口内时只有 Δ_0）。
    /// 先准入（整幅 ≤ 2/255），再按排序键从小到大排；排序键只排序、不准入。前几个候选记成全分辨率回退的尝试顺序。
    /// 周期本身不因此改变，这里只决定相位。样本目录在算完之后立即删除。
    /// </summary>
    public async Task<JsonObject> SearchLoopStartAsync(RenderRequest sampleRequest, ulong periodFrames,
        uint crossfadeFrames, double tileScale, IProgress<RenderProgress>? progress = null, CancellationToken cancellationToken = default,
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
        // 窗口 = P + 候选段：候选起点 s 落在候选段里，每个比较 (s, s+P)。候选段最长一个周期（2P 已含全部 P/stride 个相位）；
        // 多组共享起点时长周期组只需到最短周期为止（见 LoopStartSelector）。
        if (sampleRequest.Frames <= periodFrames || sampleRequest.Frames > periodFrames * (ulong)ResidualMasking.SearchWindowPeriods ||
            (sampleRequest.Frames - periodFrames) % stride != 0)
            throw new ArgumentException("起点搜索窗口必须是一个解析周期再加 1 到 P/stride 个采样步长。");
        int perPeriod = checked((int)(periodFrames / stride));
        int candidates = checked((int)((sampleRequest.Frames - periodFrames) / stride));
        int width, height, tileSize;
        ulong count;
        var wraps = new List<LoopWrapResidual>(candidates);
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
            // packed 样本的瓦片边长按单半幅（logical_width）相对画布宽度换算。
            int logicalWidth = packed ? samples["logical_width"]!.GetValue<int>() : width;
            if (packed && logicalWidth * 2 != width) throw new InvalidDataException("带覆盖度的帧样本宽度不是逻辑宽度的两倍。");
            tileSize = Math.Max(4, (int)Math.Round(ResidualMasking.SeamTileSize * tileScale * logicalWidth /
                Math.Max(1, sampleRequest.Width)));
            // 样本文件是 2 个周期 ÷ 步长 × 采样帧大小，高窄画布上能到几个 GB，所以按需读两帧，不整份载入。
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            byte[] left = new byte[frameBytes], right = new byte[frameBytes];
            // 每个采样相位 c 的 Δ_0 = 样本[c + P/stride] − 样本[c]，按相位顺序各算一次；Δ_stride 由 SeamMath 取下一个相位的读数。
            for (int candidate = 0; candidate < candidates; ++candidate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadFrameAsync(file, left, candidate, frameBytes, cancellationToken);
                await ReadFrameAsync(file, right, candidate + perPeriod, frameBytes, cancellationToken);
                wraps.Add(packed
                    ? LoopSeamMetrics.PackedResidual(left, right, logicalWidth, height, tileSize).Combined(logicalWidth)
                    : LoopSeamMetrics.WrapResidual(left, right, width, height, tileSize));
            }
        }
        finally
        {
            // 样本目录只为这次搜索存在：成功、渲染失败、取消都删，几 GB 的样本不留在 output 下。
            try { Directory.Delete(sampleDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        ResidualStartCandidate[] scored = SeamMath.ScoreStarts(wraps, stride, crossfadeFrames);
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
            ["search_window_periods"] = Math.Round((double)sampleRequest.Frames / periodFrames, 4),
            ["search_window_frames"] = sampleRequest.Frames,
            ["stride_frames"] = stride,
            ["crossfade_frames"] = crossfadeFrames,
            ["candidate_count"] = candidates,
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
        // 各组可按自己的周期 P_g 评分：相位网格从 0 起、同一步长，短周期组的候选是长周期组的前缀，合成只取公共前缀。
        int shared = searches.Min(group => group.Candidates.Count);
        searches = [.. searches.Select(group => (group.Search, (IReadOnlyList<ResidualStartCandidate>)[.. group.Candidates.Take(shared)]))];
        var first = searches[0];
        if (first.Candidates.Count == 0) throw new InvalidDataException("The shared phase grid is empty.");
        foreach (var group in searches.Skip(1))
        {
            if (!group.Candidates.Select(candidate => candidate.Start).SequenceEqual(first.Candidates.Select(candidate => candidate.Start)) ||
                new[] { "warmup_frames", "stride_frames", "crossfade_frames" }.Any(key =>
                    !JsonNode.DeepEquals(group.Search[key], first.Search[key])))
                throw new InvalidDataException("Residual groups must use the same phase grid and warmup.");
        }
        // max(global) <= limit iff every group meets the existing global admission rule.
        // The tile maxima reuse the existing minimax ranking, keeping all groups at one phase.
        ResidualStartCandidate[] combined = SeamMath.CombineGroups([.. searches.Select(group => group.Candidates)]);
        ResidualStartCandidate[] ordered = ResidualMasking.OrderStartCandidates(combined);
        int admitted = combined.Count(ResidualMasking.StartCandidateAdmitted);
        JsonObject result = first.Search.DeepClone().AsObject();
        foreach (string key in new[] { "group_id", "sampling_coverage", "pixel_packing", "sample_width", "sample_height", "sample_tile_size", "sample_count" })
            result.Remove(key);
        result["schema_version"] = 5;
        result["selection_scope"] = "all_residual_groups";
        result["status"] = admitted > 0 ? "selected_by_joint_analytic_period_phase" : "selected_without_joint_global_limit_candidate";
        result["candidate_count"] = shared;
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
    /// 所以这里不统计普通步进。淡化成品的实现核对（权重核对 + 尾段 MD5）在 <see cref="MasterRewrite.CrossfadeAsync"/> 里做。
    /// </summary>
    public async Task<JsonObject> MeasureSeamResidualAsync(string masterDirectory, ulong loopFrames,
        uint crossfadeFrames, CancellationToken cancellationToken = default, double tileScale = 1)
    {
        int tile = (int)Math.Round(ResidualMasking.SeamTileSize * tileScale);
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
            if (gpuWindow is null) _ = await ff.RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", .. head.Arguments, .. wrapWindow.Arguments,
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
                    ? LoopSeamMetrics.PackedResidual(left, right, halfWidth, height, tile) : null;
                LoopWrapResidual residual = halves is not null ? halves.Combined(halfWidth)
                    : LoopSeamMetrics.WrapResidual(left, right, width, height, tile);
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
                ["tile_size"] = tile,
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
                    ["record"] = gpuWindow is null ? "loop_crossfade.weight_verification" : "loop_crossfade.method",
                    ["basis"] = gpuWindow is not null ? "GPU 路径按整数权重在编码前混合；此处核对原始残差，编码后的接缝另行核对，不将实现对照冒充逐片自检。" :
                        "淡化实现核对，不是判据：成品淡化窗口首末帧与 master 原帧按 w = (C-i)/(C+1) 逐像素比（只允许 8 位取整差），" +
                        "stream copy 尾段首帧与 master 切点帧比 MD5；不一致是内部错误，直接抛出。"
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
            string probeText = await ff.RunTextAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height", "-of", "json", video], probeLog, cancellationToken);
            JsonObject stream = JsonNode.Parse(probeText)?["streams"]?.AsArray().OfType<JsonObject>().SingleOrDefault()
                ?? throw new InvalidDataException("ffprobe 没有报告接缝预览输入的视频流。");
            SeamPreviewPlan plan = SeamPreview.Plan(video, output, loopFrames, numerator, denominator,
                stream["width"]!.GetValue<int>(), stream["height"]!.GetValue<int>());
            TemporaryCaptureFiles.RequireFreeSpace(labels, checked((ulong)plan.LabelWidth * SeamPreview.LabelRows * 3 * (ulong)plan.OutputFrames));
            await using (var labelStream = new FileStream(labels, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                await SeamPreview.WriteLabelsAsync(plan, labelStream, cancellationToken);
            _ = await ff.RunTextAsync(tools.Ffmpeg, SeamPreview.FfmpegArguments(plan), encodeLog, cancellationToken);
            EncodedStream preview = await VerifyEncoded.ProbeAsync(ff, partial, "stream=width,height,nb_frames",
                (ulong)plan.OutputFrames, verifyLog, cancellationToken, threads: false);
            int expectedHeight = plan.OutputHeight + plan.LabelHeight;
            if (!preview.Has(plan.OutputWidth, expectedHeight, (ulong)plan.OutputFrames))
                throw new InvalidDataException($"接缝预览应为 {plan.OutputWidth}x{expectedHeight}、{plan.OutputFrames} 帧，" +
                    $"实际 {preview.Stream["width"]}x{preview.Stream["height"]}、{preview.Frames} 帧。");
            File.Move(partial, output);
            ulong n = plan.WindowFrames;
            return new JsonObject
            {
                ["status"] = "exported",
                ["frame_count_source"] = preview.CountSource,
                ["frame_count_fallback_reason"] = preview.FallbackReason,
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
