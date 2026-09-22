using System.Text.Json.Nodes;
using Baker.Core;

/// 取舍清单（feat/tradeoff-list）：分类标签、属性键名推导、清单排序与连带集合、两处改写过的文案。
internal static class TradeoffOptionsChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        Classification(check);
        Wording(check);
        Listing(check);
        await PropertyKeysAsync(check, root);
    }

    /// 逐层分类：技术类压过取舍类（取舍那一半关了也退不出实时），派生类只在没有别的理由时用。
    private static void Classification(Action<bool, string> check)
    {
        var pointer = TradeoffOptions.Classify(["observed_pointer", "pointer_api"]);
        check(pointer.Class == TradeoffOptions.Tradeoff && pointer.Kinds is ["pointer"], "tradeoff class: pointer is a tradeoff");
        var mixed = TradeoffOptions.Classify(["active_shader_audio_spectrum", "scene_camera"]);
        check(mixed.Class == TradeoffOptions.Technical, "tradeoff class: a technical reason outranks a tradeoff one");
        var derived = TradeoffOptions.Classify(["shares_live_hierarchy"]);
        check(derived.Class == TradeoffOptions.Derived && derived.Kinds is ["hierarchy"], "tradeoff class: hierarchy is derived");
        var subject = TradeoffOptions.Classify(["active_shader_pointer_input"], subject: true);
        check(subject.Class == TradeoffOptions.Subject, "tradeoff class: the whole image being the effect is subject");
        var feedback = TradeoffOptions.Classify(["reads_current_framebuffer", "single_shot_animation"]);
        check(feedback.Class == TradeoffOptions.Tradeoff && feedback.Kinds is ["feedback", "intro"],
            "tradeoff class: feedback effects and one-shot intros are tradeoffs, in list order");
        var overlay = TradeoffOptions.Classify(["observed_wall_clock"], suspectedOverlay: true);
        check(overlay.Kinds is ["clock", "overlay"], "tradeoff class: a suspected overlay adds the overlay kind");
        check(TradeoffOptions.ClassifyReason("not_a_reason") is null, "tradeoff class: unknown reasons are not tradeoffs");
        check(TradeoffOptions.KindLabel("parallax", MessageCatalog.Chinese).Contains("视差") &&
            TradeoffOptions.KindLabel("parallax", MessageCatalog.English).Contains("parallax"), "tradeoff class: kind labels are bilingual");
    }

    /// 两处按实测改写的文案：不可达不再说"改设置也没用"，主体类不再说"去壁纸设置里关掉"。
    private static void Wording(Action<bool, string> check)
    {
        var unreachable = MessageCatalog.Find("blocker.fullframe_unreachable")!;
        check(!unreachable.Zh.Contains("都不会改变这一点") && unreachable.Zh.Contains("可选方案：禁用前置的实时元素后生成整幅循环视频") &&
            !unreachable.En.Contains("no full-frame layout as authored") && unreachable.En.Contains("Options: disable the blocking live elements"),
            "wording: fullframe_unreachable now gives a way to unlock");
        check(unreachable.LegacyTemplate.EndsWith("This scene has no full-frame layout as authored.", StringComparison.Ordinal),
            "wording: fullframe_unreachable keeps its legacy English verbatim");
        foreach (string key in new[] { "blocker.no_input_independent_group", "blocker.no_input_independent_group_generic" })
        {
            var entry = MessageCatalog.Find(key)!;
            check(!entry.Zh.Contains("禁用后无剩余内容") && entry.Zh.Contains("未找到不依赖实时输入、可生成的视频组"),
                "wording: " + key + " rejects cleanly in Chinese");
            check(!entry.En.Contains("nothing remains once disabled") && entry.En.Contains("dependency analysis found no video group independent of live input"),
                "wording: " + key + " rejects cleanly in English");
            check(entry.LegacyTemplate == "No input-independent visual group remains after dependency closure.",
                "wording: " + key + " keeps its legacy English verbatim");
        }
    }

    /// 清单：排序、连带子层、残留估计、属性优先、主体类不给清单。
    private static void Listing(Action<bool, string> check)
    {
        // 10 静态底；20 视差父层（可画），30 时钟挂在它下面；40 音频律动层；50 只是跟着 40 实时的子层。
        JsonObject Layer(int id, int? parent, string name, bool live, string[] reasons, bool drawable = true,
            JsonObject? property = null)
        {
            var (classification, kinds) = TradeoffOptions.Classify(reasons);
            return new JsonObject {
                ["id"] = id, ["parent"] = parent, ["root"] = parent ?? id, ["allocation_root"] = parent ?? id,
                ["name"] = name, ["live"] = live, ["drawable"] = drawable, ["visible"] = true,
                ["reasons"] = new JsonArray([.. reasons.Select(reason => (JsonNode)JsonValue.Create(reason))]),
                ["tradeoff_class"] = live ? classification : null,
                ["tradeoff_kinds"] = live ? new JsonArray([.. kinds.Select(kind => (JsonNode)JsonValue.Create(kind))]) : null,
                ["visible_property"] = property };
        }
        JsonObject Property(string key) => new() {
            ["key"] = key, ["label"] = null, ["type"] = "bool", ["declared"] = true, ["binding"] = "self",
            ["bound_layer_id"] = 0, ["condition"] = null, ["current_value"] = true, ["off_value"] = false,
            ["status"] = "resolved", ["off_hint_zh"] = "关闭值 false", ["off_hint_en"] = "the off value is false" };
        JsonObject Plan(params JsonObject[] layers) => new() {
            ["route"] = "whole_layer", ["settings"] = new JsonObject { ["video_layout"] = "layered", ["view_mode"] = "preserve" },
            ["summary"] = new JsonObject { ["key"] = "summary.bakeable", ["zh"] = "", ["en"] = "" },
            ["blockers_localized"] = new JsonArray(),
            ["layers"] = new JsonArray([.. layers.Select(layer => (JsonNode)layer)]),
            ["live_layer_ids"] = new JsonArray([.. layers.Where(layer => layer["live"]!.GetValue<bool>())
                .Select(layer => (JsonNode)JsonValue.Create(layer["id"]!.GetValue<int>()))]) };

        var plan = Plan(
            Layer(10, null, "底", live: false, []),
            Layer(20, null, "视差组", live: true, ["active_shader_parallax_input"]),
            Layer(30, 20, "时钟", live: true, ["observed_wall_clock"], property: Property("showclock")),
            Layer(40, null, "音频十字架", live: true, ["active_shader_audio_spectrum"], property: Property("audiocross")),
            Layer(50, 40, "跟随层", live: true, ["shares_live_hierarchy"]));
        TradeoffOptions.Attach(plan);
        var record = plan[TradeoffOptions.Field]!.AsObject();
        var options = record["options"]!.AsArray().OfType<JsonObject>().ToArray();
        check(record["status"]!.GetValue<string>() == "available" && options.Length is > 0 and <= 3,
            "tradeoff list: at most three options are offered");
        check(options.Select(option => option["rank"]!.GetValue<int>()).SequenceEqual(Enumerable.Range(1, options.Length)),
            "tradeoff list: options are ranked from one");
        var order = options.Select(option => (Full: option["expected_full_frame"]!.GetValue<bool>() ? 0 : 1,
            Count: option["turn_off_count"]!.GetValue<int>())).ToArray();
        check(order.SequenceEqual(order.OrderBy(item => item.Full).ThenBy(item => item.Count)),
            "tradeoff list: options reaching full frame come first, then the ones turning off less");
        check(options[0]["expected_full_frame"]!.GetValue<bool>(), "tradeoff list: the first option is the one that should reach full frame");
        var audioOnly = options.Single(option => option["turn_off_kinds"]!.AsArray()
            .Select(kind => kind!.GetValue<string>()).SequenceEqual(["audio"]));
        check(audioOnly["exclude_layers"]!.AsArray().Select(id => id!.GetValue<int>()).SequenceEqual([40]) &&
            audioOnly["estimated_residual_live_layers"]!.GetValue<int>() == 2,
            "tradeoff list: excluding the audio layer drops its derived child, the parallax pair stays live");
        check(audioOnly["properties"]!.AsArray().OfType<JsonObject>().Select(entry => entry["key"]!.GetValue<string>()).SequenceEqual(["audiocross"]),
            "tradeoff list: the wallpaper property comes first among the ways to turn it off");
        check(audioOnly["zh"]!.GetValue<string>().Contains("audiocross") && audioOnly["zh"]!.GetValue<string>().Contains("--exclude-layers 40") &&
            options[0]["en"]!.GetValue<string>().Contains("Option 1"), "tradeoff list: the option text names the property and the command");
        check(audioOnly["zh"]!.GetValue<string>().Contains("实测无功耗收益") && audioOnly["en"]!.GetValue<string>().Contains("shows no measured saving"),
            "tradeoff list: every option repeats that keeping layers live does not save power");
        check(audioOnly["zh"]!.GetValue<string>().Contains("功耗收益相应降低") && audioOnly["en"]!.GetValue<string>().Contains("still render every frame"),
            "tradeoff list: an option that leaves layers live says the saving is reduced");
        // 视差有两个方案：单独关视差，以及视差连装饰一起关。这里查后者（它才是预计能进整幅的那个）。
        var parallax = options.Single(option => option["turn_off_kinds"]!.AsArray()
            .Select(kind => kind!.GetValue<string>()).ToHashSet().SetEquals(["parallax", "audio"]));
        check(options.Count(option => option["turn_off_kinds"]!.AsArray()
            .Select(kind => kind!.GetValue<string>()).SequenceEqual(["parallax"])) == 1,
            "tradeoff list: turning off only parallax is offered on its own");
        check(parallax["view_mode"]?.GetValue<string>() == "fixed_view" &&
            parallax["command"]!.GetValue<string>().Contains("--view-mode fixed_view"),
            "tradeoff list: turning off parallax uses fixed_view");
        check(parallax["excluded_layer_ids"]!.AsArray().Select(id => id!.GetValue<int>()).Contains(30) &&
            parallax["collateral_layer_ids"]!.AsArray().Select(id => id!.GetValue<int>()).Contains(30) &&
            parallax["collateral_drawable_layers"]!.GetValue<int>() == 2 &&
            parallax["zh"]!.GetValue<string>().Contains("时钟"),
            "tradeoff list: the clock hanging under the parallax layer is reported as collateral");
        check(parallax["zh"]!.GetValue<string>().Contains("其余图层无对应属性开关") &&
            parallax["en"]!.GetValue<string>().Contains("no property switch"),
            "tradeoff list: an option only partly covered by properties says the rest needs the command line");
        check(options.Count(option => option["turn_off_kinds"]!.AsArray().Any(kind => kind!.GetValue<string>() == "clock")) == 0,
            "tradeoff list: a later tier turning off the same layers is not listed twice");
        check(parallax["estimated_residual_live_layers"]!.GetValue<int>() == 0 &&
            parallax["expected_full_frame"]!.GetValue<bool>() &&
            parallax["zh"]!.GetValue<string>().Contains("禁用后预计无实时图层；整幅可行性需重新分析确认"),
            "tradeoff list: with everything optional off nothing stays live and full frame is expected");
        check(TradeoffOptions.Lines(plan, "zh").Length == options.Length + 1, "tradeoff list: the CLI prints a header and one line per option");

        // 连带：只想关时钟，但它挂在视差层下面时，排除的是父层还是自己？父层不在清单里时只关自己。
        var clockOnly = Plan(Layer(10, null, "底", live: false, []),
            Layer(20, null, "容器", live: true, ["active_shader_parallax_input"]),
            Layer(30, 20, "时钟", live: true, ["observed_wall_clock"]),
            Layer(31, 30, "日期", live: false, [], drawable: true));
        TradeoffOptions.Attach(clockOnly);
        var clockOption = clockOnly[TradeoffOptions.Field]!["options"]!.AsArray().OfType<JsonObject>()
            .Single(option => option["turn_off_kinds"]!.AsArray().Any(kind => kind!.GetValue<string>() == "parallax"));
        check(clockOption["collateral_drawable_layers"]!.GetValue<int>() == 2 &&
            clockOption["collateral_layer_ids"]!.AsArray().Select(id => id!.GetValue<int>()).SequenceEqual([30, 31]) &&
            clockOption["zh"]!.GetValue<string>().Contains("连带禁用：挂在这些图层下的"),
            "tradeoff list: children that were not asked for are reported as collateral");

        // 无独立组不足以证明主体只剩实时效果，不能把绘制层改标为 subject。
        var subject = Plan(Layer(10, null, "指针着色器", live: true, ["active_shader_pointer_input"]));
        subject["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.no_input_independent_group" });
        TradeoffOptions.Attach(subject);
        check(subject[TradeoffOptions.Field]!["status"]!.GetValue<string>() == "dependency_blocked" &&
            subject[TradeoffOptions.Field]!["options"]!.AsArray().Count == 0 &&
            subject["layers"]![0]!["tradeoff_class"]!.GetValue<string>() == TradeoffOptions.Tradeoff &&
            subject[TradeoffOptions.Field]!["zh"]!.GetValue<string>().Contains("尚未确认") &&
            !subject[TradeoffOptions.Field]!["zh"]!.GetValue<string>().Contains("只能实时"),
            "tradeoff list: dependency blockage does not prove all content is the live effect");

        // 已经能整幅的计划不需要清单；只剩技术类实时层的也没有可取舍元素。
        var done = Plan(Layer(10, null, "底", live: false, []));
        done["settings"]!["video_layout"] = "full_frame";
        TradeoffOptions.Attach(done);
        check(done[TradeoffOptions.Field]!["status"]!.GetValue<string>() == "not_needed" && TradeoffOptions.Lines(done, "zh").Length == 0,
            "tradeoff list: a plan that already reaches full frame gets no list");
        var technical = Plan(Layer(10, null, "相机", live: true, ["scene_camera"]));
        TradeoffOptions.Attach(technical);
        check(technical[TradeoffOptions.Field]!["status"]!.GetValue<string>() == "no_tradeoff_elements" &&
            technical[TradeoffOptions.Field]!["zh"]!.GetValue<string>().Contains("技术性"),
            "tradeoff list: technical-only live layers are reported as nothing to trade away");
    }

    /// 属性键名推导：本层绑定、祖先绑定、带 condition 的组合框、未声明属性、没绑属性。
    private static async Task PropertyKeysAsync(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "tradeoff-source");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "project.json"), new JsonObject {
            ["type"] = "scene", ["file"] = "scene.json",
            ["general"] = new JsonObject { ["properties"] = new JsonObject {
                ["showclock"] = new JsonObject { ["type"] = "bool", ["text"] = "显示时钟", ["value"] = true },
                ["cursorstyle"] = new JsonObject { ["type"] = "combo", ["text"] = "指针特效", ["value"] = "glow",
                    ["options"] = new JsonArray(
                        new JsonObject { ["label"] = "光效", ["value"] = "glow" },
                        new JsonObject { ["label"] = "关闭", ["value"] = "off" }) },
                ["brightness"] = new JsonObject { ["type"] = "slider", ["text"] = "亮度", ["value"] = 1, ["min"] = 0, ["max"] = 2 } } }
        }.ToJsonString());
        var objects = new JsonArray(
            new JsonObject { ["id"] = 10, ["name"] = "底", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" },
            // 20 绑 bool 属性；21 是它的子层，自己没绑，应沿父链给出同一个开关。
            new JsonObject { ["id"] = 20, ["name"] = "音频十字架", ["image"] = "models/cross.json",
                ["size"] = "16 8", ["origin"] = "32 16 0", ["visible"] = new JsonObject { ["user"] = "showclock" } },
            new JsonObject { ["id"] = 21, ["parent"] = 20, ["name"] = "十字架光晕", ["image"] = "models/glow.json",
                ["size"] = "8 8", ["origin"] = "32 16 0" },
            // 30 绑带 condition 的组合框；40 绑一个 project.json 没声明的属性；50 用脚本控制显示。
            new JsonObject { ["id"] = 30, ["name"] = "指针光效", ["image"] = "models/pointer.json",
                ["size"] = "8 8", ["origin"] = "16 8 0",
                ["visible"] = new JsonObject { ["user"] = new JsonObject { ["name"] = "cursorstyle", ["condition"] = "glow" } } },
            new JsonObject { ["id"] = 40, ["name"] = "音频条", ["image"] = "models/bars.json",
                ["size"] = "8 8", ["origin"] = "48 8 0", ["visible"] = new JsonObject { ["user"] = "undeclaredswitch" } },
            new JsonObject { ["id"] = 50, ["name"] = "脚本控制层", ["image"] = "models/script.json",
                ["size"] = "8 8", ["origin"] = "8 8 0", ["visible"] = new JsonObject { ["script"] = "return true;" } },
            new JsonObject { ["id"] = 60, ["name"] = "亮度绑定层", ["image"] = "models/slider.json",
                ["size"] = "8 8", ["origin"] = "56 8 0", ["visible"] = new JsonObject { ["user"] = "brightness" } });
        string scenePath = Path.Combine(sourceDirectory, "scene.json");
        await File.WriteAllTextAsync(scenePath, new JsonObject {
            ["general"] = new JsonObject {
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = true },
            ["objects"] = objects }.ToJsonString());
        // 每个实时层一条输入依赖：20/21 音频，30/50/60 指针，40 音频。
        static JsonObject Input(int owner, string property) => new() {
            ["owner"] = owner, ["target"] = owner, ["operation"] = "input", ["property"] = property };
        var dependencies = new JsonArray(Input(20, "audio"), Input(30, "pointer"), Input(40, "audio"),
            Input(50, "pointer"), Input(60, "pointer"));
        string tracePath = Path.Combine(root, "tradeoff-trace.json");
        await File.WriteAllTextAsync(tracePath, new JsonObject {
            ["source"] = scenePath, ["status"] = "complete",
            ["runtime_dependencies"] = dependencies,
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray([.. objects.OfType<JsonObject>().Select(item => (JsonNode)new JsonObject {
                ["id"] = item["id"]!.DeepClone(), ["owner"] = item["id"]!.DeepClone(), ["visible"] = true,
                ["has_mesh"] = true, ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
                    ["uses_system_media_thumbnail"] = false, ["active_uniforms"] = new JsonArray("g_ModelViewProjectionMatrix"),
                    ["textures"] = new JsonArray() }) })]) }.ToJsonString());
        var plan = await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
            new(2, sourceDirectory, root, Path.Combine(root, "tradeoff-plan"), 64, 32, RuntimeTraceFile: tracePath));
        JsonObject Layer(int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id);
        JsonObject Property(int id) => Layer(id)["visible_property"]!.AsObject();
        check(Property(20)["key"]!.GetValue<string>() == "showclock" && Property(20)["status"]!.GetValue<string>() == "resolved" &&
            Property(20)["off_value"]!.GetValue<bool>() == false && Property(20)["binding"]!.GetValue<string>() == "self" &&
            Property(20)["current_value"]!.GetValue<bool>() && Property(20)["label"]!.GetValue<string>() == "显示时钟",
            "property key: a bool binding gives the key, the current value and false as the off value");
        check(Property(20)["off_hint_zh"]!.GetValue<string>().Contains("{\"showclock\": false}") &&
            Property(20)["off_hint_en"]!.GetValue<string>().Contains("--properties"),
            "property key: the off value is spelled out for --properties in both languages");
        check(Property(21)["key"]!.GetValue<string>() == "showclock" && Property(21)["binding"]!.GetValue<string>() == "ancestor" &&
            Property(21)["bound_layer_id"]!.GetValue<int>() == 20,
            "property key: a child without its own binding follows the nearest bound ancestor");
        check(Property(30)["key"]!.GetValue<string>() == "cursorstyle" && Property(30)["status"]!.GetValue<string>() == "conditional" &&
            Property(30)["off_value"]!.GetValue<string>() == "off" && Property(30)["condition"]!.GetValue<string>() == "glow",
            "property key: a conditional combo binding gives another option as the off value");
        check(Property(40)["key"]!.GetValue<string>() == "undeclaredswitch" && Property(40)["declared"]!.GetValue<bool>() == false &&
            Property(40)["status"]!.GetValue<string>() == "resolved",
            "property key: a binding to an undeclared property still names the key");
        check(Property(50)["status"]!.GetValue<string>() == "not_bound" &&
            Property(50)["visible_binding"]!.GetValue<string>() == "script",
            "property key: a script-driven visibility says so instead of guessing");
        check(Property(60)["status"]!.GetValue<string>() == "type_not_boolean" && Property(60)["off_value"] is null,
            "property key: a slider binding is not turned into a false off value");
        check(Layer(30)["tradeoff_class"]!.GetValue<string>() == TradeoffOptions.Tradeoff &&
            Layer(30)["tradeoff_kinds"]!.AsArray()[0]!.GetValue<string>() == "pointer" &&
            Layer(10)["tradeoff_class"] is null,
            "property key: live layers carry the class and kind, still layers carry none");
        var listed = plan[TradeoffOptions.Field]!.AsObject();
        check(listed["status"]!.GetValue<string>() is "available" or "not_needed", "property key: the plan carries a tradeoff record");
        if (listed["status"]!.GetValue<string>() == "available")
        {
            check(listed["options"]!.AsArray().OfType<JsonObject>().Any(option =>
                option["properties"]!.AsArray().OfType<JsonObject>().Any(entry => entry["key"]!.GetValue<string>() == "showclock")),
                "property key: the derived key reaches the tradeoff list");
            check(plan["summary"]!["zh"]!.GetValue<string>().Contains("取舍方案") &&
                plan["summary"]!["en"]!.GetValue<string>().Contains("tradeoff option"),
                "property key: the one-line verdict points at the list");
        }
    }
}
