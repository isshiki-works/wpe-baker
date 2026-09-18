using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Baker.Core;

public sealed record NativeTools(string Renderer, string Ffmpeg, string Ffprobe, string[] RuntimeDirectories);
public sealed record RenderCaptureSelection(int? OwnerLayerId = null, int? AuthoredEffectId = null,
    int? EffectOrdinal = null, string? LocalFbo = null, string? RuntimeRenderTarget = null, int? TextureVersion = null,
    bool? EffectTerminal = null, bool? ExactExtent = null);
public sealed record RenderLayerSelection(int[] IncludeLayers, bool TransparentBackground = false, bool IncludePostprocessing = true);
public sealed record OrthographicCaptureViewport(double CenterX, double CenterY, double Width, double Height);
public sealed record RenderRequest(string Source, string Assets, string OutputDirectory, uint Width, uint Height,
    uint FpsNumerator, uint FpsDenominator, ulong Frames, ulong WarmupFrames = 0, ulong Seed = 0,
    bool IncludeAudio = false, RenderCaptureSelection? CaptureTarget = null, JsonObject? Input = null,
    JsonArray? InputTimeline = null, JsonObject? UserProperties = null, string PixelPacking = "rgb", bool LosslessTest = false,
    string? DeviceUuid = null, bool CollectAlphaBounds = false, bool GpuTiming = false, int SchemaVersion = 1,
    RenderLayerSelection? LayerSelection = null, bool TraceScene = false, bool BoundsIncludeRgb = false,
    uint FrameSampleStride = 0, uint FrameSampleWidth = 64,
    OrthographicCaptureViewport? OrthographicCaptureViewport = null,
    ulong? FrameSamplePhaseFrames = null, bool FrameSampleIncludeAlpha = false,
    bool FrameSamplesOnly = false, JsonArray? OfflineVideoRateOverrides = null,
    uint? EncodeWidth = null, uint? EncodeHeight = null, bool RequireOpaquePixels = false,
    ulong? ForceKeyFrameFrame = null, RenderEncodePadding? EncodePadding = null,
    ulong? EncodedFrames = null, ulong[]? RetainFrames = null, string? PlaybackEncoderKind = null);
// EncodedFrames：只把前 N 帧送进编码器，渲染器照常连续渲染 Frames 帧。源周期路线用它多渲第 P 帧当闭合参照，成品仍是 P 帧。
// RetainFrames：按帧号（严格递增）把渲染器原始 RGBA 帧依次写进 retained-frames.rgba，编码前的无损原帧，供闭合检查与接缝参照。
// PlaybackEncoderKind：这次渲染直接产出播放版成品（不透明整幅组，不写无损 master），值是已解析好的档位
// （software / nvenc / qsv / amf）。非 null 时色彩链补成与"无损 master 解码后再编成品"完全相同的一串，
// 成品才能与 master 路线逐字节相同；与 LosslessTest、编码尺寸覆盖、补边互斥，且只支持不透明 rgb 打包。
/// <summary>缩放后的内容居中放进更大的编码画布（每半幅），补边为透明黑；只为满足硬件解码下限，回放按原矩形取样。</summary>
public sealed record RenderEncodePadding(uint Width, uint Height, uint OffsetX, uint OffsetY);
public sealed record RenderProgress(string Stage, double? Fraction, string Message);

/// <summary>Runs the actual renderer and encoder with bounded streaming backpressure.</summary>
public sealed partial class NativeRenderRunner(NativeTools tools)
{
    internal static string PlaybackEncoder(uint width, uint height, uint numerator, uint denominator)
    {
        return PlaybackEncodeProfile.SelectPlaybackEncoder(width, height, numerator, denominator);
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = string.Join(Path.PathSeparator, tools.RuntimeDirectories.Select(Path.GetFullPath)) +
            Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return info;
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private async Task<string> RunTextAsync(string executable, string[] arguments, string logPath, CancellationToken token)
    {
        if (!string.Equals(executable, tools.Ffprobe, StringComparison.OrdinalIgnoreCase))
            TemporaryCaptureFiles.RequireFreeSpace(logPath);
        using var process = new Process { StartInfo = StartInfo(executable, arguments) };
        await using var log = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        if (!process.Start()) throw new IOException($"Could not start {executable}");
        using var cancelled = token.Register(() => Stop(process));
        async Task DrainErrorsAsync()
        {
            try { await process.StandardError.BaseStream.CopyToAsync(log, CancellationToken.None); }
            catch { Stop(process); throw; }
        }
        Task stderr = DrainErrorsAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        try
        {
            Task completed = Task.WhenAll(process.WaitForExitAsync(token), stderr, stdout);
            if (string.Equals(executable, tools.Ffprobe, StringComparison.OrdinalIgnoreCase)) await completed;
            else await TemporaryCaptureFiles.WaitWhileWritingAsync(completed, logPath, token);
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} exited {process.ExitCode}; see {logPath}");
            return stdout.Result;
        }
        catch
        {
            Stop(process);
            try { await Task.WhenAll(stderr, stdout); } catch { }
            throw;
        }
        finally { Stop(process); }
    }

    public async Task<JsonObject> RenderAsync(RenderRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 1) throw new InvalidDataException("Unsupported render request version.");
        if (request.PixelPacking is not "rgb" and not "rgba_side_by_side") throw new ArgumentException("Unsupported pixel packing.");
        if (request.FrameSamplesOnly && (request.FrameSampleStride == 0 || request.FrameSampleWidth == 0))
            throw new ArgumentException("Frame-samples-only rendering requires a positive sample stride and width.");
        if (request.FrameSamplesOnly && request.IncludeAudio)
            throw new ArgumentException("Frame-samples-only rendering does not produce encoded audio or video.");
        ulong encodedFrames = request.EncodedFrames ?? request.Frames;
        if (request.EncodedFrames is { } limit && (request.FrameSamplesOnly || request.IncludeAudio || limit == 0 || limit > request.Frames))
            throw new ArgumentException("Encoded frames must be a positive prefix of an ordinary silent video render.");
        if (request.RetainFrames is { } retain && (request.FrameSamplesOnly || retain.Length is 0 or > 8 ||
            retain.Any(frame => frame >= request.Frames) || retain.Zip(retain.Skip(1)).Any(pair => pair.Second <= pair.First)))
            throw new ArgumentException("Retained frames must be 1..8 strictly increasing frame indices inside a video render.");
        // 强制关键帧是给后面的分段改写用的切点：那一帧必须真的在这次编码的帧序列里。
        if (request.ForceKeyFrameFrame is { } forcedKeyFrame && (request.FrameSamplesOnly || forcedKeyFrame == 0 || forcedKeyFrame >= encodedFrames))
            throw new ArgumentException("Forced key frame must be a positive frame index inside an encoded video request.");
        // 直编成品：裁剪区先验就是整幅，所以不缩放、不补边；无损 master 与它互斥（直编就是为了不写 master）。
        if (request.PlaybackEncoderKind is { } directKind && (request.FrameSamplesOnly || request.LosslessTest ||
            request.PixelPacking != "rgb" || request.EncodeWidth.HasValue || request.EncodeHeight.HasValue ||
            request.EncodePadding is not null || PlaybackEncoderSelection.Normalize(directKind) != directKind ||
            directKind == PlaybackEncoderSelection.Auto))
            throw new ArgumentException("Direct playback encoding needs a resolved encoder kind on an opaque full-frame lossy render.");
        bool encodeSizeRequested = request.EncodeWidth.HasValue || request.EncodeHeight.HasValue;
        if (encodeSizeRequested && (!request.EncodeWidth.HasValue || !request.EncodeHeight.HasValue))
            throw new ArgumentException("EncodeWidth and EncodeHeight must be specified together.");
        uint encodeWidth = request.EncodeWidth ?? request.Width;
        uint encodeHeight = request.EncodeHeight ?? request.Height;
        if (encodeSizeRequested && (request.FrameSamplesOnly || request.CollectAlphaBounds ||
            request.FrameSampleStride != 0 || request.FrameSamplePhaseFrames.HasValue || request.FrameSampleIncludeAlpha || request.LosslessTest))
            throw new ArgumentException("Encoding-size override supports only ordinary video encoding without alpha bounds, frame samples, or lossless test mode.");
        if (encodeSizeRequested && (encodeWidth == 0 || encodeHeight == 0 || encodeWidth > request.Width || encodeHeight > request.Height))
            throw new ArgumentException("Encoding-size override must be positive and must not upscale the renderer output.");
        if (request.EncodePadding is { } padding && (request.FrameSamplesOnly || request.CollectAlphaBounds || request.LosslessTest ||
            request.FrameSampleStride != 0 || request.FrameSamplePhaseFrames.HasValue || request.FrameSampleIncludeAlpha ||
            padding.OffsetX % 2 != 0 || padding.OffsetY % 2 != 0 ||
            (ulong)padding.OffsetX + encodeWidth > padding.Width || (ulong)padding.OffsetY + encodeHeight > padding.Height))
            throw new ArgumentException("Encode padding must contain the scaled content at even offsets and only applies to ordinary lossy video encoding.");
        // 补边之后的画布才是真正送进编码器的尺寸；下面的偶数校验、编码器选择与成品核对都按它来。
        string? padFilter = request.EncodePadding is { } pad
            ? FormattableString.Invariant($"pad={pad.Width}:{pad.Height}:{pad.OffsetX}:{pad.OffsetY}:color=black@0,") : null;
        uint canvasWidth = request.EncodePadding?.Width ?? encodeWidth;
        uint encodedHeight = request.EncodePadding?.Height ?? encodeHeight;
        uint encodedWidth = checked(canvasWidth * (request.PixelPacking == "rgba_side_by_side" ? 2u : 1u));
        if (request.Width == 0 || request.Height == 0 ||
            (!request.FrameSamplesOnly && !request.LosslessTest && (encodedWidth % 2 != 0 || encodedHeight % 2 != 0)))
            throw new ArgumentException("4:2:0 encoding requires positive even encoded dimensions.");
        if (request.FpsNumerator == 0 || request.FpsNumerator > int.MaxValue || request.FpsDenominator == 0 || request.Frames == 0)
            throw new ArgumentException("Frame count and rational FPS must be positive.");
        if ((ulong)request.Width * request.Height * 4 > 256ul * 1024 * 1024)
            throw new ArgumentException("One RGBA frame exceeds the configured 256 MiB readback budget.");
        if (request.OrthographicCaptureViewport is { } viewport &&
            (!double.IsFinite(viewport.CenterX) || !double.IsFinite(viewport.CenterY) ||
             !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
             viewport.Width <= 0 || viewport.Height <= 0))
            throw new ArgumentException("Orthographic capture viewport requires finite coordinates and positive dimensions.");
        if (!Directory.Exists(request.Assets)) throw new DirectoryNotFoundException(request.Assets);
        string[] executables = request.FrameSamplesOnly ? [tools.Renderer] : [tools.Renderer, tools.Ffmpeg, tools.Ffprobe];
        foreach (string executable in executables)
            if (!File.Exists(executable)) throw new FileNotFoundException("Required native tool missing.", executable);
        string output = Path.GetFullPath(request.OutputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Render output must be a new directory.");
        ProjectSource.EnsureNoReparsePoints(output);
        using var source = new ProjectSource(request.Source);
        if (source.Kind != "scene") throw new InvalidDataException("Native scene renderer requires a scene project.");
        if (output.StartsWith(source.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Render output cannot be inside the source project.");
        progress?.Report(new("preflight", null, "Hashing source and verifying native tools."));
        string sourceHash = await source.SourceHashAsync(cancellationToken);
        await using var executableFile = File.OpenRead(tools.Renderer);
        string rendererHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(executableFile, cancellationToken));
        TemporaryCaptureFiles.RequireFreeSpace(output);
        Directory.CreateDirectory(output);
        var manifest = new JsonObject { ["schema_version"] = 1, ["status"] = "running", ["artifact_kind"] = request.FrameSamplesOnly ? "frame_samples" : request.CaptureTarget is null ? "offline_master" : "effect_cache_master",
            ["optimization_validated"] = false, ["source_sha256"] = sourceHash, ["renderer_sha256"] = rendererHash,
            ["started_utc"] = DateTimeOffset.UtcNow.ToString("O"), ["request"] = JsonSerializer.SerializeToNode(request, JsonOptions),
            ["source_digest_scope"] = ProjectSource.DigestScope,
            ["pixel_packing"] = request.PixelPacking, ["lossless_test_encoding"] = !request.FrameSamplesOnly && request.LosslessTest,
            ["render_dimensions"] = new JsonObject { ["width"] = request.Width, ["height"] = request.Height },
            ["encode_dimensions"] = new JsonObject { ["width"] = encodedWidth, ["height"] = encodedHeight },
            ["color"] = request.FrameSamplesOnly ? "Area-averaged pre-encode frame samples; no video was encoded."
                : request.LosslessTest ? "Lossless H.264 RGB for equivalence testing; hardware playback is not assumed."
                : "RGBA8 renderer -> BT.709 limited-range yuv420p; color and packed alpha may have compression error." };
        if (request.FrameSamplesOnly) manifest["video_output"] = "not_requested";
        string manifestPath = Path.Combine(output, "manifest.json");
        bool renderFailed = false;
        await WriteJsonAsync(manifestPath, manifest, cancellationToken);
        try
        {
            string renderDirectory = Path.Combine(output, "native");
            var job = new JsonObject { ["schema_version"] = 1, ["source"] = source.SourcePath,
                ["assets"] = Path.GetFullPath(request.Assets), ["output_dir"] = renderDirectory,
                ["width"] = request.Width, ["height"] = request.Height, ["fps_num"] = request.FpsNumerator,
                ["fps_den"] = request.FpsDenominator, ["frames"] = request.Frames, ["warmup_frames"] = request.WarmupFrames,
                ["seed"] = request.Seed, ["raw_stdout"] = true };
            if (request.CaptureTarget is not null) job["capture_target"] = JsonSerializer.SerializeToNode(request.CaptureTarget, JsonOptions);
            if (request.OrthographicCaptureViewport is not null) job["orthographic_capture_viewport"] = JsonSerializer.SerializeToNode(request.OrthographicCaptureViewport, JsonOptions);
            if (request.LayerSelection is not null) job["layer_selection"] = JsonSerializer.SerializeToNode(request.LayerSelection, JsonOptions);
            if (request.Input is not null) job["input"] = request.Input.DeepClone();
            if (request.InputTimeline is not null) job["input_timeline"] = request.InputTimeline.DeepClone();
            if (request.UserProperties is not null) job["user_properties"] = request.UserProperties.DeepClone();
            if (request.OfflineVideoRateOverrides is not null) job["offline_video_rate_overrides"] = request.OfflineVideoRateOverrides.DeepClone();
            if (request.DeviceUuid is not null) job["device_uuid"] = request.DeviceUuid;
            if (request.GpuTiming) job["gpu_timing"] = true;
            if (request.TraceScene) job["trace_scene"] = true;
            string jobPath = Path.Combine(output, "renderer-job.json");
            await WriteJsonAsync(jobPath, job, cancellationToken);
            if (request.FrameSamplesOnly)
            {
                using var sampleRenderer = new Process { StartInfo = StartInfo(tools.Renderer, ["render", "--job", jobPath]) };
                await using var sampleRenderLog = new FileStream(Path.Combine(output, "renderer.stderr.log"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read);
                if (!sampleRenderer.Start()) throw new IOException("Could not start renderer.");
                using var cancelled = cancellationToken.Register(() => Stop(sampleRenderer));
                Task stderr = sampleRenderer.StandardError.BaseStream.CopyToAsync(sampleRenderLog, CancellationToken.None);
                FrameStreamSummary captured;
                try
                {
                    progress?.Report(new("rendering", 0, "Rendering each requested frame and retaining sparse samples only."));
                    captured = await CopyFrameStreamAsync(sampleRenderer.StandardOutput.BaseStream, Stream.Null, request, output,
                        progress, cancellationToken);
                    await Task.WhenAll(sampleRenderer.WaitForExitAsync(cancellationToken), stderr);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                                              error is not OperationCanceledException)
                {
                    Stop(sampleRenderer);
                    await sampleRenderer.WaitForExitAsync(CancellationToken.None);
                    string resultPath = Path.Combine(renderDirectory, "result.json");
                    if (File.Exists(resultPath))
                    {
                        IOException native = RendererFailure(resultPath, error.Message, error);
                        if (native.Message != error.Message) throw native;
                    }
                    throw;
                }
                catch { Stop(sampleRenderer); throw; }
                finally { Stop(sampleRenderer); await stderr; }
                if (sampleRenderer.ExitCode != 0)
                    throw RendererFailure(Path.Combine(renderDirectory, "result.json"),
                        $"Renderer exited {sampleRenderer.ExitCode}; original stderr log is retained.");
                JsonObject sampleNativeResult = JsonNode.Parse(await File.ReadAllTextAsync(
                    Path.Combine(renderDirectory, "result.json"), cancellationToken))!.AsObject();
                manifest["native_result"] = sampleNativeResult;
                manifest["stream_timing"] = StreamTiming(captured);
                if (captured.OpaquePixels is not null) manifest["opaque_pixels"] = captured.OpaquePixels;
                ConfirmVideoRateOverrides(request, sampleNativeResult);
                if (sampleNativeResult["status"]?.GetValue<string>() != "complete" ||
                    sampleNativeResult["written_frames"]?.GetValue<ulong>() != request.Frames ||
                    sampleNativeResult["renderer_error_count"]?.GetValue<ulong>() != 0)
                    throw new InvalidDataException("Renderer result did not confirm the requested frame sequence.");
                if (request.DeviceUuid is not null && !string.Equals(request.DeviceUuid,
                    sampleNativeResult["device_uuid"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Renderer did not confirm the requested GPU UUID.");
                if (request.CaptureTarget is not null &&
                    string.IsNullOrWhiteSpace(sampleNativeResult["capture_source"]?["render_target"]?.GetValue<string>()))
                    throw new InvalidDataException("Renderer did not confirm the actual local capture target; an older or incompatible renderer may have ignored it.");
                if (request.OrthographicCaptureViewport is not null && !JsonNode.DeepEquals(
                    sampleNativeResult["orthographic_capture_viewport"], JsonSerializer.SerializeToNode(request.OrthographicCaptureViewport, JsonOptions)))
                    throw new InvalidDataException("Renderer did not confirm the requested orthographic capture viewport.");
                if (request.LayerSelection is not null && !JsonNode.DeepEquals(sampleNativeResult["layer_selection"],
                    JsonSerializer.SerializeToNode(request.LayerSelection, JsonOptions)))
                    throw new InvalidDataException("Renderer did not confirm the requested layer selection.");
                if (captured.Samples is null)
                    throw new InvalidDataException("Renderer did not produce the requested frame samples.");
                if (sourceHash != await source.SourceHashAsync(cancellationToken))
                    throw new IOException("Source changed during render; artifact is invalid.");
                manifest["frame_samples"] = captured.Samples;
                manifest["status"] = "completed";
                manifest["completed_utc"] = DateTimeOffset.UtcNow.ToString("O");
                await WriteJsonAsync(manifestPath, manifest, cancellationToken);
                progress?.Report(new("completed", 1, "Sparse frame samples are ready; no preview video was encoded."));
                return manifest;
            }
            string partialVideo = Path.Combine(output, "video.partial.mp4");
            string fps = $"{request.FpsNumerator}/{request.FpsDenominator}";
            var profile = PlaybackEncodeProfile.Create(encodedWidth, encodedHeight, request.FpsNumerator, request.FpsDenominator,
                request.LosslessTest, request.PlaybackEncoderKind ?? PlaybackEncoderSelection.Software);
            string colorFilter = profile.ColorFilter;
            // 直编成品要与"无损 master 解码后再编成品"逐字节相同，所以送进 swscale 的东西必须一模一样：
            // master 那条链给 swscale 的是 h264 解码器产出的 gbrp 帧、再经一次全幅 crop（NativeRenderRunner.Crop.cs 的滤镜串），
            // 这里就把 RGBA 先摊成同一个 gbrp、再做同一次全幅 crop。两步都是重排像素，不改数值。
            if (request.PlaybackEncoderKind is not null)
                colorFilter = FormattableString.Invariant($"format=gbrp,crop={encodedWidth}:{encodedHeight}:0:0,") + colorFilter;
            var encoderArguments = new List<string> { "-hide_banner", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
                "-video_size", $"{request.Width}x{request.Height}", "-framerate", fps, "-i", "pipe:0", "-an" };
            // 补边放在缩放之后、拆 RGB/alpha 之前：两半幅用同一张透明黑画布，alphaextract 得到的补边 alpha 为 0。
            if (request.PixelPacking == "rgba_side_by_side")
                encoderArguments.AddRange(["-filter_complex", $"[0:v]{(encodeSizeRequested ? $"scale={encodeWidth}:{encodeHeight}:flags=lanczos,format=rgba," : "")}{padFilter}split=2[color][mask];[color]format=rgb24[rgb];[mask]alphaextract,format=rgb24[alpha];[rgb][alpha]hstack=inputs=2,{colorFilter}[packed]", "-map", "[packed]"]);
            else encoderArguments.AddRange(["-vf", encodeSizeRequested
                ? $"scale={encodeWidth}:{encodeHeight}:flags=lanczos,{padFilter}{colorFilter}"
                : padFilter + colorFilter]);
            // 在淡化窗口末尾强制一个 IDR，让接缝改写只需要重编码这一小段，后面全部 stream copy。
            if (request.ForceKeyFrameFrame is { } keyFrame)
                encoderArguments.AddRange(["-force_key_frames", $"expr:eq(n,{keyFrame.ToString(CultureInfo.InvariantCulture)})"]);
            encoderArguments.AddRange(profile.OutputArguments(request.FpsNumerator, request.FpsDenominator, partialVideo));
            var encoderInfo = StartInfo(tools.Ffmpeg, encoderArguments);
            encoderInfo.RedirectStandardInput = true;
            manifest["encoder_command"] = JsonSerializer.SerializeToNode(new { executable = encoderInfo.FileName, arguments = encoderInfo.ArgumentList.ToArray() });
            await WriteJsonAsync(manifestPath, manifest, cancellationToken);
            var phase = new JsonObject();
            long phaseStart = Stopwatch.GetTimestamp();
            void Phase(string name) { phase[name] = Math.Round(Stopwatch.GetElapsedTime(phaseStart).TotalSeconds, 3); phaseStart = Stopwatch.GetTimestamp(); }
            using var encoder = new Process { StartInfo = encoderInfo };
            using var renderer = new Process { StartInfo = StartInfo(tools.Renderer, ["render", "--job", jobPath]) };
            await using var renderLog = new FileStream(Path.Combine(output, "renderer.stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var encodeLog = new FileStream(Path.Combine(output, "encoder.stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (!encoder.Start()) throw new IOException("Could not start encoder.");
            try
            {
                if (!renderer.Start()) throw new IOException("Could not start renderer.");
                using var cancelled = cancellationToken.Register(() => { Stop(renderer); Stop(encoder); });
                async Task DrainAsync(Stream from, Stream to)
                {
                    try
                    {
                        await from.CopyToAsync(to, CancellationToken.None);
                        await to.FlushAsync(CancellationToken.None);
                    }
                    catch { Stop(renderer); Stop(encoder); throw; }
                }
                var errors = new[] { DrainAsync(renderer.StandardError.BaseStream, renderLog),
                    DrainAsync(encoder.StandardError.BaseStream, encodeLog),
                    DrainAsync(encoder.StandardOutput.BaseStream, Stream.Null) };
                progress?.Report(new("rendering", 0, "Rendering each requested frame and encoding with backpressure."));
                async Task PipeFramesAsync()
                {
                    try
                    {
                        var captured = await CopyFrameStreamAsync(renderer.StandardOutput.BaseStream,
                            encoder.StandardInput.BaseStream, request, output, progress, cancellationToken);
                        if (captured.Bounds is not null) manifest["alpha_bounds"] = captured.Bounds;
                        if (captured.Samples is not null) manifest["frame_samples"] = captured.Samples;
                        if (captured.OpaquePixels is not null) manifest["opaque_pixels"] = captured.OpaquePixels;
                        if (captured.Retained is not null) manifest["retained_frames"] = captured.Retained;
                        manifest["stream_timing"] = StreamTiming(captured);
                    }
                    finally { encoder.StandardInput.Close(); }
                }
                try
                {
                    Phase("process_start");
                    await PipeFramesAsync();
                    Phase("pipeline");
                    await Task.WhenAll(renderer.WaitForExitAsync(cancellationToken), encoder.WaitForExitAsync(cancellationToken));
                    Phase("wait_exit");
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not OperationCanceledException)
                {
                    Stop(renderer); Stop(encoder);
                    await Task.WhenAll(errors);
                    throw RendererFailure(Path.Combine(renderDirectory, "result.json"), error.Message, error);
                }
                catch { Stop(renderer); Stop(encoder); throw; }
                finally { await Task.WhenAll(errors); }
                if (renderer.ExitCode != 0 || encoder.ExitCode != 0)
                    throw RendererFailure(Path.Combine(renderDirectory, "result.json"),
                        $"Renderer exited {renderer.ExitCode}, encoder exited {encoder.ExitCode}; original stderr logs are retained.");

            }
            finally { Stop(renderer); Stop(encoder); }
            var nativeResult = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(renderDirectory, "result.json"), cancellationToken))!.AsObject();
            if (nativeResult["status"]?.GetValue<string>() != "complete" || nativeResult["written_frames"]?.GetValue<ulong>() != request.Frames ||
                nativeResult["renderer_error_count"]?.GetValue<ulong>() != 0)
                throw new InvalidDataException("Renderer result did not confirm the requested frame sequence.");
            manifest["native_result"] = nativeResult;
            ConfirmVideoRateOverrides(request, nativeResult);
            if (request.DeviceUuid is not null && !string.Equals(request.DeviceUuid, nativeResult["device_uuid"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Renderer did not confirm the requested GPU UUID.");
            if (request.CaptureTarget is not null && string.IsNullOrWhiteSpace(nativeResult["capture_source"]?["render_target"]?.GetValue<string>()))
                throw new InvalidDataException("Renderer did not confirm the actual local capture target; an older or incompatible renderer may have ignored it.");
            if (request.OrthographicCaptureViewport is not null && !JsonNode.DeepEquals(nativeResult["orthographic_capture_viewport"], JsonSerializer.SerializeToNode(request.OrthographicCaptureViewport, JsonOptions)))
                throw new InvalidDataException("Renderer did not confirm the requested orthographic capture viewport.");
            if (request.LayerSelection is not null && !JsonNode.DeepEquals(nativeResult["layer_selection"], JsonSerializer.SerializeToNode(request.LayerSelection, JsonOptions)))
                throw new InvalidDataException("Renderer did not confirm the requested layer selection.");
            string finalVideo = Path.Combine(output, "preview.mp4");
            if (request.IncludeAudio)
            {
                string pcm = Path.Combine(renderDirectory, "audio.f32le");
                _ = await RunTextAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", "-i", partialVideo, "-f", "f32le", "-ar", "48000",
                    "-ac", "2", "-i", pcm, "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                    "-movie_timescale", request.FpsNumerator.ToString(CultureInfo.InvariantCulture),
                    "-video_track_timescale", request.FpsNumerator.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", finalVideo],
                    Path.Combine(output, "audio-encode.stderr.log"), cancellationToken);
            }
            else File.Move(partialVideo, finalVideo);
            Phase("finalize_move");
            progress?.Report(new("validating", null, "Checking encoded frame count and rational timing."));
            // 无损 master 是本进程刚写出的中间文件，且随后每一帧都会被接缝/淡化/成品编码真实解码一遍；
            // 这里只需确认容器里的编码帧数、尺寸与精确帧率，用 -count_packets 读索引即可，不必整片重解码。
            bool fastCount = request.LosslessTest;
            string countFlag = fastCount ? "-count_packets" : "-count_frames";
            string countField = fastCount ? "nb_read_packets" : "nb_read_frames";
            // 直编成品就是最终交付的视频，它的 encoded_stream 要与 master 路线成品那份（Crop.cs）字段一致。
            string colorEntries = request.PlaybackEncoderKind is null ? "" : ",pix_fmt,color_space,color_range";
            string probeText = await RunTextAsync(tools.Ffprobe, ["-v", "error", "-threads", "4", "-select_streams", "v:0", countFlag,
                "-show_entries", $"stream=codec_name,width,height,avg_frame_rate,{countField},duration,duration_ts,time_base{colorEntries}", "-of", "json", finalVideo],
                Path.Combine(output, "ffprobe.stderr.log"), cancellationToken);
            Phase("ffprobe_count_frames");
            var probe = JsonNode.Parse(probeText)!.AsObject();
            var stream = probe["streams"]?.AsArray().Single()?.AsObject() ?? throw new InvalidDataException("Encoded video has no video stream.");
            var encodedRate = (stream["avg_frame_rate"]?.GetValue<string>() ?? "0/1").Split('/');
            if (stream["width"]?.GetValue<uint>() != encodedWidth || stream["height"]?.GetValue<uint>() != encodedHeight ||
                stream[countField]?.GetValue<string>() != encodedFrames.ToString(CultureInfo.InvariantCulture) ||
                encodedRate.Length != 2 || !ulong.TryParse(encodedRate[0], out var rateNumerator) || !ulong.TryParse(encodedRate[1], out var rateDenominator) ||
                rateDenominator == 0 || (UInt128)rateNumerator * request.FpsDenominator != (UInt128)rateDenominator * request.FpsNumerator)
                throw new InvalidDataException("Encoded video dimensions, frame count or exact FPS differ from the request.");
            ConfirmEncodedDuration(stream, encodedFrames, request.FpsNumerator, request.FpsDenominator);
            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during render; artifact is invalid.");
            // 只在确实少编码了帧时记录；残差路线的交叉淡化会把 request.frames 改写成 P，这里不能留一个过期的数。
            if (request.EncodedFrames is not null) manifest["encoded_frames"] = encodedFrames;
            manifest["encoded_stream"] = probe;
            await using var videoFile = File.OpenRead(finalVideo);
            manifest["video_sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(videoFile, cancellationToken));
            Phase("video_sha256");
            phase["master_bytes"] = new FileInfo(finalVideo).Length;
            manifest["render_phase_timing"] = phase;
            manifest["status"] = "completed";
            manifest["completed_utc"] = DateTimeOffset.UtcNow.ToString("O");
            await WriteJsonAsync(manifestPath, manifest, cancellationToken);
            progress?.Report(new("completed", 1, "Offline preview master is ready; optimization and official playback are not yet validated."));
            return manifest;
        }
        catch (Exception error)
        {
            if (FindOpaquePixelFailure(error) is { } opaque) manifest["opaque_pixels"] = opaque.Evidence;
            renderFailed = true;
            bool cancelled = cancellationToken.IsCancellationRequested || error is OperationCanceledException;
            manifest["status"] = cancelled ? "cancelled" : "failed";
            manifest["error_type"] = error.GetType().Name;
            manifest["error"] = error.Message;
            await WriteJsonAsync(manifestPath, manifest, CancellationToken.None);
            if (cancelled && error is not OperationCanceledException)
                throw new OperationCanceledException("Rendering was cancelled; the original interruption error is recorded in the manifest.", error, cancellationToken);
            throw;
        }
        finally
        {
            if (!request.IncludeAudio)
            {
                TemporaryCaptureFiles.Delete(manifest, output, "native/audio.f32le", "native/audio.f32le.partial");
                if (manifest["temporary_cleanup_errors"] is not null)
                    try { await WriteJsonAsync(manifestPath, manifest, CancellationToken.None); }
                    catch when (renderFailed) { }
            }
        }
    }

    /// <summary>这次渲染里等出帧与等吃帧各花了多少墙钟；两者都在渲染-编码同一条流水线上。</summary>
    private static JsonObject StreamTiming(FrameStreamSummary captured) => new()
    {
        ["readback_seconds"] = Math.Round(captured.ReadSeconds, 3),
        ["encode_seconds"] = Math.Round(captured.WriteSeconds, 3),
        ["loop_seconds"] = Math.Round(captured.LoopSeconds, 3),
        ["opaque_scan_seconds"] = Math.Round(captured.OpaqueScanSeconds, 3),
        ["bounds_seconds"] = Math.Round(captured.BoundsSeconds, 3),
        ["identical_seconds"] = Math.Round(captured.IdenticalSeconds, 3),
        ["sample_seconds"] = Math.Round(captured.SampleSeconds, 3),
        ["retain_write_seconds"] = Math.Round(captured.RetainWriteSeconds, 3),
        ["stall_seconds"] = Math.Round(captured.StallSeconds, 3),
        ["basis"] = "readback_seconds 是等渲染器交出每一帧的累计时间，encode_seconds 是等编码器吃下每一帧的背压时间；" +
            "渲染与编码并行，两者不能相加。"
    };

    private static OpaquePixelException? FindOpaquePixelFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is OpaquePixelException opaque) return opaque;
        return null;
    }

    private static void ConfirmVideoRateOverrides(RenderRequest request, JsonObject nativeResult)
    {
        if (request.OfflineVideoRateOverrides is null) return;
        if (nativeResult["runtime_video_rate_overrides"] is not JsonArray applied || applied.Count != request.OfflineVideoRateOverrides.Count)
            throw new InvalidDataException("Renderer did not confirm the requested exact video rate overrides.");
        for (int i = 0; i < applied.Count; ++i)
        {
            JsonObject expected = request.OfflineVideoRateOverrides[i]?.AsObject()
                ?? throw new InvalidDataException("Video rate override request is malformed.");
            JsonObject actual = applied[i]?.AsObject()
                ?? throw new InvalidDataException("Renderer returned malformed video rate override evidence.");
            if (!SameInteger(expected["owner_layer_id"], actual["owner_layer_id"]) ||
                !SameInteger(expected["rate_numerator"], actual["rate_numerator"]) ||
                !SameInteger(expected["rate_denominator"], actual["rate_denominator"]) ||
                actual["applied_control_count"] is not JsonValue count || !count.TryGetValue<uint>(out uint controls) || controls == 0)
                throw new InvalidDataException("Renderer did not apply the requested exact video rate override.");
        }
    }

    private static bool SameInteger(JsonNode? left, JsonNode? right) => left is JsonValue expected && right is JsonValue actual &&
        TryInteger(expected, out long a) && TryInteger(actual, out long b) && a == b;

    private static bool TryInteger(JsonValue value, out long integer)
    {
        if (value.TryGetValue<long>(out integer)) return true;
        if (!value.TryGetValue<int>(out int narrow)) return false;
        integer = narrow;
        return true;
    }

    private static async Task WriteJsonAsync(string path, JsonNode value, CancellationToken token)
    {
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value.ToJsonString(JsonOptions), token);
        File.Move(temporary, path, overwrite: true);
    }

    private static IOException RendererFailure(string resultPath, string fallback, Exception? inner = null)
    {
        // Keep the native cause visible at the CLI/GUI boundary, including script and
        // shader errors that occur before the first frame can be produced.
        try
        {
            var detail = new List<string>();
            JsonObject? result = null;
            try { if (File.Exists(resultPath)) result = JsonNode.Parse(File.ReadAllText(resultPath)) as JsonObject; }
            catch (Exception error) when (error is IOException or JsonException) { }
            if (result is not null)
            {
                if (result["error"] is JsonValue error && error.TryGetValue<string>(out string? message) && !string.IsNullOrWhiteSpace(message))
                    detail.Add(message);
            }
            string? native = Path.GetDirectoryName(resultPath);
            string? output = native is null ? null : Directory.GetParent(native)?.FullName;
            if (output is not null)
                foreach (var (name, timestamped) in new[] { ("renderer.stderr.log", true), ("encoder.stderr.log", false) })
                {
                    string stderr = Path.Combine(output, name);
                    if (!File.Exists(stderr)) continue;
                    try
                    {
                        using var log = new FileStream(stderr, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(log);
                        var errors = new HashSet<string>(StringComparer.Ordinal);
                        for (string? line; errors.Count < 3 && (line = reader.ReadLine()) is not null;)
                        {
                            line = line.Trim();
                            int close = line.IndexOf(']');
                            bool isRendererError = line.StartsWith("[", StringComparison.Ordinal) && close > 1 &&
                                line[1..close].EndsWith(" ERROR", StringComparison.OrdinalIgnoreCase);
                            bool isEncoderError = !timestamped && (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("No space left on device", StringComparison.OrdinalIgnoreCase));
                            if (!isRendererError && !isEncoderError) continue;
                            string message = isRendererError ? line[(close + 1)..].Trim() : line;
                            if (errors.Add(message)) detail.Add(message.Length <= 480 ? message : message[..477] + "...");
                        }
                    }
                    catch (IOException) { }
                }
            if (result?["diagnostics"] is JsonArray diagnostics)
                detail.AddRange(diagnostics.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out string? text) ? text : null)
                    .OfType<string>().Take(8));
            if (detail.Count > 0) return new IOException(fallback + "\n" + string.Join("\n", detail), inner);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { }
        return new IOException(fallback, inner);
    }

    /// <summary>
    /// master 视频里实际编码的帧数。源周期路线多渲一帧当闭合参照，request.frames 比视频多一帧，以 encoded_frames 为准；
    /// 旧 master 与残差路线没有这个字段，仍读 request.frames（交叉淡化会把它改写成 P）。
    /// </summary>
    internal static ulong EncodedFrameCount(JsonObject manifest) =>
        manifest["encoded_frames"] is JsonValue encoded && encoded.TryGetValue(out ulong count) ? count
            : manifest["request"]?["frames"]?.GetValue<ulong>() ?? 0;

    private static void ConfirmEncodedDuration(JsonObject stream, ulong frames, uint numerator, uint denominator)
    {
        if (stream["time_base"]?.GetValue<string>() != $"1/{numerator}" ||
            stream["duration_ts"] is not JsonValue duration || !duration.TryGetValue<ulong>(out ulong ticks) ||
            (UInt128)ticks != (UInt128)frames * denominator)
            throw new InvalidDataException("MP4 duration is not the exact requested number of frame intervals.");
    }
}
