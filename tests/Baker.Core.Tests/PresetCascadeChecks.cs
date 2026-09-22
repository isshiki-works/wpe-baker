using System.Text.Json.Nodes;
using Baker.Core;

internal static class PresetCascadeChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        var calls = new List<HybridAnalyzeRequest>();
        async Task<JsonObject> Run(string name, Func<HybridAnalyzeRequest, JsonObject> plan, string preset = "quality", bool custom = false, string interaction = "fixed")
        {
            calls.Clear();
            return await PresetCascade.AnalyzeAsync(new(2, "unused", "unused", Path.Combine(root, name),
                Preset: preset, CustomSettings: custom, Interaction: interaction), (request, _) => {
                calls.Add(request);
                return Task.FromResult(plan(request));
            }, CancellationToken.None);
        }
        JsonObject quality = await Run("preset-quality", r => Plan(r, true, 1));
        check(calls.Count == 1 && calls[0].ViewMode == "fixed_view" && calls[0].LiveOverlayPlacement == "foreground" &&
            quality["preset_requested"]!.GetValue<string>() == "quality" && quality["preset_applied"]!.GetValue<string>() == "quality" &&
            quality["preset_fallback_reason"] is null,
            "default fixed interaction is independent of quality retiming and hoists overlays");
        JsonObject layered = await Run("preset-layered", r => Plan(r, r.VideoLayout == "layered", 4));
        check(calls.Count == 2 && layered["preset_applied"]!.GetValue<string>() == "quality" && layered["route_fallback"] is null,
            "four layered groups are a normal quality path, not experimental");
        JsonObject balanced = await Run("preset-balanced", r => Plan(r, r.Preset == "balanced", 3));
        check(balanced["preset_applied"]!.GetValue<string>() == "balanced" &&
            calls.Last().ViewMode == "fixed_view" && calls.Last().ExcludedLayerIds is null &&
            balanced["applied_tradeoffs"]!["properties"]!.AsObject().Count == 0 && calls.All(r => r.UserProperties is null),
            "balanced changes retiming without disabling properties or widgets");
        JsonObject excluded = await Run("preset-excluded", r => Plan(r, r.ExcludedLayerIds?.Contains(9) == true, 2), interaction: "off");
        check(excluded["preset_applied"]!.GetValue<string>() == "quality" &&
            excluded["applied_tradeoffs"]!["excluded_layer_ids"]!.AsArray().Any(n => n!.GetValue<int>() == 9) &&
            calls.All(r => r.ExcludedLayerIds?.Contains(7) != true && r.ExcludedLayerIds?.Contains(8) != true),
            "interaction off removes pointer content but preserves clock and fps widgets");
        JsonObject efficiency = await Run("preset-efficiency", r => Plan(r, r.Preset == "efficiency", 2));
        check(efficiency["preset_applied"]!.GetValue<string>() == "efficiency" &&
            calls.All(r => r.ExcludedLayerIds is null), "efficiency only relaxes retiming and never removes clocks");
        JsonObject fallback = await Run("preset-fallback", r => Plan(r, true, 6));
        check(fallback["route_fallback"] is null && fallback["preset_applied"]!.GetValue<string>() == "none" &&
            calls.All(r => r.Interaction != "keep"), "six groups are rejected without silently restoring the legacy route");
        JsonObject none = await Run("preset-none", r => Plan(r, false, 5));
        check(none["preset_applied"]!.GetValue<string>() == "none", "unavailable across all tiers remains unavailable");
        JsonObject overLimit = await Run("preset-over-limit", r => Plan(r, r.LiveOverlayPlacement == "foreground", 5));
        check(overLimit["preset_applied"]!.GetValue<string>() == "none" &&
            overLimit["preset_rejection_reason"]!.GetValue<string>() == "too_many_video_groups" &&
            overLimit["blockers"]!.AsArray().Count == 1 && !Admission.Bakeable(overLimit),
            "five groups remain rejected within the requested policy");
        JsonObject custom = await Run("preset-custom", r => Plan(r, true, 1), custom: true);
        check(calls.Count == 1 && custom["custom_settings"]!.GetValue<bool>() && custom["preset_applied"]!.GetValue<string>() == "quality",
            "advanced overrides do not replace the retiming preset with a custom label");
        JsonObject suggestion = await Run("preset-suggestion", r => Plan(r, r.Interaction == "fixed", 2), interaction: "keep");
        check(suggestion["preset_applied"]!.GetValue<string>() == "none" &&
            suggestion["settings"]!["interaction"]!.GetValue<string>() == "keep" &&
            suggestion["suggested_change"]!["verified"]!.GetValue<bool>() &&
            suggestion["suggested_change"]!["settings"]!["interaction"]!.GetValue<string>() == "fixed",
            "a verified fixed-view suggestion is returned without applying it to the failed keep plan");
        JsonObject day = await Run("preset-day", r => {
            JsonObject result = Plan(r, r.DaytimeState is not null, r.DaytimeState == "night" ? 2 : 3);
            result["daytime_split"] = new JsonObject { ["status"] = "recognized", ["states"] = new JsonArray(
                new JsonObject { ["name"] = "day" }, new JsonObject { ["name"] = "night" }) };
            return result;
        });
        check(day["settings"]!["daytime_state"]!.GetValue<string>() == "night" &&
            day["summary"]!["zh"]!.GetValue<string>().Contains("按 night 时段生成", StringComparison.Ordinal),
            "recognized time states choose the bakeable result with fewest groups and name the selected state");
        string allocation = Path.Combine(root, "internal-allocation");
        Directory.CreateDirectory(Path.Combine(allocation, "loop-allocation-analysis"));
        var child = Plan(new(2, "s", "a", allocation, Preset: "quality", DaytimeState: "morning"), true, 3);
        await File.WriteAllTextAsync(Path.Combine(allocation, "loop-allocation-analysis", "plan.json"), child.ToJsonString());
        var outer = Plan(new(2, "s", "a", allocation), false, 5);
        outer["analysis_directory"] = allocation;
        outer["loop_allocation_fallback"] = new JsonObject { ["status"] = "candidate_found", ["resolution_basis"] = "residual_maskable" };
        JsonObject adopted = await PresetCascade.AdoptAllocationAsync(outer, CancellationToken.None);
        check(Admission.Accepted(adopted) && adopted["settings"]!["daytime_state"]!.GetValue<string>() == "morning" &&
            adopted["allocation_adopted"] is JsonObject, "a usable internal morning allocation reaches the outer result");
        var audio = new JsonObject { ["kind"] = "image", ["canvas_fraction"] = 1.0, ["tradeoff_kinds"] = new JsonArray("audio") };
        check(InteractionPolicy.ExpensiveAudio(audio, 10, 6) && !InteractionPolicy.ExpensiveAudio(audio, 10, 9) &&
            !InteractionPolicy.ExpensiveAudio(audio, null, null), "full-screen audio is removed only with measured costly contribution");
        audio["kind"] = "text";
        check(!InteractionPolicy.ExpensiveAudio(audio, 10, 1), "audio text remains protected even with an expensive synthetic reading");
        check(!PresetCascade.MeasureSourceByDefault && !PresetCascade.IsCustom(["--out", "--tools", "--preset"]) &&
            PresetCascade.IsCustom(["--video-layout"]) && PresetCascade.IsCustom(["--properties"]),
            "source power defaults off and semantic overrides are distinguished from transport options");

        string cache = Path.Combine(root, "preset-cache");
        int computed = 0;
        JsonObject first = AnalysisCache.Get(cache, "test", () => { computed++; return new JsonObject { ["value"] = 3 }; });
        first["value"] = 8;
        JsonObject hit = AnalysisCache.Get(cache, "test", () => { computed++; return new JsonObject(); });
        check(computed == 1 && hit["value"]!.GetValue<int>() == 3 &&
            AnalysisCache.Key(new { property = false }) != AnalysisCache.Key(new { property = true }),
            "persisted cache returns independent objects and different properties produce different keys");
        await PlannerIntegration(check, root);
    }

    private static async Task PlannerIntegration(Action<bool, string> check, string root)
    {
        string source = Path.Combine(root, "preset-source");
        string assets = Path.Combine(root, "preset-assets");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(assets);
        string scenePath = Path.Combine(source, "scene.json");
        await File.WriteAllTextAsync(scenePath, """
            {"general":{"orthogonalprojection":{"width":64,"height":32},"clearenabled":true},
             "objects":[{"id":1,"name":"background","image":"models/background.json","size":"64 32","origin":"32 16 0"}]}
            """);
        string trace = Path.Combine(root, "preset-trace.json");
        await File.WriteAllTextAsync(trace, new JsonObject {
            ["source"] = scenePath, ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(),
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(new JsonObject {
                ["id"] = 1, ["owner"] = 1, ["visible"] = true, ["has_mesh"] = true,
                ["effective_parallax_depth"] = new JsonArray(0, 0), ["materials"] = new JsonArray(new JsonObject {
                    ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
                    ["active_uniforms"] = new JsonArray("g_ModelViewProjectionMatrix"), ["textures"] = new JsonArray() }) })
        }.ToJsonString());
        JsonObject staticBudget = Plan(new(2, source, assets, root), true, 5);
        staticBudget["source"] = scenePath;
        staticBudget["runtime_evidence"] = trace;
        foreach (JsonObject group in staticBudget["video_groups"]!.AsArray().OfType<JsonObject>()) group["layer_ids"] = new JsonArray(1);
        JsonObject verifiedStaticBudget = await PresetCascade.AdoptAllocationAsync(staticBudget, CancellationToken.None);
        check(Admission.GroupCount(verifiedStaticBudget) == 0 && Admission.StaticGroupCount(verifiedStaticBudget) == 5 &&
            verifiedStaticBudget["static_group_budget"]?["status"]?.GetValue<string>() == "verified",
            "only an over-budget plan with source_static proof frees decoder slots for static caches");
        var planner = new HybridScenePlanner(new("not-started", "not-started", "not-started", []));
        var request = new HybridAnalyzeRequest(2, source, assets, Path.Combine(root, "preset-integration"), 64, 32,
            RuntimeTraceFile: trace);
        JsonObject result = await planner.AnalyzeAsync(request);
        HybridPlanFormat.Validate(result);
        check(result["preset_applied"]!.GetValue<string>() == "balanced" &&
            result["settings"]!["live_overlay_placement"]!.GetValue<string>() == "foreground",
            "Core default entry point starts at balanced and produces a valid real plan");
        string[] caches = Directory.GetFiles(Path.Combine(request.OutputDirectory, "cache"), "loop-*.json", SearchOption.AllDirectories);
        var modified = caches.Select(File.GetLastWriteTimeUtc).ToArray();
        JsonObject repeat = await planner.AnalyzeAsync(request with { CustomSettings = true, VideoLayout = "layered", Preset = "balanced", LoopPreference = "balanced" });
        check(caches.Length > 0 && caches.Select(File.GetLastWriteTimeUtc).SequenceEqual(modified) &&
            Directory.GetFiles(Path.Combine(request.OutputDirectory, "cache"), "loop-*.json", SearchOption.AllDirectories).Length == caches.Length &&
            repeat["preset_applied"]!.GetValue<string>() == "balanced" && repeat["custom_settings"]!.GetValue<bool>(),
            "a layout edit reuses persistent period evidence and can reuse the same analysis directory");
    }

    private static JsonObject Plan(HybridAnalyzeRequest request, bool usable, int count) => new()
    {
        ["summary"] = new JsonObject { ["key"] = usable ? "summary.bakeable" : "summary.blocked", ["zh"] = "", ["en"] = "" },
        ["settings"] = new JsonObject { ["daytime_state"] = request.DaytimeState, ["preset"] = request.Preset,
            ["interaction"] = request.Interaction, ["excluded_layer_ids"] = new JsonArray((request.ExcludedLayerIds ?? []).Select(id => (JsonNode)JsonValue.Create(id)).ToArray()) },
        ["route"] = "whole_layer", ["has_parallax"] = true,
        ["video_groups"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()),
        ["blockers"] = new JsonArray(),
        // 可烘按结构判定（Admission.Bakeable）：有首个候选、无 blocker。
        ["loop"] = new JsonObject { ["candidates"] = usable ? new JsonArray(new JsonObject { ["frames"] = 600 }) : new JsonArray() },
        ["tradeoff_options"] = new JsonObject { ["options"] = new JsonArray(new JsonObject { ["turn_off_kinds"] = new JsonArray("fps") }) },
        ["layers"] = new JsonArray(
            new JsonObject { ["id"] = 7, ["tradeoff_class"] = "tradeoff", ["tradeoff_kinds"] = new JsonArray("fps"),
                ["visible_property"] = new JsonObject { ["key"] = "show", ["off_value"] = false } },
            new JsonObject { ["id"] = 8, ["tradeoff_class"] = "tradeoff", ["tradeoff_kinds"] = new JsonArray("clock") },
            new JsonObject { ["id"] = 9, ["tradeoff_class"] = "tradeoff", ["tradeoff_kinds"] = new JsonArray("pointer") })
    };
}
