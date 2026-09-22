using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>补边画布里的内容矩形：<paramref name="PaddedWidth"/> 是每半幅的画布宽，偏移与尺寸都按每半幅计。</summary>
public sealed record EncodedContentRegion(int PaddedWidth, int PaddedHeight, int OffsetX, int OffsetY, int Width, int Height)
{
    /// <summary>从每半幅的补边画布里取出内容，左右半幅按原顺序拼回 Width×halves 宽的帧。</summary>
    public byte[] Extract(ReadOnlySpan<byte> frame, int halves)
    {
        if (halves is not (1 or 2) || OffsetX < 0 || OffsetY < 0 || Width <= 0 || Height <= 0 ||
            OffsetX + Width > PaddedWidth || OffsetY + Height > PaddedHeight || frame.Length != checked(PaddedWidth * halves * PaddedHeight * 3))
            throw new InvalidDataException("Encoded content region does not fit the decoded padded frame.");
        byte[] content = new byte[checked(Width * halves * Height * 3)];
        for (int row = 0; row < Height; ++row)
            for (int half = 0; half < halves; ++half)
                frame.Slice(((OffsetY + row) * PaddedWidth * halves + half * PaddedWidth + OffsetX) * 3, Width * 3)
                    .CopyTo(content.AsSpan((row * Width * halves + half * Width) * 3));
        return content;
    }
}

/// <summary>
/// 原作参照帧，已排成与成品解码帧相同的测量布局（RGB24；透明组左 RGB 右 alpha）：
/// <paramref name="First"/> = m[0]（编码器第 0 帧输入），<paramref name="Last"/> = m[P−1]，<paramref name="Wrap"/> = f[P]
/// （渲染器连续播放到第 P 帧的原帧）。<paramref name="Closure"/> 是 <see cref="LoopClosureCheck"/> 的记录。
/// </summary>
public sealed record LoopReference(byte[] First, byte[] Last, byte[] Wrap, int Width, int Height, uint CrossfadeFrames,
    JsonObject Closure, string FrameSource);

/// <summary>
/// 编码后接缝校验，以原作为参照。观众在循环点看到的一步是 enc[0] − enc[P−1]，原作连续播放到同一相位的一步是 f[P] − f[P−1]。
/// 两条路线都有 m[P−1] = f[P−1]，所以逐像素恒有
/// <c>(enc[0] − enc[P−1]) − (f[P] − m[P−1]) = (m[0] − f[P]) + (e[0] − e[P−1])</c>，e 为编码误差。
/// 右边第一项在源周期路线由 <see cref="LoopClosureCheck"/> 在编码前判定，残差路线由残差层判定；第二项是编码噪声，
/// 与片内每个 GOP 边界同结构，只在接缝这一对上顺带算出（encoding_noise_seam），不裁决。
/// 这里裁决的只有：闭合（传入）、帧数、帧率。
/// </summary>
public static class EncodedLoopValidator
{
    private static readonly int[] TileSizes = [LoopClosureCheck.TileSize, LoopClosureCheck.RecordTileSize];

    /// <summary>
    /// 分组路线的参照：m[0]、m[P−1] 从无损 master 按帧号精确取出并裁成成品的裁剪区；f[P] 是主渲染留下的第 P 帧原帧。
    /// 残差路线的 master 已经淡化过，m[0] 就是淡化后的第 0 帧，f[P] 仍是淡化前的原帧。
    /// </summary>
    public static async Task<LoopReference> FromLosslessMasterAsync(NativeTools tools, string masterDirectory, CacheRegion crop,
        bool packedAlpha, ulong loopFrames, uint crossfadeFrames, JsonObject closure, byte[] wrapRgba,
        CancellationToken cancellationToken = default)
    {
        string master = Path.GetFullPath(masterDirectory);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(master, "manifest.json"), cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("master 清单无效。");
        bool masterPacked = manifest["pixel_packing"]?.GetValue<string>() == "rgba_side_by_side";
        if (manifest["status"]?.GetValue<string>() != "completed" || manifest["lossless_test_encoding"]?.GetValue<bool>() != true ||
            (packedAlpha && !masterPacked))
            throw new InvalidDataException("接缝参照需要完成的无损 master；透明成品需要左右并排的 RGB/alpha master。");
        JsonObject request = manifest["request"]!.AsObject();
        int captureWidth = request["width"]!.GetValue<int>(), captureHeight = request["height"]!.GetValue<int>();
        uint numerator = request["fps_numerator"]!.GetValue<uint>(), denominator = request["fps_denominator"]!.GetValue<uint>();
        crop.Validate();
        if (crop.CaptureWidth != captureWidth || crop.CaptureHeight != captureHeight || loopFrames < 2 ||
            NativeRenderRunner.EncodedFrameCount(manifest) != loopFrames)
            throw new InvalidDataException("接缝参照的裁剪区或周期与 master 不一致。");
        string filter = packedAlpha
            ? $"split=2[r][a];[r]crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}[rgb];" +
              $"[a]crop={crop.Width}:{crop.Height}:{crop.CaptureWidth + crop.X}:{crop.Y}[alpha];[rgb][alpha]hstack=inputs=2"
            : $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}";
        int width = crop.Width * (packedAlpha ? 2 : 1);
        string video = Path.Combine(master, "preview.mp4");
        byte[] first = (await EncodedQualityValidator.DecodeExactFramesAsync(video, tools, 0, 1, width, crop.Height,
            numerator, denominator, filter, cancellationToken))[0];
        byte[] last = (await EncodedQualityValidator.DecodeExactFramesAsync(video, tools, loopFrames - 1, 1, width, crop.Height,
            numerator, denominator, filter, cancellationToken))[0];
        byte[] wrap = LoopClosureCheck.EncodedLayout(wrapRgba, captureWidth, captureHeight, crop.X, crop.Y, crop.Width, crop.Height, packedAlpha);
        return new(first, last, wrap, width, crop.Height, crossfadeFrames, closure,
            "m[0], m[P-1]: lossless master decoded by exact frame index and cropped like the playback encode; f[P]: renderer RGBA frame P cropped the same way.");
    }

    /// <summary>
    /// 直编路线的参照：这条路线在渲染那一遍就把成品编好了，没有无损 master 可解，所以 m[0]、m[P−1] 直接取主渲染留下的
    /// 原帧——它们就是编码器第 0、P−1 帧的输入。master 路线是从 crf 0 的无损 master 按帧号解出同两帧，两者逐字节相同，
    /// 所以判据读数与 master 路线一致；裁剪区是不透明组的先验整幅，不缩放。
    /// </summary>
    public static async Task<LoopReference> FromRetainedFramesAsync(JsonObject renderManifest, CacheRegion crop, bool packedAlpha,
        ulong loopFrames, JsonObject closure, byte[] wrapRgba, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderManifest);
        ArgumentNullException.ThrowIfNull(wrapRgba);
        bool gpu = renderManifest["native_frame_transport"]?.GetValue<string>() == "gpu_nv12";
        string packing = renderManifest["pixel_packing"]?.GetValue<string>() ?? "";
        if (renderManifest["status"]?.GetValue<string>() != "completed" || renderManifest["lossless_test_encoding"]?.GetValue<bool>() != false ||
            packing != (packedAlpha ? "rgba_side_by_side" : "rgb") || (packedAlpha && !gpu))
            throw new InvalidDataException("Direct loop references require a completed render with matching pixel packing.");
        JsonObject request = renderManifest["request"]!.AsObject();
        int captureWidth = request["width"]!.GetValue<int>(), captureHeight = request["height"]!.GetValue<int>();
        crop.Validate();
        if (crop.CaptureWidth != captureWidth || crop.CaptureHeight != captureHeight || loopFrames < 2 ||
            NativeRenderRunner.EncodedFrameCount(renderManifest) != loopFrames ||
            wrapRgba.Length != checked(captureWidth * captureHeight * 4))
            throw new InvalidDataException("直编接缝参照的裁剪区或周期与这次渲染不一致。");
        byte[] Layout(ReadOnlySpan<byte> rgba) => LoopClosureCheck.EncodedLayout(rgba, captureWidth, captureHeight,
            crop.X, crop.Y, crop.Width, crop.Height, packedAlpha);
        byte[] firstRgba = await LoopClosureCheck.ReadRetainedFrameAsync(renderManifest, 0, cancellationToken);
        uint crossfade = gpu ? renderManifest["loop_crossfade"]?["crossfade_frames"]?.GetValue<uint>() ?? 0 : 0;
        if (crossfade > 0)
        {
            if (crossfade >= loopFrames || renderManifest["loop_crossfade"]?["status"]?.GetValue<string>() != "applied")
                throw new InvalidDataException("GPU loop crossfade reference is invalid.");
            for (int i = 0; i < firstRgba.Length; i++)
                firstRgba[i] = (byte)(((ulong)firstRgba[i] + (ulong)crossfade * wrapRgba[i]) / (crossfade + 1UL));
        }
        byte[] first = Layout(firstRgba);
        byte[] last = Layout(await LoopClosureCheck.ReadRetainedFrameAsync(renderManifest, loopFrames - 1, cancellationToken));
        return new(first, last, Layout(wrapRgba), crop.Width * (packedAlpha ? 2 : 1), crop.Height, crossfade, closure,
            crossfade > 0
                ? "m[0] reconstructed with the GPU integer crossfade from retained f[0] and f[P]; m[P-1] and f[P] retained before encoding; all cropped like playback."
                : "m[0], m[P-1], f[P]: renderer RGBA frames retained before encoding, cropped like the playback encode; no lossless master was written.");
    }

    /// <summary>
    /// 特效前缀路线的参照：这条路线直接编码、没有无损 master，所以主渲染留下第 0、P−1、P 帧原帧，
    /// 再按编码器同一条缩放链（lanczos 缩到编码尺寸）排成成品的内容布局。
    /// </summary>
    public static async Task<LoopReference> FromScaledRenderAsync(NativeTools tools, JsonObject renderManifest, int encodeWidth,
        int encodeHeight, bool packedAlpha, ulong loopFrames, JsonObject closure, CancellationToken cancellationToken = default)
    {
        (ulong[] indices, byte[] scaled) = await ScaleRetainedFramesAsync(tools, renderManifest, encodeWidth, encodeHeight,
            null, cancellationToken);
        if (loopFrames < 2 || !LoopClosureCheck.ReferenceFrameIndices(loopFrames).All(indices.Contains))
            throw new InvalidDataException("留下的原帧没有覆盖第 0、P−1、P 帧。");
        int scaledBytes = checked(encodeWidth * encodeHeight * 4);
        byte[] Layout(ulong index)
        {
            int position = Array.IndexOf(indices, index);
            return LoopClosureCheck.EncodedLayout(scaled.AsSpan(position * scaledBytes, scaledBytes), encodeWidth, encodeHeight,
                0, 0, encodeWidth, encodeHeight, packedAlpha);
        }
        return new(Layout(0), Layout(loopFrames - 1), Layout(loopFrames), encodeWidth * (packedAlpha ? 2 : 1), encodeHeight, 0, closure,
            "m[0], m[P-1], f[P]: renderer RGBA frames scaled with the encoder's own lanczos step to the encoded content size.");
    }

    internal static async Task<(ulong[] Indices, byte[] Rgba)> ScaleRetainedFramesAsync(NativeTools tools,
        JsonObject renderManifest, int encodeWidth, int encodeHeight, CacheRegion? crop, CancellationToken cancellationToken)
    {
        JsonObject retained = renderManifest["retained_frames"] as JsonObject
            ?? throw new InvalidDataException("渲染清单没有 retained_frames。");
        string path = retained["path"]!.GetValue<string>();
        int width = retained["width"]!.GetValue<int>(), height = retained["height"]!.GetValue<int>();
        ulong[] indices = retained["frame_indices"]!.AsArray().Select(node => node!.GetValue<ulong>()).ToArray();
        if (indices.Length == 0 || encodeWidth <= 0 || encodeHeight <= 0)
            throw new InvalidDataException("Retained frame indices and positive scaled dimensions are required.");
        int scaledBytes = checked(encodeWidth * encodeHeight * 4);
        string cropFilter = crop is null ? "" : $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y},";
        byte[] scaled = await EncodedQualityValidator.RunFfmpegBytesAsync(tools, ["-hide_banner", "-nostdin", "-v", "error",
            "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", $"{width}x{height}", "-framerate", "1", "-i", path,
            "-vf", $"{cropFilter}scale={encodeWidth}:{encodeHeight}:flags=lanczos,format=rgba", "-frames:v", indices.Length.ToString(CultureInfo.InvariantCulture),
            "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"], checked((long)scaledBytes * indices.Length), cancellationToken);
        return (indices, scaled);
    }

    public static Task<JsonObject> ValidateAsync(string videoFile, NativeTools tools, ulong loopFrames, uint fpsNumerator,
        uint fpsDenominator, bool packedAlpha, LoopReference reference, EncodedContentRegion? content = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ValidateAsync(videoFile, tools, loopFrames, fpsNumerator, fpsDenominator, packedAlpha,
            reference.Closure, reference.Width, reference.Height, content, cancellationToken, reference);
    }

    // The verdict needs closure and stream metadata. Original/decoded pixel
    // comparisons are optional diagnostics, requested by the overload above.
    public static async Task<JsonObject> ValidateAsync(string videoFile, NativeTools tools, ulong loopFrames, uint fpsNumerator,
        uint fpsDenominator, bool packedAlpha, JsonObject closure, int referenceWidth, int referenceHeight,
        EncodedContentRegion? content = null, CancellationToken cancellationToken = default, LoopReference? reference = null)
    {
        ArgumentNullException.ThrowIfNull(closure);
        if (!File.Exists(videoFile)) throw new FileNotFoundException("Encoded video is missing.", videoFile);
        foreach (string executable in new[] { tools.Ffmpeg, tools.Ffprobe }) if (!File.Exists(executable)) throw new FileNotFoundException("Required native tool is missing.", executable);
        if (loopFrames < 2 || fpsNumerator == 0 || fpsDenominator == 0) throw new ArgumentException("Loop frames and FPS must be positive.");
        JsonObject stream = await EncodedQualityValidator.ProbeStreamAsync(videoFile, tools, cancellationToken);
        int width = RequiredInt(stream, "width"), height = RequiredInt(stream, "height");
        (uint actualNum, uint actualDen, string actualFps) = ReadFps(stream);
        if (packedAlpha && width % 2 != 0) throw new InvalidDataException("Packed alpha requires an even encoded width.");
        (ulong decoded, string frameCountSource, string? frameCountFallbackReason) =
            await FrameCountAsync(videoFile, tools, stream, loopFrames, cancellationToken);
        bool framesMatch = decoded == loopFrames;
        bool fpsMatch = SameRate(actualNum, actualDen, fpsNumerator, fpsDenominator);
        bool closed = LoopClosureCheck.Allows(closure);
        ulong last = Math.Min(decoded, loopFrames);
        if (last < 2) throw new InvalidDataException("Encoded video has fewer than two decodable frames.");
        int halves = packedAlpha ? 2 : 1;
        if (content is not null && (width != checked(content.PaddedWidth * halves) || height != content.PaddedHeight))
            throw new InvalidDataException("Encoded video dimensions differ from the declared padded canvas.");
        int measuredWidth = content is null ? width : content.Width * halves, measuredHeight = content?.Height ?? height;
        if (referenceWidth != measuredWidth || referenceHeight != measuredHeight)
            throw new InvalidDataException("接缝参照布局与成品不一致。");
        JsonObject? rgb = null, alpha = null;
        if (reference is not null)
        {
            byte[] Measure(byte[] frame) => content is null ? frame : content.Extract(frame, halves);
            List<byte[]> head = await EncodedQualityValidator.DecodeExactFramesAsync(videoFile, tools, 0, 2, width, height,
                actualNum, actualDen, null, cancellationToken);
            List<byte[]> tail = await EncodedQualityValidator.DecodeExactFramesAsync(videoFile, tools, last - 2, 2, width, height,
                actualNum, actualDen, null, cancellationToken);
            byte[] enc0 = Measure(head[0]), enc1 = Measure(head[1]), encBefore = Measure(tail[0]), encLast = Measure(tail[1]);
            if (new[] { reference.First, reference.Last, reference.Wrap }.Any(frame => frame.Length != enc0.Length))
                throw new InvalidDataException("接缝参照帧与成品的测量布局不一致。");

            int planeWidth = measuredWidth / halves;
            byte[] rgbMask = packedAlpha
                ? LoopClosureCheck.AlphaMask([Half(reference.First, planeWidth, measuredHeight, 1), Half(reference.Last, planeWidth, measuredHeight, 1),
                    Half(reference.Wrap, planeWidth, measuredHeight, 1)], 3, 0)
                : [];
            JsonObject Plane(int half, byte[] mask) => SeamPlane(
                Half(enc0, planeWidth, measuredHeight, half, halves), Half(enc1, planeWidth, measuredHeight, half, halves),
                Half(encBefore, planeWidth, measuredHeight, half, halves), Half(encLast, planeWidth, measuredHeight, half, halves),
                Half(reference.First, planeWidth, measuredHeight, half, halves), Half(reference.Last, planeWidth, measuredHeight, half, halves),
                Half(reference.Wrap, planeWidth, measuredHeight, half, halves), planeWidth, measuredHeight, mask);
            rgb = Plane(0, rgbMask);
            alpha = packedAlpha ? Plane(1, []) : null;
        }

        var failures = new JsonArray();
        if (!closed) failures.Add("loop_not_closed");
        if (!framesMatch) failures.Add("frame_count_mismatch");
        if (!fpsMatch) failures.Add("frame_rate_mismatch");
        bool pass = failures.Count == 0;
        var result = new JsonObject
        {
            ["schema_version"] = 3,
            ["status"] = pass ? "observed_seam_pass" : "observed_seam_fail",
            ["criterion"] = "reference_seam",
            ["failures"] = failures,
            ["scope"] = "Verdict: loop closure (judged before encoding on renderer frames), frame count (container header, confirmed by decoding whenever it disagrees) and frame rate. Original/encoded seam differences and encoding noise are optional diagnostics and do not affect this verdict.",
            ["automatic_visual_certification"] = false,
            ["video_file"] = Path.GetFullPath(videoFile),
            ["packed_alpha"] = packedAlpha,
            ["actual"] = new JsonObject
            {
                ["width"] = width, ["height"] = height, ["decoded_frame_count"] = decoded,
                ["frame_count_source"] = frameCountSource,
                ["frame_count_fallback_reason"] = frameCountFallbackReason,
                ["container_declared_frame_count"] = OptionalString(stream, "nb_frames"),
                ["frame_rate"] = actualFps, ["fps_numerator"] = actualNum, ["fps_denominator"] = actualDen
            },
            ["expected"] = new JsonObject
            {
                ["frames"] = loopFrames, ["fps_numerator"] = fpsNumerator, ["fps_denominator"] = fpsDenominator,
                ["frame_count_matches"] = framesMatch, ["frame_rate_matches"] = fpsMatch
            },
            ["loop_closure"] = closure.DeepClone(),
            ["reference_seam"] = reference is null ? new JsonObject { ["status"] = "not_requested" } : new JsonObject
            {
                ["loop_frames"] = loopFrames,
                ["crossfade_frames"] = reference.CrossfadeFrames,
                ["encoded_frames_decoded"] = new JsonArray(0UL, 1UL, last - 2, last - 1),
                ["frame_source"] = reference.FrameSource,
                ["measured_width"] = measuredWidth,
                ["measured_height"] = measuredHeight,
                ["rgb"] = rgb,
                ["alpha"] = alpha,
                ["rgb_pixel_scope"] = packedAlpha
                    ? "RGB counted only where m[0], m[P-1] or f[P] has alpha > 0; alpha = 0 leaves RGB undefined. Decoded alpha is lossy and is not used for the mask."
                    : "Every RGB pixel.",
                ["terms"] = new JsonObject
                {
                    ["difference_map"] = "M((enc[0]-enc[P-1]) - (f[P]-m[P-1])): the seam the viewer sees against the original's step at the same phase",
                    ["reference_step"] = "M(f[P]-m[P-1]): the original's own step (a source clip's own cut shows up here)",
                    ["encoded_seam_step"] = "M(enc[0]-enc[P-1])",
                    ["wrap_term"] = "M(m[0]-f[P]): 0 when closed; |Δ0|/(C+1) after the residual crossfade",
                    ["encoding_error_first"] = "M(enc[0]-m[0])",
                    ["encoding_error_last"] = "M(enc[P-1]-m[P-1])",
                    ["encoding_noise_seam"] = "M((enc[0]-m[0]) - (enc[P-1]-m[P-1])): the encoding noise inside the seam step",
                    ["bound_excess_maximum"] = "max over tiles of difference_map - (wrap_term + encoding_error_first + encoding_error_last); never above 0 (triangle inequality)"
                }
            }
        };
        if (content is not null)
            result["content_region"] = new JsonObject {
                ["padded_width"] = content.PaddedWidth, ["padded_height"] = content.PaddedHeight,
                ["offset_x"] = content.OffsetX, ["offset_y"] = content.OffsetY,
                ["width"] = content.Width, ["height"] = content.Height,
                ["measured_width"] = measuredWidth, ["measured_height"] = measuredHeight };
        return result;
    }

    /// <summary>
    /// 本工具成功写出的成品先核容器帧数；缺失或不一致时才解码确认，并保留实际计数来源。
    /// </summary>
    internal static async Task<(ulong Count, string Source, string? FallbackReason)> FrameCountAsync(string videoFile,
        NativeTools tools, JsonObject stream, ulong loopFrames, CancellationToken token)
    {
        if (!EncodedQualityValidator.TryContainerFrameCount(stream, out ulong declared))
            return (await EncodedQualityValidator.CountFramesAsync(videoFile, tools, token), "full_decode",
                "The container header did not declare a positive nb_frames, so the frames were counted by decoding.");
        if (declared == loopFrames) return (declared, "container_header", null);
        return (await EncodedQualityValidator.CountFramesAsync(videoFile, tools, token), "full_decode",
            $"The container header declared {declared.ToString(CultureInfo.InvariantCulture)} frames instead of the expected " +
            $"{loopFrames.ToString(CultureInfo.InvariantCulture)}, so the frames were counted by decoding before judging.");
    }

    /// <summary>拒绝理由里的一段：点名是闭合、帧数还是帧率没过，带上读数。</summary>
    public static string RejectionDetail(JsonObject seam, string language)
    {
        ArgumentNullException.ThrowIfNull(seam);
        var parts = new List<string>();
        string[] failures = (seam["failures"] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? "").ToArray();
        if (seam["loop_closure"] is JsonObject closure && closure["status"]?.GetValue<string>() == LoopClosureCheck.NotClosedStatus)
        {
            JsonNode? rgb = closure["rgb"]?["tile_64"];
            parts.Add(MessageCatalog.Get("bake.loop_not_closed", language, closure["loop_frames"]?.ToJsonString() ?? "?",
                LoopClosureCheck.TileSize, rgb?["worst_x"]?.ToJsonString() ?? "?", rgb?["worst_y"]?.ToJsonString() ?? "?",
                Number(rgb?["worst"]),
                closure["alpha"] is JsonObject alpha ? MessageCatalog.Get("bake.loop_not_closed_alpha", language, Number(alpha["tile_64"]?["worst"])) : "",
                LoopClosureCheck.MaximumTileMae255.ToString("0.0", CultureInfo.InvariantCulture)));
        }
        if (failures.Contains("frame_count_mismatch"))
            parts.Add(MessageCatalog.Get("bake.encoded_frame_count_mismatch", language,
                seam["actual"]?["decoded_frame_count"]?.ToJsonString() ?? "?", seam["expected"]?["frames"]?.ToJsonString() ?? "?"));
        if (failures.Contains("frame_rate_mismatch"))
            parts.Add(MessageCatalog.Get("bake.encoded_frame_rate_mismatch", language, seam["actual"]?["frame_rate"]?.GetValue<string>() ?? "?",
                $"{seam["expected"]?["fps_numerator"]}/{seam["expected"]?["fps_denominator"]}"));
        return string.Join(MessageCatalog.NormalizeLanguage(language) == MessageCatalog.Chinese ? "" : " ", parts);
    }

    private static JsonObject SeamPlane(byte[] enc0, byte[] enc1, byte[] encBefore, byte[] encLast, byte[] first, byte[] last,
        byte[] wrap, int width, int height, byte[] mask)
    {
        var plane = new JsonObject();
        foreach (int tile in TileSizes)
        {
            double[] difference = LoopSeamMetrics.DifferenceMap(enc0, encLast, wrap, last, width, height, tile, mask);
            double[] wrapTerm = LoopSeamMetrics.TileMae(first, wrap, width, height, tile, mask);
            double[] errorFirst = LoopSeamMetrics.TileMae(enc0, first, width, height, tile, mask);
            double[] errorLast = LoopSeamMetrics.TileMae(encLast, last, width, height, tile, mask);
            double excess = double.NegativeInfinity;
            for (int i = 0; i < difference.Length; ++i) excess = Math.Max(excess, difference[i] - wrapTerm[i] - errorFirst[i] - errorLast[i]);
            JsonObject Summary(double[] map) => LoopClosureCheck.Json(LoopSeamMetrics.Summarize(map, width, height, tile));
            plane[$"tile_{tile}"] = new JsonObject
            {
                ["difference_map"] = Summary(difference),
                ["reference_step"] = Summary(LoopSeamMetrics.TileMae(wrap, last, width, height, tile, mask)),
                ["encoded_seam_step"] = Summary(LoopSeamMetrics.TileMae(enc0, encLast, width, height, tile, mask)),
                ["wrap_term"] = Summary(wrapTerm),
                ["encoding_error_first"] = Summary(errorFirst),
                ["encoding_error_last"] = Summary(errorLast),
                ["encoding_noise_seam"] = Summary(LoopSeamMetrics.DifferenceMap(enc0, first, encLast, last, width, height, tile, mask)),
                ["encoded_step_after_seam"] = Summary(LoopSeamMetrics.TileMae(enc1, enc0, width, height, tile, mask)),
                ["encoded_step_before_seam"] = Summary(LoopSeamMetrics.TileMae(encLast, encBefore, width, height, tile, mask)),
                ["bound_excess_maximum"] = Math.Round(excess, 6)
            };
        }
        return plane;
    }

    /// <summary>取测量布局里的一个半幅（0 = RGB，1 = alpha），不透明时直接返回整帧。</summary>
    private static byte[] Half(byte[] frame, int planeWidth, int height, int half, int halves = 2)
    {
        if (halves == 1) return frame;
        byte[] plane = new byte[checked(planeWidth * height * 3)];
        for (int row = 0; row < height; ++row)
            frame.AsSpan((row * planeWidth * 2 + half * planeWidth) * 3, planeWidth * 3).CopyTo(plane.AsSpan(row * planeWidth * 3));
        return plane;
    }

    private static string Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number.ToString("0.####", CultureInfo.InvariantCulture) : "?";

    private static int RequiredInt(JsonObject objectValue, string key) => objectValue[key]?.GetValue<int>() is int value && value > 0
        ? value : throw new InvalidDataException($"ffprobe did not report a positive {key}.");

    private static (uint Numerator, uint Denominator, string Text) ReadFps(JsonObject stream)
    {
        string text = OptionalString(stream, "avg_frame_rate") ?? OptionalString(stream, "r_frame_rate") ?? throw new InvalidDataException("ffprobe did not report frame rate.");
        string[] terms = text.Split('/');
        if (terms.Length != 2 || !uint.TryParse(terms[0], out uint numerator) || !uint.TryParse(terms[1], out uint denominator) || numerator == 0 || denominator == 0)
            throw new InvalidDataException("ffprobe returned an invalid rational frame rate.");
        uint divisor = Gcd(numerator, denominator); return (numerator / divisor, denominator / divisor, text);
    }

    private static uint Gcd(uint a, uint b) { while (b != 0) { uint remainder = a % b; a = b; b = remainder; } return a; }
    private static bool SameRate(uint firstNum, uint firstDen, uint secondNum, uint secondDen)
    {
        uint divisor = Gcd(secondNum, secondDen);
        return firstNum == secondNum / divisor && firstDen == secondDen / divisor;
    }
    private static string? OptionalString(JsonObject objectValue, string key) => objectValue[key]?.GetValue<string>();
}
