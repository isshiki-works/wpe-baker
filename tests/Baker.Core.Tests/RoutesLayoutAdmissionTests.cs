using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C2.2d1：路线（Routes：整层 → 特效前缀 → 布局准入 → 再试前缀）与布局准入（LayoutAdmission：全幅取证、冲突、尾组降级、准入记录）。
[Trait("Layer", "L0")]
public class WholeLayerRefreshTests
{
    private static JsonObject Loop(int unresolved, int candidates) => new()
    {
        ["unresolved"] = new JsonArray(Enumerable.Range(0, unresolved).Select(_ => (JsonNode)new JsonObject { ["kind"] = "x" }).ToArray()),
        ["candidates"] = new JsonArray(Enumerable.Range(0, candidates).Select(_ => (JsonNode)new JsonObject { ["frames"] = 60 }).ToArray())
    };

    private static JsonObject Refreshed(int blockers, int unresolved, int candidates)
    {
        var plan = new JsonObject
        {
            ["blockers"] = new JsonArray(),
            ["loop"] = Loop(unresolved, candidates),
            ["whole_layer"] = new JsonObject { ["status"] = "stale" }
        };
        PlanBlockers.Set(plan, Enumerable.Repeat(new Blocker(BlockerCode.VideoShell), blockers));
        Routes.RefreshWholeLayer(plan);
        return plan;
    }

    [Theory]
    [InlineData(0, 0, 1, "available")]
    [InlineData(1, 0, 1, "unavailable")]
    [InlineData(0, 1, 1, "unavailable")]
    [InlineData(0, 0, 0, "unavailable")]
    public void StatusNeedsNoBlockerAndACompleteLoop(int blockers, int unresolved, int candidates, string expected) =>
        Assert.Equal(expected, Refreshed(blockers, unresolved, candidates)["whole_layer"]!["status"]!.GetValue<string>());

    [Fact]
    public void WholeLayerHoldsIndependentCopies()
    {
        JsonObject plan = Refreshed(1, 0, 1);
        Assert.True(JsonNode.DeepEquals(plan["blockers"], plan["whole_layer"]!["blockers"]));
        Assert.True(JsonNode.DeepEquals(plan["loop"], plan["whole_layer"]!["loop"]));
        plan["blockers"]!.AsArray().Clear();
        plan["loop"]!["candidates"]!.AsArray().Clear();
        Assert.Single(plan["whole_layer"]!["blockers"]!.AsArray());
        Assert.Single(plan["whole_layer"]!["loop"]!["candidates"]!.AsArray());
    }
}

[Trait("Layer", "L1")]
public class LayoutAdmissionTests
{
    // 夹具与 FullFrameDemotionChecks 同一个场景（底板 + 实时音频频谱 + 两个铺满画布的标签）：按请求的布局真实分析一遍，
    // 再去掉 W 段写下的准入结果，还原成进 W 段之前的 plan。标签占满画布时尾组降级因"视频不再是主体"被拒；
    // demotable 把两个标签的画布占比改小（等于 4×2 的小标签），降级就能采纳。
    internal static async Task<JsonObject> PlanAsync(string dir, string name, bool demotable = false, string layout = "full_frame")
    {
        JsonObject plan = await FullFrameDemotionChecks.AnalyzeAsync(dir, name, FullFrameDemotionChecks.Scene(64, 32), true, new JsonArray(), layout);
        Assert.Equal(2, plan["video_groups"]!.AsArray().Count);
        Assert.Equal(layout == "full_frame" ? 1 : 0, plan["blockers"]!.AsArray().Count);
        foreach (string key in new[] { "full_frame_retention", "layout_admission_demotion", "video_layout_admission", "blockers_localized" })
            plan.Remove(key);
        plan["whole_layer"]!.AsObject().Remove("layout_conflict");
        plan["blockers"] = new JsonArray();
        plan["status"] = "requires_loop_analysis";
        if (demotable)
            foreach (JsonObject layer in plan["layers"]!.AsArray().OfType<JsonObject>().Where(layer =>
                layer["id"]!.GetValue<int>() is FullFrameDemotionChecks.LabelA or FullFrameDemotionChecks.LabelB))
                layer["canvas_fraction"] = 0.003906;
        return plan;
    }

    internal static JsonObject CompleteLoop() => new() { ["unresolved"] = new JsonArray(), ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60 }) };

    private static string Text(JsonNode? node) => node!.GetValue<string>();

    [Fact]
    public Task DemotionKeepsOneGroupAndResolvesItsLoop() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await PlanAsync(dir, "applied", demotable: true);
        JsonObject? seen = null;
        var admission = LayoutAdmission.Evaluate(plan, "full_frame", false, 2, new JsonArray(), demoted => { seen = demoted; return CompleteLoop(); });
        Assert.True(admission.Demoted);
        Assert.Null(admission.Conflict);
        Assert.NotSame(plan, admission.Plan);
        Assert.Same(admission.Plan, seen);
        JsonObject result = admission.Plan;
        Assert.Single(result["video_groups"]!.AsArray());
        Assert.Equal("available", Text(plan["full_frame_retention"]!["status"]));
        Assert.Equal("applied", Text(result["full_frame_retention"]!["status"]));
        Assert.Equal("applied", Text(result["layout_admission_demotion"]!["status"]));
        Assert.True(JsonNode.DeepEquals(CompleteLoop(), result["loop"]));
        Assert.True(JsonNode.DeepEquals(CompleteLoop(), result["whole_layer"]!["loop"]));
        Assert.Equal("available", Text(result["whole_layer"]!["status"]));

        admission.Record(effectPrefix: false);
        JsonObject record = result["video_layout_admission"]!.AsObject();
        Assert.Equal("full_frame", Text(record["requested"]));
        Assert.Equal(LayoutAdmission.DemotedStatus, Text(record["status"]));
        Assert.Equal(Text(result["layout_admission_demotion"]!["reason"]), Text(record["reason"]));
        Assert.Empty(result["blockers"]!.AsArray());
        Assert.Null(result["whole_layer"]!["layout_conflict"]);
    });

    [Fact]
    public Task DemotedLoopWithoutCandidatesLeavesWholeLayerUnavailable() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await PlanAsync(dir, "no-candidate", demotable: true);
        var admission = LayoutAdmission.Evaluate(plan, "full_frame", false, 2, new JsonArray(),
            _ => new JsonObject { ["unresolved"] = new JsonArray(), ["candidates"] = new JsonArray() });
        Assert.True(admission.Demoted);
        Assert.Equal("unavailable", Text(admission.Plan["whole_layer"]!["status"]));
    });

    [Fact]
    public Task RejectedDemotionKeepsTheConflictAndBlocksThePlan() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await PlanAsync(dir, "wide");
        string status = Text(plan["status"]);
        var admission = LayoutAdmission.Evaluate(plan, "full_frame", false, 2, new JsonArray(), _ => throw new InvalidOperationException("不该重求循环"));
        Assert.False(admission.Demoted);
        Assert.NotNull(admission.Conflict);
        Assert.Same(plan, admission.Plan);
        Assert.Equal("not_applied", Text(plan["layout_admission_demotion"]!["status"]));
        Assert.NotNull(plan["full_frame_retention"]);
        Assert.Equal(status, Text(plan["status"]));

        admission.Record(effectPrefix: false);
        Assert.Equal(admission.Conflict!.Text, Text(plan["whole_layer"]!["layout_conflict"]));
        Assert.Equal("requires_user_choice", Text(plan["video_layout_admission"]!["status"]));
        Assert.Equal(admission.Conflict.Text, Text(plan["video_layout_admission"]!["reason"]));
        Assert.Contains(plan["blockers"]!.AsArray(), node => node!.GetValue<string>() == admission.Conflict.Text);
        Assert.Contains(admission.Conflict.Code, PlanBlockers.Codes(plan));
        Assert.Equal("requires_resolution", Text(plan["status"]));
    });

    [Fact]
    public Task EffectPrefixSkipsRetentionDemotionAndTheBlocker() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await PlanAsync(dir, "prefix");
        int blockers = plan["blockers"]!.AsArray().Count;
        string status = Text(plan["status"]);
        var admission = LayoutAdmission.Evaluate(plan, "full_frame", true, 2, new JsonArray(), _ => throw new InvalidOperationException());
        Assert.NotNull(admission.Conflict);
        Assert.Null(plan["full_frame_retention"]);
        Assert.Null(plan["layout_admission_demotion"]);
        admission.Record(effectPrefix: true);
        Assert.Equal("not_applicable_to_effect_prefix", Text(plan["video_layout_admission"]!["status"]));
        Assert.Equal(blockers, plan["blockers"]!.AsArray().Count);
        Assert.Equal(status, Text(plan["status"]));
    });

    [Fact]
    public Task LayeredLayoutIsAllowedWithoutARetentionRecord() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await PlanAsync(dir, "layered", layout: "layered");
        var admission = LayoutAdmission.Evaluate(plan, "layered", false, 2, new JsonArray(), _ => throw new InvalidOperationException());
        Assert.Null(admission.Conflict);
        Assert.Null(plan["full_frame_retention"]);
        admission.Record(effectPrefix: false);
        JsonObject record = plan["video_layout_admission"]!.AsObject();
        Assert.Equal(LayoutAdmission.AllowedStatus, Text(record["status"]));
        Assert.Null(record["reason"]);
    });

    [Fact]
    public Task FullFrameGroupCountGatesConflictAndDemotion() => TestTemp.Run(async dir =>
    {
        // 构图没给出视频组：不问全幅冲突（否则会对"没有组"再报一条无从执行的布局冲突）。
        JsonObject none = await PlanAsync(dir, "none");
        var noGroup = LayoutAdmission.Evaluate(none, "full_frame", false, 0, new JsonArray(), _ => throw new InvalidOperationException());
        Assert.Null(noGroup.Conflict);
        Assert.Null(none["full_frame_retention"]);
        noGroup.Record(effectPrefix: false);
        Assert.Equal("not_applicable_no_video_group", Text(none["video_layout_admission"]!["status"]));
        // 只有一组：照常判冲突，但不取证、不降级。
        JsonObject single = await PlanAsync(dir, "single", demotable: true);
        var one = LayoutAdmission.Evaluate(single, "full_frame", false, 1, new JsonArray(), _ => throw new InvalidOperationException());
        Assert.NotNull(one.Conflict);
        Assert.False(one.Demoted);
        Assert.Null(single["full_frame_retention"]);
        Assert.Null(single["layout_admission_demotion"]);
    });
}

[Trait("Layer", "L1")]
public class RoutesTests
{
    private sealed class PrefixStub(params JsonObject[] caches)
    {
        public int Calls { get; private set; }
        public Task<JsonArray> Next()
        {
            Calls++;
            return Task.FromResult(new JsonArray(caches.Select(cache => (JsonNode)cache.DeepClone()).ToArray()));
        }
    }

    private static readonly JsonObject Cache = new() { ["owner_layer_id"] = 7, ["terminal_effect_id"] = 1 };

    private static string Text(JsonNode? node) => node!.GetValue<string>();

    private static Task<(JsonObject Plan, bool EffectPrefix)> Settle(JsonObject plan, string layout, PrefixStub stub, bool effectPrefix = false) =>
        Routes.SettleAsync(plan, effectPrefix, layout, plan["video_groups"]!.AsArray().Count, new JsonArray(), stub.Next,
            _ => LayoutAdmissionTests.CompleteLoop());

    private static void Block(JsonObject plan)
    {
        PlanBlockers.Set(plan, [new Blocker(BlockerCode.PerspectiveNeedsScreenspace)]);
        plan["status"] = "requires_resolution";
    }

    [Fact]
    public Task BlockedWholeLayerFallsBackToEffectPrefix() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await LayoutAdmissionTests.PlanAsync(dir, "blocked", layout: "layered");
        Block(plan);
        var stub = new PrefixStub(Cache);
        var (result, prefix) = await Settle(plan, "layered", stub);
        Assert.True(prefix);
        Assert.Equal(1, stub.Calls);
        Assert.Equal("effect_prefix", Text(result["route"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray(Cache.DeepClone()), result["effect_prefix_caches"]));
        Assert.Empty(result["blockers"]!.AsArray());
        Assert.Equal("requires_loop_analysis", Text(result["status"]));
        Assert.Equal("not_applicable_to_effect_prefix", Text(result["video_layout_admission"]!["status"]));
    });

    [Fact]
    public Task NoPrefixCacheLeavesTheWholeLayerBlocked() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await LayoutAdmissionTests.PlanAsync(dir, "no-cache", layout: "layered");
        Block(plan);
        var stub = new PrefixStub();
        var (result, prefix) = await Settle(plan, "layered", stub);
        Assert.False(prefix);
        Assert.Equal(1, stub.Calls);
        Assert.Equal("whole_layer", Text(result["route"]));
        Assert.Equal("requires_resolution", Text(result["status"]));
        Assert.Single(result["blockers"]!.AsArray());
    });

    [Fact]
    public Task UnblockedAllowedLayoutNeverAsksForPrefixCaches() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await LayoutAdmissionTests.PlanAsync(dir, "clear", layout: "layered");
        Assert.NotEqual("requires_resolution", Text(plan["status"]));
        var stub = new PrefixStub(Cache);
        var (result, prefix) = await Settle(plan, "layered", stub);
        Assert.False(prefix);
        Assert.Equal(0, stub.Calls);
        Assert.Same(plan, result);
        Assert.Equal(LayoutAdmission.AllowedStatus, Text(result["video_layout_admission"]!["status"]));
    });

    [Fact]
    public Task AnalysisAlreadyOnEffectPrefixIsNotAskedAgain() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await LayoutAdmissionTests.PlanAsync(dir, "on-prefix");
        Block(plan);
        var stub = new PrefixStub(Cache);
        var (_, prefix) = await Settle(plan, "full_frame", stub, effectPrefix: true);
        Assert.True(prefix);
        Assert.Equal(0, stub.Calls);
    });

    [Fact]
    public Task LayoutConflictTriesEffectPrefixAfterDemotionFails() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await LayoutAdmissionTests.PlanAsync(dir, "conflict-prefix");
        var stub = new PrefixStub(Cache);
        var (result, prefix) = await Settle(plan, "full_frame", stub);
        Assert.True(prefix);
        Assert.Equal(1, stub.Calls);
        Assert.Same(plan, result);
        Assert.Equal("not_applied", Text(result["layout_admission_demotion"]!["status"]));
        Assert.Equal("effect_prefix", Text(result["route"]));
        Assert.Empty(result["blockers"]!.AsArray());
        Assert.Equal("requires_loop_analysis", Text(result["status"]));
        Assert.Equal("not_applicable_to_effect_prefix", Text(result["video_layout_admission"]!["status"]));
        Assert.NotNull(result["whole_layer"]!["layout_conflict"]);
    });

    [Fact]
    public Task LayoutConflictWithoutPrefixBlocksThePlan() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await LayoutAdmissionTests.PlanAsync(dir, "conflict");
        int blockers = plan["blockers"]!.AsArray().Count;
        var stub = new PrefixStub();
        var (result, prefix) = await Settle(plan, "full_frame", stub);
        Assert.False(prefix);
        Assert.Equal(1, stub.Calls);
        Assert.Equal("requires_resolution", Text(result["status"]));
        Assert.Equal("requires_user_choice", Text(result["video_layout_admission"]!["status"]));
        Assert.Equal(blockers + 1, result["blockers"]!.AsArray().Count);
    });

    [Fact]
    public Task DemotionReplacesThePlanWithoutAskingForPrefix() => TestTemp.Run(async dir =>
    {
        JsonObject plan = await LayoutAdmissionTests.PlanAsync(dir, "demoted", demotable: true);
        var stub = new PrefixStub(Cache);
        var (result, prefix) = await Settle(plan, "full_frame", stub);
        Assert.False(prefix);
        Assert.Equal(0, stub.Calls);
        Assert.NotSame(plan, result);
        Assert.Single(result["video_groups"]!.AsArray());
        Assert.Equal(LayoutAdmission.DemotedStatus, Text(result["video_layout_admission"]!["status"]));
    });
}
