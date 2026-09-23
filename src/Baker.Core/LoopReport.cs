using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// plan.loop（循环报告 schema 1，随 plan v3 写出）的顶层字段。<see cref="ToJson"/> 按原顺序写出，与改动前逐字节相同：
/// no_candidate_reason 与 fixed_frame_step 缺值时写 null，sway_retime / loop_length_default / embedded_video_limit 缺值时不写。
/// candidates 与 unresolved 在分析过程中是 <see cref="LoopCandidate"/> / <see cref="LoopUnresolved"/>，只在这里渲染一次。
/// </summary>
internal sealed record LoopReport(uint FpsNum, uint FpsDen, string RetimeMode, CommonLoopPreference LoopPreference,
    double RetimeBudgetPercent, bool BudgetRelaxed, ulong? FixedFrameStep, double MaximumSeconds,
    LoopNoCandidateReason? NoCandidateReason, IReadOnlyList<LoopCandidate> Candidates, IReadOnlyList<LoopUnresolved> Unresolved,
    bool SourceStatic, VideoControlScope VideoControlScope, LoopContentCadence ContentCadence, IReadOnlyList<ShaderPeriodComponent> Evidence,
    JsonObject? SwayRetime, JsonObject? LoopLengthDefault, EmbeddedVideoLoopLimit? EmbeddedVideoLimit)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject {
            ["schema_version"] = 1, ["status"] = Candidates.Count == 0 ? "no_analytic_candidate" : "analytic_candidate_requires_seam_validation",
            ["fps_num"] = FpsNum, ["fps_den"] = FpsDen, ["retime_mode"] = RetimeMode,
            ["loop_preference"] = LoopPreference.ToString().ToLowerInvariant(), ["retime_budget_percent"] = RetimeBudgetPercent,
            ["budget_relaxed"] = BudgetRelaxed, ["fixed_frame_step"] = FixedFrameStep, ["maximum_seconds"] = MaximumSeconds,
            ["no_candidate_reason"] = NoCandidateReason?.ToJson(),
            ["candidates"] = new JsonArray(Candidates.Select(x => (JsonNode)x.ToJson()).ToArray()),
            ["unresolved"] = new JsonArray(Unresolved.Select(x => (JsonNode)x.ToJson()).ToArray()), ["source_static"] = SourceStatic,
            ["video_control_scope"] = VideoControlScope.ToJson(), ["content_cadence"] = ContentCadence.ToJson(),
            ["evidence"] = new JsonArray(Evidence.Select(x => (JsonNode)new JsonObject { ["component"] = x.Component.Id, ["detail"] = x.Evidence }).ToArray()),
            ["visual_seam"] = "not_verified", ["encoded_loop"] = "not_verified"
        };
        if (SwayRetime is not null) json["sway_retime"] = SwayRetime;
        if (LoopLengthDefault is not null) json["loop_length_default"] = LoopLengthDefault;
        // 上限被内嵌视频 2 GiB 收紧时才写（maximum_seconds 已是收紧后的值）；没收紧时 plan 不变。
        if (EmbeddedVideoLimit is { Applied: true }) json["embedded_video_limit"] = EmbeddedVideoLimit.ToJson();
        return json;
    }
}

/// <summary>plan.loop.no_candidate_reason：求解器没给出候选时的原因与计数。</summary>
internal sealed record LoopNoCandidateReason(CommonLoopNoCandidate Reason, int ShaderComponentCount, int RuntimePeriodCount,
    int ParticleCycleCount, int RuntimeClockUniformCount)
{
    public JsonObject ToJson() => new() {
        ["kind"] = Reason.Kind.ToString(), ["ceiling_seconds"] = Reason.CeilingSeconds, ["fixed_period_seconds"] = Reason.FixedPeriodSeconds,
        ["shader_component_count"] = ShaderComponentCount, ["runtime_period_count"] = RuntimePeriodCount,
        ["particle_cycle_count"] = ParticleCycleCount, ["runtime_clock_uniform_count"] = RuntimeClockUniformCount };
}

/// <summary>plan.loop.content_cadence：每个不同内容帧在捕获里重复几帧（定速视频片段整除输出帧率时大于 1）。</summary>
internal sealed record LoopContentCadence(long CaptureFramesPerContentFrame, IReadOnlyList<LoopCadenceClip> Clips)
{
    public JsonObject ToJson() => new()
    {
        ["capture_frames_per_content_frame"] = CaptureFramesPerContentFrame,
        ["basis"] = CaptureFramesPerContentFrame > 1
            ? "Every temporal mechanism here is a fixed-rate clip whose own frame rate divides the output rate, so the capture repeats each distinct content frame this many times. Seam checks read ordinary steps across one content frame instead of across duplicates."
            : "No proven clip cadence covers this capture, so every output frame is treated as a distinct content frame.",
        ["clips"] = new JsonArray(Clips.Select(x => (JsonNode)new JsonObject
        {
            ["component"] = x.Component, ["owner_layer_id"] = x.OwnerLayerId, ["track_name"] = x.TrackName,
            ["clip_fps_numerator"] = x.ClipFrameRate?.Numerator, ["clip_fps_denominator"] = x.ClipFrameRate?.Denominator
        }).ToArray())
    };
}

internal sealed record LoopCadenceClip(string Component, int OwnerLayerId, string TrackName, CommonLoopRational? ClipFrameRate);

/// <summary>
/// plan.loop.candidates[] 的一项。分析过程中按字段读写（摆动改频读 Frames 与 Components、换算圈数，精灵接缝换判定），
/// 写 plan 时由 <see cref="ToJson"/> 渲染一次，键序与 v3 相同：frames, seconds, total_retime_cost_percent, components, patches,
/// [source_period_warmup_frames], [sprite_seam_phase], [loop_length_source], [sway_retime]。
/// </summary>
internal sealed record LoopCandidate(ulong Frames, double Seconds, double TotalRetimeCostPercent,
    IReadOnlyList<CommonLoopComponentCycle> Components, IReadOnlyList<LoopPatch> Patches)
{
    /// <summary>精灵 float32 帧表的接缝判定；null = 这次捕获没有可读帧表的精灵轨道。</summary>
    public SpriteSeamPhase.Selection? SpriteSeam { get; init; }
    /// <summary>接缝是在摆动改频后的 L = kP 上重判的（basis 说明不同）。</summary>
    public bool SpriteSeamOnSwayLength { get; init; }
    /// <summary>循环长度不来自周期求解时的来源（目前只有 stationary_particle_default）。</summary>
    public string? LoopLengthSource { get; init; }
    public CandidateSwayRetime? SwayRetime { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject {
            ["frames"] = Frames, ["seconds"] = Seconds, ["total_retime_cost_percent"] = TotalRetimeCostPercent,
            ["components"] = new JsonArray(Components.Select(x => (JsonNode)new JsonObject { ["id"] = x.ComponentId, ["cycles"] = x.Cycles,
                ["old_period_seconds"] = x.OldPeriodSeconds, ["new_period_seconds"] = x.NewPeriodSeconds,
                ["speed_multiplier"] = x.SpeedMultiplier, ["delta_percent"] = x.DeltaPercent }).ToArray()),
            ["patches"] = new JsonArray(Patches.Select(x => (JsonNode)x.ToJson()).ToArray())
        };
        if (SpriteSeam is { WarmupFrames: ulong warmup and > 0 } sprite)
        {
            json["source_period_warmup_frames"] = warmup;
            json["sprite_seam_phase"] = new JsonObject { ["origin"] = sprite.AtOrigin.ToString().ToLowerInvariant(),
                ["after_one_period"] = sprite.AfterOnePeriod?.ToString().ToLowerInvariant(),
                ["basis"] = SpriteSeamOnSwayLength ? "float32 sprite frame table re-checked on the sway-retimed loop length"
                    : "float32 sprite frame table; frame 0 sits on the sprite frame-0 start boundary" };
        }
        else if (SpriteSeam?.AtOrigin == SpriteSeamPhase.Verdict.Undetermined)
            json["sprite_seam_phase"] = new JsonObject { ["origin"] = "undetermined",
                ["basis"] = "a sample lies within the renderer's double accumulation error of a sprite boundary" };
        if (LoopLengthSource is not null) json["loop_length_source"] = LoopLengthSource;
        if (SwayRetime is not null) json["sway_retime"] = SwayRecurrenceSolver.ToJson(SwayRetime.Solution,
            SwayRetime.LoopLengthMaximumSeconds, SwayRetime.FpsNumerator, SwayRetime.FpsDenominator, SwayRetime.Profile);
        return json;
    }
}

/// <summary>候选上的摆动改频解（bake 按它写覆盖 shader）；渲染参数与 loop.sway_retime 记录同一份请求。</summary>
internal sealed record CandidateSwayRetime(SwayRetimeSolution Solution, double LoopLengthMaximumSeconds, uint FpsNumerator,
    uint FpsDenominator, RetimeProfile? Profile);

/// <summary>候选里的一条改写（capture 场景按它改速度常量、动画 rate 或视频 rate）。</summary>
internal abstract record LoopPatch
{
    public abstract JsonObject ToJson();
}

/// <summary>shader_speed / shader_phase / animation_rate：改一个标量，old_value → new_value。</summary>
internal sealed record LoopValuePatch(string ComponentId, string Kind, int OwnerLayerId, int EffectIndex, int PassIndex,
    string ConstantKey, int ValueIndex, int? AnimationLayerId, double OldValue, double NewValue, double SpeedExponent = 1) : LoopPatch
{
    public override JsonObject ToJson() => new() { ["component"] = ComponentId, ["kind"] = Kind, ["owner_layer_id"] = OwnerLayerId,
        ["effect_index"] = EffectIndex, ["pass_index"] = PassIndex, ["constant_key"] = ConstantKey, ["value_index"] = ValueIndex,
        ["animation_layer_id"] = AnimationLayerId, ["old_value"] = OldValue, ["new_value"] = NewValue,
        ["speed_exponent"] = SpeedExponent, ["delta_percent"] = 100 * (NewValue / OldValue - 1) };
}

/// <summary>video_rate：视频片段按精确有理倍率调速。</summary>
internal sealed record LoopVideoRatePatch(string ComponentId, int OwnerLayerId, CommonLoopRational Rate) : LoopPatch
{
    public override JsonObject ToJson() => new() {
        ["component"] = ComponentId, ["kind"] = "video_rate", ["owner_layer_id"] = OwnerLayerId,
        ["rate_numerator"] = Rate.Numerator, ["rate_denominator"] = Rate.Denominator,
        ["old_value"] = 1, ["new_value"] = Rate.ToSeconds(), ["delta_percent"] = 100 * (Rate.ToSeconds() - 1) };
}

/// <summary>
/// plan.loop.unresolved[] 的一项：证明不了周期的时间机制。分析过程中按类型读（粒子默认长度读 <see cref="RuntimeTrackUnresolved.Particle"/>，
/// 摆动改频按 <see cref="ShaderLoopUnresolved.Source"/> 移出已建模项），写 plan 时渲染一次。带文案键的条目照旧写 detail 与
/// detail_localized（后者是临时字段：plan.loop 要经 AnalysisCache 与 HybridScenePlanner 才到 PlanNarrative.Attach，由它搬进 unresolved_localized）。
/// </summary>
internal abstract record LoopUnresolved
{
    public abstract string Kind { get; }
    public abstract JsonObject ToJson();
}

/// <summary>着色器源码分析留下的未解析项。</summary>
internal sealed record ShaderLoopUnresolved(ShaderTemporalUnresolved Source) : LoopUnresolved
{
    public override string Kind => Source.Kind.ToString();
    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = Kind, ["owner_layer_id"] = Source.OwnerLayerId,
            ["effect_index"] = Source.EffectIndex, ["pass_index"] = Source.PassIndex, ["resource"] = Source.Resource, ["detail"] = Source.Detail,
            // 机制知识按结构化字段下传，残差掩盖据此判定，不再按资源名匹配字样。
            ["bounded_displacement"] = Source.BoundedDisplacement, ["mechanism"] = Source.Mechanism.Length == 0 ? null : Source.Mechanism };
        return Source.Message?.Write(json, "detail") ?? json;
    }
}

/// <summary>被烘图层上作者脚本读时钟（非初始化）：模型/着色器周期证明不了脚本推进的状态。binding/clock 原样取自运行时依赖。</summary>
internal sealed record ScriptTimeUnresolved(int OwnerLayerId, JsonNode? Binding, JsonNode? Clock) : LoopUnresolved
{
    public override string Kind => "script_time";
    public override JsonObject ToJson() => new Message("unresolved.script_time").Write(new JsonObject {
        ["kind"] = Kind, ["owner_layer_id"] = OwnerLayerId, ["binding"] = Binding?.DeepClone(), ["clock"] = Clock?.DeepClone() }, "detail");
}

/// <summary>运行时材质里没被建模的时钟 uniform 或读不懂的材质条目。</summary>
internal sealed record RuntimeMaterialUnresolved(int OwnerLayerId, Message Detail) : LoopUnresolved
{
    public override string Kind => "runtime_material";
    public override JsonObject ToJson() => Detail.Write(new JsonObject { ["kind"] = Kind, ["owner_layer_id"] = OwnerLayerId }, "detail");
}

/// <summary>求解器留下的约束（search_budget = 搜索预算用尽，没有 component 键）。</summary>
internal sealed record SolverUnresolved(bool SearchBudget, string? ComponentId, string? Detail) : LoopUnresolved
{
    public override string Kind => SearchBudget ? "search_budget" : "solver";
    public override JsonObject ToJson() => SearchBudget
        ? new JsonObject { ["kind"] = Kind, ["detail"] = Detail }
        : new JsonObject { ["kind"] = Kind, ["component"] = ComponentId, ["detail"] = Detail };
}

/// <summary>
/// 静止证明失败的理由。<see cref="Layer"/> 点名被烘图层时另写 static_layer 临时字段（层名与是否粒子系统），
/// PlanNarrative 的一行中文结论据此说，不从英文明细里抠；Attach 之后去掉。
/// </summary>
internal sealed record SourceStaticUnresolved(string Detail, int? OwnerLayerId, StaticLayerNaming? Layer) : LoopUnresolved
{
    public override string Kind => "source_static";
    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = Kind, ["detail"] = Detail, ["owner_layer_id"] = OwnerLayerId };
        if (Layer is not null) json[PlanNarrative.StaticLayer] = new JsonObject { ["name"] = Layer.Name, ["particle"] = Layer.Particle };
        return json;
    }
}

internal sealed record StaticLayerNaming(string? Name, bool Particle);

/// <summary>精灵 float32 接缝在全部候选上都对不齐，被丢掉的候选数。</summary>
internal sealed record SpriteSeamUnresolved(int RejectedCandidateCount) : LoopUnresolved
{
    public override string Kind => "sprite_float32_seam";
    public override JsonObject ToJson() => new Message("unresolved.sprite_float32_seam_mismatch").Write(
        new JsonObject { ["kind"] = Kind, ["rejected_candidate_count"] = RejectedCandidateCount }, "detail");
}

/// <summary>
/// 运行时轨道（动画、视频、粒子）证明不了周期。键序：kind, owner_layer_id, [track_name], [particle_nonperiodic_reason],
/// [mechanism], [particle_stationarity], [random_restart], detail, detail_localized。
/// <see cref="Particle"/> 是粒子判据结论：粒子默认循环长度读它，锁定周期退回拒绝时整条换新。
/// </summary>
internal sealed record RuntimeTrackUnresolved(bool Video, int OwnerLayerId, Message Detail) : LoopUnresolved
{
    public override string Kind => Video ? "runtime_video" : "runtime_animation";
    /// <summary>写不写 track_name 键（值可以是 null）。</summary>
    public bool HasTrackName { get; init; }
    public string? TrackName { get; init; }
    public string? ParticleNonperiodicReason { get; init; }
    public string? Mechanism { get; init; }
    public ParticleStationarity.Result? Particle { get; init; }
    /// <summary>脚本控制条目上的随机重启证明（残差掩盖读 plan 里的这个字段）；null = 不是脚本控制条目。</summary>
    public bool? RandomRestart { get; init; }

    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = Kind, ["owner_layer_id"] = OwnerLayerId };
        if (HasTrackName) json["track_name"] = TrackName;
        if (ParticleNonperiodicReason is not null) json["particle_nonperiodic_reason"] = ParticleNonperiodicReason;
        if (Mechanism is not null) json["mechanism"] = Mechanism;
        if (Particle is not null) json["particle_stationarity"] = Particle.ToJson();
        if (RandomRestart is bool random) json["random_restart"] = random;
        return Detail.Write(json, "detail");
    }
}
