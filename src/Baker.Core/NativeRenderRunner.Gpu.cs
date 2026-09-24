using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    /// <summary>画质门的比对后端；R10 在这里换成渲染器内比对。</summary>
    private readonly FfmpegQualityComparer qualityComparer = new FfmpegQualityComparer(new FfmpegTool(tools));

    /// <summary>
    /// GPU 直编成品的画质门：没有无损 master，参照取渲染时留下的原帧（按同一套整数交叉淡化、裁剪、透明打包复原；
    /// 编码时缩放过的用 FFmpeg Lanczos 缩到编码尺寸），与成品在抽样帧上比。不达标直接拒绝，不升档。
    /// </summary>
    internal async Task<JsonObject> GpuPlaybackQualityAsync(JsonObject render, string video, CacheRegion crop,
        bool packed, string output, CancellationToken token)
    {
        var request = render["request"]!.Deserialize<RenderRequest>(JsonOptions)!;
        ulong frames = EncodedFrameCount(render);
        ulong[] samples = QualityGate.SampleFrames(frames);
        int colorWidth = (int)(request.EncodeWidth ?? (uint)crop.Width);
        int height = (int)(request.EncodeHeight ?? (uint)crop.Height);
        int width = colorWidth * (packed ? 2 : 1);
        bool resized = colorWidth != crop.Width || height != crop.Height;
        uint fade = request.GpuEncoding?.CrossfadeFrames ?? 0;
        async Task WriteReferenceAsync(Stream reference, CancellationToken cancel)
        {
            (ulong[] Indices, byte[] Rgba)? scaled = resized
                ? await EncodedLoopValidator.ScaleRetainedFramesAsync(tools, render, colorWidth, height, crop, cancel) : null;
            await using FileStream? window = fade > 0 ? File.OpenRead(render["gpu_loop_window"]!["path"]!.GetValue<string>()) : null;
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
                    byte[] rgba = await LoopClosureCheck.ReadRetainedFrameAsync(render, index, cancel);
                    if (index < fade)
                    {
                        byte[] wrap = new byte[rgba.Length];
                        window!.Position = checked((long)(fade + index) * rgba.Length);
                        await window.ReadExactlyAsync(wrap, cancel);
                        for (int i = 0; i < rgba.Length; i++)
                            rgba[i] = (byte)(((ulong)rgba[i] * (index + 1) + (ulong)wrap[i] * (fade - index)) / (fade + 1UL));
                    }
                    rgb = LoopClosureCheck.EncodedLayout(rgba, (int)request.Width, (int)request.Height,
                        crop.X, crop.Y, crop.Width, crop.Height, packed);
                }
                await reference.WriteAsync(rgb, cancel);
            }
        }
        QualityReport measured = await qualityComparer.CompareAsync(new(video, frames, samples, request.FpsNumerator, request.FpsDenominator,
            new FramesQualityReference(WriteReferenceAsync, width, height), output, "quality"), token);
        bool pass = QualityGate.Passes(measured.Ssim, QualityGate.DefaultReferenceSsim, QualityGate.DefaultRatio);
        var result = QualityGate.Summarize(samples, QualityGate.DefaultReferenceSsim, QualityGate.DefaultRatio,
            measured.Ssim, measured.Psnr, 0, pass ? QualityGate.ActionAccepted : QualityGate.ActionRejected);
        result["reference_source"] = resized
            ? "Retained original renderer RGBA resized with FFmpeg Lanczos, then alpha-packed and converted to BT.709."
            : "Retained renderer RGBA with the same integer loop crossfade, crop, packing and BT.709 conversion.";
        result["decode_scope"] = measured.DecodeScope;
        result["qp"] = request.GpuEncoding?.Qp;
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
            "layer_selection","offline_video_rate_overrides","effect_render_scale","match_effect_resolution","hdr_scale"})
            if (!JsonNode.DeepEquals(observed[key],target[key])) return null;
        bool packed=coverage["minimum_alpha"]!.GetValue<int>()!=255;
        var basis=new JsonObject { ["request"]=target, ["alpha_bounds"]=coverage.DeepClone() };
        CacheRegion region=CacheRegion.FromAlphaBounds(basis);
        return (HardwareDecodeDimensions.GrowRegion(region,packed,request.FpsNumerator,request.FpsDenominator),packed);
    }
}
