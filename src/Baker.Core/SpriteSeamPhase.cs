using System.Numerics;

namespace Baker.Core;

/// <summary>
/// 精灵动画在离线渲染帧网格上的接缝相位。
/// 渲染器（wpe-render 冻结版 SpriteAnimation::GetAnimateFrame）的推进方式：帧表里的 frametime 是 float32，
/// 转 double 后使用；首帧剩余时间是完整的 frametime[0]；每个输出帧推进 dt，剩余时间 ≤ 推进量时切到下一帧并把溢出
/// 带进下一帧。所以第 n 个输出帧显示的精灵帧是 J(n·dt mod T)：J(t) 是满足 B_j ≤ t 的最大 j，B_j 是 float32
/// 帧时长的前缀和，T 是总和。到达边界的那一刻已经换帧（floor 语义）。
///
/// 解析周期 P 按十进制时长求出，P·dt 比 K·T 差 Δ = K·Σ(十进制 − float32)。t = 0 恰好是帧 0 的起点边界：
/// float32 偏大（0.07、0.1）时 Δ &lt; 0，第 P 帧退回上一精灵帧；偏小（0.04）时 Δ &gt; 0，第 P 帧仍在帧 0。
/// 其余采样点离边界至少 |B_j − 十进制边界|，方向与漂移相同，不会被一个周期的漂移推过边界。
/// 唯一的奇点是 t = 0 本身，因此处理方式是：起点不在 0 闭合时，整周期预热 P 帧再开始录。
/// 整周期预热对所有精确周期分量相位不变（它们在 P 上精确回到原位），只把精灵的采样窗口移出 t = 0 这个边界奇点。
/// 是否闭合一律按 float32 精确有理数逐帧判定，不按"半个精灵帧"之类的经验余量。
/// </summary>
public static class SpriteSeamPhase
{
    public enum Verdict { Closed, Mismatch, Undetermined }

    /// <summary>判定结果：预热帧数（0 或 P）与两个起点各自的逐帧判定。</summary>
    public sealed record Selection(ulong? WarmupFrames, Verdict AtOrigin, Verdict? AfterOnePeriod);

    /// <summary>
    /// 从 .tex 读出渲染器实际使用的 float32 帧时长，解析顺序与 TexImageParser::ParseHeader 一致：
    /// TEXV/TEXI 头、TEXB 各版本的 mip 体、TEXS 帧表。坏 imageId 的帧渲染器会跳过，这里同样跳过。
    /// </summary>
    public static bool TryReadFrameTimes(byte[] data, out float[] frameTimes)
    {
        frameTimes = [];
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false));
            if (ReadVersion(reader, "TEXV") != 5 || ReadVersion(reader, "TEXI") != 1) return false;
            reader.ReadInt32();
            uint flags = reader.ReadUInt32();
            if ((flags & 4u) == 0) return false;
            for (int i = 0; i < 5; ++i) reader.ReadInt32();
            int texb = ReadVersion(reader, "TEXB");
            if (texb is < 1 or > 4) return false;
            int count = reader.ReadInt32();
            if (count < 0) return false;
            if (texb >= 3) reader.ReadInt32();
            if (texb >= 4) reader.ReadInt32();
            var hasSize = new bool[count];
            for (int image = 0; image < count; ++image)
            {
                int mips = reader.ReadInt32();
                if (mips < 0) return false;
                hasSize[image] = mips > 0;
                for (int mip = 0; mip < mips; ++mip)
                {
                    reader.ReadInt32(); reader.ReadInt32();
                    if (texb >= 2) { reader.ReadInt32(); reader.ReadInt32(); }
                    int size = reader.ReadInt32();
                    if (size < 0 || reader.BaseStream.Position + size > reader.BaseStream.Length) return false;
                    reader.BaseStream.Seek(size, SeekOrigin.Current);
                }
            }
            int texs = ReadVersion(reader, "TEXS");
            if (texs is < 1 or > 3) return false;
            int frames = reader.ReadInt32();
            if (frames <= 0) return false;
            if (texs >= 3) { reader.ReadInt32(); reader.ReadInt32(); }
            var times = new List<float>(frames);
            for (int frame = 0; frame < frames; ++frame)
            {
                int imageId = reader.ReadInt32();
                float time = reader.ReadSingle();
                for (int i = 0; i < 6; ++i) reader.ReadInt32();
                if (imageId < 0 || imageId >= count || !hasSize[imageId]) continue;
                times.Add(time);
            }
            if (times.Count == 0 || times.Any(x => !float.IsFinite(x) || x <= 0f)) return false;
            frameTimes = times.ToArray();
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }

    private static int ReadVersion(BinaryReader reader, string stamp)
    {
        byte[] bytes = reader.ReadBytes(9);
        if (bytes.Length != 9 || bytes[8] != 0 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != stamp) return -1;
        return int.TryParse(System.Text.Encoding.ASCII.GetString(bytes, 4, 4), out int version) ? version : -1;
    }

    /// <summary>
    /// 对所有精灵帧表，先判起点 0；不闭合时判整周期预热 P。两者都能逐帧证明闭合才给预热值；
    /// 任一起点出现无法与边界分开的采样点（double 累加误差之内）时不下结论，交给编码后闭合检验。
    /// </summary>
    public static Selection Select(IReadOnlyList<float[]> tables, uint fpsNumerator, uint fpsDenominator, ulong periodFrames)
    {
        Verdict origin = Combine(tables.Select(t => Verify(t, fpsNumerator, fpsDenominator, 0, periodFrames)));
        if (origin == Verdict.Closed) return new(0, origin, null);
        if (origin == Verdict.Undetermined) return new(null, origin, null);
        Verdict shifted = Combine(tables.Select(t => Verify(t, fpsNumerator, fpsDenominator, periodFrames, periodFrames)));
        return new(shifted == Verdict.Closed ? periodFrames : null, origin, shifted);
    }

    private static Verdict Combine(IEnumerable<Verdict> verdicts)
    {
        var all = verdicts.ToArray();
        return all.Contains(Verdict.Mismatch) ? Verdict.Mismatch : all.Contains(Verdict.Undetermined) ? Verdict.Undetermined : Verdict.Closed;
    }

    /// <summary>
    /// 逐帧判定 [start, start+P) 里每个输出帧的精灵帧与 P 帧之后相同。时间与边界都换成同一分母的整数精确比较；
    /// 采样点离边界的距离不超过渲染器 double 累加误差上界时记为不确定（t = 0 是初始状态，精确，不计误差）。
    /// </summary>
    public static Verdict Verify(IReadOnlyList<float> frameTimes, uint fpsNumerator, uint fpsDenominator, ulong startFrame, ulong periodFrames)
    {
        if (frameTimes.Count == 0 || fpsNumerator == 0 || fpsDenominator == 0 || periodFrames == 0) return Verdict.Undetermined;
        var mantissas = new BigInteger[frameTimes.Count];
        var exponents = new int[frameTimes.Count];
        int scale = 0;
        for (int i = 0; i < frameTimes.Count; ++i)
        {
            if (!float.IsFinite(frameTimes[i]) || frameTimes[i] <= 0f) return Verdict.Undetermined;
            int bits = BitConverter.SingleToInt32Bits(frameTimes[i]);
            int biased = (bits >> 23) & 0xFF, fraction = bits & 0x7FFFFF;
            (mantissas[i], exponents[i]) = biased == 0 ? (new BigInteger(fraction), 149) : (new BigInteger(fraction | 0x800000), 150 - biased);
            scale = Math.Max(scale, exponents[i]);
        }
        // 单位：1 / (fps_num · 2^scale) 秒。帧 n 的时刻 = n · fps_den · 2^scale。
        var boundaries = new BigInteger[frameTimes.Count + 1];
        for (int i = 0; i < frameTimes.Count; ++i)
            boundaries[i + 1] = boundaries[i] + (mantissas[i] << (scale - exponents[i])) * fpsNumerator;
        BigInteger total = boundaries[^1];
        BigInteger step = new BigInteger(fpsDenominator) << scale;
        double unitSeconds = Math.ScaleB(1.0 / fpsNumerator, -scale);
        double dt = (double)fpsDenominator / fpsNumerator;
        double longest = Math.Max(frameTimes.Max(), dt), shortest = frameTimes.Min();
        // 每帧最多一次减法加每次换帧两次减法，每次舍入不超过 2^-53 · 操作数上界；取 2 倍余量。
        double perFrameError = (3 + 2 * Math.Ceiling(dt / shortest)) * Math.ScaleB(longest, -52);

        (int Index, bool Separated) Sample(ulong frame)
        {
            BigInteger t = BigInteger.Remainder(new BigInteger(frame) * step, total);
            int lo = 0, hi = frameTimes.Count - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (boundaries[mid] <= t) lo = mid; else hi = mid - 1; }
            if (frame == 0) return (lo, true);
            BigInteger distance = BigInteger.Min(t - boundaries[lo], boundaries[lo + 1] - t);
            return (lo, (double)distance * unitSeconds > frame * perFrameError);
        }

        bool undetermined = false;
        for (ulong offset = 0; offset < periodFrames; ++offset)
        {
            var here = Sample(startFrame + offset);
            var next = Sample(checked(startFrame + periodFrames + offset));
            if (!here.Separated || !next.Separated) { undetermined = true; continue; }
            if (here.Index != next.Index) return Verdict.Mismatch;
        }
        return undetermined ? Verdict.Undetermined : Verdict.Closed;
    }
}
