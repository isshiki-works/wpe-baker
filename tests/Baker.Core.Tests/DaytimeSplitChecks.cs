using System.Text.Json.Nodes;
using Baker.Core;

/// feat/daytime-split：状态选择器的静态识别，以及按状态规划时的分配变化。
/// 识别只看"visible 绑定上读时钟、只写 visible、按小时段切图层组"的脚本模式，不按壁纸 id 特判；
/// 任何一条不满足就退回现有行为，原因如实写进 plan 的 daytime_split.fallback_reason。
internal static class DaytimeSplitChecks
{
    // Wallpaper Engine 社区昼夜模板的最小版：两组图层名字面量 + 一个小时段分支 + else 兜底。
    private const string Selector = """
        'use strict';
        var dayLayers = ["day1","day2"];
        var nightLayers = ["night1"];
        var daytime = 8, nighttime = 20;
        export function init() { dayLayers = dayLayers.map(l => thisScene.getLayer(l)); nightLayers = nightLayers.map(l => thisScene.getLayer(l)); }
        function hideAll() { [].concat(dayLayers, nightLayers).forEach(l => l.visible = false); }
        function showLayers(a) { hideAll(); a.forEach(l => l.visible = true); }
        export function update() { var h = new Date().getHours(); if (h >= daytime && h < nighttime) { showLayers(dayLayers); } else { showLayers(nightLayers); } }
        """;

    private static JsonObject Layer(int id, string name) => new() { ["id"] = id, ["name"] = name,
        ["image"] = "models/" + name + ".json", ["size"] = "64 32", ["origin"] = "32 16 0" };

    /// 背景 + 选择器（不绘制）+ 白天两层 + 夜间一层。script 为 null 时选择器没有可见性脚本。
    private static JsonArray Scene(string? script = Selector, string secondDayLayer = "day2")
    {
        var controller = new JsonObject { ["id"] = 1, ["name"] = "ctl", ["origin"] = "32 16 0" };
        if (script is not null) controller["visible"] = new JsonObject { ["value"] = true, ["script"] = script };
        return new JsonArray(
            new JsonObject { ["id"] = 5, ["name"] = "bg", ["image"] = "models/bg.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" },
            controller, Layer(10, "day1"), Layer(11, secondDayLayer), Layer(20, "night1"));
    }

    private static Dictionary<int, JsonObject> ById(JsonArray objects) =>
        objects.OfType<JsonObject>().ToDictionary(obj => obj["id"]!.GetValue<int>());

    private static bool HoursAre(DaytimeSplit.State state, int[][] expected) =>
        state.Hours.Length == expected.Length &&
        state.Hours.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second));

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        DaytimeSplit.Detection recognized = DaytimeSplit.Detect(ById(Scene()));
        DaytimeSplit.State? day = recognized.StateNamed("day"), night = recognized.StateNamed("night");
        check(recognized.Status == "recognized" && recognized.ControllerId == 1 &&
            recognized.ThresholdSource == "script_defaults" && recognized.FallbackReason is null &&
            recognized.ControlledLayerIds.SequenceEqual(new[] { 10, 11, 20 }) &&
            recognized.States.Length == 2 && day is not null && night is not null &&
            HoursAre(day, [[8, 20]]) && day.VisibleLayerIds.SequenceEqual(new[] { 10, 11 }) &&
            // 跨午夜的夜间状态按小时段出现顺序并成一个状态：先 [0,8) 再 [20,24)。
            HoursAre(night, [[0, 8], [20, 24]]) && night.VisibleLayerIds.SequenceEqual(new[] { 20 }),
            "昼夜模板脚本被识别成两个状态，阈值取脚本默认值");

        DaytimeSplit.Detection nonVisibility = DaytimeSplit.Detect(ById(Scene(
            Selector.Replace("l.visible = true", "l.alpha = 1", StringComparison.Ordinal))));
        check(nonVisibility.Status == "fallback" &&
            nonVisibility.FallbackReason?.StartsWith("writes_non_visibility", StringComparison.Ordinal) == true,
            "选择器写了 visible 以外的属性就退回");

        DaytimeSplit.Detection playback = DaytimeSplit.Detect(ById(Scene(Selector.Replace(
            "var h = new Date().getHours();",
            "var h = new Date().getHours(); thisScene.getLayer(\"day1\").play();", StringComparison.Ordinal))));
        check(playback.Status == "fallback" &&
            playback.FallbackReason?.StartsWith("calls_playback_method", StringComparison.Ordinal) == true,
            "选择器调用播放类方法就退回");

        DaytimeSplit.Detection unknownLayer = DaytimeSplit.Detect(ById(Scene(secondDayLayer: "dusk1")));
        check(unknownLayer.Status == "fallback" && unknownLayer.FallbackReason == "unknown_layer_name:day2",
            "脚本里的图层名在场景里找不到就退回并点名");

        DaytimeSplit.Detection noScript = DaytimeSplit.Detect(ById(Scene(script: null)));
        check(noScript.Status == "fallback" && noScript.FallbackReason == "no_visibility_script_reads_clock",
            "没有读时钟的可见性脚本时没有状态选择器可谈");

        // 端到端：选择器对三个受控层的 visible 写 + 一次时钟读，探测时刻恰好三层都不可见。
        string sourceDirectory = Path.Combine(root, "daytime-source");
        Directory.CreateDirectory(sourceDirectory);
        string scenePath = Path.Combine(sourceDirectory, "scene.json");

        static JsonObject VisibilityWrite(int target) => new() { ["owner"] = 1, ["target"] = target,
            ["operation"] = "write", ["property"] = "visible", ["initialization"] = false };
        static JsonArray Dependencies() => new(VisibilityWrite(10), VisibilityWrite(11), VisibilityWrite(20),
            new JsonObject { ["owner"] = 1, ["target"] = 1, ["operation"] = "input",
                ["property"] = "wall_clock", ["initialization"] = false });
        static JsonArray RuntimeLayers(JsonArray objects) =>
            new(objects.OfType<JsonObject>().Select(obj => {
                int id = obj["id"]!.GetValue<int>();
                return (JsonNode)new JsonObject {
                    // 受控层在观测里全是隐藏的：真实探测只能落在某一个时刻。
                    ["id"] = id, ["owner"] = id, ["visible"] = id is not (10 or 11 or 20),
                    // 选择器自己不绘制。
                    ["has_mesh"] = id != 1, ["effective_parallax_depth"] = new JsonArray(0, 0),
                    ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
                        ["uses_system_media_thumbnail"] = false,
                        ["active_uniforms"] = new JsonArray("g_ModelViewProjectionMatrix"),
                        ["textures"] = new JsonArray() }) };
            }).ToArray());

        async Task<JsonObject> PlanAsync(string name, bool split = false, string? state = null)
        {
            JsonArray sceneObjects = Scene();
            await File.WriteAllTextAsync(scenePath, new JsonObject {
                ["general"] = new JsonObject {
                    ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                    ["clearenabled"] = true },
                ["objects"] = sceneObjects.DeepClone() }.ToJsonString());
            string tracePath = Path.Combine(root, "daytime-trace-" + name + ".json");
            await File.WriteAllTextAsync(tracePath, new JsonObject {
                ["source"] = scenePath, ["status"] = "complete",
                ["runtime_dependencies"] = Dependencies(),
                ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
                ["runtime_animation_periods"] = new JsonArray(),
                ["runtime_layers"] = RuntimeLayers(sceneObjects) }.ToJsonString());
            return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, sourceDirectory, root, Path.Combine(root, "daytime-" + name), 64, 32,
                    RuntimeTraceFile: tracePath, DaytimeSplit: split, DaytimeState: state));
        }

        static string Allocation(JsonObject plan, int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id)["allocation"]!.GetValue<string>();
        static string[] Reasons(JsonObject plan, int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id)["reasons"]!.AsArray()
            .Select(reason => reason!.GetValue<string>()).ToArray();
        static int[] Excluded(JsonObject plan) => plan["excluded_layer_ids"]!.AsArray()
            .Select(id => id!.GetValue<int>()).ToArray();

        JsonObject plain = await PlanAsync("default");
        check(plain["daytime_split"] is null && plain["settings"]!["daytime_split"] is null &&
            plain["settings"]!["daytime_state"] is null &&
            Allocation(plain, 10) == "live" && Allocation(plain, 11) == "live" && Allocation(plain, 20) == "live" &&
            Reasons(plain, 10).Contains("written_by_live_controller") &&
            Reasons(plain, 20).Contains("written_by_live_controller") &&
            Reasons(plain, 1).Contains("wall_clock_api"),
            "开关默认关闭时 plan 不含 daytime 段，选择器与受控层照旧全判实时");

        JsonObject detected = await PlanAsync("detected", split: true);
        check(detected["daytime_split"]!["status"]!.GetValue<string>() == "recognized" &&
            detected["daytime_split"]!["states"]!.AsArray().Count == 2 &&
            detected["daytime_split"]!["controller_layer_id"]!.GetValue<int>() == 1 &&
            Allocation(detected, 10) == "live" && Allocation(detected, 20) == "live" &&
            Reasons(detected, 10).Contains("written_by_live_controller") &&
            Reasons(detected, 1).Contains("wall_clock_api") && Excluded(detected).Length == 0,
            "只开识别不选状态时 plan 记下两个状态，判定与关闭时一致");

        JsonObject dayPlan = await PlanAsync("day", split: true, state: "day");
        // 非本状态的受控层"保留但隐藏"（inactive）：不进视频、不实时、也不从成品里删——选择器脚本在成品里仍要 getLayer 找到它们。
        check(Allocation(dayPlan, 10) == "video" && Allocation(dayPlan, 11) == "video" &&
            Allocation(dayPlan, 20) == "inactive" && Excluded(dayPlan).Length == 0 &&
            !Reasons(dayPlan, 1).Contains("wall_clock_api") &&
            !Reasons(dayPlan, 1).Contains("observed_wall_clock"),
            "按 day 状态规划时白天层进视频、夜间层保留但隐藏，选择器不再是实时控制器");

        JsonObject nightPlan = await PlanAsync("night", split: true, state: "night");
        check(Allocation(nightPlan, 20) == "video" &&
            Allocation(nightPlan, 10) == "inactive" && Allocation(nightPlan, 11) == "inactive" &&
            Excluded(nightPlan).Length == 0 &&
            !Reasons(nightPlan, 1).Contains("wall_clock_api"),
            "按 night 状态规划时换成夜间层进视频，白天层整组保留但隐藏");

        bool rejectedUnknownState = false;
        try { _ = await PlanAsync("noon", split: true, state: "noon"); }
        catch (InvalidDataException) { rejectedUnknownState = true; }
        check(rejectedUnknownState, "脚本枚举不出的状态名被拒绝，不静默退回整幅实时");
    }
}
