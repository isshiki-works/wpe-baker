using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 循环分析：把源码证明的着色器周期与运行时轨道周期合成捕获用的循环候选。候选与未解析项全程是类型化对象，
/// 由 <see cref="LoopReport.ToJson"/> 渲染一次写进 plan.loop。静止证明、运行时轨道分别在
/// <see cref="StaticProof"/>、<see cref="RuntimeTrackReader"/>。
/// </summary>
internal static class LoopAnalysis
{
    internal static LoopReport Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory, JsonObject runtime,
        IReadOnlyCollection<int> bakedLayerIds, uint fpsNumerator, uint fpsDenominator, double maximumRetimePercent = 2,
        CommonLoopPreference preference = CommonLoopPreference.Balanced,
        double? loopLengthMaximumSeconds = null, JsonArray? videoGroups = null,
        IReadOnlyCollection<ulong>? groupClockSteps = null, IReadOnlyCollection<int>? fullLoopLayerIds = null)
    {
        if (fpsNumerator == 0 || fpsDenominator == 0 || !double.IsFinite(maximumRetimePercent) ||
            maximumRetimePercent < 0 || maximumRetimePercent > RetimeProfile.MaximumCommonRetimePercent)
            throw new ArgumentException("FPS must be positive and retiming must be between zero and ten percent.");
        // 循环时长上限 = --loop-max-seconds（或档位兜底）这一个值：所有周期分量共用。
        double ceilingSeconds = loopLengthMaximumSeconds ?? CommonLoopSolver.DefaultMaximumSeconds;
        CommonLoopRational ceiling = CommonLoopSolver.Ceiling(ceilingSeconds);
        ceilingSeconds = ceiling.ToSeconds();
        var shader = ShaderPeriodAnalysis.Analyze(scene, source, assetsDirectory, runtime, bakedLayerIds, ceilingSeconds, maximumRetimePercent);
        List<LoopUnresolved> unresolved = [.. shader.Unresolved.Select(item => (LoopUnresolved)new ShaderLoopUnresolved(item))];
        RuntimeTrackReader.AddMaterialClockUnresolved(runtime, bakedLayerIds, unresolved, shader.RuledMaterials);
        // 被烘图层上的脚本按时间签名出结论（ScriptTime）：周期进求解器，不能/未收敛记所有者，分配回退据此把那棵子树留实时。
        CommonLoopComponent[] scriptCycles = ScriptComponents(scene, source, assetsDirectory, runtime, bakedLayerIds, fpsNumerator, fpsDenominator, ceiling,
            unresolved, out LoopValuePatch[] scriptPatches, out (int Owner, string Binding, double Seconds)? scriptSettle);
        // 着色器调速：可调速分量在它的作者 pass 上挂时间倍率或旋钮常量，旧值 1（场景里没有这个键）。旋钮值与速度成反比时指数取 -1。
        LoopValuePatch[] patches = [.. shader.Components.Where(item => item.Component.AllowRetime).Select(item => new LoopValuePatch(item.Component.Id,
            "shader_speed", item.OwnerLayerId, item.EffectIndex, item.PassIndex, item.ConstantKey, 0, null, 1, 1, item.Inverse ? -1 : 1)), .. scriptPatches];

        var videoControlScope = VideoControlScope.Resolve(scene, runtime);
        var animation = RuntimeTrackReader.Read(scene, source, assetsDirectory, runtime, bakedLayerIds, unresolved, videoControlScope,
            new ParticleStationarity.FrameClock(fpsNumerator, fpsDenominator, ceilingSeconds), out var particleVerdicts);
        // 封顶 + 确定寿命的粒子层：替换周期（整数帧）作为锁定分量交给求解器，与着色器、轨道的锁定分量同等对待。
        CommonLoopComponent[] particleCycles = ParticleCycleComponents(particleVerdicts);
        // 组间共用时钟的组要求 L 是它自身周期的倍数（见 GroupPeriods）：每个这样的周期当一个锁定分量交给求解器，解完再从候选里去掉。
        CommonLoopComponent[] stepCycles = [.. (groupClockSteps ?? []).Distinct().Select(step =>
        {
            var exact = new CommonLoopRational(checked((long)step * fpsDenominator), fpsNumerator);
            return new CommonLoopComponent($"{GroupStepPrefix}{step}", new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact));
        })];
        LoopSolve solve = SolveLoop(shader, animation, [.. particleCycles, .. scriptCycles, .. stepCycles], fpsNumerator, fpsDenominator, maximumRetimePercent, ceiling, preference);
        // 组周期步长只是可选约束：带步长重解时不为它拆粒子锁，无解就由调用方保持原解（否则锁定粒子会被改判留实时）。
        if (particleCycles.Length > 0 && stepCycles.Length == 0 && solve.Result.Candidates.Count == 0)
        {
            // 锁定周期与其余分量在循环上限内没有公共循环，而不锁这些层时有：这些层退回拒绝（留实时），与判据收紧时的结论一致。
            LoopSolve unlocked = SolveLoop(shader, animation, scriptCycles, fpsNumerator, fpsDenominator, maximumRetimePercent, ceiling, preference);
            if (unlocked.Result.Candidates.Count > 0)
            {
                var evidence = new JsonObject
                {
                    ["solver_no_candidate_kind"] = solve.Result.NoCandidate?.Kind.ToString(),
                    ["solver_fixed_frame_step"] = solve.Result.FixedFrameStep,
                    ["loop_ceiling_seconds"] = ceilingSeconds
                };
                foreach (int owner in particleVerdicts.Where(pair => pair.Value.Lock is not null).Select(pair => pair.Key).ToArray())
                    particleVerdicts[owner] = particleVerdicts[owner].WithoutLock("lifetime_capped_no_common_loop", evidence);
                RuntimeTrackReader.RewriteParticleItems(unresolved, particleVerdicts);
                solve = unlocked;
                particleCycles = [];
            }
        }
        // 各分量都有周期证明、却在上限内凑不出公共循环：点名并不进的所有者层，由分配回退把它们的作者子树留实时，其余照常规划。
        int[]? noCommonLoopOwners = solve.Result.NoCandidate?.Kind is CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents
            or CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling
            ? NoCommonLoopOwners(shader, animation, particleCycles, unresolved, fpsNumerator, fpsDenominator, maximumRetimePercent, ceiling, preference)
            : null;
        // "不能"按所有者层逐层证明，只认最宽松模型：这一层自己的非慢着色器周期项各在预算内独立调频，加上它自己的动画轨道；
        // 不带粒子锁（实际求解可以撤锁）、不带别的层（和别的层凑不到一起只是留实时，不是这一层不能）。含这一层的任何实际候选
        // 都满足这些约束，所以它也无解才是这一层不能；有解（或给不出证明）时，缺独立调频来源的项所在 pass 记未收敛 term_not_retimable。
        // 查的层：实际模型（旋钮 + 每 pass 时间倍率）无解时并不进的层，以及剩多个 π 类的 pass 所在的层。带组步长重解时无解由调用方保持原解，不查。
        bool noLoop = solve.Result.NoCandidate?.Kind is CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents
            or CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling;
        if (stepCycles.Length == 0)
        {
            static string S(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);
            foreach (int owner in (noLoop ? noCommonLoopOwners ?? [] : []).Concat(shader.Terms.Where(x => x.Split).Select(x => x.OwnerLayerId)).Distinct())
            {
                ShaderTerm[] own = [.. shader.Terms.Where(x => x.OwnerLayerId == owner)];
                if (own.Length == 0) continue;
                LoopSolve relaxed = SolveLoop(shader with { Components = [] }, [.. animation.Where(x => x.OwnerLayerId == owner)], [.. own.Select(x => x.Relaxed)],
                    fpsNumerator, fpsDenominator, maximumRetimePercent, ceiling, preference);
                if (relaxed.Result.NoCandidate is { Kind: CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents
                    or CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling } never)
                {
                    CommonLoopComponent slowest = relaxed.Used.MaxBy(x => x.BasePeriod!.Seconds)!;
                    double longest = slowest.BasePeriod!.Seconds;
                    unresolved.Add(new NeverRepeatsUnresolved(owner, ceilingSeconds, $"No loop within the {S(ceilingSeconds)} s limit at a " +
                        $"{S(relaxed.Result.RetimeBudgetPercent)}% retime budget even with every shader period term of this layer retimed independently ({never.Kind}): " +
                        $"slowest component {slowest.Id} has period {S(longest)} s = {S(longest / ceilingSeconds)}x the limit" + (never.FixedPeriodSeconds is double step
                            ? $"; the fixed-period components close together only every {S(step)} s = {S(step / ceilingSeconds)}x the limit" : "") + "."));
                }
                else
                    foreach (var pass in own.Where(x => x.Missing is not null && (noLoop || x.Split))
                        .GroupBy(x => (x.OwnerLayerId, x.EffectIndex, x.PassIndex, x.Resource)))
                        unresolved.Add(new ShaderLoopUnresolved(new(pass.Key.OwnerLayerId, pass.Key.EffectIndex, pass.Key.PassIndex, pass.Key.Resource,
                            ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, "SPIR-V time signature: these terms need independent retiming that " +
                            "the shader source does not provide: " + string.Join("; ", pass.Select(x => $"{x.Relaxed.Id} {S(x.Relaxed.BasePeriod!.Seconds)} s ({x.Missing})")),
                            "term_not_retimable")));
            }
        }
        if (stepCycles.Length > 0)
        {
            static CommonLoopComponent[] Real(CommonLoopComponent[] list) => [.. list.Where(x => !x.Id.StartsWith(GroupStepPrefix, StringComparison.Ordinal))];
            solve = solve with { Locked = Real(solve.Locked), Used = Real(solve.Used), Result = solve.Result with { Candidates = [.. solve.Result.Candidates
                .Select(c => c with { Components = [.. c.Components.Where(x => !x.ComponentId.StartsWith(GroupStepPrefix, StringComparison.Ordinal))] })] } };
        }
        var locked = solve.Locked;
        bool retimeClips = solve.RetimeClips, singleVideoRetime = solve.SingleVideoRetime;
        CommonLoopSearchResult result = solve.Result;
        if (!result.SearchComplete)
            unresolved.Add(new SolverUnresolved(true, null, result.UnresolvedConstraints.Single().Detail));
        foreach (var constraint in result.UnresolvedConstraints.Where(x => x.Kind != CommonLoopConstraintKind.SearchBudgetExceeded))
            unresolved.Add(new SolverUnresolved(false, constraint.ComponentId, constraint.Detail));

        var candidates = new List<LoopCandidate>();
        bool sourceStatic = false;
        // 被烘集合为空时不走静态证明：没有要捕获的画面时，调用方已用 blocker
        // 说明依赖闭包后没剩下可烘组，这里再追一条 source_static 只是同一件事的重复描述。
        if (shader.Components.Count == 0 && shader.Slow.Count == 0 && animation.Count == 0 && scriptCycles.Length == 0 && unresolved.Count == 0 && bakedLayerIds.Count > 0)
        {
            // 没有任何已建模的时间机制时只剩两种结局：证明这一帧是静态的，或者说清楚为什么证明不了。
            // 旧实现在证明不了时一个字都不写，plan 里就出现零候选零理由的 unavailable（沉默拒绝）。
            SourceStaticUnresolved? obstacle = StaticProof.Obstacle(scene, source, assetsDirectory, runtime, bakedLayerIds);
            sourceStatic = obstacle is null;
            if (obstacle is not null) unresolved.Add(obstacle);
        }
        if (sourceStatic)
        {
            // 脚本过 settle 才固定的静态画面：长度取刚过 settle 的帧数，预热一整段后起录
            ulong still = scriptSettle is { } late ? (ulong)Math.Floor(late.Seconds * fpsNumerator / fpsDenominator) + 1 : 1;
            candidates.Add(new LoopCandidate(still, (double)still * fpsDenominator / fpsNumerator, 0d, [], []));
        }
        // 精灵分量按渲染器实际使用的 float32 帧表逐帧判定接缝（见 SpriteSeamPhase）。起点 0 闭合则候选不变；
        // 起点 0 不闭合但整周期预热后闭合，候选带上预热帧数；两者都不闭合，候选移除并写明理由。
        float[][] spriteTables = animation.Where(x => x.SpriteFrameTimes is not null).Select(x => x.SpriteFrameTimes!).ToArray();
        int spriteRejectedCandidates = 0;
        (string Id, HashSet<int> Layers)[] groups = [.. (videoGroups ?? []).OfType<JsonObject>().Select(group => (group["id"]!.GetValue<string>(),
            (group["layer_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>().ToHashSet()))];
        Dictionary<int, float[][]> spriteTablesByOwner = animation.Where(x => x.SpriteFrameTimes is not null).GroupBy(x => x.OwnerLayerId)
            .ToDictionary(owner => owner.Key, owner => owner.Select(x => x.SpriteFrameTimes!).ToArray());
        List<ulong>? clockSteps = null;
        foreach (CommonLoopCandidate solved in result.Candidates)
        {
            SpriteSeamPhase.Selection? spriteSeam = null;
            if (spriteTables.Length > 0)
            {
                spriteSeam = SpriteSeamPhase.Select(spriteTables, fpsNumerator, fpsDenominator, (ulong)solved.Frames);
                if (spriteSeam.AtOrigin == SpriteSeamPhase.Verdict.Mismatch && spriteSeam.WarmupFrames is null) { ++spriteRejectedCandidates; continue; }
            }
            var (candidate, groupFrames, steps) = GroupPeriods(solved, groups, solve.Used, unresolved, spriteTablesByOwner,
                // 精灵帧表从全局整周期预热之后起判（0 或 L）。
                spriteSeam is null ? 0 : spriteSeam.WarmupFrames,
                fpsNumerator, fpsDenominator, maximumRetimePercent, preference, ceiling, fullLoopLayerIds);
            clockSteps ??= steps;
            var candidatePatches = new List<LoopPatch>();
            double Speed(string id) => candidate.Components.Single(x => x.ComponentId == id).SpeedMultiplier;
            foreach (LoopValuePatch patch in patches)
            {
                double value = Speed(patch.ComponentId);
                // 同 pass 的时间倍率也乘在旋钮项上：旋钮只补差，新值 = (分量倍率 / 时间倍率)^指数
                if (patch.ConstantKey != ShaderPeriodAnalysis.TimeScaleKey)
                    value = Math.Pow(value / (patches.FirstOrDefault(time => time.ConstantKey == ShaderPeriodAnalysis.TimeScaleKey &&
                        (time.OwnerLayerId, time.EffectIndex, time.PassIndex) == (patch.OwnerLayerId, patch.EffectIndex, patch.PassIndex)) is { } time
                        ? Speed(time.ComponentId) : 1), patch.SpeedExponent);
                // 倍率为 1 不写：没调速的 pass 捕获与原作相同
                if (value != 1) candidatePatches.Add(patch with { NewValue = value });
            }
            foreach (RuntimeTrack clip in animation.Where(x => x.CanRetime))
            {
                CommonLoopComponentCycle cycle = candidate.Components.Single(x => x.ComponentId == clip.LockedComponent.Id);
                if (clip.IsVideo)
                {
                    if (!TryVideoRate(clip.LockedComponent.BasePeriod!.ExactSeconds!.Value, cycle.Cycles, candidate.Frames,
                        fpsNumerator, fpsDenominator, out CommonLoopRational videoRate))
                        throw new InvalidDataException("Video rate override exceeds the supported exact rational range.");
                    if (videoRate != new CommonLoopRational(1)) candidatePatches.Add(new LoopVideoRatePatch(clip.LockedComponent.Id, clip.OwnerLayerId, videoRate));
                    continue;
                }
                for (int layerIndex = 0; layerIndex < clip.AnimationLayerIds.Length; ++layerIndex)
                {
                    int animationLayerId = clip.AnimationLayerIds[layerIndex];
                    double oldRate = clip.AuthoredRates[layerIndex];
                    candidatePatches.Add(new LoopValuePatch(clip.LockedComponent.Id, "animation_rate", clip.OwnerLayerId, -1, -1,
                        "rate", 0, animationLayerId, oldRate, oldRate * cycle.SpeedMultiplier));
                }
                // 属性动画轨道改 options.fps：周期 = End/fps，fps 乘倍率与 rate 乘倍率等价。倍率为 1 不写，
                // 锁定求解成功时 plan 与改前逐字节相同。
                if (clip.AuthoredFps is { } fps && cycle.SpeedMultiplier != 1)
                    candidatePatches.Add(new LoopValuePatch(clip.LockedComponent.Id, "animation_fps", clip.OwnerLayerId, -1, -1,
                        "fps", 0, null, fps.Fps, fps.Fps * cycle.SpeedMultiplier) { AnimationPath = fps.AnimationPath });
            }
            candidates.Add(new LoopCandidate(candidate.Frames, candidate.Seconds, candidate.TotalRetimeCostPercent,
                candidate.Components, candidatePatches) { SpriteSeam = spriteSeam, GroupFrames = groupFrames });
        }
        if (spriteRejectedCandidates > 0)
            unresolved.Add(new SpriteSeamUnresolved(spriteRejectedCandidates));
        // 着色器里与线性时间比较的分支在 settle 时刻后固定：同精灵，整周期预热 L 帧后起录（预热只能 0 或 L），要求 L 晚于 settle；
        // 精灵已定在起点 0 闭合的候选不能再预热。候选全放不进就记未收敛
        // 脚本的 settle 同样处理，取两者较晚的一个
        double settleSeconds = Math.Max(shader.Settle?.Seconds ?? 0, scriptSettle?.Seconds ?? 0);
        if (settleSeconds > 0 && candidates.Count > 0)
        {
            candidates = [.. candidates.Where(c => c.Seconds > settleSeconds && (c.SpriteSeam is null || c.SpriteSeam.WarmupFrames == c.Frames))
                .Select(c => c with { ShaderSettleSeconds = settleSeconds })];
            if (candidates.Count == 0 && shader.Settle is { } settle && settle.Seconds >= settleSeconds)
                unresolved.Add(new ShaderLoopUnresolved(new(settle.OwnerLayerId, settle.EffectIndex, settle.PassIndex, settle.Resource,
                    ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, "SPIR-V time signature: a branch on linear time settles by " +
                    settle.Seconds.ToString("R", CultureInfo.InvariantCulture) + " s, later than the one-period warmup of every candidate",
                    "transient_settle_beyond_warmup")));
            else if (candidates.Count == 0)
                unresolved.Add(new ScriptTimeUnresolved(scriptSettle!.Value.Owner, scriptSettle.Value.Binding, false, "transient_settle_beyond_warmup",
                    "the script settles by " + settleSeconds.ToString("R", CultureInfo.InvariantCulture) + " s, later than the one-period warmup of every candidate"));
        }
        long contentStep = ContentStepFrames(shader.Components.Count + shader.Slow.Count + scriptCycles.Length, animation, unresolved.Count, fpsNumerator, fpsDenominator);
        // 没有任何周期分量（求解器与摆动改频都没给出候选），而未解析项全部是满足平稳随机判据、可交叉淡化的粒子：
        // 粒子本身定不出循环长度，从 min(60 秒, 上限) 起，必要时在上限内延长到最长寿命之后；接缝由残差交叉淡化处理。
        // 有周期分量时长度由上面的求解器按这些周期的公共闭合给出，这里不插手；位移类等不可掩盖的未解析项不是粒子，不满足前提。
        JsonObject? particleDefault = candidates.Count == 0 && locked.Length == 0
            ? StationaryParticleDefaultLoop(unresolved, candidates, fpsNumerator, fpsDenominator, ceiling)
            : null;
        // 慢分量不进求解器：各候选另写它们在 P 处的漂移上界（烘焙侧接缝门复核）。除慢分量外没有任何周期分量时，
        // P 取求解器的最短循环时长对齐到帧网格（不超过上限），是确定值。
        if (shader.Slow.Count > 0)
        {
            if (candidates.Count == 0 && locked.Length == 0)
            {
                CommonLoopRational minimum = CommonLoopSolver.DefaultMinimum;
                UInt128 unit = (UInt128)minimum.Denominator * fpsDenominator;
                UInt128 frames = UInt128.Max(1, UInt128.Min(((UInt128)minimum.Numerator * fpsNumerator + unit - 1) / unit,
                    (UInt128)ceiling.Numerator * fpsNumerator / ((UInt128)ceiling.Denominator * fpsDenominator)));
                candidates.Add(new LoopCandidate((ulong)frames, (double)frames * fpsDenominator / fpsNumerator, 0d, [], []) { LoopLengthSource = "slow_components_only" });
            }
            for (int index = 0; index < candidates.Count; ++index) candidates[index] = candidates[index] with { SlowComponents = shader.Slow };
        }
        return new LoopReport(fpsNumerator, fpsDenominator,
            singleVideoRetime ? "single_video_nearest_frame_retime" : retimeClips ? "clip_retime_after_locked_search" : "locked_clip_rates",
            result.Preference, result.RetimeBudgetPercent, result.BudgetRelaxed, result.FixedFrameStep, ceilingSeconds,
            candidates.Count == 0 && result.NoCandidate is CommonLoopNoCandidate reason
                ? new LoopNoCandidateReason(reason, shader.Components.Count, animation.Count, particleCycles.Length,
                    RuntimeTrackReader.CountRuntimeClockUniforms(runtime, bakedLayerIds)) { RetainLiveOwnerLayerIds = noCommonLoopOwners }
                : null,
            candidates, unresolved, sourceStatic, videoControlScope,
            new LoopContentCadence(contentStep, animation.Where(x => x.IsVideo)
                .Select(x => new LoopCadenceClip(x.LockedComponent.Id, x.OwnerLayerId, x.TrackName, x.ClipFrameRate)).ToArray()),
            shader.Components, particleDefault) { GroupClockSteps = clockSteps ?? [] };
    }

    private const string GroupStepPrefix = "group_period:";

    /// <summary>
    /// 各视频组按自己的周期 P_g 录制。分量按 id 里的所有者层归组（所有者不在任何组里的分量算各组共有）。
    /// 组里有平稳粒子以外的未解析项（摆动改频等）：P_g = L，不动。含精灵帧表的组只走下面的整除规则，并要求 float32 帧表
    /// 从录制起点（<paramref name="spriteStart"/>，即全局精灵预热 0 或 L）起在 P_g 上逐帧闭合；为 null 时这些组录 L。只有平稳粒子：按平稳粒子默认循环（60 s 起，
    /// 必要时延长过最长寿命），不必整除 L（没有时钟可对）。有周期分量时，先取 P_g = L / gcd(L, 各分量圈数的最大公约数)：
    /// 它整除 L，任一时刻的画面与整组录 L 帧完全相同；有平稳粒子时取满足默认长度下限的最小这种因子。
    /// 分量的时钟被本组独占（别的组没有同一基准周期的分量）时，再用同一个求解器只解本组分量（上限取上面的 P_g）：
    /// 更短就改用它，调速只落在本组的层上，别的组看不到。与别的组共用时钟、自身周期 L/G 却不整除 L 的组，
    /// 返回 round(L/G) 作为给 L 加的约束（调用方据此重解一次）。含 <paramref name="fullLoopLayerIds"/> 的组录 L（烘焙时自身周期没闭合的退回）。
    /// </summary>
    private static (CommonLoopCandidate Candidate, Dictionary<string, ulong> Frames, List<ulong> ClockSteps) GroupPeriods(
        CommonLoopCandidate candidate, (string Id, HashSet<int> Layers)[] groups, CommonLoopComponent[] used, List<LoopUnresolved> unresolved,
        Dictionary<int, float[][]> spriteTables, ulong? spriteStart, uint fpsNumerator, uint fpsDenominator, double maximumRetimePercent, CommonLoopPreference preference,
        CommonLoopRational ceiling, IReadOnlyCollection<int>? fullLoopLayerIds)
    {
        var frames = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var steps = new List<ulong>();
        ulong loop = candidate.Frames;
        HashSet<int> pinned = [.. fullLoopLayerIds ?? []];
        if (spriteStart is null) pinned.UnionWith(spriteTables.Keys);
        var lifetimes = new Dictionary<int, double>();
        foreach (LoopUnresolved item in unresolved)
        {
            if (item is RuntimeTrackUnresolved { Video: false, Particle: { Stationary: true } particle } track &&
                NonNegative(particle.LifetimeMaxSeconds, out double lifetime))
                lifetimes[track.OwnerLayerId] = Math.Max(lifetime, lifetimes.GetValueOrDefault(track.OwnerLayerId));
            else if (SceneGraph.Int(item.ToJson()["owner_layer_id"]) is int owner) pinned.Add(owner);
            else return (candidate, frames, steps);
        }
        static int? Owner(string id) => id.Split('/', ':') is [_, string text, ..] &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int owner) ? owner : null;
        bool Grouped(int owner) => groups.Any(group => group.Layers.Contains(owner));
        var components = candidate.Components.ToList();
        foreach ((string id, HashSet<int> layers) in groups)
        {
            if (loop <= 1 || layers.Overlaps(pinned)) continue;
            CommonLoopComponentCycle[] mine = [.. candidate.Components.Where(c => Owner(c.ComponentId) is not int owner || !Grouped(owner) || layers.Contains(owner))];
            double[] lives = [.. layers.Where(lifetimes.ContainsKey).Select(owner => lifetimes[owner])];
            ulong floor = lives.Length == 0 ? 1 : (ulong)UInt128.Min(DefaultLoopFrames(lives.Max(), fpsNumerator, fpsDenominator, ceiling), loop);
            ulong period = loop;
            if (mine.Length == 0)
            {
                if (lives.Length > 0 && floor > 0) period = floor;
            }
            else
            {
                ulong cycles = mine.Aggregate(0UL, (gcd, c) => (ulong)GreatestCommonDivisor(gcd, c.Cycles));
                ulong own = loop / (ulong)GreatestCommonDivisor(loop, cycles);
                for (ulong multiple = own; multiple < loop; multiple += own)
                    if (loop % multiple == 0 && multiple >= floor) { period = multiple; break; }
                bool exclusive = mine.All(c => Owner(c.ComponentId) is int owner && layers.Contains(owner) && !c.ComponentId.StartsWith("video/", StringComparison.Ordinal) &&
                    !candidate.Components.Any(other => !mine.Contains(other) && other.OldPeriodSeconds == c.OldPeriodSeconds));
                CommonLoopComponent[] basis = [.. used.Where(u => mine.Any(c => c.ComponentId == u.Id))];
                float[][] sprites = [.. layers.Where(spriteTables.ContainsKey).SelectMany(owner => spriteTables[owner])];
                if (sprites.Any(table => SpriteSeamPhase.Verify(table, fpsNumerator, fpsDenominator, spriteStart ?? 0, period) != SpriteSeamPhase.Verdict.Closed))
                    period = loop;
                if (exclusive && sprites.Length == 0 && basis.Length == mine.Length && period > 1)
                {
                    CommonLoopSearchResult alone = CommonLoopSolver.Suggest(new(fpsNumerator, fpsDenominator, basis,
                        MinimumDuration: new(checked((long)Math.Max(floor, 1) * fpsDenominator), fpsNumerator),
                        MaximumDuration: new(checked((long)period * fpsDenominator), fpsNumerator),
                        MaximumRetimePercent: maximumRetimePercent, Preference: preference));
                    if (alone.Candidates.FirstOrDefault() is { } best && best.Frames < period)
                    {
                        period = best.Frames;
                        foreach (CommonLoopComponentCycle cycle in best.Components)
                            components[components.FindIndex(c => c.ComponentId == cycle.ComponentId)] = cycle;
                    }
                }
                else if (!exclusive && cycles > 1 && loop % cycles != 0 && (ulong)Math.Round((double)loop / cycles) is ulong step and > 0 && step < period)
                    steps.Add(step);
            }
            if (period < loop) frames[id] = period;
        }
        return (candidate with { Components = components, TotalRetimeCostPercent = components.Sum(c => Math.Abs(c.DeltaPercent)) }, frames, steps);
    }

    /// <summary>粒子默认循环长度（秒）：长寿命粒子可在循环上限内延长到寿命之后。</summary>
    internal const long StationaryParticleDefaultLoopSeconds = 60;

    /// <summary>
    /// 未解析项全部是平稳随机粒子（判据结论 stationary、预热与寿命可读）时，从 min(60 秒, 上限) 起选候选，
    /// 必要时延长到最长寿命之后的首个输出帧，但不超过上限。返回 loop.loop_length_default 记录；有其它未解析项时不追加候选。
    /// 交叉淡化替换要求接缝两侧不共享粒子（循环长度 &gt; 粒子最长寿命，见 design-particle-crossfade §2.3），不满足时不加候选、记原因。
    /// </summary>
    internal static JsonObject? StationaryParticleDefaultLoop(List<LoopUnresolved> unresolved, List<LoopCandidate> candidates, uint fpsNumerator,
        uint fpsDenominator, CommonLoopRational ceiling)
    {
        if (unresolved.Count == 0) return null;
        var layerIds = new JsonArray();
        double longestLifetime = 0, longestWarmup = 0;
        foreach (LoopUnresolved item in unresolved)
        {
            if (item is not RuntimeTrackUnresolved { Video: false, Particle: { Stationary: true } particle } track ||
                !NonNegative(particle.WarmupSeconds, out double warmup) || !NonNegative(particle.LifetimeMaxSeconds, out double lifetime))
                return null;
            layerIds.Add(track.OwnerLayerId);
            longestLifetime = Math.Max(longestLifetime, lifetime);
            longestWarmup = Math.Max(longestWarmup, warmup);
        }
        UInt128 frames = DefaultLoopFrames(longestLifetime, fpsNumerator, fpsDenominator, ceiling);
        if (frames == 0) return null;
        double seconds = (double)frames * fpsDenominator / fpsNumerator;
        var record = new JsonObject {
            ["kind"] = "stationary_particle_default", ["default_seconds"] = StationaryParticleDefaultLoopSeconds,
            ["loop_length_maximum_seconds"] = ceiling.ToSeconds(), ["frames"] = (ulong)frames, ["seconds"] = seconds,
            ["particle_layer_ids"] = layerIds, ["max_particle_lifetime_seconds"] = longestLifetime, ["max_warmup_seconds"] = longestWarmup,
            ["basis"] = "No periodic component constrains the capture and every unresolved mechanism is a stationary-random particle system " +
                "whose seam is crossfaded. Start at min(60 s, loop length maximum) on the output frame grid; " +
                "extend past the longest particle lifetime when this fits within the loop length maximum."
        };
        string secondsText = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        if (seconds <= longestLifetime)
        {
            string lifetimeText = longestLifetime.ToString("0.###", CultureInfo.InvariantCulture);
            record["status"] = "particle_lifetime_not_shorter_than_loop";
            record["reason_zh"] = MessageCatalog.Get("loop_length_default.particle_lifetime_too_long", MessageCatalog.Chinese, secondsText, lifetimeText);
            record["reason_en"] = MessageCatalog.Get("loop_length_default.particle_lifetime_too_long", MessageCatalog.English, secondsText, lifetimeText);
            return record;
        }
        record["status"] = "applied";
        record["summary_zh"] = MessageCatalog.Get("summary.particle_default_loop", MessageCatalog.Chinese, secondsText);
        record["summary_en"] = MessageCatalog.Get("summary.particle_default_loop", MessageCatalog.English, secondsText);
        candidates.Add(new LoopCandidate((ulong)frames, seconds, 0d, [], []) { LoopLengthSource = "stationary_particle_default" });
        return record;
    }

    /// <summary>平稳粒子默认循环帧数：min(60 秒, 上限)，必要时延长到最长寿命之后的首帧（不超过上限）；溢出时为 0。</summary>
    private static UInt128 DefaultLoopFrames(double longestLifetime, uint fpsNumerator, uint fpsDenominator, CommonLoopRational ceiling)
    {
        var target = new CommonLoopRational(StationaryParticleDefaultLoopSeconds);
        if ((Int128)ceiling.Numerator * target.Denominator < (Int128)target.Numerator * ceiling.Denominator) target = ceiling;
        UInt128 frames = (UInt128)target.Numerator * fpsNumerator / ((UInt128)target.Denominator * fpsDenominator);
        if (frames == 0 || frames > ulong.MaxValue) return 0;
        UInt128 ceilingFrames = (UInt128)ceiling.Numerator * fpsNumerator / ((UInt128)ceiling.Denominator * fpsDenominator);
        double lifetimeFrames = Math.Floor(longestLifetime * fpsNumerator / fpsDenominator) + 1;
        if (double.IsFinite(lifetimeFrames) && lifetimeFrames <= (double)UInt128.Min(ceilingFrames, ulong.MaxValue))
            frames = UInt128.Max(frames, (UInt128)lifetimeFrames);
        return frames;
    }

    private static bool NonNegative(double? number, out double value)
    {
        value = number ?? 0;
        return number is double finite && double.IsFinite(finite) && finite >= 0;
    }

    private sealed record LoopSolve(CommonLoopComponent[] Locked, CommonLoopComponent[] Used, bool RetimeClips, bool SingleVideoRetime,
        CommonLoopSearchResult Result);

    /// <summary>
    /// 按所有者层贪心并入（分量多的先并，同数按出现顺序），并入后上限内无解的层记下返回。已有未解析项的所有者本来就留实时，不参与。
    /// 没有并不进的层时返回 null；一层都并不进时全部点名，剩下的内容值不值得烘由分配回退重查判定。
    /// </summary>
    private static int[]? NoCommonLoopOwners(ShaderPeriodAnalysisResult shader, IReadOnlyList<RuntimeTrack> animation,
        CommonLoopComponent[] particleCycles, List<LoopUnresolved> unresolved, uint fpsNumerator, uint fpsDenominator,
        double maximumRetimePercent, CommonLoopRational ceiling, CommonLoopPreference preference)
    {
        var live = unresolved.Select(item => SceneGraph.Int(item.ToJson()["owner_layer_id"])).OfType<int>().ToHashSet();
        int[] owners = [.. shader.Components.Select(x => x.OwnerLayerId).Concat(animation.Select(x => x.OwnerLayerId))
            .Where(id => !live.Contains(id)).GroupBy(id => id).OrderByDescending(group => group.Count()).Select(group => group.Key)];
        var kept = new HashSet<int>();
        var dropped = new List<int>();
        foreach (int owner in owners)
        {
            kept.Add(owner);
            if (SolveLoop(shader with { Components = [.. shader.Components.Where(x => kept.Contains(x.OwnerLayerId))] },
                [.. animation.Where(x => kept.Contains(x.OwnerLayerId))], particleCycles, fpsNumerator, fpsDenominator,
                maximumRetimePercent, ceiling, preference).Result.Candidates.Count > 0) continue;
            kept.Remove(owner);
            dropped.Add(owner);
        }
        return dropped.Count > 0 ? [.. dropped] : null;
    }

    /// <summary>
    /// 一次完整的求解：先全部锁定求解；无解时有可调速轨道就放开轨道调速再解，只有一段视频时走单视频调速。
    /// <paramref name="particleCycles"/> 是锁定的粒子替换周期，两轮都保持锁定（整数帧，调 rate 覆盖只能跳到相邻整数帧，不适用连续调速）。
    /// </summary>
    private static LoopSolve SolveLoop(ShaderPeriodAnalysisResult shader, IReadOnlyList<RuntimeTrack> animation,
        CommonLoopComponent[] particleCycles, uint fpsNumerator, uint fpsDenominator, double maximumRetimePercent, CommonLoopRational ceiling,
        CommonLoopPreference preference)
    {
        var locked = shader.Components.Select(x => x.Component).Concat(animation.Select(x => x.LockedComponent)).Concat(particleCycles).ToArray();
        // 一个时间分量都没有：这不是"求解失败"，是"根本没有东西可解"，带着原因返回，别让上层只看到一个空候选表。
        CommonLoopSearchResult lockedResult = locked.Length == 0
            ? new([], [], true, null, NoCandidate: new(CommonLoopNoCandidateKind.NoTemporalMechanism, ceiling.ToSeconds()))
            : CommonLoopSolver.Suggest(new(fpsNumerator, fpsDenominator, locked,
                MinimumDuration: MinimumFrameDuration(fpsNumerator, fpsDenominator), MaximumDuration: ceiling,
                MaximumRetimePercent: maximumRetimePercent, Preference: preference));
        bool retimeClips = lockedResult.Candidates.Count == 0 && animation.Any(x => x.CanRetime);
        bool singleVideoRetime = lockedResult.Candidates.Count == 0 && locked.Length == 1 && animation.Count == 1 && animation[0].IsVideo;
        var components = retimeClips
            ? shader.Components.Select(x => x.Component).Concat(animation.Select(x => x.CanRetime ? x.RetimableComponent : x.LockedComponent))
                .Concat(particleCycles).ToArray()
            : locked;
        CommonLoopSearchResult result = singleVideoRetime
            ? SuggestSingleVideoRetime(fpsNumerator, fpsDenominator, animation[0], maximumRetimePercent, ceiling, preference)
            : retimeClips
                ? CommonLoopSolver.Suggest(new(fpsNumerator, fpsDenominator, components,
                    MinimumDuration: MinimumFrameDuration(fpsNumerator, fpsDenominator), MaximumDuration: ceiling,
                    MaximumRetimePercent: maximumRetimePercent, Preference: preference))
                : lockedResult;
        return new(locked, singleVideoRetime ? [animation[0].RetimableComponent] : components, retimeClips, singleVideoRetime, result);
    }

    /// <summary>锁定周期的粒子层（按层 id 排序）→ 精确有理周期的锁定分量。</summary>
    private static CommonLoopComponent[] ParticleCycleComponents(IReadOnlyDictionary<int, ParticleStationarity.Result> verdicts) =>
        [.. verdicts.Where(pair => pair.Value.Stationary && pair.Value.Lock is not null).OrderBy(pair => pair.Key).Select(pair =>
        {
            ParticleStationarity.CyclostationaryLock cycle = pair.Value.Lock!;
            CommonLoopRational exact = cycle.PeriodSeconds;
            return new CommonLoopComponent(cycle.ComponentId, new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact));
        })];

    /// <summary>
    /// 被烘图层上每段脚本的时间签名（<see cref="ScriptTime"/>）：周期作分量交给求解器——按 engine.runtime 的周期可调速（捕获时改写
    /// 这段脚本读到的 runtime），跨帧状态的周期是精确帧数；不能与未收敛各记一条。运行时观测到读时钟、源码却没找到的脚本记未收敛。
    /// settle 取最晚的一段。
    /// </summary>
    private static CommonLoopComponent[] ScriptComponents(JsonObject scene, ProjectSource source, string? assetsDirectory, JsonObject runtime,
        IReadOnlyCollection<int> bakedLayerIds, uint fpsNumerator, uint fpsDenominator, CommonLoopRational ceiling, List<LoopUnresolved> unresolved,
        out LoopValuePatch[] patches, out (int Owner, string Binding, double Seconds)? settle)
    {
        UInt128 ceilingFrames = (UInt128)ceiling.Numerator * fpsNumerator / ((UInt128)ceiling.Denominator * fpsDenominator);
        var components = new List<CommonLoopComponent>();
        var retimes = new List<LoopValuePatch>();
        var seen = new HashSet<(int, string)>();
        settle = null;
        foreach (ScriptTime.Binding binding in ScriptTime.Bindings(scene, source, assetsDirectory, bakedLayerIds))
        {
            seen.Add((binding.OwnerLayerId, binding.Name));
            ScriptTime.Verdict verdict = ScriptTime.Analyze(binding, fpsNumerator, fpsDenominator, (ulong)UInt128.Min(ceilingFrames, 1_000_000));
            if (verdict.Settle > (settle?.Seconds ?? 0)) settle = (binding.OwnerLayerId, binding.Name, verdict.Settle);
            string id = $"script/{binding.OwnerLayerId}/{binding.Pointer ?? binding.Name}";
            if (verdict.PeriodFrames is ulong frames)
            {
                var exact = new CommonLoopRational(checked((long)frames * fpsDenominator), fpsNumerator);
                components.Add(new(id, new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact)));
            }
            else if (verdict.PeriodSeconds is double seconds)
            {
                components.Add(new(id, new CommonLoopPeriod(seconds, CommonLoopPeriodEvidence.Analytic), AllowRetime: verdict.Retimable));
                if (verdict.Retimable) retimes.Add(new(id, "script_speed", binding.OwnerLayerId, -1, -1, binding.Pointer!, 0, null, 1, 1));
            }
            else if (verdict.Outcome is ScriptTime.Outcome.Cannot or ScriptTime.Outcome.Unconverged)
                unresolved.Add(new ScriptTimeUnresolved(binding.OwnerLayerId, binding.Name, verdict.Outcome == ScriptTime.Outcome.Cannot, verdict.Code, verdict.Detail));
        }
        foreach (var dependency in (runtime["runtime_dependencies"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["operation"]?.GetValue<string>() == "time" && item["initialization"]?.GetValue<bool>() != true &&
                SceneGraph.Int(item["owner"]) is int owner && bakedLayerIds.Contains(owner) && !seen.Contains((owner, item["binding"]?.GetValue<string>() ?? "")))
            .DistinctBy(item => (SceneGraph.Int(item["owner"]), item["binding"]?.GetValue<string>())))
            unresolved.Add(new ScriptTimeUnresolved(SceneGraph.Int(dependency["owner"])!.Value, dependency["binding"]?.GetValue<string>() ?? "", false,
                "script_source_not_found", "a script reads " + dependency["property"] + " but its source is not in the scene or the layer's model materials"));
        patches = [.. retimes];
        return [.. components];
    }

    private static CommonLoopSearchResult SuggestSingleVideoRetime(uint fpsNumerator, uint fpsDenominator,
        RuntimeTrack video, double maximumRetimePercent, CommonLoopRational ceiling, CommonLoopPreference preference = CommonLoopPreference.Balanced)
    {
        CommonLoopRational period = video.LockedComponent.BasePeriod!.ExactSeconds!.Value;
        UInt128 numerator = (UInt128)period.Numerator * fpsNumerator;
        UInt128 denominator = (UInt128)period.Denominator * fpsDenominator;
        UInt128 nearest = (numerator * 2 + denominator) / (denominator * 2);
        if (nearest == 0 || nearest > ulong.MaxValue)
            return new([], [], true, null, preference, maximumRetimePercent, false,
                new(CommonLoopNoCandidateKind.NoExactVideoRetimeFrame, ceiling.ToSeconds()));
        var request = new CommonLoopSolveRequest(fpsNumerator, fpsDenominator, [video.RetimableComponent],
            MinimumDuration: MinimumFrameDuration(fpsNumerator, fpsDenominator), MaximumDuration: ceiling,
            MaximumRetimePercent: maximumRetimePercent);
        CommonLoopEvaluation evaluation = CommonLoopSolver.EvaluateAtFrames(request, (ulong)nearest);
        return evaluation.Candidate is null
            ? new([], evaluation.Constraints, true, null, preference, maximumRetimePercent, false)
            : new([evaluation.Candidate], [], true, null, preference, maximumRetimePercent, false);
    }

    private static bool TryVideoRate(CommonLoopRational period, ulong cycles, ulong frames, uint fpsNumerator,
        uint fpsDenominator, out CommonLoopRational rate)
    {
        rate = default;
        if (cycles == 0 || frames == 0) return false;
        UInt128 numerator = (UInt128)period.Numerator * cycles * fpsNumerator;
        UInt128 denominator = (UInt128)period.Denominator * frames * fpsDenominator;
        UInt128 divisor = GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor; denominator /= divisor;
        if (numerator == 0 || denominator == 0 || numerator > long.MaxValue || denominator > long.MaxValue) return false;
        rate = new CommonLoopRational((long)numerator, (long)denominator);
        return true;
    }

    private static CommonLoopRational MinimumFrameDuration(uint fpsNumerator, uint fpsDenominator) =>
        new(fpsDenominator, fpsNumerator);

    private static UInt128 GreatestCommonDivisor(UInt128 left, UInt128 right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }

    /// <summary>
    /// 一个内容帧占多少个输出帧。只有当捕获里全部时间机制都是定速片源、而且每段片源的原生帧率都整除输出帧率时，
    /// 相邻输出帧才确实是同一张内容帧的复制；任何着色器时钟、非视频动画或未解析项都让它退回 1。
    /// </summary>
    private static long ContentStepFrames(int shaderComponentCount, List<RuntimeTrack> animation, int unresolvedCount,
        uint fpsNumerator, uint fpsDenominator)
    {
        if (shaderComponentCount != 0 || unresolvedCount != 0 || animation.Count == 0) return 1;
        UInt128 step = 0;
        foreach (RuntimeTrack clip in animation)
        {
            if (!clip.IsVideo || clip.ClipFrameRate is not CommonLoopRational rate) return 1;
            // 输出帧率 / 片源帧率，必须是正整数
            UInt128 numerator = (UInt128)fpsNumerator * (ulong)rate.Denominator;
            UInt128 denominator = (UInt128)fpsDenominator * (ulong)rate.Numerator;
            if (denominator == 0 || numerator % denominator != 0) return 1;
            UInt128 clipStep = numerator / denominator;
            if (clipStep == 0 || clipStep > long.MaxValue) return 1;
            step = step == 0 ? clipStep : GreatestCommonDivisor(step, clipStep);
        }
        return step == 0 ? 1 : (long)step;
    }
}
