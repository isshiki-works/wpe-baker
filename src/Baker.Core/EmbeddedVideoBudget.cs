using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// Wallpaper Engine 内嵌视频（TEX 容器 TEXB0004 里的 MP4）的大小上限，以及成品视频大小的两种预估：
/// analyze 按参考码率收紧摆动改频的循环长度上限；bake 在主渲染前按短段试编码（composition probe）外推，超限就干净拒绝；
/// 编码后仍超限（外推低估）时在接缝校验前拒绝，不再跑到装配才失败。
/// </summary>
public static class EmbeddedVideoBudget
{
    /// <summary>
    /// 内嵌 MP4 的最大字节数 2^31−1。依据是 WPE 2.8.42 的实测（2026-09-17，reports-20260916/fix-embedded-video-size.md）：
    /// 同一成品视频截成 140,372,391 与 2,104,622,403 字节时，在 -playInWindow 私有窗口里正常播放；完整的 2,922,466,521 字节
    /// （超过 2^31−1、低于 2^32）视频层不显示，只剩清屏色。TEX 头里的长度字段是 32 位。成品工程是项目目录，
    /// 不打包 scene.pkg，所以这不是 pkg 偏移的限制；WPE 场景里的视频纹理也只认 TEX 内嵌这一种形式。
    /// </summary>
    public const long MaximumBytes = int.MaxValue;

    /// <summary>bake 因成品视频超过上限而拒绝时的状态（渲染前外推与编码后实测共用）。</summary>
    public const string RejectedBakeStatus = "candidate_rejected_embedded_video_size";

    // 参考码率：现有成品中每帧字节最高的全幅长片 3572877776（时钟场景，9180 帧，libx264 crf16 fast，60 fps），
    // 同一内容两个分辨率的实测成品：1920×1080 为 279,409,688 字节，3414×1920 为 518,554,375 字节（含 MP4 容器开销）。
    // 每帧字节按编码像素数的幂律缩放，指数由这两点定出（约 0.537）；阿米娅、绫波丽、DJGun 的同内容对照在 0.47–0.72 之间。
    // 每帧字节不随帧率增加（120 fps 的帧间差更小），所以按帧计是偏保守的。
    private const double ReferenceLowPixels = 1920d * 1080, ReferenceLowBytesPerFrame = 279_409_688d / 9180;
    private const double ReferenceHighPixels = 3414d * 1920, ReferenceHighBytesPerFrame = 518_554_375d / 9180;

    /// <summary>参考码率的像素幂律指数。</summary>
    public static readonly double ReferencePixelExponent =
        Math.Log(ReferenceHighBytesPerFrame / ReferenceLowBytesPerFrame) / Math.Log(ReferenceHighPixels / ReferenceLowPixels);

    /// <summary>外推假定的关键帧间隔：libx264、libx265、h264_nvenc 默认 GOP 的实测值（design-long-loop-streaming.md §2.6）。</summary>
    public const ulong AssumedKeyFrameInterval = 250;

    /// <summary>编码帧的像素数：透明组把 alpha 左右并排，宽度翻倍。</summary>
    public static double EncodedPixels(uint width, uint height, bool packedAlpha) => (double)width * height * (packedAlpha ? 2 : 1);

    /// <summary>参考码率下每帧字节（含容器开销）。</summary>
    public static double ReferenceBytesPerFrame(double encodedPixels)
    {
        if (!double.IsFinite(encodedPixels) || encodedPixels <= 0) throw new ArgumentOutOfRangeException(nameof(encodedPixels));
        return ReferenceLowBytesPerFrame * Math.Pow(encodedPixels / ReferenceLowPixels, ReferencePixelExponent);
    }

    /// <summary>参考码率下装得进上限的最多帧数。</summary>
    public static ulong ReferenceMaximumFrames(uint width, uint height, bool packedAlpha) =>
        (ulong)Math.Floor(MaximumBytes / ReferenceBytesPerFrame(EncodedPixels(width, height, packedAlpha)));

    /// <summary>帧数换成整秒（向下取整）。</summary>
    public static double WholeSeconds(ulong frames, uint fpsNumerator, uint fpsDenominator)
    {
        if (fpsNumerator == 0 || fpsDenominator == 0) throw new ArgumentException("FPS must be positive.");
        return Math.Floor((double)frames * fpsDenominator / fpsNumerator);
    }

    /// <summary>
    /// 摆动改频的循环长度上限按参考码率收紧。输出尺寸未知（0）时不收紧并返回 null。
    /// 依据是参考码率（现有成品里最高的），不是这个场景自己的码率：画面简单的场景在长档会被多限，bake 前的试编码外推只负责拒绝、不负责放宽。
    /// </summary>
    public static EmbeddedVideoLoopLimit? LoopLengthLimit(double requestedSeconds, uint width, uint height, bool packedAlpha,
        uint fpsNumerator, uint fpsDenominator)
    {
        if (width == 0 || height == 0 || fpsNumerator == 0 || fpsDenominator == 0) return null;
        ulong frames = ReferenceMaximumFrames(width, height, packedAlpha);
        return new(requestedSeconds, WholeSeconds(frames, fpsNumerator, fpsDenominator), width * (packedAlpha ? 2u : 1u), height,
            packedAlpha, fpsNumerator, fpsDenominator, ReferenceBytesPerFrame(EncodedPixels(width, height, packedAlpha)));
    }

    /// <summary>ffprobe 读出的一个视频包：字节数与是否关键帧。</summary>
    public readonly record struct VideoPacket(long Size, bool Key);

    /// <summary>
    /// 从短段试编码的包大小外推 frames 帧的成品字节：每 <see cref="AssumedKeyFrameInterval"/> 帧一个关键帧，大小取试片里最大的关键帧；
    /// 其余帧取试片非关键帧的平均。试片只有 48 帧、从第 0 帧起、不含粒子预热，现有 5 案外推为实测的 0.77–1.38 倍
    /// （高熵场景偏低，静态插画偏高），所以只拿来在超限时拒绝，不用来放宽 analyze 的上限。
    /// </summary>
    public static double ExtrapolateBytes(IReadOnlyList<VideoPacket> packets, ulong frames)
    {
        ArgumentNullException.ThrowIfNull(packets);
        if (packets.Count == 0 || packets.Any(packet => packet.Size < 0)) throw new InvalidDataException("A trial encode needs at least one packet.");
        double key = packets.Where(packet => packet.Key).Select(packet => (double)packet.Size).DefaultIfEmpty(packets[0].Size).Max();
        double[] nonKey = packets.Where(packet => !packet.Key).Select(packet => (double)packet.Size).ToArray();
        double inter = nonKey.Length == 0 ? key : nonKey.Average();
        ulong keys = frames == 0 ? 0 : (frames + AssumedKeyFrameInterval - 1) / AssumedKeyFrameInterval;
        return keys * key + (frames - keys) * inter;
    }

    /// <summary>按同一外推装得进上限的最多帧数。</summary>
    public static ulong ExtrapolatedMaximumFrames(IReadOnlyList<VideoPacket> packets)
    {
        double perInterval = ExtrapolateBytes(packets, AssumedKeyFrameInterval);
        if (perInterval <= 0) return ulong.MaxValue;
        ulong frames = (ulong)Math.Floor(MaximumBytes / perInterval) * AssumedKeyFrameInterval;
        // 整段 GOP 之后再逐帧补：下一帧若开新 GOP 要多算一个关键帧。
        while (ExtrapolateBytes(packets, frames + 1) <= MaximumBytes) ++frames;
        while (frames > 0 && ExtrapolateBytes(packets, frames) > MaximumBytes) --frames;
        return frames;
    }

    /// <summary>试编码里一个视频组的包表。</summary>
    public sealed record ProbeGroup(string Id, bool PackedAlpha, uint EncodedWidth, uint EncodedHeight, ulong ProbeFrames,
        long ProbeBytes, IReadOnlyList<VideoPacket> Packets);

    /// <summary>主渲染前的判定：任一视频组外推超限就是 predicted_over_limit，带中英理由；没有样本时 not_estimated，不拒绝。</summary>
    public static JsonObject EvaluateProbe(IReadOnlyList<ProbeGroup> groups, ulong frames, uint fpsNumerator, uint fpsDenominator,
        Message? notEstimatedReason = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var result = new JsonObject { ["maximum_bytes"] = MaximumBytes, ["frames"] = frames,
            ["assumed_key_frame_interval"] = AssumedKeyFrameInterval,
            ["basis"] = "Composition-probe packets extrapolated with one largest key frame per interval and the mean non-key packet; on five existing bakes this was 0.77-1.38x the real size, so it only rejects over-limit loops and never raises the analyze limit." };
        if (groups.Count == 0)
        {
            result["status"] = "not_estimated";
            (notEstimatedReason ?? new Message("bake.probe_no_encoded_group")).Write(result, "reason");
            return result;
        }
        var records = new JsonArray();
        ProbeGroup? worst = null;
        double worstBytes = 0;
        ulong worstMaximum = 0;
        foreach (ProbeGroup group in groups)
        {
            double predicted = ExtrapolateBytes(group.Packets, frames);
            ulong maximum = ExtrapolatedMaximumFrames(group.Packets);
            bool over = predicted > MaximumBytes;
            double[] nonKey = group.Packets.Where(packet => !packet.Key).Select(packet => (double)packet.Size).ToArray();
            records.Add(new JsonObject { ["id"] = group.Id, ["packed_alpha"] = group.PackedAlpha,
                ["encoded_width"] = group.EncodedWidth, ["encoded_height"] = group.EncodedHeight,
                ["probe_frames"] = group.ProbeFrames, ["probe_bytes"] = group.ProbeBytes, ["probe_packets"] = group.Packets.Count,
                ["key_frame_bytes"] = group.Packets.Where(packet => packet.Key).Select(packet => packet.Size).DefaultIfEmpty(0).Max(),
                ["mean_non_key_frame_bytes"] = nonKey.Length == 0 ? null : Math.Round(nonKey.Average(), 1),
                ["predicted_bytes"] = (long)Math.Round(predicted), ["maximum_frames"] = maximum,
                ["maximum_seconds"] = WholeSeconds(maximum, fpsNumerator, fpsDenominator), ["over_limit"] = over });
            if (over && (worst is null || predicted > worstBytes)) { worst = group; worstBytes = predicted; worstMaximum = maximum; }
        }
        result["groups"] = records;
        result["status"] = worst is null ? "within_limit" : "predicted_over_limit";
        if (worst is not null)
        {
            object?[] args = [worst.Id, frames, Seconds(frames, fpsNumerator, fpsDenominator), Gibibytes(worstBytes),
                WholeSeconds(worstMaximum, fpsNumerator, fpsDenominator).ToString("0", CultureInfo.InvariantCulture)];
            new Message("bake.embedded_video_size_predicted", args).Write(result, "reason");
        }
        return result;
    }

    /// <summary>编码后的实际字节超限时的拒绝理由（外推低估时的兜底）。</summary>
    public static Message EncodedRejection(string groupId, long bytes, ulong frames, uint fpsNumerator, uint fpsDenominator)
    {
        ulong maximum = bytes <= 0 ? frames : (ulong)Math.Floor((double)frames * MaximumBytes / bytes);
        return new Message("bake.embedded_video_size_rejected", [groupId, Gibibytes(bytes), frames,
            Seconds(frames, fpsNumerator, fpsDenominator), WholeSeconds(maximum, fpsNumerator, fpsDenominator).ToString("0", CultureInfo.InvariantCulture)]);
    }

    /// <summary>用 ffprobe 读出视频流全部包的大小与关键帧标记（试编码只有几十个包）。</summary>
    internal static async Task<IReadOnlyList<VideoPacket>> ReadPacketsAsync(NativeTools tools, string video, CancellationToken cancellationToken)
    {
        string text = await EncodedQualityValidator.RunAsync(tools.Ffprobe, tools, ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=size,flags", "-of", "csv=p=0", video], cancellationToken);
        var packets = new List<VideoPacket>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(',');
            if (fields.Length < 2 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                throw new InvalidDataException("ffprobe returned an unreadable packet line.");
            packets.Add(new(size, fields[1].Contains('K', StringComparison.Ordinal)));
        }
        return packets;
    }

    private static string Seconds(ulong frames, uint fpsNumerator, uint fpsDenominator) =>
        ((double)frames * fpsDenominator / fpsNumerator).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Gibibytes(double bytes) => (bytes / (1L << 30)).ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>analyze 对摆动改频循环长度上限的收紧记录。</summary>
public sealed record EmbeddedVideoLoopLimit(double RequestedSeconds, double FitSeconds, uint EncodedWidth, uint EncodedHeight,
    bool PackedAlpha, uint FpsNumerator, uint FpsDenominator, double ReferenceBytesPerFrame)
{
    /// <summary>用户给的上限超过参考码率下装得下的长度时才收紧。</summary>
    public bool Applied => FitSeconds < RequestedSeconds;

    /// <summary>生效的循环长度上限（秒）。</summary>
    public double EffectiveSeconds => Math.Min(RequestedSeconds, FitSeconds);

    public JsonObject ToJson() => new() {
        ["applied"] = Applied, ["requested_loop_length_maximum_seconds"] = RequestedSeconds, ["fit_seconds"] = FitSeconds,
        ["maximum_bytes"] = EmbeddedVideoBudget.MaximumBytes, ["encoded_width"] = EncodedWidth, ["encoded_height"] = EncodedHeight,
        ["packed_alpha"] = PackedAlpha, ["fps_numerator"] = FpsNumerator, ["fps_denominator"] = FpsDenominator,
        ["reference_bytes_per_frame"] = Math.Round(ReferenceBytesPerFrame),
        ["basis"] = "Wallpaper Engine 2.8.42 did not display a 2,922,466,521-byte embedded video and played 2,104,622,403 bytes; the reference bitrate is the highest per-frame size among existing long full-frame bakes (3572877776), scaled by pixels^0.537." };

    /// <summary>"该分辨率下最长约 x 秒"一句（结论行与无解原因共用同一格式）；没有收紧时为空串。</summary>
    public string Sentence(string language) => PlanNarrative.EmbeddedVideoLimitLine(ToJson(), language) ?? "";
}
