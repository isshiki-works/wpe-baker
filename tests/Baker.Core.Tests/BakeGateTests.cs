using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 烘焙前闸门链：每道闸门的放行与拒绝、链的顺序与"第一道拒绝即停"、报告骨架的逐字节形状、发布与否。
/// 需要渲染的两道（合成校验、内嵌视频外推）通过注入的校验/外推结果测判定本身。
/// </summary>
[Trait("Layer", "L0")]
public class BakeGateTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("periodica-gates-").FullName;
    private readonly ProjectSource source;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BakeGateTests()
    {
        string project = Path.Combine(root, "source");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "project.json"), """{"type":"scene","file":"scene.json","title":"t"}""");
        File.WriteAllText(Path.Combine(project, "scene.json"), """{"objects":[]}""");
        source = new ProjectSource(project);
    }

    public void Dispose()
    {
        source.Dispose();
        Directory.Delete(root, true);
    }

    private static JsonObject Plan(ulong frames) => new()
    {
        ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 },
        ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["transparent"] = false }),
        ["layers"] = new JsonArray(new JsonObject { ["id"] = 5 }),
        ["loop"] = new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject { ["frames"] = frames }),
            ["unresolved"] = new JsonArray()
        }
    };

    private BakeGateContext Context(JsonObject plan, uint fpsNumerator = 60) =>
        new(new HybridBakeRequest(2, plan, Path.Combine(root, "out"), EffectRenderScale: 0.5), source, "hash",
            new WorkLayout(Path.Combine(root, "out")), null)
        {
            Plan = plan,
            Settings = new HybridAnalyzeRequest(1, source.SourcePath, "", Path.Combine(root, "analysis"), 1920, 1080, fpsNumerator, 1)
        };

    private static JsonObject Residual(ulong period) => new()
    {
        ["status"] = "maskable",
        ["residual_layers"] = new JsonArray(new JsonObject
        {
            ["owner_layer_id"] = 5,
            ["cyclostationary_lock"] = new JsonObject { ["period_frames"] = period, ["fps_num"] = 60, ["fps_den"] = 1 }
        })
    };

    [Fact]
    public void EveryGateImplementationIsOnThePreflightChainInOrder()
    {
        IBakeGate[] chain = [.. BakeGates.Preflight(new("r", "f", "p", [])), .. BakeGates.Validation((_, _) => Task.FromResult(new JsonObject()),
            (_, _, _, _) => Task.FromResult(new JsonObject()))];
        Type[] implementations = typeof(IBakeGate).Assembly.GetTypes()
            .Where(type => typeof(IBakeGate).IsAssignableFrom(type) && !type.IsInterface).ToArray();
        Assert.Equal(implementations.OrderBy(type => type.Name), chain.Select(gate => gate.GetType()).OrderBy(type => type.Name));
        Assert.Equal(new[] { typeof(ScriptEvidenceGate), typeof(LoopAdmissionGate), typeof(LoopFramesGate), typeof(DiskBudgetGate),
            typeof(ParticleCycleGate), typeof(CompositionGate), typeof(EmbeddedVideoGate) }, chain.Select(gate => gate.GetType()));
    }

    private sealed class Recording(List<string> log, string name, BakeRejection? result) : IBakeGate
    {
        public Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
        {
            log.Add(name);
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task ChainStopsAtTheFirstRejection()
    {
        BakeGateContext context = Context(Plan(600));
        var log = new List<string>();
        BakeRejection stop = context.NoLoop(null);
        BakeRejection? result = await BakeGates.FirstRejectionAsync(
            [new Recording(log, "a", null), new Recording(log, "b", stop), new Recording(log, "c", null)], context, Ct);
        Assert.Same(stop, result);
        Assert.Equal(new[] { "a", "b" }, log);
        log.Clear();
        Assert.Null(await BakeGates.FirstRejectionAsync([new Recording(log, "a", null), new Recording(log, "c", null)], context, Ct));
        Assert.Equal(new[] { "a", "c" }, log);
    }

    [Fact]
    public async Task LoopFramesGateTakesTheFirstCandidateAndRejectsAnEmptyLoop()
    {
        BakeGateContext passing = Context(Plan(600));
        Assert.Null(await new LoopFramesGate().CheckAsync(passing, Ct));
        Assert.Equal(600UL, passing.Frames);

        BakeGateContext empty = Context(Plan(0));
        JsonObject rejected = (await new LoopFramesGate().CheckAsync(empty, Ct))!.ToJson();
        Assert.Equal("candidate_rejected_no_loop", rejected["status"]!.GetValue<string>());
        Assert.Equal("no_suitable_loop", rejected["loop_validation"]!.GetValue<string>());
        Assert.Equal("bake.no_loop_candidate", rejected["reason_localized"]!["key"]!.GetValue<string>());
        Assert.NotSame(empty.Plan, rejected["plan"]);
    }

    [Fact]
    public async Task DiskBudgetGateRejectsOnlyWhenThePeakCannotFit()
    {
        BakeGateContext fits = Context(Plan(600));
        fits.Frames = 600;
        Assert.Null(await new DiskBudgetGate().CheckAsync(fits, Ct));

        JsonObject huge = Plan(50_000_000);
        BakeGateContext full = Context(huge);
        full.Frames = 50_000_000;
        JsonObject rejected = (await new DiskBudgetGate().CheckAsync(full, Ct))!.ToJson();
        Assert.Equal(BakeDiskBudget.RejectedBakeStatus, rejected["status"]!.GetValue<string>());
        Assert.Equal("not_performed", rejected["loop_validation"]!.GetValue<string>());
        Assert.NotNull(rejected["disk_budget"]!["required_bytes"]);
        Assert.Same(huge, rejected["plan"]);
    }

    [Fact]
    public async Task ParticleCycleGateRejectsALoopThatIsNotAWholeNumberOfLockedCycles()
    {
        BakeGateContext sourcePeriod = Context(Plan(600));
        sourcePeriod.Frames = 600;
        Assert.Null(await new ParticleCycleGate().CheckAsync(sourcePeriod, Ct));

        BakeGateContext inPhase = Context(Plan(600));
        inPhase.Frames = 600;
        inPhase.ResidualMasking = Residual(200);
        Assert.Null(await new ParticleCycleGate().CheckAsync(inPhase, Ct));
        Assert.Equal("maskable", inPhase.ResidualMasking["status"]!.GetValue<string>());

        BakeGateContext outOfPhase = Context(Plan(600));
        outOfPhase.Frames = 600;
        outOfPhase.ResidualMasking = Residual(250);
        JsonObject rejected = (await new ParticleCycleGate().CheckAsync(outOfPhase, Ct))!.ToJson();
        Assert.Equal("candidate_rejected_no_loop", rejected["status"]!.GetValue<string>());
        Assert.Equal("rejected_particle_cycle", rejected["residual_masking"]!["status"]!.GetValue<string>());
        Assert.Equal("rejected_particle_cycle", outOfPhase.Plan["loop"]!["residual_masking"]!["status"]!.GetValue<string>());
        Assert.Equal(250UL, rejected["residual_masking"]!["particle_cycle_mismatch"]!["period_frames"]!.GetValue<ulong>());
    }

    [Fact]
    public async Task CompositionGatePassesKeepsTheValidationAndRejectsWithProbePaths()
    {
        BakeGateContext passing = Context(Plan(600));
        var pass = new JsonObject { ["status"] = "composition_pass" };
        Assert.Null(await new CompositionGate((_, _) => Task.FromResult(pass)).CheckAsync(passing, Ct));
        Assert.Same(pass, passing.CompositionValidation);

        JsonObject failed = (await new CompositionGate((_, _) => Task.FromResult(new JsonObject {
            ["status"] = "composition_failed", ["reason"] = "r", ["probe_output_path"] = "p", ["comparison_reference_path"] = "c" }))
            .CheckAsync(Context(Plan(600)), Ct))!.ToJson();
        Assert.Equal("candidate_rejected_composition", failed["status"]!.GetValue<string>());
        Assert.Equal("p", failed["probe_paths"]!["output"]!.GetValue<string>());
        Assert.Equal("c", failed["probe_paths"]!["reference"]!.GetValue<string>());
        Assert.False(failed.ContainsKey("reason_zh"));

        JsonObject scriptErrors = (await new CompositionGate((_, _) => Task.FromResult(new JsonObject {
            ["status"] = CandidateScriptErrorGate.RejectedCompositionStatus, ["reason_zh"] = "中", ["reason_en"] = "en" }))
            .CheckAsync(Context(Plan(600)), Ct))!.ToJson();
        Assert.Equal(CandidateScriptErrorGate.RejectedBakeStatus, scriptErrors["status"]!.GetValue<string>());
        Assert.Equal("中", scriptErrors["reason_zh"]!.GetValue<string>());
        Assert.True(WorkLayout.KeepsCompositionProbe(scriptErrors) && WorkLayout.KeepsCompositionProbe(failed));
    }

    [Fact]
    public async Task EmbeddedVideoGateSkipsWithoutAProbeAndRecordsTheEstimateWithoutRejecting()
    {
        int calls = 0;
        EmbeddedVideoGate Gate(string status) => new((_, frames, _, _) =>
        {
            calls++;
            return Task.FromResult(new JsonObject { ["status"] = status, ["loop_frames"] = frames });
        });
        BakeGateContext noProbe = Context(Plan(600));
        Assert.Null(await Gate("predicted_over_limit").CheckAsync(noProbe, Ct));
        Assert.Equal(0, calls);
        Assert.Null(noProbe.EmbeddedVideoEstimate);

        BakeGateContext under = Context(Plan(600));
        under.CompositionValidation = new JsonObject { ["status"] = "composition_pass" };
        under.Frames = 600;
        Assert.Null(await Gate("predicted_within_limit").CheckAsync(under, Ct));
        Assert.Equal(600UL, under.EmbeddedVideoEstimate!["loop_frames"]!.GetValue<ulong>());

        BakeGateContext over = Context(Plan(600));
        over.CompositionValidation = new JsonObject { ["status"] = "composition_pass" };
        Assert.Null(await Gate("predicted_over_limit").CheckAsync(over, Ct));
        Assert.Equal("predicted_over_limit", over.EmbeddedVideoEstimate!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScriptEvidenceGateRequiresEveryFaultOnAPlannedLayer()
    {
        JsonObject Evidence(int owner)
        {
            JsonObject plan = Plan(600);
            plan["source_script_error_evidence"] = new JsonObject { ["status"] = "available" };
            plan["source_script_error_count"] = 1;
            plan["source_script_errors"] = new JsonArray(new JsonObject { ["owner_layer_id"] = owner });
            return plan;
        }
        // tools 指向不存在的程序：放行与拒绝都不能走到重新分析。
        var gate = new ScriptEvidenceGate(new("not-started", "not-started", "not-started", []));
        Assert.Null(await gate.CheckAsync(Context(Evidence(5)), Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => gate.CheckAsync(Context(Evidence(6)), Ct));
    }

    [Fact]
    public void RunningSkeletonsKeepTheirV2KeyOrder()
    {
        JsonObject probe = BakeReportWriter.Running(new HybridBakeRequest(2, new JsonObject(), "o", ProbeFrames: 48), "abc", 48, new JsonObject(), 1, "x");
        Assert.Equal("hybrid_video_probe", probe["artifact_kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task PublisherCopiesOnlyFinishedProjectsAndWritesBothReports()
    {
        string output = Path.Combine(root, "work");
        var layout = new WorkLayout(output);
        string project = Path.Combine(output, "project");
        await source.ExtractAsync(project, Ct);
        foreach (string status in new[] { "candidate_rejected_seam", "failed" })
        {
            string destination = Path.Combine(root, "published-" + status);
            await ProjectPublisher.PublishAsync(new JsonObject { ["status"] = status }, project, destination, layout, new StageTiming(), null, Ct);
            Assert.False(Directory.Exists(destination));
        }
        foreach (string status in new[] { "candidate_generated", StaticOnlyBake.Status })
        {
            string destination = Path.Combine(root, "published-" + status);
            var report = new JsonObject { ["status"] = status, ["project_path"] = project };
            await ProjectPublisher.PublishAsync(report, project, destination, layout, new StageTiming(), null, Ct);
            Assert.True(File.Exists(Path.Combine(destination, "scene.json")));
            Assert.Equal(destination, report["project_path"]!.GetValue<string>());
            Assert.Equal(layout.Output, report["work_directory"]!.GetValue<string>());
            Assert.Equal(destination, JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "bake.json")))!["project_path"]!.GetValue<string>());
            Assert.Equal(destination, JsonNode.Parse(File.ReadAllText(layout.Report))!["project_path"]!.GetValue<string>());
            File.Delete(layout.Report);
        }
        // 目标必须全新、不能与源或工作目录互相包含。
        Assert.Throws<IOException>(() => ProjectPublisher.Destination(Path.Combine(output, "inside"), source, output));
        Assert.Throws<IOException>(() => ProjectPublisher.Destination(root, source, output));
        Assert.Null(ProjectPublisher.Destination(null, source, output));
        Assert.Equal(Path.Combine(root, "fresh"), ProjectPublisher.Destination(Path.Combine(root, "fresh") + Path.DirectorySeparatorChar, source, output));
    }

    /// <summary>缓变分量：接缝门在 P 处的结果就是结论。闭合没过 → 理由换成缓变分量漂移，附漂移上界与接缝读数；没有缓变分量照旧。</summary>
    [Fact]
    public void SlowComponentSeamFailureIsTheVerdict()
    {
        JsonObject Plan(params double[] bounds) => new() { ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject {
            ["frames"] = 600, ["slow_components"] = new JsonArray([.. bounds.Select(bound => (JsonNode)new JsonObject { ["drift_bound_radians"] = bound })]) }) } };
        Assert.Equal("10", GroupVerdicts.SlowDriftDegrees(Plan(Math.PI / 36, Math.PI / 18)));
        Assert.Null(GroupVerdicts.SlowDriftDegrees(Plan()));
        var seam = new JsonObject { ["status"] = "observed_seam_fail", ["failures"] = new JsonArray("loop_not_closed"),
            ["loop_closure"] = new JsonObject { ["status"] = LoopClosureCheck.NotClosedStatus, ["loop_frames"] = 600, ["tile_size"] = 64,
                ["rgb"] = new JsonObject { ["tile_64"] = new JsonObject { ["worst"] = 9.5, ["worst_x"] = 3, ["worst_y"] = 4 } } } };
        JsonObject Reject(string? drift)
        {
            var report = new JsonObject { ["groups"] = new JsonArray() };
            GroupVerdicts.RejectSeam(report, "g0", [1], false, new JsonObject { ["crop"] = new JsonObject() }, "v.mp4", null, seam.DeepClone().AsObject(), null, drift);
            return report;
        }
        JsonObject slow = Reject("10"), plain = Reject(null);
        Assert.Equal("candidate_rejected_seam", slow["status"]!.GetValue<string>());
        Assert.Equal("reason.slow_component_drift_exceeds_seam", slow["reason_localized"]!["key"]!.GetValue<string>());
        string zh = slow["reason_localized"]!["zh"]!.GetValue<string>();
        Assert.Contains("10°", zh);
        Assert.Contains(EncodedLoopValidator.RejectionDetail(seam, MessageCatalog.Chinese), zh);
        Assert.Equal("bake.encoded_seam_rejected", plain["reason_localized"]!["key"]!.GetValue<string>());
    }
}
