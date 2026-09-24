using System.Globalization;

namespace Baker.Core;

/// <summary>
/// 按帧号从恒定帧率视频里取一段连续帧，给出 ffmpeg 的输入参数与一段滤镜。凡是"取第 K 帧起的几帧"都走这里，
/// 不在调用点各写一套 -ss 偏移。
/// <para>
/// 为什么不能用 <c>-ss (K±0.5)/fps</c>：自带 ffmpeg 8.1.2 实测，-ss 的秒数先截断到微秒、再按流时基四舍五入，
/// 然后保留 pts 不小于它的帧。时基等于 1/fps 时（本产品整数帧率的 master 与播放版都是），60/1 下
/// <c>-ss (K-0.5)/60</c> 只有 K≡2 (mod 3) 拿到第 K 帧、其余拿到第 K-1 帧；时基细的 59.94 却总是对。
/// 同一个写法的偏移随帧率与时基变，挑哪个半帧偏移都修不好。
/// </para>
/// <para>
/// 这里 -ss 只负责"跳到附近"：落在目标之前 <see cref="SeekMarginFrames"/> 个整帧边界上；真正取哪几帧由
/// 按时间戳的半帧窗口决定。ffmpeg 在 seek 点上多留一帧少留一帧、舍入往哪边走，都只影响窗口之外的帧。
/// 余量取 16 帧，也让目标帧不可能是 seek 选中的 HEVC CRA 的前导帧（前导帧解不出来会被丢掉）。
/// </para>
/// </summary>
public static class ExactFrameRange
{
    /// <summary>seek 点落在第一帧之前的整帧数。</summary>
    public const ulong SeekMarginFrames = 16;

    /// <summary>-t 在窗口之后多读的帧数，免得最后一帧卡在读取上界上。</summary>
    public const ulong ReadAheadFrames = 2;

    /// <param name="Arguments">放在 <c>-i video</c> 前面的输入选项，已经包含 <c>-i video</c> 本身。</param>
    /// <param name="Filter">接在这个输入后面的滤镜链：只放行窗口内的帧，并把时间戳归零。</param>
    public sealed record FfmpegInput(string[] Arguments, string Filter);

    /// <summary>第 <paramref name="firstFrame"/> 帧起、共 <paramref name="frameCount"/> 帧（帧号从 0 数、按呈现顺序）。</summary>
    public static FfmpegInput Build(string video, ulong firstFrame, ulong frameCount, uint numerator, uint denominator)
    {
        if (string.IsNullOrWhiteSpace(video)) throw new ArgumentException("按帧号取帧需要输入视频路径。", nameof(video));
        if (frameCount == 0) throw new ArgumentException("按帧号取帧至少要取一帧。", nameof(frameCount));
        if (numerator == 0 || denominator == 0) throw new ArgumentException("帧率的分子分母必须为正。");
        ulong seekFrame = firstFrame > SeekMarginFrames ? firstFrame - SeekMarginFrames : 0;
        ulong lead = firstFrame - seekFrame;
        var arguments = new List<string>(6);
        if (seekFrame > 0) arguments.AddRange(["-ss", Seconds(seekFrame, numerator, denominator)]);
        arguments.AddRange(["-t", Seconds(checked(lead + frameCount + ReadAheadFrames), numerator, denominator), .. FfmpegTool.Input(video)]);
        // -ss 之后第 n 帧（原帧号 seekFrame+n）的 t 是 n 个帧长；窗口两端各让半帧，浮点与微秒截断都碰不到边。
        string filter = $"select='gte(t\\,{Seconds(lead - 0.5, numerator, denominator)})*" +
            $"lt(t\\,{Seconds(lead + frameCount - 0.5, numerator, denominator)})',setpts=PTS-STARTPTS";
        return new([.. arguments], filter);
    }

    /// <summary>帧数换算成 ffmpeg 时间参数（秒，九位小数）。</summary>
    public static string Seconds(double frames, uint numerator, uint denominator) =>
        (frames * denominator / numerator).ToString("F9", CultureInfo.InvariantCulture);
}
