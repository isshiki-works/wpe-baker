using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Baker.Core;

/// <summary>
/// 逐帧扫描的向量化内核：覆盖范围（alpha 或 RGBA 非零像素的包围盒）与 alpha 极值，以及首个非不透明像素。
/// 结果与逐像素标量扫描逐位相同：包围盒与极值都是 min/max，按行分块并行后再合并，与扫描顺序无关；
/// 首个非不透明像素取各块里最小的索引。Vector256 不可用或行宽不足一个向量时退回标量路径。
/// </summary>
internal static class FrameScan
{
    /// <summary>
    /// 扫描并行度。环境变量 WPE_BAKER_FRAME_SCAN_THREADS 可覆盖（1..64）；默认取逻辑核数的四分之一并夹在 1..4：
    /// 8 核 16 线程的机器用 4 线程，给同机的渲染器与编码器留核。扫描本身受内存带宽限制，线程再多也不值。
    /// </summary>
    internal static int Threads { get; } = ResolveThreads();

    private static int ResolveThreads()
    {
        string? configured = Environment.GetEnvironmentVariable("WPE_BAKER_FRAME_SCAN_THREADS");
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 1)
            return Math.Min(value, 64);
        return Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    }

    /// <summary>每块至少扫这么多字节才值得分线程；Parallel.For 本身有几十微秒的调度开销，小帧单线程更快。</summary>
    private const int MinimumBytesPerBlock = 1 << 20;

    /// <summary>一帧（或若干帧的并集）的包围盒与 alpha 极值；没有内容时 MaxX/MaxY 为 -1、MinX/MinY 为宽/高。</summary>
    internal readonly record struct BoundsResult(int MinX, int MaxX, int MinY, int MaxY, byte MinAlpha, byte MaxAlpha)
    {
        internal BoundsResult Union(BoundsResult other) => new(
            Math.Min(MinX, other.MinX), Math.Max(MaxX, other.MaxX), Math.Min(MinY, other.MinY), Math.Max(MaxY, other.MaxY),
            Math.Min(MinAlpha, other.MinAlpha), Math.Max(MaxAlpha, other.MaxAlpha));
    }

    private static int BlockCount(int bytes, int units, int threads) =>
        Math.Clamp(Math.Min(bytes / MinimumBytesPerBlock, units), 1, Math.Max(1, threads));

    /// <summary>
    /// 整帧的覆盖范围：includeRgb 时"非空"是 RGBA 四字节任一非零，否则只看 alpha；alpha 极值总是整帧统计。
    /// 与 perf/all 上 FrameBounds.Add 的逐像素扫描结果逐位相同。
    /// </summary>
    internal static BoundsResult Bounds(ReadOnlyMemory<byte> rgba, int width, int height, bool includeRgb, int threads)
    {
        int rowBytes = checked(width * 4);
        if (rgba.Length != checked(rowBytes * height))
            throw new ArgumentException($"Frame has {rgba.Length} bytes; expected {width}x{height} RGBA.", nameof(rgba));
        int blocks = BlockCount(rgba.Length, height, threads);
        if (blocks <= 1) return BoundsRows(rgba.Span, width, height, 0, height, includeRgb);
        int rowsPerBlock = (height + blocks - 1) / blocks;
        var partial = new BoundsResult[blocks];
        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = threads }, block =>
        {
            int y0 = Math.Min(height, block * rowsPerBlock), y1 = Math.Min(height, y0 + rowsPerBlock);
            partial[block] = BoundsRows(rgba.Span, width, height, y0, y1, includeRgb);
        });
        BoundsResult result = partial[0];
        for (int block = 1; block < blocks; ++block) result = result.Union(partial[block]);
        return result;
    }

    /// <summary>
    /// 行 [y0, y1) 的覆盖范围。两段式：先用一遍向量 OR 判整行是否为空（同一遍顺带累积 alpha 的 min/max），
    /// 非空行才收缩列，而且只在当前左界左侧、右界右侧找非零像素——多数行要么全空、要么边界早已收敛，几乎不用再看。
    /// </summary>
    private static BoundsResult BoundsRows(ReadOnlySpan<byte> rgba, int width, int height, int y0, int y1, bool includeRgb)
    {
        int rowBytes = width * 4;
        if (!Vector256.IsHardwareAccelerated || rowBytes < Vector256<byte>.Count)
            return BoundsRowsScalar(rgba, width, height, y0, y1, includeRgb);
        int minX = width, maxX = -1, minY = height, maxY = -1;
        Vector256<byte> minV = Vector256<byte>.AllBitsSet, maxV = Vector256<byte>.Zero;
        // 每个像素 4 字节、每次加载的偏移都是 4 的倍数，所以向量里 3、7、…、31 号字节恒为 alpha。
        Vector256<byte> mask = includeRgb ? Vector256<byte>.AllBitsSet : Vector256.Create(0xFF000000u).AsByte();
        uint scalarMask = includeRgb ? uint.MaxValue : 0xFF000000u;
        for (int y = y0; y < y1; ++y)
        {
            ReadOnlySpan<byte> row = rgba.Slice(y * rowBytes, rowBytes);
            ref byte start = ref MemoryMarshal.GetReference(row);
            Vector256<byte> orV = Vector256<byte>.Zero;
            int i = 0;
            for (; i <= rowBytes - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref start, (nuint)i);
                minV = Vector256.Min(minV, v); maxV = Vector256.Max(maxV, v); orV |= v;
            }
            if (i < rowBytes)
            {
                // 行尾不足一个向量：从行末往回取整个向量，与前一块重叠的字节重复参与 min/max/or，不改结果。
                Vector256<byte> v = Vector256.LoadUnsafe(ref start, (nuint)(rowBytes - Vector256<byte>.Count));
                minV = Vector256.Min(minV, v); maxV = Vector256.Max(maxV, v); orV |= v;
            }
            if ((orV & mask) == Vector256<byte>.Zero) continue;
            if (y < minY) minY = y;
            maxY = y;
            if (minX > 0)
            {
                int first = FirstNonZeroPixel(row, 0, minX * 4, mask, scalarMask);
                if (first >= 0) minX = first;
            }
            if (maxX < width - 1)
            {
                int last = LastNonZeroPixel(row, (maxX + 1) * 4, rowBytes, mask, scalarMask);
                if (last >= 0) maxX = last;
            }
        }
        byte minAlpha = byte.MaxValue, maxAlpha = 0;
        for (int lane = 3; lane < Vector256<byte>.Count; lane += 4)
        {
            minAlpha = Math.Min(minAlpha, minV.GetElement(lane));
            maxAlpha = Math.Max(maxAlpha, maxV.GetElement(lane));
        }
        return new(minX, maxX, minY, maxY, minAlpha, maxAlpha);
    }

    /// <summary>perf/all 303cf8a 上 FrameBounds.Add 的逐像素扫描，限定行范围；向量路径的对照与回退。</summary>
    private static BoundsResult BoundsRowsScalar(ReadOnlySpan<byte> rgba, int width, int height, int y0, int y1, bool includeRgb)
    {
        int minX = width, maxX = -1, minY = height, maxY = -1;
        byte minAlpha = byte.MaxValue, maxAlpha = 0;
        int position = y0 * width * 4;
        for (int y = y0; y < y1; ++y)
            for (int x = 0; x < width; ++x, position += 4)
            {
                uint pixel = BinaryPrimitives.ReadUInt32LittleEndian(rgba.Slice(position, 4));
                byte alpha = (byte)(pixel >> 24);
                minAlpha = Math.Min(minAlpha, alpha); maxAlpha = Math.Max(maxAlpha, alpha);
                if (includeRgb ? pixel == 0 : alpha == 0) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return new(minX, maxX, minY, maxY, minAlpha, maxAlpha);
    }

    /// <summary>行内字节区间 [start, end)（都是 4 的倍数）里第一个掩码后非零的像素 x；没有则 -1。</summary>
    private static int FirstNonZeroPixel(ReadOnlySpan<byte> row, int start, int end, Vector256<byte> mask, uint scalarMask)
    {
        int i = start;
        if (end - start >= Vector256<byte>.Count)
        {
            ref byte origin = ref MemoryMarshal.GetReference(row);
            for (; i <= end - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                uint zero = Vector256.Equals(Vector256.LoadUnsafe(ref origin, (nuint)i) & mask, Vector256<byte>.Zero).ExtractMostSignificantBits();
                if (zero != uint.MaxValue) return (i + BitOperations.TrailingZeroCount(~zero)) / 4;
            }
            if (i < end)
            {
                // 尾段不足一个向量：从 end 往回取整个向量，只看 [i, end) 那些位。
                int tail = end - Vector256<byte>.Count;
                uint zero = Vector256.Equals(Vector256.LoadUnsafe(ref origin, (nuint)tail) & mask, Vector256<byte>.Zero).ExtractMostSignificantBits();
                uint nonzero = ~zero & (uint.MaxValue << (i - tail));
                if (nonzero != 0) return (tail + BitOperations.TrailingZeroCount(nonzero)) / 4;
            }
            return -1;
        }
        for (; i < end; i += 4)
            if ((BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(i, 4)) & scalarMask) != 0) return i / 4;
        return -1;
    }

    /// <summary>行内字节区间 [start, end)（都是 4 的倍数）里最后一个掩码后非零的像素 x；没有则 -1。</summary>
    private static int LastNonZeroPixel(ReadOnlySpan<byte> row, int start, int end, Vector256<byte> mask, uint scalarMask)
    {
        int i = end;
        if (end - start >= Vector256<byte>.Count)
        {
            ref byte origin = ref MemoryMarshal.GetReference(row);
            for (; i - Vector256<byte>.Count >= start; i -= Vector256<byte>.Count)
            {
                int at = i - Vector256<byte>.Count;
                uint zero = Vector256.Equals(Vector256.LoadUnsafe(ref origin, (nuint)at) & mask, Vector256<byte>.Zero).ExtractMostSignificantBits();
                if (zero != uint.MaxValue) return (at + 31 - BitOperations.LeadingZeroCount(~zero)) / 4;
            }
            if (i > start)
            {
                // 头段不足一个向量：从 start 起取整个向量，只看 [start, i) 那些位。
                uint zero = Vector256.Equals(Vector256.LoadUnsafe(ref origin, (nuint)start) & mask, Vector256<byte>.Zero).ExtractMostSignificantBits();
                uint nonzero = ~zero & (uint.MaxValue >> (Vector256<byte>.Count - (i - start)));
                if (nonzero != 0) return (start + 31 - BitOperations.LeadingZeroCount(nonzero)) / 4;
            }
            return -1;
        }
        for (i = end - 4; i >= start; i -= 4)
            if ((BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(i, 4)) & scalarMask) != 0) return i / 4;
        return -1;
    }

    /// <summary>
    /// 帧里第一个 alpha 不是 255 的像素的 alpha 字节索引（与旧的逐 4 字节扫描抛出证据时用的 alphaIndex 相同）；全部不透明时 -1。
    /// </summary>
    internal static int FirstNonOpaqueAlphaIndex(ReadOnlyMemory<byte> rgba, int threads)
    {
        int pixels = rgba.Length / 4;
        int blocks = BlockCount(rgba.Length, pixels, threads);
        if (blocks <= 1) return FirstNonOpaqueAlphaIndex(rgba.Span, 0, rgba.Length);
        int pixelsPerBlock = (pixels + blocks - 1) / blocks;
        var partial = new int[blocks];
        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = threads }, block =>
        {
            int start = (int)Math.Min((long)block * pixelsPerBlock * 4, rgba.Length);
            int end = (int)Math.Min((long)start + (long)pixelsPerBlock * 4, rgba.Length);
            partial[block] = start < end ? FirstNonOpaqueAlphaIndex(rgba.Span, start, end) : -1;
        });
        // 块按位置递增，第一个命中的块给出的就是全帧最小索引。
        foreach (int index in partial) if (index >= 0) return index;
        return -1;
    }

    private static int FirstNonOpaqueAlphaIndex(ReadOnlySpan<byte> rgba, int start, int end)
    {
        int i = start;
        if (Vector256.IsHardwareAccelerated && end - start >= Vector256<byte>.Count)
        {
            ref byte origin = ref MemoryMarshal.GetReference(rgba);
            // 把每个像素的 RGB 三字节先 OR 成 255，向量里就只剩 alpha 可能不是 255。
            Vector256<byte> rgbFill = Vector256.Create(0x00FFFFFFu).AsByte();
            for (; i <= end - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                uint opaque = Vector256.Equals(Vector256.LoadUnsafe(ref origin, (nuint)i) | rgbFill, Vector256<byte>.AllBitsSet).ExtractMostSignificantBits();
                if (opaque != uint.MaxValue) return i + BitOperations.TrailingZeroCount(~opaque);
            }
            if (i < end)
            {
                int tail = end - Vector256<byte>.Count;
                uint opaque = Vector256.Equals(Vector256.LoadUnsafe(ref origin, (nuint)tail) | rgbFill, Vector256<byte>.AllBitsSet).ExtractMostSignificantBits();
                uint translucent = ~opaque & (uint.MaxValue << (i - tail));
                if (translucent != 0) return tail + BitOperations.TrailingZeroCount(translucent);
            }
            return -1;
        }
        for (i = start + 3; i < end; i += 4)
            if (rgba[i] != byte.MaxValue) return i;
        return -1;
    }
}
