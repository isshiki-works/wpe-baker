using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C1.4：渲染器 job/result、plan.loop、bake.json 拒绝报告的类型化。守住写出的字段、顺序与缺省写法（逐字节），以及 result 的读取映射。

[Trait("Layer", "L1")]
public class RenderJobContractTests
{
    private static readonly string Fixture = Path.Combine(LocalTools.RepositoryRoot, "tests", "fixtures", "native", "shader-clock");

    // 假渲染器：--version 报全部能力，真正渲染时退出 3。job 在启动渲染前就写好了，失败后照样能读。
    private const string AllFeatures = "sparse-readback-v1,gpu-samples-v1,gpu-sampling-coverage-v1,gpu-encode-v1,gpu-capture-v1," +
        "gpu-loop-encode-v1,gpu-encode-resize-v1,gpu-quality-samples-v1,effect-render-scale-v1,adaptive-effect-resolution-v1,capture-force-visible-owner-v1";

    private static NativeTools FakeTools(string dir, string features = AllFeatures)
    {
        string renderer = Path.Combine(dir, "fake-render.cmd");
        File.WriteAllText(renderer, "@echo off\r\nif \"%~1\"==\"--version\" (\r\n  echo wpe-render test features=" + features +
            "\r\n  exit /b 0\r\n)\r\nexit /b 3\r\n");
        string ffmpeg = Path.Combine(dir, "ffmpeg.exe"), ffprobe = Path.Combine(dir, "ffprobe.exe");
        File.WriteAllBytes(ffmpeg, []);
        File.WriteAllBytes(ffprobe, []);
        return new NativeTools(renderer, ffmpeg, ffprobe, []);
    }

    private static async Task<string> JobAsync(string dir, RenderRequest request, string features = AllFeatures)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => new NativeRenderRunner(FakeTools(dir, features)).RenderAsync(request));
        string job = await File.ReadAllTextAsync(Path.Combine(request.OutputDirectory, "renderer-job.json"));
        string text = job.Replace(Path.GetFullPath(dir).Replace("\\", "\\\\"), "<DIR>").Replace(Fixture.Replace("\\", "\\\\"), "<FIXTURE>");
        await File.WriteAllTextAsync(Path.Combine(dir, Path.GetFileName(request.OutputDirectory) + ".job.json"), text);
        return text;
    }

    [Fact]
    public async Task GpuLoopEncodeJob() => await TestTemp.Run(async dir =>
    {
        var request = new RenderRequest(Fixture, Fixture, Path.Combine(dir, "gpu-loop"), 130, 98, 30000, 1001, 37,
            WarmupFrames: 7, Seed: 17, PixelPacking: "rgba_side_by_side",
            GpuEncoding: new(CrossfadeFrames: 6, Crop: new(130, 98, 16, 10, 100, 78), RetainLoopWindow: true, RetainQualitySamples: true),
            CollectAlphaBounds: true, BoundsIncludeRgb: true, EncodedFrames: 31, RetainFrames: [0, 1, 30, 31, 36],
            LayerSelection: new([1], TransparentBackground: true, IncludePostprocessing: false),
            CaptureTarget: new(OwnerLayerId: 1, EffectOrdinal: 0, ForceVisibleOwner: true),
            OrthographicCaptureViewport: new(65, 49, 130, 98), DeviceUuid: "00112233445566778899aabbccddeeff",
            GpuTiming: true, TraceScene: true, EffectRenderScale: 0.5,
            UserProperties: new JsonObject { ["speed"] = 1.25 }, InputTimeline: [new JsonObject { ["frame"] = 0, ["x"] = 0.5 }],
            OfflineVideoRateOverrides: [new JsonObject { ["owner_layer_id"] = 3, ["rate_numerator"] = 99, ["rate_denominator"] = 100 }]);
        Assert.Equal(GpuLoopExpected.ReplaceLineEndings("\n"), (await JobAsync(dir, request)).ReplaceLineEndings("\n"));
    });

    [Fact]
    public async Task GpuResizeAudioJob() => await TestTemp.Run(async dir =>
    {
        var request = new RenderRequest(Fixture, Fixture, Path.Combine(dir, "gpu-resize"), 64, 48, 60, 1, 10,
            IncludeAudio: true, GpuEncoding: new("hevc_vulkan", 23), EncodeWidth: 32, EncodeHeight: 24,
            MatchEffectResolution: true, Input: new JsonObject { ["mouse"] = new JsonObject { ["x"] = 0.25 } });
        Assert.Equal(GpuResizeExpected.ReplaceLineEndings("\n"), (await JobAsync(dir, request)).ReplaceLineEndings("\n"));
    });

    [Fact]
    public async Task SampledCoverageJob() => await TestTemp.Run(async dir =>
    {
        var request = new RenderRequest(Fixture, Fixture, Path.Combine(dir, "sampled"), 256, 144, 60, 1, 120, WarmupFrames: 30,
            FrameSamplesOnly: true, FrameSampleStride: 8, FrameSampleWidth: 64, FrameSamplePhaseFrames: 13, CollectSamplingCoverage: true);
        Assert.Equal(SampledExpected.ReplaceLineEndings("\n"), (await JobAsync(dir, request)).ReplaceLineEndings("\n"));
    });

    [Fact]
    public async Task SparseOnlyJob() => await TestTemp.Run(async dir =>
    {
        var request = new RenderRequest(Fixture, Fixture, Path.Combine(dir, "sparse"), 256, 144, 60, 1, 120,
            FrameSamplesOnly: true, FrameSampleStride: 8, FrameSampleWidth: 64);
        Assert.Equal(SparseExpected.ReplaceLineEndings("\n"), (await JobAsync(dir, request, "sparse-readback-v1")).ReplaceLineEndings("\n"));
    });

    private const string SampledExpected = """
        {
          "schema_version": 1,
          "source": "<FIXTURE>\\scene.json",
          "assets": "<FIXTURE>",
          "output_dir": "<DIR>\\sampled\\native",
          "width": 256,
          "height": 144,
          "fps_num": 60,
          "fps_den": 1,
          "frames": 120,
          "warmup_frames": 30,
          "seed": 0,
          "raw_stdout": true,
          "write_audio": false,
          "match_effect_resolution": false,
          "output_frame_stride": 8,
          "output_frame_phase": 5,
          "output_sample_width": 64,
          "output_sample_height": 36,
          "collect_sampling_coverage": true
        }
        """;
    private const string SparseExpected = """
        {
          "schema_version": 1,
          "source": "<FIXTURE>\\scene.json",
          "assets": "<FIXTURE>",
          "output_dir": "<DIR>\\sparse\\native",
          "width": 256,
          "height": 144,
          "fps_num": 60,
          "fps_den": 1,
          "frames": 120,
          "warmup_frames": 0,
          "seed": 0,
          "raw_stdout": true,
          "write_audio": false,
          "match_effect_resolution": false,
          "output_frame_stride": 8
        }
        """;

    // 期望值取自改动前（main f7b50705）的代码对同一请求写出的 renderer-job.json（D:/Periodica/runs/C1.4/jobs-gpu/old/）。
    // 两边都统一成 LF 再比：CI 检出按 autocrlf 把本文件变成 CRLF，写 job 的换行随平台。
    private const string GpuLoopExpected = """
        {
          "schema_version": 1,
          "source": "<FIXTURE>\\scene.json",
          "assets": "<FIXTURE>",
          "output_dir": "<DIR>\\gpu-loop\\native",
          "width": 130,
          "height": 98,
          "fps_num": 30000,
          "fps_den": 1001,
          "frames": 37,
          "warmup_frames": 7,
          "seed": 17,
          "raw_stdout": false,
          "write_audio": false,
          "capture_target": {
            "owner_layer_id": 1,
            "effect_ordinal": 0,
            "force_visible_owner": true
          },
          "orthographic_capture_viewport": {
            "center_x": 65,
            "center_y": 49,
            "width": 130,
            "height": 98
          },
          "layer_selection": {
            "include_layers": [
              1
            ],
            "transparent_background": true,
            "include_postprocessing": false
          },
          "input_timeline": [
            {
              "frame": 0,
              "x": 0.5
            }
          ],
          "user_properties": {
            "speed": 1.25
          },
          "offline_video_rate_overrides": [
            {
              "owner_layer_id": 3,
              "rate_numerator": 99,
              "rate_denominator": 100
            }
          ],
          "device_uuid": "00112233445566778899aabbccddeeff",
          "gpu_timing": true,
          "trace_scene": true,
          "effect_render_scale": 0.5,
          "match_effect_resolution": false,
          "gpu_encode": {
            "codec": "h264_vulkan",
            "qp": 18,
            "packed_alpha": true,
            "encoded_frames": 31,
            "collect_bounds": true,
            "bounds_include_rgb": true,
            "retain_frames": [
              0,
              1,
              3,
              7,
              11,
              15,
              18,
              22,
              26,
              30,
              31,
              36
            ],
            "crossfade_frames": 6,
            "retain_loop_window": true,
            "crop_x": 16,
            "crop_y": 10,
            "crop_width": 100,
            "crop_height": 78
          }
        }
        """;
    private const string GpuResizeExpected = """
        {
          "schema_version": 1,
          "source": "<FIXTURE>\\scene.json",
          "assets": "<FIXTURE>",
          "output_dir": "<DIR>\\gpu-resize\\native",
          "width": 64,
          "height": 48,
          "fps_num": 60,
          "fps_den": 1,
          "frames": 10,
          "warmup_frames": 0,
          "seed": 0,
          "raw_stdout": false,
          "write_audio": true,
          "input": {
            "mouse": {
              "x": 0.25
            }
          },
          "match_effect_resolution": true,
          "gpu_encode": {
            "codec": "hevc_vulkan",
            "qp": 23,
            "packed_alpha": false,
            "encoded_frames": 10,
            "collect_bounds": false,
            "bounds_include_rgb": false,
            "retain_frames": [],
            "crossfade_frames": 0,
            "retain_loop_window": false,
            "crop_x": 0,
            "crop_y": 0,
            "crop_width": 64,
            "crop_height": 48,
            "resize_width": 32,
            "resize_height": 24
          }
        }
        """;
}

[Trait("Layer", "L0")]
public class RenderResultContractTests
{
    private const string Complete = """
        {"schema_version":1,"status":"complete","written_frames":12,"renderer_error_count":0,"device_uuid":"ABCD",
         "capture_source":{"render_target":"_rt_x","width":64},"readback_width":32,"readback_height":16,"gpu_sampled":true,
         "output_frame_stride":4,"output_frame_phase":1,"effect_render_scale":0.5,"match_effect_resolution":true,
         "gpu_encoded":true,"readback_frames":3,"gpu_encoder":"h264_vulkan","gpu_packed_alpha":false,
         "gpu_capture":{"encoded_packets":12,"crop":{"x":2},"loop_crossfade":{"status":"applied","crossfade_frames":2.50},
           "resize":{"filter":"lanczos3"},"loop_window":{"frame_count":4},"alpha_bounds":{"includes_rgb":true},"retained_frames":{"width":8}},
         "orthographic_capture_viewport":{"center_x":1},"layer_selection":{"include_layers":[1]},
         "sampling_coverage":{"status":"complete","minimum_alpha":0.10},"runtime_layers":[],"runtime_dependencies":[],
         "runtime_video_rate_overrides":[{"owner_layer_id":3}],"future_field":{"nested":[1,2]}}
        """;

    [Fact]
    public void ReadsSnakeCaseFieldsAndIgnoresUnknown()
    {
        RenderResult r = RenderResult.Parse(Complete);
        Assert.Equal(("ABCD", "_rt_x", 32u, 16u, true, 4u, 1ul), (r.DeviceUuid, r.CaptureSource?.RenderTarget, r.ReadbackWidth, r.ReadbackHeight,
            r.GpuSampled, r.OutputFrameStride, r.OutputFramePhase));
        Assert.Equal((0.5, true, true, 3ul, "h264_vulkan", false), (r.EffectRenderScale, r.MatchEffectResolution, r.GpuEncoded,
            r.ReadbackFrames, r.GpuEncoder, r.GpuPackedAlpha));
        Assert.Equal(12ul, r.GpuCapture?.EncodedPackets);
        Assert.Equal("applied", r.GpuCapture?.LoopCrossfade?["status"]?.GetValue<string>());
        Assert.Equal(2, r.GpuCapture?.Crop?["x"]?.GetValue<int>());
        Assert.Equal(("lanczos3", 4, true, 8), (r.GpuCapture?.Resize?["filter"]?.GetValue<string>(), r.GpuCapture?.LoopWindow?["frame_count"]?.GetValue<int>(),
            r.GpuCapture?.AlphaBounds?["includes_rgb"]?.GetValue<bool>(), r.GpuCapture?.RetainedFrames?["width"]?.GetValue<int>()));
        Assert.Equal(("""{"center_x":1}""", """{"include_layers":[1]}"""), (r.OrthographicCaptureViewport?.ToJsonString(), r.LayerSelection?.ToJsonString()));
        Assert.NotNull(r.RuntimeLayers);
        Assert.NotNull(r.RuntimeDependencies);
        Assert.Single(r.RuntimeVideoRateOverrides!);
    }

    [Fact]
    public void KeepsOriginalTextForManifest()
    {
        // manifest.native_result 与转写进 manifest 的子对象要保留原文（字段、顺序、数值写法）。
        RenderResult r = RenderResult.Parse(Complete);
        Assert.Equal(JsonNode.Parse(Complete)!.ToJsonString(), r.Json.ToJsonString());
        Assert.Equal("""{"status":"complete","minimum_alpha":0.10}""", r.SamplingCoverage!.ToJsonString());
        Assert.Equal("""{"status":"applied","crossfade_frames":2.50}""", r.GpuCapture!.LoopCrossfade!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"status":"complete","written_frames":12,"renderer_error_count":0}""", true)]
    [InlineData("""{"status":"failed","written_frames":12,"renderer_error_count":0}""", false)]
    [InlineData("""{"status":"complete","written_frames":11,"renderer_error_count":0}""", false)]
    [InlineData("""{"status":"complete","written_frames":12,"renderer_error_count":1}""", false)]
    [InlineData("""{"status":"complete","written_frames":12}""", false)]
    public void ConfirmsFrameSequence(string json, bool confirmed) => Assert.Equal(confirmed, RenderResult.Parse(json).Confirms(12));
}

[Trait("Layer", "L0")]
public class LoopReportContractTests
{
    private static LoopReport Report(IReadOnlyList<LoopCandidate> candidates, LoopNoCandidateReason? reason = null,
        JsonObject? sway = null, JsonObject? particle = null, long cadence = 1) =>
        new(30000, 1001, "locked_clip_rates", CommonLoopPreference.Balanced, 2, false, null, 60, reason, candidates, [], false,
            VideoControlScope.Resolve(new JsonObject { ["objects"] = new JsonArray() }, new JsonObject()),
            new LoopContentCadence(cadence, [new("video:1", 1, "clip", new CommonLoopRational(30))]), [], sway, particle);

    [Fact]
    public void WritesV3FieldsInOrderWithNullsWhereTheyWereWritten()
    {
        JsonObject json = Report([], new LoopNoCandidateReason(new(CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling, 60), 1, 2, 3, 4)).ToJson();
        Assert.Equal("schema_version,status,fps_num,fps_den,retime_mode,loop_preference,retime_budget_percent,budget_relaxed," +
            "fixed_frame_step,maximum_seconds,no_candidate_reason,candidates,unresolved,source_static,video_control_scope," +
            "content_cadence,evidence,visual_seam,encoded_loop", string.Join(",", json.Select(x => x.Key)));
        Assert.Equal("no_analytic_candidate", json["status"]!.GetValue<string>());
        Assert.Equal("balanced", json["loop_preference"]!.GetValue<string>());
        Assert.Equal("""{"kind":"FixedPeriodExceedsCeiling","ceiling_seconds":60,"fixed_period_seconds":null,"shader_component_count":1""" +
            ""","runtime_period_count":2,"particle_cycle_count":3,"runtime_clock_uniform_count":4}""", json["no_candidate_reason"]!.ToJsonString());
        JsonObject withCandidate = Report([new LoopCandidate(1, 1d / 30, 0d, [], [])]).ToJson();
        Assert.Equal("analytic_candidate_requires_seam_validation", withCandidate["status"]!.GetValue<string>());
        Assert.True(withCandidate.ContainsKey("no_candidate_reason") && withCandidate["no_candidate_reason"] is null);
        Assert.True(withCandidate.ContainsKey("fixed_frame_step") && withCandidate["fixed_frame_step"] is null);
    }

    [Fact]
    public void OptionalRecordsAreAppendedOnlyWhenPresent()
    {
        JsonObject json = Report([], sway: new JsonObject { ["enabled"] = true }, particle: new JsonObject { ["kind"] = "p" }).ToJson();
        Assert.Equal("sway_retime,loop_length_default", string.Join(",", json.Select(x => x.Key).TakeLast(2)));
        Assert.Equal("encoded_loop", Report([]).ToJson().Last().Key);
    }

    [Fact]
    public void ContentCadenceBasisFollowsRepeatCount()
    {
        JsonObject single = Report([], cadence: 1).ToJson()["content_cadence"]!.AsObject();
        Assert.Equal("""[{"component":"video:1","owner_layer_id":1,"track_name":"clip","clip_fps_numerator":30,"clip_fps_denominator":1}]""",
            single["clips"]!.ToJsonString());
    }
}

// C2.2c：plan.loop.candidates / unresolved 在分析中是类型化对象，写 plan 时渲染一次；这里守住每种条目的键序与缺省写法。
[Trait("Layer", "L0")]
public class LoopItemContractTests
{
    private static string Keys(JsonObject json) => string.Join(",", json.Select(x => x.Key));
    private static readonly CommonLoopComponentCycle Cycle = new("c", CommonLoopPeriodEvidence.Analytic, 3, 1, 1, 1, 0, false);

    [Fact]
    public void CandidateWritesOptionalFieldsInV3Order()
    {
        var warm = new LoopCandidate(120, 2, 0.5, [Cycle], [new LoopVideoRatePatch("v", 4, new CommonLoopRational(25, 24))]) {
            SpriteSeam = new SpriteSeamPhase.Selection(7, SpriteSeamPhase.Verdict.Mismatch, SpriteSeamPhase.Verdict.Closed),
            LoopLengthSource = "stationary_particle_default" };
        JsonObject json = warm.ToJson();
        Assert.Equal("frames,seconds,total_retime_cost_percent,components,patches,source_period_warmup_frames,sprite_seam_phase,loop_length_source", Keys(json));
        Assert.Equal("""{"origin":"mismatch","after_one_period":"closed","basis":"float32 sprite frame table; frame 0 sits on the sprite frame-0 start boundary"}""",
            json["sprite_seam_phase"]!.ToJsonString());
        JsonObject swayed = (warm with { LoopLengthSource = null,
            SwayRetime = new CandidateSwayRetime(new SwayRetimeSolution(60, 2, 120, 2, 0, 0, []), 60, 60, 1, null) }).ToJson();
        Assert.Equal("sway_retime", swayed.Last().Key);
        Assert.Equal("""[{"id":"c","cycles":3,"old_period_seconds":1,"new_period_seconds":1,"speed_multiplier":1,"delta_percent":0}]""", json["components"]!.ToJsonString());
        Assert.Equal("""[{"component":"v","kind":"video_rate","owner_layer_id":4,"rate_numerator":25,"rate_denominator":24,"old_value":1,"new_value":1.0416666666666667,"delta_percent":4.166666666666674}]""",
            json["patches"]!.ToJsonString());
        JsonObject undetermined = (warm with { SpriteSeam = new(null, SpriteSeamPhase.Verdict.Undetermined, null), LoopLengthSource = null }).ToJson();
        Assert.Equal("frames,seconds,total_retime_cost_percent,components,patches,sprite_seam_phase", Keys(undetermined));
        Assert.Equal("origin,basis", Keys(undetermined["sprite_seam_phase"]!.AsObject()));
        Assert.Equal("frames,seconds,total_retime_cost_percent,components,patches",
            Keys((warm with { SpriteSeam = new(0, SpriteSeamPhase.Verdict.Closed, null), LoopLengthSource = null }).ToJson()));
        Assert.Equal("""{"component":"s","kind":"shader_speed","owner_layer_id":1,"effect_index":0,"pass_index":0,"constant_key":"speed","value_index":0,"animation_layer_id":null,"old_value":2,"new_value":3,"speed_exponent":1,"delta_percent":50}""",
            new LoopValuePatch("s", "shader_speed", 1, 0, 0, "speed", 0, null, 2, 3).ToJson().ToJsonString());
    }

    [Fact]
    public void RuntimeTrackItemsKeepPerVariantKeyOrder()
    {
        var particle = new ParticleStationarity.Result(true, [], 1, 2);
        Assert.Equal("kind,owner_layer_id,mechanism,particle_stationarity,detail",
            Keys(new RuntimeTrackUnresolved(false, 3, new Message("unresolved.particle_stationary_random")) { Mechanism = "particle_system", Particle = particle }.ToJson()));
        Assert.Equal("kind,owner_layer_id,track_name,particle_nonperiodic_reason,particle_stationarity,detail",
            Keys(new RuntimeTrackUnresolved(false, 3, new Message("unresolved.particle_stationary_random")) {
                HasTrackName = true, TrackName = "t", ParticleNonperiodicReason = "particle_audio_input", Particle = particle }.ToJson()));
        Assert.Equal("kind,owner_layer_id,track_name,mechanism,random_restart,detail",
            Keys(new RuntimeTrackUnresolved(false, 3, new Message("unresolved.script_random_restart")) {
                HasTrackName = true, Mechanism = "sprite", RandomRestart = true }.ToJson()));
        JsonObject video = new RuntimeTrackUnresolved(true, 3, new Message("unresolved.video_rate_not_one")) { HasTrackName = true }.ToJson();
        Assert.Equal("runtime_video", video["kind"]!.GetValue<string>());
        Assert.True(video.ContainsKey("track_name") && video["track_name"] is null);
        Assert.Equal("kind,owner_layer_id,detail",
            Keys(new RuntimeTrackUnresolved(false, 3, new Message("unresolved.owner_or_duration_unresolved")).ToJson()));
    }

    private static readonly JsonObject NoRuntime = new() { ["status"] = "complete", ["runtime_layers"] = new JsonArray(),
        ["runtime_dependencies"] = new JsonArray(), ["runtime_animation_periods"] = new JsonArray() };

    [Fact]
    public void ParticleDefaultLoopNeedsEveryItemStationary()
    {
        var ceiling = new CommonLoopRational(60);
        RuntimeTrackUnresolved Particle(bool stationary) => new(false, 3, new Message("unresolved.particle_stationary_random")) {
            Mechanism = "particle_system", Particle = new ParticleStationarity.Result(stationary, [], 1, 2) };
        var candidates = new List<LoopCandidate>();
        Assert.Null(LoopAnalysis.StationaryParticleDefaultLoop([Particle(false)], candidates, 60, 1, ceiling));
        Assert.Null(LoopAnalysis.StationaryParticleDefaultLoop([Particle(true), new SolverUnresolved(false, "c", "d")], candidates, 60, 1, ceiling));
        Assert.Empty(candidates);
        JsonObject applied = LoopAnalysis.StationaryParticleDefaultLoop([Particle(true)], candidates, 60, 1, ceiling)!;
        Assert.Equal("applied", applied["status"]!.GetValue<string>());
        Assert.Equal("[3]", applied["particle_layer_ids"]!.ToJsonString());
        Assert.Equal("stationary_particle_default", Assert.Single(candidates).LoopLengthSource);
    }

    [Fact]
    public void UnlockedParticleVerdictReplacesDetailOnlyOnParticleSystemItems()
    {
        var locked = new ParticleStationarity.Result(true, [], 1, 2);
        var unlocked = new ParticleStationarity.Result(false, [new("C2", "lifetime_capped_no_common_loop", "maxcount", null)], 1, 2);
        List<LoopUnresolved> items = [
            new RuntimeTrackUnresolved(false, 3, new Message("unresolved.particle_stationary_random")) { Mechanism = "particle_system", Particle = locked },
            new RuntimeTrackUnresolved(false, 4, new Message("unresolved.particle_stationary_random")) { ParticleNonperiodicReason = "particle_audio_input", Particle = locked }];
        RuntimeTrackReader.RewriteParticleItems(items, new Dictionary<int, ParticleStationarity.Result> { [3] = unlocked, [4] = unlocked });
        var system = (RuntimeTrackUnresolved)items[0];
        var sprite = (RuntimeTrackUnresolved)items[1];
        Assert.Same(unlocked, system.Particle);
        Assert.Equal("unresolved.particle_not_stationary", system.Detail.Key);
        Assert.Same(unlocked, sprite.Particle);
        Assert.Equal("unresolved.particle_stationary_random", sprite.Detail.Key);
    }

    [Fact]
    public void RuntimeMaterialAndScriptClockItemsAreRecordedOncePerOwner()
    {
        JsonObject Layer() => new() { ["owner"] = 1, ["materials"] = new JsonArray(new JsonObject { ["role"] = "source", ["active_uniforms"] = new JsonArray("g_Time") }) };
        var runtime = new JsonObject { ["runtime_layers"] = new JsonArray(Layer(), Layer()),
            ["runtime_dependencies"] = new JsonArray(
                new JsonObject { ["operation"] = "time", ["owner"] = 1, ["binding"] = "b", ["property"] = "g_Time" },
                new JsonObject { ["operation"] = "time", ["owner"] = 1, ["binding"] = "b", ["property"] = "g_Time" }) };
        var items = new List<LoopUnresolved>();
        RuntimeTrackReader.AddMaterialClockUnresolved(runtime, [1], items, new HashSet<(int, string)>());
        RuntimeTrackReader.AddScriptTimeUnresolved(runtime, [1], items);
        Assert.Equal("runtime_material,script_time", string.Join(",", items.Select(item => item.Kind)));
        Assert.Equal("g_Time", items[1].ToJson()["clock"]!.GetValue<string>());
    }

    [Fact]
    public void StaticProofNamesTheBakedParticleLayer()
    {
        JsonObject Scene(string key) => new() { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "雨", [key] = "x.json" }) };
        Assert.Equal(new StaticLayerNaming("雨", true), StaticProof.Obstacle(Scene("particle"), null!, null, NoRuntime, [1])!.Layer);
        Assert.Equal(new StaticLayerNaming("雨", false), StaticProof.Obstacle(Scene("puppet"), null!, null, NoRuntime, [1])!.Layer);
    }

    [Fact]
    public void ConstantScriptProofRejectsClockAndAccumulation()
    {
        const string head = "'use strict';\nexport var scriptProperties = createScriptProperties().addSlider({ name: 'x', value: 0, min: -1 }).finish();\n";
        Assert.True(StaticProof.ConstantScript(head + "export function update(value) {\n  value.x = scriptProperties.x * engine.canvasSize.x; // 定位\n  return value;\n}"));
        Assert.False(StaticProof.ConstantScript(head + "export function update(value) {\n  value.x += scriptProperties.x;\n  return value;\n}"));
        Assert.False(StaticProof.ConstantScript(head + "export function update(value) {\n  value.x = value.y + 1;\n  return value;\n}"));
        Assert.False(StaticProof.ConstantScript(head + "export function update(value) {\n  value.x = engine.runtime;\n  return value;\n}"));
        Assert.False(StaticProof.ConstantScript(head + "let t = 0;\nexport function update(value) {\n  value.x = t;\n  return value;\n}"));
    }

    [Fact]
    public void UnprovenVideoTrackWritesTrackNameKey()
    {
        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }) };
        var runtime = new JsonObject { ["runtime_animation_periods"] = new JsonArray(new JsonObject {
            ["source_owner_layer_id"] = 1, ["mechanism"] = "video", ["looping"] = false, ["track_name"] = null }) };
        var items = new List<LoopUnresolved>();
        RuntimeTrackReader.Read(scene, null!, null, runtime, [1], items, VideoControlScope.Resolve(scene, runtime),
            new ParticleStationarity.FrameClock(60, 1, 60), out _);
        JsonObject video = Assert.Single(items).ToJson();
        Assert.Equal("kind,owner_layer_id,track_name,detail", string.Join(",", video.Select(x => x.Key)));
    }

    [Fact]
    public void UnresolvedNotesCarryMessagesAndStaticLayerOutsideThePlan()
    {
        LoopUnresolved[] items = [
            new RuntimeTrackUnresolved(false, 3, new Message("unresolved.particle_stationary_random")) { Mechanism = "particle_system" },
            new SourceStaticUnresolved("d", 2, new StaticLayerNaming("背景", true)),
            new SolverUnresolved(true, null, "budget")];
        var loop = new JsonObject { ["unresolved"] = new JsonArray([.. items.Select(x => (JsonNode)x.ToJson())]) };
        JsonObject packed = JsonNode.Parse(new JsonObject { ["loop"] = loop.DeepClone(), ["notes"] = UnresolvedNotes.PackNotes(items) }.ToJsonString())!.AsObject();
        var (unpacked, notes) = UnresolvedNotes.Unpack(packed);
        // 缓存往返后：loop 只有 v3 字段，文案与点名图层按下标取回。
        Assert.True(JsonNode.DeepEquals(loop, unpacked) && unpacked.Parent is null);
        Assert.Equal("unresolved.particle_stationary_random", notes.At(unpacked, 0)!.Localized!["key"]!.GetValue<string>());
        Assert.Equal(new StaticLayerNaming("背景", true), notes.At(unpacked, 1)!.Layer);
        Assert.Null(notes.At(unpacked, 2)!.Localized);
        // 同下标条目被改过就不认。
        unpacked["unresolved"]![0]!["detail"] = "changed";
        Assert.Null(notes.At(unpacked, 0));
        Assert.Null(notes.At(unpacked, 3));
    }

    [Fact]
    public void AddLoopUnresolvedKeepsNotesInStepAndDedupesOnTextAndMessage()
    {
        var plan = new JsonObject { ["loop"] = new JsonObject { ["unresolved"] = new JsonArray() },
            ["whole_layer"] = new JsonObject { ["loop"] = new JsonObject { ["unresolved"] = new JsonArray() } } };
        var notes = new UnresolvedNotes();
        var first = new Message("unresolved.particle_stationary_random").Localized();
        Verdict.AddLoopUnresolved(plan, "k", "same", notes, first);
        Verdict.AddLoopUnresolved(plan, "k", "same", notes, first);
        Assert.Single(plan["loop"]!["unresolved"]!.AsArray());
        // 同一句英文、不同文案不算重复（与原来"整条结构相等"同义）。
        Verdict.AddLoopUnresolved(plan, "k", "same", notes, new Message("unresolved.script_time").Localized());
        Assert.Equal(2, plan["loop"]!["unresolved"]!.AsArray().Count);
        Assert.Equal(2, plan["whole_layer"]!["loop"]!["unresolved"]!.AsArray().Count);
        PlanNarrative.Attach(plan, notes);
        Assert.Equal(["unresolved.particle_stationary_random", "unresolved.script_time"],
            plan["whole_layer"]!["loop"]!["unresolved_localized"]!.AsArray().Select(x => x!["key"]!.GetValue<string>()));
        Assert.Equal("unresolved_localized", plan["loop"]!.AsObject().Last().Key);
    }

    [Fact]
    public void OtherUnresolvedItemsKeepKeyOrder()
    {
        Assert.Equal("""{"kind":"search_budget","detail":"d"}""", new SolverUnresolved(true, null, "d").ToJson().ToJsonString());
        Assert.Equal("""{"kind":"solver","component":null,"detail":"d"}""", new SolverUnresolved(false, null, "d").ToJson().ToJsonString());
        Assert.Equal("""{"kind":"source_static","detail":"d","owner_layer_id":null}""", new SourceStaticUnresolved("d", null, null).ToJson().ToJsonString());
        // 点名图层只走类型化记录，不进条目。
        Assert.Equal("""{"kind":"source_static","detail":"d","owner_layer_id":2}""",
            new SourceStaticUnresolved("d", 2, new StaticLayerNaming(null, false)).ToJson().ToJsonString());
        Assert.Equal("kind,owner_layer_id,binding,clock,detail",
            Keys(new ScriptTimeUnresolved(1, JsonValue.Create("b"), JsonValue.Create("time")).ToJson()));
        Assert.Equal("kind,rejected_candidate_count,detail", Keys(new SpriteSeamUnresolved(2).ToJson()));
        var shader = new ShaderTemporalUnresolved(1, 0, 0, "r", ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, "d");
        Assert.Equal("""{"kind":"UnsupportedShaderMechanism","owner_layer_id":1,"effect_index":0,"pass_index":0,"resource":"r","detail":"d","bounded_displacement":false,"mechanism":null}""",
            new ShaderLoopUnresolved(shader).ToJson().ToJsonString());
        // 带文案的着色器条目：detail 原位换成文案的英文原文，文案本身只在 DetailMessage 上。
        var keyed = new ShaderLoopUnresolved(shader with { Message = new Message("unresolved.script_time") });
        Assert.Equal(MessageCatalog.RenderLegacy("unresolved.script_time"), keyed.ToJson()["detail"]!.GetValue<string>());
        Assert.Equal("kind,owner_layer_id,effect_index,pass_index,resource,detail,bounded_displacement,mechanism", Keys(keyed.ToJson()));
        Assert.Equal("unresolved.script_time", keyed.DetailMessage!.Key);
    }
}

[Trait("Layer", "L0")]
public class BakeRejectionContractTests
{
    [Fact]
    public void WritesHeaderEvidenceTailTrailerInOrder()
    {
        var plan = new JsonObject { ["loop"] = new JsonObject() };
        var evidence = new JsonObject { ["reason"] = "r", ["disk_budget"] = new JsonObject { ["x"] = 1 } };
        var trailer = new JsonObject { ["reason_zh"] = "中", ["reason_en"] = "en" };
        JsonObject json = new BakeRejection("candidate_rejected_disk_space", "abc", plan, 0.5, true, "no_suitable_loop", evidence, trailer).ToJson();
        const string expected = """{"schema_version":2,"artifact_kind":"hybrid_video_candidate","status":"candidate_rejected_disk_space","source_sha256":"abc","source_digest_scope":"project-source-files-sha256-v2","plan":{"loop":{}},"effect_render_scale":0.5,"match_effect_resolution":true,"reason":"r","disk_budget":{"x":1},"frames":0,"groups":[],"loop_validation":"no_suitable_loop","official_playback":"not_verified","measured_gain":"not_verified","reason_zh":"中","reason_en":"en"}""";
        Assert.Equal(expected,
            json.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Same(plan, json["plan"]);
    }
}
