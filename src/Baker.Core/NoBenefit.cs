using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 烘了也不省电的几类方案一律拒绝，只有命令行 --no-benefit allow（调试、测功耗用）能覆盖。判据：
/// 静态成品仍带实时层、固定单个时段、视频流超过 <see cref="SavingProvenStreams"/> 路，
/// 以及整层路线里每路视频省下的特效渲染不到 <see cref="MinPassCoveragePerStream"/> 道整屏（贵的层留在实时，烘掉的只是便宜的部分）。
/// 前两条在分析时就能从 plan 读准（写 blocker）。视频流路数：整层路线要等组编完才知道哪些组是静态纹理，在烘焙时按实际编出的视频流判；
/// 特效前缀路线每个缓存都编成一路视频，路数就是 effect_prefix_caches 的个数，分析时判。
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
    public const string FixedDaytime = "fixed_daytime_state";
    public const string TooManyStreams = "video_streams_over_limit";

    /// <summary>
    /// 整层路线每路视频至少要省下的特效渲染：被烘层的特效 pass 数按画布占比加权（bake_value 的 effect_pass_coverage）÷ 视频组数。
    /// 一路视频在笔记本上约 0.66 W CPU 加解码（runs/SALVAGE/report.json）。笔记本 PKG 实测：更费电的 5 张（3151551777、3436033033、
    /// 3666747189、3669680904、改前的 3737267090）在 0–0.57，省电的 3650475846 为 17.9；取 1 只拦得住已知费电的，少误伤未实测的。
    /// </summary>
    public const double MinPassCoveragePerStream = 1.0;

    public const string PlainLayersOnly = "only_plain_layers_baked";
    public const string VideoCostOverSaving = "video_cost_over_saved_rendering";

    /// <summary>分析时就能判的条件：只剩一张静态图但仍有实时层；固定在单个时段；特效前缀缓存超过上限；整层路线省下的渲染抵不过视频。</summary>
    public static string[] AnalysisConditions(JsonObject plan)
    {
        var hits = new List<string>();
        double? frames = plan["loop"]?["candidates"] is JsonArray { Count: > 0 } candidates ? StaticOnlyBake.Count(candidates[0]?["frames"]) : null;
        if (frames is <= 1 && plan["live_layer_ids"] is JsonArray { Count: > 0 })
            hits.Add(StaticWithLive);
        if (plan["settings"]?["daytime_state"] is JsonValue)
            hits.Add(FixedDaytime);
        if (plan["route"]?.GetValue<string>() == "effect_prefix" && plan["effect_prefix_caches"] is JsonArray caches &&
            TooManyVideoStreams(caches.Count))
            hits.Add(TooManyStreams);
        // 源里自带视频的方案（decode_work）换的是解码量，不按特效算；static 成品不编视频，不在此列。
        if (frames is > 1 && plan["route"]?.GetValue<string>() == "whole_layer" && plan["video_dominant"]?["decode_work"] is null &&
            plan["video_groups"] is JsonArray { Count: > 0 } groups && RemovedPassCoverage(plan) is double removed &&
            removed < groups.Count * MinPassCoveragePerStream)
            hits.Add(removed == 0 ? PlainLayersOnly : VideoCostOverSaving);
        return hits.ToArray();
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
    /// 只有普通图层的视频组（单个源材质、普通贴图着色器、不带光照、没有特效 pass；不画东西的节点层不算）省下的渲染可证明约为 0，
    /// 进视频只多一路视频和一个视频层的固定开销（SALVAGE 每路约 0.66 W，VLAYOUT 每层约 0.46 W）。返回把这些组留实时的 --retain-live 列表。
    /// 静态成品、已证静态的组不编视频，不在此列；全部组都是普通组时没有可烘内容，交给 <see cref="PlainLayersOnly"/>。
    /// </summary>
    internal static async Task<int[]?> PlainGroupRetainRootsAsync(JsonObject plan, CancellationToken token)
    {
        if (plan["route"]?.GetValue<string>() != "whole_layer" || plan["video_dominant"]?["decode_work"] is not null ||
            plan["loop"]?["candidates"] is not JsonArray { Count: > 0 } loops || !(StaticOnlyBake.Count(loops[0]?["frames"]) > 1) ||
            plan["video_groups"] is not JsonArray { Count: > 1 } groups ||
            plan["runtime_evidence"]?.GetValue<string>() is not string path || !File.Exists(path)) return null;
        var drawn = (JsonNode.Parse(await File.ReadAllTextAsync(path, token))?["runtime_layers"] as JsonArray ?? [])
            .OfType<JsonObject>().ToLookup(layer => SceneGraph.Int(layer["owner"]));
        var drawable = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>().Where(layer => SceneGraph.Int(layer["id"]) is int)
            .DistinctBy(layer => SceneGraph.Int(layer["id"])).ToDictionary(layer => SceneGraph.Int(layer["id"])!.Value,
                layer => layer["drawable"]?.GetValue<bool>() != false);
        static bool Plain(JsonObject layer) => layer["has_effect_layer"]?.GetValue<bool>() != true &&
            layer["materials"] is JsonArray { Count: 1 } materials && materials[0] is JsonObject source &&
            source["role"]?.GetValue<string>() == "source" &&
            source["shader"]?.GetValue<string>() is "genericimage2" or "genericimage3" or "genericimage4" or "flat" &&
            !(source["active_uniforms"] as JsonArray ?? []).Any(u => u?.GetValue<string>().StartsWith("g_Lights", StringComparison.Ordinal) == true);
        bool PlainLayer(int id) => drawn[id].All(Plain) && (drawn[id].Any() || !drawable.GetValueOrDefault(id, true));
        int[] roots = [.. groups.OfType<JsonObject>().Where(group => group["static_verified"]?.GetValue<bool>() != true &&
                (group["layer_ids"] as JsonArray ?? []).Select(SceneGraph.Int).All(id => id is int layer && PlainLayer(layer)))
            .SelectMany(group => (group["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int)).OfType<int>()];
        bool allPlain = groups.OfType<JsonObject>().All(group => (group["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int).All(id => id is int r && roots.Contains(r)));
        return roots.Length == 0 || allPlain ? null : FullFrameDemotion.RetainLiveCommandRoots(plan, roots);
    }

    /// <summary>被烘层省下的特效渲染（按画布占比加权的 pass 数）；bake_value 没算这一项的方案返回 null，不判。</summary>
    internal static double? RemovedPassCoverage(JsonObject plan) => plan[BakeValueAssessment.Field]?["rule"]?.GetValue<string>() switch
    {
        var rule when rule == WorkloadValue.CachedEffectPasses.Rule =>
            BakeValueAssessment.Number(plan[BakeValueAssessment.Field]!["evidence"]?["effect_pass_coverage"]) is double w && double.IsFinite(w) ? w : null,
        var rule when rule == WorkloadValue.NeedsWorkComparison.Rule => 0,   // 走到这条时被烘层一道特效都没有
        _ => null
    };

    /// <summary>分析收尾：记下判定；命中且没有覆盖时写 blocker 拒绝（与 TooManyVideoGroups 同一写法）。只差采集能力的方案按假设能采集判。</summary>
    public static void Apply(JsonObject plan, bool allowed)
    {
        string[]? ifCaptured = CaptureOpenConditions(plan);
        string[] conditions = ifCaptured ?? AnalysisConditions(plan);
        if (conditions.Length == 0)
        {
            // 没命中的方案不写记录，plan 与旧版逐字节相同；只差采集能力的标出来。
            if (ifCaptured is not null) plan[Field] = Record(RejectChoice, CaptureOpenStatus, []);
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

    /// <summary>烘焙用：plan 是否已被显式允许生成预计不省电的方案。读 settings 而不是 no_benefit 记录，烘焙期间重分析出的新 plan 也带着它。</summary>
    public static bool Allowed(JsonObject plan) => plan["settings"]?["allow_no_benefit"]?.GetValue<bool>() == true;

    /// <summary>烘焙时的视频流判据：实际编出的视频流（不含静态纹理）超过上限。</summary>
    public static bool TooManyVideoStreams(int videoLayers) => videoLayers > SavingProvenStreams;

    /// <summary>烘焙拒绝：写状态与两语言的原因，并记下命中的条件与停下时已编出的视频流数（越线就停，不知道总数）。</summary>
    public static void RejectStreams(JsonObject report, int videoLayers)
    {
        report["status"] = RejectedBakeStatus;
        JsonObject record = Record(RejectChoice, ExpectedStatus, [TooManyStreams]);
        record["video_streams_encoded"] = videoLayers;
        report[Field] = record;
        new Message("bake.no_benefit_streams", [videoLayers, SavingProvenStreams]).Write(report, "reason");
    }

    private static JsonObject Record(string policy, string status, string[] conditions) => new()
    {
        ["policy"] = policy, ["status"] = status,
        ["conditions"] = new JsonArray(conditions.Select(c => (JsonNode)JsonValue.Create(c)).ToArray())
    };

    private static string Describe(string[] conditions, bool english) => string.Join(english ? "; " : "；", conditions.Select(c => (c, english) switch
    {
        (StaticWithLive, false) => "烘完只剩一张静态图，实时图层照旧运行",
        (StaticWithLive, true) => "the result would be a still image with the live layers still running",
        (FixedDaytime, false) => "成品固定在一个时段，不随时刻切换",
        (FixedDaytime, true) => "the result is fixed to one time of day and does not follow the clock",
        (TooManyStreams, false) => $"成品需要超过 {SavingProvenStreams} 路视频（路数更多的成品通常比原作更费电）",
        (TooManyStreams, true) => $"the result needs more than {SavingProvenStreams} video streams (results with more streams usually draw more power than the original)",
        (PlainLayersOnly, false) => "带特效的图层都要留在实时，能转成视频的只有普通贴图，省下的渲染抵不过视频解码",
        (PlainLayersOnly, true) => "every layer with effects has to stay live and only plain images can become video, so the rendering saved does not cover video decoding",
        (VideoCostOverSaving, false) => "转成视频的特效太少或只占小块画面，视频解码比省下的渲染更费电",
        (VideoCostOverSaving, true) => "too few effects, or effects covering only small areas, go into the video, so decoding it costs more than the rendering saved",
        _ => c
    }));
}
