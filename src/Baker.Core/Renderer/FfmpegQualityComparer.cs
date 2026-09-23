using System.Globalization;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 用包内 ffmpeg 的 ssim/psnr 滤镜量画质（<see cref="IQualityComparer"/> 的 C2.4b 实现）。抽样帧怎么解由
/// <see cref="QualityGate.UsesFrameWindows"/> 定：短循环一遍顺序解、select 取帧、不限线程（P1a）；长循环按抽样帧逐个 seek 取窗口。
/// 两种取法解出的帧逐字节相同，比对滤镜图也与改前一致，所以同一对输入量出的 SSIM/PSNR 不随取法变。
/// </summary>
internal sealed partial class FfmpegQualityComparer(FfmpegTool ff) : IQualityComparer
{
    /// <summary>成品分支与 master 分支在滤镜图里的标签，取得够怪以免和编码滤镜里的标签撞名。</summary>
    private const string ProductLabel = "gateproduct";
    private const string MasterLabel = "gatemaster";

    public Task<QualityReport> CompareAsync(QualityRequest request, CancellationToken token)
    {
        bool windows = QualityGate.UsesFrameWindows(request.LoopFrames);
        return request.Reference switch
        {
            MasterQualityReference master when windows => MasterWindowsAsync(request, master, token),
            MasterQualityReference master => MasterSingleAsync(request, master, token),
            FramesQualityReference frames => FramesAsync(request, frames, windows, token),
            _ => throw new ArgumentException("Unsupported quality reference.", nameof(request)),
        };
    }

    /// <summary>成品与 master 各顺序解一遍，两边抽同一批帧，master 走完编码滤镜后同时算 ssim 与 psnr。</summary>
    private async Task<QualityReport> MasterSingleAsync(QualityRequest request, MasterQualityReference master, CancellationToken token)
    {
        string log = Path.Combine(request.WorkDirectory, request.Stem + ".stderr.log");
        await ff.RunTextAsync(ff.Tools.Ffmpeg, MasterArguments(request.Product, master.Video, master.EncodeFilter, request.Samples), log, token);
        string metrics = await File.ReadAllTextAsync(log, token);
        return new(ParseSsim(metrics), ParsePsnr(metrics), QualityGate.SingleDecodeScope);
    }

    /// <summary>
    /// 长循环：每个抽样帧在成品与 master 上各 seek 一个窗口单独量，再合成：SSIM 取各帧平均（与 ssim 滤镜的 All 同义），
    /// PSNR 由各帧的归一化均方误差平均后换回分贝。
    /// </summary>
    private async Task<QualityReport> MasterWindowsAsync(QualityRequest request, MasterQualityReference master, CancellationToken token)
    {
        double totalSsim = 0, totalNormalizedMse = 0;
        foreach (ulong frame in request.Samples)
        {
            string log = Path.Combine(request.WorkDirectory, $"{request.Stem}-{frame.ToString(CultureInfo.InvariantCulture)}.stderr.log");
            await ff.RunTextAsync(ff.Tools.Ffmpeg, MasterWindowArguments(request.Product, master.Video, master.EncodeFilter,
                frame, request.FpsNumerator, request.FpsDenominator), log, token);
            string metrics = await File.ReadAllTextAsync(log, token);
            if (ParseSsim(metrics) is not double ssim || ParsePsnr(metrics) is not double psnr)
                return new(null, null, QualityGate.FrameWindowsScope);
            totalSsim += ssim;
            totalNormalizedMse += Math.Pow(10, -psnr / 10);
        }
        return new(totalSsim / request.Samples.Count, -10 * Math.Log10(totalNormalizedMse / request.Samples.Count),
            QualityGate.FrameWindowsScope);
    }

    /// <summary>
    /// GPU 直编路线：成品的抽样帧解成 yuv420p 落 <c>&lt;Stem&gt;-product.yuv</c>，同时参照写进 <c>&lt;Stem&gt;-reference.rgb</c>，
    /// 两个都齐了再进一个比对进程。原始 YUV 丢了 MP4 的色彩标签，比对前补回，免得 framesync 按另一套矩阵/范围重新解释成品。
    /// </summary>
    private async Task<QualityReport> FramesAsync(QualityRequest request, FramesQualityReference reference, bool windows,
        CancellationToken token)
    {
        int width = reference.Width, height = reference.Height;
        long frameBytes = checked((long)width * height * 3 / 2);
        string productPath = Path.Combine(request.WorkDirectory, request.Stem + "-product.yuv");
        string referencePath = Path.Combine(request.WorkDirectory, request.Stem + "-reference.rgb");
        using (var failed = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            // 一边失败就取消另一边；报出去的是先失败的那个，不是被连带取消的那个。
            async Task Together(Func<CancellationToken, Task> body)
            {
                try { await body(failed.Token); }
                catch { failed.Cancel(); throw; }
            }
            Task decode = Together(cancel => DecodeProductAsync(request, productPath, frameBytes, windows, cancel));
            Task write = Task.Run(() => Together(async cancel =>
            {
                await using var stream = new FileStream(referencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await reference.WriteRgb24(stream, cancel);
            }), CancellationToken.None);
            try { await Task.WhenAll(decode, write); }
            catch
            {
                token.ThrowIfCancellationRequested();
                Exception cause = new[] { decode, write }.Select(task => task.Exception?.InnerException)
                    .FirstOrDefault(error => error is not null and not OperationCanceledException)
                    ?? decode.Exception?.InnerException ?? write.Exception!.InnerException!;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cause);
            }
        }
        if (new FileInfo(productPath).Length != frameBytes * request.Samples.Count)
            throw new InvalidDataException("Quality product decode did not return exactly the sampled frames.");
        string size = FormattableString.Invariant($"{width}x{height}");
        string graph = $"[1:v]format=gbrp,{PlaybackEncodeProfile.Bt709Filter},split=2[r1][r2];" +
            "[0:v]setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709,split=2[p1][p2];" +
            "[p1][r1]ssim[ss];[p2][r2]psnr[ps]";
        string log = Path.Combine(request.WorkDirectory, request.Stem + ".stderr.log");
        await ff.RunTextAsync(ff.Tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-nostats", "-f", "rawvideo", "-pixel_format", "yuv420p", "-video_size", size,
             "-framerate", "1", "-i", productPath, "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", size,
             "-framerate", "1", "-i", referencePath, "-filter_complex_threads", "1", "-filter_complex", graph,
             "-map", "[ss]", "-map", "[ps]", "-an", "-c:v", "rawvideo", "-f", "null", "-"], log, token);
        string metrics = await File.ReadAllTextAsync(log, token);
        return new(ParseSsim(metrics), ParsePsnr(metrics), windows ? QualityGate.FrameWindowsScope : QualityGate.SingleDecodeScope);
    }

    /// <summary>成品的抽样帧按请求的取法解成 yuv420p，依次写进 <paramref name="productPath"/>。</summary>
    private async Task DecodeProductAsync(QualityRequest request, string productPath, long frameBytes, bool windows,
        CancellationToken token)
    {
        if (windows)
        {
            await using var product = new FileStream(productPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            foreach (ulong index in request.Samples)
            {
                var range = ExactFrameRange.Build(request.Product, index, 1, request.FpsNumerator, request.FpsDenominator);
                await product.WriteAsync(await ff.RunBytesAsync(
                    ["-hide_banner", "-nostdin", "-v", "error", "-threads", "2", .. range.Arguments,
                     "-map", "0:v:0", "-vf", range.Filter, "-fps_mode", "passthrough", "-frames:v", "1",
                     "-an", "-sn", "-dn", "-f", "rawvideo", "-pix_fmt", "yuv420p", "pipe:1"], frameBytes, token), token);
            }
            return;
        }
        // P1a：一个进程顺序软解到最后一个抽样帧，select 按帧号放行，不限线程（解码器默认按核数开帧线程）。
        // 直接写文件而不经 stdout：.NET 读子进程管道只有约 60 MB/s，8K 的 9 帧 yuv 有 448 MB。
        await ff.RunTextAsync(ff.Tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-i", request.Product, "-map", "0:v:0", "-vf", Select(request.Samples),
             "-fps_mode", "passthrough", "-frames:v", request.Samples.Count.ToString(CultureInfo.InvariantCulture),
             "-an", "-sn", "-dn", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-n", productPath],
            Path.Combine(request.WorkDirectory, request.Stem + "-decode.stderr.log"), token);
    }

    /// <summary>只放行抽中的帧号。</summary>
    internal static string Select(IReadOnlyList<ulong> frames) =>
        $"select='{string.Join("+", frames.Select(n => $"eq(n\\,{n.ToString(CultureInfo.InvariantCulture)})"))}'";

    /// <summary>
    /// master 路线一遍解的参数：第一路成品、第二路无损 master。master 分支把编码滤镜的输入换到第二路、抽样 select 插在最前面
    /// （只让抽中的帧走完后面的裁剪与色彩转换），输出标签改名；两边抽同一批帧、PTS 重排成连续后逐帧对齐。
    /// </summary>
    internal static string[] MasterArguments(string product, string master, string encodeFilter, IReadOnlyList<ulong> frames)
    {
        string select = Select(frames) + ",setpts=N/TB";
        string graph = encodeFilter.Replace("[0:v]", $"[1:v]{select},", StringComparison.Ordinal)
                .Replace("[packed]", $"[{MasterLabel}]", StringComparison.Ordinal) +
            $";[0:v]{select}[{ProductLabel}];" +
            $"[{ProductLabel}]split=2[gateps][gatepp];[{MasterLabel}]split=2[gatems][gatemp];" +
            "[gateps][gatems]ssim[gatessim];[gatepp][gatemp]psnr[gatepsnr]";
        // The packaged FFmpeg omits wrapped_avframe, the null muxer's default encoder.
        return ["-hide_banner", "-nostdin", "-nostats", "-i", product, "-i", master,
            "-filter_complex", graph, "-map", "[gatessim]", "-map", "[gatepsnr]",
            "-an", "-c:v", "rawvideo", "-f", "null", "-"];
    }

    /// <summary>master 路线一个抽样帧的参数：成品与 master 各 seek 到同一段有理时间窗口，只解这一帧附近。</summary>
    internal static string[] MasterWindowArguments(string product, string master, string encodeFilter,
        ulong frame, uint numerator, uint denominator)
    {
        var productInput = ExactFrameRange.Build(product, frame, 1, numerator, denominator);
        var masterInput = ExactFrameRange.Build(master, frame, 1, numerator, denominator);
        string reference = encodeFilter.Replace("[0:v]", $"[1:v]{masterInput.Filter},", StringComparison.Ordinal)
            .Replace("[packed]", "[samplemaster]", StringComparison.Ordinal);
        string graph = reference + $";[0:v]{productInput.Filter}[sampleproduct];" +
            "[sampleproduct]split=2[ps][pp];[samplemaster]split=2[ms][mp];[ps][ms]ssim[ss];[pp][mp]psnr[psnr]";
        return ["-hide_banner", "-nostdin", "-nostats", "-threads", "2", .. productInput.Arguments,
            "-threads", "2", .. masterInput.Arguments, "-filter_complex_threads", "1", "-filter_complex", graph,
            "-map", "[ss]", "-map", "[psnr]", "-frames:v:0", "1", "-frames:v:1", "1", "-an", "-c:v", "rawvideo", "-f", "null", "-"];
    }

    [GeneratedRegex(@"\bAll:\s*(?<value>[0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex SsimAll();

    [GeneratedRegex(@"\baverage:\s*(?<value>inf|[0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex PsnrAverage();

    /// <summary>解析 ssim 滤镜汇总行的 All 值。解析不出来返回 null，由调用方当成判据无法评估。</summary>
    internal static double? ParseSsim(string text) => Parse(SsimAll(), text);

    /// <summary>解析 psnr 滤镜汇总行的 average 值。inf 表示逐位相同，按 double 的正无穷返回。</summary>
    internal static double? ParsePsnr(string text) => Parse(PsnrAverage(), text);

    private static double? Parse(Regex pattern, string text)
    {
        // 滤镜的汇总行在 stderr 最后，取最后一条命中，避免逐帧日志里的中间值。
        MatchCollection matches = pattern.Matches(text ?? "");
        if (matches.Count == 0) return null;
        string value = matches[^1].Groups["value"].Value;
        if (value == "inf") return double.PositiveInfinity;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
    }
}
