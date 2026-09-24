using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 源周期闭合检验：渲染器从起点连续多渲一帧（第 P 帧），在编码前的无损原帧上量 M_64(f[P] − f[0])。
/// 渲染器是确定性的，周期正确时第 P 帧与第 0 帧逐像素相同；留给浮点时间累积与 8 位量化的余量是逐像素 1/255，
/// 所以瓦片上限是 1.0（0..255 刻度），这是 8 位取整给的数，不是调出来的。
/// <para>
/// 它一次判定三件事：周期解析错（Z 大，拒）；精灵帧取整差一帧（Z 大，拒）；源视频自身首尾不接但渲染器在 P 处回绕到
/// 源片第 0 帧（Z = 0，通过——那道切口是原作自己的，由编码后接缝校验以 f[P] − f[P−1] 为参照如实记录）。
/// </para>
/// </summary>
public static class LoopClosureCheck
{
    /// <summary>闭合判据：64px 瓦片 MAE 上限，8 位取整。</summary>
    public const double MaximumTileMae255 = 1.0;

    /// <summary>判定用的瓦片边长（1080p 短边下；<see cref="Evaluate"/> 按输出短边等比缩放，JSON 键名 tile_64 不变）。</summary>
    public const int TileSize = 64;

    /// <summary>只记录、不判定的细瓦片边长（同上缩放，键名 tile_32）。</summary>
    public const int RecordTileSize = 32;

    public const string ClosedStatus = "closed";
    public const string NotClosedStatus = "not_closed";
    public const string NotJudgedStatus = "recorded_not_judged";

    /// <summary>源周期路线主渲染要留下的原帧：第 0 帧、第 P−1 帧、第 P 帧（P=1 时去重）。</summary>
    public static ulong[] ReferenceFrameIndices(ulong loopFrames)
    {
        if (loopFrames == 0) throw new ArgumentException("A loop needs at least one frame.");
        return [.. new[] { 0UL, loopFrames - 1, loopFrames }.Distinct().Order()];
    }

    /// <summary>从渲染清单的 retained_frames 读第 <paramref name="index"/> 帧的原始 RGBA。</summary>
    public static async Task<byte[]> ReadRetainedFrameAsync(JsonObject manifest, ulong index, CancellationToken cancellationToken = default)
    {
        JsonObject retained = manifest["retained_frames"] as JsonObject
            ?? throw new InvalidDataException("渲染清单没有 retained_frames：主渲染没有留下闭合检查要用的原帧。");
        string path = retained["path"]!.GetValue<string>();
        int width = retained["width"]!.GetValue<int>(), height = retained["height"]!.GetValue<int>();
        ulong[] indices = retained["frame_indices"]!.AsArray().Select(node => node!.GetValue<ulong>()).ToArray();
        int position = Array.IndexOf(indices, index);
        if (position < 0) throw new InvalidDataException($"渲染时没有留下第 {index} 帧的原帧。");
        int frameBytes = checked(width * height * 4);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        if (stream.Length != (long)frameBytes * indices.Length) throw new InvalidDataException("留下的原帧文件长度与清单不一致。");
        stream.Seek((long)frameBytes * position, SeekOrigin.Begin);
        byte[] frame = new byte[frameBytes];
        await stream.ReadExactlyAsync(frame, cancellationToken);
        return frame;
    }

    /// <summary>
    /// 闭合检验。<paramref name="first"/> 与 <paramref name="wrap"/> 是渲染器原始 RGBA 的第 0 帧与第 P 帧。
    /// <paramref name="withAlpha"/> 为真（透明组）时 RGB 只在两帧任一 alpha &gt; 0 的像素上算（alpha = 0 处 RGB 没有定义），
    /// alpha 单独再判一次。<paramref name="judged"/> 为假（残差掩盖路线，硬切残差由第一层判）时只记录。
    /// <paramref name="tileScale"/> 是输出画布短边 / 1080（<see cref="SwayRecurrenceSolver.SpeedLimitScale"/>），瓦片边长按它缩放。
    /// </summary>
    public static JsonObject Evaluate(ReadOnlySpan<byte> first, ReadOnlySpan<byte> wrap, int width, int height, bool withAlpha,
        ulong loopFrames, bool judged, double tileScale = 1)
    {
        if (width <= 0 || height <= 0 || first.Length != checked(width * height * 4) || wrap.Length != first.Length)
            throw new ArgumentException("闭合检验需要两帧同尺寸的 RGBA。");
        byte[] firstRgb = Rgb(first), wrapRgb = Rgb(wrap);
        byte[] mask = withAlpha ? AlphaMask(first, wrap, 4, 3) : [];
        int tile = (int)Math.Round(TileSize * tileScale), recordTile = (int)Math.Round(RecordTileSize * tileScale);
        JsonObject rgb = Plane(firstRgb, wrapRgb, width, height, mask, tile, recordTile, out double rgbWorst);
        JsonObject? alpha = null;
        double alphaWorst = 0;
        if (withAlpha) alpha = Plane(Alpha(first), Alpha(wrap), width, height, [], tile, recordTile, out alphaWorst);
        bool passed = rgbWorst <= MaximumTileMae255 && alphaWorst <= MaximumTileMae255;
        return new JsonObject
        {
            ["status"] = !judged ? NotJudgedStatus : passed ? ClosedStatus : NotClosedStatus,
            ["judged"] = judged,
            ["within_limit"] = passed,
            ["loop_frames"] = loopFrames,
            ["compared_frames"] = new JsonArray(0UL, loopFrames),
            ["frame_source"] = "renderer_rgba_before_encoding",
            ["width"] = width,
            ["height"] = height,
            ["pixel_identical"] = first.SequenceEqual(wrap),
            ["tile_size"] = tile,
            ["limit_tile_mae_255"] = MaximumTileMae255,
            ["rgb"] = rgb,
            ["alpha"] = alpha,
            ["rgb_pixel_scope"] = withAlpha
                ? "RGB counted only where alpha > 0 in frame 0 or frame P; alpha = 0 leaves RGB undefined."
                : "Every RGB pixel.",
            ["basis"] = judged
                ? "The renderer played on continuously to frame P. A true period leaves frame P equal to frame 0; the only allowance is 8-bit rounding (1/255 per pixel, so 1.0 per 64px tile)."
                : "Residual-masking route: the hard-cut residual f[P] - f[0] is judged by the residual layer, not here; recorded only."
        };
    }

    /// <summary>闭合记录是否放行：只有判定过且超限才算不放行。</summary>
    public static bool Allows(JsonObject? closure) => closure?["status"]?.GetValue<string>() != NotClosedStatus;

    /// <summary>
    /// 把渲染器 RGBA 帧（或其上一块矩形）排成成品解码帧的测量布局：不透明为 RGB24；透明组为左 RGB 右 alpha
    /// （alpha 复制到三个通道，与编码时 alphaextract 再转 RGB24 一致）。
    /// </summary>
    public static byte[] EncodedLayout(ReadOnlySpan<byte> rgba, int sourceWidth, int sourceHeight, int x, int y, int width, int height,
        bool packedAlpha)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > sourceWidth || y + height > sourceHeight ||
            rgba.Length != checked(sourceWidth * sourceHeight * 4))
            throw new ArgumentException("测量布局的矩形不在原帧之内。");
        int halves = packedAlpha ? 2 : 1;
        byte[] result = new byte[checked(width * halves * height * 3)];
        for (int row = 0; row < height; ++row)
            for (int column = 0; column < width; ++column)
            {
                int source = ((y + row) * sourceWidth + x + column) * 4, target = (row * width * halves + column) * 3;
                result[target] = rgba[source]; result[target + 1] = rgba[source + 1]; result[target + 2] = rgba[source + 2];
                if (!packedAlpha) continue;
                int alpha = target + width * 3;
                result[alpha] = result[alpha + 1] = result[alpha + 2] = rgba[source + 3];
            }
        return result;
    }

    /// <summary>
    /// 逐像素掩码：任一给定帧在该像素的 alpha 通道 &gt; 0 记 1。<paramref name="stride"/> 是每像素字节数，
    /// <paramref name="alphaOffset"/> 是 alpha 在像素内的偏移（RGBA 为 3；RGB24 的 alpha 半幅取 R 通道为 0）。
    /// </summary>
    public static byte[] AlphaMask(IReadOnlyList<byte[]> frames, int stride, int alphaOffset)
    {
        int pixels = frames[0].Length / stride;
        byte[] mask = new byte[pixels];
        foreach (byte[] frame in frames)
        {
            if (frame.Length != pixels * stride) throw new ArgumentException("掩码的帧尺寸不一致。");
            for (int pixel = 0; pixel < pixels; ++pixel) if (frame[pixel * stride + alphaOffset] != 0) mask[pixel] = 1;
        }
        return mask;
    }

    private static byte[] AlphaMask(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, int stride, int alphaOffset)
    {
        byte[] mask = new byte[first.Length / stride];
        for (int pixel = 0; pixel < mask.Length; ++pixel)
            if (first[pixel * stride + alphaOffset] != 0 || second[pixel * stride + alphaOffset] != 0) mask[pixel] = 1;
        return mask;
    }

    private static JsonObject Plane(byte[] first, byte[] wrap, int width, int height, byte[] mask, int tile, int recordTile,
        out double worst)
    {
        double[] coarse = LoopSeamMetrics.TileMae(first, wrap, width, height, tile, mask, out int maximum);
        double[] fine = LoopSeamMetrics.TileMae(first, wrap, width, height, recordTile, mask);
        TileMapSummary summary = LoopSeamMetrics.Summarize(coarse, width, height, tile);
        worst = summary.Worst;
        return new JsonObject
        {
            ["tile_64"] = Json(summary),
            ["tile_32"] = Json(LoopSeamMetrics.Summarize(fine, width, height, recordTile)),
            ["tiles_above_limit"] = coarse.Count(value => value > MaximumTileMae255),
            ["maximum_channel_difference"] = maximum
        };
    }

    internal static JsonObject Json(TileMapSummary summary) => new()
    {
        ["global"] = Math.Round(summary.Global, 4), ["worst"] = Math.Round(summary.Worst, 4),
        ["worst_x"] = summary.WorstX, ["worst_y"] = summary.WorstY, ["median"] = Math.Round(summary.Median, 4)
    };

    private static byte[] Rgb(ReadOnlySpan<byte> rgba)
    {
        byte[] rgb = new byte[rgba.Length / 4 * 3];
        for (int pixel = 0, count = rgba.Length / 4; pixel < count; ++pixel)
        {
            rgb[pixel * 3] = rgba[pixel * 4]; rgb[pixel * 3 + 1] = rgba[pixel * 4 + 1]; rgb[pixel * 3 + 2] = rgba[pixel * 4 + 2];
        }
        return rgb;
    }

    private static byte[] Alpha(ReadOnlySpan<byte> rgba)
    {
        byte[] alpha = new byte[rgba.Length / 4 * 3];
        for (int pixel = 0, count = rgba.Length / 4; pixel < count; ++pixel)
            alpha[pixel * 3] = alpha[pixel * 3 + 1] = alpha[pixel * 3 + 2] = rgba[pixel * 4 + 3];
        return alpha;
    }
}
