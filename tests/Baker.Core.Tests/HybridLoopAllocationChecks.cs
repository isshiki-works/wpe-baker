using System.Reflection;
using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

internal static class HybridLoopAllocationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var method = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridLoopAllocation")!
            .GetMethod("Propose", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonObject? Propose(JsonObject plan, JsonObject scene) =>
            (JsonObject?)method.Invoke(null, [plan, scene]);
        static int[] Ids(JsonObject proposal, string key) =>
            proposal[key]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        static (JsonObject Plan, JsonObject Scene) Fixture() => (
            JsonNode.Parse("""
            {
              "settings": { "retain_live_root_ids": [30] },
              "video_groups": [{ "layer_ids": [11, 12, 20, 50] }],
              "layers": [
                { "id": 11, "root": 10, "allocation_root": 11 },
                { "id": 12, "root": 10, "allocation_root": 12 },
                { "id": 20, "root": 20, "allocation_root": 20 },
                { "id": 50, "root": 50, "allocation_root": 50 }
              ],
              "loop": {
                "unresolved": ["No observed period", { "owner_layer_id": 12 }],
                "observed": { "analytic_evidence": {
                  "unresolved": [{ "owner_layer_id": 20 }, { "owner_layer_id": 999 }, { "owner_layer_id": 40 }]
                } }
              }
            }
            """)!.AsObject(),
            JsonNode.Parse("""
            { "objects": [
              { "id": 30, "image": "retained.json" },
              { "id": 10 },
              { "id": 11, "parent": 10, "particle": "arbitrary.json" },
              { "id": 12, "parent": 10, "image": "sibling.json" },
              { "id": 20, "image": "unresolved.json" },
              { "id": 40, "particle": "already-live.json" },
              { "id": 50, "image": "periodic.json" }
            ] }
            """)!.AsObject());

        var (plan, scene) = Fixture();
        string originalPlan = plan.ToJsonString(), originalScene = scene.ToJsonString();
        var proposal = Propose(plan, scene)!;
        check(proposal is not null && Ids(proposal, "retain_live_root_ids").SequenceEqual([30, 10, 20]) &&
            Ids(proposal, "added_live_root_ids").SequenceEqual([10, 20]),
            "loop fallback merges existing retention and maps split allocation children to author roots");
        check(Ids(proposal!, "trigger_layer_ids").SequenceEqual([11, 12, 20]) &&
            Ids(proposal!, "remaining_baked_layer_ids").SequenceEqual([50]),
            "loop fallback includes nested analytic owners and particles but ignores unknown or unselected owners");
        check(plan.ToJsonString() == originalPlan && scene.ToJsonString() == originalScene,
            "loop allocation proposals leave source objects and the failed plan unchanged");

        plan["settings"]!["retain_live_root_ids"] = proposal!["retain_live_root_ids"]!.DeepClone();
        check(Propose(plan, scene) is null, "loop fallback declines a retry with no newly retained author root");

        (plan, scene) = Fixture();
        plan["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(11, 12) });
        check(Propose(plan, scene) is null,
            "retaining a particle author root cannot leave only its separately allocated baked sibling as false savings");

        (plan, scene) = Fixture();
        scene["objects"]![2]!.AsObject().Remove("particle");
        plan["loop"] = new JsonObject { ["unresolved"] = new JsonArray("owner_layer_id: 20",
            new JsonObject { ["owner_layer_id"] = 999 }, new JsonObject { ["owner_layer_id"] = 40 }) };
        check(Propose(plan, scene) is null,
            "diagnostic text, unknown owners and already-live particles do not create loop allocation roots");

        (plan, scene) = Fixture();
        scene["objects"]![2]!.AsObject().Remove("particle");
        plan["settings"]!["retain_live_root_ids"] = null;
        plan["loop"] = new JsonObject { ["analytic_evidence"] = new JsonObject {
            ["unresolved"] = new JsonArray(new JsonObject { ["source_owner_layer_id"] = 20 }) } };
        proposal = Propose(plan, scene)!;
        check(proposal is not null && Ids(proposal, "retain_live_root_ids").SequenceEqual([20]) &&
            Ids(proposal, "remaining_baked_layer_ids").SequenceEqual([11, 12, 50]),
            "direct observed analytic evidence works without prior retention or particle resource inspection");

        plan["video_groups"] = new JsonArray();
        check(Propose(plan, scene) is null, "an allocation with no baked layers has no loop fallback proposal");

        (plan, scene) = Fixture();
        scene["objects"]![1]!["parent"] = 11;
        bool cycleRejected = false;
        try { Propose(plan, scene); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
        { cycleRejected = true; }
        check(cycleRejected, "loop allocation rejects cyclic source ancestry instead of inventing a retained root");

        (plan, scene) = Fixture();
        plan["settings"]!["retain_live_root_ids"] = new JsonArray(11);
        bool childRetentionRejected = false;
        try { Propose(plan, scene); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
        { childRetentionRejected = true; }
        check(childRetentionRejected, "loop allocation does not pass an allocation child as a retained author root");

        // feat/particle-stationarity：粒子层只在没通过平稳随机判据时才当触发器，判据结论读 loop.unresolved[].particle_stationarity。
        static JsonObject StationarityItem(int owner, bool stationary) => new() {
            ["kind"] = "runtime_animation", ["owner_layer_id"] = owner, ["mechanism"] = "particle_system",
            ["particle_stationarity"] = new JsonObject { ["stationary"] = stationary, ["failed_conditions"] = new JsonArray() } };
        JsonObject particlePlan = JsonNode.Parse("""
            { "video_groups": [{ "layer_ids": [2, 3, 4, 5, 6] }], "loop": { "unresolved": [] } }
            """)!.AsObject();
        JsonObject particleScene = JsonNode.Parse("""
            { "objects": [
              { "id": 2, "particle": "stationary.json" },
              { "id": 3, "particle": "follows-cursor.json" },
              { "id": 4, "image": "unresolved.json" },
              { "id": 5, "particle": "old-plan-without-verdict.json" },
              { "id": 6, "image": "periodic.json" }
            ] }
            """)!.AsObject();
        particlePlan["loop"]!["unresolved"] = new JsonArray(StationarityItem(2, true), StationarityItem(3, false),
            new JsonObject { ["kind"] = "NonPeriodicOrDriftingMechanism", ["owner_layer_id"] = 4 });
        proposal = Propose(particlePlan, particleScene)!;
        check(proposal is not null && Ids(proposal, "trigger_layer_ids").SequenceEqual([3, 4, 5]) &&
            Ids(proposal, "remaining_baked_layer_ids").SequenceEqual([2, 6]),
            "loop fallback keeps stationary particles baked but still retains particles that fail the criteria, unresolved owners and particles without a verdict");

        particlePlan["loop"]!["unresolved"]!.AsArray().Add(new JsonObject { ["kind"] = "runtime_material", ["owner_layer_id"] = 2 });
        check(Ids(Propose(particlePlan, particleScene)!, "trigger_layer_ids").SequenceEqual([2, 3, 4, 5]),
            "a stationary particle that also owns a non-particle unresolved mechanism is still a loop fallback trigger");

        particlePlan["loop"]!["unresolved"]!.AsArray().Add(StationarityItem(2, false));
        particlePlan["loop"]!["unresolved"]!.AsArray().RemoveAt(3);
        particlePlan["loop"]!["unresolved"]!.AsArray().Add(StationarityItem(2, true));
        check(Ids(Propose(particlePlan, particleScene)!, "trigger_layer_ids").SequenceEqual([2, 3, 4, 5]),
            "one failed stationarity verdict among several items for the same particle layer keeps it a trigger");

        particlePlan["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(2, 6) });
        particlePlan["loop"] = new JsonObject { ["unresolved"] = new JsonArray(StationarityItem(2, true)) };
        var explain = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridLoopAllocation")!
            .GetMethod("Explain", BindingFlags.Static | BindingFlags.NonPublic)!;
        var explained = (JsonObject)explain.Invoke(null, [particlePlan, particleScene])!;
        check(Propose(particlePlan, particleScene) is null && explained["status"]!.GetValue<string>() == "not_applicable",
            "an allocation whose only particles pass the stationary-random criteria has no loop fallback, and says why");

        // feat/particle-crossfade：analyze 侧回退取证。重查出的计划整层不可用、但留下的未解析项全部可由残差掩盖时也算找到候选
        // （Far From Home 留实时 Sea 之后只剩平稳随机的雨与水滴）；有不可掩盖项、有 blocker、没有候选时仍是没找到。
        var resolution = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridLoopAllocation")!
            .GetMethod("ReplannedResolution", BindingFlags.Static | BindingFlags.NonPublic)!;
        (bool Resolved, string Basis, JsonObject? Classification) Resolve(JsonObject replanned) =>
            ((bool, string, JsonObject?))resolution.Invoke(null, [replanned, particleScene, (Func<string, JsonObject?>)(_ => null)])!;
        JsonObject Replanned(JsonArray unresolved, string wholeLayer = "unavailable", string route = "whole_layer", int candidates = 1, JsonArray? blockers = null) => new()
        {
            ["route"] = route, ["blockers"] = blockers ?? new JsonArray(), ["whole_layer"] = new JsonObject { ["status"] = wholeLayer },
            ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 }, ["canvas_width"] = 1920, ["canvas_height"] = 1080,
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 2, ["name"] = "rain" }, new JsonObject { ["id"] = 4, ["name"] = "sway" }),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(2, 4), ["include_scene_clear"] = false }),
            ["loop"] = new JsonObject
            {
                ["candidates"] = new JsonArray([.. Enumerable.Range(0, candidates).Select(_ => (JsonNode)new JsonObject { ["frames"] = 1370 })]),
                ["unresolved"] = unresolved
            }
        };
        JsonObject WarmStationary() { var item = StationarityItem(2, true); item["particle_stationarity"]!["warmup_seconds"] = 4.0; return item; }
        JsonObject Note() => new() { ["kind"] = ResidualMasking.AllocationFallbackKind, ["detail"] = "note" };
        var maskableReplan = Resolve(Replanned(new JsonArray(WarmStationary(), Note())));
        JsonObject admitted = Replanned(new JsonArray(WarmStationary(), Note()));
        string admissionInput = admitted.ToJsonString();
        check(Admission.Evaluate(admitted, particleScene, _ => null).Residual!["status"]!.GetValue<string>() == "residual_maskable" &&
            admitted.ToJsonString() == admissionInput, "shared bake admission ignores explanatory notes without changing the analyzed plan");
        check(Admission.Evaluate(Replanned(new JsonArray(StationarityItem(2, false))), particleScene, _ => null)
            .Residual!["status"]!.GetValue<string>() == "rejected", "shared analysis/bake admission rejects a nonstationary particle before rendering");
        var ordered = new JsonArray(new JsonObject { ["frames"] = 36000UL }, new JsonObject { ["frames"] = 18001UL }, new JsonObject { ["frames"] = 18024UL });
        LoopCandidateFallback.PrioritizeShortest(ordered);
        check(ordered.Select(c => c!["frames"]!.GetValue<ulong>()).SequenceEqual(new ulong[] { 18001, 18024, 36000 }),
            "already-admitted candidates are executed shortest first without modifying their values");
        check(maskableReplan.Resolved && maskableReplan.Basis == "residual_maskable" &&
            maskableReplan.Classification?["max_warmup_seconds"]?.GetValue<double>() == 4,
            "a smaller allocation whose remaining unresolved items are all stationary particles counts as candidate_found for the residual masking route");
        var swayReplan = Resolve(Replanned(new JsonArray(WarmStationary(), new JsonObject
        {
            ["kind"] = "NonPeriodicOrDriftingMechanism", ["owner_layer_id"] = 4, ["bounded_displacement"] = true,
            ["mechanism"] = ShaderPeriodAnalysis.FoliageSwayMechanism, ["detail"] = "sway"
        })));
        // 重查 plan 的拒因带编号（与真实 plan 一样），ReplannedResolution 按编号放过透视拒因。
        static JsonObject WithBlocker(JsonObject plan) { PlanBlockers.Set(plan, [new Blocker(BlockerCode.VideoShell)]); return plan; }
        check(!swayReplan.Resolved && swayReplan.Basis == "unavailable" &&
            !Resolve(WithBlocker(Replanned(new JsonArray(WarmStationary())))).Resolved &&
            !Resolve(Replanned(new JsonArray(WarmStationary()), candidates: 0)).Resolved &&
            !Resolve(Replanned(new JsonArray(Note()))).Resolved,
            "a smaller allocation stays unavailable when a displacement component remains, when blockers remain, without candidates, or with only informational notes");
        check(Resolve(Replanned(new JsonArray(), wholeLayer: "available")) is { Resolved: true, Basis: "whole_layer_available" } &&
            Resolve(Replanned(new JsonArray(), route: "effect_prefix")) is { Resolved: true, Basis: "effect_prefix" },
            "whole-layer availability and an effect-prefix route still count as found, as before");

        // fix/narrative-rc10：重查只被全幅布局冲突挡住、冲突给出了保留做法时，记下完整的 --retain-live 列表（已保留的根在前）。
        var suggestionMethod = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridLoopAllocation")!
            .GetMethod("ReplannedRetainLiveSuggestion", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonObject? Suggest(JsonObject replanned, int[] retained) => (JsonObject?)suggestionMethod.Invoke(null, [replanned, retained]);
        const string conflict = "Full-frame mode requires one opaque video group; options follow.";
        JsonObject Blocked(JsonArray blockers, string retentionStatus = "available", string layoutConflict = conflict, int[]? roots = null) => new()
        {
            ["blockers"] = blockers,
            ["whole_layer"] = new JsonObject { ["status"] = "unavailable", ["layout_conflict"] = layoutConflict },
            ["full_frame_retention"] = new JsonObject { ["status"] = retentionStatus,
                ["root_ids"] = new JsonArray([.. (roots ?? [28]).Select(id => (JsonNode)JsonValue.Create(id))]),
                ["retain_live_root_ids"] = new JsonArray([.. new[] { 20 }.Concat(roots ?? [28]).Distinct().Select(id => (JsonNode)JsonValue.Create(id))]) }
        };
        JsonObject? suggestion = Suggest(Blocked(new JsonArray(conflict)), [20]);
        check(suggestion is not null && suggestion["basis"]!.GetValue<string>() == "full_frame_retention" &&
            Ids(suggestion, "retain_live_root_ids").SequenceEqual([20, 28]) && Ids(suggestion, "added_live_root_ids").SequenceEqual([28]),
            "a re-analysis stopped only by a full-frame conflict with an available retention yields the cumulative --retain-live ids");
        check(Suggest(Blocked(new JsonArray(conflict, "another blocker")), [20]) is null &&
            Suggest(Blocked(new JsonArray(conflict), retentionStatus: "unavailable"), [20]) is null &&
            Suggest(Blocked(new JsonArray("a different blocker")), [20]) is null &&
            Suggest(Blocked(new JsonArray(conflict), roots: [20]), [20]) is null &&
            Suggest(Blocked(new JsonArray()), [20]) is null,
            "no retain-live suggestion when other blockers remain, the retention is unavailable, the blocker is not the layout conflict, nothing new is retained, or nothing blocks");
        JsonObject inexpressible = Blocked(new JsonArray(conflict));
        inexpressible["full_frame_retention"]!["retain_live_root_ids"] = null;
        JsonObject splitUnits = Blocked(new JsonArray(conflict), roots: [155, 379]);
        splitUnits["full_frame_retention"]!["retain_live_root_ids"] = new JsonArray(20, 382);
        check(Suggest(inexpressible, [20]) is null &&
            Suggest(splitUnits, [20]) is JsonObject mapped && Ids(mapped, "retain_live_root_ids").SequenceEqual([20, 382]) &&
            Ids(mapped, "added_live_root_ids").SequenceEqual([382]),
            "the summary suggestion uses the retention's source-root command ids and gives none when they cannot be expressed");

        // ---- 硬超时/硬杀留下的报告必须是"未完成"----
        var markInProgress = typeof(HybridBakeService).GetMethod("MarkInProgress", BindingFlags.Static | BindingFlags.NonPublic)!;
        var stale = new JsonObject
        {
            ["schema_version"] = 2, ["artifact_kind"] = "hybrid_video_candidate",
            ["status"] = "candidate_rejected_no_loop", ["loop_validation"] = "no_suitable_loop",
            ["reason"] = "No analytic loop candidate was found.", ["reason_localized"] = new JsonObject { ["zh"] = "没有候选。" },
            ["plan"] = new JsonObject()
        };
        markInProgress.Invoke(null, [stale, 2, "loop_candidate_fallback"]);
        check(stale["status"]!.GetValue<string>() == HybridBakeService.InProgressStatus &&
            HybridBakeService.InProgressStatus == "in_progress" &&
            stale["candidate_attempt"]!.GetValue<int>() == 2 &&
            stale["in_progress_stage"]!.GetValue<string>() == "loop_candidate_fallback" &&
            stale["loop_validation"]!.GetValue<string>() == "not_performed" &&
            stale["reason"] is null && stale["reason_localized"] is null &&
            !AppJsonPresentation.CandidateCanApply(stale),
            "a report marked in progress drops the previous verdict, records the candidate index and cannot be applied");
    }
}
