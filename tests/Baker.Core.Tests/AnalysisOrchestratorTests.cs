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
        Assert.Equal((10, 8), (standard.FixedCalls, standard.PerState));
        // keep/fixed/off × 三档 × 两种布局 = 18 + 2；keep 不展开，fixed、off 每个状态 12，逐状态导出每个状态再 1。
        CallBudget widest = SearchSpace.Of(new(2, "s", "a", "o", Preset: "quality", Interaction: "keep", DaytimeSplit: true), true).Budget();
        Assert.Equal((20, 13), (widest.FixedCalls, widest.PerState));
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
        Assert.Equal(2, grown.Used);
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
        });
    }

    [Fact]
    public async Task WorstCaseUsesExactlyTheDefaultBudget()
    {
        await TestTemp.Run(async root =>
        {
            var request = new HybridAnalyzeRequest(2, "s", "a", Path.Combine(root, "worst"), Preset: "quality", Interaction: "keep", DaytimeSplit: true);
            CallBudget budget = SearchSpace.Of(request, true).Budget();
            int calls = 0;
            await AnalysisOrchestrator.RunAsync(request, (r, _) => { calls++; return Task.FromResult(Plan(r, false, Day)); }, CancellationToken.None,
                export: new(name => Path.Combine(root, "worst-" + name + ".json")), budget: budget);
            Assert.Equal(budget.Limit(Day.Length), calls);
            Assert.Equal(calls, budget.Used);
        });
    }

    [Fact]
    public async Task ExceedingTheBudgetIsAnInternalErrorBeforeTheCall()
    {
        await TestTemp.Run(async root =>
        {
            var request = new HybridAnalyzeRequest(2, "s", "a", Path.Combine(root, "short"), Preset: "quality", Interaction: "keep", DaytimeSplit: true);
            CallBudget full = SearchSpace.Of(request, true).Budget();
            int calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => AnalysisOrchestrator.RunAsync(request,
                (r, _) => { calls++; return Task.FromResult(Plan(r, false, Day)); }, CancellationToken.None,
                export: new(name => Path.Combine(root, "short-" + name + ".json")), budget: new CallBudget(full.FixedCalls - 1, full.PerState)));
            Assert.Equal(full.Limit(Day.Length) - 1, calls);
        });
    }

    [Fact]
    public async Task StatesAreSearchedOnlyOutsideKeepAndBestStateWins()
    {
        await TestTemp.Run(async root =>
        {
            var calls = new List<HybridAnalyzeRequest>();
            JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "states"), Interaction: "keep"),
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
            // 第一格就能生成：母 plan 1 次 + 四个状态各 1 次，没有逐状态导出。
            Assert.Equal(5, calls);
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
