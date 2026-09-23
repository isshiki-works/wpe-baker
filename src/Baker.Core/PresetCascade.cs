using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Existing layouts inside the interaction policy; presets change retiming only.</summary>
public static class PresetCascade
{
    public const bool MeasureSourceByDefault = false;
    // CLI 哪些选项算"自定义"已移到 Baker.Cli/OptionTable 的 Custom 列；界面按高级区是否改过算。两边都经 AnalyzeOptions.Custom 进请求。

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
            plan["video_groups"] is not JsonArray { Count: > Admission.MaxVideoGroups } groups ||
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
                JsonObject proof = HybridLoopService.Analyze(scene.DeepClone().AsObject(), source, assets, runtime, layers,
                    fpsNumerator, fpsDenominator);
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

    internal static async Task<JsonObject> AnalyzeAsync(HybridAnalyzeRequest request,
        Func<HybridAnalyzeRequest, CancellationToken, Task<JsonObject>> analyze, CancellationToken token, NativeTools? tools = null)
    {
        string requested = request.Preset ?? "balanced", interaction = request.Interaction ?? "fixed";
        string[] tiers = ["quality", "balanced", "efficiency"];
        int start = Array.IndexOf(tiers, requested);
        if (start < 0 || interaction is not ("keep" or "fixed" or "off")) throw new InvalidDataException("Invalid preset or interaction policy.");
        string root = Path.GetFullPath(request.OutputDirectory);
        ProjectSource.EnsureNoReparsePoints(root);
        Directory.CreateDirectory(root);
        string run = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        string assetsStamp = Directory.Exists(request.Assets) ? AnalysisCache.Key(Directory.EnumerateFiles(request.Assets, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => { var file = new FileInfo(path); return new { path, file.Length, file.LastWriteTimeUtc }; }).ToArray()) : "missing";
        string cache = Path.Combine(request.AnalysisCacheDirectory ?? Path.Combine(root, "cache"),
            AnalysisCache.Key(typeof(PresetCascade).Module.ModuleVersionId, assetsStamp));
        int attempt = 0;
        JsonArray costs = new();
        async Task<JsonObject> Try(HybridAnalyzeRequest candidate) => await AdoptAllocationAsync(await analyze(candidate with {
            OutputDirectory = Path.Combine(run, (++attempt).ToString()), AnalysisCacheDirectory = cache }, token), token);
        async Task<JsonObject> Layouts(HybridAnalyzeRequest candidate)
        {
            JsonObject plan = await Try(candidate with { VideoLayout = request.LayoutExplicit ? request.VideoLayout : "full_frame" });
            return Admission.Accepted(plan) || request.LayoutExplicit ? plan : await Try(candidate with { VideoLayout = "layered" });
        }
        async Task<JsonObject> States(HybridAnalyzeRequest candidate)
        {
            JsonObject plan = await Layouts(candidate);
            if (candidate.Interaction == "keep" || candidate.DaytimeState is not null ||
                plan["daytime_split"]?["status"]?.GetValue<string>() != "recognized") return plan;
            JsonObject? best = Admission.Accepted(plan) ? plan : null;
            foreach (JsonObject state in plan["daytime_split"]!["states"]!.AsArray().OfType<JsonObject>())
            {
                JsonObject statePlan = await Layouts(candidate with { DaytimeState = state["name"]!.GetValue<string>() });
                if (Admission.Accepted(statePlan) && (best is null || Admission.GroupCount(statePlan) < Admission.GroupCount(best))) best = statePlan;
            }
            return best ?? plan;
        }
        async Task<(JsonObject Plan, JsonArray Reasons)> Solve(string mode)
        {
            var reasons = new JsonArray();
            var current = request with { Interaction = mode, ViewMode = mode == "keep" ? "preserve" : "fixed_view",
                LiveOverlayPlacement = request.CustomSettings ? request.LiveOverlayPlacement : "foreground",
                DaytimeSplit = mode != "keep" || request.DaytimeSplit };
            JsonObject? last = null;
            int[]? off = null;
            for (int index = start; index < tiers.Length; index++)
            {
                string tier = tiers[index];
                current = current with { Preset = tier, LoopPreference = RetimeProfile.LoopPreferenceForPreset(tier) };
                if (mode == "off" && off is null)
                {
                    JsonObject original = await Layouts(current);
                    (off, costs) = await InteractionPolicy.ExclusionsAsync(original, current with { AnalysisCacheDirectory = cache }, tools,
                        Path.Combine(run, "interaction-cost"), token);
                }
                current = current with { ExcludedLayerIds = off is null ? request.ExcludedLayerIds :
                    (request.ExcludedLayerIds ?? []).Concat(off).Distinct().Order().ToArray() };
                last = await States(current);
                if (Admission.Accepted(last)) return (last, reasons);
                reasons.Add(tier + ": " + (Admission.GroupCount(last) > Admission.MaxVideoGroups ? "too_many_video_groups" : last["summary"]?["key"]?.GetValue<string>()));
                if (request.RetimeBudgetPercent is not null) break;
            }
            return (last!, reasons);
        }
        var selected = await Solve(interaction);
        JsonObject result = selected.Plan;
        JsonArray selectedCosts = costs.DeepClone().AsArray();
        if (!Admission.Accepted(result))
        {
            foreach (string mode in interaction == "keep" ? new[] { "fixed", "off" } : interaction == "fixed" ? new[] { "off" } : Array.Empty<string>())
            {
                var alternative = await Solve(mode);
                if (!Admission.Accepted(alternative.Plan)) continue;
                string path = Path.Combine(run, "suggested-" + mode + ".json");
                await VideoSceneBuilder.WriteJsonAsync(path, alternative.Plan, token);
                string key = mode == "fixed" ? "interaction.suggest_fixed" : "interaction.suggest_off";
                result["suggested_change"] = new JsonObject { ["verified"] = true, ["plan_path"] = path,
                    ["settings"] = new JsonObject { ["interaction"] = mode },
                    ["zh"] = MessageCatalog.Get(key, "zh"), ["en"] = MessageCatalog.Get(key, "en") };
                break;
            }
            if (Admission.Bakeable(result) && Admission.GroupCount(result) > Admission.MaxVideoGroups)
            {
                PlanBlockers.Add(result["blockers"]!.AsArray(), new Blocker(BlockerCode.TooManyVideoGroups, [Admission.MaxVideoGroups]));
                result["status"] = "requires_resolution";
                result["preset_rejection_reason"] = "too_many_video_groups";
                result["suitability"] = HybridScenePlanner.Suitability(result);
                PlanNarrative.Attach(result);
            }
        }
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
                    (result["settings"]?["daytime_state"] is JsonValue state ? "\n" + MessageCatalog.Get("preset.daytime", language, state.GetValue<string>()) : "");
        string stagedPlan = Path.Combine(run, "selected-plan.json");
        await VideoSceneBuilder.WriteJsonAsync(stagedPlan, result, token);
        File.Move(stagedPlan, Path.Combine(root, "plan.json"), true);
        return result;
    }
}
