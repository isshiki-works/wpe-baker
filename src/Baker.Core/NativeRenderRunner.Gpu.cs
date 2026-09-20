using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    internal async Task<JsonObject> GpuPlaybackQualityAsync(JsonObject render, string video, CacheRegion crop,
        bool packed, string output, CancellationToken token)
    {
        var request = render["request"]!.Deserialize<RenderRequest>(JsonOptions)!;
        ulong[] samples = PlaybackQualityGate.SampleFrames(EncodedFrameCount(render));
        int colorWidth = (int)(request.EncodeWidth ?? (uint)crop.Width);
        int height = (int)(request.EncodeHeight ?? (uint)crop.Height);
        int width = colorWidth * (packed ? 2 : 1);
        bool resized = colorWidth != crop.Width || height != crop.Height;
        (ulong[] Indices, byte[] Rgba)? scaled = resized
            ? await EncodedLoopValidator.ScaleRetainedFramesAsync(tools, render, colorWidth, height, crop, token) : null;
        uint fade = request.GpuEncoding?.CrossfadeFrames ?? 0;
        string referencePath = Path.Combine(output, "quality-reference.rgb");
        string productPath = Path.Combine(output, "quality-product.yuv");
        await using (var reference = new FileStream(referencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var product = new FileStream(productPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (FileStream? window = fade > 0 ? File.OpenRead(render["gpu_loop_window"]!["path"]!.GetValue<string>()) : null)
        {
            foreach (ulong index in samples)
            {
                byte[] rgb;
                if (scaled is { } resizedFrames)
                {
                    int position = Array.IndexOf(resizedFrames.Indices, index);
                    if (position < 0) throw new InvalidDataException("GPU quality reference omitted a requested sample.");
                    int frameBytes = checked(colorWidth * height * 4);
                    rgb = LoopClosureCheck.EncodedLayout(resizedFrames.Rgba.AsSpan(position * frameBytes, frameBytes),
                        colorWidth, height, 0, 0, colorWidth, height, packed);
                }
                else
                {
                    byte[] rgba = await LoopClosureCheck.ReadRetainedFrameAsync(render, index, token);
                    if (index < fade)
                    {
                        byte[] wrap = new byte[rgba.Length];
                        window!.Position = checked((long)(fade + index) * rgba.Length);
                        await window.ReadExactlyAsync(wrap, token);
                        for (int i = 0; i < rgba.Length; i++)
                            rgba[i] = (byte)(((ulong)rgba[i] * (index + 1) + (ulong)wrap[i] * (fade - index)) / (fade + 1UL));
                    }
                    rgb = LoopClosureCheck.EncodedLayout(rgba, (int)request.Width, (int)request.Height,
                        crop.X, crop.Y, crop.Width, crop.Height, packed);
                }
                await reference.WriteAsync(rgb, token);
                var range = ExactFrameRange.Build(video, index, 1, request.FpsNumerator, request.FpsDenominator);
                byte[] yuv = await EncodedQualityValidator.RunFfmpegBytesAsync(tools,
                    ["-hide_banner", "-nostdin", "-v", "error", "-threads", "2", .. range.Arguments,
                     "-map", "0:v:0", "-vf", range.Filter, "-fps_mode", "passthrough", "-frames:v", "1",
                     "-an", "-sn", "-dn", "-f", "rawvideo", "-pix_fmt", "yuv420p", "pipe:1"],
                    checked((long)width * height * 3 / 2), token);
                await product.WriteAsync(yuv, token);
            }
        }
        string size = FormattableString.Invariant($"{width}x{height}");
        // Raw YUV has lost the MP4's tags. Restore them before framesync so FFmpeg
        // does not reinterpret the product through a different color matrix/range.
        string graph = $"[1:v]format=gbrp,{PlaybackEncodeProfile.Bt709Filter},split=2[r1][r2];" +
            "[0:v]setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709,split=2[p1][p2];" +
            "[p1][r1]ssim[ss];[p2][r2]psnr[ps]";
        string log = Path.Combine(output, "quality.stderr.log");
        await RunTextAsync(tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-nostats", "-f", "rawvideo", "-pixel_format", "yuv420p", "-video_size", size,
             "-framerate", "1", "-i", productPath, "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", size,
             "-framerate", "1", "-i", referencePath, "-filter_complex_threads", "1", "-filter_complex", graph,
             "-map", "[ss]", "-map", "[ps]", "-an", "-c:v", "rawvideo", "-f", "null", "-"], log, token);
        string metrics = await File.ReadAllTextAsync(log, token);
        double? ssim = PlaybackQualityGate.ParseSsim(metrics), psnr = PlaybackQualityGate.ParsePsnr(metrics);
        bool pass = PlaybackQualityGate.Passes(ssim, PlaybackQualityGate.DefaultReferenceSsim, PlaybackQualityGate.DefaultRatio);
        var result = PlaybackQualityGate.Summarize(samples, PlaybackQualityGate.DefaultReferenceSsim, PlaybackQualityGate.DefaultRatio,
            ssim, psnr, 0, pass ? PlaybackQualityGate.ActionAccepted : PlaybackQualityGate.ActionRejected);
        result["reference_source"] = resized
            ? "Retained original renderer RGBA resized with FFmpeg Lanczos, then alpha-packed and converted to BT.709."
            : "Retained renderer RGBA with the same integer loop crossfade, crop, packing and BT.709 conversion.";
        result["decode_scope"] = "selected_frame_windows";
        result["qp"] = request.GpuEncoding!.Qp;
        if (pass) TemporaryCaptureFiles.Delete(result, output, "quality-reference.rgb", "quality-product.yuv");
        return result;
    }

    internal static (CacheRegion Crop, bool Packed)? SamplingCrop(JsonObject? coverage, RenderRequest request)
    {
        if (coverage?["status"]?.GetValue<string>() != "complete" || coverage["has_content"]?.GetValue<bool>() != true ||
            coverage["includes_rgb"]?.GetValue<bool>() != true || coverage["render_request"] is not JsonObject observed)
            return null;
        ulong first=coverage["first_simulation_frame"]!.GetValue<ulong>(), count=coverage["frames"]!.GetValue<ulong>();
        if (request.WarmupFrames<first || (UInt128)request.WarmupFrames+request.Frames>(UInt128)first+count) return null;
        JsonObject target=JsonSerializer.SerializeToNode(request,JsonOptions)!.AsObject();
        // Compare the simulation inputs, not sampling/encoding/output settings.
        foreach (string key in new[] {"source","assets","width","height","fps_numerator","fps_denominator","seed",
            "device_uuid","capture_target","input","input_timeline","user_properties","orthographic_capture_viewport",
            "layer_selection","offline_video_rate_overrides","effect_render_scale","match_effect_resolution"})
            if (!JsonNode.DeepEquals(observed[key],target[key])) return null;
        bool packed=coverage["minimum_alpha"]!.GetValue<int>()!=255;
        var basis=new JsonObject { ["request"]=target, ["alpha_bounds"]=coverage.DeepClone() };
        CacheRegion region=CacheRegion.FromAlphaBounds(basis);
        return (HardwareDecodeDimensions.GrowRegion(region,packed,request.FpsNumerator,request.FpsDenominator),packed);
    }
}
