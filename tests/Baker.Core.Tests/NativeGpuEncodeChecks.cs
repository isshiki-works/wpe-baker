using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class NativeGpuEncodeChecks
{
    internal static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("GPU encode check output must be new.");
        Directory.CreateDirectory(output);
        string root = Directory.GetCurrentDirectory();
        string portable = Path.Combine(root, "dist/progress-preview-20260919/WpeBaker");
        var tools = new NativeTools(Path.Combine(root, "build/native-local22/bin/wpe-render.exe"),
            Path.Combine(portable, "encoder/ffmpeg.exe"), Path.Combine(portable, "encoder/ffprobe.exe"),
            [Path.Combine(root, ".tools/llvm-mingw-22/bin"), Path.Combine(root, ".deps/ffmpeg-lgpl21/prefix/bin")]);
        string fixture = Path.Combine(root, "tests/fixtures/native/shader-clock");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string croppedFixture = Path.Combine(output, "fixture");
        using (var source = new ProjectSource(fixture)) await source.ExtractAsync(croppedFixture, timeout.Token);
        string shaderPath = Path.Combine(croppedFixture, "shaders/probe.frag");
        string shader = await File.ReadAllTextAsync(shaderPath);
        await File.WriteAllTextAsync(shaderPath, shader.Replace("vec2 p = v_TexCoord;",
            "vec2 p = v_TexCoord; if (p.x < 0.2 || p.x > 0.8 || p.y < 0.2 || p.y > 0.8) discard;", StringComparison.Ordinal)
            .Replace("gl_FragColor =", "c *= 0.25 + 0.75 * fract(g_Time); gl_FragColor =", StringComparison.Ordinal));
        // Non-macroblock-aligned extent and fractional FPS catch padded planes and last-packet duration errors.
        var request = new RenderRequest(croppedFixture, fixture, Path.Combine(output, "gpu"), 130, 98, 30000, 1001, 37,
            WarmupFrames: 7, Seed: 17, PixelPacking: "rgba_side_by_side",
            GpuEncoding: new(CrossfadeFrames: 6, Crop: new(130,98,16,10,100,78),RetainLoopWindow:true,RetainQualitySamples:true),
            CollectAlphaBounds: true, BoundsIncludeRgb: true, EncodedFrames: 31, RetainFrames: [0, 1, 30, 31, 36],
            LayerSelection: new([1], TransparentBackground: true, IncludePostprocessing: false));
        var runner = new NativeRenderRunner(tools);
        var gpu = await runner.RenderAsync(request, cancellationToken: timeout.Token);
        if (gpu["native_frame_transport"]?.GetValue<string>() != "gpu_nv12" ||
            gpu["readback_frames"]?.GetValue<ulong>() != 19 || gpu["pipe_rgba_bytes"]?.GetValue<int>() != 0 ||
            gpu["native_result"]?["written_frames"]?.GetValue<int>() != 37 ||
            Directory.EnumerateFiles(Path.Combine(output, "gpu/native"), "frames.rgba*").Any())
            throw new InvalidDataException("GPU encoding read unrequested frames or changed the requested timeline.");
        var reference = await runner.RenderAsync(request with { OutputDirectory = Path.Combine(output, "reference"),
            GpuEncoding = null, LosslessTest = true, EncodedFrames = null, ForceKeyFrameFrame = 6 }, cancellationToken: timeout.Token);
        if (reference["alpha_bounds"]!["minimum_alpha"]!.GetValue<int>() != 0 ||
            reference["alpha_bounds"]!["maximum_alpha"]!.GetValue<int>() != 255 ||
            reference["alpha_bounds"]!["width"]!.GetValue<int>() >= request.Width ||
            reference["alpha_bounds"]!["height"]!.GetValue<int>() >= request.Height)
            throw new InvalidDataException("GPU coverage fixture did not exercise a transparent cropped region.");
        foreach (string key in new[] { "has_content", "x", "y", "width", "height", "minimum_alpha", "maximum_alpha",
            "includes_rgb", "pixel_identical_in_generated_interval" })
            if (gpu["alpha_bounds"]![key]!.ToJsonString() != reference["alpha_bounds"]![key]!.ToJsonString())
                throw new InvalidDataException("GPU coverage reduction differs from full CPU scanning: " + key);
        foreach (ulong index in request.RetainFrames!)
        {
            byte[] actual = await LoopClosureCheck.ReadRetainedFrameAsync(gpu, index, timeout.Token);
            byte[] expected = await LoopClosureCheck.ReadRetainedFrameAsync(reference, index, timeout.Token);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Selected GPU original frame changed: " + index);
        }
        var gpuResidual=await runner.MeasureSeamResidualAsync(Path.Combine(output,"gpu"),31,6,timeout.Token);
        var cpuResidual=await runner.MeasureSeamResidualAsync(Path.Combine(output,"reference"),31,6,timeout.Token);
        if (!JsonNode.DeepEquals(gpuResidual["first_layer"],cpuResidual["first_layer"]))
            throw new InvalidDataException("GPU original loop window changed the residual decision.");
        await runner.ApplyLoopCrossfadeAsync(Path.Combine(output,"reference"),31,6,timeout.Token);
        string filter = "[0:v]split=2[c][a];[c]crop=100:78:16:10[rgb];[a]crop=100:78:146:10[alpha];" +
            "[rgb][alpha]hstack=inputs=2,scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuv420p[packed]";
        async Task<string> Metrics(string[] arguments)
        {
            var info = new ProcessStartInfo(tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in arguments) info.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = info };
            process.Start();
            using var cancelled = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            Task<string> stderr = process.StandardError.ReadToEndAsync(), stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            await stdout;
            string result = await stderr;
            if (process.ExitCode != 0) throw new InvalidDataException(result);
            return result;
        }
        string product = Path.Combine(output, "gpu/preview.mp4"), master = Path.Combine(output, "reference/preview.mp4");
        byte[] wrap = await LoopClosureCheck.ReadRetainedFrameAsync(gpu, 31, timeout.Token);
        JsonObject closure = LoopClosureCheck.Evaluate(await LoopClosureCheck.ReadRetainedFrameAsync(gpu, 0, timeout.Token),
            wrap, 130, 98, withAlpha: true, 31, judged: false);
        var loopReference = await EncodedLoopValidator.FromRetainedFramesAsync(gpu, new(130,98,16,10,100,78),
            true, 31, closure, wrap, timeout.Token);
        var detailedSeam = await EncodedLoopValidator.ValidateAsync(product, tools, 31, 30000, 1001, true,
            loopReference, cancellationToken: timeout.Token);
        var lightSeam = await EncodedLoopValidator.ValidateAsync(product, tools, 31, 30000, 1001, true,
            closure, 200, 78, cancellationToken: timeout.Token);
        foreach (string key in new[] {"status", "failures", "actual", "expected", "loop_closure"})
            if (!JsonNode.DeepEquals(detailedSeam[key], lightSeam[key]))
                throw new InvalidDataException("Optional seam diagnostics changed the verdict: " + key);
        if (lightSeam["reference_seam"]?["status"]?.GetValue<string>() != "not_requested" ||
            gpu["native_result"]?["audio_output_requested"]?.GetValue<bool>() != false)
            throw new InvalidDataException("Normal rendering produced unrequested seam or audio diagnostics.");
        await SeamPreviewChecks.RunAsync((condition, message) => { if (!condition) throw new InvalidDataException(message); });
        var withAudio = await runner.RenderAsync(request with {OutputDirectory=Path.Combine(output,"audio"), Frames=4,
            GpuEncoding=null, EncodedFrames=null, RetainFrames=null, CollectAlphaBounds=false, IncludeAudio=true},
            cancellationToken:timeout.Token);
        if (withAudio["native_result"]?["audio_output_requested"]?.GetValue<bool>() != true ||
            new FileInfo(Path.Combine(output,"audio/native/audio.f32le")).Length == 0)
            throw new InvalidDataException("Explicitly requested audio was not written.");
        ulong[] qualityFrames = PlaybackQualityGate.SampleFrames(31);
        string log = await Metrics(PlaybackQualityGate.MetricsArguments(product, master, filter, qualityFrames));
        await File.WriteAllTextAsync(Path.Combine(output, "quality.stderr.log"), log);
        double? ssim = PlaybackQualityGate.ParseSsim(log), psnr = PlaybackQualityGate.ParsePsnr(log);
        double seekSsim = 0, seekMse = 0;
        foreach (ulong frame in qualityFrames)
        {
            string sample = await Metrics(PlaybackQualityGate.SampleMetricArguments(product, master, filter, frame, 30000, 1001));
            seekSsim += PlaybackQualityGate.ParseSsim(sample) ?? throw new InvalidDataException("Missing sampled SSIM.");
            seekMse += Math.Pow(10, -(PlaybackQualityGate.ParsePsnr(sample) ?? throw new InvalidDataException("Missing sampled PSNR.")) / 10);
        }
        seekSsim /= qualityFrames.Length;
        double seekPsnr = -10 * Math.Log10(seekMse / qualityFrames.Length);
        if (ssim is null || psnr is null || Math.Abs(seekSsim - ssim.Value) > 0.000002 || Math.Abs(seekPsnr - psnr.Value) > 0.002)
            throw new InvalidDataException("Bounded quality seeks changed the selected-frame metrics.");
        if (gpu["frame_count_validation"]?["full_decode_performed"]?.GetValue<bool>() != false ||
            reference["frame_count_validation"]?["full_decode_performed"]?.GetValue<bool>() != false)
            throw new InvalidDataException("A completed native or software render was unnecessarily decoded for counting.");
        var fallback = await EncodedLoopValidator.FrameCountAsync(product, tools, new JsonObject { ["nb_frames"] = "999" }, 31, timeout.Token);
        if (fallback.Count != 31 || fallback.Source != "full_decode")
            throw new InvalidDataException("Inconsistent container metadata did not trigger the existing decode fallback.");
        if (!PlaybackQualityGate.Passes(ssim,
            PlaybackQualityGate.DefaultReferenceSsim, PlaybackQualityGate.DefaultRatio) || psnr is null or < 40)
            throw new InvalidDataException("GPU conversion/encoding differs excessively from the lossless reference; see quality log.");
        try
        {
            await runner.RenderAsync(request with { RequireOpaquePixels = true }, cancellationToken: timeout.Token);
            throw new InvalidDataException("Unsupported pixel consumer was silently ignored by GPU encoding.");
        }
        catch (ArgumentException) { }
        PlaybackEncodeProfileChecks.Run((condition, name) => { if (!condition) throw new InvalidDataException(name); });
        string qualityOutput = Path.Combine(output, "retained-quality");
        Directory.CreateDirectory(qualityOutput);
        JsonObject retainedQuality = await runner.GpuPlaybackQualityAsync(gpu, product, new(130,98,16,10,100,78),
            true, qualityOutput, timeout.Token);
        if (retainedQuality["passed"]?.GetValue<bool>() != true || retainedQuality["sampled_frames"]?.AsArray().Count != 9 ||
            Math.Abs(retainedQuality["measured_ssim"]!.GetValue<double>() - ssim!.Value) > 0.00001 ||
            retainedQuality["measured_psnr"]!.GetValue<double>() < 40)
            throw new InvalidDataException("Original GPU retained samples did not meet the playback quality criterion.");
        var resizeCrop = new CacheRegion(131, 99, 3, 5, 123, 91);
        var resizeRequest = request with {
            OutputDirectory = Path.Combine(output, "resize"), Width = 131, Height = 99, Frames = 32,
            WarmupFrames = 0, EncodedFrames = 31, RetainFrames = [0, 30, 31],
            EncodeWidth = 82, EncodeHeight = 68,
            GpuEncoding = new(Crop: resizeCrop, RetainQualitySamples: true) };
        JsonObject resized = await runner.RenderAsync(resizeRequest, cancellationToken: timeout.Token);
        if (resized["gpu_resize"]?["filter"]?.GetValue<string>() != "lanczos3" ||
            resized["encoded_stream"]?["streams"]?[0]?["width"]?.GetValue<int>() != 164 ||
            resized["encoded_stream"]?["streams"]?[0]?["height"]?.GetValue<int>() != 68 ||
            resized["alpha_bounds"]?["observed_frames"]?.GetValue<ulong>() != 31)
            throw new InvalidDataException("GPU resize lost the encoded extent or original-frame statistics contract.");
        string resizedQualityOutput = Path.Combine(output, "resize-quality");
        Directory.CreateDirectory(resizedQualityOutput);
        JsonObject resizeQuality = await runner.GpuPlaybackQualityAsync(resized,
            Path.Combine(output, "resize", "preview.mp4"), resizeCrop, true, resizedQualityOutput, timeout.Token);
        if (resizeQuality["passed"]?.GetValue<bool>() != true || resizeQuality["measured_psnr"]?.GetValue<double>() < 40)
            throw new InvalidDataException("GPU resize differs excessively from FFmpeg Lanczos with alpha packing.");
        // Resize must not change the original pixels retained for source-period closure.
        JsonObject resizedReference = await runner.RenderRawAsync(resizeRequest with {
            OutputDirectory = Path.Combine(output, "resize-original-reference"), GpuEncoding = null,
            Frames = 1, EncodeWidth = null, EncodeHeight = null, EncodedFrames = null, RetainFrames = null,
            CollectAlphaBounds = false }, timeout.Token);
        byte[] original = await File.ReadAllBytesAsync(resizedReference["rgba_path"]!.GetValue<string>(), timeout.Token);
        byte[] retainedOriginal = await LoopClosureCheck.ReadRetainedFrameAsync(resized, 0, timeout.Token);
        if (!original.AsSpan().SequenceEqual(retainedOriginal))
            throw new InvalidDataException("GPU resizing changed the original retained source frame.");
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), new JsonObject {
            ["status"] = "passed", ["frames"] = 37, ["fps_num"] = 30000, ["fps_den"] = 1001,
            ["encoded_frames"] = 31, ["full_frame_cpu_readbacks"] = 19, ["residual_decision_matches_lossless"]=true,
            ["gpu_crossfade_frames"] = 6, ["gpu_crop"] = gpu["gpu_crop"]!.DeepClone(),
            ["gpu_bounds_equal_cpu"] = true, ["retained_frames_identical"] = true,
            ["ssim"] = ssim, ["psnr"] = psnr, ["seek_ssim"] = seekSsim, ["seek_psnr"] = seekPsnr,
            ["bounded_quality_matches_full_decode"] = true, ["frame_count_fallback_verified"] = true,
            ["retained_quality"] = retainedQuality, ["resize_quality"] = resizeQuality,
            ["resize_metadata"] = resized["gpu_resize"]!.DeepClone(),
            ["encoded_stream"] = gpu["encoded_stream"]!.DeepClone()
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("GPU loop encode: crop, crossfade/remux order, selected raw frames, bounds and rational timeline passed.");
    }
}
