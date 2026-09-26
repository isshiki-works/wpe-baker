using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    /// <summary>GpuVideoEncoder 的 gop_size：段起点取它的整数倍，段首 IDR 正落在不分段时本来就有的关键帧上。</summary>
    private const ulong GpuGopFrames = 250;

    /// <summary>
    /// GPU 直编（无淡化）的组按时间切成至多 <paramref name="segments"/> 段，各起一个渲染器进程并行，同时在跑的进程数受 <paramref name="slots"/> 限制。
    /// 第 k 段把预热延长到段起点（预热只模拟；读上一帧像素的场景照常光栅），CQP、无 B 帧、段起点对齐 GOP，
    /// 所以解码帧与不分段逐帧相同，码流只差各段首 IDR 的 idr_pic_id。各段成品拷包拼接，留帧、覆盖度、运行时依赖合并成与不分段同形的清单。
    /// 每段不足 10 个 GOP、带淡化或不走 GPU 时照旧单进程。
    /// </summary>
    internal async Task<JsonObject> RenderSegmentsAsync(RenderRequest request, int segments, SemaphoreSlim slots,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        ulong encoded = request.EncodedFrames ?? request.Frames;
        ulong step = (encoded / (ulong)Math.Max(1, segments) + GpuGopFrames - 1) / GpuGopFrames * GpuGopFrames;
        if (request.GpuEncoding is not { CrossfadeFrames: 0 } gpu || segments < 2 || step < 10 * GpuGopFrames || step >= encoded)
            return await RenderAsync(request, progress, cancellationToken);
        string output = Path.GetFullPath(request.OutputDirectory);
        ulong[] retain = [.. (request.RetainFrames ?? []).Concat(gpu.RetainQualitySamples ? QualityGate.SampleFrames(encoded) : []).Distinct().Order()];
        ulong[] starts = [.. Enumerable.Range(0, segments).Select(k => (ulong)k * step).Where(start => start < encoded)];
        RenderRequest Part(int k)
        {
            ulong start = starts[k], end = k + 1 < starts.Length ? starts[k + 1] : request.Frames;
            ulong[] mine = [.. retain.Where(index => index >= start && index < end).Select(index => index - start)];
            return request with { OutputDirectory = Path.Combine(output, $"part{k}"), WarmupFrames = request.WarmupFrames + start,
                Frames = end - start, EncodedFrames = Math.Min(end, encoded) - start, RetainFrames = mine.Length > 0 ? mine : null,
                GpuEncoding = gpu with { RetainQualitySamples = false } };
        }
        // 先建好组目录，段目录建在它里面：某段失败时调用方照旧把整个目录挪开再回退，段目录跟着走，
        // 回退重渲不会撞上上一次留下的段目录（段目录原在组目录旁，AV1 段编码器开不了降 HEVC 重渲时撞名整张失败）。
        Directory.CreateDirectory(Path.Combine(output, "native"));
        JsonObject[] parts = await Task.WhenAll(starts.Select(async (_, k) =>
        {
            await slots.WaitAsync(cancellationToken);
            try { return await RenderAsync(Part(k), k == 0 ? progress : null, cancellationToken); }
            finally { slots.Release(); }
        }));
        string[] partDirectories = [.. parts.Select(part => part["request"]!["output_directory"]!.GetValue<string>())];

        // 同一编码器、同一参数的段，SPS/PPS 相同，concat 分离器拷包即可。
        string list = Path.Combine(output, "segments.txt"), video = Path.Combine(output, "preview.mp4");
        await File.WriteAllLinesAsync(list, partDirectories.Select(directory =>
            $"file '{Path.Combine(directory, "preview.mp4").Replace('\\', '/')}'"), cancellationToken);
        string timescale = request.FpsNumerator.ToString(CultureInfo.InvariantCulture);
        _ = await ff.RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy",
            "-movie_timescale", timescale, "-video_track_timescale", timescale, video], Path.Combine(output, "concat.stderr.log"), cancellationToken);
        JsonObject manifest = parts[^1].DeepClone().AsObject();
        JsonNode dimensions = manifest["encode_dimensions"]!;
        EncodedStream verified = await VerifyEncoded.ProbeAsync(ff, video,
            "stream=codec_name,width,height,avg_frame_rate,nb_frames,duration,duration_ts,time_base,pix_fmt,color_space,color_range", encoded,
            Path.Combine(output, "ffprobe.stderr.log"), cancellationToken);
        if (!verified.Has(dimensions["width"]!.GetValue<uint>(), dimensions["height"]!.GetValue<uint>(), encoded) ||
            !verified.RateIs(request.FpsNumerator, request.FpsDenominator))
            throw new InvalidDataException("Concatenated GPU segments differ from the requested dimensions, frame count or exact FPS.");
        ConfirmEncodedDuration(verified, encoded, request.FpsNumerator, request.FpsDenominator);

        manifest["request"] = JsonSerializer.SerializeToNode(request, JsonOptions);
        manifest["encoded_stream"] = verified.Probe;
        manifest["frame_count_validation"] = new JsonObject { ["source"] = verified.CountSource,
            ["full_decode_performed"] = verified.CountSource == "full_decode", ["fallback_reason"] = verified.FallbackReason };
        if (request.EncodedFrames is not null) manifest["encoded_frames"] = encoded;
        await using (var file = File.OpenRead(video))
            manifest["video_sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
        manifest["readback_frames"] = parts.Aggregate(0UL, (sum, part) => sum + part["readback_frames"]!.GetValue<ulong>());
        manifest["readback_bytes"] = parts.Aggregate(0UL, (sum, part) => sum + part["readback_bytes"]!.GetValue<ulong>());
        if (retain.Length > 0)
        {
            string path = Path.Combine(output, "native", RetainedFramesFile);
            await using (var merged = File.Create(path))
                foreach (JsonObject part in parts)
                    if (part["retained_frames"]?["path"]?.GetValue<string>() is { } piece)
                        await using (var from = File.OpenRead(piece)) await from.CopyToAsync(merged, cancellationToken);
            var retained = parts.First(part => part["retained_frames"] is not null)["retained_frames"]!.DeepClone().AsObject();
            retained["path"] = path;
            retained["encoded_frames"] = encoded;
            retained["frame_indices"] = new JsonArray([.. retain.Select(index => (JsonNode?)JsonValue.Create(index))]);
            manifest["retained_frames"] = retained;
        }
        if (request.CollectAlphaBounds)
        {
            JsonObject[] bounds = [.. parts.Select(part => part["alpha_bounds"]!.AsObject())];
            byte[] first = await File.ReadAllBytesAsync(bounds[0]["first_frame_rgba_path"]!.GetValue<string>(), cancellationToken);
            bool identical = true;
            foreach (JsonObject part in bounds)
                identical &= part["pixel_identical_in_generated_interval"]!.GetValue<bool>() &&
                    Same(first, await File.ReadAllBytesAsync(part["first_frame_rgba_path"]!.GetValue<string>(), cancellationToken));
            JsonObject[] content = [.. bounds.Where(part => part["has_content"]!.GetValue<bool>())];
            var union = bounds[0].DeepClone().AsObject();
            if (content.Length > 0)
            {
                int Edge(string origin, string size, bool far) => far
                    ? content.Max(part => part[origin]!.GetValue<int>() + part[size]!.GetValue<int>())
                    : content.Min(part => part[origin]!.GetValue<int>());
                int x = Edge("x", "width", false), y = Edge("y", "height", false);
                union["has_content"] = true; union["x"] = x; union["y"] = y;
                union["width"] = Edge("x", "width", true) - x; union["height"] = Edge("y", "height", true) - y;
            }
            union["minimum_alpha"] = bounds.Min(part => part["minimum_alpha"]!.GetValue<int>());
            union["maximum_alpha"] = bounds.Max(part => part["maximum_alpha"]!.GetValue<int>());
            union["observed_frames"] = bounds.Aggregate(0UL, (sum, part) => sum + part["observed_frames"]!.GetValue<ulong>());
            union["pixel_identical_in_generated_interval"] = identical;
            string firstPath = Path.Combine(output, "native", "first-frame.rgba");
            await File.WriteAllBytesAsync(firstPath, first, cancellationToken);
            union["first_frame_rgba_path"] = firstPath;
            manifest["alpha_bounds"] = union;
        }
        // 后面的段把前面的时间轴也模拟过一遍；依赖与脚本报错仍按全部段去重合并，墙钟取最慢的一段。
        var native = manifest["native_result"]!.AsObject();
        native["runtime_dependencies"] = parts.Select(part => part["native_result"]!["runtime_dependencies"]!.AsArray())
            .Aggregate(new JsonArray(), SceneAssembler.MergeRuntimeDependencies);
        var errors = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? error in parts.SelectMany(part => part["native_result"]!["source_script_errors"]!.AsArray()))
            if (error is not null && seen.Add(error.ToJsonString())) errors.Add(error.DeepClone());
        native["source_script_errors"] = errors;
        native["source_script_error_count"] = errors.Count;
        native["wall_seconds"] = parts.Max(part => part["native_result"]!["wall_seconds"]!.GetValue<double>());
        manifest["segments"] = new JsonArray([.. parts.Select((part, k) => (JsonNode?)new JsonObject { ["start_frame"] = starts[k],
            ["frames"] = part["request"]!["frames"]!.DeepClone(), ["wall_seconds"] = part["native_result"]!["wall_seconds"]!.DeepClone() })]);
        manifest["completed_utc"] = DateTimeOffset.UtcNow.ToString("O");
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), manifest, cancellationToken);
        // 段目录删掉：拷包拼好的 preview.mp4 已含全部段，段成品不再占盘。
        foreach (string directory in partDirectories) Directory.Delete(directory, true);
        return manifest;

        static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    }
}
