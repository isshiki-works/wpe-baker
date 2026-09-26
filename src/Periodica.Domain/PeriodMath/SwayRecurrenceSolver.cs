namespace Periodica.Domain;

/// <summary>输出画布短边换算（摆动改频求解器删除后只剩这一项；瓦片边长、接缝门与磁盘预算按它随输出短边缩放）。</summary>
public static class SwayRecurrenceSolver
{
    /// <summary>
    /// 换算基准的输出短边（像素）。同样的运动在 8K 下是 1080p 的 4 倍像素，而看不看得出取决于它占画面的比例，
    /// 所以按输出短边等比放大：倍率 = 短边 / 1080。
    /// 取短边不取对角线：视距惯例按画面高度（ITU-R BT.500/BT.710 以画面高度的倍数定视距），同像素密度的 3440×1440 与 2560×1440
    /// 高度相同、可见性相同，按对角线会把带鱼屏放宽约 27%；竖屏同理取短边。16:9 下两者等价。
    /// </summary>
    public const double ReferenceShortEdgePixels = 1080;

    /// <summary>
    /// 输出画布上的速度门限倍率 = min(宽, 高) / 1080；尺寸未知（非正或非有限）时为 1，按 1080p 口径。
    /// 短边恰为 1080 时倍率精确为 1.0，门限与旧版逐位相同。
    /// </summary>
    public static double SpeedLimitScale(double outputWidth, double outputHeight)
    {
        double shortEdge = Math.Min(outputWidth, outputHeight);
        return double.IsFinite(shortEdge) && shortEdge > 0 ? shortEdge / ReferenceShortEdgePixels : 1;
    }
}
