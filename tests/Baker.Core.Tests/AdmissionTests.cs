using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C1.3：生成准入单点。analyze、PresetCascade、bake 第一步都调 Admission.Evaluate，这里守住每类判据的结论。
[Trait("Layer", "L0")]
public class AdmissionTests
{
    private static readonly JsonObject Scene = new()
    {
        ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }, new JsonObject { ["id"] = 30 }, new JsonObject { ["id"] = 3, ["parent"] = 30 })
    };

    private static JsonObject? NoResource(string _) => null;

    // 可掩盖：脚本随机重启的精灵。
    private static JsonObject Maskable() => new()
    {
        ["kind"] = "runtime_animation", ["owner_layer_id"] = 3, ["mechanism"] = "sprite", ["random_restart"] = true,
        ["track_name"] = "flip", ["detail"] = "Script playback restarts this sprite animation after a Math.random() delay."
    };

    // 不可掩盖：时长或来源解析不了的运行时动画。
    private static JsonObject Unmaskable() => new()
    {
        ["kind"] = "runtime_animation", ["owner_layer_id"] = 3, ["detail"] = "Runtime duration or source owner cannot be resolved exactly."
    };

    private static JsonObject Note() => new() { ["kind"] = ResidualMasking.AllocationFallbackKind, ["detail"] = "A smaller allocation note." };

    private static JsonObject Plan(JsonArray unresolved, int candidates = 1, int[][]? groups = null, string route = "whole_layer",
        string? layoutConflict = null) => new()
    {
        ["kind"] = "hybrid_video", ["route"] = route, ["status"] = "requires_loop_analysis",
        ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080, ["video_layout"] = "layered", ["retain_live_root_ids"] = null },
        ["canvas_width"] = 1920, ["canvas_height"] = 1080,
        ["layers"] = new JsonArray(
            new JsonObject { ["id"] = 1, ["root"] = 1, ["name"] = "background", ["canvas_fraction"] = 1.0 },
            new JsonObject { ["id"] = 30, ["root"] = 30, ["name"] = "holder" },
            new JsonObject { ["id"] = 3, ["root"] = 30, ["parent"] = 30, ["name"] = "sprite", ["canvas_fraction"] = 0.05 }),
        ["video_groups"] = new JsonArray([.. (groups ?? [[1, 3]]).Select((ids, index) => (JsonNode)new JsonObject
        {
            ["id"] = $"group-{index + 1}", ["layer_ids"] = new JsonArray([.. ids.Select(id => (JsonNode)JsonValue.Create(id))]),
            ["include_scene_clear"] = index == 0
        })]),
        ["blockers"] = new JsonArray(),
        ["whole_layer"] = layoutConflict is null ? new JsonObject { ["blockers"] = new JsonArray() }
            : new JsonObject { ["blockers"] = new JsonArray(), ["layout_conflict"] = layoutConflict },
        ["loop"] = new JsonObject
        {
            ["candidates"] = new JsonArray([.. Enumerable.Range(0, candidates).Select(_ => (JsonNode)new JsonObject { ["frames"] = 600 })]),
            ["unresolved"] = unresolved
        }
    };

    private static AdmissionVerdict Evaluate(JsonObject plan) => Admission.Evaluate(plan, Scene, NoResource);

    [Fact]
    public void NoResidualIsAdmitted()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray()));
        Assert.True(verdict.Admitted);
        Assert.Equal("no_residual", verdict.Residual!["status"]!.GetValue<string>());
        Assert.Null(verdict.Blocker);
    }

    [Fact]
    public void ExplanatoryNotesAreNotMechanisms()
    {
        // 只剩说明性条目时就是没有残差：cascade 与 bake 同一口径（原来 bake 会把它当"不可掩盖"拒绝）。
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Note())));
        Assert.True(verdict.Admitted);
        Assert.Equal("no_residual", verdict.Residual!["status"]!.GetValue<string>());
    }

    [Fact]
    public void NoCandidateIsNoLoop()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(), candidates: 0));
        Assert.Equal(AdmissionRejection.NoLoop, verdict.Rejection);
        Assert.Null(verdict.Blocker);
    }

    [Fact]
    public void UnmaskableResidualBlocksWithBakeAllocation()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Unmaskable())));
        Assert.Equal(AdmissionRejection.ResidualNotMaskable, verdict.Rejection);
        Assert.Equal(BlockerCode.BakeAllocation, verdict.Blocker!.Code);
        Assert.Equal("rejected", verdict.Residual!["status"]!.GetValue<string>());
    }

    [Fact]
    public void UnmaskableResidualWithoutCandidateStillRecordsTheBlocker()
    {
        // bake 先按无循环拒；分析照样写 blocker.bake_allocation，两边都拒。
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Unmaskable()), candidates: 0));
        Assert.Equal(AdmissionRejection.NoLoop, verdict.Rejection);
        Assert.Equal(BlockerCode.BakeAllocation, verdict.Blocker!.Code);
    }

    [Fact]
    public void MaskableResidualInsideAGroupIsAdmitted()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Maskable(), Note())));
        Assert.True(verdict.Admitted);
        Assert.Equal("residual_maskable", verdict.Residual!["status"]!.GetValue<string>());
    }

    [Fact]
    public void MaskableResidualOutsideEveryGroupIsALayoutRejection()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Maskable()), groups: [[1]]));
        Assert.Equal(AdmissionRejection.ResidualLayout, verdict.Rejection);
        Assert.NotNull(verdict.LayoutGate);
        Assert.NotNull(verdict.Blocker);
        Assert.NotEqual(BlockerCode.BakeAllocation, verdict.Blocker!.Code);
    }

    [Fact]
    public void ExistingLayoutConflictIsNotStacked()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Maskable()), groups: [[1]], layoutConflict: "conflict"));
        Assert.True(verdict.Admitted);
        Assert.Null(verdict.Blocker);
    }

    [Fact]
    public void EffectPrefixRouteHasNoResidualAdmission()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Unmaskable()), route: "effect_prefix"));
        Assert.True(verdict.Admitted);
        Assert.Null(verdict.Residual);
    }

    [Fact]
    public void EvaluateDoesNotChangeThePlan()
    {
        JsonObject plan = Plan(new JsonArray(Maskable(), Note()), groups: [[1]]);
        string before = plan.ToJsonString();
        Evaluate(plan);
        Assert.Equal(before, plan.ToJsonString());
    }

    private static JsonObject Narrated(JsonObject plan)
    {
        plan["suitability"] = HybridScenePlanner.Suitability(plan);
        PlanNarrative.Attach(plan);
        return plan;
    }

    [Fact]
    public void BakeableMatchesTheNarratedConclusion()
    {
        // 可烘改按结构判定后，必须与结论行 summary.bakeable* 的判定逐一相同（原来 cascade 读 summary.key 前缀）。
        JsonObject bakeable = Narrated(Plan(new JsonArray()));
        JsonObject blocked = Plan(new JsonArray());
        blocked["blockers"]!.AsArray().Add(new Blocker(BlockerCode.VideoShell).ToNode());
        blocked = Narrated(blocked);
        JsonObject noCandidate = Narrated(Plan(new JsonArray(Unmaskable()), candidates: 0));
        foreach (JsonObject plan in new[] { bakeable, blocked, noCandidate })
            Assert.Equal(plan["summary"]!["key"]!.GetValue<string>().StartsWith("summary.bakeable", StringComparison.Ordinal), Admission.Bakeable(plan));
        Assert.True(Admission.Bakeable(bakeable));
        Assert.False(Admission.Bakeable(blocked));
        Assert.False(Admission.Bakeable(noCandidate));
    }

    [Fact]
    public void NoIndependentContentIsNotBakeable()
    {
        JsonObject plan = Plan(new JsonArray());
        plan["suitability"] = new JsonObject { ["rule"] = "no_independent_content_after_reallocation" };
        Assert.False(Admission.Bakeable(plan));
    }

    [Fact]
    public void EffectPrefixCandidateIsBakeable()
    {
        JsonObject plan = Plan(new JsonArray(), candidates: 0, route: "effect_prefix");
        plan["effect_prefix_caches"] = new JsonArray(new JsonObject { ["loop"] = new JsonObject { ["candidates"] = new JsonArray() } },
            new JsonObject { ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60 }) } });
        Assert.True(Admission.Bakeable(plan));
        Assert.Equal(2, Admission.GroupCount(plan));
    }

    [Fact]
    public void GroupBudgetCountsOnlyDynamicGroups()
    {
        JsonObject plan = Plan(new JsonArray(), groups: [[1], [3], [30], [4], [5]]);
        Assert.Equal(5, Admission.GroupCount(plan));
        Assert.False(Admission.Accepted(plan));
        plan["video_groups"]![4]!["static_verified"] = true;
        plan["video_groups"]![4]!["static_verification"] = new JsonObject { ["basis"] = "source_and_runtime_static_proof" };
        Assert.Equal(4, Admission.GroupCount(plan));
        Assert.Equal(1, Admission.StaticGroupCount(plan));
        Assert.True(Admission.Accepted(plan));
    }
}
