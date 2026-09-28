using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C2.2e：分析编排的显式搜索空间、调用预算（默认 = 现状最坏上限，超出抛内部错误）、尝试顺序与逐状态导出。
[Trait("Layer", "L0")]
public class SearchSpaceTests
{
    [Fact]
    public void DefaultsSearchFixedThenOffFromBalancedAcrossBothLayouts()
    {
        SearchSpace space = SearchSpace.Of(new(2, "s", "a", "o"), exportStates: true);
        Assert.Equal(["fixed", "off"], space.Interactions);
        Assert.Equal(["balanced", "efficiency"], space.Presets);
        Assert.Equal(["full_frame", "layered"], space.Layouts);
        Assert.True(space.ExpandStates);
        Assert.False(space.ExportStates);
        Assert.True(space.Expands("fixed"));
        Assert.False(space.Expands("keep"));
    }

    [Fact]
    public void ExplicitChoicesNarrowTheSpace()
    {
        SearchSpace space = SearchSpace.Of(new(2, "s", "a", "o", VideoLayout: "layered", Preset: "quality", RetimeBudgetPercent: 1,
            DaytimeSplit: true, DaytimeState: "day", Interaction: "keep", LayoutExplicit: true), exportStates: true);
        Assert.Equal(["keep", "fixed", "off"], space.Interactions);
        Assert.Equal(["quality"], space.Presets);
        Assert.Equal(["layered"], space.Layouts);
        Assert.False(space.ExpandStates);
        Assert.False(space.Expands("fixed"));
        Assert.True(space.ExportStates);
        Assert.Equal(["off"], SearchSpace.Of(new(2, "s", "a", "o", Interaction: "off"), false).Interactions);
        Assert.Equal(["quality", "balanced", "efficiency"], SearchSpace.Of(new(2, "s", "a", "o", Preset: "quality"), false).Presets);
    }

    [Fact]
    public void RejectsUnknownPresetOrInteraction()
    {
        Assert.Throws<InvalidDataException>(() => SearchSpace.Of(new(2, "s", "a", "o", Preset: "custom"), false));
        Assert.Throws<InvalidDataException>(() => SearchSpace.Of(new(2, "s", "a", "o", Interaction: "live"), false));
    }

    [Fact]
    public void DefaultBudgetIsTheWorstCaseOfEachDimension()
    {
        // fixed/off × balanced/efficiency × 两种布局 = 8，off 的取舍测量再 2；fixed、off 都按状态展开，每个状态 8。
        CallBudget standard = SearchSpace.Of(new(2, "s", "a", "o"), false).Budget();
        Assert.Equal((10, 8), (standard.Limit(0), standard.Limit(1) - standard.Limit(0)));
        // keep/fixed/off × 三档 × 两种布局 = 18 + 2；keep 不展开，fixed、off 每个状态 12，逐状态导出每个状态再 1。
        CallBudget widest = SearchSpace.Of(new(2, "s", "a", "o", Preset: "quality", Interaction: "keep", DaytimeSplit: true), true).Budget();
        Assert.Equal((20, 13), (widest.Limit(0), widest.Limit(1) - widest.Limit(0)));
        Assert.Equal(72, widest.Limit(4));
    }

    // 速度实测之后：看得出才按逐项预算重分析（挡住循环的记不能）；量不到另走 SpeedUnmeasured（挡住循环的记未收敛）；各只一次
    [Fact]
    public void OnlyAVisibleSpeedReadingFallsBackToTheBudget()
    {
        var request = new HybridAnalyzeRequest(2, "s", "a", "o");
        Assert.Equal(request with { BudgetOnlyRetime = true }, AnalysisOrchestrator.AfterSpeedProbe(request, "visible"));
        Assert.Equal(request with { SpeedUnmeasured = true }, AnalysisOrchestrator.AfterSpeedProbe(request, "not_measured"));
        Assert.Null(AnalysisOrchestrator.AfterSpeedProbe(request, "passed"));
        Assert.Null(AnalysisOrchestrator.AfterSpeedProbe(request with { SpeedUnmeasured = true }, "not_measured"));
        Assert.Null(AnalysisOrchestrator.AfterSpeedProbe(request with { BudgetOnlyRetime = true }, "visible"));
    }

    [Fact]
    public void BudgetRejectsARepeatedCellAndAnOverrun()
    {
        var budget = new CallBudget(1, 1);
        budget.Charge("search|fixed|balanced|full_frame|-", 0);
        Assert.Throws<InvalidOperationException>(() => budget.Charge("search|fixed|balanced|full_frame|-", 5));
        Assert.Throws<InvalidOperationException>(() => budget.Charge("search|fixed|balanced|layered|-", 0));
        var grown = new CallBudget(1, 1);
        grown.Charge("a", 0);
        grown.Charge("b", 1);
        Assert.Throws<InvalidOperationException>(() => grown.Charge("c", 1));
    }
}

[Trait("Layer", "L1")]
public class AnalysisOrchestratorTests
{
    private static readonly string[] Day = ["night", "morning", "day", "dusk"];

    [Fact]
    public async Task EveryCellIsTriedInOrderWhenNothingIsUsable()
    {
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            var request = new HybridAnalyzeRequest(2, "s", "a", Path.Combine(root, "order"));
            JsonObject result = await AnalysisOrchestrator.RunAsync(request, (r, _) => { calls.Add(r); return Task.FromResult(Plan(r, false)); },
                CancellationToken.None);
            Assert.Equal([
                "fixed balanced full_frame", "fixed balanced layered", "fixed efficiency full_frame", "fixed efficiency layered",
                "off balanced full_frame", "off balanced layered", // 取舍测量
                "off balanced full_frame", "off balanced layered", "off efficiency full_frame", "off efficiency layered"],
                calls.Select(r => $"{r.Interaction} {r.Preset} {r.VideoLayout}"));
            Assert.Equal(Enumerable.Range(1, 10).Select(n => n.ToString()), calls.Select(r => Path.GetFileName(r.OutputDirectory)));
            Assert.Equal("none", result["preset_applied"]!.GetValue<string>());
            Assert.Equal("balanced: summary.blocked; efficiency: summary.blocked", result["preset_fallback_reason"]!.GetValue<string>());
            // analysis_timing 按重查路径记次数：档位与布局回退 7 次（fixed 3 次；off 的取舍测量 1 次 + 各档 3 次），交互替代与成本试算各 1 次。
            JsonObject paths = result["analysis_timing"]!["paths"]!.AsObject();
            Assert.Equal(7, paths["a_preset_layout_fallback"]!["count"]!.GetValue<int>());
            Assert.Equal(1, paths["b_interaction_alternative"]!["count"]!.GetValue<int>());
            Assert.Equal(1, paths["i_interaction_cost_trial"]!["count"]!.GetValue<int>());
            Assert.Null(paths["c_retreat"]);
            Assert.Equal(0, result["analysis_timing"]!["sub_analyses"]!.GetValue<int>());
            Assert.NotNull(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "order", "plan.json")))!["analysis_timing"]);
        });
    }

    [Fact]
    public async Task WorstCaseUsesExactlyTheDefaultBudget()
    {
        await TestTemp.Run(async root =>
        {
            var request = new HybridAnalyzeRequest(2, "s", "a", Path.Combine(root, "worst"), Preset: "quality", Interaction: "keep", DaytimeSplit: true,
                AllowNoBenefit: true);
            CallBudget budget = SearchSpace.Of(request, true).Budget();
            int calls = 0;
            await AnalysisOrchestrator.RunAsync(request, (r, _) => { calls++; return Task.FromResult(Plan(r, false, Day)); }, CancellationToken.None,
                export: new(name => Path.Combine(root, "worst-" + name + ".json")), budget: budget);
            Assert.Equal(budget.Limit(Day.Length), calls);
        });
    }

    [Fact]
    public async Task ExceedingTheBudgetIsAnInternalErrorBeforeTheCall()
    {
        await TestTemp.Run(async root =>
        {
            var request = new HybridAnalyzeRequest(2, "s", "a", Path.Combine(root, "short"), Preset: "quality", Interaction: "keep", DaytimeSplit: true,
                AllowNoBenefit: true);
            CallBudget full = SearchSpace.Of(request, true).Budget();
            int calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => AnalysisOrchestrator.RunAsync(request,
                (r, _) => { calls++; return Task.FromResult(Plan(r, false, Day)); }, CancellationToken.None,
                export: new(name => Path.Combine(root, "short-" + name + ".json")), budget: new CallBudget(full.Limit(0) - 1, full.Limit(1) - full.Limit(0))));
            Assert.Equal(full.Limit(Day.Length) - 1, calls);
        });
    }

    [Fact]
    public async Task StatesAreSearchedOnlyOutsideKeepAndBestStateWins()
    {
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "states"), Interaction: "keep", AllowNoBenefit: true),
                (r, _) => { calls.Add(r); return Task.FromResult(Plan(r, r.DaytimeState is "day" or "dusk", Day, r.DaytimeState == "dusk" ? 1 : 2)); },
                CancellationToken.None);
            Assert.DoesNotContain(calls, r => r.Interaction == "keep" && r.DaytimeState is not null);
            Assert.Contains(calls, r => r.Interaction == "fixed" && r.DaytimeState == "night");
            JsonObject suggested = JsonNode.Parse(await File.ReadAllTextAsync(result["suggested_change"]!["plan_path"]!.GetValue<string>()))!.AsObject();
            Assert.Equal("dusk", suggested["settings"]!["daytime_state"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task ExportAnalyzesEachStateWithTheOriginalRequestAfterWritingThePlan()
    {
        await TestTemp.Run(async root =>
        {
            var exported = new List<HybridAnalyzeRequest>();
            var reported = new List<string>();
            string output = Path.Combine(root, "export");
            var request = new HybridAnalyzeRequest(2, "s", "a", output, DaytimeSplit: true, Interaction: "fixed");
            JsonObject result = await AnalysisOrchestrator.RunAsync(request, (r, _) =>
            {
                if (r.OutputDirectory.StartsWith(Path.Combine(output, "state-"), StringComparison.Ordinal)) exported.Add(r);
                return Task.FromResult(Plan(r, r.DaytimeState is null, Day, r.DaytimeState == "day" ? 3 : 1));
            }, CancellationToken.None, export: new(name => Path.Combine(root, "plan.json.state-" + name + ".json"), (name, _) => reported.Add(name)));
            Assert.Equal(Day, exported.Select(r => r.DaytimeState));
            Assert.All(exported, r => Assert.True(r.ViewMode == "preserve" && r.Preset is null && r.Interaction == "fixed" && r.AnalysisCacheDirectory is null));
            Assert.Equal(Day.Select(name => Path.Combine(output, "state-" + name)), exported.Select(r => r.OutputDirectory));
            Assert.Equal(Day, reported);
            JsonObject day = result["daytime_split"]!["states"]![2]!.AsObject();
            Assert.Equal(Path.Combine(root, "plan.json.state-day.json"), day["plan"]!.GetValue<string>());
            Assert.Equal((3, 0, "summary.blocked"), (day["video_group_count"]!.GetValue<int>(), day["blocker_count"]!.GetValue<int>(),
                day["summary_key"]!.GetValue<string>()));
            Assert.Equal("day", JsonNode.Parse(await File.ReadAllTextAsync(day["plan"]!.GetValue<string>()))!["settings"]!["daytime_state"]!.GetValue<string>());
            // 落盘的母 plan 在导出之前写好，不带子 plan 记录。
            JsonObject written = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "plan.json")))!.AsObject();
            Assert.Null(written["daytime_split"]!["states"]![2]!["plan"]);
        });
    }

    [Fact]
    public async Task StateExportRewritesItsGeneratedPlanButProtectsOtherExistingFiles()
    {
        await TestTemp.Run(async root =>
        {
            async Task<JsonObject> Analyze(HybridAnalyzeRequest request, CancellationToken _)
            {
                if (request.DaytimeState is not null)
                {
                    Directory.CreateDirectory(request.OutputDirectory);
                    await File.WriteAllTextAsync(Path.Combine(request.OutputDirectory, "plan.json"), "generated by child analysis");
                }
                return Plan(request, true, ["day"]);
            }

            string output = Path.Combine(root, "internal");
            JsonObject result = await AnalysisOrchestrator.RunAsync(
                new(2, "s", "a", output, DaytimeSplit: true, Interaction: "fixed"), Analyze, CancellationToken.None,
                export: new(name => Path.Combine(output, "state-" + name, ".", "plan.json")));
            string generated = Path.Combine(output, "state-day", "plan.json");
            Assert.Equal(generated, Path.GetFullPath(result["daytime_split"]!["states"]![0]!["plan"]!.GetValue<string>()));
            Assert.Equal("day", JsonNode.Parse(await File.ReadAllTextAsync(generated))!["settings"]!["daytime_state"]!.GetValue<string>());

            string external = Path.Combine(root, "existing.json");
            await File.WriteAllTextAsync(external, "keep this file");
            await Assert.ThrowsAsync<IOException>(() => AnalysisOrchestrator.RunAsync(
                new(2, "s", "a", Path.Combine(root, "external"), DaytimeSplit: true, Interaction: "fixed"), Analyze,
                CancellationToken.None, export: new(_ => external)));
            Assert.Equal("keep this file", await File.ReadAllTextAsync(external));
        });
    }

    [Fact]
    public async Task NoExportWithoutACaller()
    {
        await TestTemp.Run(async root =>
        {
            int calls = 0;
            await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "gui"), DaytimeSplit: true),
                (r, _) => { calls++; return Task.FromResult(Plan(r, true, Day)); }, CancellationToken.None);
            // 第一格就能生成：只有母 plan 1 次。单时段方案必被固定时段拒因拒绝，不带 --no-benefit allow 时不展开；也没有逐状态导出。
            Assert.Equal(1, calls);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleStatePlansAreSearchedOnlyWhenNoBenefitIsAllowed(bool allowed)
    {
        // 2955378002：11 个时段 × 2 个布局 × 2 档，48 次子分析里 44 次是单时段方案，它们一律命中 fixed_daytime、默认必被拒。
        // 不带 --no-benefit allow 时不展开；能生成的母 plan 也不再被组数更少的单时段方案顶替。
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            string[] states = [.. Enumerable.Range(0, 11).Select(i => "state" + i)];
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "daytime"), AllowNoBenefit: allowed),
                (r, _) => { calls.Add(r); return Task.FromResult(Plan(r, true, states, r.DaytimeState is null ? 2 : 1)); },
                CancellationToken.None);
            Assert.Equal(allowed, calls.Any(r => r.DaytimeState is not null));
            Assert.Equal(allowed ? 12 : 1, calls.Count);
            Assert.Equal(allowed ? "state0" : null, result["settings"]!["daytime_state"]?.GetValue<string>());
            Assert.True(Admission.Accepted(result));
        });
    }

    [Fact]
    public async Task InteractionOffKeepsSubtreesThatHoldProtectedContent()
    {
        var plan = new JsonObject { ["layers"] = new JsonArray(
            new JsonObject { ["id"] = 1, ["kind"] = "image", ["tradeoff_kinds"] = new JsonArray("pointer") },
            new JsonObject { ["id"] = 2, ["kind"] = "text", ["parent"] = 1 },
            new JsonObject { ["id"] = 3, ["kind"] = "image", ["tradeoff_kinds"] = new JsonArray("pointer"), ["parent"] = 4 },
            new JsonObject { ["id"] = 4, ["kind"] = "image" },
            new JsonObject { ["id"] = 5, ["kind"] = "text" },
            new JsonObject { ["id"] = 6, ["kind"] = "image", ["tradeoff_kinds"] = new JsonArray("pointer"), ["parent"] = 5 }) };
        var (excluded, costs) = await InteractionPolicy.ExclusionsAsync(plan, new(2, "s", "a", "o"), null, "unused", CancellationToken.None);
        Assert.Equal([3, 6], excluded);
        Assert.Equal(2, costs.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LowCoverageDoesNotForceInteractionOff(bool pointer)
    {
        // 普通图层/低 pass 覆盖没有足够的功耗证据，不因指针元素存在就自动建议关闭交互。
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            JsonObject Analyze(HybridAnalyzeRequest r)
            {
                calls.Add(r);
                JsonObject plan = Plan(r, true);
                plan["bake_value"] = r.Interaction == "off"
                    ? new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule, ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 2.0 } }
                    : new JsonObject { ["rule"] = WorkloadValue.NeedsWorkComparison.Rule,
                        ["evidence"] = new JsonObject { ["plain_group_ids"] = new JsonArray(0) } };
                plan["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["kind"] = "image" },
                    new JsonObject { ["id"] = 2, ["kind"] = "image", ["tradeoff_kinds"] = pointer ? new JsonArray("pointer") : new JsonArray() });
                return plan;
            }
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "nobenefit")),
                (r, _) => Task.FromResult(Analyze(r)), CancellationToken.None);
            Assert.Null(result[NoBenefit.Field]);
            Assert.Equal("fixed", result["settings"]!["interaction"]!.GetValue<string>());
            Assert.Empty(result["applied_tradeoffs"]!["properties"]!.AsObject());
            Assert.Empty(result["applied_tradeoffs"]!["turn_off_kinds"]!.AsArray());
            Assert.DoesNotContain(calls, r => r.Interaction == "off");
            Assert.Null(result["suggested_change"]);
        });
    }

    [Fact]
    public async Task LowCoverageDoesNotSpawnCostRetreats()
    {
        // pass/视频组阈值只作风险线索，不再因它为每个视频组重跑完整分析。
        await TestTemp.Run(async root =>
        {
            var outputs = new List<string>();
            JsonObject Analyze(HybridAnalyzeRequest r)
            {
                // 与 AnalyzeSingleAsync 同一条约束：输出目录必须是新的。
                if (Directory.Exists(r.OutputDirectory)) throw new IOException("Analysis output must be new.");
                Directory.CreateDirectory(r.OutputDirectory);
                outputs.Add(r.OutputDirectory);
                JsonObject plan = Plan(r, true, groups: 2);
                plan["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule, ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 0.5 } };
                plan["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["kind"] = "image", ["tradeoff_kinds"] = new JsonArray("pointer") });
                return plan;
            }
            var reports = new List<RenderProgress>();
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "twice")), (r, _) => Task.FromResult(Analyze(r)),
                CancellationToken.None, progress: new Collect(reports));
            Assert.Null(result[NoBenefit.Field]);
            Assert.Equal("unknown", result["bake_value"]!["status"]!.GetValue<string>());
            string[] retreating = [.. reports.Where(p => p.Stage == "retreating").Select(p => p.Text!.In(MessageCatalog.Chinese))];
            Assert.Empty(retreating);
            Assert.Equal(outputs.Count, outputs.Distinct().Count());
        });
    }

    private sealed class Collect(List<RenderProgress> reports) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => reports.Add(value);
    }

    [Fact]
    public async Task AnUnusableResultIsNotReanalyzedWithMoreLiveLayers()
    {
        // 生成不了、静止证明点名了还没留实时的层：编排层不再把它们加进留实时整套重跑（全集 252 次整次分析 0 次采纳），
        // 重查新点名的所有者由分配回退在同一次分析里并进留实时（HybridLoopAllocation.ReplanUntilSettledAsync）。
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            JsonObject Analyze(HybridAnalyzeRequest r)
            {
                calls.Add(r);
                JsonObject plan = Plan(r, false);
                plan["loop"]!["unresolved"] = new JsonArray(new JsonObject { ["kind"] = "source_static", ["owner_layer_id"] = 209 });
                return plan;
            }
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "static-live")),
                (r, _) => Task.FromResult(Analyze(r)), CancellationToken.None);
            Assert.False(Admission.Accepted(result));
            Assert.All(calls, r => Assert.Empty(r.RetainLiveRootIds ?? []));
            Assert.All(calls, r => Assert.False(r.SingleShotLive));
        });
    }

    // "留非循环层实时再查"递归的子分析与父分析共用 cache/<sha>：原先把已拼过源哈希的目录再传下去，子分析落到 cache/<sha>/<sha>，
    // 父分析的观测、循环与探针缓存全部用不上。子分析第一步就按缓存目录写 scene 缓存，看它写到哪里即可（之后的观测没有渲染器，失败无妨）。
    [Fact]
    public async Task LoopAllocationReplanSharesTheParentAnalysisCache() => await TestTemp.Run(async root =>
    {
        string fixture = Path.Combine(LocalTools.RepositoryRoot, "tests", "fixtures", "native", "shader-clock");
        string sha;
        using (var source = new ProjectSource(fixture)) sha = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        string cache = Path.Combine(root, "cache", sha);
        string output = Path.Combine(root, "analysis");
        Directory.CreateDirectory(output);
        var report = new JsonObject
        {
            ["route"] = "whole_layer", ["whole_layer"] = new JsonObject { ["status"] = "unavailable" }, ["blockers"] = new JsonArray(),
            ["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(1, 2) }),
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["root"] = 1, ["allocation_root"] = 1 },
                new JsonObject { ["id"] = 2, ["root"] = 2, ["allocation_root"] = 2 }),
            ["loop"] = new JsonObject { ["unresolved"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 1 }) }
        };
        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1, ["image"] = "a.json" }, new JsonObject { ["id"] = 2, ["image"] = "b.json" }) };
        var request = new HybridAnalyzeRequest(2, fixture, root, output, 64, 32, AnalysisCacheDirectory: cache);
        var record = typeof(HybridScenePlanner).GetMethod("RecordLoopAllocationFallbackAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var planner = new HybridScenePlanner(new("not-started", "not-started", "not-started", []));
        try
        {
            // memo 传 null：子分析的 scene 照常经 AnalysisCache 按缓存目录落盘，看得出它用的是哪个目录。
            await (Task)record.Invoke(planner, [report, scene, request, output, scene, (Func<string, JsonObject?>)(_ => null), null, null,
                TestContext.Current.CancellationToken])!;
        }
        // 反射调用本身对不上（签名变了）要直接失败，不能当成子分析失败吞掉。
        catch (Exception error) when (error is not System.Reflection.TargetParameterCountException and not ArgumentException) { }
        Assert.NotNull(report["loop_allocation_fallback"]!["analysis_plan_path"]);
        Assert.True(File.Exists(Path.Combine(cache, "scene.json")));
        Assert.False(Directory.Exists(Path.Combine(cache, sha)));
    });

    [Fact]
    public async Task FixedDaytimeStateIsItsOwnRejectionNotNoBenefit()
    {
        // 按请求固定在一个时段的方案做不出按时段切换：拒因是固定时段（能力缺口），不写 no_benefit；--no-benefit allow 照旧能生成。
        await TestTemp.Run(async root =>
        {
            JsonObject Run(bool allowed) => AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "fixed-day-" + allowed),
                    DaytimeState: "day", AllowNoBenefit: allowed),
                (r, _) => Task.FromResult(Plan(r, true)), CancellationToken.None).GetAwaiter().GetResult();
            JsonObject rejected = Run(false);
            Assert.Equal([BlockerCode.FixedDaytimeState], PlanBlockers.Codes(rejected).ToArray());
            Assert.Null(rejected[NoBenefit.Field]);
            Assert.True(Admission.Accepted(Run(true)));
        });
    }

    [Fact]
    public async Task FramebufferReaderStaysOutOfRealtimeOnlyWhenThePlanIsNotWorse()
    {
        // 读帧缓冲的 20、30 画在 10 之后，按放宽后的规则没按读帧缓冲留实时。放宽前的判定经 LiveFramebufferReaderIds 在实时判定阶段恢复，
        // 不占用 --retain-live（分配回退照常能跑），也不带放宽后方案自己的回退留下的层（这里的 99）。
        // 放宽后的方案只在能生成、预计收益（省下的特效覆盖减视频路数）不少于、实时画布不大于放宽前时保留，否则用放宽前的；
        // 两边都不能生成时也用放宽前的。读取层被回退以别的原因留实时（retained_by_cost_trial）也算放宽涉及的层。
        await TestTemp.Run(async root =>
        {
            string runtime = Path.Combine(root, "runtime.json");
            JsonObject Reader(int id) => new() { ["owner"] = id, ["has_mesh"] = true, ["materials"] = new JsonArray(new JsonObject {
                ["textures"] = new JsonArray("_rt_FullFrameBuffer") }) };
            await File.WriteAllTextAsync(runtime, new JsonObject { ["runtime_layers"] = new JsonArray(
                new JsonObject { ["owner"] = 10, ["has_mesh"] = true }, Reader(20), Reader(30)) }.ToJsonString());
            var retainRequests = new List<int[]?>();
            async Task<JsonObject> RunAsync(string name, (bool Usable, double Coverage, int Groups, double Canvas) relaxed,
                (bool Usable, double Coverage, int Groups, double Canvas) before, string relaxedReader = "video") =>
                await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, name)), (r, _) =>
                {
                    bool restored = (r.LiveFramebufferReaderIds ?? []).Contains(20) && (r.LiveFramebufferReaderIds ?? []).Contains(30);
                    if (restored) retainRequests.Add(r.RetainLiveRootIds);
                    var (usable, coverage, groups, canvas) = restored ? before : relaxed;
                    JsonObject plan = Plan(r, usable, groups: groups);
                    plan["runtime_evidence"] = runtime;
                    // 放宽后的方案自己的回退留下了 99。
                    if (!restored) plan["settings"]!["retain_live_root_ids"] = new JsonArray(99);
                    JsonObject ReaderLayer(int id) => restored
                        ? new() { ["id"] = id, ["allocation"] = "live", ["reasons"] = new JsonArray("reads_current_framebuffer") }
                        : relaxedReader == "video" ? new() { ["id"] = id, ["allocation"] = "video", ["reasons"] = new JsonArray() }
                        : new() { ["id"] = id, ["allocation"] = "live", ["reasons"] = new JsonArray("retained_by_cost_trial") };
                    plan["layers"] = new JsonArray(ReaderLayer(20), ReaderLayer(30),
                        new JsonObject { ["id"] = 99, ["allocation"] = "live", ["canvas_fraction"] = canvas });
                    plan["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule,
                        ["evidence"] = new JsonObject { ["effect_pass_coverage"] = coverage } };
                    if (!usable) plan["loop"]!["unresolved"] = new JsonArray(new JsonObject { ["kind"] = "shader_period", ["owner_layer_id"] = 20 });
                    return Task.FromResult(plan);
                }, CancellationToken.None);
            static double Coverage(JsonObject plan) => plan["bake_value"]!["evidence"]!["effect_pass_coverage"]!.GetValue<double>();

            // 放宽后不能生成（20 带进凑不出循环的分量）：用放宽前的；恢复放宽前判定的重查不带 --retain-live。
            JsonObject unresolved = await RunAsync("reader-unresolved", (false, 5, 1, 0), (true, 2, 1, 0));
            Assert.True(Admission.Accepted(unresolved));
            Assert.Equal(2, Coverage(unresolved));
            Assert.All(retainRequests, ids => Assert.Null(ids));
            // 两边都不能生成：结论按放宽前。
            Assert.Equal(3, Coverage(await RunAsync("reader-both-blocked", (false, 5, 1, 0), (false, 3, 1, 0))));
            // 预计收益更少、实时画布更大：用放宽前的。
            Assert.Equal(3.44, Coverage(await RunAsync("reader-worse", (true, 1.61, 1, 3.21), (true, 3.44, 2, 1.29))));
            // 预计收益更多、但实时画布更大（3346715292 一类）：用放宽前的。
            Assert.Equal(3.44, Coverage(await RunAsync("reader-canvas", (true, 5, 2, 1.76), (true, 3.44, 2, 0.76))));
            // 读取层被回退以 retained_by_cost_trial 留实时、实时画布更大：同样用放宽前的。
            Assert.Equal(3.44, Coverage(await RunAsync("reader-retained", (true, 3.5, 2, 1.42), (true, 3.44, 2, 0.44), relaxedReader: "retained")));
            // 两项都不差：保留放宽后的。
            Assert.Equal(5, Coverage(await RunAsync("reader-better", (true, 5, 2, 0.4), (true, 3.44, 2, 0.4))));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlainGroupsGoLiveOnlyWhenTheRestStillProvesItsSaving(bool proven)
    {
        // 组 1 只有普通图层（10），组 2 有特效（20）。还给实时的重查要证得出剩下的组省电：省下多少算不出（needs_work_comparison、
        // 被烘层不全是普通图层）的不采用，普通组不因"不判"留实时、实时画布不变大。
        await TestTemp.Run(async root =>
        {
            string runtime = Path.Combine(root, "runtime.json");
            JsonObject Material(string shader, string role) => new() { ["shader"] = shader, ["role"] = role, ["active_uniforms"] = new JsonArray() };
            await File.WriteAllTextAsync(runtime, new JsonObject { ["runtime_layers"] = new JsonArray(
                new JsonObject { ["owner"] = 10, ["has_mesh"] = true, ["materials"] = new JsonArray(Material("genericimage2", "source")) },
                new JsonObject { ["owner"] = 20, ["has_mesh"] = true, ["has_effect_layer"] = true,
                    ["materials"] = new JsonArray(Material("genericimage2", "source"), Material("blur", "effect")) }) }.ToJsonString());
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "plain")), (r, _) =>
            {
                bool plainLive = (r.RetainLiveRootIds ?? []).Contains(10);
                JsonObject plan = Plan(r, true);
                plan["runtime_evidence"] = runtime;
                plan["settings"]!["retain_live_root_ids"] = plainLive ? new JsonArray(10) : null;
                plan["video_groups"] = plainLive
                    ? new JsonArray(new JsonObject { ["id"] = "group-2", ["root_ids"] = new JsonArray(20), ["layer_ids"] = new JsonArray(20) })
                    : new JsonArray(new JsonObject { ["id"] = "group-1", ["root_ids"] = new JsonArray(10), ["layer_ids"] = new JsonArray(10) },
                        new JsonObject { ["id"] = "group-2", ["root_ids"] = new JsonArray(20), ["layer_ids"] = new JsonArray(20) });
                plan["layers"] = new JsonArray(new JsonObject { ["id"] = 10, ["allocation"] = plainLive ? "live" : "video", ["canvas_fraction"] = 1.0 },
                    new JsonObject { ["id"] = 20, ["allocation"] = "video" });
                plan["bake_value"] = !plainLive || proven
                    ? new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule, ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 2.5 } }
                    : new JsonObject { ["status"] = "unknown", ["rule"] = WorkloadValue.CachedEffectPrefix.Rule,
                        ["evidence"] = new JsonObject { ["prefix_count"] = 1 } };
                if (plainLive && !proven)
                {
                    plan["route"] = "effect_prefix";
                    plan["effect_prefix_caches"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 20, ["prefix_effect_count"] = 1 });
                }
                return Task.FromResult(plan);
            }, CancellationToken.None);
            Assert.Equal(proven ? 1 : 2, result["video_groups"]!.AsArray().Count);
        });
    }

    [Fact]
    public void UnknownCoverageDoesNotBecomeZeroOrBeatAHeavyWholeLayer()
    {
        var request = new HybridAnalyzeRequest(2, "s", "a", "o");
        JsonObject prefix = Plan(request, true);
        prefix["route"] = "effect_prefix";
        prefix["effect_prefix_caches"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 10, ["prefix_effect_count"] = 1 },
            new JsonObject { ["owner_layer_id"] = 20, ["prefix_effect_count"] = 1 });
        prefix["bake_value"] = new JsonObject { ["status"] = "unknown", ["rule"] = WorkloadValue.CachedEffectPrefix.Rule };
        Assert.True(Admission.Accepted(prefix));
        Assert.False(AnalysisOrchestrator.KnownEffectWorkNotReduced(prefix, prefix));
        Assert.Empty(NoBenefit.AnalysisConditions(prefix));

        JsonObject whole = Plan(request, true, groups: 2);
        whole["bake_value"] = new JsonObject { ["status"] = "potential_gain", ["rule"] = WorkloadValue.CachedEffectPasses.Rule,
            ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 35.7 } };
        Assert.False(AnalysisOrchestrator.KnownEffectWorkNotReduced(whole, prefix));
        Assert.False(AnalysisOrchestrator.KnownEffectWorkNotReduced(prefix, whole));
        Assert.True(AnalysisOrchestrator.KnownEffectWorkNotReduced(whole, whole));
        whole["video_groups"]!.AsArray().Add(new JsonObject { ["id"] = "extra" });
        JsonObject fewerStreams = Plan(request, true, groups: 2);
        fewerStreams["bake_value"] = whole["bake_value"]!.DeepClone();
        Assert.False(AnalysisOrchestrator.KnownEffectWorkNotReduced(whole, fewerStreams));
    }

    private static JsonObject Plan(HybridAnalyzeRequest request, bool usable, string[]? states = null, int groups = 1) => new()
    {
        ["summary"] = new JsonObject { ["key"] = usable ? "summary.bakeable" : "summary.blocked", ["zh"] = "", ["en"] = "" },
        ["settings"] = new JsonObject { ["daytime_state"] = request.DaytimeState, ["preset"] = request.Preset, ["interaction"] = request.Interaction },
        ["route"] = "whole_layer",
        ["video_groups"] = new JsonArray(Enumerable.Range(0, groups).Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()),
        ["blockers"] = new JsonArray(),
        ["loop"] = new JsonObject { ["candidates"] = usable ? new JsonArray(new JsonObject { ["frames"] = 600 }) : new JsonArray() },
        ["daytime_split"] = states is null ? null : new JsonObject { ["status"] = "recognized",
            ["states"] = new JsonArray(states.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray()) }
    };
}
