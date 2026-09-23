namespace Periodica.Domain;

/// <summary>求解器的一层输入：摆动方程加换算到输出像素的振幅（读不到尺寸时振幅为 null，这一层的慢项无法判定，求解器不给解）。</summary>
public sealed record SwayRetimeInput(SwayModel Model, double? AmplitudeXPixels, double? AmplitudeYPixels);

/// <summary>
/// 一项的改频结果。可见项（原周期 &lt; 60 s）取 Cycles = round(CyclesExact) 整数圈；慢项在"冻结"与"改到整数圈"里取峰值速度偏差小的。
/// 冻结项系数写 0（退化为常数相位），DeltaPercent 是非冻结项的相对频率改动 n/x − 1。
/// SpeedDeviationPixelsPerSecond 是峰值速度偏差：冻结为 2πa/T，改到 T′ 为 2πa·|1/T′ − 1/T|（a 为输出像素振幅，读不到时为 null）。
/// 冻结项另给不冻结时 10 分钟 / 1 小时内的最大漂移上界（次要记录）。
/// </summary>
public sealed record SwayRetimeTerm(int Index, string Name, double CoefficientOld, double CoefficientNew,
    double CyclesExact, long Cycles, bool Frozen, double DeltaPercent, double OldPeriodSeconds, double? NewPeriodSeconds,
    double AmplitudePixels, double? FrozenDrift10MinutesPixels, double? FrozenDrift60MinutesPixels, double? SpeedDeviationPixelsPerSecond)
{
    /// <summary>原周期短于 60 s 的看得见的摇摆项。</summary>
    public bool Visible => OldPeriodSeconds < SwayRecurrenceSolver.VisiblePeriodSeconds;
}

public sealed record SwayRetimeLayer(SwayModel Model, double? AmplitudeXPixels, double? AmplitudeYPixels, IReadOnlyList<SwayRetimeTerm> Terms);

/// <summary>一个候选周期 P 上的最优方案：L = Multiple·P 帧，所有摆动层逐项精确闭合。</summary>
public sealed record SwayRetimeSolution(ulong BaseFrames, ulong Multiple, ulong Frames, double Seconds,
    double MaximumChangePercent, double FrozenDrift10MinutesPixels, IReadOnlyList<SwayRetimeLayer> Layers)
{
    private IEnumerable<SwayRetimeTerm> Moving => Layers.SelectMany(layer => layer.Terms).Where(term => term.CoefficientOld != 0);

    /// <summary>原本在动、被冻结的项（系数本来就是 0 的不算）。</summary>
    public IEnumerable<(SwayRetimeLayer Layer, SwayRetimeTerm Term)> FrozenTerms =>
        Layers.SelectMany(layer => layer.Terms.Where(term => term.Frozen && term.CoefficientOld != 0).Select(term => (layer, term)));

    public int FrozenCount => FrozenTerms.Count();

    /// <summary>被冻结项里最短的原周期（秒）；没有冻结项时为 null。</summary>
    public double? FrozenMinimumPeriodSeconds => FrozenTerms.Select(item => (double?)item.Term.OldPeriodSeconds).Min();

    /// <summary>可见摆动项（原周期 &lt; 60 s）里非冻结项的最大 |δ|（百分比）；没有这类项时为 0。</summary>
    public double MaximumVisibleChangePercent => Moving.Where(term => !term.Frozen && term.Visible)
        .Select(term => Math.Abs(term.DeltaPercent)).DefaultIfEmpty(0).Max();

    /// <summary>次要记录：慢项（原周期 ≥ 60 s）里改频项的最大 |δ|（百分比）。慢项的观感量是速度偏差，不是百分比。</summary>
    public double MaximumEnvelopeChangePercent => Moving.Where(term => !term.Frozen && !term.Visible)
        .Select(term => Math.Abs(term.DeltaPercent)).DefaultIfEmpty(0).Max();

    /// <summary>慢项（冻结或改频）的最大峰值速度偏差（输出像素/秒）；有振幅读不到的慢项时为 null。</summary>
    public double? MaximumSlowSpeedDeviationPixelsPerSecond => Moving.Where(term => !term.Visible)
        .Aggregate((double?)0, (worst, term) => worst is double known && term.SpeedDeviationPixelsPerSecond is double deviation ? Math.Max(known, deviation) : null);

    /// <summary>可见摆动项改频后的最大峰值速度偏差（输出像素/秒），即第二道闸看的量；有振幅读不到的可见项时为 null。</summary>
    public double? MaximumVisibleSpeedDeviationPixelsPerSecond => Moving.Where(term => term.Visible && !term.Frozen)
        .Aggregate((double?)0, (worst, term) => worst is double known && term.SpeedDeviationPixelsPerSecond is double deviation ? Math.Max(known, deviation) : null);

    /// <summary>最慢的可见摆动项在这个 L 内走的整数圈数（圈数下限看的量）；没有在动的可见项时为 null。</summary>
    public long? SlowestVisibleCycles => Moving.Where(term => term.Visible && !term.Frozen)
        .Select(term => (long?)term.Cycles).Min();

    /// <summary>
    /// 这个 L 内的最坏相位差（圈）：改频项在 L 末与原作的相位之差 |n − x|，取各项最大。
    /// 观感按它排序，不按百分比——相位差 = δ·t/T，短周期项积累得快，3% 档可能比 5% 档偏得更多（sway-budget-scan.md §3.1）。
    /// 每项取 n = round(x)，所以这个值恒 &lt; 0.5 圈：放大预算只让相位偏得更快，不让它偏得更多。
    /// </summary>
    public double MaximumPhaseDriftCycles => Moving.Where(term => !term.Frozen)
        .Select(term => Math.Abs(term.Cycles - term.CyclesExact)).DefaultIfEmpty(0).Max();

    /// <summary>改动最大的那一项（非冻结项里 |δ| 最大）；全部冻结时为 null。</summary>
    public (SwayRetimeLayer Layer, SwayRetimeTerm Term)? MaximumChangeTerm => Layers
        .SelectMany(layer => layer.Terms.Where(term => !term.Frozen).Select(term => (layer, term)))
        .OrderByDescending(item => Math.Abs(item.term.DeltaPercent)).Select(item => ((SwayRetimeLayer, SwayRetimeTerm)?)item).FirstOrDefault();
}

/// <summary>
/// 方案 B "观感影响最小"版（设计 §6、§11；原型 D:\WPE-sway-proto\per_term_best.py）：给定其余可闭合分量的精确周期
/// P 与循环上限 Lmax，在 k = 1..⌊Lmax/P⌋ 上让每项在 L 内走整数圈或冻结，R = 0 精确闭合。
/// 规则 v2（fix/sway-guards，主线程裁定）：
/// - 可见项（原周期 T &lt; 60 s）不许冻结，只能改到 round(x) 整数圈；round(x) = 0 时这个 L 放弃。
/// - 慢项（T ≥ 60 s）在冻结（偏差 2πa/T）与改到 n = round(x) 圈（偏差 2πa·|n/L − 1/T|）里取峰值速度偏差小的；
///   偏差超过 0.1 px/s 时这个 L 放弃。慢项的百分比改动不是合适的观感量：905 s 的包络改到 600 s 是 50%，速度差却可能远小于察觉阈值。
/// - L = 1 帧时只要还有项在动就放弃，不产出 1 帧静止候选（旧规则在 P = 1 帧上 k = 1 把全部项冻结、改动为 0，恒胜出）。
/// - 合规的 L 里按 (可见项最大 |δ| 取 6 位, 慢项最大速度偏差取 6 位, k) 升序取第一个。
/// </summary>
public static class SwayRecurrenceSolver
{
    /// <summary>可见摆动项与慢项的分界（秒）：原周期短于它的项算看得见的摇摆。</summary>
    public const double VisiblePeriodSeconds = 60;

    /// <summary>
    /// 慢项允许的峰值速度偏差（输出像素/秒）：约 10 秒 1 像素，远低于慢漂移的可察觉量级。临时值，等用户看样片后可能调。
    /// </summary>
    public const double MaximumSlowSpeedDeviationPixelsPerSecond = 0.1;

    /// <summary>
    /// 可见摆动项允许的峰值速度偏差（输出像素/秒）：百分比预算之外的第二道闸。
    /// 理由（sway-budget-scan.md §4 第 4 条）：百分比不完全代表观感，3% 预算下有 3 案的可见项偏差冲到 0.126–0.223 px/s，
    /// 是慢项标准的 1.3–2.2 倍，而规则 v2 的现状 27 案全部 ≤ 0.057。振幅大、可见项周期偏长的案要靠这道闸兜住。
    /// </summary>
    public const double MaximumVisibleSpeedDeviationPixelsPerSecond = 0.2;

    /// <summary>
    /// 最慢的可见摆动项在一个循环内至少要走的圈数。防的是"循环比摆动周期还短"：不加下限时 2% 以上的预算会把 L 压到
    /// 19–52 s，而该案最慢可见项周期 20–56 s，整段循环里那一项只摆一个来回，循环感直接暴露（同报告 §2.1）。
    /// 这是硬护栏，不是档位旋钮，三档相同。
    /// </summary>
    public const long MinimumVisibleCycles = 3;

    /// <summary>循环长度下限（秒），与圈数下限配套：P 粒度为 1 帧的案能连续取 L，光靠圈数拦不住 20 s 循环。</summary>
    public const double MinimumLoopSeconds = 60;

    /// <summary>冻结项漂移的比较窗口（秒），与原型一致（次要记录）。</summary>
    public const double DriftWindowSeconds = 600;

    /// <summary>一项在 L 秒上的处置。Deviation 为 NaN 表示振幅读不到。</summary>
    private readonly record struct Choice(bool Frozen, long Cycles, double Delta, double Deviation, bool Visible, bool Admissible);

    /// <summary>规则 v2 的逐项判定（Solve 的标量循环与 Detail 共用）；amplitude 为 NaN 表示读不到。</summary>
    private static Choice Choose(double omega, double amplitude, double seconds)
    {
        double cycles = omega * seconds / (2 * Math.PI);
        long whole = (long)Math.Round(cycles);
        bool visible = 2 * Math.PI / omega < VisiblePeriodSeconds;
        // 改到 whole 圈后的角频率与原角频率之差乘振幅，就是峰值速度偏差。
        double retime = whole == 0 ? double.PositiveInfinity : amplitude * Math.Abs(2 * Math.PI * whole / seconds - omega);
        if (visible)
            // 可见项只能改到整数圈；改完还要过第二道闸（峰值速度偏差 ≤ 0.2 px/s），振幅读不到时（NaN）同样不合规。
            return whole == 0 ? new(true, 0, 0, amplitude * omega, true, false)
                : new(false, whole, whole / cycles - 1, retime, true, retime <= MaximumVisibleSpeedDeviationPixelsPerSecond);
        if (double.IsNaN(amplitude))
            return whole == 0 ? new(true, 0, 0, double.NaN, false, false) : new(false, whole, whole / cycles - 1, double.NaN, false, false);
        double freeze = amplitude * omega;
        // 相等时保留运动（改频），不冻结。
        bool frozen = freeze < retime;
        double deviation = frozen ? freeze : retime;
        return new(frozen, frozen ? 0 : whole, frozen ? 0 : whole / cycles - 1, deviation, false, deviation <= MaximumSlowSpeedDeviationPixelsPerSecond);
    }

    /// <summary>k 的上限：⌊Lmax / P⌋（与原型同样加 1e-9 防浮点误差）。</summary>
    public static ulong MaximumMultiple(ulong baseFrames, uint fpsNumerator, uint fpsDenominator, double maximumSeconds)
    {
        if (baseFrames == 0 || fpsNumerator == 0 || fpsDenominator == 0 || !double.IsFinite(maximumSeconds) || maximumSeconds <= 0) return 0;
        double multiple = Math.Floor(maximumSeconds * fpsNumerator / ((double)baseFrames * fpsDenominator) + 1e-9);
        return multiple < 1 ? 0 : (ulong)multiple;
    }

    /// <summary>
    /// 在候选周期 P 上求 L = kP。<paramref name="budgetPercent"/> 给了预算就取"可见项最大改动 ≤ 预算的最短 L"（档位语义：
    /// 预算换长度），没给（质量档与旧接口）就取"可见项最大改动最小"的 L。两种目标都先过逐项规则与循环下限，
    /// 不满足就换下一个 L；全都不行时返回 null，由调用方走未解析路径。
    /// </summary>
    /// <param name="minimumSeconds">循环长度下限（秒）；测试可以放宽来单测其它护栏。</param>
    /// <param name="minimumVisibleCycles">最慢可见项的圈数下限。</param>
    public static SwayRetimeSolution? Solve(IReadOnlyList<SwayRetimeInput> layers, ulong baseFrames, uint fpsNumerator,
        uint fpsDenominator, double maximumSeconds, double? budgetPercent = null,
        double minimumSeconds = MinimumLoopSeconds, long minimumVisibleCycles = MinimumVisibleCycles)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0) return null;
        if (layers.Any(layer => layer.Model.Coefficients.Count != 8 || !double.IsFinite(layer.Model.Speed) || layer.Model.Speed == 0))
            throw new ArgumentException("Each sway layer needs eight coefficients and a nonzero finite speed.", nameof(layers));
        ulong maximumMultiple = MaximumMultiple(baseFrames, fpsNumerator, fpsDenominator, maximumSeconds);
        if (maximumMultiple == 0) return null;
        // 预算每层每项的角频率与振幅，内层循环只做标量运算。
        int count = layers.Count * 8;
        var omega = new double[count];
        var amplitude = new double[count];
        for (int layer = 0; layer < layers.Count; ++layer)
            for (int term = 0; term < 8; ++term)
            {
                omega[layer * 8 + term] = Math.Abs(layers[layer].Model.Coefficients[term] * layers[layer].Model.Speed);
                amplitude[layer * 8 + term] = (term < 4 ? layers[layer].AmplitudeXPixels : layers[layer].AmplitudeYPixels) ?? double.NaN;
            }
        (double Change, double Deviation, ulong Multiple) best = (double.PositiveInfinity, double.PositiveInfinity, 0);
        for (ulong multiple = 1; multiple <= maximumMultiple; ++multiple)
        {
            ulong frames = checked(multiple * baseFrames);
            double seconds = Seconds(frames, fpsNumerator, fpsDenominator);
            // 循环长度下限：k 升序时 L 单调增，短于下限的直接跳过。
            if (seconds < minimumSeconds - 1e-9) continue;
            double change = 0, deviation = 0;
            long slowestVisibleCycles = long.MaxValue;
            bool admissible = true, moving = false;
            for (int index = 0; index < count && admissible; ++index)
            {
                if (omega[index] == 0) continue;
                Choice choice = Choose(omega[index], amplitude[index], seconds);
                admissible = choice.Admissible;
                if (!choice.Frozen) moving = true;
                if (choice.Visible)
                {
                    change = Math.Max(change, Math.Abs(choice.Delta));
                    if (!choice.Frozen) slowestVisibleCycles = Math.Min(slowestVisibleCycles, choice.Cycles);
                }
                else deviation = Math.Max(deviation, choice.Deviation);
            }
            // 还有在动的项时不产出 1 帧候选（那是"静止画面"，会让摆动悄悄消失）。
            if (!admissible || frames == 1 && moving) continue;
            // 圈数下限：最慢的可见项至少走满几圈，否则循环感直接暴露（没有可见项时这一条不适用）。
            if (slowestVisibleCycles != long.MaxValue && slowestVisibleCycles < minimumVisibleCycles) continue;
            // 预算档：k 升序，第一个把可见项改动压在预算内的 L 就是最短的可行 L。
            if (budgetPercent is double budget)
            {
                if (100 * change > budget + 1e-9) continue;
                return Detail(layers, baseFrames, multiple, fpsNumerator, fpsDenominator);
            }
            (double Change, double Deviation, ulong Multiple) key = (Math.Round(change, 6), Math.Round(deviation, 6), multiple);
            if (key.Change < best.Change || key.Change == best.Change && (key.Deviation < best.Deviation || key.Deviation == best.Deviation && key.Multiple < best.Multiple))
                best = key;
        }
        // 上限内有 kP 但都不合规时同样返回 null；调用方用 MaximumMultiple 区分两种原因。
        return best.Multiple == 0 ? null : Detail(layers, baseFrames, best.Multiple, fpsNumerator, fpsDenominator);
    }

    /// <summary>在指定 k 上按规则 v2 给出逐项结果，不检查是否合规（合规只由 Choose 判，Solve 只返回合规解）。求解器与测试共用。</summary>
    public static SwayRetimeSolution Detail(IReadOnlyList<SwayRetimeInput> layers, ulong baseFrames, ulong multiple,
        uint fpsNumerator, uint fpsDenominator)
    {
        ulong frames = checked(multiple * baseFrames);
        double seconds = Seconds(frames, fpsNumerator, fpsDenominator);
        var results = new List<SwayRetimeLayer>(layers.Count);
        double maximumChange = 0, maximumDrift = 0;
        foreach (SwayRetimeInput input in layers)
        {
            SwayModel model = input.Model;
            var terms = new SwayRetimeTerm[8];
            for (int index = 0; index < 8; ++index)
            {
                double coefficient = model.Coefficients[index];
                double rate = Math.Abs(coefficient * model.Speed);
                double? known = index < 4 ? input.AmplitudeXPixels : input.AmplitudeYPixels;
                double amplitude = known ?? 0;
                if (rate == 0)
                {
                    terms[index] = new(index, SwayModel.TermNames[index], coefficient, 0, 0, 0, true, 0,
                        double.PositiveInfinity, null, amplitude, 0, 0, 0);
                    continue;
                }
                double cycles = rate * seconds / (2 * Math.PI);
                double period = 2 * Math.PI / rate;
                Choice choice = Choose(rate, known ?? double.NaN, seconds);
                double? deviation = double.IsNaN(choice.Deviation) ? null : choice.Deviation;
                if (choice.Frozen)
                {
                    double drift10 = Drift(amplitude, rate, DriftWindowSeconds), drift60 = Drift(amplitude, rate, 3600);
                    maximumDrift = Math.Max(maximumDrift, drift10);
                    terms[index] = new(index, SwayModel.TermNames[index], coefficient, 0, cycles, 0, true, 0, period, null,
                        amplitude, drift10, drift60, deviation);
                    continue;
                }
                long whole = choice.Cycles;
                maximumChange = Math.Max(maximumChange, Math.Abs(choice.Delta));
                // 新系数让该项在 L 内恰好走 whole 圈：|c'|·s·L = 2π·whole，保留原符号。
                double updated = Math.CopySign(2 * Math.PI * whole / (Math.Abs(model.Speed) * seconds), coefficient);
                terms[index] = new(index, SwayModel.TermNames[index], coefficient, updated, cycles, whole, false, 100 * choice.Delta,
                    period, seconds / whole, amplitude, null, null, deviation);
            }
            results.Add(new(model, input.AmplitudeXPixels, input.AmplitudeYPixels, terms));
        }
        return new(baseFrames, multiple, frames, seconds, 100 * maximumChange, maximumDrift, results);
    }

    /// <summary>不冻结时该项在 dt 秒内的最大位移变化：2A·sin(min(ω·dt/2, π/2))。</summary>
    public static double Drift(double amplitude, double omega, double seconds) =>
        2 * amplitude * Math.Sin(Math.Min(omega * seconds / 2, Math.PI / 2));

    private static double Seconds(ulong frames, uint fpsNumerator, uint fpsDenominator) =>
        FrameGrid.Seconds(frames, fpsNumerator, fpsDenominator);

    /// <summary>shader 分支的速度容差：相对 1e-4，且不超过与相邻速度间距的一半，保证各速度互不误中。</summary>
    public static double[] SpeedTolerances(IReadOnlyList<double> sortedSpeeds)
    {
        var tolerances = new double[sortedSpeeds.Count];
        for (int index = 0; index < sortedSpeeds.Count; ++index)
        {
            double tolerance = 1e-4 * Math.Max(1, Math.Abs(sortedSpeeds[index]));
            if (index > 0) tolerance = Math.Min(tolerance, (sortedSpeeds[index] - sortedSpeeds[index - 1]) / 2);
            if (index + 1 < sortedSpeeds.Count) tolerance = Math.Min(tolerance, (sortedSpeeds[index + 1] - sortedSpeeds[index]) / 2);
            tolerances[index] = tolerance;
        }
        return tolerances;
    }
}
