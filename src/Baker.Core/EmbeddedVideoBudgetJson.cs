using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 内嵌视频大小判据的 JSON/文案/IO 一侧：试编码判定写成 bake 记录、编码后超限的拒绝理由、ffprobe 读包。
/// 数值判据在 Domain 的 <see cref="EmbeddedVideoBudget"/>。
/// </summary>
public static class EmbeddedVideoBudgetJson
{
    /// <summary>
    /// 主渲染前的外推：任一视频组超出目标体积就是 predicted_over_limit，quantizer_offset 是按最大那组算出的量化值增量
    /// （主渲染的所有编码档都加它，见 <see cref="EmbeddedVideoBudget.QuantizerOffset"/>），不拒绝；没有样本时 not_estimated。
    /// </summary>
    public static JsonObject EvaluateProbe(IReadOnlyList<EmbeddedVideoBudget.ProbeGroup> groups, ulong frames, uint fpsNumerator, uint fpsDenominator,
        Message? notEstimatedReason = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var result = new JsonObject { ["maximum_bytes"] = EmbeddedVideoBudget.MaximumBytes, ["frames"] = frames,
            ["assumed_key_frame_interval"] = EmbeddedVideoBudget.AssumedKeyFrameInterval,
            ["target_bytes"] = EmbeddedVideoBudget.TargetBytes,
            ["basis"] = "Composition-probe packets extrapolated with one largest key frame per interval and the mean non-key packet (0.77-1.38x the real size on five existing bakes). Over the target, every playback encoder of the main render raises its quantizer by quantizer_offset (about +6 per halving); the bytes actually written are checked again after encoding and the quantizer is raised once more if still over the limit." };
        if (groups.Count == 0)
        {
            result["status"] = "not_estimated";
            (notEstimatedReason ?? new Message("bake.probe_no_encoded_group")).Write(result, "reason");
            return result;
        }
        var records = new JsonArray();
        double worstBytes = 0;
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
            worstBytes = Math.Max(worstBytes, predicted);
        }
        result["groups"] = records;
        int offset = EmbeddedVideoBudget.QuantizerOffset(worstBytes);
        result["status"] = offset == 0 ? "within_limit" : "predicted_over_limit";
        result["quantizer_offset"] = offset;
        return result;
    }

    /// <summary>
    /// 内嵌视频超 2 GiB 被拒的报告里"按码率最长能做多少秒"（外推与编码后两条拒绝文案的第 5 个参数，整秒）；
    /// 不是这类拒绝、读不到或不足 1 s 时 null。
    /// </summary>
    public static double? MaximumSeconds(JsonObject report) =>
        report["status"]?.GetValue<string>() == EmbeddedVideoBudget.RejectedBakeStatus &&
        report["reason_localized"]?["params"] is JsonArray { Count: > 4 } args && args[4]?.GetValue<string>() is string text &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 1 ? seconds : null;

    /// <summary>编码后的实际字节超限时的拒绝理由（外推低估时的兜底）。</summary>
    public static Message EncodedRejection(string groupId, long bytes, ulong frames, uint fpsNumerator, uint fpsDenominator)
    {
        ulong maximum = bytes <= 0 ? frames : (ulong)Math.Floor((double)frames * EmbeddedVideoBudget.MaximumBytes / bytes);
        return new Message("bake.embedded_video_size_rejected", [groupId, Gibibytes(bytes), frames,
            Seconds(frames, fpsNumerator, fpsDenominator), EmbeddedVideoBudget.WholeSeconds(maximum, fpsNumerator, fpsDenominator).ToString("0", CultureInfo.InvariantCulture)]);
    }

    /// <summary>体积限制下画质门没过的拒绝理由：<paramref name="neededBytes"/> 是不抬量化值时的字节估计，第 5 个参数照旧是按它能做的最长秒数。</summary>
    public static Message QualityRejection(string groupId, double neededBytes, ulong frames, uint fpsNumerator, uint fpsDenominator,
        double? ssim, double threshold, int quantizerOffset)
    {
        ulong maximum = neededBytes <= 0 ? frames : (ulong)Math.Floor(frames * EmbeddedVideoBudget.MaximumBytes / neededBytes);
        return new Message("bake.embedded_video_quality_rejected", [groupId, Gibibytes(neededBytes), frames,
            Seconds(frames, fpsNumerator, fpsDenominator),
            EmbeddedVideoBudget.WholeSeconds(maximum, fpsNumerator, fpsDenominator).ToString("0", CultureInfo.InvariantCulture),
            ssim?.ToString("0.######", CultureInfo.InvariantCulture) ?? "n/a", threshold.ToString("0.######", CultureInfo.InvariantCulture),
            quantizerOffset]);
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

    private static string Seconds(ulong frames, uint fpsNumerator, uint fpsDenominator) =>
        ((double)frames * fpsDenominator / fpsNumerator).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Gibibytes(double bytes) => (bytes / (1L << 30)).ToString("0.00", CultureInfo.InvariantCulture);
}
