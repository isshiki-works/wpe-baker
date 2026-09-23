using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 无候选必须带结构化原因，analyze 再把原因翻成一句裁定。这里验证三条 not_suitable 规则只在
/// "这次一个候选都没有"时成立、裁定不改任何既有字段，以及能产出候选的壁纸一律不被误伤。
/// </summary>
internal static class SuitabilityVerdictChecks
{
    internal static void Run(Action<bool, string> check, string outputRoot)
    {
        var method = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridScenePlanner")!
            .GetMethod("Suitability", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonObject Verdict(JsonObject plan) => (JsonObject)method.Invoke(null, [plan])!;
        static string Text(JsonObject verdict, string key) => verdict[key]!.GetValue<string>();
        static string[] Notes(JsonObject verdict) => verdict["notes"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

        // --- 求解器层：公共帧步长跨过上限时，返回的必须是带原因的空候选，而不是一个空约束表 ---
        var exceeding = new CommonLoopSolveRequest(120, 1, [
            new("authored-95s", new CommonLoopPeriod(95, CommonLoopPeriodEvidence.Analytic, new CommonLoopRational(95))),
            new("authored-110s", new CommonLoopPeriod(110, CommonLoopPeriodEvidence.Analytic, new CommonLoopRational(110)))]);
        CommonLoopSearchResult exceeded = CommonLoopSolver.Suggest(exceeding);
        check(exceeded.Candidates.Count == 0 && exceeded.UnresolvedConstraints.Count == 0 && exceeded.FixedFrameStep == 250800 &&
            exceeded.NoCandidate is { Kind: CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling } exceededReason &&
            exceededReason.CeilingSeconds == CommonLoopSolver.DefaultMaximumSeconds && exceededReason.FixedPeriodSeconds == 2090,
            "two coprime fixed periods beyond the ceiling return an empty candidate list with FixedPeriodExceedsCeiling");
        // 步长落在上限内、却没有一帧能同时满足所有分量：也要带原因，不能给个空表了事。
        CommonLoopSearchResult noFrame = CommonLoopSolver.Suggest(new(60, 1, [
            new("locked-173s", new CommonLoopPeriod(173, CommonLoopPeriodEvidence.Analytic, new CommonLoopRational(173))),
            new("retimable-100s", new CommonLoopPeriod(100, CommonLoopPeriodEvidence.Analytic, new CommonLoopRational(100)), AllowRetime: true)]));
        check(noFrame.Candidates.Count == 0 && noFrame.UnresolvedConstraints.Count == 0 &&
            noFrame.NoCandidate is { Kind: CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents, FixedPeriodSeconds: 173 },
            "a fixed step inside the ceiling that no frame satisfies returns NoFrameOnFixedStepSatisfiesComponents");

        CommonLoopSearchResult solvable = CommonLoopSolver.Suggest(new(60, 1, [
            new("authored-30s", new CommonLoopPeriod(30, CommonLoopPeriodEvidence.Analytic, new CommonLoopRational(30)))]));
        check(solvable.Candidates.Count > 0 && solvable.NoCandidate is null,
            "a solvable request still reports no no-candidate reason");

        // --- S1：依赖闭包之后没有任何视频组 ---
        JsonObject nothingToBake = Plan(groups: 0, totalLayers: 847, videoLayers: 0, loop: Loop());
        JsonObject s1 = Verdict(nothingToBake);
        check(Text(s1, "verdict") == "not_suitable" && Text(s1, "rule") == "nothing_to_bake" &&
            Text(s1, "reason_en").Length > 0 && Text(s1, "reason_zh").Length > 0,
            "an empty video group set is ruled not_suitable with rule nothing_to_bake");

        JsonObject exhaustedAllocation = Plan(groups: 2, totalLayers: 5, videoLayers: 2,
            loop: Loop(unresolved: 1), blockers: [new Blocker(BlockerCode.BakeAllocation, ["A live controller must be retained."])]);
        exhaustedAllocation["loop_allocation_fallback"] = new JsonObject {
            ["status"] = "still_unavailable", ["replanned_video_group_count"] = 0,
            ["replanned_effect_prefix_cache_count"] = 0 };
        exhaustedAllocation["suitability"] = Verdict(exhaustedAllocation);
        check(Text(exhaustedAllocation["suitability"]!.AsObject(), "rule") == "no_independent_content_after_reallocation" &&
            PlanNarrative.Summarize(exhaustedAllocation)["zh"]!.GetValue<string>().StartsWith("当前不适合生成", StringComparison.Ordinal),
            "an already-exhausted allocation is not presented as another choice for the user to resolve");
        exhaustedAllocation["loop_allocation_fallback"]!.AsObject().Remove("replanned_video_group_count");
        check(Text(Verdict(exhaustedAllocation), "rule") != "no_independent_content_after_reallocation",
            "missing fallback group evidence is not treated as zero");
        exhaustedAllocation["loop_allocation_fallback"]!["replanned_video_group_count"] = 1;
        check(Text(Verdict(exhaustedAllocation), "rule") != "no_independent_content_after_reallocation",
            "an unresolved but nonempty smaller allocation is not declared empty");

        // --- S2：可烘集合非空但集合上没有任何已证明的时间机制 ---
        JsonObject stillImage = Plan(groups: 1, totalLayers: 130, videoLayers: 8,
            loop: Loop(reason: Reason("NoTemporalMechanism", periodCount: 0)));
        JsonObject s2 = Verdict(stillImage);
        check(Text(s2, "verdict") == "not_suitable" && Text(s2, "rule") == "no_temporal_mechanism_in_video" &&
            Text(s2, "reason_en").Contains("8 of 130", StringComparison.Ordinal) &&
            Text(s2, "reason_zh").Contains("130 层里只有 8 层", StringComparison.Ordinal),
            "a bakeable set with no proven temporal mechanism is ruled not_suitable and names the measured layer counts");
        JsonObject wholeSceneStill = Plan(groups: 1, totalLayers: 1, videoLayers: 1,
            loop: Loop(reason: Reason("NoTemporalMechanism", periodCount: 0)));
        JsonObject wholeSceneVerdict = Verdict(wholeSceneStill);
        check(Text(wholeSceneVerdict, "rule") == "no_temporal_mechanism_in_video" &&
            Text(wholeSceneVerdict, "reason_en").Contains("All 1 layer(s)", StringComparison.Ordinal) &&
            Text(wholeSceneVerdict, "reason_zh").Contains("全部 1 层都能预渲染", StringComparison.Ordinal),
            "a scene whose every layer is bakeable and still says so instead of comparing a count with itself");
        JsonObject provenStatic = Plan(groups: 1, totalLayers: 130, videoLayers: 8,
            loop: Loop(reason: Reason("NoTemporalMechanism", periodCount: 0), sourceStatic: true));
        check(Text(Verdict(provenStatic), "rule") != "no_temporal_mechanism_in_video",
            "a scene proven static never falls under the still-image rule");
        JsonObject unexplained = Plan(groups: 1, totalLayers: 130, videoLayers: 8,
            loop: Loop(reason: Reason("NoTemporalMechanism", periodCount: 0), unresolved: 4));
        JsonObject unexplainedVerdict = Verdict(unexplained);
        check(Text(unexplainedVerdict, "verdict") == "requires_user_choice" &&
            Text(unexplainedVerdict, "rule") == "loop_not_established" &&
            Text(unexplainedVerdict, "reason_en").Contains("4 temporal mechanism(s)", StringComparison.Ordinal) &&
            Text(unexplainedVerdict, "reason_zh").Contains("4 条没解开的时间机制", StringComparison.Ordinal),
            "unexplained temporal mechanisms are never reported as an absence of motion");

        // --- S3：不可调速分量的公共帧步长超过求解器上限 ---
        JsonObject overCeiling = Plan(groups: 5, totalLayers: 960, videoLayers: 27,
            loop: Loop(reason: Reason("FixedPeriodExceedsCeiling", periodCount: 45, fixedPeriodSeconds: 5266800)));
        JsonObject s3 = Verdict(overCeiling);
        check(Text(s3, "verdict") == "not_suitable" && Text(s3, "rule") == "fixed_period_exceeds_loop_ceiling" &&
            Text(s3, "reason_en").Contains("1463 hours", StringComparison.Ordinal) &&
            Text(s3, "reason_en").Contains("180-second ceiling", StringComparison.Ordinal) &&
            Text(s3, "reason_zh").Contains("1463 小时", StringComparison.Ordinal) &&
            Text(s3, "reason_zh").Contains("180 秒上限", StringComparison.Ordinal) &&
            Text(s3, "reason_en").Contains("45 authored tracks", StringComparison.Ordinal),
            "a fixed period beyond the ceiling is ruled not_suitable and names the measured period, ceiling and track count");

        // --- 优先级：能力缺口与布局选择只进 notes，主裁定与既有 blockers 都不受影响 ---
        JsonObject blocked = Plan(groups: 1, totalLayers: 130, videoLayers: 8,
            loop: Loop(reason: Reason("NoTemporalMechanism", periodCount: 0)),
            blockers: [HdrBlocker, PerspectiveBlocker, LayoutBlockerCode], layoutConflict: LayoutBlocker);
        string beforeBlockers = blocked["blockers"]!.ToJsonString();
        JsonObject blockedVerdict = Verdict(blocked);
        check(Text(blockedVerdict, "rule") == "no_temporal_mechanism_in_video" &&
            blocked["blockers"]!.ToJsonString() == beforeBlockers &&
            Notes(blockedVerdict).Any(note => note.Contains("HDR", StringComparison.Ordinal)) &&
            Notes(blockedVerdict).Any(note => note.Contains("perspective", StringComparison.Ordinal)) &&
            Notes(blockedVerdict).Any(note => note.Contains("Layout choice pending", StringComparison.Ordinal)),
            "capability gaps and the layout choice stay in notes and leave the main verdict and blockers untouched");
        check(Notes(blockedVerdict).Any(note => note.Contains("0 shader period component(s)", StringComparison.Ordinal) &&
                note.Contains("0 traced runtime animation period(s)", StringComparison.Ordinal) &&
                note.Contains("0 runtime clock uniform(s)", StringComparison.Ordinal)),
            "the still-image verdict carries the three counts a user can check it against");

        // --- HDR 能力缺口不冒充"壁纸不行" ---
        JsonObject hdrScene = Plan(groups: 1, totalLayers: 40, videoLayers: 30,
            loop: Loop(candidates: 6), blockers: [HdrBlocker]);
        JsonObject hdrVerdict = Verdict(hdrScene);
        check(Text(hdrVerdict, "verdict") == "unsupported_capture" && Text(hdrVerdict, "rule") == "capture_capability_gap",
            "an HDR scene is ruled unsupported_capture rather than not_suitable");

        // --- 不误伤 A：能产出候选的壁纸，无论 live 层多密集都不得被判不适合 ---
        JsonObject busy = Plan(groups: 5, totalLayers: 960, videoLayers: 21, loop: Loop(candidates: 20));
        JsonObject busyVerdict = Verdict(busy);
        check(Text(busyVerdict, "verdict") == "suitable" && Text(busyVerdict, "rule") == "loop_candidates_available",
            "a plan with loop candidates is suitable no matter how many live layers surround them");
        check(busy["loop"]!["no_candidate_reason"] is null, "a plan with candidates carries no no_candidate_reason");

        // --- 不误伤 B：effect_prefix 路线即使整幅候选为空也不得被判不适合 ---
        JsonObject prefix = Plan(groups: 0, totalLayers: 120, videoLayers: 0, loop: Loop(), route: "effect_prefix", prefixCaches: 3);
        check(Text(Verdict(prefix), "verdict") != "not_suitable",
            "an effect-prefix route with cached prefixes is never ruled not_suitable");

        // --- 不误伤 C：证明为静态、只有一帧候选的壁纸是 suitable ---
        JsonObject oneFrame = Plan(groups: 1, totalLayers: 3, videoLayers: 3, loop: Loop(candidates: 1, sourceStatic: true));
        check(Text(Verdict(oneFrame), "verdict") == "suitable",
            "a proven-static scene with one frame candidate stays suitable");

        // --- 无候选也无可判定理由时，说清是这次没解出来，不冒充壁纸不行 ---
        JsonObject undecided = Plan(groups: 2, totalLayers: 50, videoLayers: 20, loop: Loop());
        check(Text(Verdict(undecided), "verdict") == "requires_user_choice" &&
            Text(Verdict(undecided), "rule") == "loop_not_established",
            "an empty candidate list with no structured reason does not become a not_suitable verdict");

        // --- 真实 analyze 路径：候选存在时不写原因，没有时间机制时写 NoTemporalMechanism ---
        RunAnalyzeChecks(check, outputRoot);
    }

    private static readonly Blocker HdrBlocker = new(BlockerCode.HdrRadianceOpen, ["group-1 layer 1 (R1)."]);
    private static readonly Blocker PerspectiveBlocker = new(BlockerCode.PerspectiveNeedsScreenspace);
    private static readonly Blocker LayoutBlockerCode = new(BlockerCode.FullframeNeedsOpaqueGroupNoLive, [5, 0, "", ""]);
    private const string LayoutBlocker = "Full-frame mode requires one opaque video group; the selected scene settings currently need 5 group(s) or a transparent background.";

    private static JsonObject Reason(string kind, int periodCount, double? fixedPeriodSeconds = null) => new()
    {
        ["kind"] = kind, ["ceiling_seconds"] = 180d, ["fixed_period_seconds"] = fixedPeriodSeconds,
        ["shader_component_count"] = 0, ["runtime_period_count"] = periodCount, ["runtime_clock_uniform_count"] = 0
    };

    private static JsonObject Loop(int candidates = 0, JsonObject? reason = null, bool sourceStatic = false, int unresolved = 0) => new()
    {
        ["status"] = candidates == 0 ? "no_analytic_candidate" : "analytic_candidate_requires_seam_validation",
        ["retime_budget_percent"] = 2d, ["maximum_seconds"] = 180d, ["no_candidate_reason"] = reason,
        ["candidates"] = new JsonArray(Enumerable.Range(0, candidates).Select(index => (JsonNode)new JsonObject { ["frames"] = 600 + index }).ToArray()),
        ["unresolved"] = new JsonArray(Enumerable.Range(0, unresolved).Select(index => (JsonNode)new JsonObject {
            ["kind"] = "UnsupportedShaderMechanism", ["owner_layer_id"] = index }).ToArray()),
        ["source_static"] = sourceStatic
    };

    private static JsonObject Plan(int groups, int totalLayers, int videoLayers, JsonObject loop, string route = "whole_layer",
        int prefixCaches = 0, Blocker[]? blockers = null, string? layoutConflict = null) => new()
    {
        ["route"] = route,
        ["status"] = (blockers?.Length ?? 0) == 0 ? "requires_loop_analysis" : "requires_resolution",
        ["video_groups"] = new JsonArray(Enumerable.Range(0, groups).Select(index => (JsonNode)new JsonObject { ["id"] = index }).ToArray()),
        ["layers"] = new JsonArray(Enumerable.Range(0, totalLayers).Select(index => (JsonNode)new JsonObject {
            ["id"] = index, ["allocation"] = index < videoLayers ? "video" : "live" }).ToArray()),
        ["blockers"] = new JsonArray((blockers ?? []).Select(item => (JsonNode)item.Text).ToArray()),
        ["blockers_localized"] = new JsonArray((blockers ?? []).Select(item => (JsonNode)item.Localized()).ToArray()),
        ["video_layout_admission"] = new JsonObject { ["status"] = layoutConflict is null ? "planned_layout_allowed" : "requires_user_choice", ["reason"] = layoutConflict },
        ["whole_layer"] = new JsonObject { ["status"] = "unavailable" },
        ["effect_prefix_caches"] = new JsonArray(Enumerable.Range(0, prefixCaches).Select(index => (JsonNode)new JsonObject { ["id"] = index }).ToArray()),
        ["loop"] = loop
    };

    private static void RunAnalyzeChecks(Action<bool, string> check, string outputRoot)
    {
        string root = Path.Combine(outputRoot, "suitability-no-candidate-reason");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(root, "scene.json"), "{\"objects\":[{\"id\":1,\"image\":\"models/genericimage.json\"}]}");
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "shaders"));
        Directory.CreateDirectory(Path.Combine(root, "materials"));
        File.WriteAllText(Path.Combine(root, "models", "genericimage.json"), "{\"material\":\"materials/static.json\"}");
        File.WriteAllText(Path.Combine(root, "materials", "static.json"), "{\"passes\":[{\"shader\":\"genericimage\",\"textures\":[\"static\"]}]}");
        File.WriteAllText(Path.Combine(root, "shaders", "genericimage.vert"), "void main(){gl_Position=vec4(0.0);}");
        File.WriteAllText(Path.Combine(root, "shaders", "genericimage.frag"), "void main(){gl_FragColor=vec4(1.0);}");
        TextureContainer.WriteRgbaAsync(Path.Combine(root, "materials", "static.tex"), 1, 1, new byte[] { 1, 2, 3, 4 }).GetAwaiter().GetResult();
        using var source = new ProjectSource(root);
        JsonObject Scene() => new() { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1, ["image"] = "models/genericimage.json" }) };
        JsonObject Runtime(string uniform) => new() {
            ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(), ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 1, ["has_mesh"] = true,
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
                    ["active_uniforms"] = new JsonArray(uniform, "g_ModelMatrix", "g_ViewProjectionMatrix", "g_EyePosition", "g_Color4",
                        "g_Texture0Rotation", "g_Texture0Translation", "g_Texture0Resolution", "g_LightsAmbient"),
                    ["textures"] = new JsonArray("static", "", "", "", "", "", "", "") }) }) };

        JsonObject proven = HybridLoopService.Analyze(Scene(), source, null, Runtime("g_ModelViewProjectionMatrix"), [1], 60, 1);
        check(proven["candidates"]!.AsArray().Count == 1 && proven["no_candidate_reason"] is null &&
            proven["maximum_seconds"]!.GetValue<double>() == CommonLoopSolver.DefaultMaximumSeconds,
            "a scene that yields a candidate reports no no_candidate_reason and still publishes the solver ceiling");

        JsonObject silent = HybridLoopService.Analyze(Scene(), source, null, Runtime("g_CustomInput"), [1], 60, 1);
        check(silent["candidates"]!.AsArray().Count == 0 && !silent["source_static"]!.GetValue<bool>() &&
            silent["no_candidate_reason"]!["kind"]!.GetValue<string>() == nameof(CommonLoopNoCandidateKind.NoTemporalMechanism) &&
            silent["no_candidate_reason"]!["ceiling_seconds"]!.GetValue<double>() == CommonLoopSolver.DefaultMaximumSeconds &&
            silent["no_candidate_reason"]!["shader_component_count"]!.GetValue<int>() == 0 &&
            silent["no_candidate_reason"]!["runtime_period_count"]!.GetValue<int>() == 0,
            "a scene with no temporal mechanism at all reports NoTemporalMechanism instead of a silent empty candidate list");

        JsonObject clocked = HybridLoopService.Analyze(Scene(), source, null, Runtime("g_Time"), [1], 60, 1);
        check(clocked["no_candidate_reason"]!["runtime_clock_uniform_count"]!.GetValue<int>() == 1,
            "an active runtime clock uniform is counted in the no-candidate evidence");
    }
}
