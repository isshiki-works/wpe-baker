using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 特效前缀的资格：一层里只要有一个效果读指针/音频，整层就留实时；前缀路线本该接手——把读输入的效果当后缀，
// 之前的纯时间特效照样缓存。原来 HasRuntimeInput 查该层全部材质（读输入的恰好是后缀，永远不合格），
// 底材质又只认 genericimage3/4（抽查的背景全是 genericimage2），这类层一个前缀都提不出来。
[Trait("Layer", "L1")]
public class EffectPrefixEligibilityTests
{
    [Fact]
    public async Task CompositionReferenceUsesTheInstalledPrefixClock() => await TestTemp.Run(async root =>
    {
        string authored = Path.Combine(root, "author"), reference = Path.Combine(root, "reference");
        var (scene, _) = Background(authored);
        string shader = Path.Combine(authored, "shaders/effects/waves.frag");
        Directory.CreateDirectory(Path.GetDirectoryName(shader)!);
        await File.WriteAllTextAsync(shader,
            "uniform float g_Time; void main(){ gl_FragColor=vec4(sin(g_Time)); }",
            TestContext.Current.CancellationToken);
        using var source = new ProjectSource(authored);
        await source.ExtractAsync(reference, TestContext.Current.CancellationToken);
        var loop = new JsonObject { ["candidates"] = new JsonArray(new JsonObject {
            ["patches"] = new JsonArray(new JsonObject {
                ["kind"] = "shader_speed", ["owner_layer_id"] = 1, ["effect_index"] = 0,
                ["pass_index"] = 0, ["constant_key"] = ShaderPeriodAnalysis.TimeScaleKey,
                ["value_index"] = 0, ["old_value"] = 1.0, ["new_value"] = 1.1 }) }) };
        JsonArray written = await EffectPrefixBakeService.PatchCompositionReferenceAsync(reference, source,
            authored, scene, new JsonObject(), [loop], TestContext.Current.CancellationToken);
        Assert.Equal(1.1, scene["objects"]![0]!["effects"]![0]!["passes"]![0]!
            ["constantshadervalues"]![ShaderPeriodAnalysis.TimeScaleKey]!.GetValue<double>());
        Assert.Contains(written.OfType<JsonObject>(), patch => patch["resource"]?.GetValue<string>() ==
            "shaders/effects/waves.frag");
        Assert.Contains("g_Time*g_PeriodicaTimeScale", await File.ReadAllTextAsync(
            Path.Combine(reference, "shaders/effects/waves.frag"), TestContext.Current.CancellationToken));
    });

    /// <summary>一张 genericimage2 背景：效果 0 是纯时间特效（周期 3 s），效果 1 读指针。</summary>
    private static (JsonObject Scene, JsonObject Runtime) Background(string dir)
    {
        void Write(string resource, string text)
        {
            string path = Path.Combine(dir, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("project.json", """{"file":"scene.json","type":"scene"}""");
        Write("models/background.json", """{"material":"materials/background.json","autosize":true}""");
        Write("materials/background.json", """{"passes":[{"shader":"genericimage2","textures":["background"]}]}""");
        foreach (string name in new[] { "waves", "cursor" })
        {
            Write($"effects/{name}/effect.json", $$"""{"passes":[{"material":"materials/effects/{{name}}.json"}]}""");
            Write($"materials/effects/{name}.json", $$"""{"passes":[{"shader":"effects/{{name}}"}]}""");
        }
        var scene = JsonNode.Parse("""
            {"general":{"orthogonalprojection":{"width":64,"height":48}},
             "objects":[{"id":1,"image":"models/background.json","size":"64 48","effects":[
               {"id":10,"file":"effects/waves/effect.json","passes":[{}]},
               {"id":11,"file":"effects/cursor/effect.json","passes":[{}]}]}]}
            """)!.AsObject();
        Write("scene.json", scene.ToJsonString());
        JsonObject Material(string shader, string role, int? effect, string uniforms, string? signature) => JsonNode.Parse($$"""
            {"shader":"{{shader}}","role":"{{role}}"{{(effect is int e ? $",\"effect\":{e},\"pass\":0" : "")}},
             "uses_audio_spectrum":false,"active_uniforms":[{{uniforms}}],"textures":[]{{(signature is null ? "" : ",\"time_signature\":" + signature)}}}
            """)!.AsObject();
        const string Waves = """{"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[{"seconds":3,"num":3,"den":1,"pi":0,"knobs":[]}]}""";
        const string Still = """{"kind":"static","reasons":[],"external":[],"transient":false,"terms":[]}""";
        var runtime = new JsonObject { ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(), ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 1, ["materials"] = new JsonArray(
                Material("genericimage2", "source", null, "", Still), Material("effects/waves", "effect", 0, "\"g_Time\"", Waves),
                Material("effects/cursor", "effect", 1, "\"g_PointerPosition\"", Still)) }) };
        return (scene, runtime);
    }

    [Fact]
    public async Task PointerEffectEndsThePrefixOfAGenericImage2Background() => await TestTemp.Run(async dir =>
    {
        var (scene, runtime) = Background(dir);
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(1, dir, dir, dir, 64, 48, 30, 1);
        JsonArray proposals = EffectPrefixPlanner.Propose(scene, source, dir, runtime, new JsonObject(), request, new JsonObject());
        JsonObject proposal = Assert.Single(proposals.OfType<JsonObject>());
        Assert.Equal(1, proposal["prefix_effect_count"]!.GetValue<int>());
        Assert.Equal(10, proposal["terminal_effect_id"]!.GetValue<int>());
        await Task.CompletedTask;
    });

    [Fact]
    public async Task DynamicSuffixDoesNotHideASafePrefixButDynamicOwnerStillDoes() => await TestTemp.Run(async dir =>
    {
        var (scene, runtime) = Background(dir);
        scene["objects"]![0]!["effects"]![1]!["passes"]![0]!["constantshadervalues"] = new JsonObject {
            ["amount"] = new JsonObject { ["value"] = 0, ["script"] = "export function update(value) { return engine.timeOfDay; }" } };
        File.WriteAllText(Path.Combine(dir, "models/background.json"),
            """{"material":"materials/background.json","autosize":true,"cropoffset":"0 24"}""");
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(1, dir, dir, dir, 64, 48, 30, 1);
        JsonObject prefix = Assert.Single(EffectPrefixPlanner.Propose(scene, source, dir, runtime,
            new JsonObject(), request, new JsonObject()).OfType<JsonObject>());
        Assert.Equal(1, prefix["prefix_effect_count"]!.GetValue<int>());
        EffectPrefixBakeService.ValidateSource(source, scene, prefix);
        scene["objects"]![0]!["origin"] = new JsonObject { ["value"] = "0 0 0",
            ["script"] = "export function update(value) { return engine.timeOfDay; }" };
        Assert.Empty(EffectPrefixPlanner.Propose(scene, source, dir, runtime,
            new JsonObject(), request, new JsonObject()));
        Assert.Throws<InvalidDataException>(() => EffectPrefixBakeService.ValidateSource(source, scene, prefix));
        await Task.CompletedTask;
    });

    [Fact]
    public async Task PureOwnerVisibilityCanStayLiveAroundCachedPixels() => await TestTemp.Run(async dir =>
    {
        var (scene, runtime) = Background(dir);
        JsonObject owner = scene["objects"]![0]!.AsObject();
        owner["visible"] = new JsonObject { ["value"] = true, ["script"] = """
            'use strict';
            export function update(value) {
                if (engine.userProperties.character == 1 || engine.userProperties.character == 2) {
                    value = true;
                } else {
                    value = false;
                }
                return value;
            }
            """ };
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(1, dir, dir, dir, 64, 48, 30, 1);
        JsonObject prefix = Assert.Single(EffectPrefixPlanner.Propose(scene, source, dir, runtime,
            new JsonObject(), request, new JsonObject()).OfType<JsonObject>());
        Assert.True(prefix["preserve_external_visibility"]!.GetValue<bool>());
        EffectPrefixBakeService.ValidateSource(source, scene, prefix);
        JsonObject derived = scene.DeepClone().AsObject();
        string rgba = Path.Combine(dir, "cache.rgba"), output = Path.Combine(dir, "candidate");
        await File.WriteAllBytesAsync(rgba, new byte[64 * 48 * 4]);
        await EffectPrefixCache.ApplyAsync(source, scene, derived, output, 1, 1, rgba, 64, 48, rgbaFrame: true);
        Assert.True(JsonNode.DeepEquals(owner["visible"], derived["objects"]![0]!["visible"]));
        Assert.Single(derived["objects"]![0]!["effects"]!.AsArray());
    });

    [Fact]
    public async Task OwnerVisibilityWithPixelSideEffectCannotBeCached() => await TestTemp.Run(async dir =>
    {
        var (scene, runtime) = Background(dir);
        JsonObject owner = scene["objects"]![0]!.AsObject();
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(1, dir, dir, dir, 64, 48, 30, 1);
        JsonObject ordinary = Assert.Single(EffectPrefixPlanner.Propose(scene, source, dir, runtime,
            new JsonObject(), request, new JsonObject()).OfType<JsonObject>());
        owner["visible"] = new JsonObject { ["value"] = true, ["script"] = """
            export function update(value) {
                if (engine.userProperties.character == 1) thisLayer.alpha = 0.5;
                return value;
            }
            """ };
        Assert.Empty(EffectPrefixPlanner.Propose(scene, source, dir, runtime, new JsonObject(), request, new JsonObject()));
        Assert.Throws<InvalidDataException>(() => EffectPrefixBakeService.ValidateSource(source, scene, ordinary));
        await Task.CompletedTask;
    });

    [Fact]
    public void WholeLayerWithoutCapturedEffectsStillChecksSafePrefixes()
    {
        var loop = new JsonObject { ["candidates"] = new JsonArray(new JsonObject()), ["unresolved"] = new JsonArray() };
        var groups = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(1) });
        var layers = new JsonArray(new JsonObject { ["owner"] = 1,
            ["materials"] = new JsonArray(new JsonObject { ["role"] = "source" }) });
        Assert.False(Routes.ProbePrefix(loop, groups, layers));
        layers.Add(new JsonObject { ["owner"] = 2,
            ["materials"] = new JsonArray(new JsonObject { ["role"] = "effect" }) });
        Assert.True(Routes.ProbePrefix(loop, groups, layers));
        layers[0]!["materials"]!.AsArray().Add(new JsonObject { ["role"] = "effect" });
        Assert.False(Routes.ProbePrefix(loop, groups, layers));
    }

    [Fact]
    public async Task EncodedWorkCountsCodedPixelsAndActualStreams() => await TestTemp.Run(async dir =>
    {
        var report = new JsonObject {
            ["status"] = "candidate_generated",
            ["plan"] = new JsonObject { ["settings"] = new JsonObject { ["fps_numerator"] = 60,
                ["fps_denominator"] = 1 } },
            ["groups"] = new JsonArray(
                new JsonObject { ["status"] = "encoded", ["video_path"] = "a.mp4",
                    ["encoded_extent"] = new JsonArray(3072, 974), ["packed_alpha"] = false },
                new JsonObject { ["status"] = "encoded", ["video_path"] = "b.mp4",
                    ["encoded_extent"] = new JsonArray(6144, 1000), ["packed_alpha"] = true })
        };
        await BakeReportWriter.SaveAsync(Path.Combine(dir, "bake.json"), report, null, CancellationToken.None);
        Assert.Equal(2, report["encoded_video_work"]!["video_streams"]!.GetValue<int>());
        Assert.Equal((3072d * 974 + 6144d * 1000) * 60,
            report["encoded_video_work"]!["coded_pixels_per_second"]!.GetValue<double>());
    });

    // 别的脚本按名字取得到这张背景、观测里却没碰过它（取层在计时分支里，短观测没跑到）：完整捕获才会冒出这条依赖，
    // 烘焙时这一层的前缀就作废（#238 本机全集 3019976352、2931199278）。分析期就不提；观测里碰过、控制器可证的照常提。
    [Fact]
    public async Task UnobservedScriptLookupKeepsTheLayerOutOfThePrefix() => await TestTemp.Run(async dir =>
    {
        var (scene, runtime) = Background(dir);
        scene["objects"]![0]!["name"] = "background";
        scene["objects"]!.AsArray().Add(JsonNode.Parse("""
            {"id":2,"name":"controller","origin":{"value":"0 0 0",
             "script":"export function update(value) { if (engine.runtime > 30) thisScene.getLayer('background').visible = false; return value; }"}}
            """));
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(1, dir, dir, dir, 64, 48, 30, 1);
        Assert.Empty(EffectPrefixPlanner.Propose(scene, source, dir, runtime, new JsonObject(), request, new JsonObject()));
        runtime["runtime_dependencies"]!.AsArray().Add(new JsonObject { ["owner"] = 2, ["target"] = 1, ["operation"] = "lookup",
            ["property"] = "", ["initialization"] = false });
        Assert.Single(EffectPrefixPlanner.Propose(scene, source, dir, runtime, new JsonObject(), request, new JsonObject()).OfType<JsonObject>());
        await Task.CompletedTask;
    });

    // 分析期完整区间复核与烘焙复核同一判据：短观测里没有、完整区间里别的对象在运行中改写这一层（计时分支里才写），前缀不成立。
    // 分析按它不采用这条前缀（PrefixCaptureProbes.CompleteCaptureAsync），结论与烘焙一致（#238 本机全集 3019976352、2931199278）。
    [Fact]
    public async Task LateWriteInTheCompleteCaptureVoidsThePrefix() => await TestTemp.Run(async dir =>
    {
        var (scene, runtime) = Background(dir);
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(1, dir, dir, dir, 64, 48, 30, 1);
        Assert.True(EffectPrefixBakeService.SurvivesCompleteCapture(scene, source, request, runtime, new JsonObject(), new JsonObject(), 1, 1));
        var full = runtime.DeepClone().AsObject();
        full["runtime_dependencies"]!.AsArray().Add(new JsonObject { ["owner"] = 2, ["target"] = 1, ["operation"] = "write",
            ["property"] = "origin", ["initialization"] = false });
        Assert.False(EffectPrefixBakeService.SurvivesCompleteCapture(scene, source, request, full, new JsonObject(), new JsonObject(), 1, 1));
        JsonObject late = Assert.Single(EffectPrefixBakeService.LateDependencyRejection(1, 90, new JsonObject(), full, runtime)["late_dependencies"]!
            .AsArray().OfType<JsonObject>());
        Assert.Equal("origin", late["property"]!.GetValue<string>());
        await Task.CompletedTask;
    });

    // 烘焙时完整捕获推翻了某层的前缀：只记这一层的拒绝（带上完整捕获里新出现、涉及这一层的依赖），不抛异常让整张失败。
    [Fact]
    public void LateDependencyRejectionListsOnlyTheNewEdgesOfThatLayer()
    {
        JsonObject Edge(int owner, int target, bool initialization) => new() { ["owner"] = owner, ["target"] = target,
            ["operation"] = "write", ["property"] = "visible", ["initialization"] = initialization };
        var analyzed = new JsonObject { ["runtime_dependencies"] = new JsonArray(Edge(2, 25, true)) };
        var full = new JsonObject { ["runtime_dependencies"] = new JsonArray(Edge(2, 25, true), Edge(2, 25, false), Edge(3, 4, false)) };
        JsonObject group = EffectPrefixBakeService.LateDependencyRejection(25, 90, new JsonObject(), full, analyzed);
        Assert.Equal("rejected_late_dependency", group["status"]!.GetValue<string>());
        JsonObject late = Assert.Single(group["late_dependencies"]!.AsArray().OfType<JsonObject>());
        Assert.False(late["initialization"]!.GetValue<bool>());
    }

    // 交互关（成品就是 WPE 里鼠标不动、没有声音时的画面）：读指针的效果与音频效果一样从场景里去掉，不再把整张背景剔除。
    // 交互保留时读指针的效果不在可去掉之列（它不是音频效果）。
    [Fact]
    public async Task InteractionOffOmitsPointerEffectsInsteadOfTheLayer() => await TestTemp.Run(async dir =>
    {
        var (scene, runtime) = Background(dir);
        using var source = new ProjectSource(dir);
        JsonObject off = PlanTransforms.DescribeAudioEffectChoice(scene, source, dir, new JsonObject(), runtime, "preserve", interactionOff: true);
        Assert.Equal("applied", off["status"]!.GetValue<string>());
        Assert.Equal("interaction_off", off["trigger"]!.GetValue<string>());
        JsonObject omitted = Assert.Single(off["omitted_effects"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(1, omitted["effect_index"]!.GetValue<int>());
        PlanTransforms.ApplyAudioEffectChoice(scene, new JsonObject { ["audio_effects_choice"] = off });
        Assert.Equal([10], scene["objects"]![0]!["effects"]!.AsArray().Select(effect => effect!["id"]!.GetValue<int>()));
        JsonObject keep = PlanTransforms.DescribeAudioEffectChoice(Background(dir).Scene, source, dir, new JsonObject(), runtime, "preserve");
        Assert.Equal("not_needed", keep["status"]!.GetValue<string>());
        await Task.CompletedTask;
    });

    // 采用去掉效果的选择时，观测前把改过的场景写进解包副本：副本里已经有原作的 scene.json 与 project.json，
    // 按新建写就抛 "already exists"（#238 本机全集 40 张分析因此崩溃）。这里只到写副本为止，渲染器是占位文件、观测本身起不来。
    [Fact]
    public async Task ChosenSceneOverwritesTheExtractedCopy() => await TestTemp.Run(async dir =>
    {
        string project = Path.Combine(dir, "project"), tools = Path.Combine(dir, "tools"), output = Path.Combine(dir, "out");
        var (scene, runtime) = Background(project);
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(output);
        foreach (string tool in new[] { "renderer", "ffmpeg", "ffprobe" }) await File.WriteAllTextAsync(Path.Combine(tools, tool), "placeholder");
        using var source = new ProjectSource(project);
        runtime["source"] = source.SourcePath;
        string trace = Path.Combine(dir, "trace.json");
        await File.WriteAllTextAsync(trace, runtime.ToJsonString());
        var request = new HybridAnalyzeRequest(1, project, project, output, 64, 48, 30, 1, RuntimeTraceFile: trace, Interaction: "off");
        var observer = new NativeRuntimeObserver(new NativeTools(Path.Combine(tools, "renderer"), Path.Combine(tools, "ffmpeg"), Path.Combine(tools, "ffprobe"), []));
        Exception? error = await Record.ExceptionAsync(() => RuntimeObservation.ObserveAsync(request, source, "hash", scene, new JsonObject(), new JsonObject(),
            new SceneGraph(scene), output, observer, null, CancellationToken.None));
        Assert.DoesNotContain("already exists", error?.Message ?? "");
        JsonObject chosen = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "audio-choice-source", "scene.json")))!.AsObject();
        Assert.Equal([10], chosen["objects"]![0]!["effects"]!.AsArray().Select(effect => effect!["id"]!.GetValue<int>()));
    });
}
