using System.Text.Json.Nodes;
using Baker.Core;

/// 规则一：confidence=high、looping=false、playback_mode=single 的事件触发 authored 轨，其所属层判实时；加载即播的按入场进视频组。
/// 规则二：唯一视频组被搬不走的实时绘制挡在前面时，full_frame 布局不可达。
internal static class SingleShotAllocationChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "single-shot-source");
        Directory.CreateDirectory(sourceDirectory);
        // 一张静态背景 + 一个带轨道的提示框。轨道证据由每个用例分别给出。
        var objects = new JsonArray(
            new JsonObject { ["id"] = 10, ["name"] = "背景", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" },
            new JsonObject { ["id"] = 20, ["name"] = "提示框", ["image"] = "models/prompt.json",
                ["size"] = "16 8", ["origin"] = "32 16 0" });
        string scenePath = Path.Combine(sourceDirectory, "scene.json");
        await File.WriteAllTextAsync(scenePath, new JsonObject {
            ["general"] = new JsonObject {
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = true },
            ["objects"] = objects }.ToJsonString());

        static JsonArray RuntimeLayers(JsonArray objects) =>
            new(objects.OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
                ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(), ["visible"] = true,
                ["has_mesh"] = true, ["effective_parallax_depth"] = new JsonArray(0, 0),
                // 合并 fix/silent-rejections 后，"可证静态"要求完整的运行时材质证据：
                // 缺 uses_system_media_thumbnail 或 active_uniforms 就是证据不全，会被如实记成 source_static 障碍。
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
                    ["uses_system_media_thumbnail"] = false, ["active_uniforms"] = new JsonArray("g_ModelViewProjectionMatrix"),
                    ["textures"] = new JsonArray() })
            }).ToArray());

        async Task<JsonObject> PlanAsync(string name, JsonArray periods, JsonArray sceneObjects,
            JsonArray? dependencies = null, string layout = "full_frame", bool singleShotLive = false)
        {
            await File.WriteAllTextAsync(scenePath, new JsonObject {
                ["general"] = new JsonObject {
                    ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                    ["clearenabled"] = true },
                ["objects"] = sceneObjects.DeepClone() }.ToJsonString());
            string tracePath = Path.Combine(root, "single-shot-trace-" + name + ".json");
            await File.WriteAllTextAsync(tracePath, new JsonObject {
                ["source"] = scenePath, ["status"] = "complete",
                ["runtime_dependencies"] = dependencies?.DeepClone() ?? new JsonArray(),
                ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
                ["runtime_animation_periods"] = periods.DeepClone(),
                ["runtime_layers"] = RuntimeLayers(sceneObjects) }.ToJsonString());
            return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, sourceDirectory, root, Path.Combine(root, "single-shot-" + name), 64, 32,
                    RuntimeTraceFile: tracePath, VideoLayout: layout, SingleShotLive: singleShotLive));
        }

        static JsonObject Track(int owner, bool looping, string mode, string confidence, bool? eventDriven = null) => new() {
            ["source_owner_layer_id"] = owner, ["mechanism"] = "authored_track", ["track_name"] = null,
            ["duration_seconds"] = 17.0, ["playback_rate"] = 1.0, ["looping"] = looping,
            ["playback_mode"] = mode, ["event_driven"] = eventDriven, ["confidence"] = confidence, ["event_marker_count"] = 0 };
        static string Allocation(JsonObject plan, int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id)["allocation"]!.GetValue<string>();
        static string[] Reasons(JsonObject plan, int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id)["reasons"]!.AsArray()
            .Select(reason => reason!.GetValue<string>()).ToArray();
        static int[] GroupRoots(JsonObject plan) => plan["video_groups"]!.AsArray().OfType<JsonObject>()
            .SelectMany(group => group["root_ids"]!.AsArray().Select(id => id!.GetValue<int>())).ToArray();

        JsonObject single = await PlanAsync("single", new JsonArray(Track(20, false, "single", "high", eventDriven: true)), objects);
        check(Allocation(single, 20) == "live" && Reasons(single, 20).Contains("single_shot_animation") &&
            !GroupRoots(single).Contains(20) && GroupRoots(single).Contains(10) &&
            single["loop"]!["unresolved"]!.AsArray().Count == 0,
            "an event-driven high-confidence single-shot authored track keeps its layer live and out of every video group");

        // 加载即播的单次轨按入场处理：进视频组；入场切换退回旧行为（single_shot_live）时照旧判实时。
        JsonObject intro = await PlanAsync("intro", new JsonArray(Track(20, false, "single", "high")), objects);
        JsonObject introFallback = await PlanAsync("intro-fallback", new JsonArray(Track(20, false, "single", "high")), objects,
            singleShotLive: true);
        check(Allocation(intro, 20) == "video" && GroupRoots(intro).Contains(20) && !Reasons(intro, 20).Contains("single_shot_animation") &&
            Allocation(introFallback, 20) == "live" && Reasons(introFallback, 20).Contains("single_shot_animation"),
            "a load-played single-shot track joins the video group as an intro and stays live when the intro switch falls back");

        foreach (string mode in new[] { "loop", "mirror" })
        {
            JsonObject looping = await PlanAsync("looping-" + mode, new JsonArray(Track(20, true, mode, "high")), objects);
            check(Allocation(looping, 20) == "video" && !Reasons(looping, 20).Contains("single_shot_animation") &&
                GroupRoots(looping).Contains(20) && GroupRoots(looping).Contains(10),
                "a looping authored track still joins the video group: " + mode);
        }

        JsonObject medium = await PlanAsync("medium", new JsonArray(Track(20, false, "single", "medium")), objects);
        check(Allocation(medium, 20) == "video" && !Reasons(medium, 20).Contains("single_shot_animation") &&
            GroupRoots(medium).Contains(20) &&
            medium["loop"]!["unresolved"]!.AsArray().Any(item => item!["owner_layer_id"]!.GetValue<int>() == 20),
            "a single-shot track without high confidence is not concluded early and keeps its existing unresolved path");

        // 唯一可烘焙的内容就是那条一次性轨时：没有可成组的画面主体，给出既有的确定性拒绝。
        var promptOnly = new JsonArray(
            new JsonObject { ["id"] = 30, ["name"] = "时钟", ["text"] = new JsonObject {
                ["script"] = "export function update() { return new Date(); }" }, ["origin"] = "32 16 0" },
            new JsonObject { ["id"] = 20, ["name"] = "提示框", ["image"] = "models/prompt.json",
                ["size"] = "16 8", ["origin"] = "32 16 0" });
        JsonObject onlySingle = await PlanAsync("only-single", new JsonArray(Track(20, false, "single", "high", eventDriven: true)), promptOnly);
        check(onlySingle["video_groups"]!.AsArray().Count == 0 &&
            onlySingle["loop"]!["unresolved"]!.AsArray().Count == 0 &&
            onlySingle["loop"]!["candidates"]!.AsArray().Count == 0 &&
            onlySingle["video_layout_admission"]!["status"]!.GetValue<string>() == "not_applicable_no_video_group",
            "when the only bakeable layer is a single-shot track the plan states there is no input-independent visual group");

        // 一次性轨层排在一个实时根之后、自成第二组时：转 live 之后只剩承担清屏的那一组。
        var sandwich = new JsonArray(
            new JsonObject { ["id"] = 10, ["name"] = "背景", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" },
            new JsonObject { ["id"] = 30, ["name"] = "时钟", ["text"] = new JsonObject {
                ["script"] = "export function update() { return new Date(); }" }, ["origin"] = "8 8 0" },
            new JsonObject { ["id"] = 20, ["name"] = "提示框", ["image"] = "models/prompt.json",
                ["size"] = "16 8", ["origin"] = "32 16 0" });
        // 对照组走 layered：合并 feat/layout-demotion 之后，full_frame 下底组之上的视频 root 会被整体退回实时，
        // 第二组根本留不下来。这里要对照的是"medium 置信度时 20 仍会自成一组"，与全幅准入无关。
        JsonObject splitBaseline = await PlanAsync("split-medium", new JsonArray(Track(20, false, "single", "medium")),
            sandwich, layout: "layered");
        JsonObject splitSingle = await PlanAsync("split-single", new JsonArray(Track(20, false, "single", "high", eventDriven: true)), sandwich);
        var remainingGroup = splitSingle["video_groups"]!.AsArray().OfType<JsonObject>().Single();
        check(splitBaseline["video_groups"]!.AsArray().Count == 2 &&
            Allocation(splitSingle, 20) == "live" && splitSingle["video_groups"]!.AsArray().Count == 1 &&
            remainingGroup["root_ids"]!.AsArray().Select(id => id!.GetValue<int>()).SequenceEqual(new[] { 10 }) &&
            remainingGroup["include_scene_clear"]!.GetValue<bool>() &&
            splitSingle["blockers"]!.AsArray().Count == 0 &&
            splitSingle["video_layout_admission"]!["status"]!.GetValue<string>() == "planned_layout_allowed",
            "a single-shot track that used to form its own transparent second group leaves one opaque group behind");

        // 规则二：唯一组排在一个读 framebuffer 的全屏层与一个图像树之后，两者都不能提前景。
        // 30 是隐藏的底图：40 之前得有网格，读帧缓冲才读到真实内容而不是清屏色。
        var blocked = new JsonArray(
            new JsonObject { ["id"] = 30, ["name"] = "底图", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0", ["visible"] = false },
            new JsonObject { ["id"] = 40, ["name"] = "后处理层", ["image"] = "models/util/fullscreenlayer.json",
                ["size"] = "64 32", ["origin"] = "32 16 0", ["effects"] = new JsonArray(new JsonObject { ["name"] = "grade" }) },
            new JsonObject { ["id"] = 50, ["name"] = "时钟底板", ["image"] = "models/plate.json",
                ["size"] = "32 16", ["origin"] = "32 16 0",
                ["visible"] = new JsonObject { ["value"] = true, ["script"] = "export function update() { return new Date().getHours() > 0; }" } },
            new JsonObject { ["id"] = 10, ["name"] = "背景", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" });
        string blockedTrace = Path.Combine(root, "single-shot-trace-blocked.json");
        await File.WriteAllTextAsync(scenePath, new JsonObject {
            ["general"] = new JsonObject {
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = true },
            ["objects"] = blocked.DeepClone() }.ToJsonString());
        await File.WriteAllTextAsync(blockedTrace, new JsonObject {
            ["source"] = scenePath, ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(),
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(blocked.OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
                ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(), ["visible"] = obj["id"]!.GetValue<int>() != 30,
                ["has_mesh"] = true, ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
                    ["textures"] = new JsonArray(obj["id"]!.GetValue<int>() == 40 ? "_rt_FullFrameBuffer" : "background") })
            }).ToArray()) }.ToJsonString());
        JsonObject unreachable = await new HybridScenePlanner(new("not-started", "not-started", "not-started", []))
            .AnalyzeSingleAsync(new(2, sourceDirectory, root, Path.Combine(root, "single-shot-unreachable"), 64, 32,
                RuntimeTraceFile: blockedTrace));
        var unreachableGroup = unreachable["video_groups"]!.AsArray().OfType<JsonObject>().Single();
        check(unreachable["video_layout_admission"]!["status"]!.GetValue<string>() == "full_frame_unreachable" &&
            unreachableGroup["include_scene_clear"]!.GetValue<bool>() == false &&
            unreachableGroup["preceding_visible_live_roots"]!.AsArray().Select(id => id!.GetValue<int>())
                .SequenceEqual(new[] { 40, 50 }),
            "an unmovable realtime prefix makes full-frame unreachable and the reason names those layers without advising another analysis");

        // 可达情形：前置实时绘制全是独立的文本树，仍然是用户可选的冲突。
        var movable = new JsonArray(
            new JsonObject { ["id"] = 60, ["name"] = "时钟文字", ["text"] = new JsonObject {
                ["script"] = "export function update() { return new Date(); }" }, ["origin"] = "8 8 0" },
            new JsonObject { ["id"] = 10, ["name"] = "背景", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" });
        JsonObject movablePlan = await PlanAsync("movable", new JsonArray(), movable);
        check(movablePlan["video_layout_admission"]!["status"]!.GetValue<string>() == "requires_user_choice" &&
            movablePlan["occlusion_tradeoff"]!["promoted_roots"]!.AsArray()
                .Any(entry => entry!["root_id"]!.GetValue<int>() == 60),
            "a promotable text overlay in front of the only group stays a user choice with today's wording");
    }
}
