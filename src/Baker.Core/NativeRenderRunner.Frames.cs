using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    /// <param name="ReadSeconds">等渲染器交出帧的累计墙钟（渲染+回读）。</param>
    /// <param name="WriteSeconds">等编码器吃下帧的累计墙钟（编码背压）。</param>
    private sealed record FrameStreamSummary(JsonObject? Bounds, JsonObject? Samples, JsonObject? OpaquePixels,
        double ReadSeconds = 0, double WriteSeconds = 0, JsonObject? Retained = null,
        double OpaqueScanSeconds = 0, double BoundsSeconds = 0, double IdenticalSeconds = 0,
        double SampleSeconds = 0, double RetainWriteSeconds = 0, double LoopSeconds = 0, double StallSeconds = 0);

    /// <summary>渲染时留下的原始 RGBA 帧文件名（与 master 同目录，组结束时随中间文件一起删）。</summary>
    internal const string RetainedFramesFile = "retained-frames.rgba";

    private sealed class OpaquePixelException(JsonObject evidence) : IOException(
        $"Renderer emitted a non-opaque pixel at frame {evidence["first_nonopaque_frame"]}, " +
        $"({evidence["x"]}, {evidence["y"]}), alpha={evidence["alpha"]}.")
    {
        internal JsonObject Evidence { get; } = evidence;
    }

    /// <summary>
    /// 异常链里若有"要求不透明却读到 alpha&lt;255"的失败，返回其证据副本；否则 null。
    /// 调用方据此把它当成一次有理由的拒绝，而不是未处理的崩溃。
    /// </summary>
    internal static JsonObject? OpaquePixelEvidence(Exception error) =>
        FindOpaquePixelFailure(error)?.Evidence.DeepClone().AsObject();

    /// <summary>
    /// 第一个非不透明像素所在帧的完整证据：首个坐标与 alpha 保持原样，另把这一帧扫完，
    /// 记下非不透明像素数、最低 alpha，以及它们离画面边缘最远有多远（用来区分"只是边缘几行"还是"成片透明"）。
    /// 只多扫这一帧，随后照旧中止渲染。
    /// </summary>
    private static JsonObject NonOpaqueFrameEvidence(ReadOnlySpan<byte> rgba, int width, int height, ulong frame, int firstAlphaIndex)
    {
        int first = firstAlphaIndex / 4;
        ulong count = 0;
        byte minimum = byte.MaxValue;
        int farthestFromEdge = 0;
        for (int alphaIndex = firstAlphaIndex; alphaIndex < rgba.Length; alphaIndex += 4)
        {
            byte alpha = rgba[alphaIndex];
            if (alpha == byte.MaxValue) continue;
            ++count;
            minimum = Math.Min(minimum, alpha);
            int pixel = alphaIndex / 4, x = pixel % width, y = pixel / width;
            farthestFromEdge = Math.Max(farthestFromEdge, Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y)));
        }
        return new JsonObject {
            ["requested"] = true, ["verified"] = false, ["checked_frames"] = frame + 1,
            ["first_nonopaque_frame"] = frame, ["x"] = first % width, ["y"] = first / width,
            ["alpha"] = rgba[firstAlphaIndex],
            ["frame_width"] = width, ["frame_height"] = height,
            ["nonopaque_pixels_in_frame"] = count, ["minimum_alpha_in_frame"] = (int)minimum,
            ["maximum_edge_distance_in_frame"] = farthestFromEdge,
            ["basis"] = "Every decoded native RGBA frame was scanned in renderer resolution before encoding.",
            ["frame_scope"] = "Counts, minimum alpha and edge distance cover only the first non-opaque frame; rendering stopped there."
        };
    }

    internal static bool IsSampleFrame(RenderRequest request, ulong frame) => request.FrameSampleStride > 0 &&
        (frame % request.FrameSampleStride == 0 || request.FrameSamplePhaseFrames is ulong phase &&
         frame % request.FrameSampleStride == phase % request.FrameSampleStride);

    private static async Task<FrameStreamSummary> CopyFrameStreamAsync(Stream source, Stream destination,
        RenderRequest request, string output, IProgress<RenderProgress>? progress, CancellationToken token,
        bool sparseInput = false)
    {
        int width = checked((int)request.Width), height = checked((int)request.Height);
        var bounds = new FrameBounds(width, height);
        byte[]? firstFrame = null;
        string? firstFramePath = request.CollectAlphaBounds ? Path.Combine(output, "first-frame.rgba") : null;
        bool pixelIdentical = true;
        int sampleWidth = request.FrameSampleStride > 0 ? (int)Math.Min(request.Width, Math.Max(1, request.FrameSampleWidth)) : 0;
        int sampleHeight = sampleWidth > 0 ? Math.Max(1, (int)Math.Round((double)height * sampleWidth / width)) : 0;
        int storedSampleWidth = request.FrameSampleIncludeAlpha ? sampleWidth * 2 : sampleWidth;
        string samplePath = Path.Combine(output, "frame-samples.rgb");
        await using var samples = sampleWidth > 0 ? new FileStream(samplePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true) : null;
        ulong sampleCount = 0;
        long lastReport = 0;
        // 只有前 encoded 帧进编码器；之后的帧（源周期路线的第 P 帧）只渲染、不编码，也不算进覆盖范围与静止判定。
        ulong encoded = request.EncodedFrames ?? request.Frames;
        HashSet<ulong>? retain = request.RetainFrames is { Length: > 0 } indices ? [.. indices] : null;
        string retainedPath = Path.Combine(output, RetainedFramesFile);
        await using var retained = retain is not null
            ? new FileStream(retainedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20, true) : null;
        // 渲染与编码是同一条流水线：分别累计等出帧和等吃帧的时间，才能看出瓶颈在哪一端。
        double readSeconds = 0, writeSeconds = 0;
        double opaqueSeconds = 0, boundsSeconds = 0, identicalSeconds = 0, sampleSeconds = 0, retainSeconds = 0, stallSeconds = 0;
        // 回读异步化：两块帧缓冲轮转。读第 n+1 帧与处理第 n 帧（不透明扫描、覆盖范围、抽样、喂编码器）重叠，
        // 渲染器不再被 baker 的逐帧 CPU 扫描堵在管道写上。每帧的处理本身仍按帧序严格串行，落盘字节顺序不变。
        int frameBytes = checked(width * height * 4);
        byte[][] buffers = [new byte[frameBytes], new byte[frameBytes]];
        int thumbnailBytes = checked(storedSampleWidth * sampleHeight * 3);
        byte[][] thumbnails = [new byte[thumbnailBytes], new byte[thumbnailBytes]];
        Task pending = Task.CompletedTask;
        long loopStarted = Stopwatch.GetTimestamp();
        var estimate = new FrameProgressEstimate();
        ulong framesRead = 0;
        try
        {
            for (ulong frame = 0; frame < request.Frames; ++frame)
            {
                if (sparseInput && !IsSampleFrame(request, frame))
                {
                    token.ThrowIfCancellationRequested();
                    continue;
                }
                int slot = (int)(framesRead++ & 1);
                byte[] rgba = buffers[slot];
                long readStarted = Stopwatch.GetTimestamp();
                try { await source.ReadExactlyAsync(rgba, token); }
                catch (EndOfStreamException e) { throw new InvalidDataException($"Renderer ended while reading frame {frame}/{request.Frames}.", e); }
                finally { readSeconds += Stopwatch.GetElapsedTime(readStarted).TotalSeconds; }
                long stallStarted = Stopwatch.GetTimestamp();
                await pending;
                stallSeconds += Stopwatch.GetElapsedTime(stallStarted).TotalSeconds;
                pending = ProcessAndReportAsync(frame, rgba, thumbnails[slot]);
            }
            await pending;
        }
        finally
        {
            // A failed/cancelled read must not leave the previous frame writing to disposed streams.
            try { await pending; } catch { } // Preserve the original read/processing error.
        }

        async Task ProcessAndReportAsync(ulong frame, byte[] rgba, byte[] thumbnail)
        {
            await ProcessFrameAsync(frame, rgba, thumbnail);
            if (frame == 0 || Environment.TickCount64 - lastReport > 500 || frame + 1 == request.Frames)
            {
                TemporaryCaptureFiles.RequireFreeSpace(output);
                progress?.Report(estimate.Update(frame + 1, request.Frames, Stopwatch.GetElapsedTime(loopStarted).TotalSeconds));
                lastReport = Environment.TickCount64;
            }
        }

        async Task ProcessFrameAsync(ulong frame, byte[] rgba, byte[] thumbnail)
        {
            bool sampleThis = samples is not null && IsSampleFrame(request, frame);
            bool first = request.CollectAlphaBounds && firstFrame is null && frame < encoded;
            if (first) firstFrame = rgba.ToArray();
            byte[]? reference = firstFrame;
            await Task.Run(() =>
            {
                if (request.RequireOpaquePixels)
                {
                    long opaqueStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        int alphaIndex = FrameScan.FirstNonOpaqueAlphaIndex(rgba, FrameScan.Threads);
                        if (alphaIndex >= 0)
                            throw new OpaquePixelException(NonOpaqueFrameEvidence(rgba, width, height, frame, alphaIndex));
                    }
                    finally { opaqueSeconds += Stopwatch.GetElapsedTime(opaqueStarted).TotalSeconds; }
                }
                if (frame >= encoded) return;
                if (request.CollectAlphaBounds)
                {
                    if (pixelIdentical && !first && reference is not null)
                    {
                        long identicalStarted = Stopwatch.GetTimestamp();
                        if (!rgba.AsSpan().SequenceEqual(reference)) pixelIdentical = false;
                        identicalSeconds += Stopwatch.GetElapsedTime(identicalStarted).TotalSeconds;
                    }
                    long boundsStarted = Stopwatch.GetTimestamp();
                    bounds.Add(rgba, request.BoundsIncludeRgb);
                    boundsSeconds += Stopwatch.GetElapsedTime(boundsStarted).TotalSeconds;
                }
                if (sampleThis)
                {
                    long sampleStarted = Stopwatch.GetTimestamp();
                    Downsample(rgba, width, height, thumbnail, sampleWidth, sampleHeight, request.FrameSampleIncludeAlpha);
                    sampleSeconds += Stopwatch.GetElapsedTime(sampleStarted).TotalSeconds;
                }
            }, token);
            if (retained is not null && retain!.Contains(frame))
            {
                long retainStarted = Stopwatch.GetTimestamp();
                await retained.WriteAsync(rgba, token);
                retainSeconds += Stopwatch.GetElapsedTime(retainStarted).TotalSeconds;
            }
            if (frame >= encoded) return;
            if (first) await File.WriteAllBytesAsync(firstFramePath!, reference!, token);
            if (sampleThis) { await samples!.WriteAsync(thumbnail, token); ++sampleCount; }
            long writeStarted = Stopwatch.GetTimestamp();
            await destination.WriteAsync(rgba, token);
            writeSeconds += Stopwatch.GetElapsedTime(writeStarted).TotalSeconds;
        }

        double loopSeconds = Stopwatch.GetElapsedTime(loopStarted).TotalSeconds;
        if (await source.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Renderer emitted extra frame data.");
        JsonObject? sampleReport = samples is null ? null : new JsonObject {
            ["path"] = samplePath, ["width"] = storedSampleWidth, ["height"] = sampleHeight, ["format"] = "rgb24",
            ["includes_packed_alpha"] = request.FrameSampleIncludeAlpha, ["logical_width"] = sampleWidth,
            ["phase_frames"] = request.FrameSamplePhaseFrames,
            ["first_output_frame"] = 0, ["stride_frames"] = request.FrameSampleStride, ["count"] = sampleCount,
            ["fps_numerator"] = request.FpsNumerator, ["fps_denominator"] = request.FpsDenominator,
            ["basis"] = "Area-averaged pre-encode frames; simulation advances at the full requested FPS." };
        JsonObject? opaquePixels = request.RequireOpaquePixels ? new JsonObject {
            ["requested"] = true, ["verified"] = true, ["checked_frames"] = request.Frames,
            ["checked_pixels"] = checked((ulong)width * (ulong)height * request.Frames), ["minimum_alpha"] = 255,
            ["basis"] = "Every decoded native RGBA frame was scanned in renderer resolution before encoding."
        } : null;
        if (retained is not null) await retained.FlushAsync(token);
        JsonObject? retainedReport = retain is null ? null : new JsonObject {
            ["path"] = retainedPath, ["format"] = "rgba", ["width"] = width, ["height"] = height,
            ["frame_indices"] = new JsonArray(request.RetainFrames!.Select(index => (JsonNode?)JsonValue.Create(index)).ToArray()),
            ["encoded_frames"] = encoded,
            ["basis"] = "Renderer RGBA frames copied byte for byte before encoding, in frame-index order." };
        return new(request.CollectAlphaBounds ? bounds.ToJson(request.BoundsIncludeRgb, firstFramePath!, pixelIdentical) : null,
            sampleReport, opaquePixels, readSeconds, writeSeconds, retainedReport,
            opaqueSeconds, boundsSeconds, identicalSeconds, sampleSeconds, retainSeconds, loopSeconds, stallSeconds);
    }

    private sealed class FrameBounds
    {
        private readonly int width, height;
        private int minX, minY, maxX = -1, maxY = -1;
        private byte minAlpha = 255, maxAlpha;
        public FrameBounds(int width, int height) { this.width = width; this.height = height; minX = width; minY = height; }
        /// <summary>并入一帧：向量化分块扫描（FrameScan），结果与逐像素扫描逐位相同。</summary>
        public void Add(ReadOnlyMemory<byte> rgba, bool includeRgb)
        {
            var frame = FrameScan.Bounds(rgba, width, height, includeRgb, FrameScan.Threads);
            minAlpha = Math.Min(minAlpha, frame.MinAlpha); maxAlpha = Math.Max(maxAlpha, frame.MaxAlpha);
            minX = Math.Min(minX, frame.MinX); maxX = Math.Max(maxX, frame.MaxX);
            minY = Math.Min(minY, frame.MinY); maxY = Math.Max(maxY, frame.MaxY);
        }
        public JsonObject ToJson(bool includesRgb, string firstFramePath, bool pixelIdentical) => new() {
            ["has_content"] = maxX >= 0, ["x"] = maxX >= 0 ? minX : 0, ["y"] = maxY >= 0 ? minY : 0,
            ["width"] = maxX >= 0 ? maxX - minX + 1 : 0, ["height"] = maxY >= 0 ? maxY - minY + 1 : 0,
            ["minimum_alpha"] = (int)minAlpha, ["maximum_alpha"] = (int)maxAlpha,
            ["includes_rgb"] = includesRgb,
            ["first_frame_rgba_path"] = firstFramePath, ["pixel_identical_in_generated_interval"] = pixelIdentical,
            ["basis"] = includesRgb ? "Union of nonzero coverage OR emitted color over every native frame." : "Union of nonzero alpha over every native frame.",
            ["pixel_identity_scope"] = "Every full-resolution native RGBA frame in this generated interval was compared with the retained first frame; this is not a claim about ungenerated frames or mathematical periodicity." };
    }

    /// <summary>
    /// 面积平均降采样到 <paramref name="sw"/>×<paramref name="sh"/>，四舍五入到整数。<paramref name="packed"/> 时每行宽 2×sw：
    /// 左半 RGB，右半是覆盖度（三通道同值）；否则只出 RGB。
    /// </summary>
    private static void Downsample(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> rgb, int sw, int sh, bool packed)
    {
        int row = packed ? sw * 2 : sw;
        for (int sy = 0; sy < sh; ++sy)
            for (int sx = 0; sx < sw; ++sx)
            {
                int left = sx * width / sw, right = Math.Max(left + 1, (sx + 1) * width / sw);
                int top = sy * height / sh, bottom = Math.Max(top + 1, (sy + 1) * height / sh);
                long r = 0, g = 0, b = 0, a = 0;
                for (int y = top; y < bottom; ++y)
                    for (int x = left; x < right; ++x)
                    {
                        int p = (y * width + x) * 4;
                        r += rgba[p]; g += rgba[p + 1]; b += rgba[p + 2]; a += rgba[p + 3];
                    }
                int n = (right - left) * (bottom - top), pixel = (sy * row + sx) * 3;
                rgb[pixel] = (byte)((r + n / 2) / n); rgb[pixel + 1] = (byte)((g + n / 2) / n); rgb[pixel + 2] = (byte)((b + n / 2) / n);
                if (!packed) continue;
                int alpha = pixel + sw * 3;
                rgb[alpha] = rgb[alpha + 1] = rgb[alpha + 2] = (byte)((a + n / 2) / n);
            }
    }
}
