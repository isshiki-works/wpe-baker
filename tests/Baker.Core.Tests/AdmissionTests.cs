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
        AdmissionVerdict verdict = Evaluate(Plan(new JsonArray(Maskable())));
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
        JsonObject plan = Plan(new JsonArray(Maskable()), groups: [[1]]);
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
        // 静态图仍带实时层且 bake_value 判低价值：默认拒绝（blocker + 拒因），显式允许后放行；没命中的方案原样通过。
        static JsonObject StaticWithLive(JsonObject plan)
        {
            plan["loop"]!["candidates"]![0]!["frames"] = 1;
            plan["live_layer_ids"] = new JsonArray(7);
            plan["bake_value"] = new JsonObject { ["status"] = WorkloadValue.LowValueStatus, ["rule"] = WorkloadValue.OneStillTextureUnchanged.Rule };
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

        // 静态成品没有视频解码开销：被烘层省下了特效就不拒。
        JsonObject savedEffects = StaticWithLive(Narrated(Plan(new JsonArray())));
        savedEffects["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule,
            ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 0.3 } };
        NoBenefit.Apply(savedEffects, allowed: false);
        Assert.True(Admission.Bakeable(savedEffects));

        // 没算出省下多少（unknown）：被烘层全是普通图层才拒，否则不判（粒子、源视频、自定义着色器、模型省下多少没算）。
        JsonObject unknownPlain = StaticWithLive(Narrated(Plan(new JsonArray())));
        unknownPlain["bake_value"] = new JsonObject { ["status"] = "unknown", ["rule"] = WorkloadValue.NeedsWorkComparison.Rule,
            ["evidence"] = new JsonObject { ["plain_group_ids"] = new JsonArray("group-1") } };
        Assert.Empty(NoBenefit.AnalysisConditions(unknownPlain));
        unknownPlain["bake_value"]!["evidence"]!["plain_group_ids"] = new JsonArray();
        Assert.Empty(NoBenefit.AnalysisConditions(unknownPlain));

        // 固定单个时段是能力缺口，不是不省电：单独一条拒因，不写 no_benefit。
        JsonObject fixedDay = Narrated(Plan(new JsonArray()));
        fixedDay["settings"]!["daytime_state"] = "00-07+18-24";
        Assert.Empty(NoBenefit.AnalysisConditions(fixedDay));
        DaytimeSplit.RejectFixedState(fixedDay, allowed: false);
        Assert.Equal([BlockerCode.FixedDaytimeState], PlanBlockers.Codes(fixedDay).ToArray());
        Assert.Null(fixedDay["no_benefit"]);
        Assert.Equal("fixed_daytime_state", fixedDay["suitability"]!["rule"]!.GetValue<string>());

        // 特效前缀路线：每个缓存都是一路视频，超过上限在分析时就拒。
        JsonObject prefixes = Narrated(Plan(new JsonArray(), route: "effect_prefix"));
        prefixes["effect_prefix_caches"] = new JsonArray([.. Enumerable.Range(0, 5).Select(i => (JsonNode)new JsonObject { ["owner_layer_id"] = i })]);
        Assert.Equal([NoBenefit.TooManyStreams], NoBenefit.AnalysisConditions(prefixes));
        JsonObject overLimit = prefixes.DeepClone().AsObject();
        NoBenefit.Apply(overLimit, allowed: false);
        Assert.Equal([BlockerCode.TooManyVideoGroups], PlanBlockers.Codes(overLimit).ToArray());
        Assert.Equal("too_many_video_groups", overLimit["suitability"]!["rule"]!.GetValue<string>());
        Assert.Null(overLimit[NoBenefit.Field]);
        JsonObject bakedOverLimit = new();
        NoBenefit.RejectStreams(bakedOverLimit, 5);
        Assert.Equal(NoBenefit.TooManyStreams, bakedOverLimit[NoBenefit.Field]!["status"]!.GetValue<string>());
        Assert.Contains("current limit", bakedOverLimit["reason"]!.GetValue<string>());
        Assert.DoesNotContain("power", bakedOverLimit["reason"]!.GetValue<string>());
        prefixes["effect_prefix_caches"]!.AsArray().RemoveAt(0);
        Assert.Empty(NoBenefit.AnalysisConditions(prefixes));

        // 整层路线：pass 覆盖/视频组数只是成本风险线索，不能作硬拒。
        JsonObject cheap = Narrated(Plan(new JsonArray(), groups: [[1], [3]]));
        cheap["bake_value"] = new JsonObject { ["rule"] = "cached_effect_passes", ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 1.9 } };
        Assert.Empty(NoBenefit.AnalysisConditions(cheap));
        Assert.True(NoBenefit.UncomparedVideoCost(cheap));
        Assert.Null(SlowClosureProbe.StreamLimit(cheap));
        cheap["video_groups"]![0]!["dynamic_verified"] = true;
        cheap["video_groups"]![1]!["dynamic_verified"] = true;
        Assert.Empty(NoBenefit.AnalysisConditions(cheap));
        // 前缀缓存从第 0 帧起录：要整周期预热的前缀循环不提（接缝门必拒）。
        JsonObject prefixLoop = new() { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600, ["components"] = new JsonArray(1) }) };
        Assert.True(EffectPrefixPlanner.Cacheable(prefixLoop));
        prefixLoop["candidates"]![0]!["source_period_warmup_frames"] = 600UL;
        Assert.False(EffectPrefixPlanner.Cacheable(prefixLoop));
        cheap["bake_value"]!["evidence"]!["effect_pass_coverage"] = 2.0;
        Assert.Empty(NoBenefit.AnalysisConditions(cheap));
        // 没算出省下多少：只有每个视频组都只有普通图层时才算省下 0，否则不判。
        cheap["bake_value"] = new JsonObject { ["rule"] = "needs_work_comparison", ["evidence"] = new JsonObject { ["plain_group_ids"] = new JsonArray("group-1") } };
        Assert.Empty(NoBenefit.AnalysisConditions(cheap));
        cheap["bake_value"]!["evidence"]!["plain_group_ids"]!.AsArray().Add("group-2");
        Assert.Empty(NoBenefit.AnalysisConditions(cheap));

        // 只差透视捕获：低覆盖不是硬拒，仍保留能力缺口结论。
        PlanBlockers.Add(cheap, new Blocker(BlockerCode.PerspectiveNeedsScreenspace));
        NoBenefit.Apply(cheap, allowed: false);
        Assert.Equal(NoBenefit.CaptureOpenStatus, cheap[NoBenefit.Field]!["status"]!.GetValue<string>());
        JsonObject gap = Narrated(Plan(new JsonArray()));
        PlanBlockers.Add(gap, new Blocker(BlockerCode.PerspectiveNeedsScreenspace));
        NoBenefit.Apply(gap, allowed: false);
        Assert.Equal(NoBenefit.CaptureOpenStatus, gap["no_benefit"]!["status"]!.GetValue<string>());
        Assert.Equal("capture_capability_gap", HybridSuitability.Verdict(gap)["rule"]!.GetValue<string>());

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

    [Theory]
    [InlineData(0.61, 14640)] // 2804379697: one unproven group, shader/animation periods in a long candidate.
    [InlineData(0.01, 377)]   // 2884628849: one unproven group beside a proven static group.
    public void LowCoverageRemainsUnknownEvenWhenVideoIsDynamic(double coverage, int frames)
    {
        JsonObject plan = Narrated(Plan(new JsonArray(), groups: [[1]]));
        plan["loop"]!["candidates"]![0]!["frames"] = frames;
        plan["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule,
            ["evidence"] = new JsonObject { ["effect_pass_coverage"] = coverage } };
        Assert.Empty(NoBenefit.AnalysisConditions(plan));
        Assert.True(NoBenefit.UncomparedVideoCost(plan));
        Assert.Null(SlowClosureProbe.StreamLimit(plan));
        NoBenefit.Apply(plan, allowed: false);
        Assert.Equal("unknown", plan["bake_value"]!["status"]!.GetValue<string>());
        Assert.Null(plan[NoBenefit.Field]);
        Assert.Equal(1, plan["bake_value"]!["evidence"]!["video_group_upper_bound"]!.GetValue<int>());
        Assert.Equal(0, plan["bake_value"]!["evidence"]!["dynamic_group_lower_bound"]!.GetValue<int>());
        plan["video_groups"]![0]!["dynamic_verified"] = true; // A changed sample proves this group needs video.
        Assert.Empty(NoBenefit.AnalysisConditions(plan));
        Assert.True(NoBenefit.UncomparedVideoCost(plan));
        NoBenefit.Apply(plan, allowed: false);
        Assert.Null(plan[NoBenefit.Field]);
        Assert.Equal("unknown", plan["bake_value"]!["status"]!.GetValue<string>());
        Assert.Equal(1, plan["bake_value"]!["evidence"]!["dynamic_group_lower_bound"]!.GetValue<int>());
    }

    [Fact]
    public void HeavyWholeLayerDoesNotNeedAStreamProbeForTheCostRule()
    {
        JsonObject plan = Narrated(Plan(new JsonArray(), groups: [[1], [3]]));
        plan["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule,
            ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 35.7 } };
        Assert.Empty(NoBenefit.AnalysisConditions(plan));
        Assert.False(NoBenefit.UncomparedVideoCost(plan));
        Assert.Null(SlowClosureProbe.StreamLimit(plan));
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
