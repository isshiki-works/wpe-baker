using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C1.3：生成准入单点。analyze、分析编排（C2.2e 起 AnalysisOrchestrator）、bake 第一步都调 Admission.Evaluate，这里守住每类判据的结论。
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
        Assert.Equal(AdmissionRejection.None, verdict.Rejection);
        Assert.Equal("no_residual", verdict.Residual!["status"]!.GetValue<string>());
        Assert.Null(verdict.Blocker);
    }

    [Fact]
    public void ExplanatoryNotesAreNotMechanisms()
    {
        // 只剩说明性条目时就是没有残差：cascade 与 bake 同一口径（原来 bake 会把它当"不可掩盖"拒绝）。
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Note())));
        Assert.Equal(AdmissionRejection.None, verdict.Rejection);
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
        Assert.Equal(AdmissionRejection.None, verdict.Rejection);
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
        Assert.Equal(AdmissionRejection.None, verdict.Rejection);
        Assert.Null(verdict.Blocker);
    }

    [Fact]
    public void EffectPrefixRouteHasNoResidualAdmission()
    {
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Unmaskable()), route: "effect_prefix"));
        Assert.Equal(AdmissionRejection.None, verdict.Rejection);
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
        plan["suitability"] = HybridSuitability.Verdict(plan);
        PlanNarrative.Attach(plan);
        return plan;
    }

    [Fact]
    public void NoBenefitPlansAreRejectedByDefaultAndPassWhenAllowed()
    {
        // 静态图仍带实时层、固定时段：默认拒绝（blocker + 拒因），显式允许后放行；没命中的方案原样通过。
        static JsonObject StaticWithLive(JsonObject plan)
        {
            plan["loop"]!["candidates"]![0]!["frames"] = 1;
            plan["live_layer_ids"] = new JsonArray(7);
            return plan;
        }
        JsonObject rejected = StaticWithLive(Narrated(Plan(new JsonArray())));
        NoBenefit.Apply(rejected, allowed: false);
        Assert.Equal([BlockerCode.NoBenefitExpected], PlanBlockers.Codes(rejected).ToArray());
        Assert.False(Admission.Bakeable(rejected));
        Assert.Equal(NoBenefit.RejectionReason, rejected["preset_rejection_reason"]!.GetValue<string>());
        Assert.Equal([NoBenefit.StaticWithLive], NoBenefit.AnalysisConditions(rejected));

        JsonObject allowed = StaticWithLive(Narrated(Plan(new JsonArray())));
        NoBenefit.Apply(allowed, allowed: true);
        Assert.True(Admission.Bakeable(allowed));
        Assert.Equal(NoBenefit.OverrideStatus, allowed["no_benefit"]!["status"]!.GetValue<string>());

        JsonObject noLive = StaticWithLive(Narrated(Plan(new JsonArray())));
        noLive["live_layer_ids"] = new JsonArray();
        NoBenefit.Apply(noLive, allowed: false);
        Assert.True(Admission.Bakeable(noLive));

        JsonObject fixedDay = Narrated(Plan(new JsonArray()));
        fixedDay["settings"]!["daytime_state"] = "00-07+18-24";
        NoBenefit.Apply(fixedDay, allowed: false);
        Assert.Equal([BlockerCode.NoBenefitExpected], PlanBlockers.Codes(fixedDay).ToArray());

        JsonObject ordinary = Narrated(Plan(new JsonArray()));
        NoBenefit.Apply(ordinary, allowed: false);
        Assert.True(Admission.Bakeable(ordinary));
        Assert.Null(ordinary["no_benefit"]);

        // 视频流路数在烘焙时按实际编出的流判：4 路放行，5 路拒；settings 里的允许标记随 plan 走。
        Assert.False(NoBenefit.TooManyVideoStreams(4));
        Assert.True(NoBenefit.TooManyVideoStreams(5));
        Assert.False(NoBenefit.Allowed(ordinary));
        ordinary["settings"]!["allow_no_benefit"] = true;
        Assert.True(NoBenefit.Allowed(ordinary));
    }

    [Fact]
    public void BakeableMatchesTheNarratedConclusion()
    {
        // 可烘改按结构判定后，必须与结论行 summary.bakeable* 的判定逐一相同（原来 cascade 读 summary.key 前缀）。
        JsonObject bakeable = Narrated(Plan(new JsonArray()));
        JsonObject blocked = Plan(new JsonArray());
        PlanBlockers.Add(blocked, new Blocker(BlockerCode.VideoShell));
        blocked = Narrated(blocked);
        JsonObject noCandidate = Narrated(Plan(new JsonArray(Unmaskable()), candidates: 0));
        foreach (JsonObject plan in new[] { bakeable, blocked, noCandidate })
            Assert.Equal(plan["summary"]!["key"]!.GetValue<string>().StartsWith("summary.bakeable", StringComparison.Ordinal), Admission.Bakeable(plan));
        Assert.True(Admission.Bakeable(bakeable));
        Assert.False(Admission.Bakeable(blocked));
        Assert.False(Admission.Bakeable(noCandidate));
    }

    [Fact]
    public void GenerationAdmissionWritesTheBlockerBakeWouldRejectWith()
    {
        // 分析说不能生成的，正是 bake 第一步会拒的：blocker.bake_allocation、requires_resolution、结论行不再是可烘。
        JsonObject rejected = Narrated(Plan(new JsonArray(Unmaskable())));
        Assert.True(Admission.Bakeable(rejected));
        Admission.ApplyGenerationAdmission(rejected, Scene, NoResource);
        Assert.Equal([BlockerCode.BakeAllocation], PlanBlockers.Codes(rejected).ToArray());
        Assert.Equal("requires_resolution", rejected["status"]!.GetValue<string>());
        Assert.Equal("rejected", rejected["loop"]!["residual_masking"]!["status"]!.GetValue<string>());
        Assert.False(Admission.Bakeable(rejected));
        Assert.False(rejected["summary"]!["key"]!.GetValue<string>().StartsWith("summary.bakeable", StringComparison.Ordinal));

        JsonObject admitted = Narrated(Plan(new JsonArray(Maskable())));
        Admission.ApplyGenerationAdmission(admitted, Scene, NoResource);
        Assert.Empty(PlanBlockers.Codes(admitted));
        Assert.Equal("residual_maskable", admitted["loop"]!["residual_masking"]!["status"]!.GetValue<string>());
        Assert.True(Admission.Bakeable(admitted));
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
        JsonObject plan = Plan(new JsonArray(), groups: [[1], [3], [30], [4], [5], [6], [7], [8], [9], [10]]);
        plan["settings"] = new JsonObject { ["width"] = 1920u, ["height"] = 1080u, ["fps_numerator"] = 60u, ["fps_denominator"] = 1u };
        Assert.Equal(10, Admission.GroupCount(plan));
        Assert.False(Admission.Accepted(plan));
        plan["video_groups"]![9]!["static_verified"] = true;
        plan["video_groups"]![9]!["static_verification"] = new JsonObject { ["basis"] = "source_and_runtime_static_proof" };
        Assert.Equal(9, Admission.GroupCount(plan));
        Assert.Equal(1, Admission.StaticGroupCount(plan));
        Assert.True(Admission.Accepted(plan));
        // 解码量按输出像素率换算：1440p60 只放 5 路，4K60 不低于旧政策的 4 路。
        plan["settings"]!["width"] = 2560u; plan["settings"]!["height"] = 1440u;
        Assert.Equal(5, Admission.MaxVideoGroups(plan));
        plan["settings"]!["width"] = 3840u; plan["settings"]!["height"] = 2160u;
        Assert.Equal(4, Admission.MaxVideoGroups(plan));
    }
}

// bake 第一步与分析同一口径：重算循环后留下不可掩盖的未解析分量，bake 在任何渲染之前按"无循环"干净拒绝，
// 写出的残差分类与 Admission.Evaluate 对同一份 plan 给出的 blocker.bake_allocation 是同一个结论。
[Trait("Layer", "L1")]
public class AdmissionBakeTests
{
    // 层 1 的动画置信度决定有没有解析候选；层 3 置信度不够，留下不可掩盖的未解析分量。
    private static async Task<(JsonObject Result, JsonObject Scene)> BakeAsync(string root, string firstConfidence)
    {
        string sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        JsonObject Owner(int id) => new()
        {
            ["id"] = id, ["size"] = "100 100", ["visible"] = true,
            ["animationlayers"] = new JsonArray(new JsonObject { ["id"] = 50, ["name"] = "sway", ["rate"] = 1 })
        };
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "scene.json"), new JsonObject
        {
            ["general"] = new JsonObject(), ["objects"] = new JsonArray(Owner(1), Owner(3))
        }.ToJsonString());
        string runtimeEvidence = Path.Combine(root, "runtime.json");
        await File.WriteAllTextAsync(runtimeEvidence, new JsonObject
        {
            ["status"] = "complete", ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_dependencies"] = new JsonArray(),
            ["runtime_animation_periods"] = new JsonArray(
                new JsonObject
                {
                    ["source_owner_layer_id"] = 1, ["mechanism"] = "puppet_bone", ["track_name"] = "sway", ["duration_seconds"] = 2,
                    ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = firstConfidence, ["playback_rate"] = 1
                },
                new JsonObject
                {
                    ["source_owner_layer_id"] = 3, ["mechanism"] = "sprite", ["track_name"] = "flip", ["duration_seconds"] = 1.68,
                    ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = "low"
                })
        }.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        var settings = new HybridAnalyzeRequest(2, sourceDirectory, root, Path.Combine(root, "analysis"), 64, 32, 60, 1, VideoLayout: "layered");
        var plan = new JsonObject
        {
            ["kind"] = "hybrid_video", ["route"] = "whole_layer", ["source"] = sourceDirectory, ["source_sha256"] = await source.SourceHashAsync(),
            ["settings"] = System.Text.Json.JsonSerializer.SerializeToNode(settings,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }),
            ["loop"] = new JsonObject { ["status"] = "observed", ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) },
            ["video_groups"] = new JsonArray(new JsonObject
            {
                ["id"] = "group-1", ["layer_ids"] = new JsonArray(1, 3), ["include_scene_clear"] = true, ["transparent"] = false
            }),
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["root"] = 1, ["name"] = "background" },
                new JsonObject { ["id"] = 3, ["root"] = 3, ["name"] = "sprite" }),
            ["blockers"] = new JsonArray(), ["snapshot_properties"] = new JsonObject(), ["runtime_evidence"] = runtimeEvidence,
            ["source_script_error_evidence"] = new JsonObject { ["status"] = "available" },
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray()
        };
        V3Fixture.Upgrade(plan);
        string output = Path.Combine(root, "bake");
        JsonObject result = await new HybridBakeService(new("not-started", "not-started", "not-started", [])).BakeAsync(new(2, plan, output));
        return (result, source.ReadJson(source.SceneResource));
    }

    [Fact]
    public async Task BakeRejectsTheResidualAnalysisWouldBlock() => await TestTemp.Run(async root =>
    {
        (JsonObject result, JsonObject scene) = await BakeAsync(root, "high");
        Assert.Equal("candidate_rejected_no_loop", result["status"]!.GetValue<string>());
        Assert.Equal("rejected", result["residual_masking"]?["status"]?.GetValue<string>());
        // 同一份（bake 重算过循环的）plan 交给分析侧的判定，给出的正是 blocker.bake_allocation。
        JsonObject refreshed = result["plan"]!.AsObject();
        AdmissionVerdict verdict = Admission.Evaluate(refreshed, scene, _ => null);
        Assert.Equal(AdmissionRejection.ResidualNotMaskable, verdict.Rejection);
        Assert.Equal(BlockerCode.BakeAllocation, verdict.Blocker!.Code);
    });

    [Fact]
    public async Task BakeWithoutCandidateStopsBeforeResidualClassification() => await TestTemp.Run(async root =>
    {
        // 没有解析候选时 bake 先按无循环拒，不做残差分类，bake.json 里的 plan 副本也不带 residual_masking。
        (JsonObject result, _) = await BakeAsync(root, "low");
        Assert.Equal("candidate_rejected_no_loop", result["status"]!.GetValue<string>());
        Assert.Null(result["residual_masking"]);
        Assert.Null(result["plan"]!["loop"]!["residual_masking"]);
    });
}
