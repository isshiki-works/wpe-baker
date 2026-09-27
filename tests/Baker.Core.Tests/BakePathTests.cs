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

    // 慢项改速实测的读数（伪造渲染器出平滑纹理，改速后的版本按速度差 dv 横移 dv·t 像素）：0.05 px/s 放行、0.5 px/s 看得出；
    // 两版第 0 帧不逐位相同（所有者层里有随机内容）时不出读数，哪怕第 t 帧两版完全一样；没有纹理时同样不出读数。
    [Fact]
    public async Task SlowSpeedProbeReadsSpeedOnlyFromIdenticalOrigins()
    {
        const int size = 256;
        static byte[] Frame(double shift, bool flat = false, bool speck = false)
        {
            var rgba = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double v = flat ? 128 : 128 + 60 * Math.Sin((x - shift) / 6.0) + 60 * Math.Cos(y / 7.0);
                    int i = (y * size + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = (byte)Math.Round(v);
                    rgba[i + 3] = 255;
                }
            if (speck) rgba[0] ^= 1;
            return rgba;
        }
        static Task<JsonObject> Read(double deviation, bool randomOrigin = false, bool flat = false) => SlowClosureProbe.MeasureAsync(
            (after, frame) => Task.FromResult(Frame(after ? deviation * frame / 30.0 : 0, flat, randomOrigin && after && frame == 0)), size, size, 1, 30, 1);
        JsonObject slow = await Read(0.05), fast = await Read(0.5);
        Assert.Equal("passed", slow["status"]!.GetValue<string>());
        Assert.InRange(slow["peak_speed_deviation_px_per_second"]!.GetValue<double>(), 0.03, 0.07);
        Assert.Equal("visible", fast["status"]!.GetValue<string>());
        Assert.Equal("frame0_differs", (await Read(0, randomOrigin: true))["reason"]!.GetValue<string>());
        Assert.Equal("no_texture", (await Read(0.5, flat: true))["reason"]!.GetValue<string>());
    }
}
