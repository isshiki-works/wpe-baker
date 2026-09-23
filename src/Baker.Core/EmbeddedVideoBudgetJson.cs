using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 内嵌视频大小判据的 JSON/文案/IO 一侧：试编码判定写成 bake 记录、编码后超限的拒绝理由、ffprobe 读包，以及循环上限收紧记录的 plan 字段。
/// 数值判据在 Domain 的 <see cref="EmbeddedVideoBudget"/>。
/// </summary>
public static class EmbeddedVideoBudgetJson
{
    /// <summary>主渲染前的判定：任一视频组外推超限就是 predicted_over_limit，带中英理由；没有样本时 not_estimated，不拒绝。</summary>
    public static JsonObject EvaluateProbe(IReadOnlyList<EmbeddedVideoBudget.ProbeGroup> groups, ulong frames, uint fpsNumerator, uint fpsDenominator,
        Message? notEstimatedReason = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var result = new JsonObject { ["maximum_bytes"] = EmbeddedVideoBudget.MaximumBytes, ["frames"] = frames,
            ["assumed_key_frame_interval"] = EmbeddedVideoBudget.AssumedKeyFrameInterval,
            ["basis"] = "Composition-probe packets extrapolated with one largest key frame per interval and the mean non-key packet; on five existing bakes this was 0.77-1.38x the real size, so it only rejects over-limit loops and never raises the analyze limit." };
        if (groups.Count == 0)
        {
            result["status"] = "not_estimated";
            (notEstimatedReason ?? new Message("bake.probe_no_encoded_group")).Write(result, "reason");
            return result;
        }
        var records = new JsonArray();
        EmbeddedVideoBudget.ProbeGroup? worst = null;
        double worstBytes = 0;
        ulong worstMaximum = 0;
        foreach (EmbeddedVideoBudget.ProbeGroup group in groups)
        {
            double predicted = EmbeddedVideoBudget.ExtrapolateBytes(group.Packets, frames);
            ulong maximum = EmbeddedVideoBudget.ExtrapolatedMaximumFrames(group.Packets);
            bool over = predicted > EmbeddedVideoBudget.MaximumBytes;
            double[] nonKey = group.Packets.Where(packet => !packet.Key).Select(packet => (double)packet.Size).ToArray();
            records.Add(new JsonObject { ["id"] = group.Id, ["packed_alpha"] = group.PackedAlpha,
                ["encoded_width"] = group.EncodedWidth, ["encoded_height"] = group.EncodedHeight,
                ["probe_frames"] = group.ProbeFrames, ["probe_bytes"] = group.ProbeBytes, ["probe_packets"] = group.Packets.Count,
                ["key_frame_bytes"] = group.Packets.Where(packet => packet.Key).Select(packet => packet.Size).DefaultIfEmpty(0).Max(),
                ["mean_non_key_frame_bytes"] = nonKey.Length == 0 ? null : Math.Round(nonKey.Average(), 1),
                ["predicted_bytes"] = (long)Math.Round(predicted), ["maximum_frames"] = maximum,
                ["maximum_seconds"] = EmbeddedVideoBudget.WholeSeconds(maximum, fpsNumerator, fpsDenominator), ["over_limit"] = over });
            if (over && (worst is null || predicted > worstBytes)) { worst = group; worstBytes = predicted; worstMaximum = maximum; }
        }
        result["groups"] = records;
        result["status"] = worst is null ? "within_limit" : "predicted_over_limit";
        if (worst is not null)
        {
            object?[] args = [worst.Id, frames, Seconds(frames, fpsNumerator, fpsDenominator), Gibibytes(worstBytes),
                EmbeddedVideoBudget.WholeSeconds(worstMaximum, fpsNumerator, fpsDenominator).ToString("0", CultureInfo.InvariantCulture)];
            new Message("bake.embedded_video_size_predicted", args).Write(result, "reason");
        }
        return result;
    }

    /// <summary>编码后的实际字节超限时的拒绝理由（外推低估时的兜底）。</summary>
    public static Message EncodedRejection(string groupId, long bytes, ulong frames, uint fpsNumerator, uint fpsDenominator)
    {
        ulong maximum = bytes <= 0 ? frames : (ulong)Math.Floor((double)frames * EmbeddedVideoBudget.MaximumBytes / bytes);
        return new Message("bake.embedded_video_size_rejected", [groupId, Gibibytes(bytes), frames,
            Seconds(frames, fpsNumerator, fpsDenominator), EmbeddedVideoBudget.WholeSeconds(maximum, fpsNumerator, fpsDenominator).ToString("0", CultureInfo.InvariantCulture)]);
    }

    /// <summary>用 ffprobe 读出视频流全部包的大小与关键帧标记（试编码只有几十个包）。</summary>
    internal static async Task<IReadOnlyList<EmbeddedVideoBudget.VideoPacket>> ReadPacketsAsync(NativeTools tools, string video, CancellationToken cancellationToken)
    {
        string text = await new FfmpegTool(tools).RunCapturedAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=size,flags", "-of", "csv=p=0", video], cancellationToken);
        var packets = new List<EmbeddedVideoBudget.VideoPacket>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(',');
            if (fields.Length < 2 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                throw new InvalidDataException("ffprobe returned an unreadable packet line.");
            packets.Add(new(size, fields[1].Contains('K', StringComparison.Ordinal)));
        }
        return packets;
    }

    public static JsonObject ToJson(EmbeddedVideoLoopLimit limit) => new() {
        ["applied"] = limit.Applied, ["requested_loop_length_maximum_seconds"] = limit.RequestedSeconds, ["fit_seconds"] = limit.FitSeconds,
        ["maximum_bytes"] = EmbeddedVideoBudget.MaximumBytes, ["encoded_width"] = limit.EncodedWidth, ["encoded_height"] = limit.EncodedHeight,
        ["packed_alpha"] = limit.PackedAlpha, ["fps_numerator"] = limit.FpsNumerator, ["fps_denominator"] = limit.FpsDenominator,
        ["reference_bytes_per_frame"] = Math.Round(limit.ReferenceBytesPerFrame),
        ["basis"] = "Wallpaper Engine 2.8.42 did not display a 2,922,466,521-byte embedded video and played 2,104,622,403 bytes; the reference bitrate is the highest per-frame size among existing long full-frame bakes (3572877776), scaled by pixels^0.537." };

    /// <summary>"该分辨率下最长约 x 秒"一句（结论行与无解原因共用同一格式）；没有收紧时为空串。</summary>
    public static string Sentence(EmbeddedVideoLoopLimit limit, string language) => PlanNarrative.EmbeddedVideoLimitLine(ToJson(limit), language) ?? "";

    private static string Seconds(ulong frames, uint fpsNumerator, uint fpsDenominator) =>
        ((double)frames * fpsDenominator / fpsNumerator).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Gibibytes(double bytes) => (bytes / (1L << 30)).ToString("0.00", CultureInfo.InvariantCulture);
}
