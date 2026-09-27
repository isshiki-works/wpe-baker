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
    [InlineData("requested_daytime", false)]
    [InlineData("static_with_live", false)]
    [InlineData("coverage_only", true)]
    public async Task RetreatRunsUnlessEveryCandidateStillHitsTheSameCondition(string kind, bool retreats)
    {
        // 请求本身固定了时段：每个候选都固定在它上面，fixed_daytime 改不了。静态成品带实时层：多留实时剩下的仍是静态的。只差覆盖时照旧退。
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "run"), DaytimeState: kind == "requested_daytime" ? "day" : null), (r, _) =>
            {
                lock (calls) calls.Add(r);
                JsonObject plan = Plan(r, [.. new[] { 10, 11, 12, 13 }.Except(r.RetainLiveRootIds ?? [])], 2);
                plan["settings"]!["daytime_state"] = r.DaytimeState;
                if (kind == "static_with_live")
                {
                    plan["loop"]!["candidates"]![0]!["frames"] = 1;
                    plan["live_layer_ids"] = new JsonArray(5);
                    Block(plan);
                }
                return Task.FromResult(plan);
            }, CancellationToken.None);
            Assert.Equal(retreats, calls.Any(r => r.RetainLiveRootIds is { Length: > 0 }));
        });
    }

    [Fact]
    public async Task FarTooManyGroupsAreStillRescuedWhenOneRetainedGroupLetsThemMerge()
    {
        // 20 组远超上限 9，但留实时组 119 之后全幅布局变得可行、并成一组：逐组退回一轮就救回。
        // "组数超过上限两倍就不退"把这类作品从能掉成未收敛（全集 3463280673 在 main 上就是逐组退回救回的）。
        await TestTemp.Run(async root =>
        {
            JsonObject chosen = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "run")), (r, _) =>
                Task.FromResult((r.RetainLiveRootIds ?? []).Contains(119)
                    ? Plan(r, [1000], 100)
                    : Plan(r, [.. Enumerable.Range(100, 20).Except(r.RetainLiveRootIds ?? [])], 100)), CancellationToken.None);
            Assert.True(Admission.Accepted(chosen));
            Assert.Equal([119], chosen["settings"]!["retain_live_root_ids"]!.AsArray().Select(n => n!.GetValue<int>()));
        });
    }

    [Fact]
    public async Task APlanBlockedByOneGroupIsStillRescuedByRetainingThatGroup()
    {
        // 原方案被组 13 带来的 blocker 挡住（残差不可掩盖一类），把组 13 留实时就能生成且省电：逐组退回照旧试、照旧救回。
        // "组数之外的 blocker 一律不退"会把这类作品从能掉成未收敛（全集 3463280673）。
        await TestTemp.Run(async root =>
        {
            JsonObject? chosen = null;
            var calls = new List<HybridAnalyzeRequest>();
            chosen = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "run")), (r, _) =>
            {
                lock (calls) calls.Add(r);
                int[] kept = r.RetainLiveRootIds ?? [];
                JsonObject plan = Plan(r, [.. new[] { 10, 11, 12, 13 }.Except(kept)], 4);
                if (!kept.Contains(13)) Block(plan);
                return Task.FromResult(plan);
            }, CancellationToken.None);
            Assert.True(Admission.Accepted(chosen));
            Assert.Equal([13], chosen["settings"]!["retain_live_root_ids"]!.AsArray().Select(n => n!.GetValue<int>()));
        });
    }
}
