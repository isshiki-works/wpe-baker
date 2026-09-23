using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

/// <summary>
/// fix-j：摆动速度偏差门限按输出短边换算（门限 = 1080p 常量 × min(宽, 高) / 1080）。
/// 1080p 下倍率精确为 1、门限与旧版逐位相同；2160p ×2、4320p ×4；竖屏与横屏同门限；带鱼屏按短边（不按对角线）。
/// 求解器与 plan 记录的整链检查在 SwayRetimeChecks（CanvasScaleChecks 与 8K 集成检查）。
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
    public void ProfileRecordCarriesTheEffectiveLimits()
    {
        RetimeProfile profile = RetimeProfile.Resolve(RetimeProfile.Balanced, null, null, 2);
        JsonObject Record(uint width, uint height) => profile.ToJson(SwayRecurrenceSolver.SpeedLimitScale(width, height));
        static double Limit(JsonObject record, string kind) => record[kind + "_speed_deviation_limit_pixels_per_second"]!.GetValue<double>();
        // 1080p：与旧版常量逐位相同，序列化文本也不变。
        JsonObject fullHd = Record(1920, 1080);
        Assert.Equal(0.1, Limit(fullHd, "slow"));
        Assert.Equal(0.2, Limit(fullHd, "visible"));
        Assert.Contains("\"slow_speed_deviation_limit_pixels_per_second\":0.1,\"visible_speed_deviation_limit_pixels_per_second\":0.2}",
            fullHd.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(0.2, Limit(Record(3840, 2160), "slow"));
        Assert.Equal(0.4, Limit(Record(3840, 2160), "visible"));
        Assert.Equal(0.4, Limit(Record(7680, 4320), "slow"));
        Assert.Equal(0.8, Limit(Record(7680, 4320), "visible"));
    }
}
