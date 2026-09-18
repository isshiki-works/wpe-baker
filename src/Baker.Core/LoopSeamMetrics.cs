namespace Baker.Core;

/// <summary>One wrap's residual between the start frame and the frame exactly one period later.</summary>
public sealed record LoopWrapResidual(double GlobalRgbMae, double WorstTileRgbMae, int WorstTileX, int WorstTileY,
    int TileSize, int MaximumChannelDifference);

/// <summary>
/// 透明组 packed 帧（左半预乘色贡献 C = α·c，右半覆盖度 α 的灰度）两半分别算的接缝残差。
/// <paramref name="Color"/> 只在掩码内累加：任一帧覆盖度 &gt; 0，或任一帧 RGB 非零（加性粒子不写覆盖度，只在 RGB 半）；
/// 瓦片与整幅的分母仍是全部像素，和不透明组的瓦片平均同一口径。<paramref name="Coverage"/> 是覆盖度半，单通道。
/// 坐标都是各自半幅内的坐标。
/// </summary>
public sealed record PackedWrapResidual(LoopWrapResidual Color, LoopWrapResidual Coverage, long MaskedPixels, long HalfPixels)
{
    /// <summary>掩码像素占半幅的比例，只记录。</summary>
    public double MaskFraction => HalfPixels == 0 ? 0 : (double)MaskedPixels / HalfPixels;

    /// <summary>
    /// 第一层用的合并读数：合成到背景 B 的差 ΔC − Δα·B 里 ΔC 与 Δα 是两个独立分量，两半各自都要在限内，
    /// 所以最差瓦片与整幅都取两半里更大的那个。覆盖度半的瓦片 X 加上半宽，位置落在 packed 图坐标里不歧义。
    /// </summary>
    public LoopWrapResidual Combined(int halfWidth)
    {
        bool coverageWorst = Coverage.WorstTileRgbMae > Color.WorstTileRgbMae;
        return new(Math.Max(Color.GlobalRgbMae, Coverage.GlobalRgbMae),
            coverageWorst ? Coverage.WorstTileRgbMae : Color.WorstTileRgbMae,
            coverageWorst ? Coverage.WorstTileX + halfWidth : Color.WorstTileX,
            coverageWorst ? Coverage.WorstTileY : Color.WorstTileY,
            Color.TileSize, Math.Max(Color.MaximumChannelDifference, Coverage.MaximumChannelDifference));
    }
}

/// <summary>一张逐瓦片 MAE 图的摘要（0..255 刻度）：整幅按像素加权均值、最差瓦片与位置、瓦片中位数。</summary>
public sealed record TileMapSummary(int TileSize, double Global, double Worst, int WorstX, int WorstY, double Median, int Tiles);

/// <summary>
/// 纯托管、有界的 RGB24 瓦片度量。M_T(X) = T×T 瓦片内 |X| 的 RGB 三通道平均；边缘瓦片按实际像素数平均。
/// 掩码（每像素一个字节，0 表示该像素没有定义）只把差值置 0，瓦片面积不变，所以带掩码与不带掩码的读数同一量纲。
/// </summary>
public static class LoopSeamMetrics
{
    /// <summary>
    /// packed 帧（宽 2×<paramref name="halfWidth"/>）的接缝残差，两半分别算，见 <see cref="PackedWrapResidual"/>。
    /// 在无损 master 上掩码外两帧 RGB 都是 0，掩码不改变 RGB 半的读数；它保证 RGB 半只统计有覆盖或有加性贡献的像素，并记下覆盖范围。
    /// </summary>
    public static PackedWrapResidual PackedResidual(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after,
        int halfWidth, int height, int tileSize)
    {
        if (halfWidth <= 0 || height <= 0 || tileSize <= 0 || (long)halfWidth * 2 * height * 3 > int.MaxValue)
            throw new ArgumentException("Packed RGB24 dimensions or tile size are invalid.");
        int packedWidth = checked(halfWidth * 2), bytes = checked(packedWidth * height * 3);
        if (before.Length != bytes || after.Length != bytes)
            throw new ArgumentException("Both packed frames must match the supplied dimensions.");
        var layout = new TileLayout(halfWidth, height, tileSize);
        double[] color = new double[layout.Count], coverage = new double[layout.Count];
        int[] counts = new int[layout.Count];
        int colorMaximum = 0, coverageMaximum = 0;
        long masked = 0;
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < halfWidth; ++x)
            {
                int c = (y * packedWidth + x) * 3, a = c + halfWidth * 3, tile = layout.TileAt(x, y);
                counts[tile]++;
                int ar = Math.Abs(before[a] - after[a]), ag = Math.Abs(before[a + 1] - after[a + 1]), ab = Math.Abs(before[a + 2] - after[a + 2]);
                coverageMaximum = Math.Max(coverageMaximum, Math.Max(ar, Math.Max(ag, ab)));
                coverage[tile] += (ar + ag + ab) / 3.0;
                bool covered = before[a] != 0 || before[a + 1] != 0 || before[a + 2] != 0 || after[a] != 0 || after[a + 1] != 0 || after[a + 2] != 0;
                bool emitted = before[c] != 0 || before[c + 1] != 0 || before[c + 2] != 0 || after[c] != 0 || after[c + 1] != 0 || after[c + 2] != 0;
                if (!covered && !emitted) continue;
                ++masked;
                int r = Math.Abs(before[c] - after[c]), g = Math.Abs(before[c + 1] - after[c + 1]), b = Math.Abs(before[c + 2] - after[c + 2]);
                colorMaximum = Math.Max(colorMaximum, Math.Max(r, Math.Max(g, b)));
                color[tile] += (r + g + b) / 3.0;
            }
        LoopWrapResidual Summarize(double[] tiles, int maximum)
        {
            for (int i = 0; i < tiles.Length; ++i) tiles[i] /= counts[i];
            int worst = 0;
            for (int i = 1; i < tiles.Length; ++i) if (tiles[i] > tiles[worst]) worst = i;
            TileBounds bounds = layout.Bounds(worst);
            return new(WeightedMean(tiles, layout), tiles[worst], bounds.X, bounds.Y, tileSize, maximum);
        }
        return new(Summarize(color, colorMaximum), Summarize(coverage, coverageMaximum), masked, (long)halfWidth * height);
    }

    /// <summary>
    /// 两帧之间的接缝残差：整幅按像素加权的 RGB MAE，以及最差瓦片的 RGB MAE。两帧分别是候选起点帧
    /// 与它之后正好一个解析周期的帧，因此这个残差就是循环回绕时观察者看到的跳变。
    /// </summary>
    public static LoopWrapResidual WrapResidual(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after,
        int width, int height, int tileSize)
    {
        var layout = Layout(width, height, tileSize, before.Length, after.Length);
        double[] tiles = Differences(before, after, layout, default, out int maximum);
        TileMapSummary summary = Summarize(tiles, layout);
        return new(summary.Global, summary.Worst, summary.WorstX, summary.WorstY, tileSize, maximum);
    }

    /// <summary>M_T(a − b)，逐瓦片。</summary>
    public static double[] TileMae(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, int tileSize,
        ReadOnlySpan<byte> mask = default) =>
        Differences(a, b, Layout(width, height, tileSize, a.Length, b.Length, mask.Length), mask, out _);

    /// <summary>M_T(a − b)，逐瓦片，同时给出逐像素最大通道差。</summary>
    public static double[] TileMae(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, int tileSize,
        ReadOnlySpan<byte> mask, out int maximumChannelDifference) =>
        Differences(a, b, Layout(width, height, tileSize, a.Length, b.Length, mask.Length), mask, out maximumChannelDifference);

    /// <summary>
    /// 差分图 M_T((a − b) − (c − d))：有符号逐像素相减之后再取绝对值。接缝判据里 a,b 是成品接缝两帧、c,d 是原作同相位两帧，
    /// 读数就是"观众看到的那一步"与"原作那一步"之差。
    /// </summary>
    public static double[] DifferenceMap(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, ReadOnlySpan<byte> d,
        int width, int height, int tileSize, ReadOnlySpan<byte> mask = default)
    {
        var layout = Layout(width, height, tileSize, a.Length, b.Length, mask.Length);
        if (c.Length != a.Length || d.Length != a.Length) throw new ArgumentException("All four frames must match the supplied RGB24 dimensions.");
        var sums = new double[layout.Count];
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                int pixel = y * width + x;
                if (!mask.IsEmpty && mask[pixel] == 0) continue;
                int p = pixel * 3, total = 0;
                for (int channel = 0; channel < 3; ++channel)
                    total += Math.Abs(a[p + channel] - b[p + channel] - c[p + channel] + d[p + channel]);
                sums[layout.TileAt(x, y)] += total / 3.0;
            }
        return Normalize(sums, layout);
    }

    /// <summary>摘要一张瓦片图。</summary>
    public static TileMapSummary Summarize(double[] tiles, int width, int height, int tileSize) =>
        Summarize(tiles, Layout(width, height, tileSize, width * height * 3, width * height * 3));

    private static TileMapSummary Summarize(double[] tiles, TileLayout layout)
    {
        if (tiles.Length != layout.Count) throw new ArgumentException("The tile map does not match the tile layout.");
        int worst = 0;
        for (int i = 1; i < tiles.Length; ++i) if (tiles[i] > tiles[worst]) worst = i;
        TileBounds bounds = layout.Bounds(worst);
        double[] ordered = [.. tiles.Order()];
        double median = ordered.Length % 2 == 1 ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
        return new(layout.Edge, WeightedMean(tiles, layout), tiles[worst], bounds.X, bounds.Y, median, tiles.Length);
    }

    private static TileLayout Layout(int width, int height, int tileSize, int firstLength, int secondLength, int maskLength = 0)
    {
        if (width <= 0 || height <= 0 || tileSize <= 0 || (long)width * height * 3 > int.MaxValue)
            throw new ArgumentException("RGB24 dimensions or tile size are invalid.");
        int bytes = checked(width * height * 3);
        if (firstLength != bytes || secondLength != bytes)
            throw new ArgumentException("Both frames must match the supplied RGB24 dimensions.");
        if (maskLength != 0 && maskLength != width * height) throw new ArgumentException("The pixel mask must have one byte per pixel.");
        return new TileLayout(width, height, tileSize);
    }

    private static double[] Differences(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, TileLayout layout, ReadOnlySpan<byte> mask, out int maximum)
    {
        var sums = new double[layout.Count]; maximum = 0;
        for (int y = 0; y < layout.Height; ++y)
            for (int x = 0; x < layout.Width; ++x)
            {
                int pixel = y * layout.Width + x;
                if (!mask.IsEmpty && mask[pixel] == 0) continue;
                int p = pixel * 3, tile = layout.TileAt(x, y);
                int r = Math.Abs(a[p] - b[p]), g = Math.Abs(a[p + 1] - b[p + 1]), blue = Math.Abs(a[p + 2] - b[p + 2]);
                maximum = Math.Max(maximum, Math.Max(r, Math.Max(g, blue))); sums[tile] += (r + g + blue) / 3.0;
            }
        return Normalize(sums, layout);
    }

    private static double[] Normalize(double[] sums, TileLayout layout)
    {
        for (int i = 0; i < sums.Length; ++i) { TileBounds b = layout.Bounds(i); sums[i] /= b.Width * b.Height; }
        return sums;
    }

    private static double WeightedMean(double[] values, TileLayout layout)
    {
        double sum = 0; long pixels = 0;
        for (int i = 0; i < values.Length; ++i) { TileBounds b = layout.Bounds(i); int n = b.Width * b.Height; sum += values[i] * n; pixels += n; }
        return sum / pixels;
    }

    private readonly record struct TileBounds(int X, int Y, int Width, int Height);
    private sealed class TileLayout(int width, int height, int edge)
    {
        public int Width { get; } = width; public int Height { get; } = height; public int Edge { get; } = edge;
        private int Columns { get; } = (width + edge - 1) / edge;
        public int Count { get; } = ((width + edge - 1) / edge) * ((height + edge - 1) / edge);
        public int TileAt(int x, int y) => y / Edge * Columns + x / Edge;
        public TileBounds Bounds(int tile) { int x = tile % Columns * Edge, y = tile / Columns * Edge; return new(x, y, Math.Min(Edge, Width - x), Math.Min(Edge, Height - y)); }
    }
}
