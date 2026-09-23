// C2.1a 测试消融查出的 5 处 L0 缺口（m08、m10–m13）：此前只有差分网格与语料兜底，这里各补一张表。
using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L0")]
public class CriteriaGapTests
{
    // m08：TileMae 带掩码时只累计掩码非 0 的像素，分母仍是整块瓦片面积。
    // 4×2 画面、2 像素瓦片：像素 (0,0) 差 12（三通道），像素 (2,0) 只 G 通道差 6（均值 2）。
    [Theory]
    [InlineData(new byte[0], new[] { 3.0, 0.5 }, 12)]
    [InlineData(new byte[] { 0, 1, 1, 1, 1, 1, 1, 1 }, new[] { 0.0, 0.5 }, 6)]
    [InlineData(new byte[] { 1, 1, 0, 1, 1, 1, 1, 1 }, new[] { 3.0, 0.0 }, 12)]
    [InlineData(new byte[] { 255, 1, 1, 1, 1, 1, 1, 1 }, new[] { 3.0, 0.5 }, 12)]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, new[] { 0.0, 0.0 }, 0)]
    public void TileMaeHonoursMask(byte[] mask, double[] expected, int expectedMaximum)
    {
        byte[] a = new byte[4 * 2 * 3], b = new byte[4 * 2 * 3];
        b[0] = b[1] = b[2] = 12;
        b[2 * 3 + 1] = 6;
        Assert.Equal(expected, LoopSeamMetrics.TileMae(a, b, 4, 2, 2, mask, out int maximum));
        Assert.Equal(expectedMaximum, maximum);
        Assert.Equal(expected, LoopSeamMetrics.TileMae(a, b, 4, 2, 2, mask));
    }

    // m10：槽位不够时发射计时器夹到一个发射间隔。去掉夹紧后计时器在封顶期间单调增长、逐位不再回到同一值，
    // 周期态永远找不到。每行都是发射远快于补位的封顶系统（槽位常满）。
    [Theory]
    [InlineData(5u, 100f, 0.5f, 30u, 1u)]
    [InlineData(20u, 50f, 1.0f, 60u, 1u)]
    [InlineData(3u, 40f, 0.25f, 24u, 1u)]
    [InlineData(8u, 90f, 0.4f, 30000u, 1001u)]
    public void CappedEmitterTimerClamps(uint maxCount, float speed, float lifetime, uint fpsNumerator, uint fpsDenominator)
    {
        var outcome = ParticleCappedReplacement.Derive(
            new(maxCount, [new(speed, false)], lifetime, 1f, 0f, fpsNumerator, fpsDenominator), 100_000);
        Assert.Null(outcome.FailureCode);
        Assert.NotNull(outcome.PeriodFrames);
        Assert.Equal(outcome.PeriodFrames, outcome.ReplacementFrames);
        Assert.NotNull(outcome.CycleStartFrame);
    }

    // m11：封顶下随机寿命把替换时刻打散所需的代数 = ⌈max / (max − min)⌉ + 1。
    [Theory]
    [InlineData(1.0, 0.0, 2)]
    [InlineData(2.0, 1.0, 3)]
    [InlineData(1.0, 0.5, 3)]
    [InlineData(3.0, 2.0, 4)]
    [InlineData(10.0, 7.0, 5)]
    public void CappedWarmupGenerations(double lifetimeMax, double lifetimeMin, int expected) =>
        Assert.Equal(expected, ParticleCriteria.WarmupGenerations(lifetimeMax, lifetimeMin));

    // m12：锁定预热秒数按微秒向上取整（预热少一点就进不了周期态）。
    [Theory]
    [InlineData(1ul, 3u, 1u, 0.333334)]
    [InlineData(2ul, 3u, 1u, 0.666667)]
    [InlineData(60ul, 60u, 1u, 1.0)]
    [InlineData(1ul, 30000u, 1001u, 0.033367)]
    [InlineData(0ul, 60u, 1u, 0.0)]
    public void CycleWarmupRoundsUp(ulong cycleStartFrame, uint fpsNumerator, uint fpsDenominator, double expected) =>
        Assert.Equal(expected, ParticleCriteria.CycleWarmupSeconds(cycleStartFrame, fpsNumerator, fpsDenominator));

    // m13：封顶替换取证写 period_frames（数出寿命帧数 N 之后就写，成功与"周期态没回归"都有）；周期超上限时只写上限不写 N。
    [Theory]
    [InlineData(5, 100.0, 0.5, 30u, 600.0, true)]
    [InlineData(20, 50.0, 1.0, 60u, 600.0, true)]
    [InlineData(5, 100.0, 30.0, 30u, 10.0, false)]
    public void CappedEvidenceWritesPeriodFrames(int maxCount, double rate, double lifetime, uint fps, double ceilingSeconds, bool derived)
    {
        MethodInfo derive = typeof(ParticleStationarity).GetMethod("DeriveCappedReplacement", BindingFlags.Static | BindingFlags.NonPublic)!;
        Type clockType = typeof(ParticleStationarity).GetNestedType("FrameClock", BindingFlags.NonPublic)!;
        object clock = Activator.CreateInstance(clockType, fps, 1u, ceilingSeconds)!;
        var definition = new JsonObject { ["maxcount"] = maxCount };
        JsonObject[] emitters = [new() { ["name"] = "sphererandom", ["rate"] = rate }];
        JsonObject[] initializers = [new() { ["name"] = "lifetimerandom", ["min"] = lifetime, ["max"] = lifetime }];
        var result = (System.Runtime.CompilerServices.ITuple)derive.Invoke(null, [definition, new JsonObject(), emitters, initializers, clock])!;
        var evidence = (JsonObject)result[3]!;
        if (derived)
        {
            Assert.NotNull(result[0]);
            Assert.Equal((ulong)result[0]!, evidence["period_frames"]!.GetValue<ulong>());
            Assert.False(evidence.ContainsKey("maximum_period_frames"));
        }
        else
        {
            Assert.Equal("lifetime_capped_period_exceeds_ceiling", result[2]);
            Assert.False(evidence.ContainsKey("period_frames"));
            Assert.Equal(300ul, evidence["maximum_period_frames"]!.GetValue<ulong>());
        }
    }
}
