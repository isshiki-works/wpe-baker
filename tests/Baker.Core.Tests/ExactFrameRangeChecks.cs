using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 2026-09-17：接缝代码用 -ss (K-0.5)/fps 取第 K 帧。自带 ffmpeg 8.1.2 在 60/1、时基 1/60 下有 2/3 的 K 拿到第 K-1 帧，
/// 硬切残差比成了 f[0] 对 f[P-1]（凭空多算一步运动，偏向拒绝），交叉淡化混进 f[P-1+i]（接缝处重复一帧），
/// 权重校验取源帧同样偏移、查不出来。这里用每帧画着自身帧号的合成 master（与渲染器 master 同一套无损编码参数），
/// 把按帧号取帧、硬切残差、淡化后的帧序钉死。需要自带 ffmpeg，路径从 tools.json 读。
/// </summary>
internal static class ExactFrameRangeChecks
{
    private const int Bits = 16, BitWidth = 8, Width = Bits * BitWidth, Height = 64;
    // P 取 3 的倍数：旧写法在 60/1 下恰好取错；C 与 60 fps 下的产品淡化窗口一致。
    private const ulong Period = 123;
    private const uint Crossfade = 24;

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        NativeTools? tools = FindTools();
        check(tools is not null, "按帧号取帧的检查找到了自带 ffmpeg（WPE_BAKER_TOOLS_JSON 或 src/Baker.Cli/bin/Release/net10.0/tools.json）");
        string directory = Path.Combine(root, "exact-frame-range");
        Directory.CreateDirectory(directory);
        ulong total = Period + Crossfade;

        foreach ((uint numerator, uint denominator) in new[] { (60u, 1u), (60000u, 1001u) })
        {
            string rate = $"{numerator}/{denominator}";
            string video = await EncodeMasterAsync(tools!, directory, $"numbered-{numerator}_{denominator}", total,
                numerator, denominator, n => n);
            foreach ((ulong first, ulong count, string what) in new[]
            {
                (0UL, 1UL, "首帧"), (73UL, 1UL, "中间一帧"), (total - 1, 1UL, "末帧"),
                (Period - 1, 1UL, "接缝前一帧 K=P-1"), (Period, 1UL, "接缝后一帧 K=P（循环里的第 0 帧的延续）"),
                (0UL, (ulong)Crossfade + 1, "淡化自检的开头段 0..C"), (Period - 1, (ulong)Crossfade + 1, "淡化自检的环绕段 P-1..P+C-1"),
                (Period, (ulong)Crossfade, "环绕淡化窗口 P..P+C-1"), (Period - 2, 4UL, "跨接缝的连续四帧")
            })
            {
                ulong[] got = await DecodeNumbersAsync(tools!, video, first, count, numerator, denominator);
                check(got.SequenceEqual(Enumerable.Range(0, (int)count).Select(i => first + (ulong)i)),
                    $"{rate}：ExactFrameRange 取{what}（第 {first} 帧起 {count} 帧）帧号逐一正确");
            }
        }

        var runner = new NativeRenderRunner(tools!);
        // 周期完全闭合：f[n] = 图案(n mod P)，第 P 帧与第 0 帧逐位相同。
        string closedMaster = Path.Combine(directory, "closed-60");
        _ = await EncodeMasterAsync(tools!, closedMaster, null, total, 60, 1, n => n % Period);
        JsonObject residual = await runner.MeasureSeamResidualAsync(closedMaster, Period, Crossfade);
        check(residual["first_layer"]!["hard_cut_global_rgb_mae_255"]!.GetValue<double>() == 0 &&
            residual["first_layer"]!["hard_cut_worst_tile_rgb_mae_255"]!.GetValue<double>() == 0,
            "周期完全闭合的 master 上硬切残差（第 0 帧对第 P 帧）整幅与最差瓦片都是 0");
        check(residual["first_layer"]!["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() == 0 &&
            residual["first_layer"]!["per_frame"]!.AsArray().Count == (int)Crossfade &&
            residual["second_layer"] is null && residual["ordinary_step_p95"] is null &&
            residual["status"]!.GetValue<string>() == "observed_within_residual_limits",
            "周期完全闭合的 master 上淡化窗口内每个 Δ_k 都是 0，第一层通过；不再有第二层与普通步进 p95");

        // 静止场景 + 一块 20 级的残差：普通步进 p95 = 0，旧的 min(p95, 48) 会把上限压到 0 拒掉；新第一层按 48/2 放行。
        const int Wide = 256;
        string stillMaster = Path.Combine(directory, "still-residual-60");
        _ = await EncodeGrayMasterAsync(tools!, stillMaster, Wide, Wide, total, 60, 1,
            (n, x, y) => (byte)(n >= Period && x < 64 && y < 64 ? 120 : 100));
        JsonObject still = await runner.MeasureSeamResidualAsync(stillMaster, Period, Crossfade);
        check(still["status"]!.GetValue<string>() == "observed_within_residual_limits" &&
            still["first_layer"]!["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() == 20 &&
            still["first_layer"]!["hard_cut_global_rgb_mae_255"]!.GetValue<double>() == 1.25 &&
            Math.Abs(still["first_layer"]!["ghost_peak_worst_tile_rgb_mae_255"]!.GetValue<double>() - 20.0 * 12 / 25) < 1e-3,
            "静止场景里一块 20 级的残差按 48/255 与 2/255 通过第一层，场景没有普通运动不再把上限压到 0；重影峰值记为 min(w,1-w)·20");

        // 残差在淡化窗口中段才出现：Δ_0 = 0、Δ_12.. = 60，第一层按 max_k 拒，而不是只看硬切。
        string lateMaster = Path.Combine(directory, "late-residual-60");
        _ = await EncodeGrayMasterAsync(tools!, lateMaster, Wide, Wide, total, 60, 1,
            (n, x, y) => (byte)(n >= Period + 12 && x < 64 && y < 64 ? 160 : 100));
        JsonObject late = await runner.MeasureSeamResidualAsync(lateMaster, Period, Crossfade);
        check(late["status"]!.GetValue<string>() == "rejected_residual_above_limits" &&
            late["first_layer"]!["hard_cut_worst_tile_rgb_mae_255"]!.GetValue<double>() == 0 &&
            late["first_layer"]!["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() == 60 &&
            late["first_layer"]!["maximum_worst_tile_frame"]!.GetValue<int>() == 12,
            "硬切为 0 但淡化窗口第 12 帧起出现 60 级残差时，第一层按 max_k 拒绝并指出 k=12");

        await PackedMasterChecksAsync(check, tools!, runner, directory, total);

        foreach ((uint numerator, uint denominator) in new[] { (60u, 1u), (60000u, 1001u) })
        {
            string rate = $"{numerator}/{denominator}";
            string periodic =Path.Combine(directory, $"crossfade-closed-{numerator}_{denominator}");
            _ = await EncodeMasterAsync(tools!, periodic, null, total, numerator, denominator, n => n % Period);
            JsonObject record = await runner.ApplyLoopCrossfadeAsync(periodic, Period, Crossfade);
            ulong[] sequence = await DecodeNumbersAsync(tools!, Path.Combine(periodic, "preview.mp4"), 0, Period + 4, numerator, denominator);
            check(record["status"]!.GetValue<string>() == "applied" && sequence.Length == (int)Period &&
                sequence.SequenceEqual(Enumerable.Range(0, (int)Period).Select(i => (ulong)i)),
                $"{rate}：周期闭合的 master 淡化后仍是第 0..P-1 帧各一次，接缝处没有重复帧、没有跳帧（含 stream copy 尾段）");
            check(record["weight_verification"]?["status"]?.GetValue<string>() == "verified_against_source_frames",
                $"{rate}：淡化权重校验用独立取帧路径通过");

            // 帧号不回卷时，淡化第 i 帧必须是 (1-w)·f[i] + w·f[P+i]；混进 f[P-1+i] 在帧号位上会差出整条色块。
            string open = Path.Combine(directory, $"crossfade-open-{numerator}_{denominator}");
            _ = await EncodeMasterAsync(tools!, open, null, total, numerator, denominator, n => n);
            _ = await runner.ApplyLoopCrossfadeAsync(open, Period, Crossfade);
            byte[] faded = await DecodeRawAsync(tools!, Path.Combine(open, "preview.mp4"), 0, Crossfade, numerator, denominator);
            bool matchesWrap = true, matchesShifted = true;
            foreach (uint i in new uint[] { 0, Crossfade / 2, Crossfade - 1 })
            {
                double w = (double)(Crossfade - i) / (Crossfade + 1);
                double worstWrap = 0, worstShifted = 0;
                for (int bit = 0; bit < Bits; ++bit)
                {
                    int value = faded[(int)i * Width * Height * 3 + ((Height / 2) * Width + bit * BitWidth + BitWidth / 2) * 3];
                    double expected = (1 - w) * Bit(i, bit) + w * Bit(Period + i, bit);
                    double shifted = (1 - w) * Bit(i, bit) + w * Bit(Period - 1 + i, bit);
                    worstWrap = Math.Max(worstWrap, Math.Abs(value - expected));
                    worstShifted = Math.Max(worstShifted, Math.Abs(value - shifted));
                }
                matchesWrap &= worstWrap <= 2;
                matchesShifted &= worstShifted <= 2;
            }
            check(matchesWrap && !matchesShifted,
                $"{rate}：淡化窗口首、中、末帧混入的是 f[P+i] 而不是 f[P-1+i]");
        }

        // 淡化实现错位一帧（混入 f[P-1+i]）的成品必须让权重核对失败；同一 master 上按正确公式合成的成品通过。
        string mutation = Path.Combine(directory, "self-check-mutation");
        string source = await EncodeMasterAsync(tools!, mutation, "source", total, 60, 1, n => n);
        byte Faded(ulong n, int x, ulong wrapStart)
        {
            if (n >= Crossfade) return (byte)Bit(n, x / BitWidth);
            double w = (double)(Crossfade - n) / (Crossfade + 1);
            return (byte)Math.Floor((1 - w) * Bit(n, x / BitWidth) + w * Bit(wrapStart + n, x / BitWidth));
        }
        string fadedRight = await EncodeGrayAsync(tools!, mutation, "faded-right", Width, Height, Period, 60, 1,
            (n, x, _) => Faded(n, x, Period));
        string fadedShifted = await EncodeGrayAsync(tools!, mutation, "faded-shifted", Width, Height, Period, 60, 1,
            (n, x, _) => Faded(n, x, Period - 1));
        var rewrite = new MasterRewrite(new FfmpegTool(tools!));
        JsonObject rightCheck = await rewrite.VerifyWeightsAsync(source, fadedRight, Period, Crossfade, Width, Height, 60, 1, mutation, default);
        check(rightCheck["status"]?.GetValue<string>() == "verified_against_source_frames",
            "按 w = (C-i)/(C+1) 混入 f[P+i] 的成品通过权重核对");
        bool shiftedRejected = false;
        try { _ = await rewrite.VerifyWeightsAsync(source, fadedShifted, Period, Crossfade, Width, Height, 60, 1, mutation, default); }
        catch (InvalidDataException) { shiftedRejected = true; }
        check(shiftedRejected && !File.Exists(Path.Combine(mutation, "crossfade-verify-faded.rgb")),
            "混入 f[P-1+i]（淡化实现错位一帧）的成品让权重核对失败，帧包照样清掉");
    }

    /// <summary>
    /// 透明组 master（左半预乘色、右半覆盖度，视频宽 2W，清单宽 W）：残差两半分别算、RGB 半带掩码；淡化在 packed 域照同一公式做，
    /// 权重校验按 2W 宽逐像素通过，覆盖度半淡化后正好是两段覆盖度的线性混合。
    /// </summary>
    private static async Task PackedMasterChecksAsync(Action<bool, string> check, NativeTools tools, NativeRenderRunner runner,
        string directory, ulong total)
    {
        const int Half = 256;
        async Task<string> PackedAsync(string name, int coverageDelta)
        {
            string master = Path.Combine(directory, name);
            // 左半：左上 64×64 预乘色 100 → 第 P 帧起 120（Δ 20）；右半：第二块瓦片覆盖度 200 → 200−coverageDelta；其余透明黑。
            _ = await EncodeGrayMasterAsync(tools, master, Half * 2, Half, total, 60, 1, (n, x, y) =>
                x < Half ? (byte)(x < 64 && y < 64 ? (n >= Period ? 120 : 100) : 0)
                    : (byte)(x - Half is >= 64 and < 128 && y < 64 ? (n >= Period ? 200 - coverageDelta : 200) : 0));
            string manifestPath = Path.Combine(master, "manifest.json");
            JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["pixel_packing"] = "rgba_side_by_side";
            manifest["request"]!["width"] = Half;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return master;
        }

        string passing = await PackedAsync("packed-pass-60", 12);
        JsonObject pass = await runner.MeasureSeamResidualAsync(passing, Period, Crossfade);
        JsonObject passFirst = pass["first_layer"]!.AsObject(), passRow = passFirst["per_frame"]![0]!.AsObject();
        check(pass["status"]!.GetValue<string>() == "observed_within_residual_limits" && pass["pixel_packing"]!.GetValue<string>() == "rgba_side_by_side" &&
            pass["width"]!.GetValue<int>() == Half &&
            passFirst["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() == 20 && passFirst["maximum_worst_tile_x"]!.GetValue<int>() == 0 &&
            passFirst["hard_cut_global_rgb_mae_255"]!.GetValue<double>() == 1.25 &&
            passRow["color_worst_tile_rgb_mae_255"]!.GetValue<double>() == 20 && passRow["coverage_worst_tile_mae_255"]!.GetValue<double>() == 12 &&
            passRow["coverage_global_mae_255"]!.GetValue<double>() == 0.75 && passRow["color_mask_fraction"]!.GetValue<double>() == 0.125,
            "透明组 master 两半分别算：预乘色半 20 级、覆盖度半 12 级各自读出，掩码覆盖 1/8，第一层取更大的一半通过");

        JsonObject fade = await runner.ApplyLoopCrossfadeAsync(passing, Period, Crossfade);
        byte[] faded = await DecodeRawAsync(tools, Path.Combine(passing, "preview.mp4"), 12, 1, 60, 1);
        double w = (double)(Crossfade - 12) / (Crossfade + 1);
        int coverage = faded[((10 * Half * 2) + Half + 80) * 3], color = faded[(10 * Half * 2 + 10) * 3];
        check(fade["status"]!.GetValue<string>() == "applied" && fade["pixel_packing"]!.GetValue<string>() == "rgba_side_by_side" &&
            fade["weight_verification"]?["status"]?.GetValue<string>() == "verified_against_source_frames" &&
            Math.Abs(coverage - ((1 - w) * 200 + w * 188)) <= 1 && Math.Abs(color - ((1 - w) * 100 + w * 120)) <= 1,
            "透明组 master 在 packed 域淡化：权重校验按两倍宽度逐像素通过，覆盖度与预乘色都是两段的线性混合");

        JsonObject reject = await runner.MeasureSeamResidualAsync(await PackedAsync("packed-reject-60", 60), Period, Crossfade);
        check(reject["status"]!.GetValue<string>() == "rejected_residual_above_limits" &&
            reject["first_layer"]!["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() == 60 &&
            reject["first_layer"]!["maximum_worst_tile_x"]!.GetValue<int>() == Half + 64,
            "覆盖度半 60 级的残差单独就让第一层拒绝，瓦片位置记在 packed 图坐标里");
    }

    private static int Bit(ulong frame, int bit) => ((frame >> bit) & 1) != 0 ? 255 : 0;

    private static NativeTools? FindTools() => LocalTools.Tools is { } tools
        ? new NativeTools(tools.Ffmpeg, tools.Ffmpeg, tools.Ffprobe, [Path.GetDirectoryName(tools.Ffmpeg)!]) : null;

    /// <summary>
    /// 按渲染器 master 的无损参数编一段帧号视频：第 n 帧的第 b 列色块是 label(n) 的第 b 位。
    /// <paramref name="name"/> 为空时写成 master 目录（preview.mp4 + manifest.json），否则写成目录下的单个文件。
    /// </summary>
    private static Task<string> EncodeMasterAsync(NativeTools tools, string directory, string? name, ulong frames,
        uint numerator, uint denominator, Func<ulong, ulong> label) =>
        EncodeGrayAsync(tools, directory, name, Width, Height, frames, numerator, denominator,
            (n, x, _) => (byte)Bit(label(n), x / BitWidth));

    private static Task<string> EncodeGrayMasterAsync(NativeTools tools, string directory, int width, int height, ulong frames,
        uint numerator, uint denominator, Func<ulong, int, int, byte> level) =>
        EncodeGrayAsync(tools, directory, null, width, height, frames, numerator, denominator, level);

    /// <summary>按渲染器 master 的无损参数编一段灰度视频：第 n 帧 (x, y) 处 RGB 都是 level(n, x, y)。</summary>
    private static async Task<string> EncodeGrayAsync(NativeTools tools, string directory, string? name, int width, int height,
        ulong frames, uint numerator, uint denominator, Func<ulong, int, int, byte> level)
    {
        Directory.CreateDirectory(directory);
        string raw = Path.Combine(directory, (name ?? "master") + ".rgba");
        string video = name is null ? Path.Combine(directory, "preview.mp4") : Path.Combine(directory, name + ".mp4");
        await using (var stream = new FileStream(raw, FileMode.CreateNew, FileAccess.Write))
        {
            byte[] frame = new byte[width * height * 4];
            for (ulong n = 0; n < frames; ++n)
            {
                for (int y = 0; y < height; ++y)
                    for (int x = 0; x < width; ++x)
                    {
                        int p = (y * width + x) * 4;
                        frame[p] = frame[p + 1] = frame[p + 2] = level(n, x, y);
                        frame[p + 3] = 255;
                    }
                await stream.WriteAsync(frame);
            }
        }
        await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-n", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}", "-framerate", $"{numerator}/{denominator}", "-i", raw, "-an", "-vf", "format=rgb24",
            "-force_key_frames", $"expr:eq(n,{Crossfade})", "-c:v", "libx264rgb", "-preset", "ultrafast", "-crf", "0", "-pix_fmt", "rgb24",
            "-fps_mode", "passthrough", "-enc_time_base", $"{denominator}:{numerator}", "-movie_timescale", $"{numerator}",
            "-video_track_timescale", $"{numerator}", "-movflags", "+faststart", video]);
        File.Delete(raw);
        if (name is null)
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), new JsonObject
            {
                ["status"] = "completed", ["lossless_test_encoding"] = true, ["pixel_packing"] = "rgb",
                ["request"] = new JsonObject { ["width"] = width, ["height"] = height, ["fps_numerator"] = numerator,
                    ["fps_denominator"] = denominator, ["frames"] = frames }
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return video;
    }

    private static async Task<byte[]> DecodeRawAsync(NativeTools tools, string video, ulong first, ulong count,
        uint numerator, uint denominator)
    {
        ExactFrameRange.FfmpegInput input = ExactFrameRange.Build(video, first, count, numerator, denominator);
        return await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", .. input.Arguments, "-map", "0:v:0",
            "-vf", input.Filter, "-fps_mode", "passthrough", "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
    }

    private static async Task<ulong[]> DecodeNumbersAsync(NativeTools tools, string video, ulong first, ulong count,
        uint numerator, uint denominator)
    {
        byte[] data = await DecodeRawAsync(tools, video, first, count, numerator, denominator);
        int frameBytes = Width * Height * 3;
        var numbers = new ulong[data.Length / frameBytes];
        for (int f = 0; f < numbers.Length; ++f)
            for (int bit = 0; bit < Bits; ++bit)
                if (data[f * frameBytes + ((Height / 2) * Width + bit * BitWidth + BitWidth / 2) * 3] > 127)
                    numbers[f] |= 1UL << bit;
        return numbers;
    }

    private static async Task<byte[]> RunAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("ffmpeg 没有启动。");
        using var output = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(copy, errors, process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new IOException($"ffmpeg 退出码 {process.ExitCode}：{errors.Result}");
        return output.ToArray();
    }
}
