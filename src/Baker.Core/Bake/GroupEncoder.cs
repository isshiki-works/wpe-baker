using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 组成品的三条路线：整段逐字节不变的组只存一张裁好的 RGBA；直编组接管渲染那一遍编好的成品；
/// 其余从无损 master 裁切重编。成品编码是整案的 CPU 峰值，按跨进程槽位配额（<see cref="EncodeSlots"/>）排队。
/// </summary>
internal sealed class GroupEncoder(NativeRenderRunner runner, HybridBakeRequest request, string playbackKind,
    string? playbackFallbackReason, IProgress<RenderProgress>? progress, StageTiming timing)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // 多槽并行跑批时按配额排队，渲染不受限制。
    private readonly EncodeSlots encodeSlots = EncodeSlots.Create(request.EncodeSlots);

    /// <summary>返回编码记录（至少含 crop）、裁剪区与成品路径（静态组是 static.rgba）。</summary>
    internal async Task<(JsonObject Encoded, CacheRegion Crop, string Video)> EncodeAsync(JsonObject master, string masterPath, string work,
        GroupCapture capture, bool isStatic, bool directPlayback, bool gpuDirect, bool packedAlpha, CancellationToken cancellationToken)
    {
        if (isStatic)
        {
            var bounds = master["alpha_bounds"]!;
            var region = new CacheRegion((int)capture.PixelWidth, (int)capture.PixelHeight, bounds["x"]!.GetValue<int>(), bounds["y"]!.GetValue<int>(),
                bounds["width"]!.GetValue<int>(), bounds["height"]!.GetValue<int>());
            byte[] first = await File.ReadAllBytesAsync(bounds["first_frame_rgba_path"]!.GetValue<string>(), cancellationToken);
            byte[] pixels = new byte[checked(region.Width * region.Height * 4)];
            for (int row = 0; row < region.Height; ++row)
                first.AsSpan(checked(((row + region.Y) * (int)capture.PixelWidth + region.X) * 4), region.Width * 4)
                    .CopyTo(pixels.AsSpan(row * region.Width * 4));
            string still = Path.Combine(work, "static.rgba");
            using (timing.Measure(StageTiming.EncodePlayback))
                await File.WriteAllBytesAsync(still, pixels, cancellationToken);
            return (new JsonObject { ["crop"] = JsonSerializer.SerializeToNode(region, JsonOptions) }, region, still);
        }
        JsonObject encoded;
        // 直编组的成品已经在渲染那一遍编好了：这里只把它接管进成品目录，不再解码重编，所以不计 encode_playback
        // （它的编码墙钟与渲染重叠，已含在 master_render 里），也不占编码槽配额（它是随渲染帧率的持续负载，不是尖峰）。
        if (directPlayback)
        {
            string used = gpuDirect ? PlaybackEncoderSelection.Vulkan
                : master["request"]?["playback_encoder_kind"]?.GetValue<string>() ?? playbackKind;
            // 硬件档位（非 Vulkan）的直编组只有 2 GiB 预判一种情况会落到软件编码（GroupRenderScheduler.MasterRequest）。
            bool overBudget = used == PlaybackEncoderSelection.Software &&
                playbackKind is not (PlaybackEncoderSelection.Software or PlaybackEncoderSelection.Vulkan);
            encoded = await runner.AdoptDirectPlaybackAsync(master, masterPath, Path.Combine(work, "encoded"),
                request.PlaybackEncoder ?? PlaybackEncoderSelection.Auto, used,
                master["gpu_pipeline_fallback_reason"]?.GetValue<string>() ??
                    (overBudget ? NativeRenderRunner.HardwareBudgetFallbackReason : playbackFallbackReason),
                cancellationToken);
        }
        else
        {
            // master 路线照旧整片解码重编一遍。等槽位的时间单独计时，不混进 encode_playback。
            progress?.Report(new(StageTiming.EncodeSlotWait, null, "Waiting for an encoding slot."));
            using var slot = await encodeSlots.AcquireAsync(cancellationToken);
            timing.Add(StageTiming.EncodeSlotWait, slot.WaitSeconds);
            using (timing.Measure(StageTiming.EncodePlayback))
                encoded = await runner.EncodeCroppedRgbaAsync(masterPath, Path.Combine(work, "encoded"), cancellationToken,
                    preserveAlpha: packedAlpha, playbackEncoder: playbackKind);
            // GPU 直编回退到 master 的组：记下真正的原因（含画质门没过的 QP），不是泛泛的"这一组要走无损路线"。
            if (master["gpu_pipeline_fallback_reason"] is JsonNode gpuFallback) encoded["encoder_fallback_reason"] = gpuFallback.DeepClone();
        }
        return (encoded, encoded["crop"]!.Deserialize<CacheRegion>(JsonOptions)!, encoded["video_path"]!.GetValue<string>());
    }
}
