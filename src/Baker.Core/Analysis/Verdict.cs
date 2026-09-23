using System.Text.Json.Nodes;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 裁定阶段：初判拒因（脚本故障证据、无独立组、HDR 闭合、投影、相机）在内存里按 <see cref="Blocker"/> 持有，
/// 写 plan 时由 <see cref="PlanWriter"/> 渲染一次；路线定稿之后的收尾裁定（残差布局闸门、求解器空候选、可追溯不变量、
/// 视频外壳、公共图层查询冲突、suitability）按固定顺序在 <see cref="Conclude"/> 里跑。
/// 收尾裁定读写的是已组好的 plan：Routes/LayoutAdmission 与 bake 侧也往同一份 blockers 里追加，这部分的类型化留给 C3 的 plan v4。
/// </summary>
internal sealed class Verdict
{
    internal static readonly Blocker MissingScriptFaultEvidenceBlocker = new(BlockerCode.MissingScriptFaultEvidence);

    /// <summary>整层路线的初判拒因，按记录先后（同一条可以出现多次，例如多台相机）。</summary>
    internal IReadOnlyList<Blocker> Blockers { get; }
    /// <summary>HDR 辐射闭合判据的明细（plan.hdr_radiance_closure）。</summary>
    internal JsonObject RadianceClosure { get; }
    /// <summary>源脚本故障证据是否可用；不可用本身就是一条拒因。</summary>
    internal bool ScriptFaultEvidence { get; }
    internal int? ScriptErrorCount { get; }
    internal JsonArray SourceScriptErrors { get; }

    private Verdict(List<Blocker> blockers, JsonObject radianceClosure, bool scriptFaultEvidence, int? scriptErrorCount, JsonArray sourceScriptErrors) =>
        (Blockers, RadianceClosure, ScriptFaultEvidence, ScriptErrorCount, SourceScriptErrors) =
            (blockers, radianceClosure, scriptFaultEvidence, scriptErrorCount, sourceScriptErrors);

    /// <summary>初判（原 R 段）：顺序即 plan.blockers 的顺序。</summary>
    internal static Verdict Initial(HybridAnalyzeRequest request, ProjectSource source, JsonObject scene, JsonObject project,
        JsonObject properties, SceneGraph graph, RuntimeObservation observation, Liveness liveness, Composer composer, JsonObject projection,
        (bool Available, int? Count, JsonArray Errors) scriptFaults)
    {
        var blockers = new List<Blocker>();
        if (!scriptFaults.Available) blockers.Add(MissingScriptFaultEvidenceBlocker);
        if (composer.Groups.Count == 0) blockers.Add(PlanNarrative.NoInputIndependentGroup(graph.Objects, liveness.Reasons));
        // hdr 标志本身不是拒绝理由；拒绝理由是被捕获组的输出可能超出 [0,1] 而被 RGBA8 捕获 clip。
        // 文案走 i18n：判据给出未通过的明细，legacy 英文逐字不变，中文另报壁纸自带的 HDR 开关。
        JsonObject radianceClosure = SdrRadianceClosure.Describe(scene, properties, observation.Trace, composer.Groups, source, request.Assets,
            Resolve(scene["general"]?["hdr"], properties)?.ToJsonString() == "true", project, out Blocker? radianceBlocker);
        if (radianceBlocker is not null) blockers.Add(radianceBlocker);
        if (projection["status"]?.GetValue<string>() != "orthographic") blockers.Add(new Blocker(BlockerCode.PerspectiveNeedsScreenspace));
        foreach (var camera in graph.Objects.Values.Where(obj => obj.ContainsKey("camera")))
        {
            if (camera["path"] is JsonValue path && path.TryGetValue<string>(out string? file) &&
                SceneAnalyzer.ReadResourceJson(source, request.Assets, file)["paths"] is JsonArray { Count: > 0 })
                blockers.Add(new Blocker(BlockerCode.CameraPathNeedsEnvelope));
            if (observation.Trace["runtime_projection"] is not JsonObject) blockers.Add(new Blocker(BlockerCode.RuntimeProjectionRequired));
        }
        return new(blockers, radianceClosure, scriptFaults.Available, scriptFaults.Count, scriptFaults.Errors);
    }

    /// <summary>整层初判拒因里有"无独立组"以外的拒因：这时不试特效前缀回退。</summary>
    internal bool PrefixSafetyBlocked => PrefixSafetyBlockedBy(Blockers.Select(blocker => blocker.Code));

    /// <summary>
    /// 效果前缀回退只救"没有与输入无关的可烘组"这一种拒因（含通用形态）；拒因里还有别的时不试前缀。
    /// </summary>
    internal static bool PrefixSafetyBlockedBy(IEnumerable<BlockerCode> codes) =>
        codes.Any(code => code is not (BlockerCode.NoInputIndependentGroup or BlockerCode.NoInputIndependentGroupGeneric));

    /// <summary>初判拒因渲染成 plan v3 分析中的 blockers 数组（每次调用给一份新数组）。</summary>
    internal JsonArray RenderBlockers() => new([.. Blockers.Select(blocker => (JsonNode)blocker.ToNode())]);

    /// <summary>
    /// 收尾裁定（原 X 段，顺序不可换）：残差布局闸门 → 求解器空候选 blocker → 可追溯不变量 → 视频外壳与烘焙价值 →
    /// 特效前缀硬解预检 → 公共图层查询冲突 → suitability。
    /// </summary>
    internal static void Conclude(JsonObject report, HybridAnalyzeRequest request, ProjectSource source, SceneGraph graph,
        RuntimeObservation observation, JsonObject projection, bool effectPrefixRoute, JsonObject residualScene,
        Func<string, JsonObject?> residualResources)
    {
        // 残差掩盖要在可掩盖分量所在的视频组里淡化（透明组、多组都可以）：分量不在任何视频组里时，在这里写 blocker，不留到 bake 才拒。
        // 放在更小分配取证之后，blocker 才能给出重查过的 --retain-live id；判定读原始源场景，与 bake 第一步同一个 Admission.Evaluate。
        Admission.ApplyResidualLayoutGate(report, residualScene, residualResources);
        RecordSolverNoCandidateBlocker(report);
        RequireTraceableRejection(report);
        // 视频外壳判据放在最后：前面两处 effect_prefix 回退与布局裁决都已经定稿，这里只读结构、只追加，
        // 不改 route、不改分组，免得新加的 blocker 反过来把计划改道。
        JsonObject videoDominance = VideoDominance.Evaluate(report, observation.Trace, request.VideoShell);
        report["video_dominant"] = videoDominance;
        report[BakeValueAssessment.Field] = BakeValueAssessment.Evaluate(report, observation.Trace, source, request.Assets);
        if (videoDominance["status"]?.GetValue<string>() == VideoDominance.ShellStatus)
        {
            PlanBlockers.Add(report["blockers"]!.AsArray(), new Blocker(BlockerCode.VideoShell));
            report["status"] = "requires_resolution";
        }
        // 特效前缀缓存的编码尺寸在 analyze 阶段就能按源纹理算出来：越过硬件解码上限的提前写 unresolved 提示。
        // 只追加这一个字段，不改 effect_prefix_caches（bake 会逐字比对它），也不改 route 与裁决。
        if (report["effect_prefix_caches"] is JsonArray { Count: > 0 })
            report["effect_prefix_hardware_decode_preflight"] = HardwareDecodeDimensions.PredictEffectPrefixCaches(report, source,
                request.Assets, request, report["projection"] as JsonObject ?? projection);
        // These queries are already present in the analysis trace. Use the exporter's
        // existing assembly rule before promising a whole-layer bake, without rendering again.
        if (!effectPrefixRoute && observation.Dependencies.OfType<JsonObject>().Any(item =>
                item["operation"]?.GetValue<string>() == "query" &&
                item["property"]?.GetValue<string>()?.StartsWith("layer_", StringComparison.Ordinal) == true) &&
            LayoutAdmission.CompositionHierarchyConflict(report, graph.Objects, observation.Dependencies) is Blocker publicQueryConflict)
        {
            PlanBlockers.Add(report["blockers"]!.AsArray(), publicQueryConflict);
            PlanBlockers.Add(report["whole_layer"]!["blockers"]!.AsArray(), publicQueryConflict);
            report["whole_layer"]!["status"] = "unavailable";
            report["status"] = "requires_resolution";
        }
        report["suitability"] = HybridSuitability.Verdict(report);
    }

    /// <summary>plan 里的 loop 与 whole_layer.loop 是两份独立副本，追加理由时必须同时写。</summary>
    internal static void AddLoopUnresolved(JsonObject report, string kind, string detail, JsonNode? localized = null)
    {
        // localized 是 detail 的 {key, zh, en, params}（例如捕获点探测的理由）；只有英文原文的理由不带。
        var entry = new JsonObject { ["kind"] = kind, ["detail"] = detail };
        if (localized is not null) entry[PlanNarrative.DetailLocalized] = localized.DeepClone();
        foreach (JsonNode? node in new JsonNode?[] { report["loop"], report["whole_layer"]?["loop"] })
            if (node is JsonObject loop && loop["unresolved"] is JsonArray unresolved &&
                !unresolved.Any(item => JsonNode.DeepEquals(item, entry)))
                unresolved.Add(entry.DeepClone());
    }

    /// <summary>
    /// 整层不可用、没有阻断、也没有任何未解析机制，但求解器给出了结构化的空候选原因（公共步长上没有闭合帧、不可调速分量的周期超上限、
    /// 单段视频调速落不到整数帧）：这就是分析结论，写成 blocker 讲给用户，而不是留给下面的不变量当内部错误抛出。
    /// 典型路径是 --retain-live（包括补充分析的重查）把所有未解析机制的所有者留成实时，剩下的分量各有周期却凑不出公共循环。
    /// 没有结构化原因、或原因是"没有时间机制"（那条路由静态证明负责记录）时不处理，仍由不变量兜底。
    /// </summary>
    internal static void RecordSolverNoCandidateBlocker(JsonObject report)
    {
        if (report["route"]?.GetValue<string>() != "whole_layer" || report["whole_layer"] is not JsonObject wholeLayer ||
            wholeLayer["status"]?.GetValue<string>() != "unavailable" || wholeLayer["blockers"] is JsonArray { Count: > 0 } ||
            wholeLayer["loop"] is not JsonObject loop || loop["candidates"] is JsonArray { Count: > 0 } ||
            (loop["unresolved"] as JsonArray ?? []).OfType<JsonObject>().Any(item => item["kind"]?.GetValue<string>() != ResidualMasking.AllocationFallbackKind) ||
            loop["no_candidate_reason"] is not JsonObject reason) return;
        // 内存里刚写的 plan 是 int/double 各自装箱，从磁盘读回的是 JsonElement：两种都要读得出来。
        static double Number(JsonNode? node) => node is not JsonValue value ? 0
            : value.TryGetValue(out double number) ? number : value.TryGetValue(out long integer) ? integer : value.TryGetValue(out int small) ? small : 0;
        static string Seconds(JsonNode? node) => Number(node).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string ceiling = Seconds(reason["ceiling_seconds"]), period = Seconds(reason["fixed_period_seconds"]);
        int shaders = (int)Number(reason["shader_component_count"]), tracks = (int)Number(reason["runtime_period_count"]);
        Blocker? blocker = reason["kind"]?.GetValue<string>() switch
        {
            nameof(CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents) => new Blocker(BlockerCode.LoopNoCommonFrame, [shaders, tracks, period, ceiling]),
            nameof(CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling) => new Blocker(BlockerCode.LoopFixedPeriodExceedsCeiling, [tracks, period, ceiling]),
            nameof(CommonLoopNoCandidateKind.NoExactVideoRetimeFrame) => new Blocker(BlockerCode.LoopNoExactVideoRetime),
            _ => null
        };
        if (blocker is null) return;
        foreach (JsonNode? node in new[] { report["blockers"], wholeLayer["blockers"] })
            if (node is JsonArray blockers) PlanBlockers.Add(blockers, blocker);
        report["status"] = "requires_resolution";
    }

    /// <summary>
    /// 通用不变量：unavailable 一定要留下可追溯的理由。blockers、loop.unresolved 与求解器的结构化空候选原因
    /// （loop.no_candidate_reason，HybridSuitability 据此裁定，RecordSolverNoCandidateBlocker 会把它写成 blocker）
    /// 同时为空的 unavailable 是状态机漏写，属于内部错误——它对用户表现为"退出码 0、零产出、零解释"，比抛出异常更难处理。
    /// 把粒子层留实时之后剩下的层常常只有一个空候选原因（手工轨道公共周期超上限、着色器分量没有公共帧），
    /// 这不是漏写，是已经说清楚的拒绝。
    /// </summary>
    internal static void RequireTraceableRejection(JsonObject report)
    {
        // 结构化的空候选原因算可追溯（RecordSolverNoCandidateBlocker 会把它写成 blocker），但"没有时间机制"不算：
        // 那条路由由静态证明负责记录理由，缺了就是状态机漏写。
        static bool Traceable(JsonNode? loop) => loop?["no_candidate_reason"] is JsonObject reason &&
            reason["kind"]?.GetValue<string>() != nameof(CommonLoopNoCandidateKind.NoTemporalMechanism);
        if (report["whole_layer"] is not JsonObject wholeLayer || wholeLayer["status"]?.GetValue<string>() != "unavailable" ||
            wholeLayer["blockers"] is JsonArray { Count: > 0 } || wholeLayer["loop"]?["unresolved"] is JsonArray { Count: > 0 } ||
            Traceable(wholeLayer["loop"]) || Traceable(report["loop"]))
            return;
        // 特效前缀路线自带可烘方案，整层不可用不是这次分析的结局。
        if (report["route"]?.GetValue<string>() == "effect_prefix" && report["effect_prefix_caches"] is JsonArray { Count: > 0 }) return;
        string groups = string.Join("; ", (report["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(group => $"{group["id"]?.GetValue<string>() ?? "?"}=[{string.Join(",", (group["layer_ids"] as JsonArray ?? []).Select(id => id?.ToJsonString()))}]"));
        throw new InvalidOperationException("Internal error: whole-layer analysis ended as unavailable without a single blocker or " +
            $"unresolved loop mechanism. route={report["route"]?.GetValue<string>()}, loop.status={report["loop"]?["status"]?.GetValue<string>()}, " +
            $"loop.candidates={(report["loop"]?["candidates"] as JsonArray)?.Count}, baked groups: {(groups.Length == 0 ? "(none)" : groups)}.");
    }
}
