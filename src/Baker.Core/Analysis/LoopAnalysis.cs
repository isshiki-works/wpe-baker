using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 循环分析：把源码证明的着色器周期与运行时轨道周期合成捕获用的循环候选。候选与未解析项全程是类型化对象，
/// 由 <see cref="LoopReport.ToJson"/> 渲染一次写进 plan.loop。静止证明、运行时轨道、摆动改频分别在
/// <see cref="StaticProof"/>、<see cref="RuntimeTrackReader"/>、<see cref="SwayRetimeApplier"/>。
/// </summary>
internal static class LoopAnalysis
{
    internal static LoopReport Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory, JsonObject runtime,
        IReadOnlyCollection<int> bakedLayerIds, uint fpsNumerator, uint fpsDenominator, double maximumRetimePercent = 2,
        CommonLoopPreference preference = CommonLoopPreference.Balanced, SwayRetimeOptions? swayRetime = null,
        double? loopLengthMaximumSeconds = null, JsonArray? videoGroups = null,
        IReadOnlyCollection<ulong>? groupClockSteps = null, IReadOnlyCollection<int>? fullLoopLayerIds = null)
    {
        if (fpsNumerator == 0 || fpsDenominator == 0 || !double.IsFinite(maximumRetimePercent) ||
            maximumRetimePercent < 0 || maximumRetimePercent > RetimeProfile.MaximumCommonRetimePercent)
            throw new ArgumentException("FPS must be positive and retiming must be between zero and ten percent.");
        // 循环时长上限 = --loop-max-seconds（或档位兜底）这一个值：所有周期分量共用，摆动改频的 Lmax 不许与它不一致。
        double ceilingSeconds = loopLengthMaximumSeconds ?? swayRetime?.LoopLengthMaximumSeconds ?? CommonLoopSolver.DefaultMaximumSeconds;
        if (swayRetime is not null && swayRetime.LoopLengthMaximumSeconds != ceilingSeconds)
            throw new ArgumentException("The sway retime loop length maximum must equal the solver loop length ceiling.");
        CommonLoopRational ceiling = CommonLoopSolver.Ceiling(ceilingSeconds);
        ceilingSeconds = ceiling.ToSeconds();
        var shader = ShaderPeriodAnalysis.Analyze(scene, source, assetsDirectory, runtime, bakedLayerIds);
        List<LoopUnresolved> unresolved = [.. shader.Unresolved.Select(item => (LoopUnresolved)new ShaderLoopUnresolved(item))];
        RuntimeTrackReader.AddMaterialClockUnresolved(runtime, bakedLayerIds, unresolved, shader.RuledMaterials);
        // A model/shader period does not also prove the state advanced by an authored script.
        // Keep the observed owner so allocation fallback can retain that subtree live.
        RuntimeTrackReader.AddScriptTimeUnresolved(runtime, bakedLayerIds, unresolved);
        // 着色器调速：给该 (层, shader) 的每个作者 pass 挂 g_Time 倍率，旧值 1（场景里没有这个键）。
        var patches = new List<LoopValuePatch>();
        foreach (var item in shader.Components)
            foreach (var (effect, pass) in item.Passes)
                patches.Add(new(item.Component.Id, "shader_speed", item.OwnerLayerId, effect, pass, ShaderPeriodAnalysis.TimeScaleKey, 0, null, 1, 1));

        var videoControlScope = VideoControlScope.Resolve(scene, runtime);
        var animation = RuntimeTrackReader.Read(scene, source, assetsDirectory, runtime, bakedLayerIds, unresolved, videoControlScope,
            new ParticleStationarity.FrameClock(fpsNumerator, fpsDenominator, ceilingSeconds), out var particleVerdicts);
        // 封顶 + 确定寿命的粒子层：替换周期（整数帧）作为锁定分量交给求解器，与着色器、轨道的锁定分量同等对待。
        CommonLoopComponent[] particleCycles = ParticleCycleComponents(particleVerdicts);
        CommonLoopComponent[] scriptCycles = ScriptFrameStepComponents(scene, bakedLayerIds, fpsNumerator, fpsDenominator, ceiling, unresolved);
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
        if (shader.Components.Count == 0 && animation.Count == 0 && scriptCycles.Length == 0 && unresolved.Count == 0 && bakedLayerIds.Count > 0)
        {
            // 没有任何已建模的时间机制时只剩两种结局：证明这一帧是静态的，或者说清楚为什么证明不了。
            // 旧实现在证明不了时一个字都不写，plan 里就出现零候选零理由的 unavailable（沉默拒绝）。
            SourceStaticUnresolved? obstacle = StaticProof.Obstacle(scene, source, assetsDirectory, runtime, bakedLayerIds);
            sourceStatic = obstacle is null;
            if (obstacle is not null) unresolved.Add(obstacle);
        }
        if (sourceStatic)
            candidates.Add(new LoopCandidate(1UL, (double)fpsDenominator / fpsNumerator, 0d, [], []));
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
                // 精灵帧表从全局整周期预热之后起判（0 或 L）；摆动改频可能把 L 与预热改成 kL 时不缩短精灵组。
                spriteSeam is null ? 0 : spriteSeam.WarmupFrames is ulong warmup && (warmup == 0 || swayRetime is null || shader.Unresolved.Count == 0) ? warmup : null,
                fpsNumerator, fpsDenominator, maximumRetimePercent, preference, ceiling, fullLoopLayerIds);
            clockSteps ??= steps;
            var candidatePatches = new List<LoopPatch>();
            foreach (LoopValuePatch patch in patches)
            {
                CommonLoopComponentCycle cycle = candidate.Components.Single(x => x.ComponentId == patch.ComponentId);
                // 倍率为 1 不写：没调速的 pass 捕获与原作相同
                if (cycle.SpeedMultiplier != 1) candidatePatches.Add(patch with { NewValue = cycle.SpeedMultiplier });
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
        long contentStep = ContentStepFrames(shader.Components.Count + scriptCycles.Length, animation, unresolved.Count, fpsNumerator, fpsDenominator);
        // 没有任何周期分量（求解器与摆动改频都没给出候选），而未解析项全部是满足平稳随机判据、可交叉淡化的粒子：
        // 粒子本身定不出循环长度，从 min(60 秒, 上限) 起，必要时在上限内延长到最长寿命之后；接缝由残差交叉淡化处理。
        // 有周期分量时长度由上面的求解器按这些周期的公共闭合给出，这里不插手；位移类等不可掩盖的未解析项不是粒子，不满足前提。
        JsonObject? particleDefault = candidates.Count == 0 && locked.Length == 0
            ? StationaryParticleDefaultLoop(unresolved, candidates, fpsNumerator, fpsDenominator, ceiling)
            : null;
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
            shader.Components, null, particleDefault) { GroupClockSteps = clockSteps ?? [] };
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
    /// 没有并不进的层、或一层都并不进时返回 null。
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
        return dropped.Count > 0 && kept.Count > 0 ? [.. dropped] : null;
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
    /// 逐帧步进脚本（<see cref="ScriptFrameStep"/>）的整数帧周期作锁定分量交给求解器。这类脚本合起来的最小公倍数超过
    /// 循环上限时没有能同时闭合它们的循环：不给分量，每条记一个无界未解析项，由分配回退把这些层留实时。
    /// </summary>
    private static CommonLoopComponent[] ScriptFrameStepComponents(JsonObject scene, IReadOnlyCollection<int> bakedLayerIds,
        uint fpsNumerator, uint fpsDenominator, CommonLoopRational ceiling, List<LoopUnresolved> unresolved)
    {
        UInt128 ceilingFrames = (UInt128)ceiling.Numerator * fpsNumerator / ((UInt128)ceiling.Denominator * fpsDenominator);
        var scripts = ScriptFrameStep.Find(scene, bakedLayerIds, (ulong)UInt128.Min(ceilingFrames, ulong.MaxValue));
        if (scripts.Count == 0) return [];
        System.Numerics.BigInteger? joint = ScriptFrameStep.JointFrames(scripts);
        if (joint is null || joint > (System.Numerics.BigInteger)ceilingFrames)
        {
            string text = joint?.ToString(CultureInfo.InvariantCulture) ?? $">{ceilingFrames}";
            unresolved.AddRange(scripts.Select(item => new ScriptFrameStepUnresolved(item.OwnerLayerId, item.Binding, item.PeriodFrames, scripts.Count, text)));
            return [];
        }
        return [.. scripts.Select(item =>
        {
            var exact = new CommonLoopRational(checked((long)item.PeriodFrames!.Value * fpsDenominator), fpsNumerator);
            return new CommonLoopComponent($"script_frame_step:{item.OwnerLayerId}:{item.Binding}",
                new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact));
        })];
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
