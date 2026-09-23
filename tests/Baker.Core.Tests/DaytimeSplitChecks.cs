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

    private const string VideoSelector = """
        'use strict';
        var displayVideo = ["morning", "day", "dusk", "night", "cycle"];
        var electDisplay = false;
        var timeVarying = false;
        var morningtime = 5, daytime = 9, dusktime = 16, nighttime = 19;
        export function init() {
            displayVideo = displayVideo.map(video => thisScene.getLayer(video));
            // displayVideo.forEach(video => video.getVideoTexture().stop());
        }
        var playVideo = function(num) {
            displayVideo.forEach((video, i) => {
                if(i === num) { video.getVideoTexture().play(); video.visible = true; }
                else { video.getVideoTexture().pause(); video.visible = false; }
            });
        }
        export function update() {
            var time = new Date(); var hours = time.getHours();
            if(timeVarying) {
                if(hours >= morningtime && hours < daytime) { playVideo(0); }
                else if(hours >= daytime && hours < dusktime) { playVideo(1); }
                else if(hours >= dusktime && hours < nighttime) { playVideo(2); }
                else { playVideo(3); }
            }
            if(electDisplay && !timeVarying) {
                for(let i = 0; i < displayVideo.length; i++) { if(i == electDisplay) playVideo(i); }
            }
        }
        export function applyUserProperties(changedUserProperties) {
            if(changedUserProperties.hasOwnProperty('display')) { electDisplay = changedUserProperties.display; }
            if(changedUserProperties.hasOwnProperty('timevarying')) { timeVarying = changedUserProperties.timevarying; }
            if(changedUserProperties.hasOwnProperty('morningtime')) { morningtime = changedUserProperties.morningtime; }
            if(changedUserProperties.hasOwnProperty('daytime')) { daytime = changedUserProperties.daytime; }
            if(changedUserProperties.hasOwnProperty('dusktime')) { dusktime = changedUserProperties.dusktime; }
            if(changedUserProperties.hasOwnProperty('nighttime')) { nighttime = changedUserProperties.nighttime; }
        }
        """;

    private static JsonArray VideoScene(string script = VideoSelector) => new(
        new JsonObject { ["id"] = 1, ["name"] = "controller", ["visible"] = new JsonObject { ["value"] = true, ["script"] = script } },
        Layer(10, "morning"), Layer(11, "day"), Layer(20, "dusk"), Layer(21, "night"), Layer(22, "cycle"));

    private static JsonObject VideoProperties() => new() { ["timevarying"] = true, ["display"] = "0",
        ["morningtime"] = "4", ["daytime"] = "9", ["dusktime"] = "17", ["nighttime"] = "20" };

    private static void VideoSelectors(Action<bool, string> check)
    {
        var properties = VideoProperties();
        var detected = DaytimeSplit.Detect(ById(VideoScene()), properties: properties);
        check(detected.IsRecognized && detected.ControlsVideoPlayback && detected.ThresholdSource == "user_properties" &&
            detected.States.Length == 4 && detected.ControlledLayerIds.SequenceEqual(new[] { 10, 11, 20, 21, 22 }) &&
            HoursAre(detected.StateNamed("morning")!, [[4, 9]]) && HoursAre(detected.StateNamed("day")!, [[9, 17]]) &&
            HoursAre(detected.StateNamed("dusk")!, [[17, 20]]) && HoursAre(detected.StateNamed("night")!, [[0, 4], [20, 24]]),
            "视频选择器按属性阈值枚举四态，未参与时段的第五项仍为受控层");
        string upperInclusive = VideoSelector.Replace("hours >=", "hours >", StringComparison.Ordinal)
            .Replace("hours <", "hours <=", StringComparison.Ordinal);
        var inclusive = DaytimeSplit.Detect(ById(VideoScene(upperInclusive)), properties: properties);
        check(inclusive.IsRecognized && HoursAre(inclusive.StateNamed("morning")!, [[5, 10]]) &&
            HoursAre(inclusive.StateNamed("day")!, [[10, 18]]) && HoursAre(inclusive.StateNamed("dusk")!, [[18, 21]]) &&
            HoursAre(inclusive.StateNamed("night")!, [[0, 5], [21, 24]]),
            "getHours 整数值的 >low/<=high 与 >=low/<high 边界如实区分");
        var defaults = DaytimeSplit.Detect(ById(VideoScene(VideoSelector.Replace("timeVarying = false", "timeVarying = true", StringComparison.Ordinal))));
        check(defaults.IsRecognized && defaults.ThresholdSource == "script_defaults" && HoursAre(defaults.StateNamed("morning")!, [[5, 9]]),
            "没有属性快照时采用模板声明的开关与阈值默认值");
        string renamed = VideoSelector.Replace("'morningtime'", "'startHour'", StringComparison.Ordinal)
            .Replace("changedUserProperties.morningtime", "changedUserProperties.startHour", StringComparison.Ordinal);
        properties["startHour"] = "6";
        var aliased = DaytimeSplit.Detect(ById(VideoScene(renamed)), properties: properties);
        check(aliased.IsRecognized && HoursAre(aliased.StateNamed("morning")!, [[6, 9]]),
            "阈值变量按 applyUserProperties 的真实键名读取，不假定变量和属性同名");
        properties["timevarying"] = false;
        var manualZero = DaytimeSplit.Detect(ById(VideoScene()), properties: properties);
        check(manualZero.IsRecognized && manualZero.States is [{ Name: "morning" }] && HoursAre(manualZero.States[0], [[0, 24]]),
            "手动字符串零保持清晨单态，不枚举自动时段");
        properties["display"] = "4";
        var manualCycle = DaytimeSplit.Detect(ById(VideoScene()), properties: properties);
        check(manualCycle.IsRecognized && manualCycle.States is [{ Name: "cycle" }] && manualCycle.States[0].VisibleLayerIds is [22],
            "手动模式支持时钟分支外的第五项");
        properties["display"] = 0;
        check(DaytimeSplit.Detect(ById(VideoScene()), properties: properties).FallbackReason == "inactive_manual_selection",
            "手动数字零是 JS 假值，不能冒充调用了清晨选择器");
        foreach (string changed in new[] {
            VideoSelector.Replace(".play();", ".stop();", StringComparison.Ordinal),
            VideoSelector.Replace(".play();", ".play(); video.getVideoTexture().setCurrentTime(0);", StringComparison.Ordinal),
            VideoSelector.Replace("video.visible = true", "video.alpha = 1; video.visible = true", StringComparison.Ordinal),
            VideoSelector + "\nthisScene.destroyLayer('morning');",
            VideoSelector.Replace("playVideo(2)", "playVideo(9)", StringComparison.Ordinal),
            VideoSelector.Replace("morningtime = 5,", "morningtime = 5, morningtime = 6,", StringComparison.Ordinal)
        })
            check(!DaytimeSplit.Detect(ById(VideoScene(changed)), properties: VideoProperties()).IsRecognized,
                "视频模板拒绝额外副作用、错误索引及重复变量");
        JsonArray withDate = VideoScene();
        withDate.Insert(0, new JsonObject { ["id"] = 2, ["name"] = "Settings Date", ["visible"] = new JsonObject {
            ["script"] = "export function update() { return new Date().getDate() > 0; }" } });
        check(DaytimeSplit.Detect(ById(withDate), properties: VideoProperties()).ControllerId == 1,
            "非选择器 Date 候选不会抢走随后完整匹配的视频控制器");

        var frozen = new JsonObject { ["objects"] = VideoScene() };
        var plan = new JsonObject { ["settings"] = new JsonObject { ["daytime_state"] = "day" }, ["daytime_split"] = detected.ToJson() };
        DaytimeSplit.ApplyState(frozen, plan);
        var objects = ById(frozen["objects"]!.AsArray());
        check(objects[1]["visible"]?["script"] is null && objects[11]["visible"]?["value"]?.GetValue<bool>() == true &&
            objects[10]["visible"]?["value"]?.GetValue<bool>() == false &&
            objects[11]["visible"]?["script"]?.GetValue<string>().Contains("thisLayer.getVideoTexture().play()") == true &&
            objects[22]["visible"]?["script"]?.GetValue<string>().Contains("thisLayer.getVideoTexture().pause()") == true &&
            !frozen.ToJsonString().Contains("new Date", StringComparison.Ordinal),
            "捕获副本固定可见性并显式启动选中视频、暂停其他项，不保留时钟或用ID冒充getLayer序号");

        var observationSource = new JsonObject { ["objects"] = VideoScene() };
        string originalObservationSource = observationSource.ToJsonString();
        JsonObject? morningObservation = DaytimeSplit.PrepareVideoObservation(observationSource, VideoProperties(), "morning");
        JsonObject? duskObservation = DaytimeSplit.PrepareVideoObservation(observationSource, VideoProperties(), "dusk");
        var ambiguousObservation = observationSource.DeepClone().AsObject();
        ambiguousObservation["objects"]!.AsArray().Add(Layer(99, "morning"));
        check(morningObservation is not null && duskObservation is not null &&
            ById(morningObservation["objects"]!.AsArray())[10]["visible"]?["value"]?.GetValue<bool>() == true &&
            ById(duskObservation["objects"]!.AsArray())[20]["visible"]?["script"]?.GetValue<string>().Contains(".play()") == true &&
            ById(morningObservation["objects"]!.AsArray())[21]["visible"]?["value"]?.GetValue<bool>() == false &&
            AnalysisCache.Key(morningObservation) != AnalysisCache.Key(duskObservation) && observationSource.ToJsonString() == originalObservationSource &&
            DaytimeSplit.PrepareVideoObservation(ambiguousObservation, VideoProperties(), "morning") is null &&
            DaytimeSplit.PrepareVideoObservation(new JsonObject { ["objects"] = Scene() }, new JsonObject(), "day") is null,
            "观测前激活请求的视频状态、按冻结场景隔离缓存并保留原作，静态不明或旧模板继续原观测路径");

        Blocker blockerBlocker = PlanNarrative.NoInputIndependentGroup(ById(VideoScene()), new Dictionary<int, HashSet<string>> {
            [1] = ["wall_clock_api", "observed_wall_clock"], [10] = ["active_shader_audio_spectrum"] });
        string blocker = blockerBlocker.Text;
        JsonObject localized = blockerBlocker.ToNode();
        string explanation = localized["en"]!.GetValue<string>();
        check(explanation.Contains("wall-clock", StringComparison.Ordinal) && explanation.Contains("audio-spectrum", StringComparison.Ordinal) &&
            !explanation.Contains("all visible content", StringComparison.Ordinal) && !explanation.Contains("nothing remains", StringComparison.Ordinal),
            "无独立组文案同时报告时钟和音频依赖，不把单个音频层归因为整幅画面主体");

        DynamicExportChecks(check);
    }

    private static void DynamicExportChecks(Action<bool, string> check)
    {
        JsonObject ExportPlan(JsonArray objects) => new() {
            ["settings"] = new JsonObject { ["daytime_state"] = "day" }, ["snapshot_properties"] = VideoProperties(),
            ["daytime_split"] = DaytimeSplit.Detect(ById(objects), properties: VideoProperties()).ToJson(),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(11), ["root_ids"] = new JsonArray(11) }) };
        foreach (string script in new[] { VideoSelector, VideoSelector.Replace("hours >=", "hours >", StringComparison.Ordinal)
            .Replace("hours <", "hours <=", StringComparison.Ordinal) })
        {
            JsonArray originals = VideoScene(script);
            ById(originals)[11]["visible"] = new JsonObject { ["value"] = false,
                ["user"] = new JsonObject { ["name"] = "display", ["condition"] = "1" } };
            string before = originals.ToJsonString();
            JsonObject plan = ExportPlan(originals);
            var export = DaytimeSplit.PrepareDynamicExport(ById(originals), plan, [])!;
            JsonObject replacement = new() { ["id"] = 100, ["name"] = "Video group-1", ["image"] = "models/wpe_baker_video/group-1.json", ["visible"] = true };
            export.BindReplacement(replacement, ById(originals)[11], isStatic: false);
            JsonArray output = SceneAssembler.AssembleObjects(ById(originals), plan, new Dictionary<string, JsonObject> { ["group-1"] = replacement }, []);
            var byId = ById(output);
            check(output.Select(obj => obj!["id"]!.GetValue<int>()).SequenceEqual(originals.Select(obj => obj!["id"]!.GetValue<int>())) &&
                byId[1]["visible"]!["script"]!.GetValue<string>() == script && byId[11]["name"]!.GetValue<string>() == "day" &&
                byId[11]["image"]!.GetValue<string>() == "models/wpe_baker_video/group-1.json" &&
                byId[11]["visible"]!.ToJsonString() == ById(originals)[11]["visible"]!.ToJsonString() &&
                new[] { 10, 20, 21, 22 }.All(id => byId[id].ToJsonString() == ById(originals)[id].ToJsonString()) && originals.ToJsonString() == before,
                "动态昼夜装配保留原ID/名称/顺序和两种边界脚本，未替换时段及手动第五项保留原视频");
            JsonObject comparison = export.ComparisonProperties(plan["snapshot_properties"]!.AsObject());
            check(comparison["timevarying"]!.GetValue<bool>() == false && comparison["display"]!.GetValue<string>() == "1" &&
                plan["snapshot_properties"]!["timevarying"]!.GetValue<bool>() == true &&
                export.PropertyKeys.Order().SequenceEqual(new[] { "display", "timevarying", "morningtime", "daytime", "dusktime", "nighttime" }.Order()),
                "验证只切换验证用属性以实际覆盖替代视频，成品模式和六个昼夜控件不被固定");
            var controls = new JsonObject {
                ["display"] = new JsonObject { ["condition"] = "!timevarying.value" },
                ["timevarying"] = new JsonObject { ["type"] = "bool" },
                ["morningtime"] = new JsonObject { ["condition"] = "timevarying.value" },
                ["brightness"] = new JsonObject { ["type"] = "slider" } };
            ProjectWriter.HideFixedPropertyControls(controls, export.PropertyKeys);
            check(controls["display"]!["condition"]!.GetValue<string>() == "!timevarying.value" &&
                controls["morningtime"]!["condition"]!.GetValue<string>() == "timevarying.value" &&
                controls["timevarying"]!["condition"] is null && controls["brightness"]!["condition"]!.GetValue<string>() == "false",
                "动态成品保留自动、手动及时段阈值的原控件条件，只隐藏已固定的其他设置");
            bool staticRejected = false;
            try { export.BindReplacement(replacement, ById(originals)[11], isStatic: true); }
            catch (InvalidDataException) { staticRejected = true; }
            check(staticRejected, "需要getVideoTexture的动态目标不能静默变成静态图片");
        }
        JsonArray source = VideoScene();
        JsonObject mixed = ExportPlan(source);
        mixed["video_groups"]![0]!["layer_ids"]!.AsArray().Add(21);
        bool mixedRejected = false, ambiguousRejected = false;
        try { _ = DaytimeSplit.PrepareDynamicExport(ById(source), mixed, []); }
        catch (InvalidDataException) { mixedRejected = true; }
        JsonObject unambiguousPlan = ExportPlan(source);
        source.Add(Layer(99, "day"));
        try { _ = DaytimeSplit.PrepareDynamicExport(ById(source), unambiguousPlan, []); }
        catch (InvalidDataException) { ambiguousRejected = true; }
        check(mixedRejected && ambiguousRejected, "互斥状态混入同一视频组或目标名称无法消歧时明确拒绝动态转接");

        JsonArray parented = VideoScene();
        ById(parented)[11]["parent"] = 5;
        parented.Insert(0, new JsonObject { ["id"] = 5, ["name"] = "parent", ["origin"] = "100 50 0", ["scale"] = "2 2 1" });
        JsonObject parentPlan = ExportPlan(parented);
        parentPlan["video_groups"]![0]!["parent_id"] = 5;
        parentPlan["video_groups"]![0]!["parent_transform"] = new JsonObject { ["origin"] = "100 50 0", ["scale"] = "2 2 1" };
        var parentExport = DaytimeSplit.PrepareDynamicExport(ById(parented), parentPlan, [])!;
        JsonObject parentReplacement = new() { ["id"] = 100, ["name"] = "Video", ["image"] = "models/generated.json", ["origin"] = "110 60 0", ["scale"] = "2 2 1" };
        HybridVideoProjection.AttachToParent(parentReplacement, parentPlan["video_groups"]![0]!.AsObject());
        parentExport.BindReplacement(parentReplacement, ById(parented)[11], isStatic: false);
        check(parentReplacement["id"]!.GetValue<int>() == 11 && parentReplacement["parent"]!.GetValue<int>() == 5 &&
            parentReplacement["origin"]!.GetValue<string>() == "5 5 0" && parentReplacement["scale"]!.GetValue<string>() == "1 1 1",
            "身份转接保留生成视频已换算的父级局部坐标，不重复套用源层变换");
        JsonObject child = Layer(77, "child"); child["parent"] = 11; parented.Add(child);
        bool childRejected = false;
        try { _ = DaytimeSplit.PrepareDynamicExport(ById(parented), parentPlan, []); }
        catch (InvalidDataException) { childRejected = true; }
        check(childRejected, "受控源含子层时拒绝叶子替换，避免改变子层的父级几何语义");
    }

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
        VideoSelectors(check);
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
        static JsonArray RuntimeLayers(JsonArray objects, bool postprocessController = false) =>
            new(objects.OfType<JsonObject>().Select(obj => {
                int id = obj["id"]!.GetValue<int>();
                return (JsonNode)new JsonObject {
                    // 受控层在观测里全是隐藏的：真实探测只能落在某一个时刻。
                    ["id"] = id, ["owner"] = id, ["visible"] = id is not (10 or 11 or 20),
                    // 视频选择器也可同时承担实时后处理；纯可见性选择器自己不绘制。
                    ["has_mesh"] = id != 1 || postprocessController, ["effective_parallax_depth"] = new JsonArray(0, 0),
                    ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
                        ["uses_system_media_thumbnail"] = false,
                        ["active_uniforms"] = new JsonArray("g_ModelViewProjectionMatrix"),
                        ["textures"] = postprocessController && id == 1 ? new JsonArray("_rt_default") : new JsonArray() }) };
            }).ToArray());

        async Task<JsonObject> PlanAsync(string name, bool split = false, string? state = null, bool indexedVideo = false)
        {
            JsonArray sceneObjects = indexedVideo ? VideoScene() : Scene();
            JsonArray dependencies = Dependencies();
            if (indexedVideo)
            {
                // 与真实案例一样，控制器在主体之后合成后处理，且在 update 里读取受控视频。
                JsonNode controller = sceneObjects[0]!;
                sceneObjects.RemoveAt(0);
                sceneObjects.Add(controller);
                dependencies.Add(VisibilityWrite(21));
                dependencies.Add(VisibilityWrite(22));
                foreach (int target in new[] { 10, 11, 20, 21, 22 })
                    dependencies.Add(new JsonObject { ["owner"] = 1, ["target"] = target, ["operation"] = "read",
                        ["property"] = "videoTexture", ["binding"] = "visible", ["initialization"] = false });
            }
            await File.WriteAllTextAsync(scenePath, new JsonObject {
                ["general"] = new JsonObject {
                    ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                    ["clearenabled"] = true },
                ["objects"] = sceneObjects.DeepClone() }.ToJsonString());
            string tracePath = Path.Combine(root, "daytime-trace-" + name + ".json");
            await File.WriteAllTextAsync(tracePath, new JsonObject {
                ["source"] = scenePath, ["status"] = "complete",
                ["runtime_dependencies"] = dependencies,
                ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
                ["runtime_animation_periods"] = indexedVideo ? new JsonArray(new JsonObject {
                    ["source_owner_layer_id"] = 11, ["mechanism"] = "video", ["track_name"] = "day",
                    ["duration_numerator"] = 2, ["duration_denominator"] = 1, ["frame_count"] = 60,
                    ["playback_rate"] = 1, ["looping"] = true, ["event_driven"] = false, ["dynamic_controlled"] = false
                }) : new JsonArray(),
                ["runtime_layers"] = RuntimeLayers(sceneObjects, postprocessController: indexedVideo) }.ToJsonString());
            return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, sourceDirectory, root, Path.Combine(root, "daytime-" + name), 64, 32,
                    UserProperties: indexedVideo ? VideoProperties() : null,
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

        JsonObject videoPlan = await PlanAsync("indexed-video", split: true, state: "day", indexedVideo: true);
        check(LayoutAdmission.CompositionHierarchyConflict(videoPlan) is null,
            "昼夜计划的纯层级骨架检查不把缺少源脚本的骨架误交给动态成品重验");
        check(videoPlan["daytime_split"]?["controls_video_playback"]?.GetValue<bool>() == true && Allocation(videoPlan, 11) == "video" &&
            Allocation(videoPlan, 10) == "inactive" && Allocation(videoPlan, 22) == "inactive" &&
            Allocation(videoPlan, 1) == "live" && Reasons(videoPlan, 1).Contains("reads_current_framebuffer") &&
            !Reasons(videoPlan, 11).Contains("live_runtime_resource_dependency") && !Reasons(videoPlan, 1).Contains("wall_clock_api") &&
            videoPlan["loop"]?["video_control_scope"]?["scene_wide_fallback"]?.GetValue<bool>() == false &&
            videoPlan["loop"]?["video_control_scope"]?["resolved_targets"] is JsonArray { Count: 0 } &&
            videoPlan["loop"]?["candidates"] is JsonArray { Count: > 0 },
            "已选状态解除后处理控制器的冻结视频依赖，保留实时后处理，并用无动态控制的状态视图求循环");
    }
}
