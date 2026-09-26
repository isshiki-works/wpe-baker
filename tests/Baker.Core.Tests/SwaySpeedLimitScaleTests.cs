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
    }

}
