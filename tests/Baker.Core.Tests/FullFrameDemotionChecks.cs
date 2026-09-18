using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>全幅准入的尾组降级：可达性判定、采纳条件、记录字段与冲突文案。</summary>
internal static class FullFrameDemotionChecks
{
    private const int Base = 801, Spectrum = 802, LabelA = 803, LabelB = 804;

    private static readonly Type DemotionType = typeof(HybridPlanFormat).Assembly.GetType("Baker.Core.FullFrameDemotion")!;
    private static readonly MethodInfo RetentionMethod = DemotionType.GetMethod("FullFrameSingleGroupRetention",
        BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(JsonObject)], null)!;
    private static readonly MethodInfo ConflictMethod = typeof(HybridScenePlanner).GetMethod("FullFrameConflict",
        BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo AllocationMethod = typeof(HybridScenePlanner).GetMethod("ApplyAllocation",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        ArgumentNullException.ThrowIfNull(check);
        // 基线：同一场景按 layered 分析不触发任何降级，用它逐字段对照被退回的 root。
        JsonObject layered = await AnalyzeAsync(root, "layered", Scene(), true, new JsonArray(), "layered");
        JsonObject demoted = await AnalyzeAsync(root, "applied", Scene(), true, new JsonArray());
        var groups = demoted["video_groups"]!.AsArray();
        JsonObject record = demoted["layout_admission_demotion"]!.AsObject();
        check(layered["video_groups"]!.AsArray().Count == 2 && groups.Count == 1 &&
            Ids(groups[0]!["root_ids"]).SequenceEqual([Base]) && groups[0]!["include_scene_clear"]!.GetValue<bool>() &&
            !groups[0]!["transparent"]!.GetValue<bool>() &&
            demoted["video_layout_admission"]!["status"]!.GetValue<string>() == "planned_layout_allowed_after_demotion" &&
            demoted["blockers"]!.AsArray().Count == 0 &&
            record["status"]!.GetValue<string>() == "applied" &&
            Ids(record["demoted_root_ids"]).SequenceEqual([LabelA, LabelB]) &&
            record["demoted_layer_names"]!.AsArray().Select(name => name!.GetValue<string>()).SequenceEqual(["Tiny label A", "Tiny label B"]) &&
            Math.Abs(record["demoted_canvas_fraction_sum"]!.GetValue<double>() - 0.007812) < 1e-6 &&
            record["kept_group_canvas_fraction_max"]!.GetValue<double>() == 1 &&
            Ids(record["blocking_live_root_ids"]).SequenceEqual([Spectrum]),
            "full-frame tail demotion leaves one opaque group and records the roots, layers and canvas share it gave up");

        int[] allocationOrder = Ids(demoted["source_allocation_order"]);
        int[] composedLive = demoted["composition"]!.AsArray().OfType<JsonObject>()
            .Where(entry => entry["live_root"] is JsonValue).Select(entry => entry["live_root"]!.GetValue<int>()).ToArray();
        int[] positions = composedLive.Select(live => Array.IndexOf(allocationOrder, live)).ToArray();
        bool preserved = new[] { LabelA, LabelB }.All(retained =>
            JsonNode.DeepEquals(Role(demoted, retained)["parallax_depth"], Role(layered, retained)["parallax_depth"]) &&
            JsonNode.DeepEquals(Layer(demoted, retained)["parent"], Layer(layered, retained)["parent"]) &&
            JsonNode.DeepEquals(Layer(demoted, retained)["canvas_fraction"], Layer(layered, retained)["canvas_fraction"]) &&
            Subtree(demoted, retained).SequenceEqual(Subtree(layered, retained)) &&
            Layer(demoted, retained)["live"]!.GetValue<bool>() &&
            Layer(demoted, retained)["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "retained_as_foreground_suffix"));
        check(composedLive.SequenceEqual([Spectrum, LabelA, LabelB]) && positions.SequenceEqual([1, 2, 3]) && preserved,
            "demotion keeps live roots in source allocation order and leaves their depth, parent and subtree untouched");

        // 占比条件不满足：退回方案结构上仍可行，但视频不再承担画面主体，维持原拒绝并给出可执行指令。
        JsonObject wide = await AnalyzeAsync(root, "wide", Scene(64, 32), true, new JsonArray());
        string blocker = wide["blockers"]!.AsArray().Select(item => item!.GetValue<string>())
            .Single(item => item.StartsWith("Full-frame mode", StringComparison.Ordinal));
        check(wide["video_groups"]!.AsArray().Count == 2 &&
            wide["video_layout_admission"]!["status"]!.GetValue<string>() == "requires_user_choice" &&
            wide["layout_admission_demotion"]!["status"]!.GetValue<string>() == "not_applied" &&
            wide["layout_admission_demotion"]!["reason"]!.GetValue<string>().Contains("canvas fraction", StringComparison.Ordinal) &&
            wide["full_frame_retention"]!["status"]!.GetValue<string>() == "available" &&
            blocker.Contains("--retain-live 803,804", StringComparison.Ordinal) &&
            blocker.Contains("Tiny label A", StringComparison.Ordinal),
            "a demotion that would stop the video carrying the image is refused and offered as an explicit --retain-live command");

        JsonObject snapshot = wide.DeepClone().AsObject();
        int[]? retention = Retention(wide);
        check(retention is not null && retention.SequenceEqual([LabelA, LabelB]) && JsonNode.DeepEquals(snapshot, wide),
            "single-group retention returns the complete tail suffix and runs only on a clone");

        JsonObject reallocated = Allocate(wide, [LabelA], new JsonArray());
        check(reallocated["video_groups"]!.AsArray().Count == 1 &&
            reallocated["video_groups"]![0]!["include_scene_clear"]!.GetValue<bool>() &&
            Conflict(reallocated) is null && reallocated["full_frame_retention"] is null,
            "the retention advice applies cleanly through ApplyAllocation and drops the stale admission record");

        JsonObject unknown = await AnalyzeAsync(root, "unknown", Scene(4, 2, sizeLabelB: false), true, new JsonArray());
        check(unknown["video_groups"]!.AsArray().Count == 2 &&
            unknown["video_layout_admission"]!["status"]!.GetValue<string>() == "requires_user_choice" &&
            unknown["layout_admission_demotion"]!["reason"]!.GetValue<string>()
                .Contains("unknown canvas fraction", StringComparison.Ordinal),
            "an unknown canvas fraction among the demoted layers keeps the original rejection");

        // 依赖闭包把保留组也拉成实时：视频组会消失，必须维持拒绝并带出分配层的原因文本。
        JsonObject closure = await AnalyzeAsync(root, "closure", Scene(), true, new JsonArray(
            new JsonObject { ["owner"] = LabelA, ["target"] = Base, ["operation"] = "write", ["property"] = "visible", ["initialization"] = false }));
        check(closure["video_groups"]!.AsArray().Count == 2 &&
            closure["video_layout_admission"]!["status"]!.GetValue<string>() == "requires_user_choice" &&
            closure["layout_admission_demotion"]!["status"]!.GetValue<string>() == "not_applied" &&
            closure["full_frame_retention"]!["status"]!.GetValue<string>() == "unavailable" &&
            closure["layout_admission_demotion"]!["allocation_reason"]!.GetValue<string>().Length > 0,
            "a dependency closure that would take the retained opaque group live keeps the rejection and reports the allocation reason");

        JsonObject unclearedPlan = await AnalyzeAsync(root, "uncleared", Scene(), false, new JsonArray());
        string unclearedBlocker = unclearedPlan["blockers"]!.AsArray().Select(item => item!.GetValue<string>())
            .Single(item => item.StartsWith("Full-frame mode", StringComparison.Ordinal));
        check(unclearedPlan["video_groups"]![0]!["include_scene_clear"]!.GetValue<bool>() == false &&
            Retention(unclearedPlan) is null &&
            unclearedPlan["full_frame_retention"]!["status"]!.GetValue<string>() == "unavailable" &&
            unclearedPlan["layout_admission_demotion"]!["status"]!.GetValue<string>() == "not_applied" &&
            !unclearedBlocker.Contains("--retain-live", StringComparison.Ordinal),
            "without an opaque first group there is no retention, no demotion and no --retain-live advice");

        JsonObject crowded = CrowdedPlan();
        string crowdedBlocker = Conflict(crowded)!;
        check(crowdedBlocker.StartsWith("Full-frame mode requires one opaque video group", StringComparison.Ordinal) &&
            crowdedBlocker.Contains("into 2 video group(s)", StringComparison.Ordinal) &&
            crowdedBlocker.Contains("Blocker 5", StringComparison.Ordinal) && !crowdedBlocker.Contains("Blocker 6", StringComparison.Ordinal) &&
            crowdedBlocker.Contains("and 2 more", StringComparison.Ordinal) &&
            !crowdedBlocker.Contains("overlays in the foreground", StringComparison.Ordinal),
            "the full-frame blocker states the current split, lists at most five blockers and drops impossible foreground advice");

        crowded["occlusion_tradeoff"] = new JsonObject { ["promoted_roots"] = new JsonArray(new JsonObject { ["root_id"] = 11 }) };
        check(Conflict(crowded)!.Contains("overlays in the foreground", StringComparison.Ordinal),
            "foreground promotion is offered exactly when the plan has a promotable candidate");
    }

    private static JsonObject Role(JsonObject plan, int root) => plan["root_roles"]!.AsArray().OfType<JsonObject>()
        .Single(role => role["root_id"]!.GetValue<int>() == root);

    private static JsonObject Layer(JsonObject plan, int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
        .Single(layer => layer["id"]!.GetValue<int>() == id);

    private static int[] Subtree(JsonObject plan, int root) => plan["layers"]!.AsArray().OfType<JsonObject>()
        .Where(layer => layer["allocation_root"]!.GetValue<int>() == root).Select(layer => layer["id"]!.GetValue<int>()).ToArray();

    private static int[] Ids(JsonNode? node) => (node as JsonArray)?.Select(item => item!.GetValue<int>()).ToArray() ?? [];

    private static int[]? Retention(JsonObject plan) => (int[]?)RetentionMethod.Invoke(null, [plan]);

    private static string? Conflict(JsonObject plan) => (string?)ConflictMethod.Invoke(null, [plan]);

    private static JsonObject Allocate(JsonObject plan, int[] roots, JsonArray dependencies) =>
        (JsonObject)AllocationMethod.Invoke(null, [plan, roots, dependencies])!;

    /// <summary>不透明底图 + 可见的 image 型音频频谱实时层 + 两个小文本视频 root。</summary>
    private static JsonArray Scene(double labelWidth = 4, double labelHeight = 2, bool sizeLabelB = true)
    {
        var labelB = new JsonObject { ["id"] = LabelB, ["name"] = "Tiny label B", ["text"] = "b" };
        if (sizeLabelB) labelB["size"] = new JsonArray(labelWidth, labelHeight);
        return new JsonArray(
            new JsonObject { ["id"] = Base, ["name"] = "Base plate", ["text"] = "base", ["size"] = new JsonArray(64, 32) },
            new JsonObject { ["id"] = Spectrum, ["name"] = "Audio spectrum", ["image"] = "models/spectrum.json", ["size"] = new JsonArray(32, 16) },
            new JsonObject { ["id"] = LabelA, ["name"] = "Tiny label A", ["text"] = "a", ["size"] = new JsonArray(labelWidth, labelHeight) },
            labelB);
    }

    /// <summary>七个交织实时层的合成 plan，只用于检查冲突文案的清单上限。</summary>
    private static JsonObject CrowdedPlan()
    {
        var composition = new JsonArray(new JsonObject { ["video_group"] = "group-1" });
        var layers = new JsonArray();
        for (int index = 1; index <= 7; index++)
        {
            composition.Add(new JsonObject { ["live_root"] = 10 + index });
            layers.Add(new JsonObject { ["id"] = 10 + index, ["root"] = 10 + index, ["name"] = "Blocker " + index,
                ["visible"] = true, ["drawable"] = true });
        }
        composition.Add(new JsonObject { ["video_group"] = "group-2" });
        return new JsonObject {
            ["schema_version"] = 2, ["kind"] = "hybrid_video",
            ["settings"] = new JsonObject { ["video_layout"] = "full_frame" },
            ["video_groups"] = new JsonArray(
                new JsonObject { ["id"] = "group-1", ["include_scene_clear"] = true },
                new JsonObject { ["id"] = "group-2", ["include_scene_clear"] = false }),
            ["composition"] = composition, ["layers"] = layers };
    }

    private static async Task<JsonObject> AnalyzeAsync(string root, string name, JsonArray objects, bool clearEnabled,
        JsonArray dependencies, string layout = "full_frame")
    {
        string directory = Path.Combine(root, "demotion-" + name);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "scene.json"), new JsonObject {
            ["general"] = new JsonObject {
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = clearEnabled },
            ["objects"] = objects.DeepClone() }.ToJsonString());
        string trace = Path.Combine(root, "demotion-" + name + "-trace.json");
        await File.WriteAllTextAsync(trace, new JsonObject {
            ["source"] = Path.Combine(directory, "scene.json"), ["status"] = "complete",
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_dependencies"] = dependencies.DeepClone(),
            ["runtime_layers"] = new JsonArray(objects.OfType<JsonObject>().Select(layer => (JsonNode)new JsonObject {
                ["id"] = layer["id"]!.DeepClone(), ["owner"] = layer["id"]!.DeepClone(), ["visible"] = true,
                ["has_mesh"] = true, ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject {
                    ["uses_audio_spectrum"] = layer["id"]!.GetValue<int>() == Spectrum,
                    ["textures"] = new JsonArray() }) }).ToArray()) }.ToJsonString());
        return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
            new(2, directory, root, Path.Combine(root, "demotion-analysis-" + name), 64, 32,
                RuntimeTraceFile: trace, VideoLayout: layout));
    }
}
