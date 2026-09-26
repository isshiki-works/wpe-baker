using System.Text.Json.Nodes;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 裁定阶段：初判拒因（脚本故障证据、无独立组、HDR 闭合、投影、相机）在内存里按 <see cref="Blocker"/> 持有，
/// HDR 闭合按初始分配求，路线换成特效前缀时由 <see cref="ApplyPrefixRadianceClosure"/> 按前缀捕获对象重求，
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
        // 官方只在"后处理 ultra/displayhdr + 场景 hdr + bloom"时走浮点 HDR 管线；其余情况逐级截到 [0,1]，与 RGBA8 捕获等价，判据不运行。
        // HDR 管线里闭合不成立的组由烘焙改走浮点捕获（GroupRenderScheduler.HdrScale），不再拒绝。
        bool hdrPipeline = request.Postprocessing is "ultra" or "displayhdr" &&
            Resolve(scene["general"]?["hdr"], properties)?.ToJsonString() == "true" &&
            Resolve(scene["general"]?["bloom"], properties)?.ToJsonString() == "true";
        JsonObject radianceClosure = SdrRadianceClosure.Describe(scene, properties, observation.Trace, composer.Groups, source, request.Assets,
            hdrPipeline, project, out _);
        // 没有视频组就没有要捕获的东西，透视捕获与相机路径包络都无从谈起。
        bool capturing = composer.Groups.Count > 0;
        if (capturing && projection["status"]?.GetValue<string>() != "orthographic") blockers.Add(new Blocker(BlockerCode.PerspectiveNeedsScreenspace));
        foreach (var camera in graph.Objects.Values.Where(obj => obj.ContainsKey("camera")))
        {
            if (capturing && camera["path"] is JsonValue path && path.TryGetValue<string>(out string? file) &&
                SceneAnalyzer.ReadResourceJson(source, request.Assets, file)["paths"] is JsonArray { Count: > 0 })
                blockers.Add(new Blocker(BlockerCode.CameraPathNeedsEnvelope));
            if (observation.Trace["runtime_projection"] is not JsonObject) blockers.Add(new Blocker(BlockerCode.RuntimeProjectionRequired));
        }
        return new(blockers, radianceClosure, scriptFaults.Available, scriptFaults.Count, scriptFaults.Errors);
    }

    /// <summary>整层初判拒因里有"无独立组"与 HDR 闭合以外的拒因：这时不试特效前缀回退。</summary>
    internal bool PrefixSafetyBlocked => PrefixSafetyBlockedBy(Blockers.Select(blocker => blocker.Code));

    /// <summary>
    /// HDR 闭合的两种拒因。它们只对"当前分配/路线实际捕获的对象"成立：捕获对象换了就要重求，
    /// 不能拿整层初始分配的结论去挡前缀回退或更小分配回退。
    /// </summary>
    internal static bool IsRadianceCode(BlockerCode code) =>
        code is BlockerCode.HdrRadianceOpen or BlockerCode.HdrRadianceOpenProperty;

    /// <summary>采集能力缺口（HDR 闭合、透视投影）：说的是工具还不会采，不是这份分配烘出来不对。</summary>
    internal static bool IsCaptureGap(BlockerCode code) => IsRadianceCode(code) || code == BlockerCode.PerspectiveNeedsScreenspace;

    /// <summary>
    /// 效果前缀回退救"没有与输入无关的可烘组"（含通用形态）与 HDR 闭合不成立两类拒因；拒因里还有别的时不试前缀。
    /// HDR 拒因是按整层初始分配求的，前缀路线捕获的是另一批对象：采纳前缀后由 <see cref="ApplyPrefixRadianceClosure"/> 对前缀捕获对象重求。
    /// </summary>
    internal static bool PrefixSafetyBlockedBy(IEnumerable<BlockerCode> codes) =>
        codes.Any(code => code is not (BlockerCode.NoInputIndependentGroup or BlockerCode.NoInputIndependentGroupGeneric) && !IsRadianceCode(code));

    /// <summary>最终路线换了捕获对象时，plan 里保留整层初始分配那次 HDR 闭合求值的字段（紧跟 hdr_radiance_closure）。</summary>
    internal const string InitialRadianceClosureField = "hdr_radiance_closure_initial";

    /// <summary>
    /// 特效前缀路线定稿后，对前缀实际捕获的对象重求 HDR 闭合。每份缓存捕获的是 owner 层连同它的前 prefix_effect_count 个特效，
    /// 按 owner 层一组求值：场景副本里截掉前缀之外的特效（它们留实时），运行时证据里去掉只属于被截特效的材质（与前缀循环分析同一投影）。
    /// hdr_radiance_closure 换成这次求值（capture_scope = effect_prefix），整层初始分配那次挪到 <see cref="InitialRadianceClosureField"/> 备查；
    /// 闭合不成立时 HDR 拒因写回 blockers、状态回到 requires_resolution。whole_layer 副本仍记整层那一份，不动。
    /// hdr 未开启时判据不运行，plan 逐字不变。
    /// </summary>
    internal static void ApplyPrefixRadianceClosure(JsonObject report, JsonObject scene, JsonObject properties, JsonObject trace,
        ProjectSource source, string? assets, JsonObject? project, Analysis.EffectRange.EffectRangeRules? effectRules = null)
    {
        if (report["route"]?.GetValue<string>() != "effect_prefix" || report["effect_prefix_caches"] is not JsonArray { Count: > 0 } caches ||
            report["hdr_radiance_closure"] is not JsonObject initial ||
            initial["hdr"] is not JsonValue flag || !flag.TryGetValue(out bool hdr) || !hdr) return;
        JsonObject captureScene = scene.DeepClone().AsObject();
        JsonObject captureTrace = trace.DeepClone().AsObject();
        var groups = new JsonArray();
        foreach (JsonObject cache in caches.OfType<JsonObject>())
        {
            if (Int(cache["owner_layer_id"]) is not int owner) continue;
            TrimToEffectPrefix(captureScene, captureTrace, owner, Int(cache["prefix_effect_count"]) ?? 0, source, assets);
            groups.Add(new JsonObject {
                ["id"] = "effect_prefix_" + owner.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["layer_ids"] = new JsonArray(owner) });
        }
        JsonObject closure = SdrRadianceClosure.Describe(captureScene, properties, captureTrace, groups, source, assets, hdr, project,
            effectRules ?? Analysis.EffectRange.EffectRangeRules.Default, out Blocker? blocker);
        closure["capture_scope"] = "effect_prefix";
        JsonObject initialCopy = initial.DeepClone().AsObject();
        report["hdr_radiance_closure"] = closure;
        report.Remove(InitialRadianceClosureField);
        report.Insert(report.IndexOf("hdr_radiance_closure") + 1, InitialRadianceClosureField, initialCopy);
        if (blocker is null) return;
        PlanBlockers.Add(report, blocker);
        report["status"] = "requires_resolution";
    }

    /// <summary>
    /// 把场景副本与运行时证据副本裁到 owner 的前 <paramref name="prefixCount"/> 个特效：截掉其后的特效，
    /// 去掉只属于被截特效的材质 shader（前缀里也用到的同名 shader 保留）。读不出的特效资源当作不在集合里，不做裁定。
    /// </summary>
    private static void TrimToEffectPrefix(JsonObject scene, JsonObject trace, int ownerId, int prefixCount, ProjectSource source, string? assets)
    {
        JsonObject? owner = (scene["objects"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => Int(item["id"]) == ownerId);
        if (owner?["effects"] is not JsonArray effects || effects.Count <= prefixCount) return;
        string[][] shaders = [.. effects.OfType<JsonObject>().Select(effect => ShaderPeriodAnalysis.EffectMaterialShaders(source, assets, effect).ToArray())];
        var kept = new HashSet<string>(shaders.Take(prefixCount).SelectMany(names => names), StringComparer.Ordinal);
        var dropped = new HashSet<string>(shaders.Skip(prefixCount).SelectMany(names => names).Where(entry => !kept.Contains(entry)), StringComparer.Ordinal);
        while (effects.Count > prefixCount) effects.RemoveAt(effects.Count - 1);
        if (dropped.Count == 0 || trace["runtime_layers"] is not JsonArray layers) return;
        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            if (Int(layer["owner"]) != ownerId || layer["materials"] is not JsonArray materials) continue;
            foreach (JsonNode? node in materials.ToArray())
                if (node is JsonObject material && material["shader"] is JsonValue shader &&
                    shader.TryGetValue(out string? name) && name is not null && dropped.Contains(name))
                    materials.Remove(node);
        }
    }

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
            PlanBlockers.Add(report, new Blocker(BlockerCode.VideoShell));
            report["status"] = "requires_resolution";
        }
        // 特效前缀缓存的编码尺寸在 analyze 阶段就能按源纹理算出来：越过硬件解码上限的提前写 unresolved 提示。
        // 只追加这一个字段，不改 effect_prefix_caches（bake 会逐字比对它），也不改 route 与裁决。
        if (report["effect_prefix_caches"] is JsonArray { Count: > 0 })
            report["effect_prefix_hardware_decode_preflight"] = HardwareDecodeDimensions.PredictEffectPrefixCaches(report, source,
                request.Assets, request, report["projection"] as JsonObject ?? projection);
        // 用烘焙的装配规则（真实对象表与运行时依赖）核一遍再许诺整层烘焙，不重新渲染。只有 id/parent 的骨架看不到
        // 光源（装配时连同父级提前输出），父级下的实时层随之提前、绘制顺序改变，骨架判能、烘焙到装配才抛。
        if (!effectPrefixRoute &&
            LayoutAdmission.CompositionHierarchyConflict(report, graph.Objects, observation.Dependencies) is Blocker assemblyConflict)
        {
            PlanBlockers.Add(report, assemblyConflict);
            PlanBlockers.Add(report["whole_layer"]!.AsObject(), assemblyConflict);
            report["whole_layer"]!["status"] = "unavailable";
            report["status"] = "requires_resolution";
        }
        report["suitability"] = HybridSuitability.Verdict(report);
    }

    /// <summary>
    /// plan 里的 loop 与 whole_layer.loop 是两份独立副本，追加理由时必须同时写。条目本身只有 v3 字段；
    /// <paramref name="localized"/> 是 detail 的 {key, zh, en, params}（例如捕获点探测的理由），与条目同步记进
    /// <paramref name="notes"/>，写 plan 时渲染进 unresolved_localized。只有英文原文的理由不带。
    /// 去重看条目与它的文案两样都相同（与原来"整条结构相等"同义）。
    /// </summary>
    internal static void AddLoopUnresolved(JsonObject report, string kind, string detail, UnresolvedNotes? notes = null, JsonObject? localized = null)
    {
        var entry = new JsonObject { ["kind"] = kind, ["detail"] = detail };
        bool noted = false;
        foreach (JsonNode? node in new JsonNode?[] { report["loop"], report["whole_layer"]?["loop"] })
        {
            if (node is not JsonObject loop || loop["unresolved"] is not JsonArray unresolved ||
                Enumerable.Range(0, unresolved.Count).Any(index => JsonNode.DeepEquals(unresolved[index], entry) &&
                    JsonNode.DeepEquals(notes?.At(loop, index)?.Localized, localized))) continue;
            unresolved.Add(entry.DeepClone());
            // 两份副本同下标，文案只记一次。
            if (!noted) notes?.Add(entry, localized);
            noted = true;
        }
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
        foreach (JsonObject owner in new[] { report, wholeLayer })
            if (owner["blockers"] is JsonArray) PlanBlockers.Add(owner, blocker);
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
