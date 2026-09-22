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
    bool? EffectTerminal = null, bool? ExactExtent = null, bool? ForceVisibleOwner = null);
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
    ulong? EncodedFrames = null, ulong[]? RetainFrames = null, string? PlaybackEncoderKind = null,
    GpuEncodeRequest? GpuEncoding = null, bool CollectSamplingCoverage = false,
    double EffectRenderScale = 1.0, bool MatchEffectResolution = false);
public sealed record GpuEncodeRequest(string Codec = "h264_vulkan", int Qp = 18,
    uint CrossfadeFrames = 0, CacheRegion? Crop = null, bool RetainLoopWindow = false,
    bool RetainQualitySamples = false);
// EncodedFrames：只把前 N 帧送进编码器，渲染器照常连续渲染 Frames 帧。源周期路线用它多渲第 P 帧当闭合参照，成品仍是 P 帧。
// RetainFrames：按帧号（严格递增）把渲染器原始 RGBA 帧依次写进 retained-frames.rgba，编码前的无损原帧，供闭合检查与接缝参照。
// PlaybackEncoderKind：这次渲染直接产出播放版成品（不透明整幅组，不写无损 master），值是已解析好的档位
// （software / nvenc / qsv / amf）。非 null 时色彩链补成与"无损 master 解码后再编成品"完全相同的一串，
// 成品才能与 master 路线逐字节相同；与 LosslessTest、编码尺寸覆盖、补边互斥，且只支持不透明 rgb 打包。
/// <summary>缩放后的内容居中放进更大的编码画布（每半幅），补边为透明黑；只为满足硬件解码下限，回放按原矩形取样。</summary>
public sealed record RenderEncodePadding(uint Width, uint Height, uint OffsetX, uint OffsetY);
public sealed record RenderProgress(string Stage, double? Fraction, string Message,
    double? StageElapsedSeconds = null, double? StageRemainingSeconds = null, ulong? FramesCompleted = null, ulong? FramesTotal = null);

/// <summary>
/// 同设备 GPU 编码起不来（渲染器缺能力、编码初始化失败、缺 Vulkan 设备扩展）：调用方据此改走软件编码。
/// 渲染器是外部进程，只能从它的错误行归类；归类只在 <see cref="NativeRenderRunner"/> 这一处做。
/// </summary>
public sealed class GpuEncodeUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

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

    internal static async Task StopAndWaitAsync(Process process)
    {
        Stop(process);
        try { await process.WaitForExitAsync(CancellationToken.None); }
        catch (InvalidOperationException) { } // The second process may never have started.
    }

    private async Task<string> RunTextAsync(string executable, string[] arguments, string logPath, CancellationToken token,
        Action<string>? stderrLine = null)
    {
        if (!string.Equals(executable, tools.Ffprobe, StringComparison.OrdinalIgnoreCase))
            TemporaryCaptureFiles.RequireFreeSpace(logPath);
        using var process = new Process { StartInfo = StartInfo(executable, arguments) };
        await using var log = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        if (!process.Start()) throw new IOException($"Could not start {executable}");
        using var cancelled = token.Register(() => Stop(process));
        async Task DrainErrorsAsync()
        {
            try
            {
                if (stderrLine is null) await process.StandardError.BaseStream.CopyToAsync(log, CancellationToken.None);
                else
                {
                    await using var writer = new StreamWriter(log, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                    {
                        await writer.WriteLineAsync(line);
                        stderrLine(line);
                    }
                }
            }
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
        finally { await StopAndWaitAsync(process); }
    }

    public async Task<JsonObject> RenderAsync(RenderRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 1) throw new InvalidDataException("Unsupported render request version.");
        if (!double.IsFinite(request.EffectRenderScale) || request.EffectRenderScale is <= 0 or > 1)
            throw new ArgumentException("EffectRenderScale must be finite and in (0, 1].");
        if (request.MatchEffectResolution && request.EffectRenderScale != 1.0)
            throw new ArgumentException("Adaptive effect resolution cannot be combined with EffectRenderScale other than 1.");
        if (request.PixelPacking is not "rgb" and not "rgba_side_by_side") throw new ArgumentException("Unsupported pixel packing.");
        if (request.CollectSamplingCoverage && !request.FrameSamplesOnly)
            throw new ArgumentException("Sampling coverage applies only to frame-sample requests.");
        if (request.GpuEncoding is { } gpu && (gpu.Codec is not ("h264_vulkan" or "hevc_vulkan") ||
            gpu.Qp is < 0 or > 51 || request.LosslessTest || request.FrameSamplesOnly || request.RequireOpaquePixels ||
            request.ForceKeyFrameFrame is not null || request.FrameSampleStride != 0 ||
            request.EncodePadding is not null || request.PlaybackEncoderKind is not null ||
            (gpu.CrossfadeFrames > 0 && request.EncodeWidth is not null)))
            throw new ArgumentException("GPU encoding requires even output dimensions without per-frame CPU processing, padding or external key-frame overrides; resizing cannot be combined with loop blending.");
        if (request.GpuEncoding?.Crop is { } gpuCrop)
        {
            gpuCrop.Validate();
            bool resizedCrop = request.EncodeWidth.HasValue && request.EncodeHeight.HasValue &&
                (request.EncodeWidth != (uint)gpuCrop.Width || request.EncodeHeight != (uint)gpuCrop.Height);
            if (gpuCrop.CaptureWidth != request.Width || gpuCrop.CaptureHeight != request.Height ||
                (!resizedCrop && ((gpuCrop.X | gpuCrop.Y | gpuCrop.Width | gpuCrop.Height) & 1) != 0))
                throw new ArgumentException("GPU crop must use this capture extent and even coordinates/dimensions.");
        }
        if (request.FrameSamplesOnly && (request.FrameSampleStride == 0 || request.FrameSampleWidth == 0))
            throw new ArgumentException("Frame-samples-only rendering requires a positive sample stride and width.");
        if (request.FrameSamplesOnly && request.IncludeAudio)
            throw new ArgumentException("Frame-samples-only rendering does not produce encoded audio or video.");
        ulong encodedFrames = request.EncodedFrames ?? request.Frames;
        if (request.GpuEncoding is { RetainQualitySamples: true } quality)
            request = request with {
                RetainFrames = (request.RetainFrames ?? []).Concat(PlaybackQualityGate.SampleFrames(encodedFrames)).Distinct().Order().ToArray(),
                GpuEncoding = quality with { RetainLoopWindow = quality.RetainLoopWindow || quality.CrossfadeFrames > 0 } };
        if (request.GpuEncoding is { CrossfadeFrames: > 0 } fade &&
            (fade.CrossfadeFrames >= encodedFrames || encodedFrames > request.Frames ||
             request.Frames - encodedFrames != fade.CrossfadeFrames))
            throw new ArgumentException("GPU crossfade requires exactly one continuation window after the encoded loop.");
        if (request.GpuEncoding is { RetainLoopWindow: true, CrossfadeFrames: 0 })
            throw new ArgumentException("Original loop window retention requires GPU crossfade.");
        if (request.EncodedFrames is { } limit && (request.FrameSamplesOnly || request.IncludeAudio || limit == 0 || limit > request.Frames))
            throw new ArgumentException("Encoded frames must be a positive prefix of an ordinary silent video render.");
        if (request.RetainFrames is { } retain && (request.FrameSamplesOnly || retain.Length == 0 ||
            retain.Length > (request.GpuEncoding is null ? 8 : 32) ||
            retain.Any(frame => frame >= request.Frames) || retain.Zip(retain.Skip(1)).Any(pair => pair.Second <= pair.First)))
            throw new ArgumentException("Retained frames must be increasing indices inside the render (at most 8 CPU or 32 GPU frames).");
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
        if (encodeSizeRequested && (request.FrameSamplesOnly || (request.CollectAlphaBounds && request.GpuEncoding is null) ||
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
        uint canvasWidth = request.GpuEncoding is not null
            ? request.EncodeWidth ?? (uint)(request.GpuEncoding.Crop?.Width ?? (int)request.Width)
            : request.EncodePadding?.Width ?? encodeWidth;
        uint encodedHeight = request.GpuEncoding is not null
            ? request.EncodeHeight ?? (uint)(request.GpuEncoding.Crop?.Height ?? (int)request.Height)
            : request.EncodePadding?.Height ?? encodeHeight;
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
            RenderJob job = RenderJob.From(request, source.SourcePath, renderDirectory, rawStdout: true) with
            {
                WriteAudio = request.IncludeAudio,
                EffectRenderScale = request.EffectRenderScale != 1.0 ? request.EffectRenderScale : null,
                MatchEffectResolution = request.MatchEffectResolution
            };
            // Only pure sample consumers may omit full frames. Bounds, opacity and retained-frame
            // checks still require their original complete input. Older renderers keep that path.
            bool sampleConsumer = request.FrameSamplesOnly && !request.CollectAlphaBounds && !request.RequireOpaquePixels &&
                request.RetainFrames is not { Length: > 0 };
            string rendererVersion = sampleConsumer || request.GpuEncoding is not null || request.EffectRenderScale != 1.0 || request.MatchEffectResolution || request.CaptureTarget?.ForceVisibleOwner == true ? await RunTextAsync(tools.Renderer, ["--version"],
                Path.Combine(output, "renderer-capabilities.stderr.log"), cancellationToken) : "";
            if (request.EffectRenderScale != 1.0 && !rendererVersion.Contains("effect-render-scale-v1", StringComparison.Ordinal))
                throw new InvalidDataException("Renderer does not support internal effect scaling.");
            if (request.MatchEffectResolution && !rendererVersion.Contains("adaptive-effect-resolution-v1", StringComparison.Ordinal))
                throw new InvalidDataException("Renderer does not support adaptive effect resolution.");
            if (request.CaptureTarget?.ForceVisibleOwner == true && !rendererVersion.Contains("capture-force-visible-owner-v1", StringComparison.Ordinal))
                throw new InvalidDataException("Renderer does not support capturing a visibility-controlled effect owner.");
            bool sparseInput = sampleConsumer && rendererVersion.Contains("features=sparse-readback-v1", StringComparison.Ordinal);
            bool nativeSamples = sparseInput && rendererVersion.Contains("gpu-samples-v1", StringComparison.Ordinal);
            RenderRequest streamRequest = request;
            if (sparseInput)
            {
                job = job with { OutputFrameStride = request.FrameSampleStride,
                    OutputFramePhase = request.FrameSamplePhaseFrames % request.FrameSampleStride };
            }
            if (nativeSamples)
            {
                uint sampleWidth = Math.Min(request.Width, request.FrameSampleWidth);
                uint sampleHeight = (uint)Math.Max(1, Math.Round((double)request.Height * sampleWidth / request.Width));
                job = job with { OutputSampleWidth = sampleWidth, OutputSampleHeight = sampleHeight,
                    CollectSamplingCoverage = request.CollectSamplingCoverage &&
                        rendererVersion.Contains("gpu-sampling-coverage-v1", StringComparison.Ordinal) ? true : null };
                streamRequest = request with { Width = sampleWidth, Height = sampleHeight, FrameSampleWidth = sampleWidth };
            }
            manifest["native_frame_transport"] = nativeSamples ? "sampled_rgba" : sparseInput ? "sparse_rgba" : "full_rgba";
            if (request.GpuEncoding is { } encoding)
            {
                if (encodeSizeRequested && !rendererVersion.Contains("gpu-encode-resize-v1", StringComparison.Ordinal))
                    throw new GpuEncodeUnavailableException("GPU encode initialization: renderer cannot resize texture captures on the GPU.");
                if (encoding.RetainQualitySamples && !rendererVersion.Contains("gpu-quality-samples-v1", StringComparison.Ordinal))
                    throw new GpuEncodeUnavailableException("GPU encode initialization: renderer cannot retain the required quality samples.");
                if (!rendererVersion.Contains("gpu-encode-v1", StringComparison.Ordinal))
                    throw new InvalidDataException("Renderer does not support same-device GPU encoding.");
                if ((request.CollectAlphaBounds || request.RetainFrames is not null || request.EncodedFrames is not null) &&
                    !rendererVersion.Contains("gpu-capture-v1", StringComparison.Ordinal))
                    throw new InvalidDataException("Renderer does not support GPU capture statistics and retained frames.");
                if ((encoding.CrossfadeFrames != 0 || encoding.Crop is not null) &&
                    !rendererVersion.Contains("gpu-loop-encode-v1", StringComparison.Ordinal))
                    throw new InvalidDataException("Renderer does not support GPU crop and loop encoding.");
                job = job with { RawStdout = false, GpuEncode = new(encoding.Codec, encoding.Qp,
                    PackedAlpha: request.PixelPacking == "rgba_side_by_side", EncodedFrames: encodedFrames,
                    CollectBounds: request.CollectAlphaBounds, BoundsIncludeRgb: request.BoundsIncludeRgb,
                    RetainFrames: request.RetainFrames ?? [], encoding.CrossfadeFrames, encoding.RetainLoopWindow,
                    CropX: encoding.Crop?.X ?? 0, CropY: encoding.Crop?.Y ?? 0,
                    CropWidth: encoding.Crop?.Width ?? (int)request.Width, CropHeight: encoding.Crop?.Height ?? (int)request.Height,
                    ResizeWidth: encodeSizeRequested ? encodeWidth : null, ResizeHeight: encodeSizeRequested ? encodeHeight : null) };
            }
            string jobPath = Path.Combine(output, "renderer-job.json");
            await WriteJsonAsync(jobPath, JsonSerializer.SerializeToNode(job, JsonOptions)!, cancellationToken);
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
                    captured = await CopyFrameStreamAsync(sampleRenderer.StandardOutput.BaseStream, Stream.Null, streamRequest, output,
                        progress, cancellationToken, sparseInput);
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
                finally { await StopAndWaitAsync(sampleRenderer); await stderr; }
                if (sampleRenderer.ExitCode != 0)
                    throw RendererFailure(Path.Combine(renderDirectory, "result.json"),
                        $"Renderer exited {sampleRenderer.ExitCode}; original stderr log is retained.");
                RenderResult sampleNativeResult = RenderResult.Parse(await File.ReadAllTextAsync(
                    Path.Combine(renderDirectory, "result.json"), cancellationToken));
                manifest["native_result"] = sampleNativeResult.Json;
                ConfirmRenderOptions(request, sampleNativeResult);
                if (request.CollectSamplingCoverage)
                {
                    if (sampleNativeResult.SamplingCoverage is JsonObject coverage)
                    {
                        if (coverage["status"]?.GetValue<string>() != "complete" || coverage["includes_rgb"]?.GetValue<bool>() != true ||
                            coverage["frames"]?.GetValue<ulong>() != request.Frames ||
                            coverage["first_simulation_frame"]?.GetValue<ulong>() != request.WarmupFrames ||
                            coverage["capture_width"]?.GetValue<uint>() != request.Width || coverage["capture_height"]?.GetValue<uint>() != request.Height)
                            throw new InvalidDataException("GPU sampling coverage does not cover the requested full-resolution interval.");
                        manifest["sampling_coverage"] = coverage.DeepClone();
                        manifest["sampling_coverage"]!["render_request"] = JsonSerializer.SerializeToNode(request, JsonOptions);
                        manifest["sampling_coverage_status"] = "complete";
                    }
                    else manifest["sampling_coverage_status"] = "unavailable_on_renderer_or_device";
                }
                manifest["stream_timing"] = StreamTiming(captured);
                if (captured.Bounds is not null) manifest["alpha_bounds"] = captured.Bounds;
                if (captured.OpaquePixels is not null) manifest["opaque_pixels"] = captured.OpaquePixels;
                ConfirmVideoRateOverrides(request, sampleNativeResult);
                ulong expectedNativeFrames = sparseInput ? captured.Samples?["count"]?.GetValue<ulong>() ?? 0 : request.Frames;
                manifest["readback_frames"] = expectedNativeFrames;
                manifest["pipe_rgba_bytes"] = checked(expectedNativeFrames * streamRequest.Width * streamRequest.Height * 4);
                if (nativeSamples)
                {
                    if (sampleNativeResult.ReadbackWidth != streamRequest.Width || sampleNativeResult.ReadbackHeight != streamRequest.Height)
                        throw new InvalidDataException("Renderer did not confirm the native sample dimensions.");
                    manifest["native_sample_backend"] = sampleNativeResult.GpuSampled == true ? "gpu_box_mean" : "cpu_box_mean";
                    if (captured.Samples is not null)
                    {
                        captured.Samples["source_width"] = request.Width;
                        captured.Samples["source_height"] = request.Height;
                    }
                }
                if (sparseInput && (sampleNativeResult.OutputFrameStride != request.FrameSampleStride ||
                    sampleNativeResult.OutputFramePhase != job.OutputFramePhase))
                    throw new InvalidDataException("Renderer did not confirm the sparse frame selection.");
                if (!sampleNativeResult.Confirms(expectedNativeFrames))
                    throw new InvalidDataException("Renderer result did not confirm the requested frame sequence.");
                if (request.DeviceUuid is not null && !string.Equals(request.DeviceUuid,
                    sampleNativeResult.DeviceUuid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Renderer did not confirm the requested GPU UUID.");
                if (request.CaptureTarget is not null &&
                    string.IsNullOrWhiteSpace(sampleNativeResult.CaptureSource?.RenderTarget))
                    throw new InvalidDataException("Renderer did not confirm the actual local capture target; an older or incompatible renderer may have ignored it.");
                if (request.OrthographicCaptureViewport is not null && !JsonNode.DeepEquals(
                    sampleNativeResult.OrthographicCaptureViewport, JsonSerializer.SerializeToNode(request.OrthographicCaptureViewport, JsonOptions)))
                    throw new InvalidDataException("Renderer did not confirm the requested orthographic capture viewport.");
                if (request.LayerSelection is not null && !JsonNode.DeepEquals(sampleNativeResult.LayerSelection,
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
            var phase = new JsonObject();
            long phaseStart = Stopwatch.GetTimestamp();
            void Phase(string name) { phase[name] = Math.Round(Stopwatch.GetElapsedTime(phaseStart).TotalSeconds, 3); phaseStart = Stopwatch.GetTimestamp(); }
            if (request.GpuEncoding is not null)
            {
                manifest["encoder_command"] = new JsonObject { ["executable"] = tools.Renderer,
                    ["gpu_encode"] = JsonSerializer.SerializeToNode(job.GpuEncode, JsonOptions) };
                progress?.Report(new("rendering", 0, "Rendering and encoding on the same GPU."));
                var gpuProgress = new FrameProgressEstimate();
                long gpuStarted = Stopwatch.GetTimestamp();
                try
                {
                    _ = await RunTextAsync(tools.Renderer, ["render", "--job", jobPath],
                        Path.Combine(output, "renderer.stderr.log"), cancellationToken, line =>
                        {
                            const string prefix = "wpe-render: ";
                            int slash = line.IndexOf('/');
                            if (line.StartsWith(prefix, StringComparison.Ordinal) && slash > prefix.Length &&
                                ulong.TryParse(line.AsSpan(prefix.Length, slash - prefix.Length), out ulong done) &&
                                done > 0 && done <= request.Frames)
                                progress?.Report(gpuProgress.Update(done, request.Frames, Stopwatch.GetElapsedTime(gpuStarted).TotalSeconds));
                        });
                }
                catch (IOException error)
                {
                    throw RendererFailure(Path.Combine(renderDirectory, "result.json"), error.Message, error);
                }
                Phase("gpu_pipeline");
                File.Move(Path.Combine(renderDirectory, "gpu-video.mp4"), partialVideo);
                manifest["native_frame_transport"] = "gpu_nv12";
                manifest["pipe_rgba_bytes"] = 0;
            }
            else
            {
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
                        progress?.Report(new("finishing_encode", null, "Finishing the video encoding."));
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
                finally { await Task.WhenAll(StopAndWaitAsync(renderer), StopAndWaitAsync(encoder)); }
            }
            RenderResult nativeResult = RenderResult.Parse(await File.ReadAllTextAsync(Path.Combine(renderDirectory, "result.json"), cancellationToken));
            if (!nativeResult.Confirms(request.Frames))
                throw new InvalidDataException("Renderer result did not confirm the requested frame sequence.");
            manifest["native_result"] = nativeResult.Json;
            ConfirmRenderOptions(request, nativeResult);
            ulong gpuReadbacks = (ulong)(request.RetainFrames?.Length ?? 0) +
                (request.CollectAlphaBounds && request.RetainFrames?.Contains(0UL) != true ? 1UL : 0UL);
            if (request.GpuEncoding is { RetainLoopWindow: true } windowRequest)
                gpuReadbacks = 2UL * windowRequest.CrossfadeFrames + (ulong)(request.RetainFrames?.Count(
                    index => index >= windowRequest.CrossfadeFrames && index < encodedFrames) ?? 0);
            if (request.GpuEncoding is { } expectedGpu && (nativeResult.GpuEncoded != true ||
                nativeResult.ReadbackFrames != gpuReadbacks || nativeResult.GpuEncoder != expectedGpu.Codec ||
                nativeResult.GpuPackedAlpha != (request.PixelPacking == "rgba_side_by_side")))
                throw new InvalidDataException("Renderer did not confirm the GPU encoding path.");
            if (request.GpuEncoding is not null)
            {
                if (encodeSizeRequested && (encodeWidth != (uint)(request.GpuEncoding.Crop?.Width ?? (int)request.Width) ||
                    encodeHeight != (uint)(request.GpuEncoding.Crop?.Height ?? (int)request.Height)))
                {
                    JsonObject? resize = nativeResult.GpuCapture?.Resize;
                    if (resize is null || resize["width"]?.GetValue<uint>() != encodeWidth || resize["height"]?.GetValue<uint>() != encodeHeight ||
                        resize["filter"]?.GetValue<string>() != "lanczos3")
                        throw new InvalidDataException("Renderer did not confirm the requested Lanczos GPU resize.");
                    manifest["gpu_resize"] = resize.DeepClone();
                }
                if (request.GpuEncoding.CrossfadeFrames != 0 || request.GpuEncoding.Crop is not null)
                {
                    CacheRegion expectedCrop = request.GpuEncoding.Crop ?? new((int)request.Width, (int)request.Height,
                        0, 0, (int)request.Width, (int)request.Height);
                    if (!JsonNode.DeepEquals(nativeResult.GpuCapture?.Crop, JsonSerializer.SerializeToNode(expectedCrop, JsonOptions)))
                        throw new InvalidDataException("Renderer did not confirm the GPU crop.");
                    manifest["gpu_crop"] = nativeResult.GpuCapture!.Crop!.DeepClone();
                }
                if (request.GpuEncoding.CrossfadeFrames > 0)
                {
                    var crossfade = nativeResult.GpuCapture?.LoopCrossfade?.DeepClone().AsObject();
                    if (crossfade?["status"]?.GetValue<string>() != "applied" ||
                        crossfade["crossfade_frames"]?.GetValue<uint>() != request.GpuEncoding.CrossfadeFrames ||
                        crossfade["loop_frames"]?.GetValue<ulong>() != encodedFrames)
                        throw new InvalidDataException("Renderer did not confirm GPU loop blending and packet assembly.");
                    manifest["loop_crossfade"] = crossfade;
                }
                if (request.GpuEncoding.RetainLoopWindow)
                {
                    var window = nativeResult.GpuCapture?.LoopWindow?.DeepClone().AsObject()
                        ?? throw new InvalidDataException("Renderer omitted the original loop window.");
                    string path = Path.Combine(renderDirectory, "loop-window.rgba");
                    uint count = request.GpuEncoding.CrossfadeFrames;
                    if (window["width"]?.GetValue<uint>() != request.Width || window["height"]?.GetValue<uint>() != request.Height ||
                        window["crossfade_frames"]?.GetValue<uint>() != count || window["loop_frames"]?.GetValue<ulong>() != encodedFrames ||
                        window["frame_count"]?.GetValue<ulong>() != 2UL * count ||
                        (ulong)new FileInfo(path).Length != (ulong)request.Width * request.Height * 4 * count * 2)
                        throw new InvalidDataException("Original GPU loop window does not match the captured interval.");
                    window["path"] = path;
                    manifest["gpu_loop_window"] = window;
                }
                manifest["readback_frames"] = gpuReadbacks;
                manifest["readback_bytes"] = gpuReadbacks * request.Width * request.Height * 4 + (request.CollectAlphaBounds ? 32UL : 0UL);
                if (request.CollectAlphaBounds)
                {
                    var bounds = nativeResult.GpuCapture?.AlphaBounds?.DeepClone().AsObject()
                        ?? throw new InvalidDataException("Renderer omitted GPU coverage statistics.");
                    if (bounds["includes_rgb"]?.GetValue<bool>() != request.BoundsIncludeRgb ||
                        bounds["pixel_identical_in_generated_interval"] is null)
                        throw new InvalidDataException("Renderer did not confirm GPU coverage/identity semantics.");
                    string first = Path.Combine(renderDirectory, "first-frame.rgba");
                    if (new FileInfo(first).Length != (long)request.Width * request.Height * 4)
                        throw new InvalidDataException("First GPU reference frame is incomplete.");
                    bounds["first_frame_rgba_path"] = first;
                    manifest["alpha_bounds"] = bounds;
                }
                if (request.RetainFrames is { } retainedIndices)
                {
                    var retained = nativeResult.GpuCapture?.RetainedFrames?.DeepClone().AsObject()
                        ?? throw new InvalidDataException("Renderer omitted selected GPU reference frames.");
                    string path = Path.Combine(renderDirectory, RetainedFramesFile);
                    if (retained["width"]?.GetValue<uint>() != request.Width || retained["height"]?.GetValue<uint>() != request.Height ||
                        retained["encoded_frames"]?.GetValue<ulong>() != encodedFrames ||
                        !JsonNode.DeepEquals(retained["frame_indices"], JsonSerializer.SerializeToNode(retainedIndices)) ||
                        new FileInfo(path).Length != (long)request.Width * request.Height * 4 * retainedIndices.Length)
                        throw new InvalidDataException("GPU reference frame dimensions or sequence differ from the request.");
                    retained["path"] = path;
                    manifest["retained_frames"] = retained;
                }
            }
            ConfirmVideoRateOverrides(request, nativeResult);
            if (request.DeviceUuid is not null && !string.Equals(request.DeviceUuid, nativeResult.DeviceUuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Renderer did not confirm the requested GPU UUID.");
            if (request.CaptureTarget is not null && string.IsNullOrWhiteSpace(nativeResult.CaptureSource?.RenderTarget))
                throw new InvalidDataException("Renderer did not confirm the actual local capture target; an older or incompatible renderer may have ignored it.");
            if (request.OrthographicCaptureViewport is not null && !JsonNode.DeepEquals(nativeResult.OrthographicCaptureViewport, JsonSerializer.SerializeToNode(request.OrthographicCaptureViewport, JsonOptions)))
                throw new InvalidDataException("Renderer did not confirm the requested orthographic capture viewport.");
            if (request.LayerSelection is not null && !JsonNode.DeepEquals(nativeResult.LayerSelection, JsonSerializer.SerializeToNode(request.LayerSelection, JsonOptions)))
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
            // The input frame sequence and successful encoder exit are already known.
            // Check MP4 metadata; decode for counting only when its declaration is inconsistent.
            bool nativePacketCount = request.GpuEncoding is not null &&
                nativeResult.GpuCapture?.EncodedPackets == encodedFrames;
            string colorEntries = request.PlaybackEncoderKind is null && request.GpuEncoding is null ? "" : ",pix_fmt,color_space,color_range";
            string probeText = await RunTextAsync(tools.Ffprobe, ["-v", "error", "-threads", "4", "-select_streams", "v:0",
                "-show_entries", $"stream=codec_name,width,height,avg_frame_rate,nb_frames,duration,duration_ts,time_base{colorEntries}", "-of", "json", finalVideo],
                Path.Combine(output, "ffprobe.stderr.log"), cancellationToken);
            var probe = JsonNode.Parse(probeText)!.AsObject();
            var stream = probe["streams"]?.AsArray().Single()?.AsObject() ?? throw new InvalidDataException("Encoded video has no video stream.");
            var videoCount = await EncodedLoopValidator.FrameCountAsync(finalVideo, tools, stream, encodedFrames, cancellationToken);
            Phase(videoCount.Source == "full_decode" ? "ffprobe_count_fallback" : "ffprobe_header");
            var encodedRate = (stream["avg_frame_rate"]?.GetValue<string>() ?? "0/1").Split('/');
            if (stream["width"]?.GetValue<uint>() != encodedWidth || stream["height"]?.GetValue<uint>() != encodedHeight ||
                videoCount.Count != encodedFrames ||
                encodedRate.Length != 2 || !ulong.TryParse(encodedRate[0], out var rateNumerator) || !ulong.TryParse(encodedRate[1], out var rateDenominator) ||
                rateDenominator == 0 || (UInt128)rateNumerator * request.FpsDenominator != (UInt128)rateDenominator * request.FpsNumerator)
                throw new InvalidDataException("Encoded video dimensions, frame count or exact FPS differ from the request.");
            ConfirmEncodedDuration(stream, encodedFrames, request.FpsNumerator, request.FpsDenominator);
            manifest["frame_count_validation"] = new JsonObject {
                ["source"] = nativePacketCount && videoCount.Source == "container_header"
                    ? "container_header_and_native_encoder_packets" : videoCount.Source,
                ["full_decode_performed"] = videoCount.Source == "full_decode", ["fallback_reason"] = videoCount.FallbackReason };
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
            if (request.GpuEncoding is { CrossfadeFrames: > 0 })
                TemporaryCaptureFiles.Delete(manifest, output,
                    "native/gpu-video.mp4.partial.head.mp4", "native/gpu-video.mp4.partial.body.mp4");
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

    private static void ConfirmRenderOptions(RenderRequest request, RenderResult nativeResult)
    {
        if ((nativeResult.EffectRenderScale ?? 1.0) != request.EffectRenderScale)
            throw new InvalidDataException("Renderer did not confirm the requested effect resolution scale.");
        if ((nativeResult.MatchEffectResolution ?? false) != request.MatchEffectResolution)
            throw new InvalidDataException("Renderer did not confirm the requested adaptive effect resolution.");
    }

    private static void ConfirmVideoRateOverrides(RenderRequest request, RenderResult nativeResult)
    {
        if (request.OfflineVideoRateOverrides is null) return;
        if (nativeResult.RuntimeVideoRateOverrides is not JsonArray applied || applied.Count != request.OfflineVideoRateOverrides.Count)
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
            if (detail.Count > 0) return Classified(fallback + "\n" + string.Join("\n", detail), inner);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { }
        return Classified(fallback, inner);
    }

    /// <summary>渲染器报的是 GPU 编码初始化失败或缺 Vulkan 设备扩展时，归成 <see cref="GpuEncodeUnavailableException"/>。</summary>
    private static IOException Classified(string message, Exception? inner)
    {
        bool gpuUnavailable = message.Contains("GPU encode initialization", StringComparison.Ordinal) ||
            message.Contains("required vulkan device extension", StringComparison.Ordinal);
        return gpuUnavailable ? new GpuEncodeUnavailableException(message, inner) : new IOException(message, inner);
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
