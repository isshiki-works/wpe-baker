namespace Periodica.Domain;

/// <summary>
/// 主渲染录制前跳过的帧数。两种预热都是"录制前多渲帧"，互不替代，取和：
/// 精灵 float32 整周期预热（source_period_warmup_frames，只能是 0 或 P，见 <see cref="SpriteSeamPhase"/>），
/// 平稳随机粒子预热 W（见 Baker.Core 的 ResidualMasking.WarmupFrames）。
/// 残差路线再加起点相位 S。整周期预热不改精确周期分量的相位，所以起点搜索与 master 共用"精灵 + 粒子"这个基准。
/// </summary>
public static class LoopWarmup
{
    /// <summary>起点相位之前的预热基准 = 精灵整周期预热 + 粒子预热 W。精灵预热不是 0 或 P 时拒绝。</summary>
    public static ulong BaseFrames(ulong sourcePeriodWarmupFrames, ulong loopFrames, ulong particleWarmupFrames)
    {
        if (sourcePeriodWarmupFrames != 0 && sourcePeriodWarmupFrames != loopFrames)
            throw new InvalidDataException("Source-period warmup must be exactly one analytic period.");
        return checked(sourcePeriodWarmupFrames + particleWarmupFrames);
    }

    /// <summary>master 渲染器实际跳过的帧数 = 精灵整周期预热 + 粒子预热 W + 残差路线起点相位 S。</summary>
    public static ulong MasterFrames(ulong sourcePeriodWarmupFrames, ulong loopFrames, ulong particleWarmupFrames,
        ulong loopStartPhaseFrame) =>
        checked(BaseFrames(sourcePeriodWarmupFrames, loopFrames, particleWarmupFrames) + loopStartPhaseFrame);
}
