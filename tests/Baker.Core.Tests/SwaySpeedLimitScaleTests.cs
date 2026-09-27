using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

/// <summary>
/// fix-j：摆动速度偏差门限按输出短边换算（门限 = 1080p 常量 × min(宽, 高) / 1080）。
/// 1080p 下倍率精确为 1、门限与旧版逐位相同；2160p ×2、4320p ×4；竖屏与横屏同门限；带鱼屏按短边（不按对角线）。
/// </summary>
[Trait("Layer", "L0")]
public class SwaySpeedLimitScaleTests
{
    [Fact]
    public void ScaleFollowsTheShortEdge()
    {
        Assert.Equal(1.0, SwayRecurrenceSolver.SpeedLimitScale(1920, 1080));
        Assert.Equal(2.0, SwayRecurrenceSolver.SpeedLimitScale(3840, 2160));
        Assert.Equal(4.0, SwayRecurrenceSolver.SpeedLimitScale(7680, 4320));
        // 竖屏取短边，与同尺寸横屏同门限。
        Assert.Equal(1.0, SwayRecurrenceSolver.SpeedLimitScale(1080, 1920));
        Assert.Equal(4.0, SwayRecurrenceSolver.SpeedLimitScale(4320, 7680));
        // 带鱼屏 3440×1440 与 2560×1440 高度相同、门限相同；按对角线会放宽约 27%。
        Assert.Equal(1440 / 1080.0, SwayRecurrenceSolver.SpeedLimitScale(3440, 1440));
        Assert.Equal(SwayRecurrenceSolver.SpeedLimitScale(2560, 1440), SwayRecurrenceSolver.SpeedLimitScale(3440, 1440));
    }

    [Fact]
    public void SlowComponentCapOverridesTheRequestLimitAndFreezesOnlyBeyondTheCeiling()
    {
        // 150.8 s 的摆动慢项在 390 s 上要改 +16% 才闭合：逐项 3% 不行，分量自己的 50% 上限可以；
        // 周期不超过循环上限时上限再大也至少一圈，超过上限才冻结（0 圈，与 1.0.2 一致）
        static CommonLoopComponent Slow(double seconds, double? cap) => new("slow", new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic), true, cap);
        var request = new CommonLoopSolveRequest(30, 1, [Slow(150.8, null)], MaximumRetimePercent: 3);
        Assert.Null(CommonLoopSolver.EvaluateAtFrames(request, 390 * 30).Candidate);
        Assert.Equal(3ul, CommonLoopSolver.EvaluateAtFrames(request with { Components = [Slow(150.8, 50)] }, 390 * 30).Candidate!.Components[0].Cycles);
        Assert.Equal(1ul, CommonLoopSolver.EvaluateAtFrames(request with { Components = [Slow(1000, 500)] }, 390 * 30).Candidate!.Components[0].Cycles);
        Assert.Equal(0ul, CommonLoopSolver.EvaluateAtFrames(request with { Components = [Slow(1000, 500)], MaximumDuration = new CommonLoopRational(600) }, 390 * 30).Candidate!.Components[0].Cycles);
        // 只有分量自身上限 ≥ 100% 才可冻结：理想圈数小到被取整容差吞掉（< 1e-12）时，3% 的上限也不许冻结成 −100%
        Assert.Null(CommonLoopSolver.EvaluateAtFrames(request with { Components = [Slow(1e15, null)], MaximumDuration = new CommonLoopRational(600) }, 390 * 30).Candidate);
    }

    // 官方 waterwaves 的峰值速度系数 K = strength²·Σ eᵢ|speedᵢ|·max(宽·scaleX, 高·scaleY)·1080/画布短边：
    // 单波 0.1²·0.05·1920 = 0.96 px/s；父层缩放 0.5 减半；双波按两项相加；指数 < 1、双波缺第二组常量时不放行
    [Fact]
    public void WaterWavePeakSpeedBoundsTheRetimedDisplacement()
    {
        static JsonObject Layer(string constants, string combos = "{}", int? parent = null) => JsonNode.Parse($$"""
            {"id":10,"size":"1920 1080","scale":"1 1 1"{{(parent is int p ? $",\"parent\":{p}" : "")}},
             "effects":[{"passes":[{"constantshadervalues":{{constants}},"combos":{{combos}}}]}]}
            """)!.AsObject();
        double? K(JsonObject layer, bool dual = false) => ShaderPeriodAnalysis.WaveSpeed(layer, new Dictionary<int, JsonObject> {
            [10] = layer, [1] = JsonNode.Parse("""{"id":1,"scale":"0.5 0.5 1"}""")!.AsObject() }, 1080, 0, 0, dual);
        Assert.Equal(0.96, K(Layer("""{"strength":0.1,"speed":0.05}"""))!.Value, 9);
        Assert.Equal(0.48, K(Layer("""{"strength":0.1,"speed":0.05}""", parent: 1))!.Value, 9);
        Assert.Equal(0.01 * (0.05 + 2 * 0.02) * 1920, K(Layer("""{"strength":0.1,"speed":0.05,"speed2":0.02,"exponent2":2}""", """{"DUALWAVES":1}"""))!.Value, 9);
        Assert.Null(K(Layer("""{"strength":0.1,"speed":0.05,"exponent":0.8}""")));
        Assert.Null(K(Layer("""{"strength":0.1,"speed":0.05}"""), dual: true));
    }
}
