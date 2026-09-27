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
        // version：0 改速前、1 改速后、2 对照（改速项按原速 2 倍）
        static Task<JsonObject> Read(Func<int, double, byte[]> frame) => SlowClosureProbe.MeasureAsync(
            (version, index) => Task.FromResult(frame(version, index / 30.0)), size, size, 1, 30, 1, 400 * 30);
        static Task<JsonObject> Shift(double deviation) => Read((version, t) => Frame(version == 1 ? deviation * t : 0));
        JsonObject slow = await Shift(0.05), fast = await Shift(0.5);
        Assert.Equal("passed", slow["status"]!.GetValue<string>());
        Assert.InRange(slow["peak_speed_deviation_px_per_second"]!.GetValue<double>(), 0.005, 0.07);
        Assert.Equal("visible", fast["status"]!.GetValue<string>());
        JsonObject pulse = await Read((version, t) => Frame(0, 20 * Math.Sin(2 * Math.PI * t / (version == 1 ? 101 : 400)), halfFlat: true));
        Assert.Equal("visible", pulse["status"]!.GetValue<string>());
        Assert.True(pulse["flat_block_changed"]!.GetValue<bool>());
        // 慢亮度漂移（3715870843 的 caustics 形态）：平坦光晕 ±5 levels、周期 400 s 改到 300 s。8 s 内两版量化后逐位相同（旧的 0.5–8 s 窗读 0 放行），
        // 翻倍到第一个最差块差 ≥ 3 levels 的窗才算读数，平坦块里的变化判看得出
        JsonObject drift = await Read((version, t) => Frame(0, 5 * Math.Sin(2 * Math.PI * t / (version == 1 ? 300 : 400)), halfFlat: true));
        Assert.Equal("visible", drift["status"]!.GetValue<string>());
        Assert.True(drift["worst_window_seconds"]!.GetValue<double>() > 8);
        // 整个循环（400 s）里两版的差都在量化阈值以下，对照（2 倍速）也分不出：改速项在画面上不起作用，放行并写明依据
        static double Period(int version) => version switch { 1 => 300, 2 => 200, _ => 400 };
        JsonObject faint = await Read((version, t) => Frame(0, 0.4 * Math.Sin(2 * Math.PI * t / Period(version)), halfFlat: true));
        Assert.Equal("passed", faint["status"]!.GetValue<string>());
        Assert.Equal("component_not_visible", faint["basis"]!.GetValue<string>());
        Assert.Equal(400.0, faint["worst_window_seconds"]!.GetValue<double>());
        // 所有者层在动（光晕 ±20 levels 来自没改速的项），改速的项却不影响画面（3715870843 的一种可能）：三版逐位相同。
        // 原速版本自身变化很大（self_change）不能说明补丁没生效——对照也分不出，按 component_not_visible 放行
        JsonObject idle = await Read((_, t) => Frame(0, 20 * Math.Sin(2 * Math.PI * t / 400), halfFlat: true));
        Assert.Equal("component_not_visible", idle["basis"]!.GetValue<string>());
        Assert.True(idle["self_change_levels"]!.GetValue<double>() >= SlowClosureProbe.MeasurableLevels);
        // 补丁没生效：改速项确实影响画面（对照 2 倍速分得出），改速后的版本却与改速前逐位相同——不放行
        JsonObject inert = await Read((version, t) => Frame(0, 20 * Math.Sin(2 * Math.PI * t / (version == 2 ? 200 : 400)), halfFlat: true));
        Assert.Equal("not_measured", inert["status"]!.GetValue<string>());
        Assert.Equal("patch_not_effective", inert["reason"]!.GetValue<string>());
        Assert.Equal("frame0_differs", (await Read((version, t) => Frame(0, speck: version == 1 && t == 0)))["reason"]!.GetValue<string>());
        Assert.Equal("no_texture", (await Read((version, t) => Frame(0, version == 1 ? t : 0, flat: true)))["reason"]!.GetValue<string>());
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

    // 主渲染不等合成校验道的体积外推（原先每组开跑前 await SizeEstimate，校验道整段做完才放行，主渲染与它并不并行）。
    // 外推一直不来时，主渲染照样开跑、走到"渲染器文件不存在"上；改回等外推，这里等到超时失败。
    [Fact]
    public async Task MasterRenderStartsBeforeTheSizeEstimateArrives() => await TestTemp.Run(async dir =>
    {
        var tools = new NativeTools(Path.Combine(dir, "no-renderer"), Path.Combine(dir, "no-ffmpeg"), Path.Combine(dir, "no-ffprobe"), []);
        var plan = new JsonObject
        {
            ["projection"] = new JsonObject { ["center_x"] = 0.0, ["center_y"] = 0.0, ["visible_width"] = 64.0, ["visible_height"] = 48.0 },
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60, ["patches"] = new JsonArray() }) },
        };
        JsonObject[] groups = [new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1), ["include_scene_clear"] = true }];
        var settings = new HybridAnalyzeRequest(1, Fixture, dir, dir, 64, 48, 30, 1);
        var estimate = new TaskCompletionSource<JsonObject?>();
        await using var scheduler = new GroupRenderScheduler(new NativeRenderRunner(tools), new HybridBakeRequest(2, plan, dir), plan, settings, groups,
            Fixture, Path.Combine(dir, "out"), new JsonObject(), 60, [60], 0, 0, [], 1, PlaybackEncoderSelection.Software, null, CancellationToken.None)
            { SizeEstimate = estimate.Task };
        Exception error;
        // 放行外推放在 finally：失败时渲染不再挂着，scheduler 退出时能收尾。
        try { error = await Assert.ThrowsAnyAsync<Exception>(() => scheduler.RenderAsync(0).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)); }
        finally { estimate.TrySetResult(null); }
        Assert.IsType<FileNotFoundException>(error);
    });

    // 慢分量闭合预检每组一次稀疏渲染：1 帧预热后渲 P+1 帧、步长 P，只光栅化第 0 与第 P 帧并留原帧。
    // 假渲染器（Python）每帧字节 = 模拟帧号 mod 251：留下的两帧必须来自模拟帧 W 与 W+P，且只起了一个渲染进程。
    // 改回两次 1 帧渲染时 job 数与帧数对不上，这条失败。
    [Fact]
    public async Task SlowClosurePairComesFromOneSparseRender() => await TestTemp.Run(async dir =>
    {
        string? python = new[] { "python3", "python" }.SelectMany(name => (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator).Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name)))
            .FirstOrDefault(File.Exists);
        Assert.SkipUnless(python is not null, "缺 Python，假渲染器跑不起来");
        string script = Path.Combine(dir, "fake-render.py");
        await File.WriteAllTextAsync(script, """
            import json, os, sys
            if sys.argv[1] == "--version":
                print("wpe-render fake features=sparse-readback-v1,gpu-samples-v1"); sys.exit(0)
            job = json.load(open(sys.argv[3], encoding="utf-8"))
            os.makedirs(job["output_dir"], exist_ok=True)
            warm, frames = job.get("warmup_frames", 0), job["frames"]
            stride, phase = job.get("output_frame_stride", 1), job.get("output_frame_phase")
            size = (job.get("output_sample_width") or job["width"]) * (job.get("output_sample_height") or job["height"]) * 4
            written = 0
            for frame in range(warm, warm + frames):
                offset = (frame - warm) % stride
                if offset != 0 and offset != phase: continue
                sys.stdout.buffer.write(bytes([frame % 251]) * size); written += 1
            sys.stdout.flush()
            json.dump({"status": "complete", "written_frames": written, "renderer_error_count": 0, "output_frame_stride": stride,
                       "output_frame_phase": phase, "effect_render_scale": 1.0, "match_effect_resolution": False,
                       "orthographic_capture_viewport": job.get("orthographic_capture_viewport"), "layer_selection": job.get("layer_selection")},
                      open(os.path.join(job["output_dir"], "result.json"), "w"))
            """);
        string renderer;
        if (OperatingSystem.IsWindows())
        {
            renderer = Path.Combine(dir, "fake-render.cmd");
            await File.WriteAllTextAsync(renderer, $"@\"{python}\" \"{script}\" %*\r\n");
        }
        else
        {
            renderer = Path.Combine(dir, "fake-render");
            await File.WriteAllTextAsync(renderer, $"#!/bin/sh\nexec \"{python}\" \"{script}\" \"$@\"\n");
            File.SetUnixFileMode(renderer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var runner = new NativeRenderRunner(new NativeTools(renderer, Path.Combine(dir, "no-ffmpeg"), Path.Combine(dir, "no-ffprobe"), []));
        var plan = new JsonObject
        {
            ["projection"] = new JsonObject { ["center_x"] = 0.0, ["center_y"] = 0.0, ["visible_width"] = 64.0, ["visible_height"] = 48.0 },
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 60, ["patches"] = new JsonArray() }) },
        };
        JsonObject[] groups = [new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1, 2), ["include_scene_clear"] = true }];
        var settings = new HybridAnalyzeRequest(1, Fixture, dir, dir, 64, 48, 30, 1);
        await using var scheduler = new GroupRenderScheduler(runner, new HybridBakeRequest(2, plan, dir), plan, settings, groups,
            Fixture, Path.Combine(dir, "out"), new JsonObject(), 60, [60], 0, 7, [], 1, PlaybackEncoderSelection.Software, null, CancellationToken.None);
        RenderRequest request = scheduler.ClosureProbeRequest(0, Path.Combine(dir, "probe"));
        Assert.True(request is { Frames: 61, FrameSampleStride: 60, FrameSamplesOnly: true, PlaybackEncoderKind: null, GpuEncoding: null });
        JsonObject render = await runner.RenderAsync(request, null, CancellationToken.None);
        byte[] first = await LoopClosureCheck.ReadRetainedFrameAsync(render, 0), wrap = await LoopClosureCheck.ReadRetainedFrameAsync(render, 60);
        Assert.Equal(64 * 48 * 4, first.Length);
        Assert.Equal((byte)(request.WarmupFrames % 251), first[0]);
        Assert.Equal((byte)((request.WarmupFrames + 60) % 251), wrap[0]);
        Assert.Equal("sparse_rgba", render["native_frame_transport"]!.GetValue<string>());
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

    // 覆盖 shader 的写出记录带上改写的键：创意工坊特效（资源名带 workshop/<id>/，3715870843 的 caustics 形态）照样改写，
    // 慢项测速据此核对每条改速补丁的键真的写进了覆盖 shader；pass 着色器解析不出（这里删掉材质）时静默跳过，核对把它挑出来
    [Fact]
    public async Task OverrideShaderRecordsTheKnobsItRewrote() => await TestTemp.Run(async dir =>
    {
        string project = Path.Combine(dir, "project");
        void Write(string resource, string text)
        {
            string path = Path.Combine(project, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        string knob = ShaderTextPatch.KnobKey(new JsonObject { ["stage"] = "frag", ["literal"] = 0.003777 });
        Write("scene.json", """{"objects":[]}""");
        Write("project.json", """{"file":"scene.json","type":"scene"}""");
        Write("workshop/3468454002/effects/caustics/effect.json", """{"passes":[{"material":"workshop/3468454002/materials/effects/caustics.json"}]}""");
        Write("workshop/3468454002/materials/effects/caustics.json", """{"passes":[{"shader":"workshop/3468454002/effects/caustics"}]}""");
        Write("shaders/workshop/3468454002/effects/caustics.frag",
            "uniform float g_Time;\nvoid main() { float time = g_Time * 0.5; gl_FragColor = vec4(fract(time * 0.003777)); }\n");
        var scene = JsonNode.Parse($$$"""
            {"objects":[{"id":409,"effects":[{"file":"workshop/3468454002/effects/caustics/effect.json",
              "passes":[{"constantshadervalues":{"{{{knob}}}":1.33}}]}]}]}
            """)!.AsObject();
        JsonArray Written(string capture)
        {
            using var source = new ProjectSource(project);
            return ShaderTextPatch.WriteTimeScaleAsync(Path.Combine(dir, capture), source, null, scene, CancellationToken.None).GetAwaiter().GetResult();
        }
        JsonArray written = Written("capture");
        Assert.Equal("shaders/workshop/3468454002/effects/caustics.frag", written.Single()!["resource"]!.GetValue<string>());
        Assert.Empty(SlowClosureProbe.UnwrittenKeys(written, [knob]));
        File.Delete(Path.Combine(project, "workshop/3468454002/materials/effects/caustics.json"));
        Assert.Equal([knob], SlowClosureProbe.UnwrittenKeys(Written("capture-without-material"), [knob]));
        await Task.CompletedTask;
    });

    // 冻结的粒子湍流场不进求解器，没有候选分量与证据：按补丁认项，同样进测速
    [Fact]
    public async Task FrozenParticleFieldEntersTheSpeedProbe() => await TestTemp.Run(async dir =>
    {
        const string component = "particle_field/1/operator1";
        var plan = new JsonObject
        {
            ["source"] = Path.Combine(dir, "missing-source"),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1) }),
            ["loop"] = new JsonObject
            {
                ["retime_budget_percent"] = 1.0, ["evidence"] = new JsonArray(),
                ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 3000, ["components"] = new JsonArray(),
                    ["patches"] = new JsonArray(new JsonObject { ["component"] = component, ["kind"] = LoopAnalysis.ParticleFieldPatchKind, ["owner_layer_id"] = 1,
                        ["effect_index"] = -1, ["pass_index"] = -1, ["constant_key"] = "timescale", ["value_index"] = 1, ["old_value"] = 20.0, ["new_value"] = 0.0,
                        ["speed_exponent"] = 1.0 }) })
            },
        };
        JsonObject? record = await SlowClosureProbe.SpeedAsync(plan, new NativeTools("must-not-run", "must-not-run", "must-not-run", []),
            Path.Combine(dir, "speed"), CancellationToken.None);
        Assert.Equal([component], record?["components"]!.AsArray().Select(x => x!.GetValue<string>()) ?? []);
        Assert.Equal("not_measured", record!["status"]!.GetValue<string>());
    });
}
