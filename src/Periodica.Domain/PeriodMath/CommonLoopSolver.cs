namespace Periodica.Domain;

public enum CommonLoopPeriodEvidence { Analytic, Observed, Unknown, NonPeriodic }

/// <summary>循环候选的择优倾向。效率档要最短周期，平衡档先收紧分量调速预算，质量档要最低总调速成本。</summary>
public enum CommonLoopPreference { Performance, Balanced, Quality }

/// <summary>
/// A measured or analytic period. ExactSeconds is required for an unretimed hard seam;
/// a floating-point observation alone remains evidence, not a proof of an exact repeat.
/// </summary>
public sealed record CommonLoopPeriod(double Seconds, CommonLoopPeriodEvidence Evidence,
    CommonLoopRational? ExactSeconds = null);

public sealed record CommonLoopComponent(string Id, CommonLoopPeriod? BasePeriod, bool AllowRetime = false);

public enum CommonLoopConstraintKind
{
    UnknownPeriod,
    NonPeriodic,
    MissingExactPeriod,
    FixedPeriodDoesNotClose,
    RetimeOutsideLimit,
    DurationOutsideRange,
    SearchBudgetExceeded,
    ArithmeticOverflow
}

public sealed record CommonLoopConstraint(string ComponentId, CommonLoopConstraintKind Kind, string Detail);

public sealed record CommonLoopSolveRequest(uint FpsNumerator, uint FpsDenominator,
    IReadOnlyList<CommonLoopComponent> Components, CommonLoopRational? MinimumDuration = null,
    CommonLoopRational? MaximumDuration = null, double MaximumRetimePercent = 2,
    int MaximumCandidates = 20, ulong MaximumFrameCandidates = 1_000_000,
    CommonLoopPreference Preference = CommonLoopPreference.Balanced);

public sealed record CommonLoopComponentCycle(string ComponentId, CommonLoopPeriodEvidence Evidence,
    ulong Cycles, double OldPeriodSeconds, double NewPeriodSeconds, double SpeedMultiplier,
    double DeltaPercent, bool Retimed);

public sealed record CommonLoopCandidate(ulong Frames, uint FpsNumerator, uint FpsDenominator,
    double Seconds, IReadOnlyList<CommonLoopComponentCycle> Components, double TotalRetimeCostPercent);

public sealed record CommonLoopEvaluation(ulong Frames, double Seconds, CommonLoopCandidate? Candidate,
    IReadOnlyList<CommonLoopConstraint> Constraints);

/// <summary>求解器返回空候选时的可判定原因。没有原因的空返回就是静默失败，上层没法讲给用户听。</summary>
public enum CommonLoopNoCandidateKind
{
    /// <summary>不可调速分量的公共帧步长已经超过求解器实际使用的循环时长上限：上限内确定无解。</summary>
    FixedPeriodExceedsCeiling,
    /// <summary>公共帧步长在上限内，但该步长网格上没有任何一帧能让所有分量同时闭合。</summary>
    NoFrameOnFixedStepSatisfiesComponents,
    /// <summary>根本没有分量可解：要进视频的内容上没有任何已证明的时间机制。</summary>
    NoTemporalMechanism,
    /// <summary>单段视频调速找不到落在输出帧网格上的整数帧长。</summary>
    NoExactVideoRetimeFrame
}

/// <summary>
/// 空候选的结构化原因。秒数一律已按输出帧率换算，上层直接引用，不得另行硬编码上限。
/// </summary>
public sealed record CommonLoopNoCandidate(CommonLoopNoCandidateKind Kind, double CeilingSeconds,
    double? FixedPeriodSeconds = null);

public sealed record CommonLoopSearchResult(IReadOnlyList<CommonLoopCandidate> Candidates,
    IReadOnlyList<CommonLoopConstraint> UnresolvedConstraints, bool SearchComplete, ulong? FixedFrameStep,
    CommonLoopPreference Preference = CommonLoopPreference.Balanced, double RetimeBudgetPercent = 2, bool BudgetRelaxed = false,
    CommonLoopNoCandidate? NoCandidate = null);

/// <summary>
/// Finds repeat times on the exact rational output frame grid. It never turns an unknown or
/// non-periodic component into a hard-cut candidate; callers keep such seam constraints separate.
/// </summary>
public static class CommonLoopSolver
{
    /// <summary>--loop-max-seconds 的默认值（秒）。</summary>
    public const double DefaultLoopLengthMaximumSeconds = 600;

    /// <summary>--loop-max-seconds 允许的上限（秒）。</summary>
    public const double MaximumLoopLengthSeconds = 3600;

    /// <summary>请求没带下限时的最短循环时长（10 秒）。</summary>
    public static readonly CommonLoopRational DefaultMinimum = new(10);
    private static readonly CommonLoopRational DefaultMaximum = Ceiling(DefaultLoopLengthMaximumSeconds);

    /// <summary>
    /// 请求没带上限时使用的循环时长上限（秒），等于 --loop-max-seconds 的默认值。
    /// 实际上限由调用方按 --loop-max-seconds 传入；上层讲"超上限"时复述请求里实际用的那个数，不要自己写死。
    /// </summary>
    public static double DefaultMaximumSeconds => DefaultMaximum.ToSeconds();

    /// <summary>
    /// 把 --loop-max-seconds 的秒数换成求解器上限（取到微秒，0 &lt; 秒数 ≤ 3600）。
    /// 所有周期分量（着色器、动画、视频、摆动改频）共用这一个上限。
    /// </summary>
    public static CommonLoopRational Ceiling(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > MaximumLoopLengthSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "The loop length ceiling must be above 0 and at most 3600 seconds.");
        long micros = (long)Math.Round(seconds * 1_000_000, MidpointRounding.AwayFromZero);
        return new(Math.Max(1, micros), 1_000_000);
    }

    /// <summary>平衡档先收紧到的分量调速预算（百分比）。</summary>
    public const double BalancedRetimePercent = 1;

    /// <summary>
    /// 按择优倾向给出候选表。效率档保留"预算内最短周期"前插；质量档纯按总调速成本排序；
    /// 平衡档先用更紧的分量预算搜一遍，该预算下无解时才回落到请求上限并标注 BudgetRelaxed。
    /// 三档都返回完整候选表，只是排序与预算不同。
    /// </summary>
    public static CommonLoopSearchResult Suggest(CommonLoopSolveRequest request)
    {
        if (request.Preference != CommonLoopPreference.Balanced || request.MaximumRetimePercent <= BalancedRetimePercent)
            return Search(request, request.MaximumRetimePercent, false);
        CommonLoopSearchResult tightened = Search(request with { MaximumRetimePercent = BalancedRetimePercent }, BalancedRetimePercent, false);
        // 收紧预算下已有解，或失败原因根本不是预算（未知周期、非周期、搜索规模超限），都不回落。
        return tightened.Candidates.Count > 0 || tightened.UnresolvedConstraints.Count > 0 || !tightened.SearchComplete
            ? tightened
            : Search(request, request.MaximumRetimePercent, true);
    }

    private static CommonLoopSearchResult Search(CommonLoopSolveRequest request, double budgetPercent, bool budgetRelaxed)
    {
        var setup = Prepare(request);
        if (setup.Constraints.Count != 0)
            return new([], setup.Constraints, true, setup.FixedFrameStep, request.Preference, budgetPercent, budgetRelaxed);

        (ulong minimumFrames, ulong maximumFrames) = FrameRange(setup.Minimum, setup.Maximum, request.FpsNumerator, request.FpsDenominator);
        ulong step = setup.FixedFrameStep ?? 1;
        ulong first = RoundUp(minimumFrames, step);
        double ceilingSeconds = setup.Maximum.ToSeconds();
        // 公共帧步长已经跨过上限：这是算术上确定的"无解"，必须带着步长与上限返回，不能静默给个空约束。
        if (first > maximumFrames)
            return new([], [], true, step, request.Preference, budgetPercent, budgetRelaxed,
                new(CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling, ceilingSeconds, FrameSeconds(step, request)));
        UInt128 count = ((UInt128)maximumFrames - first) / step + 1;
        if (count > request.MaximumFrameCandidates)
        {
            var limit = new CommonLoopConstraint("solver", CommonLoopConstraintKind.SearchBudgetExceeded,
                $"The frame range contains {count} candidates at a {step}-frame fixed-period step; the configured bound is {request.MaximumFrameCandidates}.");
            return new([], [limit], false, step, request.Preference, budgetPercent, budgetRelaxed);
        }

        var candidates = new List<CommonLoopCandidate>();
        for (ulong frame = first; frame <= maximumFrames;)
        {
            var evaluation = EvaluatePrepared(request, setup, frame, false);
            if (evaluation.Candidate is not null) candidates.Add(evaluation.Candidate);
            if (maximumFrames - frame < step) break;
            frame += step;
        }
        if (candidates.Count == 0)
            return new([], [], true, step, request.Preference, budgetPercent, budgetRelaxed,
                new(CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents, ceilingSeconds,
                    setup.FixedFrameStep is ulong fixedStep ? FrameSeconds(fixedStep, request) : null));

        var ordered = candidates.OrderBy(c => c.TotalRetimeCostPercent).ThenBy(c => c.Seconds).ThenBy(c => c.Frames);
        if (request.Preference == CommonLoopPreference.Quality)
        {
            // 质量档不前插最短周期：按总调速成本升序，同成本取更短周期；长度只受搜索上界约束。
            var cheapest = ordered.Take(request.MaximumCandidates).ToArray();
            return new(cheapest, [], true, step, request.Preference, budgetPercent, budgetRelaxed);
        }

        // The first valid frame establishes the earliest integer-cycle combination. Prefer the
        // most balanced frame for that combination before listing lower-cost longer captures.
        CommonLoopCandidate earliest = candidates[0];
        CommonLoopCandidate preferred = candidates.Where(candidate => SameCycles(candidate, earliest))
            .OrderBy(MaximumAbsoluteRetimePercent).ThenBy(candidate => candidate.TotalRetimeCostPercent)
            .ThenBy(candidate => candidate.Frames).First();
        var selected = new List<CommonLoopCandidate> { preferred };
        foreach (CommonLoopCandidate candidate in ordered)
        {
            if (selected.Count >= request.MaximumCandidates) break;
            if (!SameCandidate(candidate, preferred)) selected.Add(candidate);
        }
        return new(selected, [], true, step, request.Preference, budgetPercent, budgetRelaxed);
    }

    private static double FrameSeconds(ulong frames, CommonLoopSolveRequest request) =>
        FrameGrid.Seconds(frames, request.FpsNumerator, request.FpsDenominator);

    /// <summary>Evaluates one exact output-frame duration, including useful reasons it cannot close.</summary>
    public static CommonLoopEvaluation EvaluateAtFrames(CommonLoopSolveRequest request, ulong frames)
    {
        if (frames == 0) throw new ArgumentOutOfRangeException(nameof(frames));
        var setup = Prepare(request);
        return EvaluatePrepared(request, setup, frames, true);
    }

    private static CommonLoopEvaluation EvaluatePrepared(CommonLoopSolveRequest request, Setup setup, ulong frames, bool enforceRange)
    {
        double seconds = FrameSeconds(frames, request);
        var constraints = new List<CommonLoopConstraint>(setup.Constraints);
        if (enforceRange && (CompareFrameToDuration(frames, request.FpsNumerator, request.FpsDenominator, setup.Minimum) < 0 ||
            CompareFrameToDuration(frames, request.FpsNumerator, request.FpsDenominator, setup.Maximum) > 0))
            constraints.Add(new("duration", CommonLoopConstraintKind.DurationOutsideRange, "The explicit duration is outside the configured search range."));
        if (setup.FixedFrameStep is ulong step && frames % step != 0)
            constraints.Add(new("fixed-periods", CommonLoopConstraintKind.FixedPeriodDoesNotClose, "The exact fixed periods do not all complete on this frame."));
        if (constraints.Count != 0) return new(frames, seconds, null, constraints);

        var cycles = new List<CommonLoopComponentCycle>(request.Components.Count);
        foreach (var component in request.Components)
        {
            CommonLoopPeriod period = component.BasePeriod!;
            if (!component.AllowRetime)
            {
                CommonLoopRational exact = period.ExactSeconds!.Value;
                UInt128 value = (UInt128)frames * request.FpsDenominator * (ulong)exact.Denominator /
                    ((UInt128)request.FpsNumerator * (ulong)exact.Numerator);
                if (value == 0 || value > ulong.MaxValue) return Overflow(frames, seconds, component.Id);
                cycles.Add(new(component.Id, period.Evidence, (ulong)value, period.Seconds, period.Seconds, 1, 0, false));
                continue;
            }

            double idealCycles = seconds / period.Seconds;
            double tolerance = request.MaximumRetimePercent / 100;
            double lower = Math.Ceiling(idealCycles * (1 - tolerance) - 1e-12);
            double upper = Math.Floor(idealCycles * (1 + tolerance) + 1e-12);
            if (upper < 1 || lower > upper || lower > ulong.MaxValue)
            {
                constraints.Add(new(component.Id, CommonLoopConstraintKind.RetimeOutsideLimit,
                    "No positive integer cycle count fits the allowed local speed adjustment."));
                continue;
            }
            ulong selected = (ulong)Math.Clamp(Math.Round(idealCycles, MidpointRounding.AwayFromZero), lower, upper);
            double multiplier = selected * period.Seconds / seconds;
            double delta = 100 * (multiplier - 1);
            cycles.Add(new(component.Id, period.Evidence, selected, period.Seconds, period.Seconds / multiplier,
                multiplier, delta, Math.Abs(delta) > 1e-12));
        }
        if (constraints.Count != 0) return new(frames, seconds, null, constraints);
        double cost = cycles.Sum(c => Math.Abs(c.DeltaPercent));
        return new(frames, seconds, new(frames, request.FpsNumerator, request.FpsDenominator, seconds, cycles, cost), []);
    }

    private static CommonLoopEvaluation Overflow(ulong frames, double seconds, string componentId) => new(frames, seconds, null,
        [new(componentId, CommonLoopConstraintKind.ArithmeticOverflow, "The requested duration exceeds the supported cycle-count range.")]);

    private static bool SameCycles(CommonLoopCandidate left, CommonLoopCandidate right) =>
        left.Components.Count == right.Components.Count && left.Components.Zip(right.Components).All(pair => pair.First.Cycles == pair.Second.Cycles);

    private static bool SameCandidate(CommonLoopCandidate left, CommonLoopCandidate right) => left.Frames == right.Frames;

    private static double MaximumAbsoluteRetimePercent(CommonLoopCandidate candidate) =>
        candidate.Components.Max(component => Math.Abs(component.DeltaPercent));

    private static Setup Prepare(CommonLoopSolveRequest request)
    {
        ValidateRequest(request);
        var constraints = new List<CommonLoopConstraint>();
        ulong? step = 1;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in request.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id)) throw new ArgumentException("Each component needs an id.", nameof(request));
            if (!ids.Add(component.Id)) throw new ArgumentException("Component ids must be unique.", nameof(request));
            CommonLoopPeriod? period = component.BasePeriod;
            if (period is null || period.Evidence == CommonLoopPeriodEvidence.Unknown)
            {
                constraints.Add(new(component.Id, CommonLoopConstraintKind.UnknownPeriod, "No periodic seam is established for this component."));
                continue;
            }
            if (period.Evidence == CommonLoopPeriodEvidence.NonPeriodic)
            {
                constraints.Add(new(component.Id, CommonLoopConstraintKind.NonPeriodic, "This component is explicitly non-periodic and cannot be hard-cut."));
                continue;
            }
            if (!double.IsFinite(period.Seconds) || period.Seconds <= 0)
                throw new ArgumentException($"Component '{component.Id}' has no positive finite period.", nameof(request));
            if (component.AllowRetime) continue;
            if (period.ExactSeconds is null)
            {
                constraints.Add(new(component.Id, CommonLoopConstraintKind.MissingExactPeriod,
                    "An unretimed component needs an exact rational period to prove a frame-aligned seam."));
                continue;
            }
            double exactSeconds = period.ExactSeconds.Value.ToSeconds();
            if (Math.Abs(exactSeconds - period.Seconds) > Math.Max(1e-12, Math.Abs(exactSeconds) * 1e-12))
                throw new ArgumentException($"Component '{component.Id}' disagrees with its exact period.", nameof(request));
            try
            {
                ulong componentStep = FixedFrameStep(period.ExactSeconds.Value, request.FpsNumerator, request.FpsDenominator);
                step = FrameGrid.LeastCommonMultiple(step.Value, componentStep);
            }
            catch (OverflowException)
            {
                constraints.Add(new(component.Id, CommonLoopConstraintKind.ArithmeticOverflow, "Fixed-period frame constraints exceed the supported range."));
            }
        }
        return new(request.MinimumDuration ?? DefaultMinimum, request.MaximumDuration ?? DefaultMaximum, step, constraints);
    }

    private static void ValidateRequest(CommonLoopSolveRequest request)
    {
        if (request.FpsNumerator == 0 || request.FpsDenominator == 0 || request.Components is null || request.Components.Count == 0 ||
            request.MaximumCandidates <= 0 || request.MaximumFrameCandidates == 0 || !double.IsFinite(request.MaximumRetimePercent) ||
            request.MaximumRetimePercent < 0 || request.MaximumRetimePercent > RetimeProfile.MaximumCommonRetimePercent)
            throw new ArgumentException("Use positive rational FPS and components, a positive bounded search, and a retime limit from 0 to 10 percent.", nameof(request));
        CommonLoopRational minimum = request.MinimumDuration ?? DefaultMinimum, maximum = request.MaximumDuration ?? DefaultMaximum;
        if ((Int128)minimum.Numerator * maximum.Denominator > (Int128)maximum.Numerator * minimum.Denominator ||
            maximum.ToSeconds() > MaximumLoopLengthSeconds)
            throw new ArgumentException("Duration range must be positive, ordered, and no longer than 3600 seconds.", nameof(request));
    }

    private static (ulong Minimum, ulong Maximum) FrameRange(CommonLoopRational minimum, CommonLoopRational maximum, uint fpsNumerator, uint fpsDenominator)
    {
        UInt128 minimumNumerator = (UInt128)minimum.Numerator * fpsNumerator;
        UInt128 minimumDenominator = (UInt128)minimum.Denominator * fpsDenominator;
        UInt128 maximumNumerator = (UInt128)maximum.Numerator * fpsNumerator;
        UInt128 maximumDenominator = (UInt128)maximum.Denominator * fpsDenominator;
        UInt128 low = (minimumNumerator + minimumDenominator - 1) / minimumDenominator;
        UInt128 high = maximumNumerator / maximumDenominator;
        if (low > ulong.MaxValue || high > ulong.MaxValue) throw new ArgumentException("Duration range exceeds supported frame counts.");
        return ((ulong)low, (ulong)high);
    }

    private static int CompareFrameToDuration(ulong frames, uint fpsNumerator, uint fpsDenominator, CommonLoopRational duration)
    {
        UInt128 left = (UInt128)frames * fpsDenominator * (ulong)duration.Denominator;
        UInt128 right = (UInt128)duration.Numerator * fpsNumerator;
        return left.CompareTo(right);
    }

    private static ulong FixedFrameStep(CommonLoopRational period, uint fpsNumerator, uint fpsDenominator)
    {
        UInt128 denominator = (UInt128)fpsNumerator * (ulong)period.Numerator;
        UInt128 numerator = (UInt128)fpsDenominator * (ulong)period.Denominator;
        UInt128 result = denominator / FrameGrid.GreatestCommonDivisor(denominator, numerator);
        if (result == 0 || result > ulong.MaxValue) throw new OverflowException();
        return (ulong)result;
    }

    private static ulong RoundUp(ulong value, ulong step)
    {
        ulong remainder = value % step;
        if (remainder == 0) return value;
        if (value > ulong.MaxValue - (step - remainder)) throw new OverflowException();
        return value + step - remainder;
    }

    private sealed record Setup(CommonLoopRational Minimum, CommonLoopRational Maximum, ulong? FixedFrameStep,
        List<CommonLoopConstraint> Constraints);
}
