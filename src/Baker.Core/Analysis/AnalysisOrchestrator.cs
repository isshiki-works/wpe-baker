using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Baker.Core;

/// <summary>
/// 逐状态子 plan 的去处（CLI 的 --daytime-split on）：<paramref name="PlanPath"/> 按状态名给落盘路径（新建，已存在即报错），
/// <paramref name="Analyzed"/> 在每个状态分析完后回调（CLI 用它在 stderr 上报一行结论）。
/// </summary>
public sealed record StateExport(Func<string, string> PlanPath, Action<string, JsonObject>? Analyzed = null);

/// <summary>
/// 分析编排：在 <see cref="SearchSpace"/>（交互 × 档位 × 布局 × 状态）里按固定顺序调用单次分析，挑第一个能生成的结果；
/// 请求的交互策略不行时验证替代策略并记成建议；最后把尝试经过写进既有的 preset_* 字段、落盘，按需逐状态导出子 plan。
/// 每次调用先在 <see cref="CallBudget"/> 记账，超出现状最坏上限抛内部错误。
/// </summary>
internal sealed class AnalysisOrchestrator
{
    // 与 CLI 写 plan 的序列化选项相同：逐状态子 plan 原先由 Program.cs 用这组选项写出。
    private static readonly JsonSerializerOptions StatePlanJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HybridAnalyzeRequest request;
    private readonly Func<HybridAnalyzeRequest, CancellationToken, Task<JsonObject>> analyze;
    private readonly SearchSpace space;
    private readonly CallBudget budget;
    private readonly NativeTools? tools;
    private readonly string run, cache;
    private readonly CancellationToken token;
    private int attempt, states;
    private JsonArray costs = new();

    private AnalysisOrchestrator(HybridAnalyzeRequest request, Func<HybridAnalyzeRequest, CancellationToken, Task<JsonObject>> analyze,
        SearchSpace space, CallBudget budget, NativeTools? tools, string run, string cache, CancellationToken token)
    {
        this.request = request;
        this.analyze = analyze;
        this.space = space;
        this.budget = budget;
        this.tools = tools;
        this.run = run;
        this.cache = cache;
        this.token = token;
    }

    /// <param name="budget">省略时用 <see cref="SearchSpace.Budget"/> 的现状最坏上限；测试传入别的预算验证超出时的内部错误。</param>
    internal static async Task<JsonObject> RunAsync(HybridAnalyzeRequest request,
        Func<HybridAnalyzeRequest, CancellationToken, Task<JsonObject>> analyze, CancellationToken token, NativeTools? tools = null,
        StateExport? export = null, CallBudget? budget = null)
    {
        SearchSpace space = SearchSpace.Of(request, export is not null);
        string root = Path.GetFullPath(request.OutputDirectory);
        ProjectSource.EnsureNoReparsePoints(root);
        Directory.CreateDirectory(root);
        string run = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        string assetsStamp = Directory.Exists(request.Assets) ? AnalysisCache.Key(Directory.EnumerateFiles(request.Assets, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => { var file = new FileInfo(path); return new { path, file.Length, file.LastWriteTimeUtc }; }).ToArray()) : "missing";
        string cache = Path.Combine(request.AnalysisCacheDirectory ?? Path.Combine(root, "cache"),
            AnalysisCache.Key(typeof(AnalysisOrchestrator).Module.ModuleVersionId, assetsStamp));
        var orchestrator = new AnalysisOrchestrator(request, analyze, space, budget ?? space.Budget(), tools, run, cache, token);
        JsonObject result = await orchestrator.SelectAsync();
        // 加载即播的单次轨进了视频组（入场切换）而结果不能生成：按旧行为（所属层判实时、不做切换）整套再分析一次，能生成就用它。
        if (!Admission.Accepted(result) && !request.SingleShotLive && result["runtime_evidence"]?.GetValue<string>() is string evidence &&
            SingleShotAllocation.IntroTrackSeconds(result, JsonNode.Parse(await File.ReadAllTextAsync(evidence, token))!.AsObject()) > 0)
        {
            var fallback = new AnalysisOrchestrator(request with { SingleShotLive = true }, analyze, space, space.Budget(), tools,
                Path.Combine(run, "single-shot-live"), cache, token);
            JsonObject old = await fallback.SelectAsync();
            if (Admission.Accepted(old)) (orchestrator, result) = (fallback, old);
        }
        string stagedPlan = Path.Combine(run, "selected-plan.json");
        await VideoSceneBuilder.WriteJsonAsync(stagedPlan, result, token);
        File.Move(stagedPlan, Path.Combine(root, "plan.json"), true);
        // 逐状态导出只改内存里的结果（母 plan 的 states[] 记子 plan 路径与结论），不回写上面落盘的 plan.json。
        if (space.ExportStates) await orchestrator.ExportStatesAsync(result, export!);
        return result;
    }

    private async Task<JsonObject> SelectAsync()
    {
        string requested = request.Preset ?? "balanced", interaction = space.Interactions[0];
        var selected = await SolveAsync(interaction);
        JsonObject result = selected.Plan;
        JsonArray selectedCosts = costs.DeepClone().AsArray();
        if (!Admission.Accepted(result))
        {
            foreach (string mode in space.Interactions.Skip(1))
            {
                var alternative = await SolveAsync(mode);
                if (!Admission.Accepted(alternative.Plan)) continue;
                string path = Path.Combine(run, "suggested-" + mode + ".json");
                await VideoSceneBuilder.WriteJsonAsync(path, alternative.Plan, token);
                string key = mode == "fixed" ? "interaction.suggest_fixed" : "interaction.suggest_off";
                result["suggested_change"] = new JsonObject { ["verified"] = true, ["plan_path"] = path,
                    ["settings"] = new JsonObject { ["interaction"] = mode },
                    ["zh"] = MessageCatalog.Get(key, "zh"), ["en"] = MessageCatalog.Get(key, "en") };
                break;
            }
            if (Admission.Bakeable(result) && Admission.GroupCount(result) > Admission.MaxVideoGroups(result))
            {
                PlanBlockers.Add(result, new Blocker(BlockerCode.TooManyVideoGroups, [Admission.MaxVideoGroups(result)]));
                result["status"] = "requires_resolution";
                result["preset_rejection_reason"] = "too_many_video_groups";
                result["suitability"] = HybridSuitability.Verdict(result);
                PlanNarrative.Attach(result);
            }
        }
        result = await RetreatAsync(result, interaction);
        // 只有普通图层的视频组省不下渲染，还给实时（加 --retain-live 重新分析）；重新分析能生成、预计不省电的条件没变多才采用。
        // --no-benefit allow 时照旧全烘，测功耗用。
        if (Admission.Accepted(result) && !request.AllowNoBenefit && await NoBenefit.PlainGroupRetainRootsAsync(result, token) is { Length: > 0 } plain)
        {
            var (replanned, _) = await new AnalysisOrchestrator(request with { RetainLiveRootIds = plain }, analyze, space, space.Budget(), tools,
                Path.Combine(run, "plain-groups-live"), cache, token).SolveAsync(interaction);
            if (Admission.Accepted(replanned) && NoBenefit.AnalysisConditions(replanned).Length <= NoBenefit.AnalysisConditions(result).Length)
                result = replanned;
        }
        // 预计不省电的方案默认拒绝（判据与覆盖见 NoBenefit）；已经被别的原因拒掉的不重复写，只差采集能力的按假设能采集判。
        if (Admission.Accepted(result) || NoBenefit.CaptureOpenConditions(result) is not null) NoBenefit.Apply(result, request.AllowNoBenefit);
        // 尝试经过只写进既有的 preset_* / interaction_* 字段。
        result["preset_requested"] = requested;
        result["preset_applied"] = Admission.Accepted(result) ? result["settings"]?["preset"]?.DeepClone() ?? JsonValue.Create(requested) : JsonValue.Create("none");
        result["preset_fallback_reason"] = selected.Reasons.Count == 0 ? null : string.Join("; ", selected.Reasons.Select(n => n!.GetValue<string>()));
        result["interaction_requested"] = interaction;
        result["interaction_costs"] = selectedCosts;
        result["custom_settings"] = request.CustomSettings;
        result["live_overlays_hoisted"] = result["occlusion_tradeoff"]?["status"]?.GetValue<string>() == "applied"
            ? result["occlusion_tradeoff"]?["promoted_roots"]?.DeepClone() : new JsonArray();
        var omitted = (result["layers"] as JsonArray ?? []).OfType<JsonObject>().Where(l => l["allocation"]?.GetValue<string>() == "excluded").ToArray();
        var kinds = omitted.SelectMany(l => (l["tradeoff_kinds"] as JsonArray ?? []).Select(k => k!.GetValue<string>())).ToHashSet();
        foreach (JsonObject decision in selectedCosts.OfType<JsonObject>().Where(c => c["decision"]?.GetValue<string>() == "omit"))
            kinds.Add(decision["kind"]!.GetValue<string>());
        if (interaction != "keep" && result["has_parallax"]?.GetValue<bool>() == true) kinds.Add("parallax");
        result["applied_tradeoffs"] = new JsonObject { ["properties"] = new JsonObject(),
            ["excluded_layer_ids"] = result["settings"]?["excluded_layer_ids"]?.DeepClone() ?? new JsonArray(),
            ["turn_off_kinds"] = new JsonArray(kinds.Order().Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
            ["daytime_state"] = result["settings"]?["daytime_state"]?.DeepClone() };
        if (Admission.Accepted(result) && result["summary"] is JsonObject summary)
            foreach (string language in new[] { "zh", "en" })
                summary[language] = MessageCatalog.Get("preset.generated", language) + (kinds.Count == 0 ? "" : "\n" +
                    MessageCatalog.Get("preset.omitted", language, string.Join(language == "zh" ? "、" : ", ", kinds.Order().Select(k => TradeoffOptions.KindLabel(k, language))))) +
                    (result["settings"]?["daytime_state"] is JsonValue state ? "\n" + MessageCatalog.Get("preset.daytime", language, MessageCatalog.DaytimeStateLabel(state.GetValue<string>(), language)) : "");
        return result;
    }

    /// <summary>
    /// 放进视频的层多了反而不行（整层无解、被阻断、预计视频成本高于省下的渲染）时，退回更多层留实时的方案：每轮把每个视频组各留一次实时
    /// 重新分析（连同 plan 已留的和分配回退已点名的层），能生成且预计省电的候选里取预计收益（省下的渲染减视频路数）最大的；一个都没有时，
    /// 从"能生成、只差省电"且余量比这一轮起点大的候选里取余量最大的接着退。只加实时层、不减，证明不能循环的层照旧留实时。都不行时原样返回。
    /// </summary>
    private async Task<JsonObject> RetreatAsync(JsonObject result, string interaction)
    {
        bool Viable(JsonObject plan) => Admission.Accepted(plan) && (request.AllowNoBenefit || NoBenefit.AnalysisConditions(plan).Length == 0);
        static double Margin(JsonObject plan) => Admission.Accepted(plan) ? (NoBenefit.RemovedPassCoverage(plan) ?? 0) -
            ((plan["video_groups"] as JsonArray)?.Count ?? 0) * NoBenefit.MinPassCoveragePerStream : double.NegativeInfinity;
        static IEnumerable<int> Ids(JsonNode? node) => (node as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>();
        if (Viable(result) || Admission.Accepted(result) && !NoBenefit.AnalysisConditions(result).Contains(NoBenefit.VideoCostOverSaving)) return result;
        JsonObject current = result;
        for (int round = 0; current["video_groups"] is JsonArray { Count: > 1 } groups; round++)
        {
            int[] kept = [.. Ids(current["settings"]?["retain_live_root_ids"]).Concat(Ids(current["loop_allocation_fallback"]?["retain_live_root_ids"]))];
            JsonObject? best = null, next = null;
            for (int index = 0; index < groups.Count; index++)
            {
                var (plan, _) = await new AnalysisOrchestrator(request with { RetainLiveRootIds = [.. kept.Concat(Ids(groups[index]?["root_ids"])).Distinct()] },
                    analyze, space, space.Budget(), tools, Path.Combine(run, $"retreat-{round}-{index}"), cache, token).SolveAsync(interaction);
                if (Viable(plan)) { if (best is null || Margin(plan) > Margin(best)) best = plan; }
                else if (Margin(plan) > Margin(next ?? current)) next = plan;
            }
            if (best is not null) return best;
            if (next is null) break;
            current = next;
        }
        return result;
    }

    /// <summary>一个交互策略：档位由请求的那档往省电方向逐档试，第一个能生成的就用；每档失败的原因记进 reasons。</summary>
    private async Task<(JsonObject Plan, JsonArray Reasons)> SolveAsync(string interaction)
    {
        var reasons = new JsonArray();
        var current = request with { Interaction = interaction, ViewMode = interaction == "keep" ? "preserve" : "fixed_view",
            LiveOverlayPlacement = request.CustomSettings ? request.LiveOverlayPlacement : "foreground",
            DaytimeSplit = interaction != "keep" || request.DaytimeSplit };
        JsonObject? last = null;
        int[]? off = null;
        foreach (string preset in space.Presets)
        {
            current = current with { Preset = preset, LoopPreference = RetimeProfile.LoopPreferenceForPreset(preset) };
            if (interaction == "off" && off is null)
            {
                // 关交互：先按原图层分析一次，据此测出要剔除的指针内容与代价高的全屏音频层，之后各档都带着这份剔除。
                JsonObject original = await LayoutsAsync(current, "measure", null);
                (off, costs) = await InteractionPolicy.ExclusionsAsync(original, current with { AnalysisCacheDirectory = cache }, tools,
                    Path.Combine(run, "interaction-cost"), token);
            }
            current = current with { ExcludedLayerIds = off is null ? request.ExcludedLayerIds :
                (request.ExcludedLayerIds ?? []).Concat(off).Distinct().Order().ToArray() };
            last = await StatesAsync(current);
            if (Admission.Accepted(last)) return (last, reasons);
            reasons.Add(preset + ": " + (Admission.GroupCount(last) > Admission.MaxVideoGroups(last) ? "too_many_video_groups" : last["summary"]?["key"]?.GetValue<string>()));
        }
        return (last!, reasons);
    }

    /// <summary>母 plan 识别出状态时，每个状态各规划一次，取能生成且视频组最少的；都不行保留母 plan。</summary>
    private async Task<JsonObject> StatesAsync(HybridAnalyzeRequest candidate)
    {
        JsonObject plan = await LayoutsAsync(candidate, "search", null);
        if (!space.Expands(candidate.Interaction!) || DaytimeSplit.RecognizedStates(plan) is not { } entries) return plan;
        states = Math.Max(states, entries.Length);
        JsonObject? best = Admission.Accepted(plan) ? plan : null;
        for (int index = 0; index < entries.Length; index++)
        {
            string name = entries[index]["name"]!.GetValue<string>();
            JsonObject statePlan = await LayoutsAsync(candidate with { DaytimeState = name }, "search", index + ":" + name);
            if (Admission.Accepted(statePlan) && (best is null || Admission.GroupCount(statePlan) < Admission.GroupCount(best))) best = statePlan;
        }
        return best ?? plan;
    }

    private async Task<JsonObject> LayoutsAsync(HybridAnalyzeRequest candidate, string phase, string? state)
    {
        JsonObject? plan = null;
        foreach (string layout in space.Layouts)
        {
            plan = await TryAsync(candidate with { VideoLayout = layout }, phase, state);
            if (Admission.Accepted(plan)) break;
        }
        return plan!;
    }

    private async Task<JsonObject> TryAsync(HybridAnalyzeRequest candidate, string phase, string? state)
    {
        budget.Charge($"{phase}|{candidate.Interaction}|{candidate.Preset}|{candidate.VideoLayout}|{state ?? "-"}", states);
        return await AdoptAllocationAsync(await analyze(candidate with {
            OutputDirectory = Path.Combine(run, (++attempt).ToString()), AnalysisCacheDirectory = cache }, token), token);
    }

    /// <summary>
    /// 逐状态子 plan：选定结果识别出状态时，每个状态按原请求（不经级联改写）再分析一次，补生成准入后落盘，
    /// 并在结果的 daytime_split.states[] 里记下子 plan 路径、结论 key、阻断数、实时层数与视频组数。
    /// </summary>
    private async Task ExportStatesAsync(JsonObject result, StateExport export)
    {
        if (DaytimeSplit.RecognizedStates(result) is not { } entries) return;
        states = Math.Max(states, entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            JsonObject state = entries[index];
            string stateName = state["name"]!.GetValue<string>();
            budget.Charge($"export|-|-|-|{index}:{stateName}", states);
            JsonObject statePlan = await analyze(request with {
                OutputDirectory = Path.Combine(request.OutputDirectory, "state-" + stateName), DaytimeState = stateName }, token);
            // 子 plan 不经档位与布局的搜索，生成准入要在这里补上，否则它会说能生成、bake 第一步才拒。
            Admission.ApplyGenerationAdmission(statePlan);
            string statePlanPath = export.PlanPath(stateName);
            await using (var stateFile = new FileStream(statePlanPath, FileMode.CreateNew, FileAccess.Write))
                await JsonSerializer.SerializeAsync(stateFile, statePlan, StatePlanJson, token);
            state["plan"] = statePlanPath;
            state["status"] = statePlan["status"]?.DeepClone();
            state["summary_key"] = statePlan["summary"]?["key"]?.DeepClone();
            state["blocker_count"] = (statePlan["blockers"] as JsonArray)?.Count;
            state["live_layer_count"] = (statePlan["live_layer_ids"] as JsonArray)?.Count;
            state["video_group_count"] = (statePlan["video_groups"] as JsonArray)?.Count;
            export.Analyzed?.Invoke(stateName, statePlan);
        }
    }

    internal static async Task<JsonObject> AdoptAllocationAsync(JsonObject plan, CancellationToken token)
    {
        await VerifyStaticGroupBudgetAsync(plan, token);
        Admission.ApplyGenerationAdmission(plan);
        if (Admission.Accepted(plan) || plan["loop_allocation_fallback"]?["status"]?.GetValue<string>() != "candidate_found" ||
            plan["analysis_directory"]?.GetValue<string>() is not string directory) return plan;
        string path = Path.Combine(directory, "loop-allocation-analysis", "plan.json");
        if (!File.Exists(path)) return plan;
        JsonObject child = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        await VerifyStaticGroupBudgetAsync(child, token);
        Admission.ApplyGenerationAdmission(child);
        if (!Admission.Accepted(child) || !JsonNode.DeepEquals(child["source_sha256"], plan["source_sha256"])) return plan;
        child["allocation_adopted"] = new JsonObject { ["plan_path"] = path,
            ["retain_live_root_ids"] = child["settings"]?["retain_live_root_ids"]?.DeepClone(),
            ["basis"] = plan["loop_allocation_fallback"]?["resolution_basis"]?.DeepClone() };
        return child;
    }

    /// <summary>Only over-budget whole-layer plans receive this CPU-only source/runtime proof.  It is an admission estimate;
    /// baking still captures the complete interval and rejects a claimed static group that changes.</summary>
    private static async Task VerifyStaticGroupBudgetAsync(JsonObject plan, CancellationToken token)
    {
        if (!Admission.Bakeable(plan) || plan["route"]?.GetValue<string>() != "whole_layer" ||
            plan["video_groups"] is not JsonArray groups || groups.Count <= Admission.MaxVideoGroups(plan) ||
            plan["source"]?.GetValue<string>() is not string sourcePath ||
            plan["runtime_evidence"]?.GetValue<string>() is not string runtimePath || !File.Exists(runtimePath)) return;
        string sourceKey = plan["source_sha256"]?.GetValue<string>() ?? sourcePath;
        if (plan["static_group_budget"] is JsonObject prior &&
            prior["source"]?.GetValue<string>() == sourceKey && prior["runtime_evidence"]?.GetValue<string>() == runtimePath &&
            prior["status"]?.GetValue<string>() == "verified") return;
        foreach (JsonObject group in groups.OfType<JsonObject>())
        {
            group["static_verified"] = false;
            group.Remove("static_verification");
        }
        try
        {
            JsonObject runtime = JsonNode.Parse(await File.ReadAllTextAsync(runtimePath, token))!.AsObject();
            JsonObject settings = plan["settings"]?.AsObject() ?? new JsonObject();
            uint fpsNumerator = settings["fps_numerator"]?.GetValue<uint>() ?? 120;
            uint fpsDenominator = settings["fps_denominator"]?.GetValue<uint>() ?? 1;
            string? assets = plan["assets"]?.GetValue<string>() ?? settings["assets"]?.GetValue<string>();
            using var source = new ProjectSource(sourcePath);
            JsonObject scene = source.ReadJson(source.SceneResource);
            foreach (JsonObject group in groups.OfType<JsonObject>())
            {
                int[] layers = (group["layer_ids"] as JsonArray)?.Select(node => node!.GetValue<int>()).ToArray() ?? [];
                if (layers.Length == 0) continue;
                JsonObject proof = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, assets, runtime, layers,
                    fpsNumerator, fpsDenominator).ToJson();
                if (proof["source_static"]?.GetValue<bool>() != true) continue;
                group["static_verified"] = true;
                group["static_verification"] = new JsonObject { ["basis"] = "source_and_runtime_static_proof",
                    ["runtime_evidence"] = runtimePath, ["source"] = sourceKey };
            }
            plan["static_group_budget"] = new JsonObject { ["status"] = "verified", ["basis"] = "source_and_runtime_static_proof",
                ["source"] = sourceKey, ["runtime_evidence"] = runtimePath };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            foreach (JsonObject group in groups.OfType<JsonObject>())
            {
                group["static_verified"] = false;
                group.Remove("static_verification");
            }
            plan["static_group_budget"] = new JsonObject { ["status"] = "unavailable", ["basis"] = "source_and_runtime_static_proof",
                ["source"] = sourceKey, ["runtime_evidence"] = runtimePath, ["reason"] = error.Message };
        }
    }
}
