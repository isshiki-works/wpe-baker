using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 分析编排里救不回来的整套重跑不再跑：单次轨判实时、只差不省电时的静止证明留实时、多留实时改不了的退回。
[Trait("Layer", "L1")]
public class HopelessRerunTests
{
    private static JsonObject Plan(HybridAnalyzeRequest r, int[] groups, double coverage, bool usable = true, string? runtime = null) => new()
    {
        ["summary"] = new JsonObject { ["key"] = usable ? "summary.bakeable" : "summary.blocked", ["zh"] = "", ["en"] = "" },
        ["settings"] = new JsonObject { ["preset"] = r.Preset, ["interaction"] = r.Interaction,
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

    private static async Task<List<HybridAnalyzeRequest>> RunAsync(string root, Func<HybridAnalyzeRequest, JsonObject> analyze)
    {
        var calls = new List<HybridAnalyzeRequest>();
        await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "run")), (r, _) =>
        {
            lock (calls) calls.Add(r);
            return Task.FromResult(analyze(r));
        }, CancellationToken.None);
        return calls;
    }

    private static void Block(JsonObject plan)
    {
        PlanBlockers.Add(plan, new Blocker(BlockerCode.BakeAllocation, ["residual not maskable"]));
        plan["status"] = "requires_resolution";
    }

    [Fact]
    public async Task AnUnusableResultWithAnIntroTrackIsNotReanalyzedWithTheTrackLive()
    {
        // 加载即播的单次轨进了视频组、结果生成不了：原来按 SingleShotLive 整套再分析一次（全集 0 次采纳），现在不跑。
        await TestTemp.Run(async root =>
        {
            string runtime = Path.Combine(root, "runtime.json");
            await File.WriteAllTextAsync(runtime, new JsonObject { ["runtime_animation_periods"] = new JsonArray(new JsonObject {
                ["source_owner_layer_id"] = 1, ["looping"] = false, ["playback_mode"] = "single", ["confidence"] = "high",
                ["duration_seconds"] = 2.0 }) }.ToJsonString());
            var calls = await RunAsync(root, r => Plan(r, [1], 0, usable: false, runtime: runtime));
            Assert.DoesNotContain(calls, r => r.SingleShotLive);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceStaticLayersAreRetainedOnlyWhenSomethingBesidesNoBenefitBlocks(bool blocked)
    {
        // 只差预计不省电（能生成）：把静止证明点名的层留实时整套重跑只会烘得更少，不跑。还有别的 blocker 时照旧试。
        await TestTemp.Run(async root =>
        {
            var calls = await RunAsync(root, r =>
            {
                JsonObject plan = Plan(r, [1], 0.5);
                plan["loop"]!["unresolved"] = new JsonArray(new JsonObject { ["kind"] = "source_static", ["owner_layer_id"] = 209 });
                if (blocked) Block(plan);
                return plan;
            });
            Assert.Equal(blocked, calls.Any(r => (r.RetainLiveRootIds ?? []).Contains(209)));
        });
    }

    [Theory]
    [InlineData("fixed_daytime", false)]
    [InlineData("non_count_blocker", false)]
    [InlineData("far_over_limit", false)]
    [InlineData("coverage_only", true)]
    public async Task RetreatRunsOnlyWhenRetainingMoreCanHelp(string kind, bool retreats)
    {
        // 固定时段（2955378002：一张 1414 s）、组数之外的 blocker、组数超过上限两倍：多留实时改不了，不退。只差覆盖时照旧退。
        await TestTemp.Run(async root =>
        {
            var calls = await RunAsync(root, r =>
            {
                int[] kept = r.RetainLiveRootIds ?? [];
                JsonObject plan = kind == "far_over_limit"
                    ? Plan(r, [.. Enumerable.Range(100, 20).Except(kept)], 100)
                    : Plan(r, [.. new[] { 10, 11, 12, 13 }.Except(kept)], 2);
                if (kind == "fixed_daytime") plan["settings"]!["daytime_state"] = "day";
                if (kind == "non_count_blocker") Block(plan);
                return plan;
            });
            Assert.Equal(retreats, calls.Any(r => r.RetainLiveRootIds is { Length: > 0 }));
        });
    }
}
