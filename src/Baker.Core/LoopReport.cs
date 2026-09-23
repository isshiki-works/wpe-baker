using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// plan.loop（循环报告 schema 1，随 plan v3 写出）的顶层字段。<see cref="ToJson"/> 按原顺序写出，与改动前逐字节相同：
/// no_candidate_reason 与 fixed_frame_step 缺值时写 null，sway_retime / loop_length_default / embedded_video_limit 缺值时不写。
/// candidates 与 unresolved 的元素仍是 JsonObject（下游 planner 与 bake 按键读取并改写），类型化留给 C2。
/// </summary>
internal sealed record LoopReport(uint FpsNum, uint FpsDen, string RetimeMode, CommonLoopPreference LoopPreference,
    double RetimeBudgetPercent, bool BudgetRelaxed, ulong? FixedFrameStep, double MaximumSeconds,
    LoopNoCandidateReason? NoCandidateReason, JsonArray Candidates, JsonArray Unresolved, bool SourceStatic,
    VideoControlScope VideoControlScope, LoopContentCadence ContentCadence, IReadOnlyList<ShaderPeriodComponent> Evidence,
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
            ["candidates"] = Candidates, ["unresolved"] = Unresolved, ["source_static"] = SourceStatic,
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
