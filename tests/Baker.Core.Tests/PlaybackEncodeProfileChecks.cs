using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class PlaybackEncodeProfileChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Type profileType = typeof(NativeRenderRunner).Assembly.GetType("Baker.Core.PlaybackEncodeProfile")!;
        MethodInfo create = profileType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        MethodInfo outputArguments = profileType.GetMethod("OutputArguments", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        string Value(object profile, string property) => (string)profileType.GetProperty(property)!.GetValue(profile)!;
        string[] Arguments(object profile, string output) => (string[])outputArguments.Invoke(profile, [60u, 1u, output])!;
        object Create(uint width, uint height, bool lossless, string kind = PlaybackEncoderSelection.Software, int sizeOffset = 0) =>
            create.Invoke(null, [width, height, 60u, 1u, lossless, kind, sizeOffset])!;

        // 体积预算：所有档位的量化值一起抬（CRF/CQ/QP 加同一增量，MF 的 quality 每 +1 降 2）。
        check(Arguments(Create(1920, 1080, false, sizeOffset: 4), "x.mp4").SkipWhile(a => a != "-crf").Skip(1).First() == "20" &&
            Arguments(Create(1920, 1080, false, PlaybackEncoderSelection.Nvenc, 4), "x.mp4").SkipWhile(a => a != "-cq").Skip(1).First() == "26" &&
            Arguments(Create(1920, 1080, false, PlaybackEncoderSelection.Amf, 4), "x.mp4").SkipWhile(a => a != "-qp_p").Skip(1).First() == "20" &&
            Arguments(Create(1920, 1080, false, PlaybackEncoderSelection.Mf, 4), "x.mp4").SkipWhile(a => a != "-quality").Skip(1).First() == "72",
            "size budget: the quantizer offset raises CRF, NVENC CQ and AMF QP by the same amount and lowers MF quality by twice it");

        object h264 = Create(1920, 1080, false);
        check(Value(h264, "Encoder") == "libx264" && Value(h264, "Preset") == "fast" && Value(h264, "Crf") == "16" &&
            Value(h264, "PixelFormat") == "yuv420p" && Value(h264, "ColorFilter").Contains("bt709", StringComparison.Ordinal) &&
            Arguments(h264, "master.partial.mp4").SequenceEqual(["-c:v", "libx264", "-preset", "fast", "-crf", "16", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "-movflags", "+faststart", "master.partial.mp4"]),
            "playback H.264 profile preserves the existing ordinary master arguments");

        object hevc = Create(4097, 2, false);
        check(Value(hevc, "Encoder") == "libx265", "playback profile selects HEVC beyond the H.264 dimension limit");

        // 无损 master 的整条命令行钉死：ultrafast + crf 0 仍是 x264 的无损路径（解码像素逐位相同），
        // 但省掉 CABAC 与去块，编解码两头都便宜；master 是中间文件，不加 faststart。
        object masterLossless = Create(1920, 1080, true);
        check(Value(masterLossless, "Encoder") == "libx264rgb" && Value(masterLossless, "Preset") == "ultrafast" &&
            Value(masterLossless, "Crf") == "0" && Value(masterLossless, "PixelFormat") == "rgb24" &&
            Value(masterLossless, "ColorFilter") == "format=rgb24" &&
            Arguments(masterLossless, "master.partial.mp4").SequenceEqual(["-c:v", "libx264rgb", "-preset", "ultrafast", "-crf", "0", "-pix_fmt", "rgb24", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "master.partial.mp4"]) &&
            Arguments(h264, "cache.partial.mp4").SequenceEqual(Arguments(Create(1920, 1080, false), "cache.partial.mp4")),
            "lossless RGB master uses its compact lossless preset while cropped playback keeps its profile");

        // 无损 master 永远是软件编码，请求硬件档位也不能改动它。
        object masterUnderNvenc = Create(1920, 1080, true, PlaybackEncoderSelection.Nvenc);
        check(Value(masterUnderNvenc, "Encoder") == "libx264rgb" && Value(masterUnderNvenc, "Crf") == "0" &&
            Value(masterUnderNvenc, "Kind") == PlaybackEncoderSelection.Software &&
            Arguments(masterUnderNvenc, "master.partial.mp4").SequenceEqual(Arguments(masterLossless, "master.partial.mp4")),
            "requesting a hardware encoder never changes the lossless RGB master");

        // 硬件档位：编码器名、码率控制与像素格式，容器参数与软件档位保持一致。
        object nvencHevc = Create(4097, 2, false, PlaybackEncoderSelection.Nvenc);
        check(Value(nvencHevc, "Encoder") == "hevc_nvenc" && Value(nvencHevc, "PixelFormat") == "yuv420p" &&
            Arguments(nvencHevc, "cache.partial.mp4").SequenceEqual(["-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "22", "-b:v", "0", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "-movflags", "+faststart", "cache.partial.mp4"]),
            "nvenc playback profile maps HEVC to hevc_nvenc with constant-quality cq 22");

        object nvencH264 = Create(1920, 1080, false, PlaybackEncoderSelection.Nvenc);
        check(Value(nvencH264, "Encoder") == "h264_nvenc", "nvenc playback profile maps the H.264 selection to h264_nvenc");

        object qsv = Create(4097, 2, false, PlaybackEncoderSelection.Qsv);
        check(Value(qsv, "Encoder") == "hevc_qsv" && Value(qsv, "PixelFormat") == "nv12" &&
            Arguments(qsv, "cache.partial.mp4").SequenceEqual(["-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", "16", "-pix_fmt", "nv12", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "-movflags", "+faststart", "cache.partial.mp4"]),
            "qsv playback profile uses global_quality 16 on nv12");

        object amf = Create(4097, 2, false, PlaybackEncoderSelection.Amf);
        check(Value(amf, "Encoder") == "hevc_amf" && Value(amf, "PixelFormat") == "nv12" &&
            Arguments(amf, "cache.partial.mp4").SequenceEqual(["-c:v", "hevc_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", "16", "-qp_p", "16", "-qp_b", "16", "-pix_fmt", "nv12", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "-movflags", "+faststart", "cache.partial.mp4"]),
            "amf playback profile uses constant qp 16 on nv12");

        // 每个硬件档位在同一次生成里都要能覆盖 HEVC 与 H.264 两种选择。
        check(PlaybackEncoderSelection.RequiredEncoders(PlaybackEncoderSelection.Nvenc).SequenceEqual(["hevc_nvenc", "h264_nvenc"]) &&
            PlaybackEncoderSelection.RequiredEncoders(PlaybackEncoderSelection.Qsv).SequenceEqual(["hevc_qsv", "h264_qsv"]) &&
            PlaybackEncoderSelection.RequiredEncoders(PlaybackEncoderSelection.Amf).SequenceEqual(["hevc_amf", "h264_amf"]) &&
            PlaybackEncoderSelection.RequiredEncoders(PlaybackEncoderSelection.Software).Length == 0,
            "each hardware kind requires both its HEVC and H.264 encoders");

        // 真实的 ffmpeg -encoders 输出片段（含图例表头），只应解析出编码器名字。
        const string EncoderListing = """
            Encoders:
             V..... = Video
             A..... = Audio
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D libx265              libx265 H.265 / HEVC (codec hevc)
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
             V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
             A....D aac                  AAC (Advanced Audio Coding)
            """;
        var listed = PlaybackEncoderSelection.ParseEncoders(EncoderListing);
        check(listed.Contains("libx265") && listed.Contains("hevc_nvenc") && listed.Contains("h264_nvenc") &&
            listed.Contains("aac") && !listed.Contains("Encoders:") && !listed.Contains("=") && !listed.Contains("Video"),
            "ffmpeg encoder listing parses encoder names without the legend header");
        check(PlaybackEncoderSelection.ParseEncoders("").Count == 0, "an empty encoder listing yields no encoders");

        var withNvenc = PlaybackEncoderSelection.ParseEncoders(EncoderListing);
        var softwareOnly = PlaybackEncoderSelection.ParseEncoders(" V....D libx264  x\n V....D libx265  x");
        var qsvOnly = PlaybackEncoderSelection.ParseEncoders(" V....D hevc_qsv  x\n V....D h264_qsv  x");
        var amfOnly = PlaybackEncoderSelection.ParseEncoders(" V....D hevc_amf  x\n V....D h264_amf  x");
        var halfNvenc = PlaybackEncoderSelection.ParseEncoders(" V....D hevc_nvenc  x");

        check(PlaybackEncoderSelection.Resolve("software", softwareOnly) == (PlaybackEncoderSelection.Software, null) &&
            PlaybackEncoderSelection.Resolve("software", withNvenc) == (PlaybackEncoderSelection.Software, null),
            "software stays software and never probes for a hardware fallback reason");

        check(PlaybackEncoderSelection.Resolve("nvenc", withNvenc) == (PlaybackEncoderSelection.Nvenc, null) &&
            PlaybackEncoderSelection.Resolve("auto", withNvenc) == (PlaybackEncoderSelection.Nvenc, null) &&
            PlaybackEncoderSelection.Resolve("AUTO", withNvenc) == (PlaybackEncoderSelection.Nvenc, null),
            "auto and an explicit request both pick nvenc when both nvenc encoders exist");

        check(PlaybackEncoderSelection.Resolve("auto", qsvOnly).Used == PlaybackEncoderSelection.Qsv &&
            PlaybackEncoderSelection.Resolve("auto", amfOnly).Used == PlaybackEncoderSelection.Amf,
            "auto falls through the hardware order to the kind that is actually present");

        var autoNone = PlaybackEncoderSelection.Resolve("auto", softwareOnly);
        check(autoNone.Used == PlaybackEncoderSelection.Software && autoNone.FallbackReason is not null &&
            autoNone.FallbackReason.Contains("nvenc", StringComparison.Ordinal) &&
            autoNone.FallbackReason.Contains("qsv", StringComparison.Ordinal) &&
            autoNone.FallbackReason.Contains("amf", StringComparison.Ordinal),
            "auto without any hardware encoder falls back to software and names every kind it checked");

        var missingNvenc = PlaybackEncoderSelection.Resolve("nvenc", softwareOnly);
        check(missingNvenc.Used == PlaybackEncoderSelection.Software && missingNvenc.FallbackReason is not null &&
            missingNvenc.FallbackReason.Contains("hevc_nvenc", StringComparison.Ordinal) &&
            missingNvenc.FallbackReason.Contains("h264_nvenc", StringComparison.Ordinal),
            "an explicit unavailable request falls back to software and names the missing encoders");

        var partialNvenc = PlaybackEncoderSelection.Resolve("nvenc", halfNvenc);
        check(partialNvenc.Used == PlaybackEncoderSelection.Software &&
            partialNvenc.FallbackReason is not null &&
            partialNvenc.FallbackReason.Contains("h264_nvenc", StringComparison.Ordinal) &&
            !partialNvenc.FallbackReason.Contains("hevc_nvenc", StringComparison.Ordinal) &&
            PlaybackEncoderSelection.Resolve("auto", halfNvenc).Used == PlaybackEncoderSelection.Software,
            "a half-present hardware kind is refused and only the missing encoder is reported");

        bool rejected = false;
        try { PlaybackEncoderSelection.Normalize("vaapi"); } catch (ArgumentException) { rejected = true; }
        check(rejected && PlaybackEncoderSelection.Normalize(null) == PlaybackEncoderSelection.Auto &&
            PlaybackEncoderSelection.Normalize("  ") == PlaybackEncoderSelection.Auto &&
            PlaybackEncoderSelection.Normalize(" NVENC ") == PlaybackEncoderSelection.Nvenc,
            "encoder choices are normalized and unknown kinds are refused");

        // bake.json / bake 请求字段：snake_case 的 playback_encoder 必须能读进请求记录。
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var request = JsonSerializer.Deserialize<HybridBakeRequest>("""
            {"schema_version":2,"plan":{"kind":"hybrid_video"},"output_directory":"out","playback_encoder":"nvenc"}
            """, jsonOptions)!;
        check(request.PlaybackEncoder == "nvenc" &&
            JsonSerializer.Deserialize<HybridBakeRequest>("""
                {"schema_version":2,"plan":{"kind":"hybrid_video"},"output_directory":"out"}
                """, jsonOptions)!.PlaybackEncoder is null,
            "bake requests carry playback_encoder and default to no request at all");

        // bake.json 顶层 playback_encoder 汇总：字段名与合计值都是报告和界面读取的契约。
        JsonObject Group(string? used, double? seconds, long bytes, string? reason = null) => new()
        {
            ["video_bytes"] = bytes,
            ["playback_encode"] = used is null ? null : new JsonObject {
                ["encoder_used"] = used, ["encode_seconds"] = seconds, ["encoder_fallback_reason"] = reason },
        };
        var summary = PlaybackEncoderSelection.Summarize("auto",
            [Group("nvenc", 12.25, 1000), Group("nvenc", 3.5, 24), Group(null, null, 99)]);
        check(summary["requested"]!.GetValue<string>() == "auto" && summary["used"]!.GetValue<string>() == "nvenc" &&
            summary["fallback_reason"] is null && summary["encode_seconds_total"]!.GetValue<double>() == 15.75 &&
            summary["video_bytes_total"]!.GetValue<long>() == 1024,
            "bake.json playback_encoder sums only the groups that actually encoded a playback video");

        var fellBack = PlaybackEncoderSelection.Summarize("nvenc", [Group("software", 500.125, 7, "缺少 hevc_nvenc")]);
        check(fellBack["requested"]!.GetValue<string>() == "nvenc" && fellBack["used"]!.GetValue<string>() == "software" &&
            fellBack["fallback_reason"]!.GetValue<string>().Contains("hevc_nvenc", StringComparison.Ordinal) &&
            fellBack["encode_seconds_total"]!.GetValue<double>() == 500.125,
            "bake.json records the requested kind next to the software fallback and its reason");

        var mixed = PlaybackEncoderSelection.Summarize("auto", [Group("nvenc", 1, 1), Group("software", 2, 1)]);
        var staticOnly = PlaybackEncoderSelection.Summarize(null, [Group(null, null, 5)]);
        check(mixed["used"]!.GetValue<string>() == "mixed" && staticOnly["used"] is null &&
            staticOnly["requested"]!.GetValue<string>() == PlaybackEncoderSelection.Auto &&
            staticOnly["encode_seconds_total"]!.GetValue<double>() == 0 &&
            staticOnly["video_bytes_total"]!.GetValue<long>() == 0,
            "bake.json marks a disagreeing bake mixed and leaves an all-static bake without an encoder");

        // ---- mf 档位：厂商无关通道 ----
        MethodInfo escalate = profileType.GetMethod("Escalate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        object Escalate(object profile) => escalate.Invoke(profile, [])!;
        bool CanEscalate(object profile) => (bool)profileType.GetProperty("CanEscalate",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(profile)!;
        int Step(object profile) => (int)profileType.GetProperty("QualityStep")!.GetValue(profile)!;

        object mfHevc = Create(4097, 2, false, PlaybackEncoderSelection.Mf);
        check(Value(mfHevc, "Encoder") == "hevc_mf" && Value(mfHevc, "PixelFormat") == "nv12" &&
            Arguments(mfHevc, "cache.partial.mp4").SequenceEqual(["-c:v", "hevc_mf", "-hw_encoding", "true", "-rate_control", "quality", "-quality", "80", "-pix_fmt", "nv12", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "-movflags", "+faststart", "cache.partial.mp4"]),
            "mf playback profile uses MediaFoundation constant-quality 80 on nv12 with the shared container arguments");

        object mfH264 = Create(1920, 1080, false, PlaybackEncoderSelection.Mf);
        check(Value(mfH264, "Encoder") == "h264_mf" && Step(mfH264) == 0 && CanEscalate(mfH264),
            "mf playback profile maps the H.264 selection to h264_mf and starts at the lowest quality step");

        object masterUnderMf = Create(1920, 1080, true, PlaybackEncoderSelection.Mf);
        check(Value(masterUnderMf, "Encoder") == "libx264rgb" && Value(masterUnderMf, "Crf") == "0" &&
            Arguments(masterUnderMf, "master.partial.mp4").SequenceEqual(Arguments(masterLossless, "master.partial.mp4")),
            "requesting mf never changes the lossless RGB master");

        check(PlaybackEncoderSelection.RequiredEncoders(PlaybackEncoderSelection.Mf).SequenceEqual(["hevc_mf", "h264_mf"]) &&
            PlaybackEncoderSelection.Normalize(" MF ") == PlaybackEncoderSelection.Mf &&
            PlaybackEncoderSelection.Choices.Contains(PlaybackEncoderSelection.Mf),
            "mf is a first-class choice requiring both its MediaFoundation encoders");

        var mfOnly = PlaybackEncoderSelection.ParseEncoders(" V....D hevc_mf  x\n V....D h264_mf  x");
        var mfAndNvenc = PlaybackEncoderSelection.ParseEncoders(
            " V....D hevc_mf  x\n V....D h264_mf  x\n V....D hevc_nvenc  x\n V....D h264_nvenc  x");
        check(PlaybackEncoderSelection.Resolve("auto", mfOnly).Used == PlaybackEncoderSelection.Mf &&
            PlaybackEncoderSelection.Resolve("auto", mfAndNvenc).Used == PlaybackEncoderSelection.Mf &&
            PlaybackEncoderSelection.Resolve("nvenc", mfAndNvenc).Used == PlaybackEncoderSelection.Nvenc,
            "auto prefers the vendor-neutral mf channel over nvenc while an explicit nvenc request is still honoured");

        var autoNames = PlaybackEncoderSelection.Resolve("auto", softwareOnly);
        check(autoNames.FallbackReason!.Contains("mf", StringComparison.Ordinal),
            "the auto fallback reason names mf among the kinds it checked");

        // ---- 质量升档阶梯 ----
        object mfStep1 = Escalate(mfHevc), mfStep2 = Escalate(mfStep1);
        check(Arguments(mfStep1, "o.mp4")[7] == "90" && Arguments(mfStep2, "o.mp4")[7] == "96" &&
            Step(mfStep2) == 2 && !CanEscalate(mfStep2) && CanEscalate(mfStep1),
            "mf escalates along its own 80/90/96 quality ladder and stops at the top");

        object swStep1 = Escalate(Create(1920, 1080, false)), swStep2 = Escalate(swStep1);
        check(Arguments(swStep1, "o.mp4").SequenceEqual(["-c:v", "libx264", "-preset", "fast", "-crf", "13", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "-movflags", "+faststart", "o.mp4"]) &&
            Arguments(swStep2, "o.mp4")[5] == "10" && Arguments(Escalate(swStep2), "o.mp4")[5] == "10",
            "the quantizer ladder steps 16/13/10 and clamps at its floor");

        object nvStep1 = Escalate(Create(1920, 1080, false, PlaybackEncoderSelection.Nvenc));
        object amfStep1 = Escalate(Create(4097, 2, false, PlaybackEncoderSelection.Amf));
        check(Arguments(nvStep1, "o.mp4")[7] == "19" &&
            Arguments(amfStep1, "o.mp4").SequenceEqual(["-c:v", "hevc_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", "13", "-qp_p", "13", "-qp_b", "13", "-pix_fmt", "nv12", "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", "-movflags", "+faststart", "o.mp4"]),
            "the vendor tiers escalate their own quantizer options together");

        // ---- 画质判据 ----
        ulong[] sampled = QualityGate.SampleFrames(3372);
        check(sampled.Length >= QualityGate.MinimumSamples && sampled[0] == 0 && sampled[^1] == 3371 &&
            sampled.Distinct().Count() == sampled.Length && sampled.SequenceEqual(sampled.OrderBy(n => n)),
            "the quality gate samples at least nine ascending distinct frames including the first and the last");

        check(QualityGate.SampleFrames(4).SequenceEqual([0ul, 1ul, 2ul, 3ul]) &&
            QualityGate.SampleFrames(0).Length == 0 &&
            QualityGate.SampleFrames(1).SequenceEqual([0ul]) &&
            QualityGate.SampleFrames(3372, 20).Length == 20,
            "a short clip samples every frame, an empty clip samples nothing and a larger request is honoured");

        const string SsimLog = """
            [Parsed_ssim_4 @ 0] n:1 Y:0.9 U:0.9 V:0.9 All:0.900000 (10.000000)
            [Parsed_ssim_4 @ 0] SSIM Y:0.996 (24.1) U:0.998 (27.0) V:0.998 (27.2) All:0.996800 (24.949)
            """;
        const string PsnrLog = """
            [Parsed_psnr_5 @ 0] n:1 psnr_avg:30.00
            [Parsed_psnr_5 @ 0] PSNR y:44.12 u:48.30 v:48.55 average:45.21 min:40.01 max:50.12
            """;
        check(FfmpegQualityComparer.ParseSsim(SsimLog) == 0.9968 && FfmpegQualityComparer.ParsePsnr(PsnrLog) == 45.21 &&
            FfmpegQualityComparer.ParseSsim("nothing here") is null && FfmpegQualityComparer.ParsePsnr("") is null &&
            FfmpegQualityComparer.ParsePsnr("PSNR average:inf") == double.PositiveInfinity,
            "the gate reads the summary SSIM and PSNR lines rather than the per-frame ones");

        double threshold = QualityGate.Threshold(0.9948, 0.98);
        check(Math.Abs(threshold - 0.9948 * 0.98) < 1e-12 &&
            QualityGate.Passes(0.9948, 0.9948, 0.98) && QualityGate.Passes(threshold, 0.9948, 0.98) &&
            !QualityGate.Passes(threshold - 1e-6, 0.9948, 0.98) && !QualityGate.Passes(null, 0.9948, 0.98),
            "the gate threshold is the reference SSIM times the ratio and an unmeasurable SSIM never passes");

        string[] metricArguments = FfmpegQualityComparer.MasterArguments("cache.partial.mp4", "preview.mp4",
            "[0:v]crop=8:8:0:0,format=yuv420p[packed]", [0ul, 5ul]);
        string graph = metricArguments[Array.IndexOf(metricArguments, "-filter_complex") + 1];
        check(graph.StartsWith("[1:v]select='eq(n\\,0)+eq(n\\,5)',setpts=N/TB,crop=8:8:0:0", StringComparison.Ordinal) &&
            graph.Contains("[gatemaster]", StringComparison.Ordinal) &&
            graph.Contains("[0:v]select='eq(n\\,0)+eq(n\\,5)',setpts=N/TB[gateproduct]", StringComparison.Ordinal) &&
            !graph.Contains("[packed]", StringComparison.Ordinal) && !graph.Contains("[0:v]crop", StringComparison.Ordinal),
            "the compare graph pushes the master through the same crop filter and samples both sides identically");

        check(metricArguments[..7].SequenceEqual(["-hide_banner", "-nostdin", "-nostats", "-i", "cache.partial.mp4", "-i", "preview.mp4"]),
            "the gate measures the product against the master and discards the decoded output");

        var accepted = QualityGate.Summarize([0ul, 9ul], 0.9948, 0.98, 0.9950, 45.2, 0,
            QualityGate.ActionAccepted);
        check(accepted["metric"]!.GetValue<string>() == QualityGate.MetricName &&
            accepted["sampled_frames"]!.AsArray().Count == 2 && accepted["passed"]!.GetValue<bool>() &&
            accepted["action"]!.GetValue<string>() == QualityGate.ActionAccepted &&
            accepted["quality_step"]!.GetValue<int>() == 0 &&
            accepted["measured_ssim"]!.GetValue<double>() == 0.995 &&
            accepted["reference_ssim"]!.GetValue<double>() == 0.9948 &&
            accepted["threshold"]!.GetValue<double>() == Math.Round(threshold, 6),
            "bake.json records the gate metric, sampling, reference, threshold and verdict");

        var fellBackGate = QualityGate.Summarize([0ul], 0.9948, 0.98, 0.90, double.PositiveInfinity, 2,
            QualityGate.ActionFellBack, "回退软件编码");
        check(!fellBackGate["passed"]!.GetValue<bool>() &&
            fellBackGate["action"]!.GetValue<string>() == QualityGate.ActionFellBack &&
            fellBackGate["measured_psnr"] is null && fellBackGate["measured_psnr_infinite"]!.GetValue<bool>() &&
            fellBackGate["note"]!.GetValue<string>() == "回退软件编码" &&
            QualityGate.Summarize([0ul], 0.9948, 0.98, null, null, 1,
                QualityGate.ActionEscalated)["measured_ssim"] is null,
            "a failed gate is recorded as not passed with its reason and an infinite PSNR is flagged rather than rounded");
    }
}
