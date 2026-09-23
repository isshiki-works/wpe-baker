using System.Text.Json.Nodes;
using Baker.Core;

internal static class ExactVideoLoopChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "exact-video-loop-source");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(sourceDirectory, "scene.json"), "{\"objects\":[]}");
        using var source = new ProjectSource(sourceDirectory);

        JsonObject Trace() => new() {
            ["source_owner_layer_id"] = 1, ["mechanism"] = "video", ["track_name"] = "clip",
            ["duration_seconds"] = "3.336666666666666666666", ["duration_numerator"] = 1001,
            ["duration_denominator"] = 300, ["looping"] = true, ["event_driven"] = false,
            ["confidence"] = "high", ["playback_rate"] = 1 };
        JsonObject Analyze(JsonObject trace, JsonObject? owner = null) => LoopAnalysis.Analyze(
            new JsonObject { ["objects"] = new JsonArray { owner?.DeepClone() ?? new JsonObject { ["id"] = 1 } } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray { trace.DeepClone() } }, [1], 30000, 1001).ToJson();

        JsonObject exact = Analyze(Trace());
        JsonObject candidate = exact["candidates"]!.AsArray().First()!.AsObject();
        check(candidate["frames"]!.GetValue<ulong>() == 100 && exact["fixed_frame_step"]!.GetValue<ulong>() == 100 &&
            !candidate["patches"]!.AsArray().OfType<JsonObject>().Any(x => x["kind"]?.GetValue<string>() == "animation_rate"),
            "a video uses rational native duration metadata on the output frame grid and emits no layer-rate patch");

        JsonObject shortVideo = Trace(); shortVideo["duration_numerator"] = 344; shortVideo["duration_denominator"] = 60;
        JsonObject shortResult = LoopAnalysis.Analyze(new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }) },
            source, null, new JsonObject { ["runtime_animation_periods"] = new JsonArray(shortVideo) }, [1], 60, 1).ToJson();
        check(shortResult["candidates"]![0]!["frames"]!.GetValue<ulong>() == 344,
            "a 344-frame source video is captured at its one-cycle period rather than an arbitrary ten-second multiple");

        JsonObject fractionalShort = Trace(); fractionalShort["duration_numerator"] = 100100000; fractionalShort["duration_denominator"] = 300210000;
        JsonObject fractionalShortResult = Analyze(fractionalShort);
        check(fractionalShortResult["candidates"]!.AsArray().Single()!["frames"]!.GetValue<ulong>() == 10 &&
            fractionalShortResult["candidates"]![0]!["patches"]!.AsArray().OfType<JsonObject>().Single()["kind"]!.GetValue<string>() == "video_rate",
            "a sub-ten-second fractional-FPS video can use its nearest bounded rate-adjusted output frame");

        JsonObject ordinary = Trace(); ordinary["duration_numerator"] = 1199; ordinary["duration_denominator"] = 60;
        ordinary["confidence"] = "medium"; ordinary["event_driven"] = null; ordinary["dynamic_controlled"] = null;
        JsonObject ordinaryResult = LoopAnalysis.Analyze(new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }) },
            source, null, new JsonObject { ["runtime_animation_periods"] = new JsonArray(ordinary) }, [1], 60, 1).ToJson();
        check(ordinaryResult["candidates"]![0]!["frames"]!.GetValue<ulong>() == 1199 &&
            ordinaryResult["candidates"]![0]!["patches"]!.AsArray().Count == 0,
            "an uncontrolled 1199-frame video selects its source period without an unnecessary rate override");

        JsonObject hacker = Trace(); hacker["duration_numerator"] = 493493; hacker["duration_denominator"] = 24000;
        JsonObject hackerResult = LoopAnalysis.Analyze(new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }) },
            source, null, new JsonObject { ["runtime_animation_periods"] = new JsonArray(hacker) }, [1], 60, 1).ToJson();
        JsonObject hackerCandidate = hackerResult["candidates"]!.AsArray().Single()!.AsObject();
        JsonObject hackerPatch = hackerCandidate["patches"]!.AsArray().OfType<JsonObject>().Single();
        check(hackerCandidate["frames"]!.GetValue<ulong>() == 1234 && hackerPatch["kind"]!.GetValue<string>() == "video_rate" &&
            hackerPatch["rate_numerator"]!.GetValue<long>() == 493493 && hackerPatch["rate_denominator"]!.GetValue<long>() == 493600 &&
            Math.Abs(hackerPatch["delta_percent"]!.GetValue<double>()) < 2,
            "a single exact video uses its nearest output frame with a bounded exact capture-rate override");

        JsonObject missingExact = Trace(); missingExact.Remove("duration_numerator"); missingExact.Remove("duration_denominator");
        check(Analyze(missingExact)["candidates"]!.AsArray().Count == 0 &&
            Analyze(missingExact)["unresolved"]!.AsArray().OfType<JsonObject>().Any(x => x["kind"]?.GetValue<string>() == "runtime_video"),
            "a source decimal duration string never becomes an exact video period");

        JsonObject controlled = Trace(); controlled["dynamic_controlled"] = true;
        check(Analyze(controlled)["candidates"]!.AsArray().Count == 0,
            "a dynamically controlled video remains unresolved even with exact timing metadata");
        JsonObject hackerControlled = hacker.DeepClone().AsObject(); hackerControlled["dynamic_controlled"] = true;
        check(LoopAnalysis.Analyze(new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }) }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray(hackerControlled) }, [1], 60, 1).ToJson()["candidates"]!.AsArray().Count == 0,
            "a video forbidden from retiming does not receive a nearest-frame override");

        JsonObject missingRate = Trace(); missingRate.Remove("playback_rate");
        check(Analyze(missingRate)["candidates"]!.AsArray().Count == 0 &&
            Analyze(missingRate)["unresolved"]!.AsArray().OfType<JsonObject>().Any(x => x["kind"]?.GetValue<string>() == "runtime_video"),
            "a video with no native playback-rate evidence remains unresolved");

        JsonObject scriptControlled = Analyze(Trace(), new JsonObject { ["id"] = 1,
            ["script"] = "const video = thisLayer.getVideoTexture(); video.setCurrentTime(4);" });
        check(scriptControlled["candidates"]!.AsArray().Count == 0 &&
            scriptControlled["unresolved"]!.AsArray().OfType<JsonObject>().Any(x => x["kind"]?.GetValue<string>() == "runtime_video"),
            "source getVideoTexture seek control rejects an otherwise exact looping video");

        // A sprite flipbook restarted after a random delay has no period at any capture length, which
        // is a stronger refusal than ordinary script control and must read differently to the user.
        JsonObject SpriteTrace() => new() {
            ["source_owner_layer_id"] = 1, ["mechanism"] = "sprite", ["track_name"] = "flipbook",
            ["duration_seconds"] = 2, ["looping"] = true, ["event_driven"] = false,
            ["confidence"] = "high", ["playback_rate"] = 1 };
        string SpriteRefusal(JsonObject result) => result["unresolved"]!.AsArray().OfType<JsonObject>()
            .Single(x => x["kind"]?.GetValue<string>() == "runtime_animation")["detail"]!.GetValue<string>();
        JsonObject randomSprite = Analyze(SpriteTrace(), new JsonObject { ["id"] = 1,
            ["visible"] = new JsonObject { ["value"] = true, ["script"] =
                "const ani = thisLayer.getTextureAnimation();\n" +
                "engine.setTimeout(() => { ani.stop(); engine.setTimeout(() => ani.play(), Math.random() * 5000); }, 16);" } });
        check(SpriteRefusal(randomSprite).Contains("Math.random() delay", StringComparison.Ordinal) &&
            SpriteRefusal(randomSprite).Contains("no seam placement", StringComparison.Ordinal),
            "a sprite restarted after a Math.random() delay is refused as having no period at any capture length");
        JsonObject steadySprite = Analyze(SpriteTrace(), new JsonObject { ["id"] = 1,
            ["visible"] = new JsonObject { ["value"] = true, ["script"] =
                "const ani = thisLayer.getTextureAnimation();\nani.play();" } });
        check(SpriteRefusal(steadySprite).Contains("full capture and seam validation", StringComparison.Ordinal),
            "deterministic sprite playback control keeps the softer capture-and-validate refusal");

        JsonObject externalControl = LoopAnalysis.Analyze(new JsonObject { ["objects"] = new JsonArray {
                new JsonObject { ["id"] = 1 },
                new JsonObject { ["id"] = 2, ["script"] = "thisScene.getLayer(1).getVideoTexture().pause();" }
            } }, source, null, new JsonObject { ["runtime_animation_periods"] = new JsonArray { Trace() } }, [1], 30000, 1001).ToJson();
        check(externalControl["candidates"]!.AsArray().Count == 0 &&
            externalControl["unresolved"]!.AsArray().OfType<JsonObject>().Any(x => x["kind"]?.GetValue<string>() == "runtime_video"),
            "a different source layer's video playback control rejects the target video");

        var loop = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["patches"] = new JsonArray {
            new JsonObject { ["kind"] = "video_rate", ["owner_layer_id"] = 1, ["rate_numerator"] = 99, ["rate_denominator"] = 100 },
            new JsonObject { ["kind"] = "video_rate", ["owner_layer_id"] = 2, ["rate_numerator"] = 101, ["rate_denominator"] = 100 }
        } }) };
        JsonArray? scoped = (JsonArray?)typeof(HybridBakeService).GetMethod("SelectVideoRateOverrides",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [loop, new HashSet<int> { 1 }]);
        check(scoped is { Count: 1 } && scoped[0]!["owner_layer_id"]!.GetValue<int>() == 1,
            "a group renderer receives no video rate override for retained live owners");
        var confirm = typeof(NativeRenderRunner).GetMethod("ConfirmVideoRateOverrides",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var applied = scoped![0]!.DeepClone().AsObject();
        applied["applied_control_count"] = 1;
        var native = new JsonObject { ["runtime_video_rate_overrides"] = new JsonArray(applied) };
        var request = new RenderRequest("source", "assets", "output", 1, 1, 60, 1, 1, OfflineVideoRateOverrides: scoped);
        confirm.Invoke(null, [request, RenderResult.Parse(native.ToJsonString())]);
        native["runtime_video_rate_overrides"]![0]!["rate_denominator"] = 99;
        bool rejected = false;
        try { confirm.Invoke(null, [request, RenderResult.Parse(native.ToJsonString())]); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
        check(rejected, "native video rate evidence accepts integer JSON representations but rejects a different exact rate");

        ContentCadence(check, source);
    }

    /// <summary>
    /// 片源自身的帧率慢于输出帧率时，捕获会把每个内容帧原样重复若干输出帧。运行时探针早就记录了 frame_count，
    /// 这里确认它被带进计划、周期取内容帧的整数倍。接缝校验以原作为参照，不再需要节拍。
    /// </summary>
    private static void ContentCadence(Action<bool, string> check, ProjectSource source)
    {
        JsonObject Clip(int? frameCount) => new() {
            ["source_owner_layer_id"] = 17, ["mechanism"] = "video", ["track_name"] = "wallpaper",
            ["duration_seconds"] = 30.0, ["duration_numerator"] = 30, ["duration_denominator"] = 1,
            ["frame_count"] = frameCount, ["playback_rate"] = 1, ["looping"] = true, ["playback_mode"] = "loop",
            ["event_driven"] = null, ["confidence"] = "medium" };
        JsonObject Cadence(int? frameCount) => LoopAnalysis.Analyze(
            new JsonObject { ["objects"] = new JsonArray { new JsonObject { ["id"] = 17 } } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray { Clip(frameCount) } }, [17], 60, 1).ToJson();

        JsonObject divides = Cadence(900);
        check(divides["candidates"]!.AsArray().First()!["frames"]!.GetValue<ulong>() == 1800 &&
            divides["content_cadence"]!["capture_frames_per_content_frame"]!.GetValue<long>() == 2,
            "a 900-frame 30-second clip closes at 1800 output frames and repeats each content frame twice at 60 fps");
        JsonObject entry = divides["content_cadence"]!["clips"]!.AsArray().Single()!.AsObject();
        check(entry["owner_layer_id"]!.GetValue<int>() == 17 && entry["clip_fps_numerator"]!.GetValue<long>() == 30 &&
            entry["clip_fps_denominator"]!.GetValue<long>() == 1,
            "the probed clip frame count reaches the plan as the clip's own exact frame rate");
        check(Cadence(null)["content_cadence"]!["capture_frames_per_content_frame"]!.GetValue<long>() == 1 &&
            Cadence(899)["content_cadence"]!["capture_frames_per_content_frame"]!.GetValue<long>() == 1,
            "a missing frame count or a clip rate that does not divide the output rate claims no cadence");
    }
}
