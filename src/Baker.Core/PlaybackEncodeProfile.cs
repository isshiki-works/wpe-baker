using System.Globalization;

namespace Baker.Core;

internal sealed record PlaybackEncodeProfile(string Encoder, string Preset, string Crf, string PixelFormat, string ColorFilter,
    string Kind = PlaybackEncoderSelection.Software, bool Lossless = false, int QualityStep = 0)
{
    /// <summary>画质判据不达标时最多再升这么多档；升满仍不达标就回退软件编码，不无限重编。</summary>
    internal const int MaximumQualityStep = 2;

    /// <summary>
    /// MF 的 <c>-quality</c> 是 0..100 且越大越好，与 CRF 完全反向，所以单列一张阶梯表，不套用 Crf 数值。
    /// 起点 80 是实测能编出与 libx264 crf16 同量级体积的一档；上面两档留给画质判据升档用。
    /// </summary>
    private static readonly int[] MfQualityLadder = [80, 90, 96];

    /// <summary>
    /// NVENC 起始 cq。cq16 的成品是 x264 crf16 的 1.8–2.7 倍；cq22 为 0.79–1.56 倍，画质门 SSIM 0.9918–0.9999（门限 0.9749），
    /// cq19 为 1.55–2.16 倍（HW1b 实测，3572877776 / 3441873795 / 3000562427）。不达标仍按 22→19→16 升档。
    /// </summary>
    private const string NvencStartCq = "22";

    internal const string Bt709Filter =
        "scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuv420p,setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709";

    internal static string SelectPlaybackEncoder(uint width, uint height, uint numerator, uint denominator)
    {
        // H.264 level 5.2 limits from FFmpeg's h264_levels.c; also avoid the observed
        // >4096-pixel H.264 hardware-decode failure. This is a selection rule, not device certification.
        // 数字与出处集中在 HardwareDecodeDimensions.H264 表里。
        var h264 = HardwareDecodeDimensions.H264;
        ulong blocks = ((ulong)width + 15) / 16 * (((ulong)height + 15) / 16);
        return width > h264.MaximumWidth || height > h264.MaximumHeight || blocks > h264.MaximumMacroblocks!.Value ||
            (UInt128)blocks * numerator > (UInt128)h264.MaximumMacroblocksPerSecond!.Value * denominator ? "libx265" : "libx264";
    }

    /// <summary>把软件编码器名换成同一编解码的硬件档位；档位为软件时原样返回。</summary>
    internal static string HardwareEncoder(string softwareEncoder, string kind) => (softwareEncoder, kind) switch
    {
        (_, PlaybackEncoderSelection.Software) => softwareEncoder,
        ("libx265", PlaybackEncoderSelection.Vulkan) => "hevc_vulkan",
        ("libx264", PlaybackEncoderSelection.Vulkan) => "h264_vulkan",
        ("libx265", PlaybackEncoderSelection.Mf) => "hevc_mf",
        ("libx264", PlaybackEncoderSelection.Mf) => "h264_mf",
        ("libx265", PlaybackEncoderSelection.Nvenc) => "hevc_nvenc",
        ("libx264", PlaybackEncoderSelection.Nvenc) => "h264_nvenc",
        ("libx265", PlaybackEncoderSelection.Qsv) => "hevc_qsv",
        ("libx264", PlaybackEncoderSelection.Qsv) => "h264_qsv",
        ("libx265", PlaybackEncoderSelection.Amf) => "hevc_amf",
        ("libx264", PlaybackEncoderSelection.Amf) => "h264_amf",
        _ => throw new ArgumentException($"No {kind} encoder corresponds to {softwareEncoder}."),
    };

    /// <summary>硬件档位的速度/质量取向：都取各自的“质量优先但仍是硬件实时”的中档。</summary>
    internal static string PresetFor(string kind) => kind switch
    {
        // mf 这一格存的是 -rate_control 的模式名：quality 是 MF 里语义最接近恒定质量的一档。
        // 另一个选择是 u_vbr（未约束可变码率），但它要给目标码率、与 crf16 没有可对齐的量，故不用。
        PlaybackEncoderSelection.Mf => "quality",
        PlaybackEncoderSelection.Nvenc => "p5",
        PlaybackEncoderSelection.Qsv => "medium",
        PlaybackEncoderSelection.Amf => "quality",
        _ => "fast",
    };

    /// <summary>硬件编码器要的输入格式。NVENC 直接吃 yuv420p，QSV/AMF 走 nv12；两者编码后的色度语义一致。</summary>
    internal static string PixelFormatFor(string kind) => kind switch
    {
        // MF 的 MFT 原生输入就是 NV12，给 yuv420p 只会多一次转换。
        PlaybackEncoderSelection.Mf or PlaybackEncoderSelection.Qsv or PlaybackEncoderSelection.Amf => "nv12",
        _ => "yuv420p",
    };

    internal static PlaybackEncodeProfile Create(uint width, uint height, uint numerator, uint denominator, bool losslessTest,
        string kind = PlaybackEncoderSelection.Software)
    {
        // 需要无损 master 的路径使用软件 RGB 编码；直编路径保留必要原帧做检查。
        // 档位取 ultrafast 而不是 veryfast：crf 0 下两者都走 x264 的无损路径，解码像素逐位相同，
        // 只是 ultrafast 换成 CAVLC 并关掉去块，把 master 这一路的 CPU 砍掉一大截——编码约 1/4，
        // 解码约 1/2.6（master 在一次烘焙里要被整片解码 2~3 次，省的是两头）。代价是 master 大约 +19% 字节，
        // 而 master 只是本地中间文件，没人依赖它的字节数。GOP 也不变：keyint 仍是 250、
        // -force_key_frames 照旧在指定帧落 IDR，所以交叉淡化的切点与 stream copy 范围一字不动。
        // 实测见 reports-20260916/perf-master-encode.md。
        if (losslessTest) return new("libx264rgb", "ultrafast", "0", "rgb24", "format=rgb24", Lossless: true);
        string software = SelectPlaybackEncoder(width, height, numerator, denominator);
        return new(HardwareEncoder(software, kind), PresetFor(kind), kind == PlaybackEncoderSelection.Nvenc ? NvencStartCq : "16",
            PixelFormatFor(kind), Bt709Filter, kind);
    }

    /// <summary>
    /// 各档位在同一目标质量下的码率控制参数。软件是 CRF 16；硬件取语义最接近“恒定质量”的模式，
    /// 并沿用同一个质量数值，便于在 bake.json 与 ffprobe 结果里直接比对。
    /// </summary>
    internal string[] QualityArguments()
    {
        string q = StepQuantizer();
        return Kind switch
        {
            // MF：rate_control quality + quality 0..100 是 MF 里唯一与 CRF 同类的恒定质量模式。
            PlaybackEncoderSelection.Mf => ["-rate_control", Preset, "-quality", StepMfQuality()],
            // NVENC：vbr + cq 且 b:v 0 即恒定质量模式，是 NVENC 里语义最接近 CRF 的一档。
            PlaybackEncoderSelection.Nvenc => ["-preset", Preset, "-rc", "vbr", "-cq", q, "-b:v", "0"],
            // QSV：global_quality 就是 ICQ 的质量值。
            PlaybackEncoderSelection.Qsv => ["-preset", Preset, "-global_quality", q],
            // AMF：cqp 固定量化，I/P/B 取同一个值。
            PlaybackEncoderSelection.Amf => ["-quality", Preset, "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q],
            _ => ["-preset", Preset, "-crf", q],
        };
    }

    /// <summary>画质判据不达标时升一档重编。升到上限仍不达标由调用方回退软件编码。</summary>
    internal PlaybackEncodeProfile Escalate() => this with { QualityStep = QualityStep + 1 };

    /// <summary>还能不能再升一档。</summary>
    internal bool CanEscalate => QualityStep < MaximumQualityStep;

    /// <summary>量化值档位：每升一档减 3，下限 10。第 0 档原样返回 Crf，保证无损 master 的 "0" 不被改写。</summary>
    private string StepQuantizer() => QualityStep <= 0 ? Crf
        : Math.Max(10, int.Parse(Crf, CultureInfo.InvariantCulture) - 3 * Math.Min(QualityStep, MaximumQualityStep))
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>MF 的质量档位，越大越好，取自 MfQualityLadder。</summary>
    private string StepMfQuality() =>
        MfQualityLadder[Math.Clamp(QualityStep, 0, MfQualityLadder.Length - 1)].ToString(CultureInfo.InvariantCulture);

    internal string[] OutputArguments(uint numerator, uint denominator, string output) =>
    [
        "-c:v", Encoder, .. QualityArguments(), "-pix_fmt", PixelFormat, "-fps_mode", "passthrough",
        "-enc_time_base", $"{denominator}:{numerator}", "-movie_timescale", numerator.ToString(CultureInfo.InvariantCulture),
        "-video_track_timescale", numerator.ToString(CultureInfo.InvariantCulture),
        // 无损 master 是本地中间文件，永远整文件顺序读；faststart 会把它整片再读写一遍，纯浪费。
        .. (Lossless ? Array.Empty<string>() : new[] { "-movflags", "+faststart" }), output
    ];
}
