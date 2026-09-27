using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 烘焙侧此前没有测试执行到的几处：分段渲染的段目录、分析阶段慢分量闭合预检的分流（视频组与特效前缀）。都不需要 GPU 与 WPE 素材。

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

    // 前缀缓存的闭合预检：没有缓变分量不渲染（null）；有缓变分量时渲染失败记 probe_failed，不是 closed，规划侧按没闭合退一级前缀。
    [Fact]
    public async Task PrefixSlowClosureProbeOnlyClosesOnARenderedVerdict() => await TestTemp.Run(async dir =>
    {
        var tools = new NativeTools("must-not-run", "must-not-run", "must-not-run", []);
        using var source = new ProjectSource(Fixture);
        var settings = new HybridAnalyzeRequest(2, Fixture, Path.Combine(dir, "missing-assets"), dir, 64, 48, 60, 1);
        JsonObject Cache(JsonArray slow) => new()
        {
            ["owner_layer_id"] = 1, ["prefix_effect_count"] = 1, ["terminal_effect_id"] = 2,
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60UL, ["components"] = new JsonArray(),
                ["patches"] = new JsonArray(), ["slow_components"] = slow }) },
        };
        Assert.Null(await SlowClosureProbe.PrefixAsync(Cache([]), tools, settings, source, new JsonObject(), Path.Combine(dir, "plain"), CancellationToken.None));
        JsonObject? record = await SlowClosureProbe.PrefixAsync(Cache([new JsonObject { ["owner_layer_id"] = 1, ["drift_bound_radians"] = 0.01 }]),
            tools, settings, source, new JsonObject(), Path.Combine(dir, "slow"), CancellationToken.None);
        Assert.Equal("probe_failed", record!["status"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(dir, "slow")));
    });
}
