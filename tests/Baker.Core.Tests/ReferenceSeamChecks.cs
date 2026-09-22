using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 源周期闭合检验与以原作为参照的编码后接缝校验。纯函数部分不启动任何进程；端到端部分用自带 ffmpeg 造合成 master
/// 与成品（与产品同一套无损/播放编码参数），覆盖：闭合且原作自身有切口 → 通过、参照差分恰为编码噪声；差一帧 → 拒；
/// 特效前缀路线（缩放、补边、透明打包）同样生效。
/// </summary>
internal static class ReferenceSeamChecks
{
    private const int Width = 192, Height = 128;
    private const ulong Period = 30;

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        PureChecks(check);
        await RenderStreamChecksAsync(check, root);
        NativeTools? tools = FindTools();
        check(tools is not null, "参照式接缝校验的端到端检查找到了自带 ffmpeg");
        string directory = Path.Combine(root, "reference-seam");
        Directory.CreateDirectory(directory);
        await HybridRouteAsync(check, tools!, directory);
        await EffectPrefixRouteAsync(check, tools!, directory);
        await DirectPlaybackRouteAsync(check, tools!, directory);
    }

    // ---- 合成内容：30 fps 的条纹在 60 fps 下每个内容帧重复两次，周期末尾跳回起点（原作自身的切口）。
    private static byte[] Rgba(ulong content, bool transparent = false, byte garbage = 0)
    {
        byte[] frame = new byte[Width * Height * 4];
        int bar = (int)(content / 2) * 6;
        for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x)
            {
                int p = (y * Width + x) * 4;
                bool inBar = x >= bar && x < bar + 24;
                frame[p] = inBar ? (byte)230 : (byte)((x * 7 + y * 3) & 0xFF);
                frame[p + 1] = inBar ? (byte)80 : (byte)((x * 2 + y * 5) & 0xFF);
                frame[p + 2] = inBar ? (byte)40 : (byte)(90 + (x ^ y) % 60);
                bool opaque = !transparent || (x >= 24 && x < Width - 24 && y >= 16 && y < Height - 16);
                frame[p + 3] = opaque ? (byte)255 : (byte)0;
                if (!opaque) frame[p] = frame[p + 1] = frame[p + 2] = garbage;
            }
        return frame;
    }

    private static void PureChecks(Action<bool, string> check)
    {
        byte[] start = Rgba(0), closed = Rgba(Period % Period), shifted = Rgba(Period % (Period + 2));
        JsonObject same = LoopClosureCheck.Evaluate(start, closed, Width, Height, false, Period, judged: true);
        check(same["status"]!.GetValue<string>() == LoopClosureCheck.ClosedStatus && same["pixel_identical"]!.GetValue<bool>() &&
            same["rgb"]!["tile_64"]!["worst"]!.GetValue<double>() == 0 && same["alpha"] is null,
            "闭合检验：第 P 帧与第 0 帧逐像素相同时 closure = 0 并放行");
        // 内容周期实际是 P+2（条纹要多走一个内容帧才回原位），拿 P 当周期时第 P 帧差一个内容帧。
        JsonObject offByOne = LoopClosureCheck.Evaluate(start, shifted, Width, Height, false, Period, judged: true);
        check(offByOne["status"]!.GetValue<string>() == LoopClosureCheck.NotClosedStatus &&
            offByOne["rgb"]!["tile_64"]!["worst"]!.GetValue<double>() > LoopClosureCheck.MaximumTileMae255 &&
            !LoopClosureCheck.Allows(offByOne),
            "闭合检验：差一个内容帧的周期被拒，并报出最差瓦片");

        // 8 位取整边界：整幅每个通道差 1 → 瓦片 MAE 正好 1.0，放行；某块多出一级 → 超过 1.0，拒。
        byte[] unsaturated = start.ToArray();
        for (int p = 0; p < unsaturated.Length; p += 4) for (int channel = 0; channel < 3; ++channel) unsaturated[p + channel] = Math.Min((byte)250, unsaturated[p + channel]);
        byte[] plusOne = unsaturated.ToArray();
        for (int p = 0; p < plusOne.Length; p += 4) for (int channel = 0; channel < 3; ++channel) plusOne[p + channel] += 1;
        JsonObject rounding = LoopClosureCheck.Evaluate(unsaturated, plusOne, Width, Height, false, Period, true);
        check(rounding["status"]!.GetValue<string>() == LoopClosureCheck.ClosedStatus && rounding["rgb"]!["tile_64"]!["worst"]!.GetValue<double>() == 1.0,
            "闭合检验：逐像素 1/255 的取整差（瓦片 MAE 正好 1.0）仍算闭合");
        byte[] plusTwo = plusOne.ToArray();
        for (int y = 0; y < 64; ++y) for (int x = 0; x < 64; ++x) plusTwo[(y * Width + x) * 4] += 3;
        JsonObject overRounding = LoopClosureCheck.Evaluate(unsaturated, plusTwo, Width, Height, false, Period, true);
        check(overRounding["status"]!.GetValue<string>() == LoopClosureCheck.NotClosedStatus &&
            overRounding["rgb"]!["tile_64"]!["worst_x"]!.GetValue<int>() == 0 && overRounding["rgb"]!["tile_64"]!["worst_y"]!.GetValue<int>() == 0,
            "闭合检验：一块瓦片超过取整余量就拒，并点名该瓦片");

        // 透明组：alpha = 0 处 RGB 没有定义，渲染器填什么都不算不闭合；alpha 自己变了才算。
        JsonObject garbage = LoopClosureCheck.Evaluate(Rgba(0, true, 11), Rgba(Period % Period, true, 200), Width, Height, true, Period, true);
        check(garbage["status"]!.GetValue<string>() == LoopClosureCheck.ClosedStatus && !garbage["pixel_identical"]!.GetValue<bool>() &&
            garbage["alpha"]!["tile_64"]!["worst"]!.GetValue<double>() == 0,
            "闭合检验：透明组 alpha = 0 处的 RGB 差不计入");
        check(LoopClosureCheck.Evaluate(Rgba(0, true, 11), Rgba(Period % Period, true, 200), Width, Height, false, Period, true)["status"]!
                .GetValue<string>() == LoopClosureCheck.NotClosedStatus,
            "闭合检验（变异对照）：不按 alpha 掩码时同一对帧会被误拒");
        byte[] alphaChanged = Rgba(Period % Period, true);
        for (int y = 16; y < 80; ++y) for (int x = 24; x < 88; ++x) alphaChanged[(y * Width + x) * 4 + 3] = 128;
        check(LoopClosureCheck.Evaluate(Rgba(0, true), alphaChanged, Width, Height, true, Period, true)["status"]!.GetValue<string>() ==
            LoopClosureCheck.NotClosedStatus, "闭合检验：透明组 alpha 本身不闭合时被拒");
        JsonObject notJudged = LoopClosureCheck.Evaluate(start, shifted, Width, Height, false, Period, judged: false);
        check(notJudged["status"]!.GetValue<string>() == LoopClosureCheck.NotJudgedStatus && LoopClosureCheck.Allows(notJudged) &&
            !notJudged["within_limit"]!.GetValue<bool>(),
            "闭合检验：残差掩盖路线只记录硬切残差，不据此拒绝");
        check(LoopClosureCheck.ReferenceFrameIndices(1).SequenceEqual([0UL, 1UL]) && LoopClosureCheck.ReferenceFrameIndices(Period).SequenceEqual([0UL, Period - 1, Period]),
            "闭合检验：主渲染留下第 0、P−1、P 帧，P = 1 时去重");

        byte[] layout = LoopClosureCheck.EncodedLayout(Rgba(0, true), Width, Height, 24, 16, 8, 4, packedAlpha: true);
        check(layout.Length == 8 * 2 * 4 * 3 && layout[0] == Rgba(0, true)[(16 * Width + 24) * 4] && layout[8 * 3] == 255 &&
            layout[8 * 3 + 1] == 255 && layout[8 * 3 + 2] == 255,
            "测量布局：透明组裁剪后左 RGB 右 alpha（alpha 复制到三个通道）");

        // 差分图是带符号相减之后再取绝对值：成品那一步与原作那一步完全相同就抵消为 0，哪怕这一步是切口。
        byte[] a = Rgb(Rgba(0)), b = Rgb(Rgba(Period - 1)), c = Rgb(Rgba(Period % Period)), d = Rgb(Rgba(Period - 1));
        double[] identical = LoopSeamMetrics.DifferenceMap(a, b, c, d, Width, Height, 64);
        double[] step = LoopSeamMetrics.TileMae(a, b, Width, Height, 64);
        check(identical.All(value => value == 0) && step.Max() > 10,
            "差分图：成品接缝一步与原作同一步完全一致时差分为 0，即使这一步本身是原作的大切口");

        var seam = new JsonObject
        {
            ["failures"] = new JsonArray("loop_not_closed", "frame_count_mismatch"),
            ["loop_closure"] = offByOne, ["actual"] = new JsonObject { ["decoded_frame_count"] = 29 },
            ["expected"] = new JsonObject { ["frames"] = Period }
        };
        // 直编判定：只有不透明、不含残差层、不是探针的组能在渲染那一遍直接编成品。
        check(HybridBakeService.AllowsDirectPlayback(true, false, 0) &&
            !HybridBakeService.AllowsDirectPlayback(false, false, 0) &&
            !HybridBakeService.AllowsDirectPlayback(true, true, 0) &&
            !HybridBakeService.AllowsDirectPlayback(true, false, 120) &&
            !HybridBakeService.AllowsDirectPlayback(false, true, 0),
            "直编判定：不透明 + 非残差 + 非探针才直编，其余仍走无损 master");

        string zh = EncodedLoopValidator.RejectionDetail(seam, MessageCatalog.Chinese), en = EncodedLoopValidator.RejectionDetail(seam, MessageCatalog.English);
        check(zh.Contains("第 30 帧", StringComparison.Ordinal) && zh.Contains("1.0/255", StringComparison.Ordinal) &&
            !zh.Contains("alpha", StringComparison.Ordinal) && !en.Contains("alpha", StringComparison.Ordinal) &&
            zh.Contains("成品解码出 29 帧，应为 30 帧", StringComparison.Ordinal) &&
            en.Contains("frame 30", StringComparison.Ordinal) && en.Contains("decodes to 29 frames instead of 30", StringComparison.Ordinal),
            "拒绝理由：中英文都点名闭合读数与帧数");
    }

    private static async Task RenderStreamChecksAsync(Action<bool, string> check, string root)
    {
        const int w = 2, h = 2;
        byte[] Solid(byte value) => Enumerable.Repeat(new byte[] { value, value, value, 255 }, w * h).SelectMany(x => x).ToArray();
        byte[] stream = [.. Solid(10), .. Solid(10), .. Solid(10), .. Solid(99)];
        string output = Path.Combine(root, "render-encoded-prefix");
        Directory.CreateDirectory(output);
        var request = new RenderRequest(root, root, output, w, h, 60, 1, 4, CollectAlphaBounds: true, EncodedFrames: 3, RetainFrames: [0, 2, 3]);
        MethodInfo method = typeof(NativeRenderRunner).GetMethod("CopyFrameStreamAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var source = new MemoryStream(stream, writable: false);
        using var destination = new MemoryStream();
        var task = (Task)method.Invoke(null, [source, destination, request, output, null, CancellationToken.None, false])!;
        await task;
        object summary = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var bounds = (JsonObject)summary.GetType().GetProperty("Bounds")!.GetValue(summary)!;
        var retained = (JsonObject)summary.GetType().GetProperty("Retained")!.GetValue(summary)!;
        byte[] kept = await File.ReadAllBytesAsync(retained["path"]!.GetValue<string>());
        check(destination.ToArray().SequenceEqual(stream[..(3 * w * h * 4)]) && bounds["pixel_identical_in_generated_interval"]!.GetValue<bool>(),
            "多渲一帧：编码器只收到前 P 帧，静止判定也只看这 P 帧");
        check(kept.SequenceEqual([.. Solid(10), .. Solid(10), .. Solid(99)]) &&
            retained["frame_indices"]!.AsArray().Select(x => x!.GetValue<ulong>()).SequenceEqual([0UL, 2UL, 3UL]),
            "多渲一帧：第 0、P−1、P 帧原帧按帧号顺序逐字节留下");
        var manifest = new JsonObject { ["retained_frames"] = retained.DeepClone() };
        byte[] frameP = await LoopClosureCheck.ReadRetainedFrameAsync(manifest, 3);
        check(frameP.SequenceEqual(Solid(99)), "多渲一帧：按帧号读回留下的第 P 帧");

        var runner = new NativeRenderRunner(new(Path.Combine(root, "missing-renderer"), Path.Combine(root, "missing-ffmpeg"),
            Path.Combine(root, "missing-ffprobe"), []));
        foreach ((RenderRequest invalid, string name) in new[]
        {
            (request with { EncodedFrames = 5 }, "编码帧数超过渲染帧数时拒绝请求"),
            (request with { RetainFrames = [3, 0] }, "留原帧的帧号必须严格递增"),
            (request with { RetainFrames = [4] }, "留原帧的帧号必须在渲染范围内"),
            (request with { ForceKeyFrameFrame = 3 }, "强制关键帧必须落在编码的前 P 帧里"),
            (request with { PlaybackEncoderKind = PlaybackEncoderSelection.Software, LosslessTest = true },
                "直编成品与无损 master 互斥"),
            (request with { PlaybackEncoderKind = PlaybackEncoderSelection.Software, PixelPacking = "rgba_side_by_side" },
                "直编成品只支持不透明 rgb 打包"),
            (request with { PlaybackEncoderKind = PlaybackEncoderSelection.Auto },
                "直编成品要的是已解析好的档位，不是 auto"),
            (request with { PlaybackEncoderKind = "nvidia" }, "直编成品的档位必须是已知取值")
        })
        {
            try { await runner.RenderAsync(invalid); }
            catch (ArgumentException) { check(true, name); continue; }
            throw new InvalidOperationException("Accepted invalid render request: " + name);
        }
    }

    private static async Task HybridRouteAsync(Action<bool, string> check, NativeTools tools, string directory)
    {
        // 闭合：内容周期正好 P；原作在 P−1 → P 处跳回起点（自身切口）。成品按播放档编码，第 10、20 帧强制 IDR。
        string master = Path.Combine(directory, "hybrid-master");
        byte[][] rendered = Enumerable.Range(0, (int)Period + 1).Select(n => Rgba((ulong)n % Period)).ToArray();
        await WriteMasterAsync(tools, master, rendered[..(int)Period], Period + 1);
        string encoded = Path.Combine(directory, "hybrid-cache.mp4");
        await EncodePlaybackAsync(tools, Path.Combine(master, "preview.mp4"), encoded, null);
        var crop = new CacheRegion(Width, Height, 0, 0, Width, Height);
        JsonObject closure = LoopClosureCheck.Evaluate(rendered[0], rendered[Period], Width, Height, false, Period, true);
        LoopReference reference = await EncodedLoopValidator.FromLosslessMasterAsync(tools, master, crop, false, Period, 0, closure, rendered[Period]);
        JsonObject pass = await EncodedLoopValidator.ValidateAsync(encoded, tools, Period, 60, 1, false, reference);
        JsonObject rgb = pass["reference_seam"]!["rgb"]!["tile_64"]!.AsObject();
        double Worst(JsonObject plane, string key) => plane[key]!["worst"]!.GetValue<double>();
        check(pass["status"]!.GetValue<string>() == "observed_seam_pass" && pass["failures"]!.AsArray().Count == 0 &&
            pass["loop_closure"]!["status"]!.GetValue<string>() == LoopClosureCheck.ClosedStatus,
            "分组路线：闭合、原作自身有切口的循环通过参照式校验");
        check(pass["actual"]!["frame_count_source"]!.GetValue<string>() == "container_header" &&
            pass["actual"]!["frame_count_fallback_reason"] is null &&
            pass["actual"]!["container_declared_frame_count"]!.GetValue<string>() == Period.ToString() &&
            pass["actual"]!["decoded_frame_count"]!.GetValue<ulong>() == Period,
            "分组路线：帧数对得上时只读容器头，不再全解码计帧");
        check(Worst(rgb, "reference_step") > 20 && Worst(rgb, "encoded_seam_step") > 20 && Worst(rgb, "wrap_term") == 0 &&
            Worst(rgb, "difference_map") < Worst(rgb, "reference_step") / 4,
            "分组路线：原作切口出现在参照步进里，成品接缝同样大，差分图远小于切口本身");
        check(JsonNode.DeepEquals(rgb["difference_map"], rgb["encoding_noise_seam"]) &&
            rgb["bound_excess_maximum"]!.GetValue<double>() <= 1e-9 &&
            Worst(rgb, "difference_map") <= Worst(rgb, "encoding_error_first") + Worst(rgb, "encoding_error_last") + 1e-9,
            "分组路线：闭合时参照差分图逐瓦片恰等于编码噪声，且不超过两帧编码误差之和");

        // 无损对照：把 crf 0 的 master 本身当成品校验，编码误差与差分图都必须恰为 0。
        JsonObject lossless = await EncodedLoopValidator.ValidateAsync(Path.Combine(master, "preview.mp4"), tools, Period, 60, 1, false, reference);
        JsonObject losslessRgb = lossless["reference_seam"]!["rgb"]!["tile_64"]!.AsObject();
        check(lossless["status"]!.GetValue<string>() == "observed_seam_pass" && Worst(losslessRgb, "difference_map") == 0 &&
            Worst(losslessRgb, "encoding_error_first") == 0 && Worst(losslessRgb, "encoding_error_last") == 0 &&
            Worst(losslessRgb, "encoded_seam_step") == Worst(losslessRgb, "reference_step"),
            "分组路线：crf 0 成品上编码误差为 0、差分图为 0，成品接缝一步正好等于原作那一步");

        JsonObject wrongCount = await EncodedLoopValidator.ValidateAsync(encoded, tools, Period + 1, 60, 1, false, reference);
        check(wrongCount["status"]!.GetValue<string>() == "observed_seam_fail" &&
            wrongCount["failures"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["frame_count_mismatch"]),
            "分组路线：成品帧数与周期不符时被拒");
        check(wrongCount["actual"]!["frame_count_source"]!.GetValue<string>() == "full_decode" &&
            wrongCount["actual"]!["frame_count_fallback_reason"]!.GetValue<string>().Contains("counted by decoding", StringComparison.Ordinal) &&
            wrongCount["actual"]!["decoded_frame_count"]!.GetValue<ulong>() == Period,
            "分组路线：容器头与期望帧数不一致时回退全解码确认，再按解码读数裁决");

        // 差一帧：内容周期实际是 P+2，拿 P 当周期；渲染器连续播放到第 P 帧时没有回到起点。
        string openMaster = Path.Combine(directory, "hybrid-master-open");
        byte[][] open = Enumerable.Range(0, (int)Period + 1).Select(n => Rgba((ulong)n % (Period + 2))).ToArray();
        await WriteMasterAsync(tools, openMaster, open[..(int)Period], Period + 1);
        string openEncoded = Path.Combine(directory, "hybrid-cache-open.mp4");
        await EncodePlaybackAsync(tools, Path.Combine(openMaster, "preview.mp4"), openEncoded, null);
        JsonObject openClosure = LoopClosureCheck.Evaluate(open[0], open[Period], Width, Height, false, Period, true);
        JsonObject rejected = await EncodedLoopValidator.ValidateAsync(openEncoded, tools, Period, 60, 1, false,
            await EncodedLoopValidator.FromLosslessMasterAsync(tools, openMaster, crop, false, Period, 0, openClosure, open[Period]));
        check(rejected["status"]!.GetValue<string>() == "observed_seam_fail" &&
            rejected["failures"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["loop_not_closed"]) &&
            rejected["reference_seam"]!["rgb"]!["tile_64"]!["wrap_term"]!["worst"]!.GetValue<double>() > LoopClosureCheck.MaximumTileMae255,
            "分组路线：差一帧的周期被闭合检验拒绝，参照记录里的 m[0]−f[P] 项同样非零");
    }

    private static async Task EffectPrefixRouteAsync(Action<bool, string> check, NativeTools tools, string directory)
    {
        // 特效前缀：源 192×128 带透明，缩到 96×64，补边到 128×96（偏移 16,16），左右并排透明打包，直接有损编码。
        const int encodeWidth = 96, encodeHeight = 64;
        var content = new EncodedContentRegion(128, 96, 16, 16, encodeWidth, encodeHeight);
        string output = Path.Combine(directory, "prefix");
        Directory.CreateDirectory(output);
        byte[][] rendered = Enumerable.Range(0, (int)Period + 1).Select(n => Rgba((ulong)n % Period, transparent: true)).ToArray();
        string raw = Path.Combine(output, "all.rgba");
        await File.WriteAllBytesAsync(raw, rendered[..(int)Period].SelectMany(x => x).ToArray());
        string encoded = Path.Combine(output, "preview.mp4");
        await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{Width}x{Height}", "-framerate", "60/1", "-i", raw, "-an", "-filter_complex",
            $"[0:v]scale={encodeWidth}:{encodeHeight}:flags=lanczos,format=rgba,pad=128:96:16:16:color=black@0,split=2[color][mask];" +
            "[color]format=rgb24[rgb];[mask]alphaextract,format=rgb24[alpha];[rgb][alpha]hstack=inputs=2," +
            "scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuv420p[packed]", "-map", "[packed]",
            "-c:v", "libx264", "-preset", "fast", "-crf", "16", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough",
            "-enc_time_base", "1:60", "-video_track_timescale", "60", encoded]);
        async Task<JsonObject> ValidateAsync(byte[] wrap, string name)
        {
            string retainedPath = Path.Combine(output, name + ".rgba");
            await File.WriteAllBytesAsync(retainedPath, [.. rendered[0], .. rendered[Period - 1], .. wrap]);
            var manifest = new JsonObject { ["retained_frames"] = new JsonObject { ["path"] = retainedPath, ["format"] = "rgba",
                ["width"] = Width, ["height"] = Height, ["frame_indices"] = new JsonArray(0UL, Period - 1, Period) } };
            JsonObject closure = LoopClosureCheck.Evaluate(rendered[0], wrap, Width, Height, true, Period, true);
            LoopReference reference = await EncodedLoopValidator.FromScaledRenderAsync(tools, manifest, encodeWidth, encodeHeight, true, Period, closure);
            return await EncodedLoopValidator.ValidateAsync(encoded, tools, Period, 60, 1, true, reference, content);
        }
        JsonObject pass = await ValidateAsync(rendered[Period], "closed");
        JsonObject rgb = pass["reference_seam"]!["rgb"]!["tile_64"]!.AsObject(), alpha = pass["reference_seam"]!["alpha"]!["tile_64"]!.AsObject();
        check(pass["status"]!.GetValue<string>() == "observed_seam_pass" && pass["content_region"] is not null,
            "特效前缀路线：闭合的透明缓存按缩放参照通过，补边被裁掉");
        check(JsonNode.DeepEquals(rgb["difference_map"], rgb["encoding_noise_seam"]) && JsonNode.DeepEquals(alpha["difference_map"], alpha["encoding_noise_seam"]) &&
            rgb["reference_step"]!["worst"]!.GetValue<double>() > 5 && rgb["wrap_term"]!["worst"]!.GetValue<double>() == 0 &&
            rgb["encoding_error_first"]!["worst"]!.GetValue<double>() < 16,
            "特效前缀路线：参照帧按编码器同一缩放链得到，差分图恰为编码噪声，编码误差在正常量级");
        JsonObject rejected = await ValidateAsync(Rgba(Period % (Period + 2), transparent: true), "open");
        check(rejected["status"]!.GetValue<string>() == "observed_seam_fail" &&
            rejected["failures"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["loop_not_closed"]),
            "特效前缀路线：第 P 帧没回到第 0 帧时同样被闭合检验拒绝");
    }

    /// <summary>
    /// 直编路线（不透明整幅组在渲染那一遍直接编出成品，不写无损 master）。这里证两件事：
    /// <list type="number">
    /// <item>同一串渲染器 RGBA 帧，"直编"与"先无损 master 再裁切重编"产出的成品逐字节相同——把评估报告 §5 的循环 0
    /// 固化成回归，两条链的编码参数都从产品的 <c>PlaybackEncodeProfile</c> 取，参数一漂移这里就失配。</item>
    /// <item>直编的接缝参照（读渲染留下的原帧）与 master 路线的参照（从无损 master 按帧号解码）逐字节相同，
    /// 所以判据读数不因换了参照来源而变。</item>
    /// </list>
    /// </summary>
    private static async Task DirectPlaybackRouteAsync(Action<bool, string> check, NativeTools tools, string directory)
    {
        string output = Path.Combine(directory, "direct");
        Directory.CreateDirectory(output);
        byte[][] rendered = Enumerable.Range(0, (int)Period + 1).Select(n => Rgba((ulong)n % Period)).ToArray();
        byte[] stream = rendered[..(int)Period].SelectMany(x => x).ToArray();
        string[] rawInput = ["-hide_banner", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{Width}x{Height}", "-framerate", "60/1", "-i", "pipe:0", "-an"];

        // A（现状）：渲染器 RGBA → 无损 master → 解码得 gbrp → 全幅 crop → 播放档 x264。
        string master = Path.Combine(output, "master");
        Directory.CreateDirectory(master);
        string masterVideo = Path.Combine(master, "preview.mp4");
        (string losslessFilter, string[] losslessOutput) = Profile(losslessTest: true, masterVideo);
        await RunWithStdinAsync(tools.Ffmpeg, [.. rawInput, "-vf", losslessFilter, .. losslessOutput], stream);
        (string playbackFilter, string[] playbackOutput) = Profile(losslessTest: false, Path.Combine(output, "a.mp4"));
        await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-n", "-i", masterVideo, "-filter_complex",
            $"[0:v]crop={Width}:{Height}:0:0,{playbackFilter}[packed]", "-map", "[packed]", "-an", .. playbackOutput]);

        // B（直编）：渲染器 RGBA → format=gbrp（与 h264 解码器交给 swscale 的格式相同）→ 同一次全幅 crop → 同一个播放档。
        (_, string[] directOutput) = Profile(losslessTest: false, Path.Combine(output, "b.mp4"));
        await RunWithStdinAsync(tools.Ffmpeg,
            [.. rawInput, "-vf", $"format=gbrp,crop={Width}:{Height}:0:0,{playbackFilter}", .. directOutput], stream);
        string Sha(string name) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(output, name))));
        check(Sha("a.mp4") == Sha("b.mp4"),
            "直编路线：直编成品与经无损 master 裁切重编的成品逐字节相同（循环 0 回归）");

        // 旁证：不插 format=gbrp 时 swscale 收到的是 packed rgba。实测这条链的结果同样逐字节相同，
        // 所以 format=gbrp 不是为了修正差异，而是把"送进 swscale 的格式与 master 链相同"写死成显式不变量；
        // 将来 ffmpeg 升级让两条输入路径不再等价时，这一条会先失败，而成品仍受 format=gbrp 保护。
        (_, string[] rgbaOutput) = Profile(losslessTest: false, Path.Combine(output, "b-no-gbrp.mp4"));
        await RunWithStdinAsync(tools.Ffmpeg, [.. rawInput, "-vf", $"crop={Width}:{Height}:0:0,{playbackFilter}", .. rgbaOutput], stream);
        check(Sha("b-no-gbrp.mp4") == Sha("a.mp4"),
            "直编路线（旁证）：这版 swscale 对 rgba 与 gbrp 输入在这条色彩链上等价");

        // 接管：渲染那一遍的产物移进成品目录，报告与 master 路线同构。
        var runner = new NativeRenderRunner(tools);
        string adopted = Path.Combine(output, "adopt");
        Directory.CreateDirectory(adopted);
        File.Copy(Path.Combine(output, "b.mp4"), Path.Combine(adopted, "preview.mp4"));
        var adoptManifest = new JsonObject
        {
            ["status"] = "completed", ["lossless_test_encoding"] = false, ["pixel_packing"] = "rgb", ["encoded_frames"] = Period,
            ["alpha_bounds"] = new JsonObject { ["has_content"] = true, ["minimum_alpha"] = 255 },
            ["video_sha256"] = Sha("b.mp4"),
            ["encoded_stream"] = new JsonObject { ["streams"] = new JsonArray(new JsonObject { ["pix_fmt"] = "yuv420p" }) },
            ["encoder_command"] = new JsonObject { ["arguments"] = new JsonArray("-hide_banner", "-vf", "format=gbrp") },
            ["request"] = new JsonObject { ["width"] = Width, ["height"] = Height, ["fps_numerator"] = 60u, ["fps_denominator"] = 1u,
                ["frames"] = Period + 1, ["playback_encoder_kind"] = PlaybackEncoderSelection.Software }
        };
        JsonObject adoptReport = await runner.AdoptDirectPlaybackAsync(adoptManifest, adopted, Path.Combine(output, "adopted-encoded"),
            PlaybackEncoderSelection.Software, PlaybackEncoderSelection.Software, null);
        var adoptedCrop = adoptReport["crop"]!.Deserialize<CacheRegion>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        check(adoptReport["status"]!.GetValue<string>() == "completed" &&
            adoptReport["capture_mode"]!.GetValue<string>() == NativeRenderRunner.DirectPlaybackCaptureMode &&
            adoptedCrop == new CacheRegion(Width, Height, 0, 0, Width, Height) &&
            adoptReport["encode_seconds"] is null && adoptReport["encode_seconds_basis"] is not null &&
            adoptReport["video_sha256"]!.GetValue<string>() == Sha("b.mp4") &&
            adoptReport["encoder"]!.GetValue<string>() == "libx264" && adoptReport["encoder_used"]!.GetValue<string>() == PlaybackEncoderSelection.Software &&
            adoptReport["encoder_arguments"]!.AsArray().Count == 3 && adoptReport["decoded_area_fraction"]!.GetValue<double>() == 1d &&
            adoptReport["hardware_decode_preflight"]!["crop_grown_for_minimum"]!.GetValue<bool>() == false &&
            File.Exists(Path.Combine(output, "adopted-encoded", "cache.mp4")) && !File.Exists(Path.Combine(adopted, "preview.mp4")),
            "直编接管：成品移进 encoded/cache.mp4，裁剪区是先验整幅，编码参数与 SHA 从渲染清单透传，encode_seconds 为 null");
        foreach ((JsonObject broken, string name) in new[]
        {
            (Mutate(adoptManifest, m => m["lossless_test_encoding"] = true), "接管拒绝无损 master 的清单"),
            (Mutate(adoptManifest, m => m["pixel_packing"] = "rgba_side_by_side"), "接管拒绝透明打包"),
            (Mutate(adoptManifest, m => m["alpha_bounds"]!["minimum_alpha"] = 254), "接管拒绝不是每个像素都不透明的捕获")
        })
        {
            string target = Path.Combine(output, "reject-" + Guid.NewGuid().ToString("N")[..8]);
            try { await runner.AdoptDirectPlaybackAsync(broken, adopted, target, PlaybackEncoderSelection.Software, PlaybackEncoderSelection.Software, null); }
            catch (InvalidDataException) { check(true, "直编接管：" + name); continue; }
            throw new InvalidOperationException("Accepted an invalid direct playback manifest: " + name);
        }

        // 接缝参照：直编读渲染留下的原帧，master 路线从无损 master 解码，两者必须逐字节相同。
        await File.WriteAllTextAsync(Path.Combine(master, "manifest.json"), new JsonObject
        {
            ["status"] = "completed", ["lossless_test_encoding"] = true, ["pixel_packing"] = "rgb", ["encoded_frames"] = Period,
            ["request"] = new JsonObject { ["width"] = Width, ["height"] = Height, ["fps_numerator"] = 60, ["fps_denominator"] = 1, ["frames"] = Period + 1 }
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string retained = Path.Combine(output, "retained-frames.rgba");
        await File.WriteAllBytesAsync(retained, [.. rendered[0], .. rendered[Period - 1], .. rendered[Period]]);
        var render = new JsonObject
        {
            ["status"] = "completed", ["lossless_test_encoding"] = false, ["pixel_packing"] = "rgb", ["encoded_frames"] = Period,
            ["request"] = new JsonObject { ["width"] = Width, ["height"] = Height, ["fps_numerator"] = 60, ["fps_denominator"] = 1,
                ["frames"] = Period + 1, ["playback_encoder_kind"] = PlaybackEncoderSelection.Software },
            ["retained_frames"] = new JsonObject { ["path"] = retained, ["format"] = "rgba", ["width"] = Width, ["height"] = Height,
                ["frame_indices"] = new JsonArray(0UL, Period - 1, Period) }
        };
        var crop = new CacheRegion(Width, Height, 0, 0, Width, Height);
        JsonObject closure = LoopClosureCheck.Evaluate(rendered[0], rendered[Period], Width, Height, false, Period, true);
        LoopReference fromMaster = await EncodedLoopValidator.FromLosslessMasterAsync(tools, master, crop, false, Period, 0, closure, rendered[Period]);
        LoopReference fromRetained = await EncodedLoopValidator.FromRetainedFramesAsync(render, crop, false, Period, closure, rendered[Period]);
        check(fromRetained.First.SequenceEqual(fromMaster.First) && fromRetained.Last.SequenceEqual(fromMaster.Last) &&
            fromRetained.Wrap.SequenceEqual(fromMaster.Wrap) && fromRetained.Width == fromMaster.Width &&
            fromRetained.Height == fromMaster.Height && fromRetained.FrameSource != fromMaster.FrameSource,
            "直编路线：读原帧的接缝参照与从无损 master 解码的参照逐字节相同，只有来源文案不同");

        JsonObject pass = await EncodedLoopValidator.ValidateAsync(Path.Combine(output, "b.mp4"), tools, Period, 60, 1, false, fromRetained);
        check(pass["status"]!.GetValue<string>() == "observed_seam_pass" && pass["failures"]!.AsArray().Count == 0 &&
            pass["reference_seam"]!["frame_source"]!.GetValue<string>().Contains("no lossless master", StringComparison.Ordinal),
            "直编路线：直编成品按原帧参照通过接缝校验，来源记明没有写过无损 master");

        // 透明打包与差一帧的周期在这条路线上都要被拒绝。
        try
        {
            await EncodedLoopValidator.FromRetainedFramesAsync(render, crop, true, Period, closure, rendered[Period]);
            throw new InvalidOperationException("Accepted packed alpha on the direct playback route.");
        }
        catch (InvalidDataException) { check(true, "直编路线：透明打包的参照请求被拒（直编只做不透明组）"); }
        var masterRender = new JsonObject { ["status"] = "completed", ["lossless_test_encoding"] = true,
            ["pixel_packing"] = "rgb", ["encoded_frames"] = Period,
            ["request"] = render["request"]!.DeepClone(), ["retained_frames"] = render["retained_frames"]!.DeepClone() };
        try
        {
            await EncodedLoopValidator.FromRetainedFramesAsync(masterRender, crop, false, Period, closure, rendered[Period]);
            throw new InvalidOperationException("Accepted a lossless master manifest on the direct playback route.");
        }
        catch (InvalidDataException) { check(true, "直编路线：无损 master 的清单不能当直编参照用"); }
    }

    private static JsonObject Mutate(JsonObject source, Action<JsonObject> change)
    {
        var copy = source.DeepClone().AsObject();
        change(copy);
        return copy;
    }

    /// <summary>按产品的播放/无损档位取色彩链与输出参数，参数漂移时这里的两条链会立刻失配。</summary>
    private static (string ColorFilter, string[] Output) Profile(bool losslessTest, string output)
    {
        Type type = typeof(NativeRenderRunner).Assembly.GetType("Baker.Core.PlaybackEncodeProfile")!;
        object profile = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(null, [(uint)Width, (uint)Height, 60u, 1u, losslessTest, PlaybackEncoderSelection.Software])!;
        return ((string)type.GetProperty("ColorFilter")!.GetValue(profile)!,
            (string[])type.GetMethod("OutputArguments", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(profile, [60u, 1u, output])!);
    }

    private static byte[] Rgb(byte[] rgba)
    {
        byte[] rgb = new byte[rgba.Length / 4 * 3];
        for (int i = 0; i < rgba.Length / 4; ++i) { rgb[i * 3] = rgba[i * 4]; rgb[i * 3 + 1] = rgba[i * 4 + 1]; rgb[i * 3 + 2] = rgba[i * 4 + 2]; }
        return rgb;
    }

    /// <summary>按渲染器 master 的无损参数写一个 P 帧的 master 目录，清单里 request.frames 记渲染帧数、encoded_frames 记 P。</summary>
    private static async Task WriteMasterAsync(NativeTools tools, string directory, byte[][] frames, ulong renderedFrames)
    {
        Directory.CreateDirectory(directory);
        string raw = Path.Combine(directory, "master.rgba");
        await File.WriteAllBytesAsync(raw, frames.SelectMany(x => x).ToArray());
        await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{Width}x{Height}", "-framerate", "60/1", "-i", raw, "-an", "-vf", "format=rgb24",
            "-c:v", "libx264rgb", "-preset", "ultrafast", "-crf", "0", "-pix_fmt", "rgb24", "-fps_mode", "passthrough",
            "-enc_time_base", "1:60", "-movie_timescale", "60", "-video_track_timescale", "60", Path.Combine(directory, "preview.mp4")]);
        File.Delete(raw);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), new JsonObject
        {
            ["status"] = "completed", ["lossless_test_encoding"] = true, ["pixel_packing"] = "rgb", ["encoded_frames"] = (ulong)frames.Length,
            ["request"] = new JsonObject { ["width"] = Width, ["height"] = Height, ["fps_numerator"] = 60, ["fps_denominator"] = 1, ["frames"] = renderedFrames }
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>播放档编码（BT.709 limited yuv420p、CRF 16），第 10、20 帧强制 IDR 以便记录片内 IDR 对。</summary>
    private static Task EncodePlaybackAsync(NativeTools tools, string master, string output, string? filter) =>
        RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-n", "-i", master, "-an",
            "-vf", (filter is null ? "" : filter + ",") + "scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuv420p",
            "-force_key_frames", "expr:eq(n,10)+eq(n,20)", "-sc_threshold", "0", "-c:v", "libx264", "-preset", "fast", "-crf", "16", "-pix_fmt", "yuv420p",
            "-fps_mode", "passthrough", "-enc_time_base", "1:60", "-video_track_timescale", "60", output]);

    private static NativeTools? FindTools() => LocalTools.Tools is { } tools
        ? new NativeTools(tools.Ffmpeg, tools.Ffmpeg, tools.Ffprobe, [Path.GetDirectoryName(tools.Ffmpeg)!]) : null;

    /// <summary>与产品的编码器一样从管道吃 rawvideo：stdout/stderr 先挂上异步读，再一次写完输入。</summary>
    private static async Task RunWithStdinAsync(string executable, IEnumerable<string> arguments, byte[] input)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("ffmpeg 没有启动。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.BaseStream.WriteAsync(input);
        process.StandardInput.Close();
        await Task.WhenAll(output, errors, process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new IOException($"ffmpeg 退出码 {process.ExitCode}：{errors.Result}");
    }

    private static async Task RunAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("ffmpeg 没有启动。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, errors, process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new IOException($"ffmpeg 退出码 {process.ExitCode}：{errors.Result}");
    }
}
