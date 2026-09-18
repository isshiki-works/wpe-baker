using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 视频外壳判据的离线检查：全部用注入的伪运行时观察跑 AnalyzeAsync，不需要渲染器也不需要 GPU。
/// 正例钉住"判定成立时只追加 blocker、不改道"，反例钉住"只要有一条不成立就照常放行"。
/// </summary>
internal static class VideoDominanceChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string clipSource = Path.Combine(root, "video-shell-source");
        WriteScene(clipSource, FullFrameClip());
        string shaderSource = Path.Combine(root, "video-shell-shader-source");
        WriteScene(shaderSource, ShaderClip());
        WriteShakeEffect(shaderSource);
        string partialSource = Path.Combine(root, "video-shell-partial-source");
        WriteScene(partialSource, PartialClip());

        string shellTrace = WriteTrace(root, "video-shell-trace.json", clipSource, RuntimeLayers(effect: false), VideoPeriods());
        string effectTrace = WriteTrace(root, "video-shell-effect-trace.json", clipSource, RuntimeLayers(effect: true), VideoPeriods());
        string unresolvedTrace = WriteTrace(root, "video-shell-unresolved-trace.json", clipSource, RuntimeLayers(effect: false),
            VideoPeriods(new JsonObject { ["source_owner_layer_id"] = 10, ["mechanism"] = "animation", ["track_name"] = "drift",
                ["duration_seconds"] = 2.0, ["playback_rate"] = 1.0, ["looping"] = true, ["confidence"] = "low" }));
        string shaderTrace = WriteTrace(root, "video-shell-shader-trace.json", shaderSource, RuntimeLayers(effect: false), VideoPeriods());
        string partialTrace = WriteTrace(root, "video-shell-partial-trace.json", partialSource, RuntimeLayers(effect: false), VideoPeriods());

        async Task<JsonObject> PlanAsync(string name, string source, string trace, string videoShell = VideoDominance.RejectChoice) =>
            await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, source, root, Path.Combine(root, name), 64, 32, RuntimeTraceFile: trace, VideoShell: videoShell));
        static string Status(JsonObject plan) => plan["video_dominant"]!["status"]!.GetValue<string>();
        static string Evidence(JsonObject plan) => string.Join(" | ",
            plan["video_dominant"]!["evidence"]!.AsArray().Select(item => item!.GetValue<string>()));
        static bool Blocked(JsonObject plan) => plan["blockers"]!.AsArray()
            .Any(item => item?.GetValue<string>() == VideoDominance.Blocker);

        JsonObject shell = await PlanAsync("video-shell-plan", clipSource, shellTrace);
        check(Status(shell) == VideoDominance.ShellStatus && Blocked(shell) &&
            shell["status"]!.GetValue<string>() == "requires_resolution" &&
            shell["video_dominant"]!["reason_zh"]!.GetValue<string>() == VideoDominance.BlockerZh,
            "one full-frame clip with no effect pass is reported as a video shell and blocks the plan with a bilingual reason");
        check(shell["route"]!.GetValue<string>() == "whole_layer" &&
            shell["effect_prefix_caches"]!.AsArray().Count == 0 &&
            shell["video_groups"]!.AsArray().Count == 1 &&
            shell["loop"]!["candidates"]!.AsArray().Count > 0,
            "the video-shell blocker is appended after routing, so the plan keeps its whole-layer route and grouping");
        check(Evidence(shell).Contains("periodic_components=video/10/clip/1/1", StringComparison.Ordinal) &&
            Evidence(shell).Contains("non_video_components=(none)", StringComparison.Ordinal) &&
            Evidence(shell).Contains("unresolved_components=0", StringComparison.Ordinal) &&
            Evidence(shell).Contains("retime_patches=0", StringComparison.Ordinal) &&
            Evidence(shell).Contains("opaque_group=group-1 layers=10", StringComparison.Ordinal) &&
            Evidence(shell).Contains("clip_layer_canvas_fraction=1 centre=0.5,0.5", StringComparison.Ordinal) &&
            Evidence(shell).Contains("baked_effect_layers=0 baked_effect_passes=0", StringComparison.Ordinal),
            "the video-shell record carries every structural condition it read, with no measurement or threshold");

        JsonObject allowed = await PlanAsync("video-shell-override-plan", clipSource, shellTrace, VideoDominance.AllowChoice);
        check(Status(allowed) == VideoDominance.OverrideStatus && !Blocked(allowed) &&
            allowed["video_dominant"]!["requested"]!.GetValue<string>() == VideoDominance.AllowChoice &&
            Evidence(allowed) == Evidence(shell) && allowed["status"]!.GetValue<string>() == "requires_loop_analysis",
            "an explicit video-shell override records the same evidence and keeps the plan unblocked");

        JsonObject withEffect = await PlanAsync("video-shell-effect-plan", clipSource, effectTrace);
        check(Status(withEffect) == VideoDominance.NotShellStatus && !Blocked(withEffect) &&
            Evidence(withEffect).Contains("baked_effect_layers=1 baked_effect_passes=1", StringComparison.Ordinal),
            "a baked layer carrying one effect pass is not a video shell: that per-frame work moves into the video");

        JsonObject withShaderPeriod = await PlanAsync("video-shell-shader-plan", shaderSource, shaderTrace);
        check(Status(withShaderPeriod) == VideoDominance.NotShellStatus && !Blocked(withShaderPeriod) &&
            !Evidence(withShaderPeriod).Contains("non_video_components=(none)", StringComparison.Ordinal),
            "one shader clock beside the clip is enough to keep the plan out of the video-shell verdict");

        JsonObject withUnresolved = await PlanAsync("video-shell-unresolved-plan", clipSource, unresolvedTrace);
        check(Status(withUnresolved) == VideoDominance.NotShellStatus && !Blocked(withUnresolved) &&
            !Evidence(withUnresolved).Contains("unresolved_components=0", StringComparison.Ordinal),
            "an unresolved temporal mechanism keeps the verdict on the permissive side");

        JsonObject partial = await PlanAsync("video-shell-partial-plan", partialSource, partialTrace);
        check(Status(partial) == VideoDominance.NotShellStatus && !Blocked(partial),
            "a clip that does not cover the centred canvas is not a video shell");

        JsonObject runtime = JsonNode.Parse(await File.ReadAllTextAsync(shellTrace))!.AsObject();
        static JsonObject Loop(JsonObject plan) => plan["loop"]!.AsObject();
        JsonObject prefixRoute = shell.DeepClone().AsObject();
        prefixRoute["route"] = "effect_prefix";
        check(VideoDominance.Evaluate(prefixRoute, runtime, VideoDominance.RejectChoice)["status"]!.GetValue<string>() ==
            VideoDominance.EffectPrefixStatus,
            "the effect-prefix route bakes the effect passes themselves, so the video-shell test does not apply to it");

        JsonObject retimed = shell.DeepClone().AsObject();
        Loop(retimed)["candidates"]![0]!["patches"]!.AsArray().Add(new JsonObject { ["kind"] = "animation_rate" });
        check(VideoDominance.Evaluate(retimed, runtime, VideoDominance.RejectChoice)["status"]!.GetValue<string>() ==
            VideoDominance.NotShellStatus,
            "a candidate that retimes authored rates to close is not a video shell");

        JsonObject aperiodic = shell.DeepClone().AsObject();
        Loop(aperiodic)["candidates"]!.AsArray().Clear();
        check(VideoDominance.Evaluate(aperiodic, runtime, VideoDominance.RejectChoice)["status"]!.GetValue<string>() ==
            VideoDominance.NotShellStatus,
            "a plan with no periodic component at all is not a video shell");

        JsonObject twoClips = shell.DeepClone().AsObject();
        Loop(twoClips)["candidates"]![0]!["components"]!.AsArray().Add(new JsonObject { ["id"] = "video/20/second/1/1" });
        check(VideoDominance.Evaluate(twoClips, runtime, VideoDominance.RejectChoice)["status"]!.GetValue<string>() ==
            VideoDominance.NotShellStatus,
            "two layers carrying periodic video components leave no single clip standing for the picture");

        JsonObject withTransparentGroup = shell.DeepClone().AsObject();
        withTransparentGroup["video_groups"]!.AsArray().Add(new JsonObject { ["id"] = "group-2",
            ["root_ids"] = new JsonArray(20), ["layer_ids"] = new JsonArray(20), ["transparent"] = true });
        withTransparentGroup["layers"]!.AsArray().Add(new JsonObject { ["id"] = 20, ["allocation"] = "video",
            ["canvas_fraction"] = 0.01, ["canvas_center_x"] = 0.1, ["canvas_center_y"] = 0.1 });
        JsonObject transparentRuntime = runtime.DeepClone().AsObject();
        transparentRuntime["runtime_layers"]!.AsArray().Add(new JsonObject { ["id"] = 20, ["owner"] = 20,
            ["has_effect_layer"] = false, ["materials"] = new JsonArray(new JsonObject { ["role"] = "source" }) });
        JsonObject transparentVerdict = VideoDominance.Evaluate(withTransparentGroup, transparentRuntime, VideoDominance.RejectChoice);
        check(transparentVerdict["status"]!.GetValue<string>() == VideoDominance.ShellStatus &&
            string.Join(" | ", transparentVerdict["evidence"]!.AsArray().Select(item => item!.GetValue<string>()))
                .Contains("opaque_groups=1 transparent_groups=1", StringComparison.Ordinal),
            "extra transparent groups of effect-free layers still leave the plan a video shell");

        JsonObject withoutEffectEvidence = runtime.DeepClone().AsObject();
        foreach (var layer in withoutEffectEvidence["runtime_layers"]!.AsArray().OfType<JsonObject>()) layer.Remove("has_effect_layer");
        check(VideoDominance.Evaluate(shell, withoutEffectEvidence, VideoDominance.RejectChoice)["status"]!.GetValue<string>() ==
            VideoDominance.NotShellStatus,
            "a runtime observation without effect-layer metadata proves nothing, so the plan is not called a video shell");

        try
        {
            VideoDominance.Evaluate(shell, runtime, "bake");
            throw new InvalidOperationException("Accepted an unknown video shell choice.");
        }
        catch (InvalidDataException) { check(true, "video shell handling accepts only reject or allow"); }
    }

    private static JsonArray FullFrameClip() => new(
        new JsonObject { ["id"] = 10, ["name"] = "clip", ["image"] = "models/clip.json",
            ["size"] = "64 32", ["origin"] = "32 16 0" });

    private static JsonArray PartialClip() => new(
        new JsonObject { ["id"] = 10, ["name"] = "clip", ["image"] = "models/clip.json",
            ["size"] = "16 8", ["origin"] = "32 16 0" });

    private static JsonArray ShaderClip() => new(
        new JsonObject { ["id"] = 10, ["name"] = "clip", ["image"] = "models/clip.json",
            ["size"] = "64 32", ["origin"] = "32 16 0",
            ["effects"] = new JsonArray(new JsonObject { ["file"] = "effects/shake.json",
                ["passes"] = new JsonArray(new JsonObject {
                    ["constantshadervalues"] = new JsonObject { ["speed"] = 1.25 } }) }) });

    private static void WriteScene(string directory, JsonArray objects)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(directory, "scene.json"), new JsonObject {
            ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = true },
            ["objects"] = objects }.ToJsonString());
    }

    private static void WriteShakeEffect(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "effects"));
        Directory.CreateDirectory(Path.Combine(directory, "materials"));
        Directory.CreateDirectory(Path.Combine(directory, "shaders"));
        File.WriteAllText(Path.Combine(directory, "effects", "shake.json"), "{\"passes\":[{\"material\":\"materials/shake.json\"}]}");
        File.WriteAllText(Path.Combine(directory, "materials", "shake.json"), "{\"passes\":[{\"shader\":\"shake\"}]}");
        File.WriteAllText(Path.Combine(directory, "shaders", "shake.frag"), """
            uniform float g_Time;
            // [COMBO] {"combo":"NOISE","type":"options","default":0}
            // [COMBO] {"combo":"AUDIOPROCESSING","type":"audioprocessingoptions","default":0}
            #if AUDIOPROCESSING == 0
            float flowPhase = 0.0;
            flowPhase = texSample2D(g_Texture2, v_TexCoord.zw).r * M_PI_2;
            #if NOISE
            vec4 sines = flowPhase + frac(g_Speed * g_Time / M_PI_2 * vec4(1, -0.16161616, 0.0083333, -0.00019841)) * M_PI_2;
            #else
            float time = g_Speed * g_Time + flowPhase;
            offset = sin(frac(time / M_PI_2) * M_PI_2);
            float base = step(0.0, cos(time));
            #endif
            #endif
            """);
    }

    private static JsonArray RuntimeLayers(bool effect)
    {
        var materials = new JsonArray(new JsonObject { ["shader"] = "genericimage4", ["role"] = "source",
            ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
            ["active_uniforms"] = new JsonArray(), ["textures"] = new JsonArray() });
        if (effect) materials.Add(new JsonObject { ["shader"] = "filmgrain", ["role"] = "effect",
            ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
            ["active_uniforms"] = new JsonArray(), ["textures"] = new JsonArray() });
        return new JsonArray(new JsonObject { ["id"] = 10, ["owner"] = 10, ["parent"] = -1, ["name"] = "clip",
            ["visible"] = true, ["has_mesh"] = true, ["has_effect_layer"] = effect, ["render_group"] = false,
            ["effective_parallax_depth"] = new JsonArray(0, 0), ["materials"] = materials });
    }

    private static JsonArray VideoPeriods(JsonObject? extra = null)
    {
        var periods = new JsonArray(new JsonObject { ["source_owner_layer_id"] = 10, ["mechanism"] = "video",
            ["track_name"] = "clip", ["duration_seconds"] = 1.0, ["duration_numerator"] = 1, ["duration_denominator"] = 1,
            ["frame_count"] = 30, ["playback_rate"] = 1.0, ["looping"] = true, ["playback_mode"] = "loop",
            ["event_driven"] = null, ["dynamic_controlled"] = null, ["confidence"] = "medium" });
        if (extra is not null) periods.Add(extra);
        return periods;
    }

    private static string WriteTrace(string root, string name, string source, JsonArray layers, JsonArray periods)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, new JsonObject {
            ["source"] = Path.Combine(source, "scene.json"), ["status"] = "complete",
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_dependencies"] = new JsonArray(),
            ["runtime_animation_periods"] = periods,
            ["runtime_layers"] = layers }.ToJsonString());
        return path;
    }
}
