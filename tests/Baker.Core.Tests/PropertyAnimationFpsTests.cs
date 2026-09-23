using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 属性动画轨道（字段上的 animation，带 options.fps/length、没有 animationlayers）按 options.fps 调速：
// 读侧唯一定位才可调速、写侧 ApplyPatches 改对字段并校验旧值、一组互质周期在预算内求得出公共循环。

[Trait("Layer", "L0")]
public class PropertyAnimationFpsTests
{
    private static JsonObject Trace(int owner, string? name, long numerator, long denominator) => new() {
        ["source_owner_layer_id"] = owner, ["mechanism"] = "authored_track", ["track_name"] = name,
        ["duration_seconds"] = (double)numerator / denominator, ["duration_numerator"] = numerator, ["duration_denominator"] = denominator,
        ["playback_rate"] = 1, ["looping"] = true, ["playback_mode"] = "loop", ["confidence"] = "high" };

    private static RuntimeTrack ReadSingle(string objectJson, JsonObject trace)
    {
        var scene = new JsonObject { ["objects"] = new JsonArray(JsonNode.Parse(objectJson)) };
        var runtime = new JsonObject { ["runtime_animation_periods"] = new JsonArray(trace) };
        var items = new List<LoopUnresolved>();
        List<RuntimeTrack> tracks = RuntimeTrackReader.Read(scene, null!, null, runtime, [1], items,
            VideoControlScope.Resolve(scene, runtime), new ParticleStationarity.FrameClock(30, 1, 600), out _);
        Assert.Empty(items);
        return Assert.Single(tracks);
    }

    [Fact]
    public void UniqueNamedFieldAnimationIsRetimableThroughItsFps()
    {
        RuntimeTrack track = ReadSingle("""
            {"id":1,"alpha":{"value":1,"animation":{"options":{"fps":30,"length":360,"mode":"loop","name":"s1","wraploop":true},"c0":[]}},
             "origin":{"value":"0 0 0","animation":{"options":{"fps":30,"length":150,"mode":"loop","name":"t20","wraploop":true}}}}
            """, Trace(1, "s1", 12, 1));
        Assert.True(track.CanRetime);
        Assert.Equal(new AuthoredFpsLocation("/alpha/animation", 30), track.AuthoredFps);
    }

    [Fact]
    public void AnonymousFieldAnimationIsLocatedWhenItIsTheOnlyAnonymousOne()
    {
        RuntimeTrack track = ReadSingle("""
            {"id":1,"effects":[{"passes":[{"constantshadervalues":{"a/b":{"value":1,"animation":{"options":{"fps":24,"length":114,"mode":"mirror"}}}}}]}]}
            """, Trace(1, "", 19, 4));
        Assert.True(track.CanRetime);
        Assert.Equal("/effects/0/passes/0/constantshadervalues/a~1b/animation", track.AuthoredFps!.AnimationPath);
        // 指针（含数组下标与转义的键）能被 ApplyPatches 原样找回。
        var scene = new JsonObject { ["objects"] = new JsonArray(JsonNode.Parse("""
            {"id":1,"effects":[{"passes":[{"constantshadervalues":{"a/b":{"value":1,"animation":{"options":{"fps":24,"length":114,"mode":"mirror"}}}}}]}]}
            """)) };
        HybridLoopService.ApplyPatches(scene, Loop(24, 25, track.AuthoredFps.AnimationPath));
        Assert.Equal(25, scene["objects"]![0]!["effects"]![0]!["passes"]![0]!["constantshadervalues"]!["a/b"]!["animation"]!["options"]!["fps"]!.GetValue<double>());
    }

    [Fact]
    public void PuppetTrackDoesNotBorrowASameNamedFieldAnimation()
    {
        JsonObject trace = Trace(1, "s1", 12, 1);
        trace["mechanism"] = "puppet_bone";
        RuntimeTrack track = ReadSingle("""{"id":1,"alpha":{"animation":{"options":{"fps":30,"length":360,"name":"s1"}}}}""", trace);
        Assert.False(track.CanRetime);
    }

    [Theory]
    // 同名两条：定位不唯一。
    [InlineData("""{"id":1,"alpha":{"animation":{"options":{"fps":30,"length":360,"name":"s1"}}},"origin":{"animation":{"options":{"fps":30,"length":360,"name":"s1"}}}}""", 12, 1)]
    // 父子关系：渲染器要求父子 fps 相同，只改一条会拆散。
    [InlineData("""{"id":1,"alpha":{"animation":{"options":{"fps":30,"length":360,"name":"s1","children":["origin"]}}}}""", 12, 1)]
    [InlineData("""{"id":1,"alpha":{"animation":{"options":{"fps":30,"length":360,"name":"s1","parent":"origin"}}}}""", 12, 1)]
    // fps 缺省或不是正数：渲染器退回 30，场景里没有可改的值。
    [InlineData("""{"id":1,"alpha":{"animation":{"options":{"length":360,"name":"s1"}}}}""", 12, 1)]
    [InlineData("""{"id":1,"alpha":{"animation":{"options":{"fps":0,"length":360,"name":"s1"}}}}""", 12, 1)]
    // 观测时长 × fps 不是不小于 length 的整数帧：场景与观测对不上。
    [InlineData("""{"id":1,"alpha":{"animation":{"options":{"fps":30,"length":360,"name":"s1"}}}}""", 11, 1)]
    [InlineData("""{"id":1,"alpha":{"animation":{"options":{"fps":30,"length":360,"name":"s1"}}}}""", 1201, 100)]
    public void UnlocatableFieldAnimationStaysLocked(string objectJson, long numerator, long denominator)
    {
        RuntimeTrack track = ReadSingle(objectJson, Trace(1, "s1", numerator, denominator));
        Assert.False(track.CanRetime);
        Assert.Null(track.AuthoredFps);
    }

    private static JsonObject Loop(double oldFps, double newFps, string path = "/origin/animation") => new() { ["candidates"] = new JsonArray(new JsonObject {
        ["patches"] = new JsonArray(new LoopValuePatch("c", "animation_fps", 1, -1, -1, "fps", 0, null, oldFps, newFps)
            { AnimationPath = path }.ToJson()) }) };

    [Fact]
    public void ApplyPatchesRewritesOnlyTheLocatedFps()
    {
        var scene = JsonNode.Parse("""
            {"objects":[{"id":1,"alpha":{"animation":{"options":{"fps":30,"length":360}}},"origin":{"animation":{"options":{"fps":30,"length":150}}}}]}
            """)!.AsObject();
        HybridLoopService.ApplyPatches(scene, Loop(30, 30.6));
        Assert.Equal(30.6, scene["objects"]![0]!["origin"]!["animation"]!["options"]!["fps"]!.GetValue<double>());
        Assert.Equal(30, scene["objects"]![0]!["alpha"]!["animation"]!["options"]!["fps"]!.GetValue<double>());
    }

    [Fact]
    public void ApplyPatchesRejectsAChangedFps()
    {
        var scene = JsonNode.Parse("""{"objects":[{"id":1,"origin":{"animation":{"options":{"fps":24,"length":150}}}}]}""")!.AsObject();
        Assert.Throws<InvalidDataException>(() => HybridLoopService.ApplyPatches(scene, Loop(30, 30.6)));
    }

    private static JsonObject AnalyzeTracks((long Numerator, long Denominator)[] periods, double maximumRetimePercent, out JsonObject scene)
    {
        var objects = new JsonArray();
        var traces = new JsonArray();
        for (int index = 0; index < periods.Length; ++index)
        {
            long frames = periods[index].Numerator * 30 / periods[index].Denominator;
            JsonNode owner = JsonNode.Parse("""{"origin":{"animation":{"options":{"fps":30,"mode":"loop"}}}}""")!;
            owner["id"] = index + 1;
            owner["origin"]!["animation"]!["options"]!["length"] = JsonNode.Parse(frames.ToString(System.Globalization.CultureInfo.InvariantCulture));
            owner["origin"]!["animation"]!["options"]!["name"] = $"t{index}";
            objects.Add(owner);
            traces.Add(Trace(index + 1, $"t{index}", periods[index].Numerator, periods[index].Denominator));
        }
        scene = new JsonObject { ["objects"] = objects };
        string root = Path.Combine(Path.GetTempPath(), "periodica-fps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            File.WriteAllText(Path.Combine(root, "scene.json"), scene.ToJsonString());
            using var source = new ProjectSource(root);
            return HybridLoopService.Analyze(scene, source, null, new JsonObject { ["runtime_animation_periods"] = traces },
                Enumerable.Range(1, periods.Length).ToArray(), 30, 1, maximumRetimePercent);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LockedSolutionWritesNoFpsPatch()
    {
        // 锁定求解就闭合（12 s 与 6 s）时倍率都是 1，不写 no-op 补丁，plan 与改前相同。
        JsonObject loop = AnalyzeTracks([(12, 1), (6, 1)], 5, out _);
        JsonObject candidate = Assert.IsType<JsonObject>(loop["candidates"]!.AsArray().First());
        Assert.Equal(360UL, candidate["frames"]!.GetValue<ulong>());
        Assert.Empty(candidate["patches"]!.AsArray());
    }

    [Fact]
    public void CoprimePropertyTracksCloseWithinTheRetimeBudget()
    {
        // 十条属性动画轨道，周期 {12,5,6,7,10,8,15,4,3.8,9} s，最小公倍数 47880 s 远超 600 s 上限；按 fps 调速后应有候选。
        (long Numerator, long Denominator)[] periods = [(12, 1), (5, 1), (6, 1), (7, 1), (10, 1), (8, 1), (15, 1), (4, 1), (19, 5), (9, 1)];
        JsonObject loop = AnalyzeTracks(periods, 5, out JsonObject scene);
        JsonObject candidate = Assert.IsType<JsonObject>(loop["candidates"]!.AsArray().First());
        double seconds = candidate["seconds"]!.GetValue<double>();
        Assert.InRange(seconds, 1, 600);
        // 改写后的场景里每条轨道的周期 End/fps' 在循环长度内走整数圈（接缝门在真实帧上验的正是这件事）。
        var patched = scene.DeepClone().AsObject();
        HybridLoopService.ApplyPatches(patched, loop);
        for (int index = 0; index < periods.Length; ++index)
        {
            JsonObject options = patched["objects"]![index]!["origin"]!["animation"]!["options"]!.AsObject();
            double period = options["length"]!.GetValue<double>() / (float)options["fps"]!.GetValue<double>();
            double cycles = seconds / period;
            Assert.True(Math.Abs(cycles - Math.Round(cycles)) < 1e-4, $"track {index}: {cycles} cycles");
            Assert.InRange(Math.Abs(options["fps"]!.GetValue<double>() / 30 - 1), 0, .05 + 1e-12);
        }
        Assert.Contains(candidate["patches"]!.AsArray().OfType<JsonObject>(), x => x["kind"]!.GetValue<string>() == "animation_fps");
    }
}
