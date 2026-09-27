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

    // 慢项改速实测的读数（伪造渲染器出平滑纹理）。位移：改速后的版本按速度差 dv 横移 dv·t 像素，0.05 px/s 放行、0.5 px/s 看得出。
    // 亮度：画面左半是静止纹理，右半是平坦的光晕按 pulse 起伏（周期 400 s 改到 101 s，2997331973 的实况），不动一个像素也必须看得出——
    // 分块光流只量位移（平坦块没有纹理、纹理块没变化），这种改速曾读出 0.019 px/s 放行。
    // 窗口从 0.5 s 翻倍到循环长度，取第一个最差块差 ≥ 3 levels 的窗算读数。两版第 0 帧不逐位相同、第 0 帧没有梯度时不出读数。
    [Fact]
    public async Task SlowSpeedProbeCatchesDisplacementAndBrightness()
    {
        const int size = 256;
        static byte[] Frame(double shift, double glow = 0, bool flat = false, bool halfFlat = false, bool speck = false)
        {
            var rgba = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool smooth = flat || halfFlat && x >= size / 2;
                    double v = smooth ? 128 + glow : 128 + 50 * Math.Sin((x - shift) / 6.0) + 50 * Math.Cos(y / 7.0);
                    int i = (y * size + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = (byte)Math.Round(v);
                    rgba[i + 3] = 255;
                }
            if (speck) rgba[0] ^= 1;
            return rgba;
        }
        static Task<JsonObject> Read(Func<bool, double, byte[]> frame) => SlowClosureProbe.MeasureAsync(
            (after, index) => Task.FromResult(frame(after, index / 30.0)), size, size, 1, 30, 1, 400 * 30);
        static Task<JsonObject> Shift(double deviation) => Read((after, t) => Frame(after ? deviation * t : 0));
        JsonObject slow = await Shift(0.05), fast = await Shift(0.5);
        Assert.Equal("passed", slow["status"]!.GetValue<string>());
        Assert.InRange(slow["peak_speed_deviation_px_per_second"]!.GetValue<double>(), 0.005, 0.07);
        Assert.Equal("visible", fast["status"]!.GetValue<string>());
        JsonObject pulse = await Read((after, t) => Frame(0, 20 * Math.Sin(2 * Math.PI * t / (after ? 101 : 400)), halfFlat: true));
        Assert.Equal("visible", pulse["status"]!.GetValue<string>());
        Assert.True(pulse["flat_block_changed"]!.GetValue<bool>());
        // 慢亮度漂移（3715870843 的 caustics 形态）：平坦光晕 ±5 levels、周期 400 s 改到 300 s。8 s 内两版量化后逐位相同（旧的 0.5–8 s 窗读 0 放行），
        // 翻倍到第一个最差块差 ≥ 3 levels 的窗才算读数，平坦块里的变化判看得出
        JsonObject drift = await Read((after, t) => Frame(0, 5 * Math.Sin(2 * Math.PI * t / (after ? 300 : 400)), halfFlat: true));
        Assert.Equal("visible", drift["status"]!.GetValue<string>());
        Assert.True(drift["worst_window_seconds"]!.GetValue<double>() > 8);
        // 整个循环（400 s）里两版的差都在量化阈值以下，原速版本自己也不可测地变化：这一层看不见或不动，放行并写明依据
        JsonObject faint = await Read((after, t) => Frame(0, 0.4 * Math.Sin(2 * Math.PI * t / (after ? 300 : 400)), halfFlat: true));
        Assert.Equal("passed", faint["status"]!.GetValue<string>());
        Assert.Equal("component_not_visible", faint["basis"]!.GetValue<string>());
        Assert.Equal(400.0, faint["worst_window_seconds"]!.GetValue<double>());
        // 补丁没生效（3715870843 的疑点）：原速版本明显在动，改速后的版本却与它逐位相同，整个循环两版都不差——不放行
        JsonObject inert = await Read((_, t) => Frame(0, 20 * Math.Sin(2 * Math.PI * t / 400), halfFlat: true));
        Assert.Equal("not_measured", inert["status"]!.GetValue<string>());
        Assert.Equal("patch_not_effective", inert["reason"]!.GetValue<string>());
        Assert.Equal("frame0_differs", (await Read((after, t) => Frame(0, speck: after && t == 0)))["reason"]!.GetValue<string>());
        Assert.Equal("no_texture", (await Read((after, t) => Frame(0, after ? t : 0, flat: true)))["reason"]!.GetValue<string>());
    }

    // 测速请求不带烘焙专用设置：组本来是残差组（淡化窗口 + CPU 直编裁剪）时，从主渲染请求派生的 1 帧请求过不了渲染器的请求校验
    // （本机全集 3582367840 等 3 张分析因此崩溃）。原样渲染的请求要过完全部参数校验，停在"渲染器文件不存在"上。
    [Fact]
    public async Task SpeedProbeRequestDropsCropAndCrossfadeOfAResidualGroup() => await TestTemp.Run(async dir =>
    {
        var tools = new NativeTools(Path.Combine(dir, "no-renderer"), Path.Combine(dir, "no-ffmpeg"), Path.Combine(dir, "no-ffprobe"), []);
        var runner = new NativeRenderRunner(tools);
        var plan = new JsonObject
        {
            ["projection"] = new JsonObject { ["center_x"] = 0.0, ["center_y"] = 0.0, ["visible_width"] = 64.0, ["visible_height"] = 48.0 },
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60, ["patches"] = new JsonArray() }) },
        };
        JsonObject[] groups = [new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1, 2), ["include_scene_clear"] = true }];
        var settings = new HybridAnalyzeRequest(1, Fixture, dir, dir, 64, 48, 30, 1);
        await using var scheduler = new GroupRenderScheduler(runner, new HybridBakeRequest(2, plan, dir), plan, settings, groups,
            Fixture, Path.Combine(dir, "out"), new JsonObject(), 60, [60], 30, 0, [0], 1, PlaybackEncoderSelection.Software, null, CancellationToken.None);
        RenderRequest request = scheduler.SpeedProbeRequest(0, Path.Combine(dir, "speed"), 120, [2]);
        Assert.True(request is { DirectCrop: null, DirectCrossfadeFrames: null, PlaybackEncoderKind: null, Frames: 1, WarmupFrames: 120 });
        Assert.Equal([2], request.LayerSelection!.IncludeLayers);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runner.RenderAsync(request, null, CancellationToken.None));
        Assert.IsType<FileNotFoundException>(error);
    });

    // 测速准备或渲染失败（这里是源工程不存在）记 not_measured / probe_failed，不让整次分析崩溃；调用方据此按逐项预算重分析。
    [Fact]
    public async Task SlowSpeedProbeFailureIsNotMeasuredInsteadOfThrowing() => await TestTemp.Run(async dir =>
    {
        const string component = "shader/1/0/0/effects/x/periodica_k_frag_3e800000";
        var plan = new JsonObject
        {
            ["source"] = Path.Combine(dir, "missing-source"),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1) }),
            ["loop"] = new JsonObject
            {
                ["retime_budget_percent"] = 1.0,
                ["evidence"] = new JsonArray(new JsonObject { ["component"] = component, ["detail"] = "period 90 s" + ShaderPeriodAnalysis.MeasuredNote }),
                ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 3000,
                    ["components"] = new JsonArray(new JsonObject { ["id"] = component, ["delta_percent"] = 11.1 }),
                    ["patches"] = new JsonArray(new JsonObject { ["component"] = component, ["owner_layer_id"] = 1, ["effect_index"] = 0, ["pass_index"] = 0,
                        ["constant_key"] = "periodica_k_frag_3e800000", ["new_value"] = 1.111, ["speed_exponent"] = 1.0 }) })
            },
        };
        JsonObject record = (await SlowClosureProbe.SpeedAsync(plan, new NativeTools("must-not-run", "must-not-run", "must-not-run", []),
            Path.Combine(dir, "speed"), CancellationToken.None))!;
        Assert.Equal("not_measured", record["status"]!.GetValue<string>());
        Assert.Equal("probe_failed", record["groups"]![0]!["reason"]!.GetValue<string>());
    });
}
