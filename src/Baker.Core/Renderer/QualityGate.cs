using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 播放版成品的唯一画质门（C2.4b 把 master 路线的两种解码与 GPU 直编路线合成这一处）：抽样若干帧，算成品对参照的
/// SSIM/PSNR，与「同一 master 的 libx264 fast crf16 成品」的 SSIM 参照值按比例比较。master 路线不达标先升一档质量重编、
/// 升满仍不达标回退软件编码；GPU 直编路线不达标直接拒绝。比对本身交给 <see cref="IQualityComparer"/>。
/// 这是新增判据，不替换也不放宽任何既有判据（接缝、帧数、时基、哈希那些照旧各自把关）。
/// </summary>
public static class QualityGate
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
    /// 循环不超过这么多帧时，成品（与 master）各顺序解一遍、用 select 取出抽样帧；更长的循环按抽样帧逐个 seek 取窗口。
    /// 两种取法解出的抽样帧逐字节相同，只差耗时：顺序解的量随片长走，窗口的量只随 GOP 走（每帧从前一个关键帧解起）。
    /// 实测（9800X3D，16 线程）：8K HEVC 377 帧顺序解 4.7 s、9 个窗口 35 s；3840×1080 H.264 9000 帧顺序解 6.3 s、9 个窗口 4.6 s。
    /// </summary>
    public const ulong SingleDecodeMaximumFrames = 4096;

    /// <summary>写进 bake.json 的解码方式（master 路线在 quality_decode_scope，GPU 直编路线在 playback_quality_gate.decode_scope）。</summary>
    public const string SingleDecodeScope = "short_clip_single_decode";
    public const string FrameWindowsScope = "selected_frame_windows";

    /// <summary>这段循环是否按抽样帧逐个 seek 取窗口（否则一遍顺序解）。</summary>
    public static bool UsesFrameWindows(ulong loopFrames) => loopFrames > SingleDecodeMaximumFrames;

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
