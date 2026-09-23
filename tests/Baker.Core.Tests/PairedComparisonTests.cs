using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

/// <summary>
/// 合成比较（P2）：向量化整数内核与原来逐像素 double 累加的写出逐字节相同；两条渲染器 stdout 的锁步汇合；合成门阈值。
/// </summary>
[Trait("Layer", "L0")]
public class PairedComparisonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    // 另一边没被取消时锁步会一直等：给个上限，挂住就算失败而不是卡死测试。
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>原 CandidateValidation 的逐像素 double 累加（C2.3c 之前的实现），作为逐字节对照。</summary>
    private sealed class OldErrors
    {
        public ulong Pixels, DifferentRgb, DifferentAlpha;
        public double RgbSum, RgbSquared, AlphaSum;
        public int MaxRgb, MaxAlpha;
        public void Add(int r, int g, int b, int alpha)
        {
            ++Pixels;
            RgbSum += r + g + b;
            RgbSquared += r * r + g * g + b * b;
            AlphaSum += alpha;
            int maximum = Math.Max(r, Math.Max(g, b));
            MaxRgb = Math.Max(MaxRgb, maximum); MaxAlpha = Math.Max(MaxAlpha, alpha);
            if (maximum != 0) ++DifferentRgb;
            if (alpha != 0) ++DifferentAlpha;
        }
        public void Merge(OldErrors other)
        {
            Pixels += other.Pixels; DifferentRgb += other.DifferentRgb; DifferentAlpha += other.DifferentAlpha;
            RgbSum += other.RgbSum; RgbSquared += other.RgbSquared; AlphaSum += other.AlphaSum;
            MaxRgb = Math.Max(MaxRgb, other.MaxRgb); MaxAlpha = Math.Max(MaxAlpha, other.MaxAlpha);
        }
        public JsonObject ToJson() => new()
        {
            ["pixels"] = Pixels, ["rgb_mae_255"] = RgbSum / (Pixels * 3.0),
            ["rgb_rmse_255"] = Math.Sqrt(RgbSquared / (Pixels * 3.0)), ["rgb_max_abs_255"] = (double)MaxRgb,
            ["alpha_mae_255"] = AlphaSum / Pixels, ["alpha_max_abs_255"] = (double)MaxAlpha,
            ["differing_rgb_pixel_fraction"] = (double)DifferentRgb / Pixels,
            ["differing_alpha_pixel_fraction"] = (double)DifferentAlpha / Pixels
        };
    }

    /// <summary>旧实现的整段比较：逐帧行、瓦片、合计与最差瓦片，拼成与报告同形的 JSON 文本。</summary>
    private static (string[] Frames, string Total, string Tiles, string Worst) Old(byte[][] a, byte[][] b, int width, int height, int edge)
    {
        int columns = (width + edge - 1) / edge, rows = (height + edge - 1) / edge;
        var tiles = Enumerable.Range(0, columns * rows).Select(_ => new OldErrors()).ToArray();
        var aggregate = new OldErrors();
        var frames = new List<string>();
        for (int frame = 0; frame < a.Length; ++frame)
        {
            var frameErrors = new OldErrors();
            for (int y = 0; y < height; ++y)
            {
                int tileRow = y / edge * columns;
                for (int x = 0; x < width; ++x)
                {
                    int offset = (y * width + x) * 4;
                    int dr = Math.Abs(a[frame][offset] - b[frame][offset]), dg = Math.Abs(a[frame][offset + 1] - b[frame][offset + 1]);
                    int db = Math.Abs(a[frame][offset + 2] - b[frame][offset + 2]), da = Math.Abs(a[frame][offset + 3] - b[frame][offset + 3]);
                    frameErrors.Add(dr, dg, db, da);
                    tiles[tileRow + x / edge].Add(dr, dg, db, da);
                }
            }
            aggregate.Merge(frameErrors);
            frames.Add(frameErrors.ToJson().ToJsonString());
        }
        var tileJson = new JsonArray();
        for (int i = 0; i < tiles.Length; ++i)
        {
            int x = i % columns * edge, y = i / columns * edge;
            tileJson.Add(new JsonObject { ["x"] = x, ["y"] = y, ["width"] = Math.Min(edge, width - x),
                ["height"] = Math.Min(edge, height - y), ["metrics"] = tiles[i].ToJson() });
        }
        int worst = Enumerable.Range(0, tiles.Length).MaxBy(i => tiles[i].RgbSum / tiles[i].Pixels);
        return ([.. frames], aggregate.ToJson().ToJsonString(), tileJson.ToJsonString(), tileJson[worst]!.ToJsonString());
    }

    private static byte[][] Frames(Random random, int count, int bytes, byte[][]? near = null)
    {
        var frames = new byte[count][];
        for (int i = 0; i < count; ++i)
        {
            frames[i] = new byte[bytes];
            random.NextBytes(frames[i]);
            if (near is null) continue;
            // 大部分像素只差一点或完全相同，少数整块乱：接近真实合成比较里的分布，也覆盖 0 与 255 两端。
            for (int j = 0; j < bytes; ++j)
                frames[i][j] = (j / 4 % 7) switch
                {
                    0 or 1 or 2 => near[i][j],
                    3 => (byte)Math.Clamp(near[i][j] + random.Next(-3, 4), 0, 255),
                    4 => (byte)(255 - near[i][j]),
                    _ => frames[i][j]
                };
        }
        return frames;
    }

    [Theory]
    [InlineData(1, 1, 64)]
    [InlineData(8, 3, 8)]
    [InlineData(13, 7, 4)]
    [InlineData(130, 67, 64)]
    [InlineData(257, 33, 16)]
    [InlineData(1030, 9, 1024)]
    public void IntegerKernelMatchesTheOldScalarReportByteForByte(int width, int height, int edge)
    {
        var random = new Random(width * 7919 + height * 31 + edge);
        byte[][] a = Frames(random, 3, width * height * 4);
        byte[][] b = Frames(random, 3, width * height * 4, a);
        var expected = Old(a, b, width, height, edge);
        foreach (bool vectorized in new[] { true, false })
        {
            var comparer = new PairedFrameComparer(width, height, edge);
            string[] frames = [.. Enumerable.Range(0, a.Length).Select(i => comparer.Add(a[i], b[i], Ct, vectorized).ToJson().ToJsonString())];
            Assert.Equal(expected.Frames, frames);
            Assert.Equal(expected.Total, comparer.Total.ToJson().ToJsonString());
            Assert.Equal(expected.Tiles, new JsonArray([.. comparer.Tiles.Select(tile => (JsonNode)tile.ToJson())]).ToJsonString());
            Assert.Equal(expected.Worst, comparer.Tiles[comparer.WorstRgbTile].ToJson().ToJsonString());
        }
    }

    [Fact]
    public void FramesOfTheWrongSizeAreRejectedBeforeReading()
    {
        var comparer = new PairedFrameComparer(16, 16, 64);
        Assert.Throws<ArgumentException>(() => comparer.Add(new byte[16 * 16 * 4], new byte[16 * 16 * 4 - 4], Ct));
    }

    /// <summary>假渲染器：按帧号填满同一块缓冲区交给接收器，交出后立刻涂掉，比较时读到的必须是交出那一刻的内容。</summary>
    private static Func<IFrameSink, CancellationToken, Task<JsonObject>> FakeRenderer(byte side, int frames, int failAt = -1, int delayMs = 0) =>
        async (sink, token) =>
        {
            byte[] buffer = new byte[16];
            for (int i = 0; i < frames; ++i)
            {
                if (i == failAt) throw new IOException($"renderer {side} failed at {i}");
                if (delayMs > 0) await Task.Delay(delayMs, token);
                Array.Fill(buffer, (byte)(side * 100 + i));
                await sink.WriteFrameAsync((ulong)i, buffer, token);
                Array.Fill(buffer, (byte)0xEE);
            }
            return new JsonObject { ["side"] = side };
        };

    [Fact]
    public async Task LockstepPairsEqualFrameNumbersWhileBothBuffersAreStillValid()
    {
        var seen = new List<(ulong Frame, byte Source, byte Candidate)>();
        (JsonObject source, JsonObject candidate) = await CandidateValidation.RenderLockstepAsync(
            FakeRenderer(1, 5, delayMs: 3), FakeRenderer(2, 5),
            (frame, a, b, token) => { seen.Add((frame, a.Span[0], b.Span[0])); return ValueTask.CompletedTask; }, Ct).WaitAsync(Timeout, Ct);
        Assert.Equal(1, source["side"]!.GetValue<byte>());
        Assert.Equal(2, candidate["side"]!.GetValue<byte>());
        Assert.Equal(Enumerable.Range(0, 5).Select(i => ((ulong)i, (byte)(100 + i), (byte)(200 + i))), seen);
    }

    [Fact]
    public async Task LockstepCancelsTheOtherRendererAndThrowsTheOriginalError()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => CandidateValidation.RenderLockstepAsync(
            FakeRenderer(1, 50, delayMs: 2), FakeRenderer(2, 50, failAt: 3),
            (_, _, _, _) => ValueTask.CompletedTask, Ct).WaitAsync(Timeout, Ct));
        Assert.Equal("renderer 2 failed at 3", error.Message);
    }

    /// <summary>构造一份类型化比较结果：一个瓦片（误差给定），报告带上合成门要读的字段。</summary>
    internal static PairedComparison Comparison(PixelErrors? total = null, PixelErrors[]? tiles = null, JsonObject? extra = null)
    {
        total ??= new PixelErrors { Pixels = 1 };
        tiles ??= [total];
        var typed = tiles.Select((errors, i) => new PairedTile(i * 64, 0, 64, 64, errors)).ToArray();
        var report = new JsonObject
        {
            ["schema_version"] = 1, ["status"] = "compared", ["report_path"] = "comparison.json", ["frames_compared"] = 48UL,
            ["metrics"] = total.ToJson(), ["tiles"] = new JsonArray([.. typed.Select(tile => (JsonNode)tile.ToJson())])
        };
        foreach (var (key, value) in extra ?? []) report[key] = value?.DeepClone();
        return new PairedComparison(report, total, typed);
    }

    private static string Decision(PairedComparison comparison) => CompositionGate.Evaluate(comparison)["status"]!.GetValue<string>();

    [Fact]
    public void CompositionGateLimitsAreInclusive()
    {
        // 全局 RGB 8、全局 alpha 1、每瓦片 RGB 25 恰好在限值上放行，多一点就拒绝。
        PixelErrors Errors(ulong pixels, ulong rgbSum, ulong alphaSum = 0) => new() { Pixels = pixels, RgbSum = rgbSum, AlphaSum = alphaSum };
        Assert.Equal("composition_pass", Decision(Comparison(Errors(3, 72, 3), [Errors(3, 225), Errors(3, 3)])));
        Assert.Equal("composition_rejected", Decision(Comparison(Errors(3, 73, 3), [Errors(3, 3)])));
        Assert.Equal("composition_rejected", Decision(Comparison(Errors(3, 3, 4), [Errors(3, 3)])));
        JsonObject tile = CompositionGate.Evaluate(Comparison(Errors(3, 3), [Errors(3, 3), Errors(3, 226)]));
        Assert.Equal("composition_rejected", tile["status"]!.GetValue<string>());
        Assert.Equal(64, tile["worst_rgb_tile"]!["x"]!.GetValue<int>());
        Assert.Contains("Tile (64,0) RGB MAE 25.1111 exceeds 25.", tile["failures"]!.AsArray().Select(node => node!.GetValue<string>()));
    }
}
