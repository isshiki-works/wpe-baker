using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 分析期判"能"、烘焙期才被拒的几类挪到分析：烘焙预检的读数怎么改方案（ProbeReplan）、起点搜索读数判残差、动态组的判定、
// 路数只按证出的动态组拒、烘焙不再退回重烘、重试期间盘上是"还在跑"。都不需要 GPU 与 WPE 素材。
[Trait("Layer", "L1")]
public class AnalysisProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedResidualReplansUseIndependentRefreshDirectories(bool trailingSeparator) => await TestTemp.Run(async root =>
    {
        string output = Path.Combine(root, "bake"), sentinel = output + ".analysis-refresh";
        Directory.CreateDirectory(sentinel);
        File.WriteAllText(Path.Combine(sentinel, "user.txt"), "preserve");
        var plan = new JsonObject {
            ["settings"] = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "source", "assets", "analysis", RetainLiveRootIds: [3])),
            ["loop"] = new JsonObject { ["unresolved"] = new JsonArray(
                new JsonObject { ["owner_layer_id"] = 7, ["mechanism"] = "particle_system" },
                new JsonObject { ["owner_layer_id"] = 8, ["mechanism"] = "particle_system" }) }
        };
        int bakes = 0;
        var refreshes = new List<string>();
        var service = new HybridBakeService(new("not-started", "not-started", "not-started", []),
            bakeOnce: _ => {
                Directory.CreateDirectory(output);
                if (++bakes == 3) return Task.FromResult(new JsonObject { ["status"] = "candidate_generated" });
                int owner = bakes == 1 ? 7 : 8;
                return Task.FromResult(new JsonObject { ["status"] = "candidate_rejected_seam", ["loop_validation"] = "residual_above_limits",
                    ["groups"] = new JsonArray(new JsonObject { ["id"] = "g" + owner, ["source_layers"] = new JsonArray(owner), ["status"] = "rejected_seam_residual" }),
                    ["residual_masking"] = new JsonObject { ["residual_layers"] = new JsonArray(new JsonObject { ["owner_layer_id"] = owner, ["mechanism"] = "particle_system" }) } });
            },
            analyze: settings => {
                Assert.False(Directory.Exists(settings.OutputDirectory));
                Directory.CreateDirectory(settings.OutputDirectory);
                refreshes.Add(settings.OutputDirectory);
                JsonObject next = plan.DeepClone().AsObject();
                next["settings"] = PlanSettings.ToJson(settings);
                next["blockers"] = new JsonArray();
                return Task.FromResult(next);
            });
        JsonObject result = await service.BakeAsync(new(2, plan, trailingSeparator ? output + Path.DirectorySeparatorChar : output));
        Assert.Equal("candidate_generated", result["status"]!.GetValue<string>());
        Assert.Equal(3, bakes);
        Assert.Equal(2, refreshes.Distinct().Count());
        var owned = new WorkLayout(output, refreshes[0]);
        owned.AnalysisRefreshCreated();
        owned.RemoveIntermediates(keepCompositionProbe: false);
        Assert.False(Directory.Exists(refreshes[0]));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(sentinel, "user.txt")));
    });

    private static JsonObject Closure(bool closed) => new() { ["status"] = closed ? "closed" : LoopClosureCheck.NotClosedStatus };

    private static int[] Ids(int[]? ids) => [.. (ids ?? []).Order()];

    // 预检一轮的读数 → 下一次分析：没闭合的慢分量所有者、起点搜索证出第一层必拒的残差组里的粒子、证出超路数时点名的组留实时，
    // 各带专门的原因码（plan 已带的原因跟着走），读数记录上写这条让哪些层留实时、同分配单元连带了哪些层；都过了返回 null。
    [Fact]
    public void ProbeReadingsDecideTheNextAnalysis()
    {
        // 粒子 31 挂在全屏背景 30 下，两者同一分配单元（allocation_root 30）；33 同单元但原来就实时。组 106 的根带着 107。
        JsonObject Layer(int id, int unit) => new() { ["id"] = id, ["allocation_root"] = unit };
        var plan = new JsonObject { ["settings"] = new JsonObject { ["retain_live_root_ids"] = new JsonArray(9),
                ["retain_live_reasons"] = new JsonObject { ["9"] = new JsonArray("source_static") } },
            ["live_layer_ids"] = new JsonArray(33),
            ["layers"] = new JsonArray(Layer(11, 11), Layer(30, 30), Layer(31, 30), Layer(33, 30), Layer(106, 106), Layer(107, 106)),
            ["loop"] = new JsonObject { ["unresolved"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 31, ["mechanism"] = "particle_system" }) } };
        var request = new HybridAnalyzeRequest(2, "s", "a", "o");
        var round = new JsonArray(
            new JsonObject { ["kind"] = "slow_closure", ["group_id"] = "group-1", ["owner_layer_ids"] = new JsonArray(11, 9), ["loop_closure"] = Closure(false) },
            new JsonObject { ["kind"] = "slow_closure", ["group_id"] = "group-4", ["owner_layer_ids"] = new JsonArray(41), ["loop_closure"] = Closure(true) },
            new JsonObject { ["kind"] = "residual_start_search", ["group_id"] = "group-3", ["particle_layer_ids"] = new JsonArray(31),
                ["status"] = "rejected_residual_above_limits", ["sampled_global_rgb_mae_255"] = 3.5 },
            new JsonObject { ["kind"] = "residual_start_search", ["group_id"] = "group-5", ["particle_layer_ids"] = new JsonArray(51),
                ["status"] = "first_layer_checked_at_bake" },
            new JsonObject { ["kind"] = "video_streams", ["dynamic_groups"] = 5, ["retreat_root_ids"] = new JsonArray(106),
                ["retreat_layer_ids"] = new JsonArray(106, 107) });
        HybridAnalyzeRequest next = AnalysisOrchestrator.ProbeReplan(plan, round, request)!;
        Assert.Equal([9, 11, 31, 106], Ids(next.RetainLiveRootIds));
        Assert.Equal([AnalysisOrchestrator.SlowClosureNotClosed], next.RetainLiveReasons![11]);
        Assert.Equal(["particle_system", AnalysisOrchestrator.ResidualRetainReason], next.RetainLiveReasons[31]);
        Assert.Equal([AnalysisOrchestrator.StreamRetainReason], next.RetainLiveReasons[106]);
        Assert.Equal(["source_static"], next.RetainLiveReasons[9]);
        // 读数记录：9 原来就留实时，不算这条留的；背景 30 与 107 是同单元连带（原来就实时的 33 不算），没过的记录才写。
        int[] Read(int index, string key) => [.. round[index]![key]!.AsArray().Select(n => n!.GetValue<int>())];
        Assert.Equal([11], Read(0, "retained_live_layer_ids"));
        Assert.Equal([31], Read(2, "retained_live_layer_ids"));
        Assert.Equal([30], Read(2, "carried_layer_ids"));
        Assert.Equal([107], Read(4, "carried_layer_ids"));
        Assert.Null(round[1]!["retained_live_layer_ids"]);
        Assert.Null(round[3]!["retained_live_layer_ids"]);
        // 请求其余设置不变（不再改录制周期）。
        Assert.Null(next.FullLoopLayerIds);

        // 都过了，或要留的层都已留：不再分析。
        Assert.Null(AnalysisOrchestrator.ProbeReplan(plan, new JsonArray(round[1]!.DeepClone(), round[3]!.DeepClone()), request));
        JsonObject kept = plan.DeepClone().AsObject();
        kept["settings"]!["retain_live_root_ids"] = new JsonArray(9, 11);
        Assert.Null(AnalysisOrchestrator.ProbeReplan(kept, new JsonArray(round[0]!.DeepClone()), request));
    }

    private static async Task<JsonObject> Samples(string dir, string name, params byte[][] frames)
    {
        string path = Path.Combine(dir, name + ".rgb");
        await File.WriteAllBytesAsync(path, [.. frames.SelectMany(frame => frame)]);
        return new JsonObject { ["frame_samples"] = new JsonObject { ["path"] = path, ["width"] = 128, ["height"] = 64,
            ["count"] = (ulong)frames.Length, ["includes_packed_alpha"] = false } };
    }

    private static byte[] Frame(byte value, int tileValue = -1)
    {
        var rgb = Enumerable.Repeat(value, 128 * 64 * 3).ToArray();
        if (tileValue >= 0)
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    rgb[(y * 128 + x) * 3] = (byte)tileValue;
        return rgb;
    }

    // 残差只用预检本来就跑的起点搜索判：没有一个起点过整幅 Δ_0 限、且含粒子的组在选定起点上整幅超限，才证出烘焙第一层必拒；
    // 否则只记读数，瓦片量留给烘焙全分辨率第一层。不含粒子的残差组不出记录。
    [Fact]
    public void StartSearchReadingsProveOnlyWholeFrameResidualRejections()
    {
        var plan = new JsonObject { ["loop"] = new JsonObject { ["residual_masking"] = new JsonObject { ["status"] = "residual_maskable",
            ["residual_layers"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 31, ["mechanism"] = "particle_system" },
                new JsonObject { ["owner_layer_id"] = 41, ["mechanism"] = "sprite" }) } } };
        JsonObject[] groups = [new JsonObject { ["id"] = "g0", ["layer_ids"] = new JsonArray(30, 31) }, new JsonObject { ["id"] = "g1", ["layer_ids"] = new JsonArray(41) }];
        JsonObject Search(int within, double global) => new() { ["selected_start_frame"] = 16, ["candidates_within_global_limit"] = within,
            ["groups"] = new JsonArray(new JsonObject { ["group_id"] = "g0", ["at_shared_start"] = new JsonObject { ["sampled_global_rgb_mae_255"] = global } },
                new JsonObject { ["group_id"] = "g1", ["at_shared_start"] = new JsonObject { ["sampled_global_rgb_mae_255"] = 9.0 } }) };
        JsonObject[] Records(JsonObject search) => [.. SlowClosureProbe.ResidualRecords(plan, groups, [0, 1], search)];
        JsonObject[] over = Records(Search(0, 3.5));
        Assert.Equal("g0", Assert.Single(over)["group_id"]!.GetValue<string>());
        Assert.Equal("rejected_residual_above_limits", over[0]["status"]!.GetValue<string>());
        Assert.Equal([31], over[0]["particle_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal("first_layer_checked_at_bake", Records(Search(2, 3.5))[0]["status"]!.GetValue<string>());
        Assert.Equal("first_layer_checked_at_bake", Records(Search(0, 1.5))[0]["status"]!.GetValue<string>());
        // 单个残差组时搜索记录没有 groups，读 selected。
        JsonObject single = new() { ["group_id"] = "g0", ["candidates_within_global_limit"] = 0,
            ["selected"] = new JsonObject { ["sampled_global_rgb_mae_255"] = 4.0 } };
        Assert.Equal("rejected_residual_above_limits", Assert.Single(SlowClosureProbe.ResidualRecords(plan, groups, [0], single))["status"]!.GetValue<string>());
    }

    // 动态判定的样本：有一个和第一个不同就证出动态；全同只是没证出，不当静态。
    [Fact]
    public async Task DifferingSamplesProveADynamicGroup() => await TestTemp.Run(async dir =>
    {
        Assert.True(await SlowClosureProbe.SamplesDifferAsync(await Samples(dir, "moving", Frame(1), Frame(1), Frame(2)), CancellationToken.None));
        Assert.False(await SlowClosureProbe.SamplesDifferAsync(await Samples(dir, "still", Frame(1), Frame(1), Frame(1)), CancellationToken.None));
    });

    private static JsonObject StreamPlan(int groups, bool dynamic) => new()
    {
        ["route"] = "whole_layer", ["blockers"] = new JsonArray(),
        ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) },
        ["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule, ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 50.0 } },
        ["video_groups"] = new JsonArray([.. Enumerable.Range(0, groups).Select(i => (JsonNode)new JsonObject { ["id"] = $"group-{i}",
            ["root_ids"] = new JsonArray(100 + i), ["layer_ids"] = new JsonArray(100 + i), ["dynamic_verified"] = dynamic })])
    };

    // 路数只按证出的动态组拒：5 组都证出动态 → 超路数；5 组都没证出（可能有静态纹理）→ 不当拒因。
    [Fact]
    public void StreamLimitCountsOnlyProvenDynamicGroups()
    {
        Assert.Contains(NoBenefit.TooManyStreams, NoBenefit.AnalysisConditions(StreamPlan(5, dynamic: true)));
        Assert.DoesNotContain(NoBenefit.TooManyStreams, NoBenefit.AnalysisConditions(StreamPlan(5, dynamic: false)));
        Assert.DoesNotContain(NoBenefit.TooManyStreams, NoBenefit.AnalysisConditions(StreamPlan(4, dynamic: true)));
    }

    // 选的方案不因动态组判定而变（子分析里不渲），不装渲染器时编排结果与原先相同：没证出的组不当超路数的拒因。
    [Fact]
    public async Task UnprovenGroupsDoNotRejectWithoutARenderer() => await TestTemp.Run(async root =>
    {
        JsonObject Analyze(HybridAnalyzeRequest r)
        {
            JsonObject plan = StreamPlan(5, dynamic: false);
            plan["summary"] = new JsonObject { ["key"] = "summary.bakeable", ["zh"] = "", ["en"] = "" };
            plan["settings"] = new JsonObject { ["preset"] = r.Preset, ["interaction"] = r.Interaction };
            return plan;
        }
        JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", Path.Combine(root, "streams"), Interaction: "keep"),
            (r, _) => Task.FromResult(Analyze(r)), CancellationToken.None);
        Assert.True(Admission.Accepted(result));
        Assert.Null(result[NoBenefit.Field]);
        Assert.Null(result["video_stream_probe"]);
    });

    // 动态组判定是 main 没有的渲染，限时：预算用完（这里是 0）时不建采集工程、不起渲染器，记 budget_exhausted，没证出的组照旧没证出，
    // 不点名退回；已证出的动态组照旧算。
    [Fact]
    public async Task StreamProbeStopsAtItsTimeBudget() => await TestTemp.Run(async dir =>
    {
        var tools = new NativeTools("must-not-run", "must-not-run", "must-not-run", []);
        JsonObject plan = StreamPlan(5, dynamic: false);
        plan["source"] = Path.Combine(dir, "missing-source");
        Assert.Equal(NoBenefit.SavingProvenStreams, SlowClosureProbe.StreamLimit(plan));
        JsonObject record = Assert.Single(await SlowClosureProbe.RunAsync(plan, tools, Path.Combine(dir, "probe"), TimeSpan.Zero, CancellationToken.None))!.AsObject();
        Assert.Equal("video_streams", record["kind"]!.GetValue<string>());
        Assert.Equal("budget_exhausted", record["status"]!.GetValue<string>());
        Assert.Equal(5, record["unproven_group_ids"]!.AsArray().Count);
        Assert.Null(record["retreat_root_ids"]);
        Assert.Null(AnalysisOrchestrator.ProbeReplan(plan, new JsonArray(record.DeepClone()), new HybridAnalyzeRequest(2, "s", "a", "o")));
    });

    // 烘焙被接缝门或超路数拒了：结论就是结论，不再退回重新分析重烘（慢分量闭合、路数都已在分析的预检里判过）。
    // 残差第一层的拒绝不在此列（见 ResidualRejectionRetainsParticlesAndRebakes）。
    [Theory]
    [InlineData("candidate_rejected_seam", "encoded_seam_failed")]
    public async Task BakeRejectionsNoLongerReplan(string status, string validation) => await TestTemp.Run(async dir =>
    {
        string output = Path.Combine(dir, "bake");
        var plan = new JsonObject { ["settings"] = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "source", "assets", "analysis")),
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60, ["group_frames"] = new JsonObject { ["g0"] = 30 } }) },
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "g0", ["layer_ids"] = new JsonArray(7) }) };
        int baked = 0;
        var service = new HybridBakeService(new NativeTools("must-not-run", "must-not-run", "must-not-run", []),
            bakeOnce: _ =>
            {
                ++baked;
                Directory.CreateDirectory(output);
                // 每种拒绝都带着旧退回层会用的线索：残差组记录、按自身周期录的组、no_benefit 里的退回根。
                return Task.FromResult(new JsonObject { ["status"] = status, ["loop_validation"] = validation,
                    ["groups"] = new JsonArray(new JsonObject { ["id"] = "g0", ["source_layers"] = new JsonArray(7),
                        ["status"] = validation == "residual_above_limits" ? "rejected_seam_residual" : "rejected_seam" }),
                    [NoBenefit.Field] = new JsonObject { ["retreat_root_ids"] = new JsonArray(7) },
                    ["residual_masking"] = new JsonObject { ["residual_layers"] = new JsonArray(new JsonObject {
                        ["owner_layer_id"] = 7, ["mechanism"] = "particle_system" }) } });
            },
            analyze: _ => throw new InvalidOperationException("must not replan"));
        JsonObject result = await service.BakeAsync(new HybridBakeRequest(2, plan, output));
        Assert.Equal(1, baked);
        Assert.Equal(status, result["status"]!.GetValue<string>());
    });

    [Fact]
    public async Task ActualStreamOverflowReplansWithCheapestGroupLive() => await TestTemp.Run(async dir =>
    {
        string output = Path.Combine(dir, "bake"), evidence = Path.Combine(dir, "runtime.json");
        await File.WriteAllTextAsync(evidence, "{\"runtime_layers\":[]}");
        var plan = new JsonObject {
            ["settings"] = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "source", "assets", "analysis")),
            ["runtime_evidence"] = evidence,
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "g0", ["root_ids"] = new JsonArray(7), ["layer_ids"] = new JsonArray(7) })
        };
        int baked = 0; HybridAnalyzeRequest? replanned = null;
        var service = new HybridBakeService(new NativeTools("must-not-run", "must-not-run", "must-not-run", []),
            bakeOnce: _ => {
                Directory.CreateDirectory(output);
                return Task.FromResult(++baked == 1
                    ? new JsonObject { ["status"] = NoBenefit.RejectedBakeStatus, ["no_benefit"] = new JsonObject { ["video_streams_encoded"] = 5, [NoBenefit.RetreatRootsField] = new JsonArray(7) },
                        ["groups"] = new JsonArray(new JsonObject { ["id"] = "g0", ["storage"] = "video" }) }
                    : new JsonObject { ["status"] = "candidate_generated" });
            },
            analyze: settings => { replanned = settings; return Task.FromResult(new JsonObject { ["settings"] = PlanSettings.ToJson(settings), ["blockers"] = new JsonArray() }); });
        JsonObject result = await service.BakeAsync(new HybridBakeRequest(2, plan, output));
        Assert.Equal(2, baked);
        Assert.Equal("candidate_generated", result["status"]!.GetValue<string>());
        Assert.Contains(7, replanned!.RetainLiveRootIds!);
    });

    // 残差组在烘焙第一层被拒、组里有被掩盖的粒子：粒子留实时重新分析再烘（分析的残差预检只在慢分量预检本来就跑起点搜索时判得了，
    // 其余作品只有第一层量得到）。留实时的粒子写 residual_seam_over_limit，并上原来的未解析原因；只重烘一次就生成。
    [Theory]
    [InlineData("full_frame")]
    [InlineData("layered")]
    public async Task ResidualRejectionRetainsParticlesAndRebakes(string layout) => await TestTemp.Run(async dir =>
    {
        string output = Path.Combine(dir, "bake");
        var plan = new JsonObject { ["settings"] = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "source", "assets", "analysis", RetainLiveRootIds: [3],
                VideoLayout: layout, LiveOverlayPlacement: "preserve", CustomSettings: true, LayoutExplicit: true)),
            ["loop"] = new JsonObject { ["unresolved"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 7, ["mechanism"] = "particle_system" }) } };
        int baked = 0;
        HybridAnalyzeRequest? replanned = null;
        var service = new HybridBakeService(new NativeTools("must-not-run", "must-not-run", "must-not-run", []),
            bakeOnce: _ =>
            {
                Directory.CreateDirectory(output);
                return Task.FromResult(++baked > 1 ? new JsonObject { ["status"] = "candidate_generated" } : new JsonObject {
                    ["status"] = "candidate_rejected_seam", ["loop_validation"] = "residual_above_limits",
                    ["groups"] = new JsonArray(new JsonObject { ["id"] = "g0", ["source_layers"] = new JsonArray(7, 8), ["status"] = "rejected_seam_residual" }),
                    ["residual_masking"] = new JsonObject { ["residual_layers"] = new JsonArray(new JsonObject {
                        ["owner_layer_id"] = 7, ["mechanism"] = "particle_system" }) } });
            },
            analyze: settings =>
            {
                replanned = settings;
                return Task.FromResult(new JsonObject { ["settings"] = PlanSettings.ToJson(settings), ["blockers"] = new JsonArray() });
            });
        JsonObject result = await service.BakeAsync(new HybridBakeRequest(2, plan, output));
        Assert.Equal(2, baked);
        Assert.Equal("candidate_generated", result["status"]!.GetValue<string>());
        Assert.Equal([3, 7], replanned!.RetainLiveRootIds!);
        Assert.Equal(["particle_system", AnalysisOrchestrator.ResidualRetainReason], replanned.RetainLiveReasons![7]);
        Assert.True(replanned.CustomSettings);
        Assert.True(replanned.LayoutExplicit);
        Assert.Equal("preserve", replanned.LiveOverlayPlacement);
        Assert.Equal(layout, replanned.VideoLayout);
        Assert.Equal("g0", result["residual_particle_retry"]!["first_rejected_groups"]![0]!.GetValue<string>());
    });

    [Fact]
    public void SavedPlanRestoresExplicitOverlayChoiceFromExistingMetadata()
    {
        var settings = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "source", "assets", "analysis", LiveOverlayPlacement: "preserve")).AsObject();
        Assert.False(PlanSettings.Of(new JsonObject { ["settings"] = settings.DeepClone() }).CustomSettings);
        var saved = new JsonObject { ["settings"] = settings, ["custom_settings"] = true };
        Assert.True(PlanSettings.Of(saved).CustomSettings);
        Assert.Equal("preserve", PlanSettings.Of(saved).LiveOverlayPlacement);
    }

    // 自动重试（入场切换没过合成门）重新分析期间，盘上的 bake.json 是"还在跑"，不是首次的拒绝结论；首次产物挪走时带回首次的结论。
    [Fact]
    public async Task RetryMarksTheReportInProgressWhileReplanning() => await TestTemp.Run(async dir =>
    {
        string output = Path.Combine(dir, "bake");
        var plan = new JsonObject { ["settings"] = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "source", "assets", "analysis")) };
        var first = new JsonObject { ["status"] = "candidate_rejected_composition", ["reason"] = "differs",
            ["composition_validation"] = new JsonObject { ["status"] = "composition_fail", ["intro_live"] = new JsonObject { ["status"] = "applied" } } };
        int baked = 0;
        JsonObject? onDisk = null;
        var service = new HybridBakeService(new NativeTools("must-not-run", "must-not-run", "must-not-run", []),
            bakeOnce: _ =>
            {
                Directory.CreateDirectory(output);
                return Task.FromResult(++baked == 1 ? first.DeepClone().AsObject() : new JsonObject { ["status"] = "candidate_generated" });
            },
            analyze: async _ =>
            {
                onDisk = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "bake.json")))!.AsObject();
                return new JsonObject { ["settings"] = plan["settings"]!.DeepClone(), ["blockers"] = new JsonArray() };
            });
        JsonObject result = await service.BakeAsync(new HybridBakeRequest(2, plan, output));
        Assert.Equal(HybridBakeService.InProgressStatus, onDisk!["status"]!.GetValue<string>());
        Assert.Equal("intro_fallback", onDisk["in_progress_stage"]!.GetValue<string>());
        Assert.Null(onDisk["reason"]);
        Assert.Equal("candidate_generated", result["status"]!.GetValue<string>());
        string moved = result["intro_fallback"]!["first_attempt_directory"]!.GetValue<string>();
        Assert.Equal("candidate_rejected_composition",
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(moved, "bake.json")))!["status"]!.GetValue<string>());
    });

    // 全静态、没有任何实时层的成品按事实说：没有实时渲染；带实时层的静态成品不算。
    [Fact]
    public void StaticOnlyWithoutLiveLayersIsNamedAsSuch()
    {
        JsonObject Report(params int[] live) => new() { ["status"] = StaticOnlyBake.Status,
            ["plan"] = new JsonObject { ["live_layer_ids"] = new JsonArray([.. live.Select(id => (JsonNode)id)]) } };
        Assert.True(StaticOnlyBake.WithoutLiveLayers(Report()));
        Assert.False(StaticOnlyBake.WithoutLiveLayers(Report(3)));
        Assert.False(StaticOnlyBake.WithoutLiveLayers(new JsonObject { ["status"] = "candidate_generated", ["plan"] = new JsonObject() }));
    }
}
