using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 对已完成无损 master 的改写：接缝交叉淡化。只重编码接缝所在的 GOP，其余 stream copy；
/// 改写后核两道——淡化窗口首末帧对 master 原帧的权重核对，stream copy 尾段首帧对 master 切点帧的 MD5。
/// </summary>
internal sealed class MasterRewrite(FfmpegTool ff)
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
    /// 在接缝处做整帧交叉淡化：master 采集了 P + C 帧，把第 P..P+C-1 帧按线性权重混进第 0..C-1 帧，
    /// 再截成 P 帧。周期分量两端逐像素相同，混合后不变；只有残差分量被这段窗口摊开。
    /// 真正改变的只有前 C 帧，所以只重编码「接缝所在的那一个 GOP」：切点取第一个不小于 C 的 IDR，
    /// [cut, P) 一律 stream copy，代价只随淡化窗口和 GOP 长度走，与 master 多长无关。
    /// </summary>
    public async Task<JsonObject> CrossfadeAsync(string masterDirectory, ulong loopFrames, uint crossfadeFrames,
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
        // 合成 out = C + (1−α)·B 对 (C, α) 线性，这就是预乘空间里正确的淡化，与不透明组同一个公式；权重校验也逐像素照做。
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
            Path.Combine(master, "crossfade-verify-faded.stderr.log")];
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
            _ = await ff.RunTextAsync(ff.Tools.Ffmpeg, headArguments, Path.Combine(master, "crossfade.stderr.log"), cancellationToken);
            verification = await VerifyWeightsAsync(video, headSegment, loopFrames, crossfadeFrames,
                width, height, numerator, denominator, master, cancellationToken);
            ulong copied = loopFrames - cut;
            string finished = headSegment;
            if (copied > 0)
            {
                // -c copy 全程不解码，没法用帧号窗口，只能靠 -ss 正好落在第 cut 帧的时间戳上命中那个 IDR。
                // 原来的 cut+0.25 帧在 59.94 这类细时基下会把 IDR 当成 seek 点之前的包丢掉，所以改成整帧边界，
                // 再把尾段首帧和 master 第 cut 帧逐字节比一次：seek 语义以后再变，这里会直接失败而不是悄悄错位。
                // 不要靠半帧偏移让 -ss 落在"两帧中间"：自带 ffmpeg 会先截断到微秒、再按流时基舍入，偏移随帧率与时基变。
                _ = await ff.RunTextAsync(ff.Tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n",
                    "-ss", ExactFrameRange.Seconds(cut, numerator, denominator), "-i", video,
                    "-map", "0:v:0", "-an", "-c", "copy", "-frames:v", copied.ToString(CultureInfo.InvariantCulture),
                    // 尾段与拼接结果都是本地中间文件、只被顺序整读，faststart 会把接近整个 master 再读写一遍，纯浪费。
                    "-video_track_timescale", numerator.ToString(CultureInfo.InvariantCulture), tailSegment],
                    Path.Combine(master, "crossfade-tail.stderr.log"), cancellationToken);
                await RequireTailStartsAtCutAsync(video, tailSegment, cut, numerator, denominator, master, cancellationToken);
                await File.WriteAllTextAsync(concatList,
                    $"file '{ConcatEntry(headSegment)}'\nfile '{ConcatEntry(tailSegment)}'\n", cancellationToken);
                _ = await ff.RunTextAsync(ff.Tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", "-f", "concat", "-safe", "0",
                    "-i", concatList, "-map", "0:v:0", "-an", "-c", "copy", "-fps_mode", "passthrough",
                    "-movie_timescale", numerator.ToString(CultureInfo.InvariantCulture),
                    "-video_track_timescale", numerator.ToString(CultureInfo.InvariantCulture), partial],
                    Path.Combine(master, "crossfade-concat.stderr.log"), cancellationToken);
                finished = partial;
            }
            // 本进程刚完成重封装，只核容器帧数/尺寸/时基；异常才解码确认。
            EncodedStream verified = await VerifyEncoded.ProbeAsync(ff, finished,
                "stream=width,height,nb_frames,avg_frame_rate,time_base,duration_ts", loopFrames,
                Path.Combine(master, "crossfade.ffprobe.stderr.log"), cancellationToken);
            if (!verified.Has(width, height, loopFrames))
                throw new InvalidDataException("交叉淡化后的 master 没有给出请求的尺寸与帧数。");
            // 拼接是容器层操作，时间戳漂了不会报错，所以这里连时基和总时长一起核。
            NativeRenderRunner.ConfirmEncodedDuration(verified, loopFrames, numerator, denominator);
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
                ["scope"] = "整帧线性交叉淡化，窗口固定。周期分量在两端逐像素相同因此不受影响；" +
                    "残差分量被摊开在窗口内。没有做任何局部接缝修复。",
                ["method"] = $"只重编码 [0, {cut}) 这一个包含接缝的 GOP，[{cut}, {loopFrames}) 用 concat demuxer 加 " +
                    "-c copy 原样拼接。开销只随淡化窗口与 GOP 长度增长，与 master 总长无关。"
            };
            manifest["loop_crossfade"] = record.DeepClone();
            manifest["color"] = "Lossless H.264 RGB master with the fixed loop crossfade applied; hardware playback is not assumed.";
            await NativeRenderRunner.WriteJsonAsync(manifestPath, manifest, cancellationToken);
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
        string text = await ff.RunTextAsync(ff.Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0",
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
        string sourceHash = (await ff.RunTextAsync(ff.Tools.Ffmpeg, ["-hide_banner", "-nostdin", .. expected.Arguments,
            "-map", "0:v:0", "-vf", expected.Filter, "-frames:v", "1", "-an", "-pix_fmt", "rgb24", "-f", "md5", "-"],
            Path.Combine(master, "crossfade-tail-check-master.stderr.log"), token)).Trim();
        string copiedHash = (await ff.RunTextAsync(ff.Tools.Ffmpeg, ["-hide_banner", "-nostdin", "-i", tailSegment,
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
    /// 而走 <see cref="FrameAccess.DecodeSelectedRgb24Async"/> 的逐帧选取（整秒锚点 + 帧序号计数）。两套定位互相独立，
    /// 任何一边错开一帧都会在这里失败；以前两边用同一个 -ss 写法、同偏同错，这道校验查不出来。
    /// 只取四帧，开销与 master 长度无关。
    /// </summary>
    internal async Task<JsonObject> VerifyWeightsAsync(string video, string headSegment, ulong loopFrames,
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
            _ = await ff.RunTextAsync(ff.Tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", "-i", headSegment,
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
                await foreach ((ulong index, byte[] frame) in FrameAccess.DecodeSelectedRgb24Async(video, ff,
                    (int)width, (int)height, indices, null, Path.Combine(master, "crossfade-verify-source.stderr.log"), token))
                {
                    token.ThrowIfCancellationRequested();
                    if (frame.Length != frameBytes) throw new InvalidDataException("接缝抽样帧的数量或尺寸与 master 不一致。");
                    if (index < loopFrames) { original[index] = frame; continue; }
                    // 到了第 P+i 帧就和第 i 帧、成片第 i 帧一起比，比完立刻放掉第 i 帧，内存里最多同时留三帧。
                    uint sample = checked((uint)(index - loopFrames));
                    if (!original.Remove(sample, out byte[]? before))
                        throw new InvalidDataException("接缝抽样帧没有按帧号顺序解出。");
                    mixed.Seek((long)(sample == 0 ? 0 : 1) * frameBytes, SeekOrigin.Begin);
                    await mixed.ReadExactlyAsync(faded, token);
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
}
