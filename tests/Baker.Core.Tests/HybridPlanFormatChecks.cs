using System.Text.Json.Nodes;
using Baker.Core;

internal static class HybridPlanFormatChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string outputRoot)
    {
        JsonObject Plan() => JsonNode.Parse("""
            {"schema_version":3,"kind":"hybrid_video","layers":[
              {"id":1,"root":1,"allocation_root":1,"parent":null},
             {"id":2,"root":1,"allocation_root":2,"parent":1}],
             "root_order":[1,2],"source_root_order":[1],
             "video_groups":[{"id":"group-1","root_ids":[2],"layer_ids":[2],"parent_id":1,
               "parent_transform":{"origin":[10,20,0],"scale":[1,1,1]}}],
             "composition":[{"video_group":"group-1"}]}
            """)!.AsObject();
        void Reject(JsonObject plan, string description)
        {
            bool rejected = false;
            try { HybridPlanFormat.Validate(plan); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, description);
        }
        var current = Plan();
        HybridPlanFormat.Validate(current);
        check(current["schema_version"]!.GetValue<int>() == HybridPlanFormat.CurrentVersion,
            "plan v3 explicitly represents author roots, allocation units and parent placement");
        var currentRoundTrip = JsonNode.Parse(current.ToJsonString())!.AsObject();
        HybridPlanFormat.Validate(currentRoundTrip);
        check(JsonNode.DeepEquals(current, currentRoundTrip), "current v3 plan survives a JSON round trip without losing hierarchy metadata");
        var legacy = JsonNode.Parse("""
            {"schema_version":2,"kind":"hybrid_video","layers":[{"id":1,"root":1},{"id":2,"root":1}],
             "root_order":[1],"video_groups":[{"id":"group-1","root_ids":[1],"layer_ids":[1,2]}]}
            """)!.AsObject();
        HybridPlanFormat.Validate(legacy);
        foreach (var template in new[] { current, legacy })
        {
            var escapedGroup = template.DeepClone().AsObject();
            escapedGroup["video_groups"]![0]!["id"] = "../outside";
            if (escapedGroup["composition"] is JsonArray entries) entries[0]!["video_group"] = "../outside";
            Reject(escapedGroup, $"plan v{template["schema_version"]} rejects a group path escaping owned scratch storage");
        }
        check(!legacy["layers"]![0]!.AsObject().ContainsKey("allocation_root"),
            "genuine legacy v2 remains readable without inventing allocation metadata");
        var legacyRoundTrip = JsonNode.Parse(legacy.ToJsonString())!.AsObject();
        HybridPlanFormat.Validate(legacyRoundTrip);
        check(JsonNode.DeepEquals(legacy, legacyRoundTrip), "legacy v2 plan survives a JSON round trip without gaining v3 metadata");
        var disguised = Plan(); disguised["schema_version"] = 2;
        Reject(disguised, "subtree semantics cannot masquerade as a legacy v2 plan");
        var missing = Plan(); missing["layers"]![1]!.AsObject().Remove("allocation_root");
        Reject(missing, "v3 does not silently fall back when allocation data is absent");
        var noParent = Plan(); noParent["video_groups"]![0]!.AsObject().Remove("parent_transform");
        Reject(noParent, "v3 parented groups require explicit placement information");
        var badUnit = Plan(); badUnit["layers"]![1]!["allocation_root"] = 77;
        Reject(badUnit, "v3 rejects unknown allocation units");
        var cyclic = Plan(); cyclic["layers"]![0]!["parent"] = 2;
        Reject(cyclic, "v3 parent cycles reject before projection or rendering");
        var repeatedGroup = Plan(); repeatedGroup["composition"]!.AsArray().Add(new JsonObject { ["video_group"] = "group-1" });
        Reject(repeatedGroup, "v3 composition cannot write the same video group twice");
        var doubledRoot = Plan(); doubledRoot["composition"]!.AsArray().Add(new JsonObject { ["live_root"] = 2 });
        Reject(doubledRoot, "v3 composition cannot retain a video allocation as live too");
        var unknown = Plan(); unknown["schema_version"] = 99;
        Reject(unknown, "unknown future plan versions reject explicitly");
        var tools = new NativeTools("not-started", "not-started", "not-started", []);
        async Task RejectBeforeTools(Func<string, Task> action, string name)
        {
            string output = Path.Combine(outputRoot, name);
            bool rejected = false;
            try { await action(output); }
            catch (InvalidDataException error) when (error.Message.Contains("supported Scene plan", StringComparison.Ordinal)) { rejected = true; }
            check(rejected && !Directory.Exists(output), name + " rejects plan version before tools and output creation");
        }
        await RejectBeforeTools(async output => { await new HybridBakeService(tools).BakeAsync(new(2, unknown, output)); }, "bake-version-boundary");
        current["blockers"] = new JsonArray(new Blocker(BlockerCode.PerspectiveNeedsScreenspace).ToNode());
        bool blockerReached = false;
        try { await new HybridBakeService(tools).BakeAsync(new(2, current, Path.Combine(outputRoot, "v3-blocked"))); }
        catch (InvalidDataException error) when (error.Message.StartsWith("Resolve the plan", StringComparison.Ordinal)) { blockerReached = true; }
        check(blockerReached && !Directory.Exists(Path.Combine(outputRoot, "v3-blocked")),
            "request v2 accepts plan v3 and still enforces the pre-render blocker");
    }
}
