namespace Periodica.Domain;

/// <summary>越限的一项：宽、高或亮度样本数，实际值与上限。</summary>
public sealed record DecodeViolation(string Measure, ulong Actual, ulong Limit);

/// <summary>
/// 硬件解码尺寸预检里的数值部分：补边到下限（两侧对称、偶数偏移）、裁剪区在捕获范围内对称扩大、按上限列出越限项。
/// 各编解码的上下限表（带出处与说明文字）、编码器选择与 plan 字段在 Baker.Core 的 HardwareDecodeDimensions。
/// </summary>
public static class DecodeDimensions
{
    /// <summary>4:2:0 色度二次采样要求编码宽高为偶数（FFmpeg yuv420p 与 ITU-T H.264/H.265 的 4:2:0 定义）。</summary>
    public const uint ChromaAlignment = 2;

    /// <summary>
    /// 把一边从 <paramref name="content"/> 补到不小于 <paramref name="minimum"/>：两侧对称、偏移取偶数，
    /// 这样 4:2:0 色度块不跨内容边界，且上下或左右翻转取样时内容矩形的 UV 不变。奇数内容多出的 1 像素补在末端。
    /// </summary>
    public static (uint Size, uint Offset) Grow(uint content, uint minimum)
    {
        ulong needed = minimum > content ? minimum - content : 0;
        ulong offset = (needed + 1) / 2;
        offset += offset % 2;
        ulong size = content + 2 * offset;
        size += size % ChromaAlignment;
        if (size > uint.MaxValue) throw new OverflowException("Padded dimension exceeds the supported range.");
        return ((uint)size, (uint)offset);
    }

    /// <summary>
    /// 把一维区间 [start, start + size) 在 [0, capture) 内对称扩大到 <paramref name="target"/>，起点取偶数；
    /// 目标不比原区大或捕获本身装不下时保留原区。
    /// </summary>
    public static (int Start, int Size) Expand(int start, int size, int target, int capture)
    {
        if (target <= size || capture < target) return (start, size);
        int extra = target - size;
        // 夹紧之后再取偶即可：夹紧前取偶是多余的（夹紧前后各取一次与只在夹紧后取一次处处相等，差分已核）。
        int begin = Math.Clamp(start - extra / 2, 0, capture - target);
        begin -= begin & 1;
        if (begin < 0 || begin + target > capture) return (start, size);
        return (begin, target);
    }

    /// <summary>按宽、高、亮度样本数的顺序列出越过上限的项；亮度样本数上限为 null 时不查这一项。</summary>
    public static List<DecodeViolation> Violations(uint width, uint height, uint maximumWidth, uint maximumHeight, ulong? maximumLumaSamples)
    {
        var violations = new List<DecodeViolation>();
        if (width > maximumWidth) violations.Add(new("width", width, maximumWidth));
        if (height > maximumHeight) violations.Add(new("height", height, maximumHeight));
        ulong luma = (ulong)width * height;
        if (maximumLumaSamples is ulong maximumLuma && luma > maximumLuma) violations.Add(new("luma_samples", luma, maximumLuma));
        return violations;
    }
}
