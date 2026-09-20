using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 播放版成品的画质判据：抽样若干帧，算成品对无损 master 的 SSIM/PSNR，
/// 与「同一 master 的 libx264 fast crf16 成品」的 SSIM 参照值按比例比较。
/// 不达标先升一档质量重编，升满仍不达标回退软件编码。
/// 这是新增判据，不替换也不放宽任何既有判据（接缝、帧数、时基、哈希那些照旧各自把关）。
/// </summary>
public static partial class PlaybackQualityGate
{
    /// <summary>抽样帧数下限：均匀取样再加首尾两帧，任务书要求至少 9 帧。</summary>
    public const int MinimumSamples = 9;

    /// <summary>硬件档位的 SSIM 不得低于参照值的这个比例。可由 bake 请求覆盖。</summary>
    public const double DefaultRatio = 0.98;

    /// <summary>
    /// libx264 fast crf16 在本项目 1080p 素材上的 SSIM(All) 参照值。
    /// 这是标定值而不是物理常数：换素材或换 ffmpeg 版本都应重新标定，bake 请求可直接覆盖。
    /// </summary>
    public const double DefaultReferenceSsim = 0.9948;

    /// <summary>判据用的指标名，写进 bake.json 供后续比对时对齐口径。</summary>
    public const string MetricName = "ssim_all";

    /// <summary>
    /// 抽样帧号：首帧、尾帧，加中间均匀取样，合计至少 <see cref="MinimumSamples"/> 帧。
    /// 帧数不足时退化为「每帧都取」。返回值升序且不重复。
    /// </summary>
    public static ulong[] SampleFrames(ulong frames, int samples = MinimumSamples)
    {
        if (frames == 0) return [];
        int want = Math.Max(samples, MinimumSamples);
        if (frames <= (ulong)want) return [.. Enumerable.Range(0, (int)frames).Select(i => (ulong)i)];
        var picked = new SortedSet<ulong> { 0, frames - 1 };
        // 均匀落点：want-1 段的等分点，首尾已在集合里，重复落点被 SortedSet 吞掉后再补也无意义，
        // 因为 frames > want 时等分点必然互不相同。
        for (int i = 1; i < want - 1; i++) picked.Add((ulong)((double)i / (want - 1) * (frames - 1)));
        return [.. picked];
    }

    /// <summary>抽样 select 表达式：只放行抽中的帧号，再把 PTS 重排成连续，供逐帧对齐比对。</summary>
    public static string SelectExpression(IReadOnlyList<ulong> frames) =>
        $"select='{string.Join("+", frames.Select(n => $"eq(n\\,{n.ToString(CultureInfo.InvariantCulture)})"))}',setpts=N/TB";

    /// <summary>成品分支与 master 分支在滤镜图里的标签，取得够怪以免和编码滤镜里的标签撞名。</summary>
    private const string ProductLabel = "gateproduct";
    private const string MasterLabel = "gatemaster";

    /// <summary>
    /// 把编码用的 filter_complex 改造成 master 侧的比对分支：master 是整张捕获、成品是裁剪后的画面，
    /// 几何和色彩都不同，必须让 master 走完同一条裁剪/打包/bt709 滤镜才有可比性。
    /// 输入标签换到第二路，抽样 select 插在最前面（只让抽中的帧走完后面的裁剪与色彩转换），输出标签改名。
    /// </summary>
    public static string MasterBranch(string encodeFilter, IReadOnlyList<ulong> frames) =>
        encodeFilter.Replace("[0:v]", $"[1:v]{SelectExpression(frames)},", StringComparison.Ordinal)
            .Replace("[packed]", $"[{MasterLabel}]", StringComparison.Ordinal);

    /// <summary>成品与 master 的比对滤镜图。成品已经是裁剪后的画面，只需要抽同一批帧。</summary>
    public static string CompareGraph(string encodeFilter, IReadOnlyList<ulong> frames, string metric) =>
        $"{MasterBranch(encodeFilter, frames)};[0:v]{SelectExpression(frames)}[{ProductLabel}];" +
        $"[{ProductLabel}][{MasterLabel}]{metric}";

    /// <summary>
    /// 算一个指标的 ffmpeg 参数。第一路是成品，第二路是无损 master，输出丢进 null。
    /// metric 只接受 ssim 或 psnr —— 包内 ffmpeg 两个滤镜都在，不需要在 C# 里自己解帧算。
    /// </summary>
    public static string[] MetricArguments(string metric, string product, string master, string encodeFilter,
        IReadOnlyList<ulong> frames) =>
        ["-hide_banner", "-nostdin", "-i", product, "-i", master,
            "-filter_complex", CompareGraph(encodeFilter, frames, metric), "-c:v", "rawvideo", "-f", "null", "-"];

    /// <summary>Decode and convert each input once for both metrics; split only the selected frames.</summary>
    public static string[] MetricsArguments(string product, string master, string encodeFilter,
        IReadOnlyList<ulong> frames)
    {
        string graph = $"{MasterBranch(encodeFilter, frames)};[0:v]{SelectExpression(frames)}[{ProductLabel}];" +
            $"[{ProductLabel}]split=2[gateps][gatepp];[{MasterLabel}]split=2[gatems][gatemp];" +
            "[gateps][gatems]ssim[gatessim];[gatepp][gatemp]psnr[gatepsnr]";
        // The packaged FFmpeg omits wrapped_avframe, the null muxer's default encoder.
        return ["-hide_banner", "-nostdin", "-nostats", "-i", product, "-i", master,
            "-filter_complex", graph, "-map", "[gatessim]", "-map", "[gatepsnr]",
            "-an", "-c:v", "rawvideo", "-f", "null", "-"];
    }

    // Seek both videos to the same short rational-time window instead of decoding
    // the whole loop merely to discard every frame except the quality samples.
    internal static string[] SampleMetricArguments(string product, string master, string encodeFilter,
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
    public static double? ParseSsim(string text) => Parse(SsimAll(), text);

    /// <summary>解析 psnr 滤镜汇总行的 average 值。inf 表示逐位相同，按 double 的正无穷返回。</summary>
    public static double? ParsePsnr(string text) => Parse(PsnrAverage(), text);

    private static double? Parse(Regex pattern, string text)
    {
        // 滤镜的汇总行在 stderr 最后，取最后一条命中，避免逐帧日志里的中间值。
        MatchCollection matches = pattern.Matches(text ?? "");
        if (matches.Count == 0) return null;
        string value = matches[^1].Groups["value"].Value;
        if (value == "inf") return double.PositiveInfinity;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
    }

    /// <summary>判据阈值：参照 SSIM 乘比例。</summary>
    public static double Threshold(double referenceSsim, double ratio) => referenceSsim * ratio;

    /// <summary>成品 SSIM 是否达标。测不出 SSIM 时算不达标，不放过。</summary>
    public static bool Passes(double? ssim, double referenceSsim, double ratio) =>
        ssim is double value && value >= Threshold(referenceSsim, ratio);

    /// <summary>判据不达标后该做什么：还能升档就升档重编，升满就回退软件编码。</summary>
    public const string ActionAccepted = "accepted";
    public const string ActionEscalated = "escalated";
    public const string ActionFellBack = "fell_back_to_software";
    public const string ActionRejected = "rejected";

    /// <summary>
    /// 汇总 bake.json 的 playback_quality_gate 段：指标、抽样帧、参照与阈值、实测值、升档次数与最终动作。
    /// </summary>
    public static JsonObject Summarize(IReadOnlyList<ulong> frames, double referenceSsim, double ratio,
        double? ssim, double? psnr, int qualityStep, string action, string? note = null) => new()
    {
        ["metric"] = MetricName,
        ["sampled_frames"] = new JsonArray([.. frames.Select(n => (JsonNode?)JsonValue.Create(n))]),
        ["reference_encoder"] = "libx264 fast crf16",
        ["reference_ssim"] = Math.Round(referenceSsim, 6),
        ["ratio"] = ratio,
        ["threshold"] = Math.Round(Threshold(referenceSsim, ratio), 6),
        ["measured_ssim"] = ssim is null ? null : Math.Round(ssim.Value, 6),
        // PSNR 只记录，不参与判定：它对结构性瑕疵不敏感，判据用 SSIM。
        ["measured_psnr"] = psnr is null || double.IsInfinity(psnr.Value) ? null : Math.Round(psnr.Value, 3),
        ["measured_psnr_infinite"] = psnr is not null && double.IsInfinity(psnr.Value),
        ["quality_step"] = qualityStep,
        ["passed"] = action != ActionFellBack && action != ActionRejected,
        ["action"] = action,
        ["note"] = note,
    };
}
