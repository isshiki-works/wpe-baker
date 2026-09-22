using System.Text.Json.Nodes;
using Baker.Core;

internal static class HybridLoopGeneralizationChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "loop-generalization-source");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(sourceDirectory, "scene.json"), "{\"objects\":[]}");
        using var source = new ProjectSource(sourceDirectory);

        JsonObject AnonymousTrack(double seconds) => new() {
            ["source_owner_layer_id"] = 1, ["mechanism"] = "authored_track", ["track_name"] = null,
            ["duration_seconds"] = seconds, ["playback_rate"] = 1, ["looping"] = true,
            ["event_driven"] = false, ["confidence"] = "high" };
        JsonObject Sprite(double seconds) => new() {
            ["source_owner_layer_id"] = 1, ["mechanism"] = "sprite", ["track_name"] = "sprite",
            ["duration_seconds"] = seconds, ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" };
        JsonObject Analyze(JsonObject trace, uint fpsNumerator, uint fpsDenominator, bool particle = false)
        {
            var owner = new JsonObject { ["id"] = 1 };
            if (particle) owner["particle"] = "particles/test.json";
            return HybridLoopService.Analyze(new JsonObject { ["objects"] = new JsonArray { owner } }, source, null,
                new JsonObject { ["runtime_animation_periods"] = new JsonArray { trace } }, [1], fpsNumerator, fpsDenominator);
        }

        JsonObject anonymous = Analyze(AnonymousTrack(60), 60, 1);
        check(anonymous["candidates"]!.AsArray().First()!["frames"]!.GetValue<ulong>() == 3600 &&
            anonymous["fixed_frame_step"]!.GetValue<ulong>() == 3600,
            "an unnamed high-confidence authored track remains a fixed exact loop constraint");

        var scriptClock = new JsonObject { ["owner"] = 1, ["operation"] = "time", ["property"] = "frametime",
            ["binding"] = "origin", ["initialization"] = false };
        JsonObject ScriptLoop() => HybridLoopService.Analyze(new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }) },
            source, null, new JsonObject { ["runtime_animation_periods"] = new JsonArray(AnonymousTrack(3)),
                ["runtime_dependencies"] = new JsonArray(scriptClock.DeepClone()) }, [1], 60, 1);
        check(ScriptLoop()["unresolved"]!.AsArray().OfType<JsonObject>().Any(item =>
                item["kind"]?.GetValue<string>() == "script_time" && item["owner_layer_id"]?.GetValue<int>() == 1),
            "a three-second authored animation does not certify a script advancing the same layer's position with frametime");
        scriptClock["initialization"] = true;
        check(ScriptLoop()["unresolved"]!.AsArray().Count == 0,
            "a one-time initialization clock read does not create an ongoing script-state loop constraint");
        scriptClock["initialization"] = false; scriptClock["owner"] = 2;
        check(ScriptLoop()["unresolved"]!.AsArray().Count == 0,
            "a clock read in a retained live layer does not block a different baked layer's animation");

        JsonObject sprite60 = Analyze(Sprite(4.53), 60, 1);
        JsonObject sprite120 = Analyze(Sprite(4.53), 120, 1);
        check(sprite60["candidates"]!.AsArray().First()!["frames"]!.GetValue<ulong>() == 1359 &&
            sprite120["candidates"]!.AsArray().First()!["frames"]!.GetValue<ulong>() == 2718 &&
            !sprite60["unresolved"]!.AsArray().OfType<JsonObject>().Any(x =>
                x["detail"]?.GetValue<string>().Contains("playback_rate", StringComparison.Ordinal) == true),
            "sprite duration without playback_rate closes exactly at 60 and 120 FPS");

        JsonObject fractional = Analyze(Sprite(1.001), 30000, 1001);
        check(fractional["candidates"]!.AsArray().First()!["frames"]!.GetValue<ulong>() == 30 &&
            Math.Abs(fractional["candidates"]![0]!["seconds"]!.GetValue<double>() - 1.001) < 1e-12,
            "sprite duration stays on the exact rational frame grid at a custom fractional FPS");

        JsonObject particle = Analyze(Sprite(1), 60, 1, particle: true);
        check(particle["candidates"]!.AsArray().Count == 0 && particle["unresolved"]!.AsArray().OfType<JsonObject>().Any(x =>
                x["detail"]?.GetValue<string>().Contains("particle system", StringComparison.Ordinal) == true),
            "particle lifetime and sequence timing are not replaced by the material sprite texture period");

        // 具名理由是结构化字段，而写给用户的那句话走文案表：同一条 detail 带着自己的键与中英两版。
        JsonObject particleReason = particle["unresolved"]!.AsArray().OfType<JsonObject>()
            .Single(x => x["particle_nonperiodic_reason"] is not null);
        JsonObject particleLocalized = particleReason[PlanNarrative.DetailLocalized]!.AsObject();
        check(particleReason["particle_nonperiodic_reason"]!.GetValue<string>() == "particle_definition_unavailable" &&
            particleLocalized["key"]?.GetValue<string>() == "unresolved.particle_definition_unreadable" &&
            particleLocalized["en"]!.GetValue<string>().Contains("particle definition \"", StringComparison.Ordinal) &&
            particleLocalized["en"]!.GetValue<string>().Contains("could not be read", StringComparison.Ordinal) &&
            particleLocalized["en"]!.GetValue<string>().Contains("no random source can be ruled out", StringComparison.Ordinal) &&
            particleLocalized["zh"]!.GetValue<string>().Contains("粒子定义", StringComparison.Ordinal),
            "an unreadable particle definition is reported as a named reason whose detail resolves to both languages");

        var fixedHundred = AnonymousTrack(100); fixedHundred["source_owner_layer_id"] = 2;
        var controlledOwner = new JsonObject { ["id"] = 1, ["visible"] = new JsonObject { ["value"] = true,
            ["script"] = "export function update(v) { const a = thisLayer.getTextureAnimation(); engine.setTimeout(() => a.stop(), Math.random() * 8000); return v; }" } };
        var mixedScene = new JsonObject { ["objects"] = new JsonArray { controlledOwner, new JsonObject { ["id"] = 2 } } };
        var mixedRuntime = new JsonObject { ["runtime_animation_periods"] = new JsonArray { Sprite(1.68), fixedHundred } };
        JsonObject Mixed() => HybridLoopService.Analyze(mixedScene, source, null, mixedRuntime, [1, 2], 60, 1);
        var controlled = Mixed();
        // 上限是 --loop-max-seconds（默认 600 秒）：100 秒定长轨在上限内有 100、200 … 600 秒六个候选，选中的仍是 100 秒。
        check(controlled["fixed_frame_step"]!.GetValue<ulong>() == 6000 &&
            controlled["candidates"]!.AsArray().Select(x => x!["frames"]!.GetValue<ulong>())
                .SequenceEqual(new ulong[] { 6000, 12000, 18000, 24000, 30000, 36000 }) &&
            controlled["unresolved"]!.AsArray().OfType<JsonObject>().Any(x => x["owner_layer_id"]?.GetValue<int>() == 1) &&
            controlled["encoded_loop"]!.GetValue<string>() == "not_verified",
            "a script-scheduled 1.68-second sprite does not force a 100-second candidate to 2100 seconds");

        controlledOwner["visible"]!["script"] = "export function update(v) { thisLayer.getTextureAnimation().getFrame(); return v; }";
        check(Mixed()["fixed_frame_step"]!.GetValue<ulong>() == 126000,
            "read-only sprite inspection preserves the continuously playing texture period constraint");
        mixedRuntime["runtime_dependencies"] = new JsonArray { new JsonObject { ["owner"] = 2, ["target"] = 1,
            ["operation"] = "write", ["property"] = "textureAnimation", ["initialization"] = false } };
        check(Mixed()["fixed_frame_step"]!.GetValue<ulong>() == 6000,
            "observed external sprite playback control also removes the false fixed period");

        JsonObject mirroredTrace = AnonymousTrack(60); mirroredTrace["playback_mode"] = "mirror";
        check(Analyze(mirroredTrace, 60, 1)["candidates"]!.AsArray().First()!["frames"]!.GetValue<ulong>() == 7200,
            "mirror animation needs an outward and return traversal before repeating");

        var authoredControl = new JsonObject { ["owner"] = 1, ["target"] = 1,
            ["operation"] = "write", ["property"] = "animation", ["initialization"] = true };
        var authoredRuntime = new JsonObject { ["runtime_animation_periods"] = new JsonArray { AnonymousTrack(60) },
            ["runtime_dependencies"] = new JsonArray { authoredControl } };
        var authoredScene = new JsonObject { ["objects"] = new JsonArray { new JsonObject { ["id"] = 1 } } };
        var initialPhase = HybridLoopService.Analyze(authoredScene, source, null, authoredRuntime, [1], 60, 1);
        authoredControl["initialization"] = false;
        var ongoingControl = HybridLoopService.Analyze(authoredScene, source, null, authoredRuntime, [1], 60, 1);
        check(initialPhase["candidates"]!.AsArray().First()!["frames"]!.GetValue<ulong>() == 3600 &&
            ongoingControl["candidates"]!.AsArray().Count == 0 &&
            ongoingControl["unresolved"]!.AsArray().OfType<JsonObject>().Any(x => x["owner_layer_id"]?.GetValue<int>() == 1),
            "initial authored phase setup preserves its period while ongoing playback writes remain unresolved");

        JsonObject RoundedRate(double authoredRate) => new() { ["id"] = 3, ["animationlayers"] = new JsonArray {
            new JsonObject { ["id"] = 30, ["name"] = "float32-rate", ["rate"] = authoredRate } } };
        JsonObject roundedTrace = AnonymousTrack(7); roundedTrace["source_owner_layer_id"] = 3;
        roundedTrace["track_name"] = "float32-rate"; roundedTrace["playback_rate"] = .29;
        JsonObject rounded = HybridLoopService.Analyze(new JsonObject { ["objects"] = new JsonArray { RoundedRate(.28999999) } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray { roundedTrace } }, [3], 60, 1);
        JsonObject roundedPatch = rounded["candidates"]!.AsArray().First()!["patches"]!.AsArray().OfType<JsonObject>().Single();
        check(roundedPatch["kind"]?.GetValue<string>() == "animation_rate" &&
            Math.Abs(roundedPatch["old_value"]!.GetValue<double>() - .28999999) < 1e-12,
            "source and runtime rates with the same finite float32 value retime using the authored source value");

        JsonObject distinct = HybridLoopService.Analyze(new JsonObject { ["objects"] = new JsonArray { RoundedRate(.29000004) } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray { roundedTrace.DeepClone() } }, [3], 60, 1);
        check(distinct["unresolved"]!.AsArray().OfType<JsonObject>().Any(x =>
                x["detail"]?.GetValue<string>()?.Contains("No exact authored animation rate patch", StringComparison.Ordinal) == true),
            "genuinely different float32 source and runtime rates remain unresolved");

        VideoControlScopeChecks(check, source);
    }

    /// <summary>
    /// 脚本对视频播放的控制按目标定界：一段脚本控制它在初始化阶段解析到的那条视频轨，场景里别的视频轨
    /// 不跟着失去周期。目标解析不出来时才退回全场景连坐。夹具用一个两条视频轨的场景：
    /// 层 49 的脚本在 init 里拿到层 55 的 videoTexture 并调速，层 96 从头到尾没被任何人碰过。
    /// </summary>
    private static void VideoControlScopeChecks(Action<bool, string> check, ProjectSource source)
    {
        // 2002/375 秒、320 帧的 59.94 fps 片源：120 fps 下 640.64 帧，最近整帧是 641。
        JsonObject Clip(int ownerId) => new() {
            ["source_owner_layer_id"] = ownerId, ["mechanism"] = "video", ["track_name"] = "clip",
            ["duration_seconds"] = 5.338667, ["duration_numerator"] = 2002, ["duration_denominator"] = 375,
            ["frame_count"] = 320, ["playback_rate"] = 1.0, ["looping"] = true, ["playback_mode"] = "loop",
            ["event_driven"] = null, ["dynamic_controlled"] = null, ["confidence"] = "medium" };
        JsonObject Read(int owner, int target, bool initialization) => new() {
            ["owner"] = owner, ["target"] = target, ["operation"] = "read",
            ["property"] = "videoTexture", ["binding"] = "visible", ["initialization"] = initialization };
        const string Controller = "const v = thisScene.getLayer(id).getVideoTexture(); if (v.rate !== undefined) v.rate = 2;";
        JsonObject Controlling(int id) => new() { ["id"] = id,
            ["visible"] = new JsonObject { ["value"] = true, ["script"] = Controller } };
        JsonObject Scene(params JsonNode[] objects) => new() { ["objects"] = new JsonArray(objects) };
        JsonObject Analyze(JsonObject scene, JsonObject runtime, int bakedOwner) =>
            HybridLoopService.Analyze(scene, source, null, runtime, [bakedOwner], 120, 1);
        static bool Unresolved(JsonObject loop, int ownerId) => loop["unresolved"]!.AsArray().OfType<JsonObject>()
            .Any(x => x["kind"]?.GetValue<string>() == "runtime_video" && x["owner_layer_id"]?.GetValue<int>() == ownerId);
        static int[] Ids(JsonObject loop, string field) => loop["video_control_scope"]![field]!.AsArray()
            .Select(id => id!.GetValue<int>()).ToArray();

        JsonObject scopedScene = Scene(new JsonObject { ["id"] = 96 }, Controlling(49), new JsonObject { ["id"] = 55 });
        JsonObject scopedRuntime = new() {
            ["runtime_animation_periods"] = new JsonArray { Clip(96), Clip(55) },
            ["runtime_dependencies"] = new JsonArray { Read(49, 55, true) } };
        JsonObject untouched = Analyze(scopedScene, scopedRuntime, 96);
        JsonObject untouchedCandidate = untouched["candidates"]!.AsArray().First()!.AsObject();
        // 2002/375 秒在 120 fps 下每周期 640.64 帧，25 个周期才回到整帧：16016 帧、133.4667 秒、零调速。
        // 600 秒默认上限内还有它的 2、3、4 倍（32032 / 48048 / 64064 帧），选中的仍是最短的 16016 帧。
        check(!Unresolved(untouched, 96) && untouchedCandidate["frames"]!.GetValue<ulong>() == 16016 &&
            untouched["candidates"]!.AsArray().Select(x => x!["frames"]!.GetValue<ulong>()).SequenceEqual(new ulong[] { 16016, 32032, 48048, 64064 }) &&
            Math.Abs(untouchedCandidate["seconds"]!.GetValue<double>() - 2002d / 375 * 25) < 1e-9 &&
            untouchedCandidate["total_retime_cost_percent"]!.GetValue<double>() == 0,
            "a video track no script ever resolved keeps its exact rational period while another layer's script controls a different video");

        JsonObject target = Analyze(scopedScene, scopedRuntime, 55);
        // 同一份运行时证据，但场景里没有一段做播放控制的脚本时，读到 videoTexture 本身不是受控证据。
        JsonObject inspectingScene = Scene(new JsonObject { ["id"] = 96 }, new JsonObject { ["id"] = 49,
            ["visible"] = new JsonObject { ["value"] = true,
                ["script"] = "const v = thisScene.getLayer(id).getVideoTexture(); engine.log(v.duration);" } },
            new JsonObject { ["id"] = 55 });
        JsonObject inspected = Analyze(inspectingScene, scopedRuntime, 55);
        check(target["candidates"]!.AsArray().Count == 0 && Unresolved(target, 55) &&
            inspected["candidates"]!.AsArray().Count == 4 && !Unresolved(inspected, 55),
            "the video track a script read the videoTexture of stays controlled and keeps its runtime_video refusal");

        JsonObject unknownRuntime = new() {
            ["runtime_animation_periods"] = new JsonArray { Clip(96), Clip(55) },
            ["runtime_dependencies"] = new JsonArray { Read(49, 55, false) } };
        JsonObject unknown = Analyze(scopedScene, unknownRuntime, 96);
        check(unknown["candidates"]!.AsArray().Count == 0 && Unresolved(unknown, 96) &&
            unknown["video_control_scope"]!["scene_wide_fallback"]!.GetValue<bool>(),
            "a video control script that resolved no target during initialization locks every video track in the scene again");

        JsonObject selfScene = Scene(Controlling(96));
        JsonObject selfRuntime = new() { ["runtime_animation_periods"] = new JsonArray { Clip(96) },
            ["runtime_dependencies"] = new JsonArray() };
        JsonObject self = Analyze(selfScene, selfRuntime, 96);
        check(self["candidates"]!.AsArray().Count == 0 && Unresolved(self, 96) &&
            !self["video_control_scope"]!["scene_wide_fallback"]!.GetValue<bool>(),
            "a layer whose own script controls its own video is controlled without locking the rest of the scene");

        check(Ids(untouched, "resolved_targets").SequenceEqual(new[] { 49, 55 }) &&
            Ids(untouched, "unknown_scope_owners").Length == 0 &&
            Ids(unknown, "unknown_scope_owners").SequenceEqual(new[] { 49 }) &&
            Ids(self, "resolved_targets").SequenceEqual(new[] { 96 }),
            "the loop report states which video tracks are resolved targets and which control scripts have an unknown scope");
    }
}
