using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class HybridHierarchyGeneralizationChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "hierarchy-generalization-source");
        Directory.CreateDirectory(sourceDirectory);
        var objects = new JsonArray(
            new JsonObject { ["id"] = 100, ["name"] = "independent static parent", ["origin"] = "8 4 0" },
            new JsonObject { ["id"] = 102, ["parent"] = 100, ["image"] = "models/background.json" },
            new JsonObject { ["id"] = 101, ["parent"] = 100, ["image"] = "models/media.json" },
            new JsonObject { ["id"] = 103, ["parent"] = 100, ["particle"] = "particle.json" },
            new JsonObject { ["id"] = 400, ["name"] = "dynamic access parent" },
            new JsonObject { ["id"] = 401, ["parent"] = 400, ["image"] = "models/static-sibling.json" },
            new JsonObject { ["id"] = 402, ["parent"] = 400, ["image"] = "models/controller.json" },
            new JsonObject { ["id"] = 500, ["image"] = "models/fault-target.json" });
        string sourcePath = Path.Combine(sourceDirectory, "scene.json");
        await File.WriteAllTextAsync(sourcePath, new JsonObject {
            ["general"] = new JsonObject {
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = true },
            ["objects"] = objects }.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "particle.json"), "{\"emitter\":[]}");
        string tracePath = Path.Combine(root, "hierarchy-generalization-trace.json");
        await File.WriteAllTextAsync(tracePath, new JsonObject {
            ["source"] = sourcePath, ["status"] = "complete",
            ["runtime_dependencies"] = new JsonArray(
                new JsonObject { ["owner"] = 101, ["target"] = -1, ["operation"] = "input", ["property"] = "media",
                    ["binding"] = "system_media_texture", ["initialization"] = false },
                new JsonObject { ["owner"] = 103, ["target"] = -1, ["operation"] = "input", ["property"] = "audio",
                    ["binding"] = "particle_emitter", ["initialization"] = true },
                new JsonObject { ["owner"] = 402, ["target"] = -1, ["operation"] = "input", ["property"] = "pointer",
                    ["initialization"] = false },
                new JsonObject { ["owner"] = 402, ["target"] = -1, ["operation"] = "lookup", ["property"] = "runtime child",
                    ["initialization"] = false },
                new JsonObject { ["owner"] = 102, ["target"] = 500, ["operation"] = "write", ["property"] = "alpha",
                    ["initialization"] = false }),
            ["source_script_error_count"] = 1,
            ["source_script_errors"] = new JsonArray(new JsonObject {
                ["binding_id"] = 17, ["owner_layer_id"] = 102, ["owner_name"] = "background",
                ["property"] = "text", ["phase"] = "update", ["script_sha"] = "abc123",
                ["message"] = "TypeError: test fault", ["stack"] = "update@test:1" }),
            ["runtime_layers"] = new JsonArray(objects.OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
                ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(), ["visible"] = true,
                ["has_mesh"] = obj["id"]!.GetValue<int>() is not (100 or 400),
                ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() })
            }).ToArray()) }.ToJsonString());

        var plan = await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
            new(2, sourceDirectory, root, Path.Combine(root, "hierarchy-generalization-analysis"), 64, 32,
                RuntimeTraceFile: tracePath, VideoLayout: "layered"));
        var layers = plan["layers"]!.AsArray().OfType<JsonObject>().ToDictionary(layer => layer["id"]!.GetValue<int>());
        check(layers[102]["allocation_root"]!.GetValue<int>() == 100 && layers[102]["live"]!.GetValue<bool>(),
            "an unresolved runtime target keeps static splitting fail-closed across the scene");
        check(layers[401]["allocation_root"]!.GetValue<int>() == 400 && layers[401]["live"]!.GetValue<bool>() &&
            layers[401]["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "shares_live_hierarchy"),
            "unknown runtime access still protects the owner author root");
        check(layers[101]["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "observed_media") &&
            layers[103]["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "observed_audio"),
            "native system-media and particle-audio input dependencies retain their exact owners without JavaScript");
        check(layers[102]["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "source_script_error") &&
            layers[500]["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "written_by_live_controller") &&
            plan["source_script_error_count"]!.GetValue<int>() == 1 &&
            plan["source_script_errors"]![0]!["message"]!.GetValue<string>() == "TypeError: test fault" &&
            plan["source_script_error_evidence"]!["status"]!.GetValue<string>() == "available",
            "a faulted source binding retains its exact owner, preserves raw diagnostics and closes observed writes");
        var unknownFaultTrace = JsonNode.Parse(await File.ReadAllTextAsync(tracePath))!.AsObject();
        unknownFaultTrace["source_script_errors"]![0]!["owner_layer_id"] = 999;
        string unknownFaultTracePath = Path.Combine(root, "unknown-script-fault-trace.json");
        await File.WriteAllTextAsync(unknownFaultTracePath, unknownFaultTrace.ToJsonString());
        bool unknownFaultRejected = false;
        try
        {
            await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, sourceDirectory, root, Path.Combine(root, "unknown-script-fault-analysis"), 64, 32,
                    RuntimeTraceFile: unknownFaultTracePath, VideoLayout: "layered"));
        }
        catch (InvalidDataException error) when (error.Message.Contains("known authored owner", StringComparison.Ordinal))
        {
            unknownFaultRejected = true;
        }
        check(unknownFaultRejected, "an unattributed source script fault fails closed instead of guessing a safe allocation");

        using var source = new ProjectSource(sourceDirectory);
        string noLoopOutput = Path.Combine(root, "stale-cost-no-loop");
        var staleCostPlan = plan.DeepClone().AsObject();
        // Model an older version-2 saved proposal, but retain current source and runtime evidence.
        staleCostPlan["schema_version"] = 2;
        staleCostPlan["blockers"] = new JsonArray();
        staleCostPlan["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(102), ["include_scene_clear"] = true });
        foreach (JsonObject layer in staleCostPlan["layers"]!.AsArray().OfType<JsonObject>())
            layer.Remove("allocation_root");
        staleCostPlan["loop"] = new JsonObject { ["status"] = "observed", ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) };
        JsonObject noLoop = await new HybridBakeService(new("not-started", "not-started", "not-started", [])).BakeAsync(
            new(2, staleCostPlan, noLoopOutput));
        check(noLoop["status"]!.GetValue<string>() == "candidate_rejected_no_loop" &&
            !Directory.Exists(noLoopOutput + ".cost-probe") && noLoop["source_script_error_count"]!.GetValue<int>() == 1 &&
            noLoop["source_script_errors"]![0]!["stack"]!.GetValue<string>() == "update@test:1",
            "a malicious saved observed loop is re-parsed and rejected while source script faults remain without GPU work");

        var classifyLateFaults = typeof(HybridBakeService).GetMethod("ClassifyLateSourceScriptErrors",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonObject Fault(int owner, int binding) => new() {
            ["binding_id"] = binding, ["owner_layer_id"] = owner, ["owner_name"] = "late", ["property"] = "text",
            ["phase"] = "update", ["script_sha"] = "late-sha", ["message"] = "TypeError: late", ["stack"] = "late@test:1" };
        var lateFaultObjects = new Dictionary<int, JsonObject> {
            [9] = new JsonObject { ["id"] = 9 }, [10] = new JsonObject { ["id"] = 10, ["parent"] = 9 },
            [20] = new JsonObject { ["id"] = 20 }, [30] = new JsonObject { ["id"] = 30 } };
        var lateFaults = new JsonArray(Fault(10, 1), Fault(9, 2), Fault(20, 3), Fault(30, 4));
        var classifiedLateFaults = (JsonObject)classifyLateFaults.Invoke(null, [lateFaults,
            new HashSet<int> { 10 }, new HashSet<int> { 20 }, lateFaultObjects])!;
        check(classifiedLateFaults["unsafe"]!.AsArray().Count == 3 &&
            classifiedLateFaults["unsafe"]!.AsArray().OfType<JsonObject>().Any(error =>
                error["rejection_reason"]!.GetValue<string>() == "source_script_fault_in_baked_layer") &&
            classifiedLateFaults["unsafe"]!.AsArray().OfType<JsonObject>().Any(error =>
                error["rejection_reason"]!.GetValue<string>() == "source_script_fault_in_retained_ancestor") &&
            classifiedLateFaults["external_live"]!.AsArray().Single()!["owner_layer_id"]!.GetValue<int>() == 20 &&
            lateFaults.OfType<JsonObject>().All(error => error["rejection_reason"] is null),
            "a fault first seen in full capture rejects baked and ancestor owners while preserving raw external-live diagnostics");

        async Task<JsonObject> PlanRuntimeParent(string name, bool describeParent, bool scriptFaultEvidence = true)
        {
            string directory = Path.Combine(root, name + "-source");
            Directory.CreateDirectory(directory);
            var sourceObjects = new JsonArray(
                new JsonObject { ["id"] = 700, ["name"] = "static parent", ["origin"] = "8 4 0" },
                new JsonObject { ["id"] = 701, ["parent"] = 700, ["image"] = "models/live.json" },
                new JsonObject { ["id"] = 702, ["parent"] = 700, ["image"] = "models/baked.json" });
            string scenePath = Path.Combine(directory, "scene.json");
            await File.WriteAllTextAsync(scenePath, new JsonObject {
                ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 } },
                ["objects"] = sourceObjects }.ToJsonString());
            JsonObject RuntimeLayer(int id, bool mesh) => new() {
                ["id"] = id, ["owner"] = id, ["visible"] = true, ["has_mesh"] = mesh,
                ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() }) };
            var runtimeLayers = new JsonArray(RuntimeLayer(701, true), RuntimeLayer(702, true));
            if (describeParent) runtimeLayers.Insert(0, RuntimeLayer(700, false));
            string runtimePath = Path.Combine(root, name + "-trace.json");
            var runtime = new JsonObject {
                ["source"] = scenePath, ["status"] = "complete",
                ["runtime_dependencies"] = new JsonArray(new JsonObject {
                    ["owner"] = 701, ["target"] = -1, ["operation"] = "input", ["property"] = "pointer", ["initialization"] = false }),
                ["runtime_layers"] = runtimeLayers };
            if (scriptFaultEvidence)
            {
                runtime["source_script_error_count"] = 0;
                runtime["source_script_errors"] = new JsonArray();
            }
            await File.WriteAllTextAsync(runtimePath, runtime.ToJsonString());
            return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, directory, root, Path.Combine(root, name + "-analysis"), 64, 32,
                    RuntimeTraceFile: runtimePath, VideoLayout: "layered"));
        }
        JsonObject describedParent = await PlanRuntimeParent("described-static-parent", true);
        JsonObject missingParent = await PlanRuntimeParent("missing-static-parent", false);
        JsonObject legacyFaultEvidence = await PlanRuntimeParent("legacy-script-fault-evidence", true, false);
        JsonObject Layer(JsonObject candidate, int id) => candidate["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id);
        check(Layer(describedParent, 702)["allocation_root"]!.GetValue<int>() == 702 &&
            !Layer(describedParent, 702)["live"]!.GetValue<bool>() &&
            Layer(missingParent, 702)["allocation_root"]!.GetValue<int>() == 700 &&
            Layer(missingParent, 702)["live"]!.GetValue<bool>(),
            "an authored has_mesh:false container enables static splitting while a missing runtime node remains fail-closed");
        check(legacyFaultEvidence["source_script_error_evidence"]!["status"]!.GetValue<string>() == "not_available" &&
            legacyFaultEvidence["source_script_error_count"] is null && legacyFaultEvidence["source_script_errors"] is null &&
            legacyFaultEvidence["blockers"]!.AsArray().Any(blocker => blocker!.GetValue<string>().Contains("source script fault metadata", StringComparison.Ordinal)),
            "an older explicit trace reports unavailable script-fault evidence and requires refresh without inventing zero errors");

    }
}
