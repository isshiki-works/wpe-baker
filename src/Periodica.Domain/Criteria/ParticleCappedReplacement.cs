namespace Periodica.Domain;

/// <summary>
/// 槽位常满（maxcount 封顶）且寿命确定的粒子系统：按渲染器（engine ce30024）的离散推进逐帧复刻"死一个补一个"的计数动力学，
/// 推出精确的替换周期（输出帧数）与进入周期态的起点帧。
/// <para>
/// 这类系统每个槽位按同一节拍整批替换，只是每批粒子的随机属性（位置、大小、速度……）不同：计数状态严格周期，画面是按该周期的
/// 统计平稳过程。周期作为锁定分量交给循环求解器，循环长度是它的整数倍时接缝两侧处在同一相位，交叉淡化替换的是同相位、统计等价的
/// 另一批。周期不是 L × lifetime 覆盖 / rate 覆盖 这个连续值：寿命按帧步长做 float 累减，第 N 帧减到 ≤ 0 才被杀，同一帧补发，
/// 所以槽位周期是整数 N 帧（Far From Home 雨 0.795 s @60 fps 是 48 帧 = 0.8 s；Silent Fields 鸟 25 s 的 float 累减多走一步，是 1501 帧）。
/// </para>
/// <para>
/// 复刻的源码语义（ParticleRuntime.cpp、ParticleEmitter.cpp、ParticleProgram.cppm、Particle.cppm、SceneWallpaper.cpp）：
/// 离线步长 dt = fps_den / fps_num（step() 强制），子系统步长 δ = dt × (double)rate 覆盖；每步先 lifecycle（lifetime -= δ，≤ 0 杀），
/// 再各发射器依次 emit（timer += δ；count = ⌊timer / (double)(1f / speed)⌋，one_per_frame 时至多 1；槽位不够时 timer 夹到一个发射间隔，
/// 够时扣掉已发的）。出生寿命 = (float)lifetimerandom × lifetime 覆盖（float），发射率 = rate × count 覆盖（float）。starttime &gt; 0 时
/// 第一次推进前以 1/60 s 为步长预跑 ⌈starttime × 60⌉（至多 240）步。第 k 个输出帧渲染的是推进 k 次之后的状态。
/// </para>
/// <para>
/// 状态 = 最近 N 步每步出生数 + 各发射器 timer（逐位）+ 预跑残留。第 k 帧与第 k + N 帧状态逐位相同，之后永远以 N 为周期（确定性系统）；
/// 找不到这样的 k（补发跟不上死亡、timer 不回到同一值）或 N 超过求解器的循环上限，都判算不准，维持拒绝。
/// </para>
/// </summary>
internal static class ParticleCappedReplacement
{
    /// <summary>一个发射器：发射率（已乘 count 覆盖，float）与 one_per_frame 位。</summary>
    internal sealed record Emitter(float Speed, bool OnePerFrame);

    /// <summary>复刻所需的全部源值，都已按渲染器的类型转换（float）。</summary>
    internal sealed record Input(uint MaxCount, IReadOnlyList<Emitter> Emitters, float Lifetime, float RateScale, float StartTime,
        uint FpsNumerator, uint FpsDenominator);

    /// <summary>
    /// 推导结果。PeriodFrames / CycleStartFrame 成立时 FailureCode 为 null；否则 FailureCode 是稳定代号。
    /// 其余字段是写进判据快照的取证，推到哪一步就有哪几个：子系统步长与 float 寿命总有；周期超上限时有上限帧数；
    /// 数出寿命帧数 N 之后有 N 与 starttime 预跑步数；最后是模拟步数。Baker.Core 的 ParticleStationarity 按这个顺序写成 JSON。
    /// </summary>
    internal sealed record Outcome(ulong? PeriodFrames, ulong? CycleStartFrame, string? FailureCode,
        double SubsystemStepSeconds, float AuthoredLifetime, ulong? MaximumPeriodFrames = null, ulong? ReplacementFrames = null,
        ulong? StartTimePrerunSteps = null, long? SimulatedSteps = null);

    /// <summary>找周期态起点时最多看多少个周期：一般一到两代就进入，看到 16 代仍不回归就判算不准。</summary>
    internal const int CycleStartSearchPeriods = 16;

    internal static Outcome Derive(Input input, ulong maximumPeriodFrames)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.FpsNumerator == 0 || input.FpsDenominator == 0) throw new ArgumentException("帧率必须是正的有理数。", nameof(input));
        double dt = (double)input.FpsDenominator / input.FpsNumerator;
        double delta = dt * input.RateScale;
        if (!(delta > 0) || !double.IsFinite(delta) || input.MaxCount == 0 || input.Emitters.Count == 0 ||
            input.Emitters.Any(emitter => !(emitter.Speed > 0) || !float.IsFinite(emitter.Speed)) || !float.IsFinite(input.Lifetime) ||
            !(input.StartTime >= 0) || !float.IsFinite(input.StartTime))
            return new(null, null, "lifetime_capped_parameters_unverified", delta, input.Lifetime);

        // 一个主步出生的粒子在第 N 次 lifecycle 减到 ≤ 0 被杀。
        ulong period = 0;
        float remaining = input.Lifetime;
        do
        {
            remaining = (float)(remaining - delta);
            ++period;
            if (period > maximumPeriodFrames)
                return new(null, null, "lifetime_capped_period_exceeds_ceiling", delta, input.Lifetime, maximumPeriodFrames);
        } while (remaining > 0f);

        int n = checked((int)period);
        int emitterCount = input.Emitters.Count;
        double[] durations = input.Emitters.Select(emitter => (double)(1.0f / emitter.Speed)).ToArray();
        double[] timers = new double[emitterCount];
        long active = 0;
        long capacity = input.MaxCount;

        // starttime 预跑：步长不同，出生的粒子寿命序列与主步不同，单独逐个跟踪（至多 240 批）。
        var warmupCohorts = new List<(float Lifetime, long Count)>();
        long EmitAll(double step)
        {
            long born = 0;
            for (int index = 0; index < emitterCount; ++index)
            {
                timers[index] += step;
                double elapsed = timers[index], duration = durations[index];
                long requested = elapsed < duration ? 0 : (long)Math.Min(Math.Floor(elapsed / duration), uint.MaxValue);
                if (input.Emitters[index].OnePerFrame && requested > 1) requested = 1;
                if (requested == 0) continue;
                long emitted = Math.Min(requested, Math.Max(0, capacity - active - born));
                double left = Math.Max(0.0, timers[index] - duration * emitted);
                if (emitted < requested) left = Math.Min(left, duration);
                else if (input.Emitters[index].OnePerFrame) left %= duration; // std::fmod：被除数非负时即截断余数
                timers[index] = left;
                born += emitted;
            }
            return born;
        }
        long KillWarmupCohorts(double step)
        {
            long died = 0;
            for (int index = warmupCohorts.Count - 1; index >= 0; --index)
            {
                float next = (float)(warmupCohorts[index].Lifetime - step);
                if (next <= 0f) { died += warmupCohorts[index].Count; warmupCohorts.RemoveAt(index); }
                else warmupCohorts[index] = (next, warmupCohorts[index].Count);
            }
            return died;
        }

        ulong warmupSteps = 0;
        if (input.StartTime > 0)
        {
            double start = input.StartTime;
            const double frameTime = 1.0 / 60.0;
            warmupSteps = (ulong)Math.Min(240, Math.Max(1.0, Math.Ceiling(start / frameTime)));
            double warmupDelta = start / warmupSteps;
            for (ulong step = 0; step < warmupSteps; ++step)
            {
                active -= KillWarmupCohorts(warmupDelta);
                long born = EmitAll(warmupDelta);
                active += born;
                if (born > 0) warmupCohorts.Add((input.Lifetime, born));
            }
        }

        // 主步：ring[k mod N] 是出生于第 k − N 步的批次，第 k 步 lifecycle 杀掉它、emit 后写入本步出生数。
        var ring = new long[n];
        var timerHistory = new double[checked((n + 1) * emitterCount)];
        long run = 0;
        long lastWarmupDeath = warmupCohorts.Count == 0 ? 0 : -1;
        long limit = checked((long)CycleStartSearchPeriods * n + n + 1);
        for (long step = 1; step <= limit; ++step)
        {
            int slot = (int)(step % n);
            long died = ring[slot] + KillWarmupCohorts(delta);
            if (lastWarmupDeath < 0 && warmupCohorts.Count == 0) lastWarmupDeath = step;
            active -= died;
            long born = EmitAll(delta);
            active += born;
            if (active < 0 || active > capacity) throw new InvalidOperationException("封顶粒子计数越界。");
            // born[step] 与 born[step − N]（覆盖前的 ring[slot]）是否相同。
            run = born == ring[slot] ? run + 1 : 0;
            ring[slot] = born;
            int history = (int)(step % (n + 1));
            int previous = step > n ? (int)((step - n) % (n + 1)) : 0;
            bool timersMatch = step > n;
            for (int index = 0; index < emitterCount; ++index)
            {
                if (timersMatch && BitConverter.DoubleToInt64Bits(timerHistory[previous * emitterCount + index]) !=
                    BitConverter.DoubleToInt64Bits(timers[index])) timersMatch = false;
                timerHistory[history * emitterCount + index] = timers[index];
            }
            long cycleStart = step - n;
            if (run >= n && timersMatch && lastWarmupDeath >= 0 && cycleStart >= lastWarmupDeath)
                return new(period, (ulong)cycleStart, null, delta, input.Lifetime, null, period, warmupSteps, step);
        }
        return new(null, null, "lifetime_capped_cycle_not_reached", delta, input.Lifetime, null, period, warmupSteps, limit);
    }
}
