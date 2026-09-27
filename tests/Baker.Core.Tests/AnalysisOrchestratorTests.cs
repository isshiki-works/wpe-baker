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
    public async Task NoExportWithoutACaller()
    {
        await TestTemp.Run(async root =>
        {
            int calls = 0;
            await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "gui"), DaytimeSplit: true),
                (r, _) => { calls++; return Task.FromResult(Plan(r, true, Day)); }, CancellationToken.None);
            // 第一格就能生成：只有母 plan 1 次。单时段方案必判不省电（fixed_daytime），不带 --no-benefit allow 时不展开；也没有逐状态导出。
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

    [Fact]
    public async Task RetreatCarriesTheReasonsOfLayersAlreadyRetained()
    {
        // 回退重查带上分配回退点名层的原因（INV-FRIEREN 层 64 的 term_not_retimable），不被盖成只剩 retained_by_cost_trial。
        await TestTemp.Run(async root =>
        {
            var retreats = new List<HybridAnalyzeRequest>();
            JsonObject Analyze(HybridAnalyzeRequest r)
            {
                if (r.RetainLiveRootIds is { Length: > 0 }) retreats.Add(r);
                JsonObject plan = Plan(r, true, groups: 2);
                plan["bake_value"] = new JsonObject { ["rule"] = "cached_effect_passes", ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 0.5 } };
                plan["loop_allocation_fallback"] = new JsonObject { ["retain_live_root_ids"] = new JsonArray(64), ["trigger_layer_ids"] = new JsonArray(64) };
                plan["loop"]!["unresolved"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 64, ["mechanism"] = "term_not_retimable" });
                return plan;
            }
            var reports = new List<RenderProgress>();
            await AnalysisOrchestrator.RunAsync(new(2, "s", "a", root, Interaction: "keep"), (r, _) => Task.FromResult(Analyze(r)), CancellationToken.None,
                progress: new Collect(reports));
            Assert.NotEmpty(retreats);
            Assert.All(retreats, r => Assert.Contains("term_not_retimable", r.RetainLiveReasons![64]));
            // 每试一种分组报一条阶段信息，按第 1、2… 种编号（命令行与界面同一通道）。
            Assert.Equal(Enumerable.Range(1, retreats.Count).Select(n => MessageCatalog.Get("progress.trying_grouping", MessageCatalog.Chinese, n)),
                reports.Where(p => p.Stage == "retreating").Select(p => p.Text!.In(MessageCatalog.Chinese)));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoBenefitWithSwitchableInteractionSuggestsOffWithoutApplyingIt(bool pointer)
    {
        // fixed 能生成但只烘普通图层（预计不省电）；关交互后烘下的层带特效、省电。有指针层才试 off，试出能就给一键建议，不自动关。
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            JsonObject Analyze(HybridAnalyzeRequest r)
            {
                calls.Add(r);
                JsonObject plan = Plan(r, true);
                plan["bake_value"] = r.Interaction == "off"
                    ? new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule, ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 2.0 } }
                    : new JsonObject { ["rule"] = WorkloadValue.NeedsWorkComparison.Rule };
                plan["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["kind"] = "image" },
                    new JsonObject { ["id"] = 2, ["kind"] = "image", ["tradeoff_kinds"] = pointer ? new JsonArray("pointer") : new JsonArray() });
                return plan;
            }
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "nobenefit")),
                (r, _) => Task.FromResult(Analyze(r)), CancellationToken.None);
            Assert.Equal(NoBenefit.ExpectedStatus, result[NoBenefit.Field]!["status"]!.GetValue<string>());
            Assert.Equal("fixed", result["settings"]!["interaction"]!.GetValue<string>());
            Assert.Empty(result["applied_tradeoffs"]!["properties"]!.AsObject());
            Assert.Empty(result["applied_tradeoffs"]!["turn_off_kinds"]!.AsArray());
            Assert.Equal(pointer, calls.Any(r => r.Interaction == "off"));
            Assert.Equal(pointer ? "off" : null, result["suggested_change"]?["settings"]?["interaction"]?.GetValue<string>());
        });
    }

    [Fact]
    public async Task MainAndOffRetreatsNeverShareAnOutputDirectory()
    {
        // 主路径退回后判不省电、再试关交互也退回：两次退回在同一个编排器里，子分析目录与进度序号都不能从头再来（489 张全集里 7 张撞了 "Analysis output must be new."）。
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
                // 省下的渲染抵不过两路视频（video_cost_over_saved_rendering）：主路径与 off 都会退回，退回的每一格也一样不省电。
                plan["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule, ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 0.5 } };
                plan["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["kind"] = "image", ["tradeoff_kinds"] = new JsonArray("pointer") });
                return plan;
            }
            var reports = new List<RenderProgress>();
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "twice")), (r, _) => Task.FromResult(Analyze(r)),
                CancellationToken.None, progress: new Collect(reports));
            Assert.Equal(NoBenefit.ExpectedStatus, result[NoBenefit.Field]!["status"]!.GetValue<string>());
            string[] retreating = [.. reports.Where(p => p.Stage == "retreating").Select(p => p.Text!.In(MessageCatalog.Chinese))];
            Assert.Equal(4, retreating.Length);
            Assert.Equal(Enumerable.Range(1, 4).Select(n => MessageCatalog.Get("progress.trying_grouping", MessageCatalog.Chinese, n)), retreating);
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
