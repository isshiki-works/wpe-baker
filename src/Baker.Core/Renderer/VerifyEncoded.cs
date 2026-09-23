using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 全仓唯一的成品探流核对：ffprobe 读容器头的第一路视频流，帧数先信容器头（nb_frames），缺失或与期望不符才全解码计数。
/// 读哪些字段（show_entries）由调用方给，因为探流原文会原样写进 manifest 的 encoded_stream；尺寸、帧率、时长的比较在
/// <see cref="EncodedStream"/> 上各只有一份，拒绝时的文案仍由各调用方给。
/// </summary>
internal static class VerifyEncoded
{
    /// <param name="logPath">给了就把 ffprobe 的 stderr 落这个新文件（失败抛 IOException）；null 则收进内存（失败抛 InvalidDataException）。</param>
    /// <param name="threads">探流时带 <c>-threads 4</c>（原来 5 处里 3 处带、2 处不带，照原样保留）。</param>
    public static async Task<EncodedStream> ProbeAsync(FfmpegTool tool, string file, string entries, ulong expectedFrames,
        string? logPath, CancellationToken token, bool threads = true)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Encoded video is missing.", file);
        if (!File.Exists(tool.Tools.Ffprobe)) throw new FileNotFoundException("Required native tool is missing.", tool.Tools.Ffprobe);
        string[] arguments = threads
            ? ["-v", "error", "-threads", "4", "-select_streams", "v:0", "-show_entries", entries, "-of", "json", file]
            : ["-v", "error", "-select_streams", "v:0", "-show_entries", entries, "-of", "json", file];
        string text = logPath is null ? await tool.RunCapturedAsync(tool.Tools.Ffprobe, arguments, token)
            : await tool.RunTextAsync(tool.Tools.Ffprobe, arguments, logPath, token);
        JsonObject probe = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("ffprobe returned no unique video stream.");
        JsonObject stream = probe["streams"]?.AsArray().SingleOrDefault()?.AsObject()
            ?? throw new InvalidDataException("ffprobe returned no unique video stream.");
        (ulong frames, string source, string? fallback) = await FrameCountAsync(tool, file, stream, expectedFrames, token);
        return new(probe, stream, frames, source, fallback);
    }

    /// <summary>
    /// 本工具成功写出的成品先核容器帧数；缺失或不一致时才解码确认，并保留实际计数来源。
    /// </summary>
    internal static async Task<(ulong Count, string Source, string? FallbackReason)> FrameCountAsync(FfmpegTool tool, string file,
        JsonObject stream, ulong expectedFrames, CancellationToken token)
    {
        if (!TryContainerFrameCount(stream, out ulong declared))
            return (await CountFramesAsync(tool, file, token), "full_decode",
                "The container header did not declare a positive nb_frames, so the frames were counted by decoding.");
        if (declared == expectedFrames) return (declared, "container_header", null);
        return (await CountFramesAsync(tool, file, token), "full_decode",
            $"The container header declared {declared.ToString(CultureInfo.InvariantCulture)} frames instead of the expected " +
            $"{expectedFrames.ToString(CultureInfo.InvariantCulture)}, so the frames were counted by decoding before judging.");
    }

    /// <summary>容器头声明的帧数；没有或不是正数时返回 false。</summary>
    internal static bool TryContainerFrameCount(JsonObject stream, out ulong count) =>
        ulong.TryParse(stream["nb_frames"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0;

    /// <summary>ffprobe -count_frames 全解码计帧。</summary>
    internal static async Task<ulong> CountFramesAsync(FfmpegTool tool, string file, CancellationToken token)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Encoded video is missing.", file);
        if (!File.Exists(tool.Tools.Ffprobe)) throw new FileNotFoundException("Required native tool is missing.", tool.Tools.Ffprobe);
        string text = await tool.RunCapturedAsync(tool.Tools.Ffprobe, ["-v", "error", "-threads", "4", "-count_frames",
            "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "json", file], token);
        JsonObject stream = JsonNode.Parse(text)?["streams"]?.AsArray().SingleOrDefault()?.AsObject()
            ?? throw new InvalidDataException("ffprobe returned no unique video stream.");
        return ulong.TryParse(stream["nb_read_frames"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong count) && count > 0
            ? count : throw new InvalidDataException("ffprobe did not return a positive counted video frame total.");
    }
}

/// <summary>一次探流核对的结果：ffprobe 原文（写进 encoded_stream）、那一路视频流、实际帧数及其来源。</summary>
internal sealed record EncodedStream(JsonObject Probe, JsonObject Stream, ulong Frames, string CountSource, string? FallbackReason)
{
    /// <summary>尺寸与帧数都对上（宽高缺失即不符）。</summary>
    public bool Has(long width, long height, ulong frames) =>
        Stream["width"] is JsonValue w && w.TryGetValue(out long actualWidth) && actualWidth == width &&
        Stream["height"] is JsonValue h && h.TryGetValue(out long actualHeight) && actualHeight == height && Frames == frames;

    /// <summary>avg_frame_rate 与请求的有理帧率精确相等（交叉相乘，不约分）。</summary>
    public bool RateIs(uint numerator, uint denominator)
    {
        string[] rate = (Stream["avg_frame_rate"]?.GetValue<string>() ?? "0/1").Split('/');
        return rate.Length == 2 && ulong.TryParse(rate[0], out ulong n) && ulong.TryParse(rate[1], out ulong d) && d != 0 &&
            (UInt128)n * denominator == (UInt128)d * numerator;
    }

    /// <summary>时基正好 1/fps_num，且总时长正好 frames 个帧间隔（duration_ts == frames·fps_den）。</summary>
    public bool DurationIs(ulong frames, uint numerator, uint denominator) =>
        Stream["time_base"]?.GetValue<string>() == $"1/{numerator}" &&
        Stream["duration_ts"] is JsonValue duration && duration.TryGetValue(out ulong ticks) &&
        (UInt128)ticks == (UInt128)frames * denominator;
}
