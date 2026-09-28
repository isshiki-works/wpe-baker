using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 默认拒绝有明确低价值证据的静态成品、或超过现有视频路数政策的方案；命令行 --no-benefit allow 可覆盖。
/// pass 覆盖/视频路数只作风险线索，不能证明视频解码比省下的渲染更费电：编码会裁剪，透明度与帧率也影响工作量。
/// 视频流路数：特效前缀路线每个缓存都编成一路视频，路数就是 effect_prefix_caches 的个数；
/// 整层路线按已证动态的组数（<see cref="Admission.DynamicGroupCount"/>）判——没证出静态的组数越线时，分析按渲染证据证出哪些组是动态的
/// （<see cref="SlowClosureProbe.StreamsAsync"/>，限时），证出越线就在分析里把省下渲染最少的越线几组留实时再分析；没证出的不当拒因。烘焙时仍按实际编出的视频流复核，不再退回重烘。
/// 判据只说"预计"：不是功耗实测，覆盖后照常烘焙。
/// </summary>
public static class NoBenefit
{
    public const string RejectChoice = "reject";
    public const string AllowChoice = "allow";

    /// <summary>plan 顶层字段：{ policy, status, conditions[] }，命中判据时才写。界面读 status。</summary>
    public const string Field = "no_benefit";

    public const string ExpectedStatus = "expected_no_benefit";
    public const string OverrideStatus = "override_accepted";
    /// <summary>只差采集能力、假设能采集也不命中判据：留在能力缺口，标给主线程排期实现采集。</summary>
    public const string CaptureOpenStatus = "benefit_if_captured";
    internal const string ReplannedConditionsField = "replanned_no_benefit_conditions";

    public const string RejectionReason = "no_benefit_expected";
    public const string RejectedBakeStatus = "candidate_rejected_no_benefit_expected";

    /// <summary>旧政策"2–4 路省电"；更多路的成品比原作费电的居多（9/18 同批实测 5、6、9 路），默认不生成。</summary>
    public const int SavingProvenStreams = 4;

    public const string StaticWithLive = "static_only_with_live_layers";
    public const string TooManyStreams = "video_streams_over_limit";

    /// <summary>历史实测启发的低覆盖风险线索，不是视频成本下界或硬拒阈值。</summary>
    public const double MinPassCoveragePerStream = 1.0;

    public const string PlainLayersOnly = "only_plain_layers_baked";
    public const string VideoCostOverSaving = "video_cost_over_saved_rendering";

    /// <summary>分析时可写的拒因：静态图仍带实时层且证出低价值；或超过现有视频路数政策。</summary>
    public static string[] AnalysisConditions(JsonObject plan)
    {
        var hits = new List<string>();
        double? frames = plan["loop"]?["candidates"] is JsonArray { Count: > 0 } candidates ? StaticOnlyBake.Count(candidates[0]?["frames"]) : null;
        if (frames is <= 1 && plan["live_layer_ids"] is JsonArray { Count: > 0 } && BakeValueAssessment.IsLowValue(plan))
            hits.Add(StaticWithLive);
        if (plan["route"]?.GetValue<string>() == "effect_prefix" && plan["effect_prefix_caches"] is JsonArray caches && TooManyVideoStreams(caches.Count) ||
            TooManyVideoStreams(Admission.DynamicGroupCount(plan)))
            hits.Add(TooManyStreams);
        return hits.ToArray();
    }

    /// <summary>只作风险线索：pass 覆盖/视频组数没有考虑裁剪、透明打包、帧率、绘制与粒子模拟，不能作拒因。</summary>
    internal static bool UncomparedVideoCost(JsonObject plan)
    {
        if (plan["route"]?.GetValue<string>() != "whole_layer" || DecodeWorkCounts(plan) ||
            plan["loop"]?["candidates"] is not JsonArray { Count: > 0 } candidates ||
            !(StaticOnlyBake.Count(candidates[0]?["frames"]) > 1) ||
            RemovedPassCoverage(plan) is not double removed) return false;
        int upper = Admission.GroupCount(plan);
        return upper > 0 && removed < upper * MinPassCoveragePerStream;
    }

    /// <summary>
    /// 只差采集能力（<see cref="Verdict.IsCaptureGap"/>）的方案按"假设能采集"照常预判：拒因只剩这类时读 plan 自身（bake_value 不看这类拒因）；
    /// 更小分配重查只差采集能力时读重查记下的条件。都不是返回 null。
    /// </summary>
    internal static string[]? CaptureOpenConditions(JsonObject plan)
    {
        BlockerCode[] codes = [.. PlanBlockers.Codes(plan)];
        if (codes.Length > 0 && codes.All(Verdict.IsCaptureGap)) return AnalysisConditions(plan);
        return plan["loop_allocation_fallback"]?[ReplannedConditionsField] is JsonArray replanned
            ? [.. replanned.Select(c => c!.GetValue<string>())] : null;
    }

    /// <summary>
    /// 只有普通图层的视频组（单个源材质、普通贴图着色器、不带光照、没有特效 pass；不画东西的节点层不算）
    /// 可试着留实时以少编一路视频。这只是路线候选；原图层绘制与新视频解码的净功耗仍需比较。返回 --retain-live 列表。
    /// 静态成品、已证静态的组不编视频，不在此列；全部组都是普通组时没有可退的分组。
    /// </summary>
    internal static async Task<int[]?> PlainGroupRetainRootsAsync(JsonObject plan, CancellationToken token)
    {
        if (plan["route"]?.GetValue<string>() != "whole_layer" || DecodeWorkCounts(plan) ||
            plan["loop"]?["candidates"] is not JsonArray { Count: > 0 } loops || !(StaticOnlyBake.Count(loops[0]?["frames"]) > 1) ||
            plan["video_groups"] is not JsonArray { Count: > 1 } groups ||
            plan["runtime_evidence"]?.GetValue<string>() is not string path || !File.Exists(path)) return null;
        Func<int, bool> plainLayer = PlainLayers(plan, JsonNode.Parse(await File.ReadAllTextAsync(path, token))?["runtime_layers"] as JsonArray ?? []);
        int[] roots = [.. groups.OfType<JsonObject>().Where(group => group["static_verified"]?.GetValue<bool>() != true &&
                (group["layer_ids"] as JsonArray ?? []).Select(SceneGraph.Int).All(id => id is int layer && plainLayer(layer)))
            .SelectMany(group => (group["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int)).OfType<int>()];
        bool allPlain = groups.OfType<JsonObject>().All(group => (group["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).All(id => id is int r && roots.Contains(r)));
        return roots.Length == 0 || allPlain ? null : FullFrameDemotion.RetainLiveCommandRoots(plan, roots);
    }

    /// <summary>被烘层省下的特效渲染（按画布占比加权的 pass 数）；bake_value 没算这一项的方案返回 null，不判。</summary>
    internal static double? RemovedPassCoverage(JsonObject plan) => plan[BakeValueAssessment.Field]?["rule"]?.GetValue<string>() switch
    {
        var rule when rule == WorkloadValue.CachedEffectPasses.Rule =>
            BakeValueAssessment.Number(plan[BakeValueAssessment.Field]!["evidence"]?["effect_pass_coverage"]) is double w && double.IsFinite(w) ? w : null,
        var rule when rule == WorkloadValue.NeedsWorkComparison.Rule => PlainOnly(plan, videoOnly: true) ? 0 : null,
        _ => null
    };

    internal static bool Plain(JsonObject layer) => layer["has_effect_layer"]?.GetValue<bool>() != true &&
        layer["materials"] is JsonArray { Count: 1 } materials && materials[0] is JsonObject source &&
        source["role"]?.GetValue<string>() == "source" &&
        source["shader"]?.GetValue<string>() is "genericimage2" or "genericimage3" or "genericimage4" or "flat" &&
        !(source["active_uniforms"] as JsonArray ?? []).Any(u => u?.GetValue<string>().StartsWith("g_Lights", StringComparison.Ordinal) == true);

    internal static Func<int, bool> PlainLayers(JsonObject plan, JsonArray runtimeLayers)
    {
        var drawn = runtimeLayers.OfType<JsonObject>().ToLookup(layer => SceneGraph.Int(layer["owner"]));
        var drawable = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>().Where(layer => SceneGraph.Int(layer["id"]) is int)
            .DistinctBy(layer => SceneGraph.Int(layer["id"])).ToDictionary(layer => SceneGraph.Int(layer["id"])!.Value,
                layer => layer["drawable"]?.GetValue<bool>() != false);
        return id => drawn[id].All(Plain) && (drawn[id].Any() || !drawable.GetValueOrDefault(id, true));
    }

    private static bool PlainOnly(JsonObject plan, bool videoOnly) =>
        plan[BakeValueAssessment.Field]?["rule"]?.GetValue<string>() == WorkloadValue.NeedsWorkComparison.Rule &&
        plan[BakeValueAssessment.Field]?["evidence"]?[BakeValueAssessment.PlainGroupsField] is JsonArray plain &&
        (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>().Where(group => !videoOnly || group["static_verified"]?.GetValue<bool>() != true)
            .ToArray() is { Length: > 0 } groups &&
        groups.All(group => plain.Any(id => JsonNode.DeepEquals(id, group["id"])));

    internal static bool DecodeWorkCounts(JsonObject plan) => plan["video_dominant"] is JsonObject dominant && dominant["decode_work"] is JsonObject decode &&
        (decode["status"]?.GetValue<string>() == WorkloadValue.DecodePotentialGain || dominant["shell_structure"]?.GetValue<bool>() != false);

    /// <summary>分析收尾：记下判定；命中且没有覆盖时写 blocker 拒绝（与 TooManyVideoGroups 同一写法）。只差采集能力的方案按假设能采集判。</summary>
    public static void Apply(JsonObject plan, bool allowed)
    {
        string[]? ifCaptured = CaptureOpenConditions(plan);
        string[] conditions = ifCaptured ?? AnalysisConditions(plan);
        if (conditions.Length == 0)
        {
            // 覆盖/组数是经验风险线索，不能据此拒绝；保留已识别特效与视频组范围，净收益仍为未知。
            if (UncomparedVideoCost(plan) && plan[BakeValueAssessment.Field] is JsonObject value)
            {
                value["status"] = WorkloadValue.UncertainVideoCost.Status;
                value["reason_zh"] = WorkloadValue.UncertainVideoCost.ReasonZh;
                value["reason_en"] = WorkloadValue.UncertainVideoCost.ReasonEn;
                JsonObject evidence = value["evidence"] as JsonObject ?? new JsonObject();
                if (value["evidence"] is not JsonObject) value["evidence"] = evidence;
                evidence["video_group_upper_bound"] = Admission.GroupCount(plan);
                evidence["dynamic_group_lower_bound"] = Admission.DynamicGroupCount(plan);
                evidence["workload_risk_basis"] = "Effect-pass coverage per potential video stream is a heuristic only; encoded extent, alpha, frame rate and non-effect work are not compared.";
            }
            // 没命中的方案不写拒绝记录；只差采集能力的标出来。
            if (ifCaptured is not null) plan[Field] = Record(RejectChoice, CaptureOpenStatus, []);
            return;
        }
        if (conditions is [TooManyStreams])
        {
            if (allowed) { plan[Field] = Record(AllowChoice, OverrideStatus, conditions); return; }
            PlanBlockers.Add(plan, new Blocker(BlockerCode.TooManyVideoGroups, [SavingProvenStreams]));
            plan["status"] = "requires_resolution";
            plan["preset_rejection_reason"] = "too_many_video_groups";
            plan["suitability"] = HybridSuitability.Verdict(plan);
            PlanNarrative.Attach(plan);
            return;
        }
        plan[Field] = Record(allowed ? AllowChoice : RejectChoice, allowed ? OverrideStatus : ExpectedStatus, conditions);
        if (allowed) return;
        PlanBlockers.Add(plan, new Blocker(BlockerCode.NoBenefitExpected, [Describe(conditions, english: true)], [Describe(conditions, english: false)]));
        plan["status"] = "requires_resolution";
        plan["preset_rejection_reason"] = RejectionReason;
        plan["suitability"] = HybridSuitability.Verdict(plan);
        PlanNarrative.Attach(plan);
    }

    /// <summary>plan 是否已被显式允许生成预计不省电的方案。读 settings 而不是 no_benefit 记录，烘焙期间重分析出的新 plan 也带着它。</summary>
    public static bool Allowed(JsonObject plan) => plan["settings"]?["allow_no_benefit"]?.GetValue<bool>() == true;

    /// <summary>烘焙时的视频流判据：实际编出的视频流（不含静态纹理）超过上限。</summary>
    public static bool TooManyVideoStreams(int videoLayers) => videoLayers > SavingProvenStreams;

    /// <summary>烘焙拒绝：写状态与两语言的原因，并记下命中的条件与停下时已编出的视频流数（越线就停，不知道总数）。</summary>
    public static void RejectStreams(JsonObject report, int videoLayers)
    {
        report["status"] = RejectedBakeStatus;
        JsonObject record = Record(RejectChoice, TooManyStreams, [TooManyStreams]);
        record["video_streams_encoded"] = videoLayers;
        report[Field] = record;
        new Message("bake.no_benefit_streams", [videoLayers, SavingProvenStreams]).Write(report, "reason");
    }

    public const string RetreatRootsField = "retreat_root_ids";

    internal static async Task<int[]?> StreamRetreatRootsAsync(JsonObject plan, JsonObject report, CancellationToken token)
    {
        if (plan["runtime_evidence"]?.GetValue<string>() is not string path) return null;
        var observed = (JsonNode.Parse(await File.ReadAllTextAsync(path, token))?["runtime_layers"] as JsonArray ?? [])
            .OfType<JsonObject>().ToLookup(layer => SceneGraph.Int(layer["owner"]));
        var staticIds = (report["groups"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(group => group["storage"]?.GetValue<string>() == "static_rgba")
            .Select(group => group["id"]?.GetValue<string>()).ToHashSet();
        int[] kept = [.. (plan["settings"]?["retain_live_root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>()];
        int[]? roots = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(group => group["static_verified"]?.GetValue<bool>() != true && !staticIds.Contains(group["id"]?.GetValue<string>()))
            .Select(group => (Roots: (group["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>().Except(kept).ToArray(),
                Value: BakeValueAssessment.PassCoverage(plan, (group["layer_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>()
                    .Distinct().SelectMany(id => observed[id])))).Where(group => group.Roots.Length > 0)
            .OrderBy(group => group.Value).Select(group => group.Roots).FirstOrDefault();
        return roots is null ? null : [.. kept, .. roots];
    }

    private static JsonObject Record(string policy, string status, string[] conditions) => new()
    {
        ["policy"] = policy, ["status"] = status,
        ["conditions"] = new JsonArray(conditions.Select(c => (JsonNode)JsonValue.Create(c)).ToArray())
    };

    private static string Describe(string[] conditions, bool english) => string.Join(english ? "; " : "；", conditions.Select(c => (c, english) switch
    {
        (StaticWithLive, false) => "静态图替换没有减少原来的绘制工作，实时图层照旧运行",
        (StaticWithLive, true) => "the still-image replacement preserves the original drawing work and leaves the live layers running",
        (TooManyStreams, false) => $"成品需要超过当前 {SavingProvenStreams} 路视频上限",
        (TooManyStreams, true) => $"the result exceeds the current limit of {SavingProvenStreams} video streams",
        (PlainLayersOnly, false) => "带特效的图层都要留在实时，能转成视频的只有普通贴图，省下的渲染抵不过视频解码",
        (PlainLayersOnly, true) => "every layer with effects has to stay live and only plain images can become video, so the rendering saved does not cover video decoding",
        (VideoCostOverSaving, false) => "转成视频的特效太少或只占小块画面，视频解码比省下的渲染更费电",
        (VideoCostOverSaving, true) => "too few effects, or effects covering only small areas, go into the video, so decoding it costs more than the rendering saved",
        _ => c
    }));
}
