using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>Combines source-proven shader periods and traced runtime animation periods into capture-only loop proposals.</summary>
public static class HybridLoopService
{
    public static JsonObject Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory, JsonObject runtime,
        IReadOnlyCollection<int> bakedLayerIds, uint fpsNumerator, uint fpsDenominator, double maximumRetimePercent = 2,
        CommonLoopPreference preference = CommonLoopPreference.Balanced, SwayRetimeOptions? swayRetime = null,
        double? loopLengthMaximumSeconds = null, EmbeddedVideoLoopLimit? loopLengthLimit = null)
    {
        if (fpsNumerator == 0 || fpsDenominator == 0 || !double.IsFinite(maximumRetimePercent) ||
            maximumRetimePercent < 0 || maximumRetimePercent > RetimeProfile.MaximumBudgetPercent)
            throw new ArgumentException("FPS must be positive and retiming must be between zero and five percent.");
        // 循环时长上限 = --loop-max-seconds 再按内嵌视频 2 GiB 收紧后的那一个值：所有周期分量共用。
        // 摆动改频的 Lmax 与收紧记录来自同一个请求字段，三者不许不一致。
        double ceilingSeconds = loopLengthMaximumSeconds ?? swayRetime?.LoopLengthMaximumSeconds ?? CommonLoopSolver.DefaultMaximumSeconds;
        if (swayRetime is not null && swayRetime.LoopLengthMaximumSeconds != ceilingSeconds)
            throw new ArgumentException("The sway retime loop length maximum must equal the solver loop length ceiling.");
        if (loopLengthLimit is not null && loopLengthLimit.EffectiveSeconds != ceilingSeconds)
            throw new ArgumentException("The embedded video loop length limit must equal the solver loop length ceiling.");
        CommonLoopRational ceiling = CommonLoopSolver.Ceiling(ceilingSeconds);
        ceilingSeconds = ceiling.ToSeconds();
        var shader = ShaderPeriodAnalysis.Analyze(scene, source, assetsDirectory, bakedLayerIds, ceilingSeconds);
        var unresolved = new JsonArray(shader.Unresolved.Select(UnresolvedJson).ToArray());
        AddRuntimeMaterialTemporalUnresolved(runtime, bakedLayerIds, unresolved, shader.RuledMaterials);
        // A model/shader period does not also prove the state advanced by an authored script.
        // Keep the observed owner so allocation fallback can retain that subtree live.
        foreach (var dependency in (runtime["runtime_dependencies"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["operation"]?.GetValue<string>() == "time" &&
                item["initialization"]?.GetValue<bool>() != true &&
                HybridScenePlanner.Int(item["owner"]) is int owner && bakedLayerIds.Contains(owner))
            .DistinctBy(item => (HybridScenePlanner.Int(item["owner"]), item["binding"]?.GetValue<string>())))
            unresolved.Add(new Message("unresolved.script_time").Write(new JsonObject {
                ["kind"] = "script_time", ["owner_layer_id"] = dependency["owner"]!.DeepClone(),
                ["binding"] = dependency["binding"]?.DeepClone(), ["clock"] = dependency["property"]?.DeepClone() }, "detail"));
        var patches = new List<Patch>();
        foreach (var item in shader.Components)
        {
            // A fixed-period mechanism whose rate is a shader literal names no constant: it stays a
            // locked loop constraint and there is nothing in the capture scene to rewrite for it.
            if (item.Patch.ConstantKey.Length == 0) continue;
            patches.Add(new(item.Component.Id, "shader_speed", item.Patch.OwnerLayerId, item.Patch.EffectIndex, item.Patch.PassIndex,
                item.Patch.ConstantKey, item.Patch.ValueIndex, null, item.Patch.OldValue, BaseShaderValue(item), item.Patch.SpeedExponent));
            if (item.Patch.CompanionConstantKey is string companionKey && item.Patch.CompanionOldValue is double companionOldValue)
                patches.Add(new(item.Component.Id, "shader_phase", item.Patch.OwnerLayerId, item.Patch.EffectIndex, item.Patch.PassIndex,
                    companionKey, item.Patch.CompanionValueIndex, null, companionOldValue, companionOldValue, item.Patch.CompanionSpeedExponent));
        }

        var videoControlScope = VideoControlScope.Resolve(scene, runtime);
        var animation = ReadAnimations(scene, source, assetsDirectory, runtime, bakedLayerIds, unresolved, videoControlScope,
            new ParticleStationarity.FrameClock(fpsNumerator, fpsDenominator, ceilingSeconds), out var particleVerdicts);
        // 封顶 + 确定寿命的粒子层：替换周期（整数帧）作为锁定分量交给求解器，与着色器、轨道的锁定分量同等对待。
        CommonLoopComponent[] particleCycles = ParticleCycleComponents(particleVerdicts);
        LoopSolve solve = SolveLoop(shader, animation, particleCycles, fpsNumerator, fpsDenominator, maximumRetimePercent, ceiling, preference);
        if (particleCycles.Length > 0 && solve.Result.Candidates.Count == 0)
        {
            // 锁定周期与其余分量在循环上限内没有公共循环，而不锁这些层时有：这些层退回拒绝（留实时），与判据收紧时的结论一致。
            LoopSolve unlocked = SolveLoop(shader, animation, [], fpsNumerator, fpsDenominator, maximumRetimePercent, ceiling, preference);
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
                RewriteParticleItems(unresolved, particleVerdicts);
                solve = unlocked;
                particleCycles = [];
            }
        }
        var locked = solve.Locked;
        bool retimeClips = solve.RetimeClips, singleVideoRetime = solve.SingleVideoRetime;
        CommonLoopSearchResult result = solve.Result;
        if (!result.SearchComplete)
            unresolved.Add(new JsonObject { ["kind"] = "search_budget", ["detail"] = result.UnresolvedConstraints.Single().Detail });
        foreach (var constraint in result.UnresolvedConstraints.Where(x => x.Kind != CommonLoopConstraintKind.SearchBudgetExceeded))
            unresolved.Add(new JsonObject { ["kind"] = "solver", ["component"] = constraint.ComponentId, ["detail"] = constraint.Detail });

        var candidates = new JsonArray();
        bool sourceStatic = false;
        // 被烘集合为空时不走静态证明：没有要捕获的画面时，调用方已用 blocker
        // 说明依赖闭包后没剩下可烘组，这里再追一条 source_static 只是同一件事的重复描述。
        if (shader.Components.Count == 0 && animation.Count == 0 && unresolved.Count == 0 && bakedLayerIds.Count > 0)
        {
            // 没有任何已建模的时间机制时只剩两种结局：证明这一帧是静态的，或者说清楚为什么证明不了。
            // 旧实现在证明不了时一个字都不写，plan 里就出现零候选零理由的 unavailable（沉默拒绝）。
            JsonObject? obstacle = SourceStaticObstacle(scene, source, assetsDirectory, runtime, bakedLayerIds);
            sourceStatic = obstacle is null;
            if (obstacle is not null) unresolved.Add(obstacle);
        }
        if (sourceStatic)
            candidates.Add(new JsonObject { ["frames"] = 1UL, ["seconds"] = (double)fpsDenominator / fpsNumerator,
                ["total_retime_cost_percent"] = 0d, ["components"] = new JsonArray(), ["patches"] = new JsonArray() });
        // 精灵分量按渲染器实际使用的 float32 帧表逐帧判定接缝（见 SpriteSeamPhase）。起点 0 闭合则候选不变；
        // 起点 0 不闭合但整周期预热后闭合，候选带上预热帧数；两者都不闭合，候选移除并写明理由。
        float[][] spriteTables = animation.Where(x => x.SpriteFrameTimes is not null).Select(x => x.SpriteFrameTimes!).ToArray();
        int spriteRejectedCandidates = 0;
        foreach (CommonLoopCandidate candidate in result.Candidates)
        {
            JsonObject? spriteSeam = null;
            if (spriteTables.Length > 0)
            {
                var selection = SpriteSeamPhase.Select(spriteTables, fpsNumerator, fpsDenominator, (ulong)candidate.Frames);
                if (selection.AtOrigin == SpriteSeamPhase.Verdict.Mismatch && selection.WarmupFrames is null) { ++spriteRejectedCandidates; continue; }
                if (selection.WarmupFrames is ulong warmup && warmup > 0)
                    spriteSeam = new JsonObject { ["source_period_warmup_frames"] = warmup, ["sprite_seam_phase"] = new JsonObject {
                        ["origin"] = selection.AtOrigin.ToString().ToLowerInvariant(),
                        ["after_one_period"] = selection.AfterOnePeriod?.ToString().ToLowerInvariant(),
                        ["basis"] = "float32 sprite frame table; frame 0 sits on the sprite frame-0 start boundary" } };
                else if (selection.AtOrigin == SpriteSeamPhase.Verdict.Undetermined)
                    spriteSeam = new JsonObject { ["sprite_seam_phase"] = new JsonObject { ["origin"] = "undetermined",
                        ["basis"] = "a sample lies within the renderer's double accumulation error of a sprite boundary" } };
            }
            var candidatePatches = new JsonArray();
            foreach (Patch patch in patches)
            {
                CommonLoopComponentCycle cycle = candidate.Components.Single(x => x.ComponentId == patch.ComponentId);
                candidatePatches.Add(PatchJson(patch with { NewValue = ShaderPatchValue(patch.NewValue, cycle.SpeedMultiplier, patch.SpeedExponent) }));
            }
            foreach (AnimationInfo clip in animation.Where(x => x.CanRetime))
            {
                CommonLoopComponentCycle cycle = candidate.Components.Single(x => x.ComponentId == clip.LockedComponent.Id);
                if (clip.IsVideo)
                {
                    if (!TryVideoRate(clip.LockedComponent.BasePeriod!.ExactSeconds!.Value, cycle.Cycles, candidate.Frames,
                        fpsNumerator, fpsDenominator, out CommonLoopRational videoRate))
                        throw new InvalidDataException("Video rate override exceeds the supported exact rational range.");
                    if (videoRate != new CommonLoopRational(1)) candidatePatches.Add(VideoPatchJson(clip, videoRate));
                    continue;
                }
                for (int layerIndex = 0; layerIndex < clip.AnimationLayerIds.Length; ++layerIndex)
                {
                    int animationLayerId = clip.AnimationLayerIds[layerIndex];
                    double oldRate = clip.AuthoredRates[layerIndex];
                    candidatePatches.Add(PatchJson(new(clip.LockedComponent.Id, "animation_rate", clip.OwnerLayerId, -1, -1,
                        "rate", 0, animationLayerId, oldRate, oldRate * cycle.SpeedMultiplier)));
                }
            }
            var candidateJson = new JsonObject {
                ["frames"] = candidate.Frames, ["seconds"] = candidate.Seconds,
                ["total_retime_cost_percent"] = candidate.TotalRetimeCostPercent,
                ["components"] = new JsonArray(candidate.Components.Select(CycleJson).ToArray()), ["patches"] = candidatePatches
            };
            if (spriteSeam is not null)
                foreach (var (name, value) in spriteSeam.ToArray()) { spriteSeam.Remove(name); candidateJson[name] = value; }
            candidates.Add(candidateJson);
        }
        if (spriteRejectedCandidates > 0)
            unresolved.Add(new Message("unresolved.sprite_float32_seam_mismatch").Write(new JsonObject { ["kind"] = "sprite_float32_seam", ["rejected_candidate_count"] = spriteRejectedCandidates }, "detail"));
        long contentStep = ContentStepFrames(shader.Components.Count, animation, unresolved.Count, fpsNumerator, fpsDenominator);
        // 摆动改频（默认关）：在其余分量解出的每个候选 P 上找 L = kP，让摆动层逐项精确闭合；成立时摆动分量
        // 从未解析项里移出，候选帧数改成 L。开关关闭时这里什么都不做，plan 与旧版逐字节相同。
        JsonObject? swayRecord = swayRetime is null ? null : ApplySwayRetime(shader.Unresolved, unresolved, candidates,
            locked.Length == 0, fpsNumerator, fpsDenominator, swayRetime, spriteTables);
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
                    CountRuntimeClockUniforms(runtime, bakedLayerIds))
                : null,
            candidates, unresolved, sourceStatic, videoControlScope,
            new LoopContentCadence(contentStep, animation.Where(x => x.IsVideo)
                .Select(x => new LoopCadenceClip(x.LockedComponent.Id, x.OwnerLayerId, x.TrackName, x.ClipFrameRate)).ToArray()),
            shader.Components, swayRecord, particleDefault, loopLengthLimit).ToJson();
    }

    /// <summary>粒子默认循环长度（秒）：长寿命粒子可在循环上限内延长到寿命之后。</summary>
    public const long StationaryParticleDefaultLoopSeconds = 60;

    /// <summary>
    /// 未解析项全部是平稳随机粒子（particle_stationarity.stationary 为真、预热与寿命可读）时，从 min(60 秒, 上限) 起选候选，
    /// 必要时延长到最长寿命之后的首个输出帧，但不超过上限。返回 loop.loop_length_default 记录；有其它未解析项时不追加候选。
    /// 交叉淡化替换要求接缝两侧不共享粒子（循环长度 &gt; 粒子最长寿命，见 design-particle-crossfade §2.3），不满足时不加候选、记原因。
    /// </summary>
    private static JsonObject? StationaryParticleDefaultLoop(JsonArray unresolved, JsonArray candidates, uint fpsNumerator,
        uint fpsDenominator, CommonLoopRational ceiling)
    {
        if (unresolved.Count == 0) return null;
        var layerIds = new JsonArray();
        double longestLifetime = 0, longestWarmup = 0;
        foreach (JsonNode? node in unresolved)
        {
            if (node is not JsonObject item || item["kind"]?.GetValue<string>() != "runtime_animation" ||
                item["owner_layer_id"] is not JsonValue owner || !owner.TryGetValue(out int ownerId) ||
                item["particle_stationarity"] is not JsonObject stationarity ||
                stationarity["stationary"] is not JsonValue flag || !flag.TryGetValue(out bool stationary) || !stationary ||
                !TryNonNegativeNumber(stationarity["warmup_seconds"], out double warmup) ||
                !TryNonNegativeNumber(stationarity["lifetime_max_seconds"], out double lifetime))
                return null;
            layerIds.Add(ownerId);
            longestLifetime = Math.Max(longestLifetime, lifetime);
            longestWarmup = Math.Max(longestWarmup, warmup);
        }
        var target = new CommonLoopRational(StationaryParticleDefaultLoopSeconds);
        if ((Int128)ceiling.Numerator * target.Denominator < (Int128)target.Numerator * ceiling.Denominator) target = ceiling;
        UInt128 frames = (UInt128)target.Numerator * fpsNumerator / ((UInt128)target.Denominator * fpsDenominator);
        if (frames == 0 || frames > ulong.MaxValue) return null;
        UInt128 ceilingFrames = (UInt128)ceiling.Numerator * fpsNumerator / ((UInt128)ceiling.Denominator * fpsDenominator);
        double lifetimeFrames = Math.Floor(longestLifetime * fpsNumerator / fpsDenominator) + 1;
        if (double.IsFinite(lifetimeFrames) && lifetimeFrames <= (double)UInt128.Min(ceilingFrames, ulong.MaxValue))
            frames = UInt128.Max(frames, (UInt128)lifetimeFrames);
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
        candidates.Add(new JsonObject { ["frames"] = (ulong)frames, ["seconds"] = seconds, ["total_retime_cost_percent"] = 0d,
            ["components"] = new JsonArray(), ["patches"] = new JsonArray(),
            ["loop_length_source"] = "stationary_particle_default" });
        return record;
    }

    private static bool TryNonNegativeNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue json) return false;
        if (json.TryGetValue(out double number)) value = number;
        else if (json.TryGetValue(out int integer)) value = integer;
        else if (json.TryGetValue(out long wide)) value = wide;
        else if (json.TryGetValue(out decimal exact)) value = (double)exact;
        else return false;
        return double.IsFinite(value) && value >= 0;
    }

    private sealed record LoopSolve(CommonLoopComponent[] Locked, bool RetimeClips, bool SingleVideoRetime, CommonLoopSearchResult Result);

    /// <summary>
    /// 一次完整的求解：先全部锁定求解；无解时有可调速轨道就放开轨道调速再解，只有一段视频时走单视频调速。
    /// <paramref name="particleCycles"/> 是锁定的粒子替换周期，两轮都保持锁定（整数帧，调 rate 覆盖只能跳到相邻整数帧，不适用连续调速）。
    /// </summary>
    private static LoopSolve SolveLoop(ShaderPeriodAnalysisResult shader, IReadOnlyList<AnimationInfo> animation,
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
        return new(locked, retimeClips, singleVideoRetime, result);
    }

    /// <summary>锁定周期的粒子层（按层 id 排序）→ 精确有理周期的锁定分量。</summary>
    private static CommonLoopComponent[] ParticleCycleComponents(IReadOnlyDictionary<int, ParticleStationarity.Result> verdicts) =>
        [.. verdicts.Where(pair => pair.Value.Stationary && pair.Value.Lock is not null).OrderBy(pair => pair.Key).Select(pair =>
        {
            ParticleStationarity.CyclostationaryLock cycle = pair.Value.Lock!;
            CommonLoopRational exact = cycle.PeriodSeconds;
            return new CommonLoopComponent(cycle.ComponentId, new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact));
        })];

    /// <summary>粒子未解析项的 detail：锁定周期、平稳随机、不满足判据三种，文案走 MessageCatalog。</summary>
    private static Message ParticleDetail(ParticleStationarity.Result verdict) => verdict.Stationary
        ? verdict.Lock is ParticleStationarity.CyclostationaryLock cycle
            ? new Message("unresolved.particle_cyclostationary_locked", [cycle.PeriodFrames.ToString(CultureInfo.InvariantCulture),
                cycle.PeriodSeconds.ToSeconds().ToString("0.######", CultureInfo.InvariantCulture)])
            : new Message("unresolved.particle_stationary_random")
        : new Message("unresolved.particle_not_stationary", [verdict.FailureSummary()]);

    /// <summary>判据结论改变（锁定周期退回拒绝）后，重写 unresolved 里对应层的 particle_stationarity；补记的 particle_system 条目连 detail 一起换。</summary>
    private static void RewriteParticleItems(JsonArray unresolved, IReadOnlyDictionary<int, ParticleStationarity.Result> verdicts)
    {
        foreach (JsonObject item in unresolved.OfType<JsonObject>())
        {
            if (item["particle_stationarity"] is not JsonObject || HybridScenePlanner.Int(item["owner_layer_id"]) is not int owner ||
                !verdicts.TryGetValue(owner, out ParticleStationarity.Result? verdict)) continue;
            item["particle_stationarity"] = verdict.ToJson();
            if (item["mechanism"]?.GetValue<string>() == "particle_system") ParticleDetail(verdict).Write(item, "detail");
        }
    }

    /// <summary>
    /// 把可建模的 uv_sway 未解析项变成"改频后精确闭合的周期分量"：对每个候选 P 求 L = kP ≤ Lmax 上改动最小的逐项改频方案，
    /// 没有解的候选丢掉；至少一个候选有解时，把这些摆动项从 unresolved 里移出，候选帧数改成 L、分量圈数乘 k，
    /// 并挂上 sway_retime 记录（bake 按它写覆盖 shader）。全都没有解时候选与未解析项原样保留，只记原因。
    /// 除摆动之外没有任何时间机制时（求解器没有候选），以 1 帧为 P；求解器保证不会因此产出 1 帧静止候选（见 SwayRecurrenceSolver 护栏）。
    /// </summary>
    private static JsonObject ApplySwayRetime(IReadOnlyList<ShaderTemporalUnresolved> shaderUnresolved, JsonArray unresolved,
        JsonArray candidates, bool noOtherTemporalMechanism, uint fpsNumerator, uint fpsDenominator, SwayRetimeOptions options,
        float[][] spriteTables)
    {
        var record = new JsonObject { ["enabled"] = true, ["loop_length_maximum_seconds"] = options.LoopLengthMaximumSeconds,
            ["output_per_scene_x"] = options.OutputPerSceneX, ["output_per_scene_y"] = options.OutputPerSceneY,
            // 档位与预算：观感改动预算是求解参数，循环长度是求解结果。
            ["preset"] = options.Profile?.Preset, ["retime_budget_percent"] = options.BudgetPercent,
            ["retime_budget_source"] = options.Profile?.BudgetSource };
        // 循环长度上限按内嵌视频 2 GiB 收紧的记录；loop_length_maximum_seconds 已是收紧后的生效值。
        if (options.VideoLimit is EmbeddedVideoLoopLimit videoLimit) record["embedded_video_limit"] = videoLimit.ToJson();
        int[] swayIndexes = shaderUnresolved.Select((item, index) => (item, index))
            .Where(pair => pair.item.Mechanism == ShaderPeriodAnalysis.FoliageSwayMechanism).Select(pair => pair.index).ToArray();
        int[] modeled = swayIndexes.Where(index => shaderUnresolved[index].Sway is not null).ToArray();
        record["sway_items"] = swayIndexes.Length;
        record["modeled_items"] = modeled.Length;
        if (modeled.Length == 0)
        {
            record["status"] = swayIndexes.Length == 0 ? "no_sway_component" : "sway_not_modeled";
            return record;
        }
        SwayRetimeInput[] inputs = modeled.Select(index =>
        {
            SwayModel model = shaderUnresolved[index].Sway!;
            var amplitude = model.AmplitudeOutputPixels(options.OutputPerSceneX, options.OutputPerSceneY);
            return new SwayRetimeInput(model, amplitude?.X, amplitude?.Y);
        }).ToArray();
        // 振幅换算不到输出像素（读不到图层尺寸）时慢项的速度偏差无从判定：候选与未解析项原样保留，写明原因。
        int[] unknownAmplitude = inputs.Where(input => input.AmplitudeXPixels is null || input.AmplitudeYPixels is null)
            .Select(input => input.Model.OwnerLayerId).Distinct().ToArray();
        if (unknownAmplitude.Length > 0)
        {
            string layers = string.Join(", ", unknownAmplitude.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            record["status"] = "amplitude_unknown";
            record["amplitude_unknown_layer_ids"] = new JsonArray(unknownAmplitude.Select(id => (JsonNode)JsonValue.Create(id)).ToArray());
            record["reason_zh"] = MessageCatalog.Get("sway_retime.amplitude_unknown", MessageCatalog.Chinese, layers);
            record["reason_en"] = MessageCatalog.Get("sway_retime.amplitude_unknown", MessageCatalog.English, layers);
            return record;
        }
        bool synthesized = false;
        if (candidates.Count == 0 && noOtherTemporalMechanism && modeled.Length == swayIndexes.Length)
        {
            candidates.Add(new JsonObject { ["frames"] = 1UL, ["seconds"] = (double)fpsDenominator / fpsNumerator,
                ["total_retime_cost_percent"] = 0d, ["components"] = new JsonArray(), ["patches"] = new JsonArray() });
            synthesized = true;
        }
        record["base_candidate_count"] = candidates.Count;
        var solutions = new SwayRetimeSolution?[candidates.Count];
        var spriteSelections = new SpriteSeamPhase.Selection?[candidates.Count];
        int spriteRejected = 0, limitRejected = 0, budgetRejected = 0;
        for (int index = 0; index < candidates.Count; ++index)
        {
            if (candidates[index] is not JsonObject candidate || candidate["frames"] is not JsonValue frames || !frames.TryGetValue(out ulong baseFrames))
                continue;
            solutions[index] = SwayRecurrenceSolver.Solve(inputs, baseFrames, fpsNumerator, fpsDenominator,
                options.LoopLengthMaximumSeconds, options.BudgetPercent);
            // 上限内取得到 kP、却没有一个 L 合规（可见项走不满整圈或速度偏差超限、慢项速度偏差超限、圈数不够、只剩 1 帧静止）：
            // 记下来，别当成"上限内没有 kP"。预算档再分一层：不设预算时有解，就是预算太紧卡的，原因要讲成预算。
            if (solutions[index] is null && SwayRecurrenceSolver.MaximumMultiple(baseFrames, fpsNumerator, fpsDenominator, options.LoopLengthMaximumSeconds) > 0)
            {
                ++limitRejected;
                if (options.BudgetPercent is not null &&
                    SwayRecurrenceSolver.Solve(inputs, baseFrames, fpsNumerator, fpsDenominator, options.LoopLengthMaximumSeconds) is not null)
                    ++budgetRejected;
            }
            // 精灵 float32 接缝按 P 判过；L = kP 上漂移累积 k 倍，要在 L 上重新逐帧判定（与候选生成同一判据）。
            if (solutions[index] is SwayRetimeSolution solved && spriteTables.Length > 0)
            {
                var selection = SpriteSeamPhase.Select(spriteTables, fpsNumerator, fpsDenominator, solved.Frames);
                if (selection.AtOrigin == SpriteSeamPhase.Verdict.Mismatch && selection.WarmupFrames is null) { solutions[index] = null; ++spriteRejected; }
                else spriteSelections[index] = selection;
            }
        }
        if (spriteRejected > 0) record["sprite_float32_rejected_candidate_count"] = spriteRejected;
        if (limitRejected > 0) record["speed_limit_rejected_candidate_count"] = limitRejected;
        if (budgetRejected > 0) record["budget_rejected_candidate_count"] = budgetRejected;
        if (solutions.All(solution => solution is null))
        {
            if (synthesized) candidates.Clear();
            string status = budgetRejected > 0 ? "no_multiple_within_budget"
                : limitRejected > 0 ? "no_multiple_meets_speed_limit"
                : candidates.Count == 0 ? "no_base_candidate" : "no_multiple_within_maximum";
            string maximum = options.LoopLengthMaximumSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            string limit = status == "no_multiple_within_budget"
                ? (options.BudgetPercent ?? 0).ToString("0.###", CultureInfo.InvariantCulture)
                : SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond.ToString("0.###", CultureInfo.InvariantCulture);
            // 上限是被内嵌视频大小收紧的，原因后面补一句说明收紧依据，否则用户看到的秒数和自己给的对不上。
            string limitZh = options.VideoLimit is { Applied: true } appliedLimit ? appliedLimit.Sentence(MessageCatalog.Chinese) : "";
            string limitEn = options.VideoLimit is { Applied: true } appliedLimitEn ? " " + appliedLimitEn.Sentence(MessageCatalog.English) : "";
            record["status"] = status;
            record["reason_zh"] = MessageCatalog.Get("sway_retime." + status, MessageCatalog.Chinese, maximum, limit) + limitZh;
            record["reason_en"] = MessageCatalog.Get("sway_retime." + status, MessageCatalog.English, maximum, limit) + limitEn;
            return record;
        }
        for (int index = candidates.Count - 1; index >= 0; --index)
        {
            if (solutions[index] is not SwayRetimeSolution solution) { candidates.RemoveAt(index); continue; }
            JsonObject candidate = candidates[index]!.AsObject();
            candidate["frames"] = solution.Frames;
            candidate["seconds"] = solution.Seconds;
            if (spriteSelections[index] is SpriteSeamPhase.Selection sprite)
            {
                candidate.Remove("source_period_warmup_frames");
                candidate.Remove("sprite_seam_phase");
                if (sprite.WarmupFrames is ulong warmup && warmup > 0)
                {
                    candidate["source_period_warmup_frames"] = warmup;
                    candidate["sprite_seam_phase"] = new JsonObject { ["origin"] = sprite.AtOrigin.ToString().ToLowerInvariant(),
                        ["after_one_period"] = sprite.AfterOnePeriod?.ToString().ToLowerInvariant(),
                        ["basis"] = "float32 sprite frame table re-checked on the sway-retimed loop length" };
                }
                else if (sprite.AtOrigin == SpriteSeamPhase.Verdict.Undetermined)
                    candidate["sprite_seam_phase"] = new JsonObject { ["origin"] = "undetermined",
                        ["basis"] = "a sample lies within the renderer's double accumulation error of a sprite boundary" };
            }
            // 其余分量在 P 上已闭合，L = kP 上各自多走 k 倍圈数，调速倍率与补丁不变。
            foreach (JsonObject component in candidate["components"]?.AsArray().OfType<JsonObject>() ?? [])
                if (component["cycles"] is JsonValue cycles && cycles.TryGetValue(out ulong count))
                    component["cycles"] = checked(count * solution.Multiple);
            candidate["sway_retime"] = SwayRecurrenceSolver.ToJson(solution, options.LoopLengthMaximumSeconds, fpsNumerator,
                fpsDenominator, options.Profile);
        }
        // shader 未解析项在 unresolved 里排在最前、与 shaderUnresolved 同序；从后往前移出已建模的摆动项。
        foreach (int index in modeled.OrderDescending())
        {
            if (unresolved[index] is not JsonObject item || item["mechanism"]?.GetValue<string>() != ShaderPeriodAnalysis.FoliageSwayMechanism ||
                item["owner_layer_id"]?.GetValue<int>() != shaderUnresolved[index].OwnerLayerId)
                throw new InvalidOperationException("Sway unresolved items are out of order.");
            unresolved.RemoveAt(index);
        }
        record["status"] = "applied";
        record["resolved_items"] = new JsonArray(modeled.Select(index => (JsonNode)new JsonObject {
            ["owner_layer_id"] = shaderUnresolved[index].OwnerLayerId, ["effect_index"] = shaderUnresolved[index].EffectIndex,
            ["pass_index"] = shaderUnresolved[index].PassIndex, ["resource"] = shaderUnresolved[index].Resource }).ToArray());
        record["dropped_candidate_count"] = solutions.Count(solution => solution is null);
        return record;
    }

    /// <summary>
    /// 返回 null 表示这次捕获可由源与运行时证据证明为一张静态图；否则保留理由和可定位的所有者，供重新分配使用。
    /// 旧版返回 bool，判否时不留任何记录，是 plan 里"零候选零理由 unavailable"的直接来源。
    /// </summary>
    private static JsonObject? SourceStaticObstacle(JsonObject scene, ProjectSource source, string? assetsDirectory,
        JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds)
    {
        static JsonObject Obstacle(string detail, int? owner = null) => new() {
            ["kind"] = "source_static", ["detail"] = detail, ["owner_layer_id"] = owner };
        // 点名被烘图层的几种理由另带结构化的层名与"是不是粒子系统"，一行结论的中文据此说，不从英文明细里抠。
        JsonObject Named(string detail, int id, bool particle = false)
        {
            JsonObject obstacle = Obstacle(detail, id);
            obstacle[PlanNarrative.StaticLayer] = new JsonObject { ["name"] = Name(id), ["particle"] = particle };
            return obstacle;
        }
        if (runtime["status"] is not JsonValue status || !status.TryGetValue<string>(out string? state) || state != "complete" || runtime["runtime_layers"] is not JsonArray layers ||
            runtime["runtime_dependencies"] is not JsonArray dependencies || runtime["runtime_animation_periods"] is not JsonArray periods)
            return Obstacle("Runtime observation is incomplete, so a static capture cannot be proven.");
        var selected = new HashSet<int>(bakedLayerIds);
        if (selected.Count == 0) return Obstacle("No layer is allocated to video, so there is nothing to capture.");
        if (scene["objects"] is not JsonArray objects) return Obstacle("The source scene lists no objects.");
        bool noLights = !SceneAnalyzer.Walk(scene).OfType<JsonObject>().Any(node => node.ContainsKey("light"));
        var owners = new Dictionary<int, JsonObject>();
        foreach (JsonNode? node in objects)
        {
            if (node is not JsonObject owner || owner["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out int id) || !owners.TryAdd(id, owner))
                return Obstacle("The source scene has an object without a unique integer id.");
        }
        string? Name(int id) => owners.TryGetValue(id, out JsonObject? owner) && owner["name"] is JsonValue name &&
            name.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        string Describe(int id) => Name(id) is string text ? $"layer {id} \"{text}\"" : $"layer {id}";
        foreach (int id in selected)
            if (!owners.ContainsKey(id)) return Obstacle($"Baked {Describe(id)} is absent from the source scene.");
        foreach (int id in selected)
            if (DynamicSourceMechanism(owners[id]) is (string key, string mechanism))
                return Named($"Baked {Describe(id)} contains {mechanism}; no analytic period was established for it either, so neither a loop nor a still image can be proven.", id, key == "particle");
        foreach (JsonNode? node in periods)
        {
            if (node is not JsonObject period || period["source_owner_layer_id"] is not JsonValue owner ||
                !owner.TryGetValue<int>(out int id) || period["mechanism"] is not JsonValue)
                return Obstacle("A runtime animation period entry is malformed, so the observation cannot establish a still image.");
            if (selected.Contains(id)) return Obstacle($"Runtime observation recorded an animation period on baked {Describe(id)}.", id);
        }
        foreach (JsonNode? node in dependencies)
        {
            if (node is not JsonObject dependency || dependency["owner"] is not JsonValue owner || dependency["target"] is not JsonValue target ||
                !owner.TryGetValue<int>(out int ownerId) || !target.TryGetValue<int>(out int targetId))
                return Obstacle("A runtime dependency entry is malformed, so the observation cannot establish a still image.");
            if (!selected.Contains(ownerId) && !selected.Contains(targetId)) continue;
            if (dependency["operation"] is not JsonValue operation || !operation.TryGetValue<string>(out string? name) || name != "write" ||
                dependency["initialization"] is not JsonValue initialization || !initialization.TryGetValue<bool>(out bool initial) || !initial)
                return Named($"Baked {Describe(selected.Contains(ownerId) ? ownerId : targetId)} takes part in a runtime dependency that is not an initialization write.", selected.Contains(ownerId) ? ownerId : targetId);
        }
        foreach (int id in selected)
        {
            JsonObject[] observed = layers.OfType<JsonObject>().Where(layer =>
                layer["owner"] is JsonValue owner && owner.TryGetValue<int>(out int observedOwner) && observedOwner == id).ToArray();
            if (observed.Length == 0) return Obstacle($"Runtime observation recorded no rendered layer for baked {Describe(id)}.", id);
            foreach (JsonObject layer in observed)
            {
                if (layer["has_mesh"] is not JsonValue mesh || !mesh.TryGetValue<bool>(out _) || layer["materials"] is not JsonArray materials)
                    return Obstacle($"Runtime observation of baked {Describe(id)} omits its mesh flag or material list.", id);
                foreach (JsonNode? node in materials)
                {
                    if (node is not JsonObject material) return Obstacle($"Runtime observation of baked {Describe(id)} has a malformed material entry.", id);
                    if (MaterialStaticObstacle(material, source, assetsDirectory, noLights) is string reason)
                        return Named($"Baked {Describe(id)}: {reason}.", id);
                }
            }
        }
        return null;
    }

    private static readonly (string Key, string Description)[] DynamicSourceMechanisms = [
        ("particle", "a particle system, whose emission and lifetimes are not a solved periodic mechanism"),
        ("puppet", "a puppet warp rig"),
        ("sound", "an audio source"),
        ("animation", "an authored animation"),
        ("animations", "authored animations"),
        ("animationlayers", "authored animation layers")];

    private static (string Key, string Description)? DynamicSourceMechanism(JsonObject owner)
    {
        foreach (JsonObject node in SceneAnalyzer.Walk(owner).OfType<JsonObject>())
        {
            if (node["script"] is not null) return ("script", "a script binding whose behavior over time is not proven");
            foreach (var (key, description) in DynamicSourceMechanisms)
                if (node.ContainsKey(key)) return (key, description);
        }
        return null;
    }

    /// <summary>
    /// 材质层面的静态障碍。role=effect 的材质与 <see cref="AddRuntimeMaterialTemporalUnresolved"/> 用同一口径：
    /// 特效的时间机制由 <see cref="ShaderPeriodAnalysis"/> 做源码级分析，走到这里说明它一个分量、一条未解析都没留下，
    /// 因此只拒绝运行时时钟与动态纹理，不再拿基础材质的 uniform 白名单去卡特效自带的参数（u_strength 之类）。
    /// 合成中间目标 _rt_* 是本层这一串 pass 自己的产物：跨层读写由上面的运行时依赖检查拦下，
    /// 读取整帧的图层更早就被判为必须实时、根本不在被烘集合里。
    /// </summary>
    private static string? MaterialStaticObstacle(JsonObject material, ProjectSource source, string? assetsDirectory, bool noLights)
    {
        if (!False(material["uses_audio_spectrum"])) return "a material reacts to the audio spectrum";
        if (!False(material["uses_system_media_thumbnail"])) return "a material reads the system media thumbnail";
        if (material["role"] is JsonNode role && (role is not JsonValue roleValue || !roleValue.TryGetValue<string>(out _)))
            return "a material role is not a string, so its temporal behavior is unknown";
        bool effect = string.Equals(material["role"]?.GetValue<string>(), "effect", StringComparison.Ordinal);
        if (material["active_uniforms"] is not JsonArray uniforms) return "a material omits its active uniform list";
        if (material["textures"] is not JsonArray textures) return "a material omits its texture list";
        foreach (JsonNode? node in uniforms)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out string? name))
                return "a material active uniform entry is not a string";
            if (effect ? IsRuntimeClock(name) : !StaticUniform(name, noLights))
                return $"material uniform \"{name}\" is not proven time-independent";
        }
        foreach (JsonNode? node in textures)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out string? name))
                return "a material texture entry is not a string";
            if (effect && name.StartsWith("_rt_", StringComparison.Ordinal)) continue;
            if (!StaticTexture(source, assetsDirectory, name)) return $"material texture \"{name}\" is not a proven still image";
        }
        return null;
    }

    private static bool False(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out bool flag) && !flag;

    private static bool StaticUniform(string name, bool noLights) => name is "g_ModelViewProjectionMatrix" or "g_EyePosition" or
        "g_ModelMatrix" or "g_ViewProjectionMatrix" or "g_Color4" || Regex.IsMatch(name, @"^g_Texture\d+(?:Rotation|Translation|Resolution)$",
            RegexOptions.CultureInvariant) || noLights && name.StartsWith("g_Lights", StringComparison.Ordinal);

    private static bool StaticTexture(ProjectSource source, string? assetsDirectory, string texture)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(texture)) return true;
            if (texture.StartsWith("_rt_", StringComparison.Ordinal)) return false;
            string[] resources = texture.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                ? [texture] : [texture, "materials/" + texture + ".tex"];
            foreach (string resource in resources)
            {
                if (source.Contains(resource))
                {
                    if (StaticTextureHeader(source.ReadPrefix(resource, 256))) return true;
                    continue;
                }
                // 官方 assets 目录里的通用纹理（util/white 之类）与项目包内资源同等看待：同一份头部校验。
                if (AssetTexturePrefix(assetsDirectory, resource) is byte[] header && StaticTextureHeader(header)) return true;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentOutOfRangeException) { return false; }
    }

    private static byte[]? AssetTexturePrefix(string? assetsDirectory, string resource)
    {
        if (assetsDirectory is null) return null;
        string path = ProjectSource.ContainedPath(assetsDirectory, resource);
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        var header = new byte[(int)Math.Min(file.Length, 256)];
        file.ReadExactly(header);
        return header;
    }

    private static bool StaticTextureHeader(byte[] header)
    {
        if (header.Length < 75 || !header.AsSpan(0, 9).SequenceEqual("TEXV0005\0"u8) ||
            !header.AsSpan(9, 9).SequenceEqual("TEXI0001\0"u8) || (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4)) & 0x24) != 0 ||
            !header.AsSpan(46, 4).SequenceEqual("TEXB"u8) || header[53] is < (byte)'1' or > (byte)'4') return false;
        int version = header[53] - '0', offset = 55;
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 4;
        if (count != 1) return false;
        if (version >= 3)
        {
            int type = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 4;
            if (type == 100) return false;
            if (version >= 4) offset += 4;
            if (type != -1) return true;
        }
        if (offset + 16 + (version >= 2 ? 8 : 0) > header.Length) return false;
        // mip 数量之后紧跟第一级 mip 的宽高与数据，多级 mipmap 不改变这个偏移，也不会是视频容器
        // （封装的动图走 type==100 或下面的 ftyp/EBML 判据）。要求恰好一级会把普通带 mipmap 的
        // 贴图（官方 assets 里的 util/white 就是四级）误判成非静态。
        int mips = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 12;
        if (mips < 1) return false;
        // TEXB0002 起每级 mip 带 [是否 LZ4 压缩, 解压后字节数]。压缩只改变存储，不改变内容：解出开头 12 个字节，
        // 照未压缩时的同一判据排除视频容器头。
        bool lz4 = false;
        int decodedSize = 0;
        if (version >= 2)
        {
            int compression = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4));
            if (compression is not (0 or 1)) return false;
            lz4 = compression == 1;
            decodedSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset + 4, 4));
            offset += 8;
        }
        int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 4;
        if (size < 0) return false;
        if (!lz4) return size < 12 || offset + 12 <= header.Length && !IsVideoContainerPrefix(header.AsSpan(offset, 12));
        if (decodedSize < 0) return false;
        if (decodedSize < 12) return true;
        return TryLz4Prefix(header.AsSpan(offset, Math.Min(size, header.Length - offset)), 12, out byte[] decoded) &&
            !IsVideoContainerPrefix(decoded);
    }

    private static bool IsVideoContainerPrefix(ReadOnlySpan<byte> prefix) =>
        prefix.Slice(4, 4).SequenceEqual("ftyp"u8) || prefix.Slice(0, 4).SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

    /// <summary>
    /// 只解 LZ4 块开头的 <paramref name="count"/> 个字节（token、literal、两字节回指距离、匹配长度，按 LZ4 块格式）。
    /// 输入在凑够之前用完或回指越界时返回 false，调用方按未证明处理。
    /// </summary>
    internal static bool TryLz4Prefix(ReadOnlySpan<byte> input, int count, out byte[] output)
    {
        output = [];
        var decoded = new List<byte>(count);
        int position = 0;
        while (decoded.Count < count)
        {
            if (position >= input.Length) return false;
            int token = input[position++];
            if (!TryLength(input, ref position, token >> 4, out int literal)) return false;
            for (int index = 0; index < literal && decoded.Count < count; ++index)
            {
                if (position >= input.Length) return false;
                decoded.Add(input[position++]);
            }
            if (decoded.Count >= count) break;
            if (position + 2 > input.Length) return false;
            int distance = input[position] | input[position + 1] << 8;
            position += 2;
            if (distance == 0 || distance > decoded.Count) return false;
            // 匹配长度下限（半字节 + 4）已够补满前缀时不读扩展长度字节：纯色大图的扩展字节可以长过预读范围。
            int match = (token & 15) + 4;
            if (match < count - decoded.Count)
            {
                if (!TryLength(input, ref position, token & 15, out match)) return false;
                match += 4;
            }
            for (int index = 0; index < match && decoded.Count < count; ++index) decoded.Add(decoded[decoded.Count - distance]);
        }
        output = [.. decoded];
        return true;

        static bool TryLength(ReadOnlySpan<byte> bytes, ref int at, int nibble, out int length)
        {
            length = nibble;
            if (nibble != 15) return true;
            int more;
            do
            {
                if (at >= bytes.Length) return false;
                more = bytes[at++];
                length += more;
            } while (more == 255);
            return true;
        }
    }

    /// <summary>Applies only the selected report candidate's rate patches to a temporary capture scene.</summary>
    public static void ApplyPatches(JsonObject captureScene, JsonObject loopReport, int candidateIndex = 0)
    {
        JsonObject candidate = loopReport["candidates"]?.AsArray().ElementAtOrDefault(candidateIndex)?.AsObject()
            ?? throw new InvalidDataException("Loop report has no selected candidate.");
        var owners = captureScene["objects"]?.AsArray().OfType<JsonObject>().ToDictionary(x => x["id"]!.GetValue<int>())
            ?? throw new InvalidDataException("Capture scene has no objects.");
        foreach (JsonObject patch in candidate["patches"]!.AsArray().OfType<JsonObject>())
        {
            if (patch["kind"]?.GetValue<string>() == "video_rate") continue;
            int ownerId = patch["owner_layer_id"]!.GetValue<int>();
            if (!owners.TryGetValue(ownerId, out JsonObject? owner)) throw new InvalidDataException($"Patch owner {ownerId} is absent.");
            double oldValue = patch["old_value"]!.GetValue<double>(), newValue = patch["new_value"]!.GetValue<double>();
            if (patch["kind"]!.GetValue<string>() is "shader_speed" or "shader_phase")
            {
                JsonObject pass = owner["effects"]!.AsArray()[patch["effect_index"]!.GetValue<int>()]!.AsObject()["passes"]!.AsArray()[patch["pass_index"]!.GetValue<int>()]!.AsObject();
                string key = patch["constant_key"]!.GetValue<string>(); int index = patch["value_index"]!.GetValue<int>();
                JsonNode? value = pass["constantshadervalues"]?[key];
                if (value is JsonArray array) { Verify(array[index], oldValue); array[index] = newValue; }
                else { Verify(value, oldValue); pass["constantshadervalues"]![key] = newValue; }
            }
            else if (patch["kind"]!.GetValue<string>() == "animation_rate")
            {
                int layerId = patch["animation_layer_id"]!.GetValue<int>();
                JsonObject layer = owner["animationlayers"]!.AsArray().OfType<JsonObject>().Single(x => x["id"]?.GetValue<int>() == layerId);
                Verify(layer["rate"], oldValue); layer["rate"] = newValue;
            }
            else throw new InvalidDataException("Unknown loop patch kind.");
        }
    }

    private static List<AnimationInfo> ReadAnimations(JsonObject scene, ProjectSource source, string? assetsDirectory,
        JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds, JsonArray unresolved, VideoControlScope videoControlScope,
        ParticleStationarity.FrameClock clock, out Dictionary<int, ParticleStationarity.Result> particleVerdicts)
    {
        var owners = scene["objects"]?.AsArray().OfType<JsonObject>().ToDictionary(x => x["id"]!.GetValue<int>())
            ?? throw new InvalidDataException("Scene has no objects.");
        var selected = new HashSet<int>(bakedLayerIds);
        var output = new Dictionary<string, AnimationInfo>(StringComparer.Ordinal);
        // 粒子平稳随机判据按层只算一次；读不到的资源交给判据记成不满足，不在这里抛。
        var stationarity = new Dictionary<int, ParticleStationarity.Result>();
        particleVerdicts = stationarity;
        JsonObject? ReadParticleResource(string resource)
        {
            try { return SceneAnalyzer.ReadResourceJson(source, assetsDirectory, resource); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
                InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
        ParticleStationarity.Result Stationarity(int ownerId, JsonObject owner)
        {
            if (!stationarity.TryGetValue(ownerId, out ParticleStationarity.Result? verdict))
                stationarity[ownerId] = verdict = ParticleStationarity.Evaluate(owner, owners, runtime, ReadParticleResource, clock);
            return verdict;
        }
        foreach (JsonObject trace in runtime["runtime_animation_periods"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            int ownerId = trace["source_owner_layer_id"]?.GetValue<int>() ?? -1;
            if (!selected.Contains(ownerId)) continue;
            string? trackName = trace["track_name"]?.GetValue<string>();
            bool video = string.Equals(trace["mechanism"]?.GetValue<string>(), "video", StringComparison.OrdinalIgnoreCase);
            if (!owners.TryGetValue(ownerId, out JsonObject? owner))
            { unresolved.Add(new Message("unresolved.owner_or_duration_unresolved").Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId }, "detail")); continue; }
            if (video)
            {
                if (trace["looping"]?.GetValue<bool>() != true || trace["event_driven"]?.GetValue<bool>() == true ||
                    !TryExactVideoDuration(trace, out CommonLoopRational videoDuration) || VideoPlaybackIsControlled(trace, runtime, ownerId, videoControlScope))
                {
                    unresolved.Add(new Message("unresolved.video_needs_exact_duration").Write(new JsonObject { ["kind"] = "runtime_video", ["owner_layer_id"] = ownerId, ["track_name"] = trackName }, "detail"));
                    continue;
                }
                JsonValue? videoRate = trace["playback_rate"] as JsonValue ?? trace["current_rate"] as JsonValue;
                if (videoRate is null || !TryRational(videoRate.ToJsonString(), out CommonLoopRational lockedVideoRate) || lockedVideoRate != new CommonLoopRational(1))
                {
                    unresolved.Add(new Message("unresolved.video_rate_not_one").Write(new JsonObject { ["kind"] = "runtime_video", ["owner_layer_id"] = ownerId, ["track_name"] = trackName }, "detail"));
                    continue;
                }
                string videoTrack = string.IsNullOrWhiteSpace(trackName) ? "(anonymous)" : trackName;
                string videoKey = $"video/{ownerId}/{videoTrack}/{videoDuration.Numerator}/{videoDuration.Denominator}";
                if (!output.ContainsKey(videoKey)) output[videoKey] = new(ownerId, videoTrack, [], [], true,
                    new CommonLoopComponent(videoKey, new CommonLoopPeriod(videoDuration.ToSeconds(), CommonLoopPeriodEvidence.Analytic, videoDuration)),
                    new CommonLoopComponent(videoKey, new CommonLoopPeriod(videoDuration.ToSeconds(), CommonLoopPeriodEvidence.Analytic), true), true,
                    TryClipFrameRate(trace, videoDuration, out CommonLoopRational clipRate) ? clipRate : null);
                continue;
            }
            if (trace["looping"]?.GetValue<bool>() != true || trace["event_driven"]?.GetValue<bool>() == true ||
                !string.Equals(trace["confidence"]?.GetValue<string>(), "high", StringComparison.OrdinalIgnoreCase))
            { unresolved.Add(new Message("unresolved.animation_not_high_confidence").Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId }, "detail")); continue; }
            // 优先读渲染器给的有理时长（已吸附成 float 精度区间内的最简分数，如 12/5）；duration_seconds 是 double 真值
            // （2.400000035762787），按十进制文字取有理数会把 float 误差带进公倍数。旧渲染器或溢出时没有有理字段，才退回十进制。
            if (!TryExactVideoDuration(trace, out CommonLoopRational duration) &&
                (trace["duration_seconds"] is not JsonValue durationValue || !TryRational(durationValue.ToJsonString(), out duration)))
            { unresolved.Add(new Message("unresolved.owner_or_duration_unresolved").Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId }, "detail")); continue; }
            bool spriteDurationIsPeriod = string.Equals(trace["mechanism"]?.GetValue<string>(), "sprite", StringComparison.OrdinalIgnoreCase);
            if (spriteDurationIsPeriod && owner["particle"] is not null)
            {
                // 证明不了周期时要说清是哪一种随机源或外部输入，不是一句笼统的拒绝。分配不变：本次不合成粒子有效周期。
                // detail 走 MessageCatalog（每条理由一个 key），与 i18n 分支口径一致：legacy 英文写进 plan，中文由 Localize 反查。
                // particle_stationarity 是下游（更小分配回退、残差掩盖）读的结构化结论，detail 与理由代号保持原样。
                (string code, Message detail) = ParticleInputAnalysis.NonperiodicReason(owner, source, assetsDirectory);
                unresolved.Add(detail.Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId, ["track_name"] = trackName,
                    ["particle_nonperiodic_reason"] = code, ["particle_stationarity"] = Stationarity(ownerId, owner).ToJson() }, "detail"));
                continue;
            }
            string resource = spriteDurationIsPeriod ? "textureAnimation" : "animation";
            bool observedControl = runtime["runtime_dependencies"]?.AsArray().OfType<JsonObject>().Any(dependency =>
                dependency["target"]?.GetValue<int>() == ownerId && dependency["operation"]?.GetValue<string>() == "write" &&
                dependency["property"]?.GetValue<string>() == resource && dependency["initialization"]?.GetValue<bool>() != true) == true;
            if (observedControl || spriteDurationIsPeriod && HasSpritePlaybackControl(owner))
            {
                // 随机重启的证明只对精灵轨道成立，而且必须在同一段脚本里闭合：取到这条纹理动画、对它做播放控制、
                // 用 Math.random 喂定时器延迟。骨骼/属性动画轨道被脚本控制时不借用这份证明；脚本别处拿
                // Math.random 做颜色之类也不算。结论写成结构化字段，下游（残差掩盖）读字段，不再匹配文案。
                bool randomRestart = spriteDurationIsPeriod && HasRandomSpriteRestart(owner, trackName);
                unresolved.Add(new Message(randomRestart ? "unresolved.script_random_restart" : "unresolved.script_controlled_playback").Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId,
                    ["track_name"] = trackName,
                    ["mechanism"] = spriteDurationIsPeriod ? "sprite" : trace["mechanism"]?.GetValue<string>() ?? "animation",
                    ["random_restart"] = randomRestart }, "detail"));
                continue;
            }
            JsonValue? traceRate = trace["playback_rate"] as JsonValue ?? trace["current_rate"] as JsonValue;
            CommonLoopRational rate = new(1);
            if ((traceRate is not null && !TryRational(traceRate.ToJsonString(), out rate)) || (!spriteDurationIsPeriod && traceRate is null))
            { unresolved.Add(new Message("unresolved.playback_rate_unresolved").Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId, ["track_name"] = trackName }, "detail")); continue; }
            if (spriteDurationIsPeriod && rate != new CommonLoopRational(1))
            { unresolved.Add(new Message("unresolved.sprite_rate_not_one").Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId, ["track_name"] = trackName }, "detail")); continue; }
            CommonLoopRational period = spriteDurationIsPeriod ? duration : Divide(duration, rate);
            // Duration is one authored traversal; a mirror track returns to its start after two.
            if (!spriteDurationIsPeriod && string.Equals(trace["playback_mode"]?.GetValue<string>(), "mirror", StringComparison.OrdinalIgnoreCase))
                period = new CommonLoopRational(checked(period.Numerator * 2), period.Denominator);
            string track = string.IsNullOrWhiteSpace(trackName) ? "(anonymous)" : trackName;
            string key = $"animation/{ownerId}/{track}/{duration.Numerator}/{duration.Denominator}/{rate.Numerator}/{rate.Denominator}";
            JsonObject[] layers = string.IsNullOrWhiteSpace(trackName) ? [] :
                owner["animationlayers"]?.AsArray().OfType<JsonObject>().Where(x => x["name"]?.GetValue<string>() == trackName).ToArray() ?? [];
            var matched = layers.Where(x => x["rate"] is JsonValue).ToArray();
            int[] ids = matched.Select(x => x["id"]?.GetValue<int>() ?? -1).Where(x => x >= 0).Distinct().ToArray();
            var authoredRates = new double[matched.Length];
            bool finiteAuthoredRates = true;
            for (int index = 0; index < matched.Length; ++index)
                finiteAuthoredRates &= TryFiniteRate(matched[index]["rate"], out authoredRates[index]);
            bool canRetime = !spriteDurationIsPeriod && matched.Length != 0 && ids.Length == matched.Length && finiteAuthoredRates &&
                matched.All(x => Float32RateEquals(x["rate"], rate));
            if (!canRetime && matched.Length != 0)
                unresolved.Add(new Message("unresolved.no_authored_rate_patch").Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId, ["track_name"] = track }, "detail"));
            if (!output.ContainsKey(key)) output[key] = new(ownerId, track, ids, authoredRates, canRetime, new CommonLoopComponent(key,
                new CommonLoopPeriod(period.ToSeconds(), CommonLoopPeriodEvidence.Observed, period)),
                new CommonLoopComponent(key, new CommonLoopPeriod(period.ToSeconds(), CommonLoopPeriodEvidence.Observed), true), false,
                SpriteFrameTimes: spriteDurationIsPeriod ? ReadSpriteFrameTimes(source, assetsDirectory, trackName, duration) : null);
        }
        // 没有精灵轨道的粒子层（雨、雪、雾大多如此）运行时观测里没有任何轨道，以前对周期分析不可见，
        // 只能靠更小分配回退把所有粒子层一律留实时。这里给每个被烘的粒子层补一条未解析项：
        // 粒子没有周期，接缝要么淡化替换（判据通过），要么这层留实时（判据不通过）。
        foreach (int ownerId in bakedLayerIds.Distinct())
        {
            if (!owners.TryGetValue(ownerId, out JsonObject? owner) || owner["particle"] is null || stationarity.ContainsKey(ownerId)) continue;
            ParticleStationarity.Result verdict = Stationarity(ownerId, owner);
            unresolved.Add(ParticleDetail(verdict).Write(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = ownerId, ["mechanism"] = "particle_system",
                ["particle_stationarity"] = verdict.ToJson() }, "detail"));
        }
        return output.Values.ToList();
    }

    /// <summary>
    /// 运行时材质里没被建模的时钟 uniform。一个图层的时间行为由「哪个 shader、哪套常量」决定，不由它在运行时
    /// 以 source 还是 effect 角色实例化决定：<paramref name="ruledMaterials"/> 里已经被方程规则裁定过的
    /// (层, shader) 不再重复计一条——direct-draw 效果层会把同一个 effect 实例化成 source + effect 两份材质，
    /// 旧的 role=="effect" 白名单只挡住其中一份，另一份就成了假阳性。
    /// </summary>
    private static void AddRuntimeMaterialTemporalUnresolved(JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds,
        JsonArray unresolved, IReadOnlySet<(int OwnerLayerId, string Shader)> ruledMaterials)
    {
        if (runtime["runtime_layers"] is not JsonArray layers) return;
        var selected = new HashSet<int>(bakedLayerIds);
        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            int? owner = HybridScenePlanner.Int(layer["owner"]);
            if (owner is null || !selected.Contains(owner.Value) || layer["materials"] is not JsonArray materials) continue;
            foreach (JsonNode? node in materials)
            {
                if (node is not JsonObject material) continue;
                if (material["role"] is JsonNode roleNode && (roleNode is not JsonValue roleValue || !roleValue.TryGetValue<string>(out _)))
                {
                    AddRuntimeMaterialUnresolved(new Message("unresolved.material_role_not_string").Write(
                        new JsonObject { ["kind"] = "runtime_material", ["owner_layer_id"] = owner.Value }, "detail"), unresolved);
                    continue;
                }
                string? role = material["role"]?.GetValue<string>();
                // 键必须带层 id：同名 shader 在别的层上是另一套常量，那边的裁定不能借到这里来。
                if (material["shader"] is JsonValue shaderValue && shaderValue.TryGetValue(out string? shaderName) &&
                    shaderName is not null && ruledMaterials.Contains((owner.Value, shaderName))) continue;
                if (material["active_uniforms"] is not JsonArray uniforms)
                {
                    if (role is not null) AddRuntimeMaterialUnresolved(new Message("unresolved.material_omits_active_uniforms").Write(
                        new JsonObject { ["kind"] = "runtime_material", ["owner_layer_id"] = owner.Value }, "detail"), unresolved);
                    continue;
                }
                if (uniforms.Any(uniform => uniform is not JsonValue value || !value.TryGetValue<string>(out _)))
                {
                    AddRuntimeMaterialUnresolved(new Message("unresolved.material_uniforms_invalid").Write(
                        new JsonObject { ["kind"] = "runtime_material", ["owner_layer_id"] = owner.Value }, "detail"), unresolved);
                    continue;
                }
                string[] clocks = uniforms.OfType<JsonValue>().Select(value => value.GetValue<string>())
                    .Where(IsRuntimeClock).Distinct(StringComparer.Ordinal).ToArray();
                if (clocks.Length != 0)
                    AddRuntimeMaterialUnresolved(new Message("unresolved.material_temporal_uniforms", [role ?? "unknown-role", string.Join(", ", clocks)])
                        .Write(new JsonObject { ["kind"] = "runtime_material", ["owner_layer_id"] = owner.Value }, "detail"), unresolved);
            }
        }
    }

    private static bool IsRuntimeClock(string uniform) => uniform is "g_Time" or "g_Runtime" or "g_Frametime" or "g_DeltaTime";

    /// <summary>
    /// 数出要进视频的图层上、非 effect 运行时材质里出现的时钟 uniform 条数（每个材质内去重）。
    /// 这个计数只进裁定理由供用户核对，不参与任何判定。
    /// </summary>
    private static int CountRuntimeClockUniforms(JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds)
    {
        if (runtime["runtime_layers"] is not JsonArray layers) return 0;
        var selected = new HashSet<int>(bakedLayerIds);
        int count = 0;
        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            int? owner = HybridScenePlanner.Int(layer["owner"]);
            if (owner is null || !selected.Contains(owner.Value) || layer["materials"] is not JsonArray materials) continue;
            foreach (JsonObject material in materials.OfType<JsonObject>())
            {
                if (material["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out string? role) &&
                    string.Equals(role, "effect", StringComparison.Ordinal)) continue;
                if (material["active_uniforms"] is not JsonArray uniforms) continue;
                count += uniforms.OfType<JsonValue>()
                    .Where(value => value.TryGetValue<string>(out string? name) && IsRuntimeClock(name))
                    .Select(value => value.GetValue<string>()).Distinct(StringComparer.Ordinal).Count();
            }
        }
        return count;
    }

    /// <summary>追加一条运行时材质未解析项；同一条（整条结构相等）已在就不重复加。</summary>
    private static void AddRuntimeMaterialUnresolved(JsonObject item, JsonArray unresolved)
    {
        if (!unresolved.Any(existing => JsonNode.DeepEquals(existing, item))) unresolved.Add(item);
    }

    private static readonly Regex TextureAnimationCall = new(@"getTextureAnimation\s*\(\s*(?:['""]([^'""]*)['""]\s*)?\)", RegexOptions.CultureInvariant);
    private static readonly Regex PlaybackControlCall = new(
        @"(?:\.\s*(?:play|pause|stop|setFrame|join)\b|\[\s*['""](?:play|pause|stop|setFrame|join)['""]\s*\]|\.\s*rate\s*[+\-*/%]?=(?!=))", RegexOptions.CultureInvariant);
    private static readonly Regex RandomCall = new(@"\bMath\s*\.\s*random\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex TimerCall = new(@"\bset(?:Timeout|Interval)\s*\(", RegexOptions.CultureInvariant);

    private static bool HasSpritePlaybackControl(JsonObject owner) => SceneAnalyzer.Walk(owner).OfType<JsonObject>()
        .Any(binding => binding["script"] is JsonValue script && script.TryGetValue<string>(out string? code) &&
            code.Contains("getTextureAnimation", StringComparison.Ordinal) && PlaybackControlCall.IsMatch(code));

    /// <summary>
    /// 随机重启比一般的脚本控制说得更强：精灵在任何采集长度下都没有周期。证明必须在同一段脚本里闭合——
    /// 取到这条纹理动画（无参调用即本层默认动画；带名字则必须与轨道同名）、对它做播放控制、并用 Math.random
    /// 喂给定时器。三者缺一都只是"脚本控制"，不是随机证明；脚本别处的 Math.random（颜色、位置）不算。
    /// </summary>
    internal static bool HasRandomSpriteRestart(JsonObject owner, string? trackName) => SceneAnalyzer.Walk(owner).OfType<JsonObject>()
        .Any(binding => binding["script"] is JsonValue script && script.TryGetValue<string>(out string? code) &&
            TextureAnimationCall.Matches(code).Any(match => !match.Groups[1].Success || string.IsNullOrWhiteSpace(trackName) ||
                string.Equals(match.Groups[1].Value, trackName, StringComparison.Ordinal)) &&
            PlaybackControlCall.IsMatch(code) && RandomCall.IsMatch(code) && TimerCall.IsMatch(code));

    private static CommonLoopRational Divide(CommonLoopRational left, CommonLoopRational right)
    {
        if ((Int128)left.Numerator * right.Denominator > long.MaxValue || (Int128)left.Denominator * right.Numerator > long.MaxValue) throw new ArgumentOutOfRangeException();
        return new CommonLoopRational(checked(left.Numerator * right.Denominator), checked(left.Denominator * right.Numerator));
    }

    private static bool Float32RateEquals(JsonNode? sourceRate, CommonLoopRational runtimeRate) => sourceRate is JsonValue value &&
        value.TryGetValue<double>(out double authored) && double.IsFinite(authored) &&
        float.IsFinite((float)authored) && float.IsFinite((float)runtimeRate.ToSeconds()) &&
        (float)authored == (float)runtimeRate.ToSeconds();

    private static bool TryFiniteRate(JsonNode? sourceRate, out double rate)
    {
        rate = 0;
        return sourceRate is JsonValue value && value.TryGetValue<double>(out rate) && double.IsFinite(rate);
    }

    private static CommonLoopSearchResult SuggestSingleVideoRetime(uint fpsNumerator, uint fpsDenominator,
        AnimationInfo video, double maximumRetimePercent, CommonLoopRational ceiling, CommonLoopPreference preference = CommonLoopPreference.Balanced)
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

    private static bool TryExactVideoDuration(JsonObject trace, out CommonLoopRational duration)
    {
        duration = default;
        if (!TryPositiveInteger(trace["duration_numerator"], out long numerator) ||
            !TryPositiveInteger(trace["duration_denominator"], out long denominator)) return false;
        try { duration = new CommonLoopRational(numerator, denominator); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>片源自身的原生帧率 = 帧数 / 时长。运行时探针已记录 frame_count，这里只是把它变成精确有理数。</summary>
    private static bool TryClipFrameRate(JsonObject trace, CommonLoopRational duration, out CommonLoopRational rate)
    {
        rate = default;
        if (!TryPositiveInteger(trace["frame_count"], out long frames)) return false;
        try
        {
            rate = new CommonLoopRational(checked(frames * duration.Denominator), duration.Numerator);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    /// <summary>
    /// 一个内容帧占多少个输出帧。只有当捕获里全部时间机制都是定速片源、而且每段片源的原生帧率都整除输出帧率时，
    /// 相邻输出帧才确实是同一张内容帧的复制；任何着色器时钟、非视频动画或未解析项都让它退回 1。
    /// </summary>
    private static long ContentStepFrames(int shaderComponentCount, List<AnimationInfo> animation, int unresolvedCount,
        uint fpsNumerator, uint fpsDenominator)
    {
        if (shaderComponentCount != 0 || unresolvedCount != 0 || animation.Count == 0) return 1;
        UInt128 step = 0;
        foreach (AnimationInfo clip in animation)
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

    private static bool TryPositiveInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue json) return false;
        if (json.TryGetValue<long>(out value)) return value > 0;
        if (json.TryGetValue<int>(out int integer)) { value = integer; return value > 0; }
        return false;
    }

    /// <summary>
    /// 四条并列判据，命中任一即受控：运行时对该轨的非初始化播放属性写入（A1）、
    /// 脚本已把这条轨的 videoTexture 拿到手或自身脚本控制自身（A2/A3，见 <see cref="VideoControlScope"/>），
    /// 以及目标解析不出来时退回全场景连坐（A4）。
    /// </summary>
    private static bool VideoPlaybackIsControlled(JsonObject trace, JsonObject runtime, int ownerId, VideoControlScope videoControlScope)
    {
        if (trace["dynamic_controlled"] is JsonValue dynamic && (!dynamic.TryGetValue<bool>(out bool controlled) || controlled)) return true;
        bool observed = runtime["runtime_dependencies"]?.AsArray().OfType<JsonObject>().Any(dependency =>
            dependency["target"]?.GetValue<int>() == ownerId && dependency["operation"]?.GetValue<string>() == "write" &&
            dependency["initialization"]?.GetValue<bool>() != true && IsVideoPlaybackProperty(dependency["property"]?.GetValue<string>())) == true;
        return observed || videoControlScope.Controls(ownerId);
    }

    private static bool IsVideoPlaybackProperty(string? property) => property is not null &&
        (property.Equals("video", StringComparison.OrdinalIgnoreCase) || property.Equals("videoPlayback", StringComparison.OrdinalIgnoreCase) ||
         property.Equals("video_texture", StringComparison.OrdinalIgnoreCase));

    private static bool TryRational(string token, out CommonLoopRational value)
    {
        value = default;
        if (!decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number) || number <= 0) return false;
        int scale = (decimal.GetBits(number)[3] >> 16) & 0x7f; decimal factor = 1;
        for (int i = 0; i < scale; i++) factor *= 10;
        decimal numerator = number * factor;
        if (numerator != decimal.Truncate(numerator) || numerator > long.MaxValue || factor > long.MaxValue) return false;
        value = new((long)numerator, (long)factor); return true;
    }

    private static void Verify(JsonNode? node, double expected)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out double actual) || Math.Abs(actual - expected) > Math.Max(1e-10, Math.Abs(expected) * 1e-10))
            throw new InvalidDataException("Capture scene changed since its loop patch was analyzed.");
    }

    private static double BaseShaderValue(ShaderPeriodComponent component)
    {
        CommonLoopPeriod period = component.Component.BasePeriod!;
        if (component.Component.AllowRetime || period.ExactSeconds is null) return component.Patch.OldValue;
        double magnitude = (double)period.ExactSeconds.Value.Denominator / period.ExactSeconds.Value.Numerator;
        return Math.CopySign(magnitude, component.Patch.OldValue);
    }

    internal static double ShaderPatchValue(double baseSpeed, double speedMultiplier, double speedExponent)
    {
        if (!double.IsFinite(speedMultiplier) || speedMultiplier <= 0 || !double.IsFinite(speedExponent) ||
            speedExponent == 0 || speedExponent < 0 && speedExponent != -1)
            throw new InvalidDataException("Shader patch exponent must be positive or the supported inverse exponent -1.");
        return baseSpeed * Math.Pow(speedMultiplier, 1 / speedExponent);
    }

    private static JsonNode CycleJson(CommonLoopComponentCycle x) => new JsonObject { ["id"] = x.ComponentId, ["cycles"] = x.Cycles,
        ["old_period_seconds"] = x.OldPeriodSeconds, ["new_period_seconds"] = x.NewPeriodSeconds, ["speed_multiplier"] = x.SpeedMultiplier, ["delta_percent"] = x.DeltaPercent };
    private static JsonNode PatchJson(Patch x) => new JsonObject { ["component"] = x.ComponentId, ["kind"] = x.Kind, ["owner_layer_id"] = x.OwnerLayerId,
        ["effect_index"] = x.EffectIndex, ["pass_index"] = x.PassIndex, ["constant_key"] = x.ConstantKey, ["value_index"] = x.ValueIndex,
        ["animation_layer_id"] = x.AnimationLayerId, ["old_value"] = x.OldValue, ["new_value"] = x.NewValue,
        ["speed_exponent"] = x.SpeedExponent, ["delta_percent"] = 100 * (x.NewValue / x.OldValue - 1) };
    private static JsonNode VideoPatchJson(AnimationInfo video, CommonLoopRational rate) => new JsonObject {
        ["component"] = video.LockedComponent.Id, ["kind"] = "video_rate", ["owner_layer_id"] = video.OwnerLayerId,
        ["rate_numerator"] = rate.Numerator, ["rate_denominator"] = rate.Denominator,
        ["old_value"] = 1, ["new_value"] = rate.ToSeconds(), ["delta_percent"] = 100 * (rate.ToSeconds() - 1)
    };
    private static JsonNode UnresolvedJson(ShaderTemporalUnresolved x)
    {
        var json = new JsonObject { ["kind"] = x.Kind.ToString(), ["owner_layer_id"] = x.OwnerLayerId,
        ["effect_index"] = x.EffectIndex, ["pass_index"] = x.PassIndex, ["resource"] = x.Resource, ["detail"] = x.Detail,
        // 机制知识按结构化字段下传，残差掩盖据此判定，不再按资源名匹配字样。
        ["bounded_displacement"] = x.BoundedDisplacement, ["mechanism"] = x.Mechanism.Length == 0 ? null : x.Mechanism };
        return x.Message?.Write(json, "detail") ?? json;
    }
    private sealed record Patch(string ComponentId, string Kind, int OwnerLayerId, int EffectIndex, int PassIndex, string ConstantKey, int ValueIndex, int? AnimationLayerId, double OldValue, double NewValue, double SpeedExponent = 1);
    private sealed record AnimationInfo(int OwnerLayerId, string TrackName, int[] AnimationLayerIds, double[] AuthoredRates, bool CanRetime, CommonLoopComponent LockedComponent, CommonLoopComponent RetimableComponent, bool IsVideo, CommonLoopRational? ClipFrameRate = null, float[]? SpriteFrameTimes = null);

    /// <summary>
    /// 读精灵纹理的 float32 帧表（先项目内 materials/，再全局 assets/materials/，与渲染器 vfs 顺序一致）。
    /// 帧表总和必须与运行时 trace 的时长（std::to_string 的 6 位小数）一致，否则不是同一张纹理，不用。
    /// </summary>
    private static float[]? ReadSpriteFrameTimes(ProjectSource source, string? assetsDirectory, string? texture, CommonLoopRational traceDuration)
    {
        if (string.IsNullOrWhiteSpace(texture)) return null;
        try
        {
            string resource = $"materials/{texture}.tex";
            byte[]? data = source.Contains(resource) ? source.Read(resource, 256 * 1024 * 1024) : null;
            if (data is null && !string.IsNullOrWhiteSpace(assetsDirectory))
            {
                string path = Path.GetFullPath(Path.Combine(assetsDirectory, "materials", texture + ".tex"));
                if (path.StartsWith(Path.GetFullPath(assetsDirectory), StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    data = File.ReadAllBytes(path);
            }
            if (data is null || !SpriteSeamPhase.TryReadFrameTimes(data, out float[] times)) return null;
            double sum = times.Sum(x => (double)x);
            return Math.Abs(sum - traceDuration.ToSeconds()) <= 5.01e-7 ? times : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
