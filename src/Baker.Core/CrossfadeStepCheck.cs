namespace Baker.Core;

/// <summary>
/// 淡化自检里一步的读数。全部是 64px 瓦片 RGB MAE，0..255 通道尺度。
/// </summary>
/// <param name="K">步号：第 k 步是观众从上一帧看到 g[k]；k=0 是接缝那一步（上一帧是 f[P-1]）。</param>
/// <param name="StepWorstTile">成品这一步 M(shown[k] − shown[k−1]) 的最差瓦片。</param>
/// <param name="ReferenceWorstTile">原作参照步进（延续线 f[P+k]−f[P+k−1]、回归线 f[k]−f[k−1] 里有的那几条）的最差瓦片。</param>
/// <param name="ExtraWorstTile">成品相对最近那条参照多出的步进：逐瓦片取两条参照里较小的，再取最差。</param>
/// <param name="FirstOrderWorstTile">一阶项 M(Δ)/(C+1) 的最差瓦片（残差本身被摊到 C+1 份）。</param>
/// <param name="SecondOrderWorstTile">二阶项 min(k, C−k)/(C+1)·M(Δ_k − Δ_{k−1}) 的最差瓦片（残差的帧间变化率）。</param>
/// <param name="MaximumExcessOverBound">各瓦片、各参照上"实测多出量 − 推导上界"的最大值（浮点，只作记录）。</param>
/// <param name="WithinRounding">
/// 每个瓦片、每条参照都满足"实测多出量 ≤ 推导上界 + 取整余量"。这一项在整数域上判：两边同乘 瓦片样本数×(C+1) 后都是整数和，
/// 不受浮点舍入影响——淡化输出的截断误差在平坦区能让整块瓦片正好顶到余量上（实测到过 0.996），浮点比较会在边界上误报。
/// </param>
public sealed record CrossfadeStepReading(int K, double StepWorstTile, double ReferenceWorstTile, double ExtraWorstTile,
    double FirstOrderWorstTile, double SecondOrderWorstTile, double MaximumExcessOverBound,
    int ExcessTileX, int ExcessTileY, string ExcessReference, bool WithinRounding);

/// <summary>
/// 淡化实现自检（原残差掩盖的第二层）。淡化后 g[k] = (1−w_k)·f[k] + w_k·f[P+k]，w_k = (C−k)/(C+1)，
/// Δ_k = f[P+k] − f[k]。观众在第 k 步看到的 g[k] − g[k−1] 与原作两条时间线的步进逐像素满足恒等式：
/// <code>
/// 延续线：g[k]−g[k−1] = (f[P+k]−f[P+k−1]) − Δ_k/(C+1) − k/(C+1)·(Δ_k − Δ_{k−1})
/// 回归线：g[k]−g[k−1] = (f[k]−f[k−1])     − Δ_{k−1}/(C+1) + (C−k)/(C+1)·(Δ_k − Δ_{k−1})
/// </code>
/// 所以逐瓦片"实测多出量"不会超过右边两项的瓦片 MAE 之和；k=0 只有延续线（上一帧是 f[P−1]），k=C 只有回归线
/// （g[C] = f[C]，f[P+C] 没有采集）。成品比上界多出来的只能是 8 位取整，超过 <see cref="ResidualMasking.CrossfadeSelfCheckRounding255"/>
/// 就是淡化实现（权重、混入帧、拼接）错了。这是内部自检，不是用户可调判据。
/// </summary>
public static class CrossfadeStepCheck
{
    /// <summary>
    /// 量第 <paramref name="k"/> 步。<paramref name="shownPrevious"/>/<paramref name="shownCurrent"/> 是成品里观众先后看到的两帧
    /// （k=0 时前者是 f[P−1]）；<paramref name="headPrevious"/>/<paramref name="headCurrent"/> 是 f[k−1]/f[k]（k=0 时前者为空）；
    /// <paramref name="wrapPrevious"/>/<paramref name="wrapCurrent"/> 是 f[P+k−1]/f[P+k]（k=C 时后者为空）。全部是 RGB24。
    /// </summary>
    public static CrossfadeStepReading Measure(int k, uint crossfadeFrames, int width, int height, int tileSize,
        ReadOnlySpan<byte> shownPrevious, ReadOnlySpan<byte> shownCurrent,
        ReadOnlySpan<byte> headPrevious, ReadOnlySpan<byte> headCurrent,
        ReadOnlySpan<byte> wrapPrevious, ReadOnlySpan<byte> wrapCurrent)
    {
        if (crossfadeFrames == 0 || crossfadeFrames > int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(crossfadeFrames), "淡化窗口必须为正。");
        int c = (int)crossfadeFrames;
        if (k < 0 || k > c) throw new ArgumentOutOfRangeException(nameof(k), "淡化自检的步号必须在 0..C 之间。");
        if (width <= 0 || height <= 0 || tileSize <= 0 || (long)width * height * 3 > int.MaxValue)
            throw new ArgumentException("RGB24 尺寸或瓦片边长无效。");
        int bytes = width * height * 3;
        bool forward = k < c, backward = k > 0;
        foreach (int length in new[] { shownPrevious.Length, shownCurrent.Length, headCurrent.Length, wrapPrevious.Length })
            if (length != bytes) throw new ArgumentException("淡化自检的帧尺寸与给定 RGB24 尺寸不一致。");
        if (headPrevious.Length != (backward ? bytes : 0))
            throw new ArgumentException("f[k−1] 只在 k ≥ 1 时提供，且尺寸必须一致。");
        if (wrapCurrent.Length != (forward ? bytes : 0))
            throw new ArgumentException("f[P+k] 只在 k < C 时提供，且尺寸必须一致。");

        double c1 = c + 1;
        double stepWorst = 0, referenceWorst = 0, extraWorst = 0, firstWorst = 0, secondWorst = 0;
        double excessMaximum = double.NegativeInfinity;
        int excessX = 0, excessY = 0;
        string excessReference = "";
        bool withinRounding = true;
        for (int y0 = 0; y0 < height; y0 += tileSize)
        {
            int y1 = Math.Min(height, y0 + tileSize);
            for (int x0 = 0; x0 < width; x0 += tileSize)
            {
                int x1 = Math.Min(width, x0 + tileSize);
                long step = 0, forwardExtra = 0, backwardExtra = 0, forwardReference = 0, backwardReference = 0;
                long residualCurrent = 0, residualPrevious = 0, residualChange = 0;
                for (int y = y0; y < y1; ++y)
                {
                    int row = y * width * 3;
                    for (int p = row + x0 * 3, end = row + x1 * 3; p < end; ++p)
                    {
                        int shown = shownCurrent[p] - shownPrevious[p];
                        step += Math.Abs(shown);
                        int wrapBefore = wrapPrevious[p], headNow = headCurrent[p];
                        if (forward)
                        {
                            int wrapNow = wrapCurrent[p];
                            int reference = wrapNow - wrapBefore;
                            forwardReference += Math.Abs(reference);
                            forwardExtra += Math.Abs(shown - reference);
                            int delta = wrapNow - headNow;
                            residualCurrent += Math.Abs(delta);
                            if (backward) residualChange += Math.Abs(delta - (wrapBefore - headPrevious[p]));
                        }
                        if (backward)
                        {
                            int headBefore = headPrevious[p];
                            int reference = headNow - headBefore;
                            backwardReference += Math.Abs(reference);
                            backwardExtra += Math.Abs(shown - reference);
                            residualPrevious += Math.Abs(wrapBefore - headBefore);
                        }
                    }
                }
                double samples = (double)(x1 - x0) * (y1 - y0) * 3;
                // 整数域判定：extra/samples ≤ residual/samples/(C+1) + m/(C+1)·change/samples + rounding
                // ⇔ extra·(C+1) ≤ residual + m·change + rounding·samples·(C+1)，m 是二阶项系数（延续线 k，回归线 C−k）。
                long sampleCount = (long)(x1 - x0) * (y1 - y0) * 3;
                long allowance = checked((long)Math.Round(ResidualMasking.CrossfadeSelfCheckRounding255 * sampleCount * (c + 1)));
                if (forward && checked(forwardExtra * (c + 1)) > checked(residualCurrent + k * residualChange + allowance))
                    withinRounding = false;
                if (backward && checked(backwardExtra * (c + 1)) > checked(residualPrevious + (c - k) * residualChange + allowance))
                    withinRounding = false;
                stepWorst = Math.Max(stepWorst, step / samples);
                // k=0 与 k=C 时二阶项系数为 0，Δ_k − Δ_{k−1} 也没有采集，residualChange 保持 0。
                double change = residualChange / samples;
                secondWorst = Math.Max(secondWorst, Math.Min(k, c - k) / c1 * change);
                double nearestExtra = double.PositiveInfinity;
                if (forward)
                {
                    double first = residualCurrent / samples / c1;
                    double extra = forwardExtra / samples;
                    Track(extra - (first + k / c1 * change), "continuation");
                    referenceWorst = Math.Max(referenceWorst, forwardReference / samples);
                    firstWorst = Math.Max(firstWorst, first);
                    nearestExtra = Math.Min(nearestExtra, extra);
                }
                if (backward)
                {
                    double first = residualPrevious / samples / c1;
                    double extra = backwardExtra / samples;
                    Track(extra - (first + (c - k) / c1 * change), "return");
                    referenceWorst = Math.Max(referenceWorst, backwardReference / samples);
                    firstWorst = Math.Max(firstWorst, first);
                    nearestExtra = Math.Min(nearestExtra, extra);
                }
                extraWorst = Math.Max(extraWorst, nearestExtra);

                void Track(double excess, string reference)
                {
                    if (excess <= excessMaximum) return;
                    excessMaximum = excess; excessX = x0; excessY = y0; excessReference = reference;
                }
            }
        }
        return new(k, stepWorst, referenceWorst, extraWorst, firstWorst, secondWorst, excessMaximum, excessX, excessY, excessReference,
            withinRounding);
    }

    /// <summary>量第 k 步并要求实测多出量不超过推导上界加取整余量；超出按内部错误抛 <see cref="InvalidOperationException"/>。</summary>
    public static CrossfadeStepReading Verify(int k, uint crossfadeFrames, int width, int height, int tileSize,
        ReadOnlySpan<byte> shownPrevious, ReadOnlySpan<byte> shownCurrent,
        ReadOnlySpan<byte> headPrevious, ReadOnlySpan<byte> headCurrent,
        ReadOnlySpan<byte> wrapPrevious, ReadOnlySpan<byte> wrapCurrent)
    {
        CrossfadeStepReading reading = Measure(k, crossfadeFrames, width, height, tileSize,
            shownPrevious, shownCurrent, headPrevious, headCurrent, wrapPrevious, wrapCurrent);
        if (!reading.WithinRounding)
            throw new InvalidOperationException(
                $"淡化实现自检失败（内部错误）：第 {k} 步在瓦片 ({reading.ExcessTileX},{reading.ExcessTileY}) 相对" +
                (reading.ExcessReference == "continuation" ? "延续线 f[P+k]−f[P+k−1]" : "回归线 f[k]−f[k−1]") +
                $" 多出的步进比由残差 Δ_k 推导的上界大 {reading.MaximumExcessOverBound:0.###}/255，" +
                $"超过 8 位取整余量 {ResidualMasking.CrossfadeSelfCheckRounding255}/255。成品淡化段与 w = (C−k)/(C+1)、" +
                "混入 f[P+k] 的公式不一致（权重或混入帧错位、拼接错帧）。这不是可调判据，不要放宽，去查淡化实现。");
        return reading;
    }
}
