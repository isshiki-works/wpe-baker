using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 分析编排的判定链：退回、分配回退、布局与原因码只修信息源，不给坏判断叠整套重跑。
[Trait("Layer", "L1")]
public class AnalysisRerunChainTests
{
    /// <summary>四个组 10–13：10、11 只有普通贴图（0 道特效），12、13 各一道整屏特效。</summary>
    private static async Task<string> RuntimeAsync(string root)
    {
        string path = Path.Combine(root, "runtime.json");
        JsonObject Layer(int owner, int effects) => new() { ["owner"] = owner,
            ["materials"] = new JsonArray([.. Enumerable.Range(0, effects).Select(_ => (JsonNode)new JsonObject { ["role"] = "effect" })]) };
        await File.WriteAllTextAsync(path, new JsonObject { ["runtime_layers"] = new JsonArray(Layer(10, 0), Layer(11, 0), Layer(12, 1), Layer(13, 1)) }.ToJsonString());
        return path;
    }

    private static JsonObject Plan(HybridAnalyzeRequest r, int[] groups, double coverage, bool usable = true, string? runtime = null) => new()
    {
        ["summary"] = new JsonObject { ["key"] = usable ? "summary.bakeable" : "summary.blocked", ["zh"] = "", ["en"] = "" },
        ["settings"] = new JsonObject { ["preset"] = r.Preset, ["interaction"] = r.Interaction, ["video_layout"] = r.VideoLayout,
            ["retain_live_root_ids"] = new JsonArray([.. (r.RetainLiveRootIds ?? []).Select(id => (JsonNode)id)]) },
        ["route"] = "whole_layer",
        ["video_groups"] = new JsonArray([.. groups.Select(id => (JsonNode)new JsonObject { ["id"] = "g" + id,
            ["root_ids"] = new JsonArray(id), ["layer_ids"] = new JsonArray(id) })]),
        ["blockers"] = new JsonArray(),
        ["loop"] = new JsonObject { ["candidates"] = usable ? new JsonArray(new JsonObject { ["frames"] = 600 }) : new JsonArray() },
        ["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule,
            ["evidence"] = new JsonObject { ["effect_pass_coverage"] = coverage } },
        ["runtime_evidence"] = runtime
    };

    private static Task<JsonObject> RunAsync(string root, Func<HybridAnalyzeRequest, JsonObject> analyze, List<HybridAnalyzeRequest> calls) =>
        AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "run")), (r, _) =>
        {
            lock (calls) calls.Add(r);
            return Task.FromResult(analyze(r));
        }, CancellationToken.None);

    private static int[] Retained(JsonObject plan) => [.. plan["settings"]!["retain_live_root_ids"]!.AsArray().Select(n => n!.GetValue<int>()).Order()];

    [Fact]
    public async Task APrefixThatOnlyWonOnALayoutConflictIsComparedWithTheLayeredWholeLayer()
    {
        // full_frame 下整层零阻断、有候选，只被布局冲突挡住，特效前缀（层 12 的一道特效）接手就停；layered 下整层分组省下 3 道。
        await TestTemp.Run(async root =>
        {
            string runtime = await RuntimeAsync(root);
            var calls = new List<HybridAnalyzeRequest>();
            JsonObject result = await RunAsync(root, r =>
            {
                if (r.VideoLayout == "layered") return Plan(r, [12, 13], 3, runtime: runtime);
                JsonObject prefix = Plan(r, [12, 13], 3, runtime: runtime);
                prefix["route"] = "effect_prefix";
                prefix.Remove("bake_value");
                prefix.Remove("loop");
                prefix["effect_prefix_caches"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 12,
                    ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) } });
                prefix["whole_layer"] = new JsonObject { ["layout_conflict"] = "full-frame conflict", ["blockers"] = new JsonArray(),
                    ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) } };
                return prefix;
            }, calls);
            Assert.Equal("whole_layer", result["route"]!.GetValue<string>());
            Assert.Equal("layered", result["settings"]!["video_layout"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task ReplanningAddsOwnersTheReplanNamesUntilItSettles()
    {
        // 第一轮留实时触发器所在单元（12、20、没判据的粒子 11）；重查又点名了 50（source_static），并进留实时再查一次就找到。
        // 原因码按各轮的未解析项写，没有可写的（粒子 11）写 loop_allocation_trigger，不借用用户要求的 retained_by_cost_trial。
        JsonObject scene = JsonNode.Parse("""
            { "objects": [ { "id": 30 }, { "id": 10 }, { "id": 11, "parent": 10, "particle": "p.json" }, { "id": 12, "parent": 10 },
              { "id": 20 }, { "id": 50 }, { "id": 60 } ] }
            """)!.AsObject();
        JsonArray layers = JsonNode.Parse("""
            [ { "id": 11, "root": 10, "allocation_root": 11 }, { "id": 12, "root": 10, "allocation_root": 12 },
              { "id": 20, "root": 20, "allocation_root": 20 }, { "id": 50, "root": 50, "allocation_root": 50 },
              { "id": 60, "root": 60, "allocation_root": 60 } ]
            """)!.AsArray();
        JsonObject report = new() { ["settings"] = new JsonObject { ["retain_live_root_ids"] = new JsonArray(30) },
            ["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(11, 12, 20, 50, 60) }),
            ["layers"] = layers.DeepClone(),
            ["loop"] = new JsonObject { ["unresolved"] = new JsonArray(
                new JsonObject { ["owner_layer_id"] = 12, ["mechanism"] = "term_not_retimable" },
                new JsonObject { ["owner_layer_id"] = 20, ["kind"] = "source_static" }) } };
        JsonObject evidence = HybridLoopAllocation.Explain(report, scene);
        report["loop_allocation_fallback"] = evidence;
        var replans = new List<(int[] Ids, Dictionary<int, string[]> Reasons)>();
        var (_, resolved, basis, _) = await HybridLoopAllocation.ReplanUntilSettledAsync(report, scene, evidence, (ids, reasons) =>
        {
            replans.Add((ids, reasons));
            return Task.FromResult(new JsonObject {
                ["settings"] = new JsonObject { ["retain_live_root_ids"] = new JsonArray([.. ids.Select(id => (JsonNode)id)]),
                    ["retain_live_reasons"] = System.Text.Json.JsonSerializer.SerializeToNode(reasons) },
                ["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray([.. new[] { 50, 60 }.Except(ids).Select(id => (JsonNode)id)]) }),
                ["layers"] = layers.DeepClone(),
                ["loop"] = new JsonObject { ["unresolved"] = ids.Contains(50) ? new JsonArray()
                    : new JsonArray(new JsonObject { ["owner_layer_id"] = 50, ["kind"] = "source_static" }) } });
        }, plan => (plan["loop"]!["unresolved"]!.AsArray().Count == 0, plan["loop"]!["unresolved"]!.AsArray().Count == 0 ? "whole_layer_available" : "unavailable", null),
        () => "attempt-1/plan.json");
        Assert.Equal(2, replans.Count);
        Assert.True(resolved);
        Assert.Equal("whole_layer_available", basis);
        Assert.Equal([11, 12, 20, 30, 50], replans[1].Ids.Order());
        Assert.Equal(["term_not_retimable"], replans[0].Reasons[12]);
        Assert.Equal(["source_static"], replans[0].Reasons[20]);
        Assert.Equal([HybridLoopAllocation.TriggerReason], replans[0].Reasons[11]);
        Assert.Equal(["source_static"], replans[1].Reasons[50]);
        Assert.Equal(["term_not_retimable"], replans[1].Reasons[12]);
        Assert.Equal([11, 12, 20, 30, 50], evidence["retain_live_root_ids"]!.AsArray().Select(n => n!.GetValue<int>()).Order());
        Assert.Contains(50, evidence["added_live_root_ids"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal("attempt-1/plan.json", evidence["attempts"]![0]!["plan_path"]!.GetValue<string>());
    }

    [Fact]
    public void SlowComponentsThatDoNotCloseAreRetainedWithTheirOwnReason()
    {
        // slow-live 重查原先只传 plan 已带的原因，没闭合的慢分量层只剩 retained_by_cost_trial（被当成用户 --retain-live）。
        JsonObject result = new() { ["settings"] = new JsonObject { ["retain_live_root_ids"] = new JsonArray(8),
            ["retain_live_reasons"] = new JsonObject { ["5"] = new JsonArray("source_static") } } };
        JsonArray round = new(
            new JsonObject { ["owner_layer_ids"] = new JsonArray(7, 8), ["loop_closure"] = new JsonObject { ["status"] = LoopClosureCheck.NotClosedStatus } },
            new JsonObject { ["owner_layer_ids"] = new JsonArray(9), ["loop_closure"] = new JsonObject { ["status"] = "closed" } });
        HybridAnalyzeRequest next = AnalysisOrchestrator.ProbeReplan(result, round, new HybridAnalyzeRequest(2, "s", "a", "o"))!;
        Assert.Equal([8, 7], next.RetainLiveRootIds!);
        var reasons = next.RetainLiveReasons!;
        Assert.Equal([AnalysisOrchestrator.SlowClosureNotClosed], reasons[7]);
        Assert.Equal(["source_static"], reasons[5]);
        Assert.Equal([7], round[0]!["retained_live_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Null(round[1]!["retained_live_layer_ids"]);
    }

    [Fact]
    public async Task AutomaticRetentionWritesTheCallersReasonInsteadOfTheUserRequestCode()
    {
        // 调用方给了原因码（自动留实时）就写它；没给的（用户 --retain-live、退回轮）才写 retained_by_cost_trial。
        await TestTemp.Run(async root =>
        {
            string source = await AnalysisMemoTests.FixtureAsync(root);
            var planner = new HybridScenePlanner(new("not-started", "not-started", "not-started", []));
            JsonObject automatic = await planner.AnalyzeSingleAsync(AnalysisMemoTests.Request(root, source) with {
                OutputDirectory = Path.Combine(root, "automatic"), ViewMode = "fixed_view", RetainLiveRootIds = [4],
                RetainLiveReasons = new() { [4] = [AnalysisOrchestrator.SlowClosureNotClosed] } });
            JsonObject requested = await planner.AnalyzeSingleAsync(AnalysisMemoTests.Request(root, source) with {
                OutputDirectory = Path.Combine(root, "requested"), ViewMode = "fixed_view", RetainLiveRootIds = [4] });
            string[] Reasons(JsonObject plan) => [.. AnalysisMemoTests.Layer(plan, 4)["reasons"]!.AsArray().Select(n => n!.GetValue<string>())];
            Assert.Equal([AnalysisOrchestrator.SlowClosureNotClosed], Reasons(automatic));
            Assert.Equal(["retained_by_cost_trial"], Reasons(requested));
        });
    }
}
