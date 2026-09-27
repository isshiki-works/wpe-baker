using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 烘焙侧此前没有测试执行到的两处：分段渲染的段目录、分析阶段慢分量闭合预检的分流。都不需要 GPU 与 WPE 素材。

[Trait("Layer", "L1")]
public class BakePathTests
{
    private static readonly string Fixture = Path.Combine(LocalTools.RepositoryRoot, "tests", "fixtures", "native", "shader-clock");

    // 段目录必须建在组目录里且各段不同：回退时调用方整个挪走组目录，段目录跟着走，重渲不撞上一次的段目录。
    // 假渲染器一开渲染就失败；每段的 manifest.json 在启动渲染器之前就写好了，失败后照样在。
    [Fact]
    public async Task SegmentDirectoriesStayInsideGroupOutput() => await TestTemp.Run(async dir =>
    {
        string output = Path.Combine(dir, "group");
        var request = new RenderRequest(Fixture, Fixture, output, 64, 48, 60, 1, 6000, GpuEncoding: new());
        await Assert.ThrowsAnyAsync<Exception>(() => new NativeRenderRunner(RenderJobContractTests.FakeTools(dir))
            .RenderSegmentsAsync(request, 2, new SemaphoreSlim(2), null, CancellationToken.None));
        Assert.All(new[] { "part0", "part1" }, part => Assert.True(File.Exists(Path.Combine(output, part, "manifest.json")), part));
    });

    // 闭合预检只对含慢分量所有者层的组动手：慢分量所有者不在任何组里时不碰源与渲染器，在组里时才开始准备渲染。
    [Fact]
    public async Task SlowClosureProbeOnlyTouchesGroupsOwningSlowComponents() => await TestTemp.Run(async dir =>
    {
        var tools = new NativeTools("must-not-run", "must-not-run", "must-not-run", []);
        JsonObject Plan(int owner) => new()
        {
            ["source"] = Path.Combine(dir, "missing-source"),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1) }),
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60,
                ["slow_components"] = new JsonArray(new JsonObject { ["owner_layer_id"] = owner, ["drift_bound_radians"] = 0.01 }) }) },
        };
        Assert.Empty(await SlowClosureProbe.RunAsync(Plan(2), tools, Path.Combine(dir, "other"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => SlowClosureProbe.RunAsync(Plan(1), tools, Path.Combine(dir, "owned"), CancellationToken.None));
    });

    // 接缝门因慢分量漂移拒了某组、点名层 7：退回让层 7 留实时，重新分析，用新计划再烘一次，报告记成 retried。
    [Fact]
    public async Task SlowComponentRejectionRetreatsReplansAndRebakes() => await TestTemp.Run(async dir =>
    {
        string output = Path.Combine(dir, "bake");
        var firstPlan = new JsonObject { ["settings"] = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "source", "assets", "analysis")) };
        var newPlan = new JsonObject { ["settings"] = firstPlan["settings"]!.DeepClone(), ["blockers"] = new JsonArray() };
        HybridAnalyzeRequest? replanned = null;
        var baked = new List<JsonObject>();
        var service = new HybridBakeService(new NativeTools("must-not-run", "must-not-run", "must-not-run", []),
            bakeOnce: request =>
            {
                baked.Add(request.Plan);
                Directory.CreateDirectory(output);
                return Task.FromResult(baked.Count == 1
                    ? new JsonObject { ["status"] = "candidate_rejected_seam",
                        [NoBenefit.Field] = new JsonObject { [NoBenefit.RetreatRootsField] = new JsonArray(7) } }
                    : new JsonObject { ["status"] = "candidate_generated" });
            },
            analyze: settings => { replanned = settings; return Task.FromResult(newPlan); });

        JsonObject result = await service.BakeAsync(new HybridBakeRequest(2, firstPlan, output));

        Assert.Contains(7, replanned!.RetainLiveRootIds!);
        Assert.Equal(2, baked.Count);
        Assert.Same(newPlan, baked[1]);
        Assert.Equal("retried", result["slow_component_retreat"]?["status"]?.GetValue<string>());
    });
}
