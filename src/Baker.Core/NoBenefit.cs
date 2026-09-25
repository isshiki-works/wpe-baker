using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 烘了也不省电的几类方案默认拒绝，用户用 --no-benefit allow 显式覆盖。三条判据原样取自台账"必须有功耗实测证据"的条件：
/// 静态成品仍带实时层、固定单个时段、视频流超过 <see cref="SavingProvenStreams"/> 路。
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

    public const string RejectionReason = "no_benefit_expected";
    public const string RejectedBakeStatus = "candidate_rejected_no_benefit_expected";

    /// <summary>旧政策"2–4 路省电"；更多路的成品比原作费电的居多（9/18 同批实测 5、6、9 路），默认不生成。</summary>
    public const int SavingProvenStreams = 4;

    public const string StaticWithLive = "static_only_with_live_layers";
    public const string FixedDaytime = "fixed_daytime_state";
    public const string TooManyStreams = "video_streams_over_limit";

    /// <summary>分析时就能判的条件：只剩一张静态图但仍有实时层；固定在单个时段；特效前缀缓存超过上限。</summary>
    public static string[] AnalysisConditions(JsonObject plan)
    {
        var hits = new List<string>();
        if (plan["loop"]?["candidates"] is JsonArray { Count: > 0 } candidates &&
            StaticOnlyBake.Count(candidates[0]?["frames"]) is <= 1 &&
            plan["live_layer_ids"] is JsonArray { Count: > 0 })
            hits.Add(StaticWithLive);
        if (plan["settings"]?["daytime_state"] is JsonValue)
            hits.Add(FixedDaytime);
        if (plan["route"]?.GetValue<string>() == "effect_prefix" && plan["effect_prefix_caches"] is JsonArray caches &&
            TooManyVideoStreams(caches.Count))
            hits.Add(TooManyStreams);
        return hits.ToArray();
    }

    /// <summary>分析收尾：记下判定；命中且没有覆盖时写 blocker 拒绝（与 TooManyVideoGroups 同一写法）。</summary>
    public static void Apply(JsonObject plan, bool allowed)
    {
        string[] conditions = AnalysisConditions(plan);
        if (conditions.Length == 0) return;   // 没命中的方案不写记录，plan 与旧版逐字节相同
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

    /// <summary>plan 里记下的命中条件，讲成一句话（界面结论区用）。</summary>
    public static string Describe(JsonObject plan, bool english) => Describe(
        (plan[Field]?["conditions"] as JsonArray ?? []).Select(c => c!.GetValue<string>()).ToArray(), english);

    private static string Describe(string[] conditions, bool english) => string.Join(english ? "; " : "；", conditions.Select(c => (c, english) switch
    {
        (StaticWithLive, false) => "烘完只剩一张静态图，实时图层照旧运行",
        (StaticWithLive, true) => "the result would be a still image with the live layers still running",
        (FixedDaytime, false) => "成品固定在一个时段，不随时刻切换",
        (FixedDaytime, true) => "the result is fixed to one time of day and does not follow the clock",
        (TooManyStreams, false) => $"成品需要超过 {SavingProvenStreams} 路视频（路数更多的成品通常比原作更费电）",
        (TooManyStreams, true) => $"the result needs more than {SavingProvenStreams} video streams (results with more streams usually draw more power than the original)",
        _ => c
    }));
}
