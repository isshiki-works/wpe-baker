namespace Periodica.Domain;

/// <summary>
/// 粒子"平稳随机、可淡化替换"判据（design-particle-crossfade.md §2.2 的 C1–C9）里与 JSON 无关的部分：
/// 核过的节点种类白名单、渲染器缺省值，以及封顶代数、预热时长、周期上限这些数值判据。
/// 逐节点读粒子定义、写 failed_conditions 的遍历在 Baker.Core 的 ParticleStationarity。
/// </summary>
internal static class ParticleCriteria
{
    // Native Emitter::rate and Particle::FromJson supply these defaults, including capped emission.
    internal const float DefaultEmitterRate = 5f;

    internal static readonly HashSet<string> Emitters = new(StringComparer.Ordinal) { "sphererandom", "boxrandom" };

    internal static readonly HashSet<string> Initializers = new(StringComparer.Ordinal)
    {
        "lifetimerandom", "sizerandom", "colorrandom", "rotationrandom", "velocityrandom", "alpharandom",
        "angularvelocityrandom", "turbulentvelocityrandom", "mapsequencearoundcontrolpoint"
    };

    internal static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "movement", "alphafade", "sizechange", "angularmovement", "colorchange", "alphachange",
        "turbulence", "vortex", "controlpointattract", "controlpointforce", "remapvalue"
    };

    internal static readonly HashSet<string> OscillateOperators = new(StringComparer.Ordinal) { "oscillateposition", "oscillatealpha", "oscillatesize" };

    internal static readonly HashSet<string> Renderers = new(StringComparer.Ordinal) { "sprite", "spritetrail" };

    /// <summary>
    /// 精灵帧的取法：缺省与 sequence 按粒子年龄推进，randomframe 每个粒子出生时抽一帧，都是每粒子的标记。
    /// 其它写法没有核过，判不满足。
    /// </summary>
    internal static readonly HashSet<string> AnimationModes = new(StringComparer.Ordinal) { "sequence", "randomframe" };

    /// <summary>间歇发射的四个字段：min/max 延迟与 min/max 持续时长。</summary>
    internal static readonly string[] PeriodicKeys = ["minperiodicdelay", "maxperiodicdelay", "minperiodicduration", "maxperiodicduration"];

    /// <summary>对象与祖先上会改变发射器位置、可见性或整体透明度的属性；这些属性被关键帧或脚本驱动时判不满足。</summary>
    internal static readonly string[] AncestorMotionKeys = ["origin", "angles", "scale", "visible", "alpha"];

    /// <summary>寿命随机时替换时刻逐代打散（第 k 代的展宽是 k × (L_max − L_min)），要经过 ⌈L_max / (L_max − L_min)⌉ 代才混合，再加 1 代。</summary>
    internal static int WarmupGenerations(double lifetimeMax, double lifetimeMin) =>
        (int)Math.Ceiling(lifetimeMax / (lifetimeMax - lifetimeMin)) + 1;

    /// <summary>预热（真实秒）= starttime + (寿命上界 × 寿命覆盖 × 代数 + 间歇发射上界) / rate 覆盖，取到微秒。</summary>
    internal static double WarmupSeconds(double startTime, double lifetimeMax, double lifetimeScale, int generations,
        double periodicWarmup, double rateScale) =>
        Round(startTime + (lifetimeMax * lifetimeScale * generations + periodicWarmup) / rateScale);

    /// <summary>锁定周期的预热：进入周期态的起点帧换成秒，按微秒向上取，保证 ⌈预热秒 × fps⌉ 不少于起点帧。</summary>
    internal static double CycleWarmupSeconds(ulong cycleStartFrame, uint fpsNumerator, uint fpsDenominator) =>
        (double)(decimal.Ceiling((decimal)cycleStartFrame * fpsDenominator / fpsNumerator * 1_000_000m) / 1_000_000m);

    /// <summary>循环上限（秒）内能容下的最长替换周期（输出帧）。</summary>
    internal static ulong MaximumPeriodFrames(double loopCeilingSeconds, uint fpsNumerator, uint fpsDenominator) =>
        (ulong)Math.Floor(loopCeilingSeconds * fpsNumerator / fpsDenominator);

    /// <summary>turbulence 算子共享场的周期（系统秒）：Perlin 排列表周期 256 / (2 × scale × timescale)；不推进时为 null。</summary>
    internal static double? TurbulenceFieldPeriodSeconds(double scale, double timescale) =>
        scale * timescale > 0 ? Round(256 / (2 * scale * timescale)) : null;

    /// <summary>判据快照里的秒数一律取到微秒（远离零舍入）。</summary>
    internal static double Round(double seconds) => Math.Round(seconds, 6, MidpointRounding.AwayFromZero);
}
