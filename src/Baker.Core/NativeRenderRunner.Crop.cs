using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record CacheRegion(int CaptureWidth, int CaptureHeight, int X, int Y, int Width, int Height)
{
    public void Validate()
    {
        if (CaptureWidth <= 0 || CaptureHeight <= 0 || X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            (long)X + Width > CaptureWidth || (long)Y + Height > CaptureHeight)
            throw new InvalidDataException("Cache crop lies outside its capture extent.");
    }

    public static CacheRegion FromAlphaBounds(JsonObject manifest, int padding = 16)
    {
        if (padding < 2) throw new ArgumentException("At least two pixels of sampling halo are required.");
        int width = manifest["request"]!["width"]!.GetValue<int>(), height = manifest["request"]!["height"]!.GetValue<int>();
        if (width % 2 != 0 || height % 2 != 0) throw new InvalidDataException("This packed 4:2:0 crop requires even capture dimensions.");
        var bounds = manifest["alpha_bounds"]?.AsObject() ?? throw new InvalidDataException("Every-frame alpha bounds were not collected.");
        if (bounds["has_content"]?.GetValue<bool>() != true) throw new InvalidDataException("Transparent-only output should use a static transparent cache.");
        int bx = bounds["x"]!.GetValue<int>(), by = bounds["y"]!.GetValue<int>();
        int bw = bounds["width"]!.GetValue<int>(), bh = bounds["height"]!.GetValue<int>();
        int left = Math.Max(0, bx - padding) & ~1, top = Math.Max(0, by - padding) & ~1;
        int right = Math.Min(width, checked(bx + bw + padding + 1) & ~1);
        int bottom = Math.Min(height, checked(by + bh + padding + 1) & ~1);
        var region = new CacheRegion(width, height, left, top, right - left, bottom - top);
        region.Validate();
        return region;
    }
}

public sealed partial class NativeRenderRunner
{
    private static JsonObject HardwareDecodePreflightReport(HardwareDecodeDimensions.Plan plan, CacheRegion alphaRegion, CacheRegion region)
    {
        JsonObject report = plan.ToJson();
        report["alpha_bounds_crop"] = System.Text.Json.JsonSerializer.SerializeToNode(alphaRegion, JsonOptions);
        report["crop_grown_for_minimum"] = alphaRegion != region;
        // 裁剪区已经扩到位时 plan 为 pass；捕获本身小于下限扩不动时 plan 仍是 padded，这条路线不补边，只如实记录。
        if (plan.Status == HardwareDecodeDimensions.PaddedStatus)
            report["status"] = "below_minimum_capture_too_small";
        return report;
    }

    /// <summary>
    /// 解析一次播放版编码档位。软件档位不起任何进程；硬件档位问一次 <c>ffmpeg -encoders</c>。
    /// 直编路线要在渲染开始前就知道档位，所以把这一步单独拿出来，与 <see cref="EncodeCroppedRgbaAsync"/> 内部那次同一套判据。
    /// </summary>
    public async Task<(string Used, string? FallbackReason)> ResolvePlaybackEncoderAsync(string? requested, string logDirectory,
        CancellationToken cancellationToken = default)
    {
        string kind = PlaybackEncoderSelection.Normalize(requested);
        if (kind == PlaybackEncoderSelection.Software) return (PlaybackEncoderSelection.Software, null);
        Directory.CreateDirectory(logDirectory);
        if (kind == PlaybackEncoderSelection.Vulkan)
        {
            RendererCapabilities capabilities = await client.CapabilitiesAsync(
                Path.Combine(logDirectory, "gpu-renderer-capabilities.stderr.log"), cancellationToken);
            return capabilities.Has("gpu-loop-encode-v1") && capabilities.Has("gpu-sampling-coverage-v1")
                ? (kind,null) : (PlaybackEncoderSelection.Software,"Renderer does not support the complete GPU pipeline.");
        }
        return PlaybackEncoderSelection.Resolve(kind, await UsableEncodersAsync(kind, logDirectory, cancellationToken));
    }

    /// <summary>
    /// ffmpeg 编码器表；auto 时再把打不开的硬件编码器剔掉：表里有不等于这台机器能用（没有 NVIDIA 驱动时 nvenc 打不开，
    /// mf 常只落到软件 MFT）。按 auto 顺序逐档试编两帧空白画面，第一档全过就停。
    /// </summary>
    private async Task<HashSet<string>> UsableEncodersAsync(string kind, string logDirectory, CancellationToken cancellationToken)
    {
        HashSet<string> listed = PlaybackEncoderSelection.ParseEncoders(await ff.RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"],
            Path.Combine(logDirectory, "playback-encoders.stderr.log"), cancellationToken));
        if (kind != PlaybackEncoderSelection.Auto) return listed;
        string blank = Path.Combine(logDirectory, "encoder-probe.yuv");
        await File.WriteAllBytesAsync(blank, new byte[256 * 256 * 3], cancellationToken);
        foreach (string candidate in PlaybackEncoderSelection.AutoOrder)
        {
            string[] names = PlaybackEncoderSelection.RequiredEncoders(candidate);
            if (!names.All(listed.Contains)) continue;
            bool opened = true;
            foreach (string name in names)
                try
                {
                    _ = await ff.RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-f", "rawvideo", "-pix_fmt", "yuv420p",
                        "-s", "256x256", "-i", blank, "-c:v", name, .. (candidate == PlaybackEncoderSelection.Mf ? new[] { "-hw_encoding", "true" } : []),
                        "-f", "null", "-"], Path.Combine(logDirectory, $"encoder-probe-{name}.stderr.log"), cancellationToken);
                }
                catch (IOException) { cancellationToken.ThrowIfCancellationRequested(); opened = false; }
            if (opened) break;
            listed.ExceptWith(names);
        }
        File.Delete(blank);
        return listed;
    }

    /// <summary>
    /// 直编成品的接管：渲染时已按播放档把成品编好（<see cref="RenderRequest.PlaybackEncoderKind"/>），这里只把它移进成品目录，
    /// 并拼出与 <see cref="EncodeCroppedRgbaAsync"/> 同构的报告。裁剪区是不透明组的先验整幅；帧数、精确帧率、时长与
    /// SHA256 已由 <see cref="RenderAsync"/> 在同一文件上核对；GPU路径复用原生包计数与容器帧数，异常才解码计数。
    /// </summary>
    public async Task<JsonObject> AdoptDirectPlaybackAsync(JsonObject render, string renderDirectory, string outputDirectory,
        string requestedEncoder, string encoderKind, string? encoderFallbackReason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(render);
        renderDirectory = Path.GetFullPath(renderDirectory);
        bool gpu = render["native_frame_transport"]?.GetValue<string>() == "gpu_nv12";
        bool packed = render["pixel_packing"]?.GetValue<string>() == "rgba_side_by_side";
        if (render["status"]?.GetValue<string>() != "completed" || render["lossless_test_encoding"]?.GetValue<bool>() != false ||
            (!gpu && (packed || render["request"]?["playback_encoder_kind"]?.GetValue<string>() != encoderKind)) ||
            (gpu && encoderKind != PlaybackEncoderSelection.Vulkan))
            throw new InvalidDataException("Direct playback adoption requires a completed opaque render encoded with the same playback kind.");
        // 不透明组的裁剪范围是先验的整幅：BoundsIncludeRgb 下 alpha 恒 255 让空像素判据只剩"RGBA 全零"，并集就是整张画布。
        if (!packed && render["alpha_bounds"]?["minimum_alpha"]?.ToJsonString() != "255")
            throw new InvalidDataException("Direct playback requires every captured pixel to be opaque.");
        JsonObject request = render["request"]!.AsObject();
        int captureWidth = request["width"]!.GetValue<int>(), captureHeight = request["height"]!.GetValue<int>();
        uint numerator = request["fps_numerator"]!.GetValue<uint>(), denominator = request["fps_denominator"]!.GetValue<uint>();
        var region = gpu && render["gpu_crop"] is JsonObject gpuCrop
            ? System.Text.Json.JsonSerializer.Deserialize<CacheRegion>(gpuCrop.ToJsonString(),JsonOptions)!
            : new CacheRegion(captureWidth, captureHeight, 0, 0, captureWidth, captureHeight);
        region.Validate();
        if (gpu)
        {
            CacheRegion required = CacheRegion.FromAlphaBounds(render,padding:2);
            if (region.CaptureWidth != captureWidth || region.CaptureHeight != captureHeight ||
                region.X > required.X || region.Y > required.Y ||
                region.X + region.Width < required.X + required.Width || region.Y + region.Height < required.Y + required.Height)
                throw new InvalidDataException("The GPU crop does not contain the observed coverage and sampling halo.");
        }
        // 整幅区扩不动（捕获本身就是上界），GrowRegion 会原样返回；这里只是把同一条判据走一遍，越限照旧只记录。
        if (HardwareDecodeDimensions.GrowRegion(region, packedAlpha: packed, numerator, denominator) != region)
            throw new InvalidDataException("A full-frame crop cannot be grown for the hardware decode minimum.");
        HardwareDecodeDimensions.Plan decodePlan = HardwareDecodeDimensions.Evaluate((uint)region.Width, (uint)region.Height,
            packedAlpha: packed, numerator, denominator);
        ulong frames = EncodedFrameCount(render);
        string output = Path.GetFullPath(outputDirectory);
        ProjectSource.EnsureNoReparsePoints(output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Crop output must be a new directory.");
        Directory.CreateDirectory(output);
        string video = Path.Combine(output, "cache.mp4");
        File.Move(Path.Combine(renderDirectory, "preview.mp4"), video);
        var profile = PlaybackEncodeProfile.Create((uint)region.Width*(packed ? 2u : 1u), (uint)region.Height, numerator, denominator,
            losslessTest: false, encoderKind);
        var report = new JsonObject { ["schema_version"] = 1, ["status"] = "completed",
            ["capture_mode"] = gpu ? "gpu_direct_playback" : DirectPlaybackCaptureMode, ["master_sha256"] = null,
            ["pixel_packing"] = packed ? "rgba_side_by_side" : "rgb", ["crop"] = System.Text.Json.JsonSerializer.SerializeToNode(region, JsonOptions),
            ["frames"] = frames, ["fps_num"] = numerator, ["fps_den"] = denominator, ["encoder"] = profile.Encoder,
            ["encoder_requested"] = PlaybackEncoderSelection.Normalize(requestedEncoder), ["encoder_used"] = encoderKind,
            ["encoder_fallback_reason"] = encoderFallbackReason,
            ["hardware_decode"] = "not_verified",
            ["hardware_decode_preflight"] = HardwareDecodePreflightReport(decodePlan, region, region),
            ["decoded_area_fraction"] = (double)region.Width*region.Height/(captureWidth*(double)captureHeight),
            ["encoder_arguments"] = render["encoder_command"]?["arguments"]?.DeepClone(),
            // 直编没有独立的编码阶段：x264 与渲染器同时在跑，秒数无法与 master 路线的 encode_playback 横比。
            ["encode_seconds"] = null,
            ["encode_seconds_basis"] = "Encoded inside the render pipeline; its wall clock overlaps master_render and is not measured separately.",
            ["video_path"] = video,
            ["video_sha256"] = render["video_sha256"]?.DeepClone(),
            ["encoded_stream"] = render["encoded_stream"]?.DeepClone(),
            ["validation_required"] = "Cropped-cache reinjection, encoded loop seam and official playback." };
        // 硬件直编（GPU 管线或 nvenc 等）没有 master 可比，拿渲染器保留的原帧跑同一个画质门；直编无法升档重编，不过就拒。
        if (gpu || encoderKind != PlaybackEncoderSelection.Software)
        {
            JsonObject quality = await GpuPlaybackQualityAsync(render, video, region, packed, output, cancellationToken);
            report["playback_quality_gate"] = quality;
            if (quality["passed"]?.GetValue<bool>() != true)
            {
                report["status"] = "quality_rejected";
                report["reason"] = MessageCatalog.RenderLegacy("bake.gpu_quality_rejected");
                await WriteJsonAsync(Path.Combine(output, "manifest.json"), report, cancellationToken);
                throw new InvalidDataException(MessageCatalog.RenderLegacy("bake.gpu_quality_rejected"));
            }
        }
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), report, cancellationToken);
        return report;
    }

    /// <summary>成品是渲染时直接编出来的（不透明整幅组），没有写过无损 master。</summary>
    public const string DirectPlaybackCaptureMode = "direct_playback";

    /// <summary>成品是从无损 master 裁切重编出来的。</summary>
    public const string LosslessMasterCaptureMode = "lossless_master";

    /// <summary>Produces a playback video from a verified lossless RGB/alpha master.</summary>
    public async Task<JsonObject> EncodeCroppedRgbaAsync(string masterDirectory, string outputDirectory,
        CancellationToken cancellationToken = default, bool preserveAlpha = true, string? playbackEncoder = null)
    {
        string requestedEncoder = PlaybackEncoderSelection.Normalize(playbackEncoder);
        masterDirectory = Path.GetFullPath(masterDirectory);
        string manifestPath = Path.Combine(masterDirectory, "manifest.json");
        var master = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        bool masterPacked = master["pixel_packing"]?.GetValue<string>() == "rgba_side_by_side";
        if (master["status"]?.GetValue<string>() != "completed" || master["lossless_test_encoding"]?.GetValue<bool>() != true ||
            (!masterPacked && (preserveAlpha || master["pixel_packing"]?.GetValue<string>() != "rgb")))
            throw new InvalidDataException("Cropping requires a completed lossless RGB/alpha master to avoid repeated lossy encoding.");
        var request = master["request"]!.AsObject();
        uint numerator = request["fps_numerator"]!.GetValue<uint>(), denominator = request["fps_denominator"]!.GetValue<uint>();
        CacheRegion alphaRegion = CacheRegion.FromAlphaBounds(master);
        // 硬件解码下限：在捕获范围内对称扩大裁剪区。图层几何、质量校验与源切口比对都按裁剪区取，显示不变；
        // 这条分组路线不以硬件解码作闸门，越上限只记录，编码后的实测照旧执行。
        var region = HardwareDecodeDimensions.GrowRegion(alphaRegion, preserveAlpha, numerator, denominator);
        HardwareDecodeDimensions.Plan decodePlan = HardwareDecodeDimensions.Evaluate((uint)region.Width, (uint)region.Height,
            preserveAlpha, numerator, denominator);
        string inputPath = Path.Combine(masterDirectory, "preview.mp4");
        await using var inputLease = File.OpenRead(inputPath);
        string inputHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(inputLease, cancellationToken));
        if (!inputHash.Equals(master["video_sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lossless master video changed since verification.");
        ulong frames = EncodedFrameCount(master);
        string output = Path.GetFullPath(outputDirectory);
        ProjectSource.EnsureNoReparsePoints(output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Crop output must be a new directory.");
        Directory.CreateDirectory(output);
        string partial = Path.Combine(output, "cache.partial.mp4");
        // 左右并排越过 HEVC 宽度上限时上下并排（HardwareDecodeDimensions.StackedVertically）；master 仍是左右并排。
        bool below = HardwareDecodeDimensions.StackedVertically(preserveAlpha, region.Width);
        string filter = $"[0:v]split=2[color][mask];[color]crop={region.Width}:{region.Height}:{region.X}:{region.Y}[rgb];" +
            $"[mask]crop={region.Width}:{region.Height}:{region.CaptureWidth + region.X}:{region.Y}[alpha];" +
            $"[rgb][alpha]{(below ? "vstack" : "hstack")}=inputs=2,{PlaybackEncodeProfile.Bt709Filter}[packed]";
        if (!preserveAlpha)
        {
            if (master["alpha_bounds"]?["minimum_alpha"]?.GetValue<int>() != 255)
                throw new InvalidDataException("Dropping alpha requires every captured pixel to be opaque.");
            filter = $"[0:v]crop={region.Width}:{region.Height}:{region.X}:{region.Y}," +
                $"{PlaybackEncodeProfile.Bt709Filter}[packed]";
        }
        int encodedWidth = region.Width * (preserveAlpha && !below ? 2 : 1), encodedHeight = region.Height * (below ? 2 : 1);
        // 只有请求了硬件档位才去问一次 ffmpeg 支持哪些编码器；软件档位保持原来的零额外进程。
        string encoderKind = PlaybackEncoderSelection.Software;
        string? encoderFallbackReason = null;
        if (requestedEncoder == PlaybackEncoderSelection.Vulkan)
            encoderFallbackReason = "This group requires the lossless path; using the software playback encoder.";
        else if (requestedEncoder != PlaybackEncoderSelection.Software)
            (encoderKind, encoderFallbackReason) = PlaybackEncoderSelection.Resolve(requestedEncoder,
                await UsableEncodersAsync(requestedEncoder, output, cancellationToken));
        var profile = PlaybackEncodeProfile.Create((uint)encodedWidth, (uint)encodedHeight, numerator, denominator,
            losslessTest: false, encoderKind);
        // mf 档位要区分「真的落到厂商硬件 MFT」与「只有微软自带的软件 MFT」：能编不等于硬件在编。
        JsonObject? mediaFoundation = null;
        if (encoderKind == PlaybackEncoderSelection.Mf)
        {
            string probeVideo = Path.Combine(output, "mf-probe.mp4"), probeLog = Path.Combine(output, "mf-probe.stderr.log");
            int probeExit = 0;
            try
            {
                _ = await ff.RunTextAsync(tools.Ffmpeg,
                    PlaybackEncoderSelection.MfProbeArguments(profile.Encoder, inputPath, probeVideo), probeLog, cancellationToken);
            }
            // 探测失败是正常结果之一（这台机器走不通硬件 MFT），不是这次生成的错误。
            catch (IOException) { cancellationToken.ThrowIfCancellationRequested(); probeExit = 1; }
            string probeText = File.Exists(probeLog) ? await File.ReadAllTextAsync(probeLog, cancellationToken) : "";
            (bool hardware, string? mft, string? failure) = PlaybackEncoderSelection.ParseMfProbe(probeExit, probeText);
            mediaFoundation = new JsonObject
            {
                ["mf_hardware"] = hardware,
                ["mf_mft"] = hardware ? mft : null,
                ["mf_hardware_failure"] = failure,
                ["mf_note"] = hardware ? null : PlaybackEncoderSelection.MfSoftwareOnlyNote(failure, mft),
            };
            try { File.Delete(probeVideo); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // 画质判据的抽样帧号在编码前定下来，升档重编时保持同一批帧，前后可直接横比。
        ulong[] gateFrames = QualityGate.SampleFrames(frames);
        double gateReference = QualityGate.DefaultReferenceSsim, gateRatio = QualityGate.DefaultRatio;
        string[] EncodeArguments(PlaybackEncodeProfile current) =>
            ["-hide_banner", "-nostdin", "-n", "-i", inputPath, "-filter_complex", filter, "-map", "[packed]", "-an",
                .. current.OutputArguments(numerator, denominator, partial)];
        string encoder = profile.Encoder;
        string[] arguments = EncodeArguments(profile);
        var report = new JsonObject { ["schema_version"] = 1, ["status"] = "running", ["master_sha256"] = inputHash,
            ["pixel_packing"] = preserveAlpha ? "rgba_side_by_side" : "rgb", ["crop"] = System.Text.Json.JsonSerializer.SerializeToNode(region, JsonOptions),
            ["frames"] = frames, ["fps_num"] = numerator, ["fps_den"] = denominator, ["encoder"] = encoder,
            ["encoder_requested"] = requestedEncoder, ["encoder_used"] = encoderKind,
            ["encoder_fallback_reason"] = encoderFallbackReason,
            ["media_foundation"] = mediaFoundation,
            ["hardware_decode"] = "not_verified",
            ["hardware_decode_preflight"] = HardwareDecodePreflightReport(decodePlan, alphaRegion, region),
            ["decoded_area_fraction"] = (double)region.Width * region.Height / ((double)region.CaptureWidth * region.CaptureHeight),
            ["encoder_arguments"] = new JsonArray(arguments.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["validation_required"] = "Cropped-cache reinjection, encoded loop seam and official playback." };
        string reportPath = Path.Combine(output, "manifest.json");
        await WriteJsonAsync(reportPath, report, cancellationToken);
        bool encodingFailed = false;
        try
        {
            JsonObject probe;
            JsonObject? qualityGate = null;
            for (int attempt = 0; ; attempt++)
            {
                // 升档重编要另开日志：RunTextAsync 用 FileMode.CreateNew，同名日志会直接失败。
                string suffix = attempt == 0 ? "" : "." + attempt.ToString(CultureInfo.InvariantCulture);
                // 只包住这一次 ffmpeg 调用：播放版编码在这里是独立进程，不与渲染重叠，秒数可直接横比。
                long encodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = await ff.RunTextAsync(tools.Ffmpeg, arguments, Path.Combine(output, $"encoder{suffix}.stderr.log"), cancellationToken);
                report["encode_seconds"] = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(encodeStart).TotalSeconds, 3);
                EncodedStream verified = await VerifyEncoded.ProbeAsync(ff, partial,
                    "stream=codec_name,width,height,avg_frame_rate,nb_frames,duration,duration_ts,time_base,pix_fmt,color_space,color_range",
                    frames, Path.Combine(output, $"ffprobe{suffix}.stderr.log"), cancellationToken);
                probe = verified.Probe;
                report["frame_count_validation"] = new JsonObject {
                    ["source"] = verified.CountSource, ["full_decode_performed"] = verified.CountSource == "full_decode",
                    ["fallback_reason"] = verified.FallbackReason };
                if (!verified.Has(encodedWidth, encodedHeight, frames) || !verified.RateIs(numerator, denominator))
                    throw new InvalidDataException("Cropped video violates dimensions, frame count or rational FPS.");
                ConfirmEncodedDuration(verified, frames, numerator, denominator);
                // 软件档位本身就是画质判据的参照，不自己跟自己比，也保持原来的零额外进程。
                if (profile.Kind == PlaybackEncoderSelection.Software || gateFrames.Length == 0) break;
                QualityReport measured = await qualityComparer.CompareAsync(new(partial, frames, gateFrames, numerator, denominator,
                    new MasterQualityReference(inputPath, filter), output, $"quality{suffix}"), cancellationToken);
                double? ssim = measured.Ssim, psnr = measured.Psnr;
                report["quality_decode_scope"] = measured.DecodeScope;
                if (QualityGate.Passes(ssim, gateReference, gateRatio))
                {
                    qualityGate = QualityGate.Summarize(gateFrames, gateReference, gateRatio, ssim, psnr,
                        profile.QualityStep, QualityGate.ActionAccepted);
                    break;
                }
                if (profile.CanEscalate)
                {
                    qualityGate = QualityGate.Summarize(gateFrames, gateReference, gateRatio, ssim, psnr,
                        profile.QualityStep, QualityGate.ActionEscalated, "SSIM 低于阈值，升一档质量重编。");
                    profile = profile.Escalate();
                }
                else
                {
                    encoderFallbackReason = $"{profile.Kind} 升到质量上限仍未达到 SSIM 阈值 " +
                        $"{QualityGate.Threshold(gateReference, gateRatio).ToString("0.######", CultureInfo.InvariantCulture)}，回退软件编码。";
                    qualityGate = QualityGate.Summarize(gateFrames, gateReference, gateRatio, ssim, psnr,
                        profile.QualityStep, QualityGate.ActionFellBack, encoderFallbackReason);
                    encoderKind = PlaybackEncoderSelection.Software;
                    profile = PlaybackEncodeProfile.Create((uint)encodedWidth, (uint)encodedHeight, numerator, denominator,
                        losslessTest: false, PlaybackEncoderSelection.Software);
                    report["encoder_used"] = encoderKind;
                    report["encoder_fallback_reason"] = encoderFallbackReason;
                }
                encoder = profile.Encoder;
                arguments = EncodeArguments(profile);
                report["encoder"] = encoder;
                report["encoder_arguments"] = new JsonArray(arguments.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
                File.Delete(partial);
            }
            if (qualityGate is not null) report["playback_quality_gate"] = qualityGate;
            string video = Path.Combine(output, "cache.mp4");
            File.Move(partial, video);
            await using var videoFile = File.OpenRead(video);
            report["video_sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(videoFile, cancellationToken));
            report["video_path"] = video;
            report["encoded_stream"] = probe;
            report["status"] = "completed";
            await WriteJsonAsync(reportPath, report, cancellationToken);
            return report;
        }
        catch (Exception error)
        {
            encodingFailed = true;
            bool cancelled = cancellationToken.IsCancellationRequested || error is OperationCanceledException;
            report["status"] = cancelled ? "cancelled" : "failed";
            report["error_type"] = error.GetType().Name;
            report["error"] = error.Message;
            await WriteJsonAsync(reportPath, report, CancellationToken.None);
            if (cancelled && error is not OperationCanceledException) throw new OperationCanceledException("Cache encoding cancelled.", error, cancellationToken);
            throw;
        }
        finally
        {
            TemporaryCaptureFiles.Delete(report, output, "cache.partial.mp4");
            if (report["temporary_cleanup_errors"] is not null)
                try { await WriteJsonAsync(reportPath, report, CancellationToken.None); }
                catch when (encodingFailed) { }
        }
    }
}
