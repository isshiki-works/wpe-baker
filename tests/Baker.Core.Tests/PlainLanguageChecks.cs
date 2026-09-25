using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

/// 界面说人话（feat/gui-plain-language）：结论三行、取舍卡片，以及"用户看得到的字符串里不许出现术语"的扫描。
internal static class PlainLanguageChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Verdicts(check);
        NumberLine(check);
        TurnOffCard(check);
    }

    // ---------------------------------------------------------------------------------------
    // 结论三行与取舍卡片
    // ---------------------------------------------------------------------------------------

    private static JsonObject Layer(int id, int? parent, string name, bool live, string[] reasons, JsonObject? property = null)
    {
        var (classification, kinds) = TradeoffOptions.Classify(reasons);
        return new JsonObject {
            ["id"] = id, ["parent"] = parent, ["root"] = parent ?? id, ["allocation_root"] = parent ?? id,
            ["name"] = name, ["live"] = live, ["drawable"] = true, ["visible"] = true,
            ["reasons"] = new JsonArray([.. reasons.Select(reason => (JsonNode)JsonValue.Create(reason))]),
            ["tradeoff_class"] = live ? classification : null,
            ["tradeoff_kinds"] = live ? new JsonArray([.. kinds.Select(kind => (JsonNode)JsonValue.Create(kind))]) : null,
            ["visible_property"] = property };
    }

    private static JsonObject Plan(params JsonObject[] layers) => new() {
        ["route"] = "whole_layer",
        ["settings"] = new JsonObject { ["video_layout"] = "layered", ["view_mode"] = "preserve",
            ["fps_numerator"] = 60, ["fps_denominator"] = 1 },
        ["summary"] = new JsonObject { ["key"] = "summary.bakeable", ["zh"] = "", ["en"] = "" },
        ["output_resolution"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 },
        ["blockers_localized"] = new JsonArray(),
        ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject {
            ["seconds"] = 151.0, ["frames"] = 9060, ["total_retime_cost_percent"] = 0.4 }) },
        ["layers"] = new JsonArray([.. layers.Select(layer => (JsonNode)layer)]),
        ["live_layer_ids"] = new JsonArray([.. layers.Where(layer => layer["live"]!.GetValue<bool>())
            .Select(layer => (JsonNode)JsonValue.Create(layer["id"]!.GetValue<int>()))]) };

    /// 关得掉的东西：视差组、挂在它下面的时钟、能靠壁纸属性关掉的音频律动层。
    private static JsonObject TradeoffPlan()
    {
        var plan = Plan(
            Layer(10, null, "底", live: false, []),
            Layer(20, null, "视差", live: true, ["active_shader_parallax_input"]),
            Layer(30, 20, "时钟", live: true, ["observed_wall_clock"]),
            Layer(40, null, "音频", live: true, ["active_shader_audio_spectrum"], new JsonObject {
                ["key"] = "audiocross", ["label"] = null, ["type"] = "bool", ["declared"] = true, ["binding"] = "self",
                ["bound_layer_id"] = 40, ["condition"] = null, ["current_value"] = true, ["off_value"] = false,
                ["status"] = "resolved", ["off_hint_zh"] = "关掉它", ["off_hint_en"] = "switch it off" }));
        TradeoffOptions.Attach(plan);
        return plan;
    }

    private static void Verdicts(Action<bool, string> check)
    {

        // 能烘：没有阻塞、有循环、整幅路线，取舍清单判成"不需要"。
        var bakeable = Plan(Layer(10, null, "底", live: false, []));
        bakeable["settings"]!["video_layout"] = "full_frame";
        TradeoffOptions.Attach(bakeable);
        check(PlainLanguage.Verdict(bakeable, false) == "可以生成",
            "tool register: a clean plan reports one fixed state, an action line and the basis in the details");

        var tradeoff = TradeoffPlan();
        check(PlainLanguage.Verdict(tradeoff, false) == "可以生成",
            "a plan with tradeoff options does not point at a disable list the GUI no longer shows");
        var applied = bakeable.DeepClone().AsObject();
        applied["preset_applied"] = "quality";
        applied[TradeoffOptions.Field] = tradeoff[TradeoffOptions.Field]!.DeepClone();
        check(PlainLanguage.Verdict(applied, false) == "可以生成",
            "integrated GUI does not mistake optional tradeoffs for unapplied requirements");
        applied["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.loop_unresolved" });
        applied["preset_applied"] = "none";
        check(PlainLanguage.Verdict(applied, false) == "不支持", "integrated GUI does not promote a rejected plan because tradeoff options exist");

        // 主体类：整张画面就是那个实时效果画出来的。
        var subject = Plan(Layer(10, null, "指针着色器", live: true, ["active_shader_pointer_input"]));
        // v3 形态：blockers 英文原文与 blockers_localized 同下标成对；取舍清单按编号读，只有 blockers_localized 读不到。
        subject["blockers"] = new JsonArray(MessageCatalog.RenderLegacy("blocker.no_input_independent_group"));
        subject["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.no_input_independent_group" });
        TradeoffOptions.Attach(subject);
        check(PlainLanguage.Verdict(subject, false) == "不支持" &&
            !PlainLanguage.HasTurnOffCard(subject) && PlainLanguage.TurnOffItems(subject, false).Length == 0,
            "plain language: dependency blockage does not claim that disabling effects leaves no content");

        // 3D 镜头。
        var camera = Plan(Layer(10, null, "底", live: false, []));
        camera["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.perspective_needs_screenspace" });
        TradeoffOptions.Attach(camera);
        check(PlainLanguage.Verdict(camera, false) == "不支持",
            "tool register: a moving 3D camera is stated as the cause, in the details");

        // 找不到循环。
        var noLoop = Plan(Layer(10, null, "底", live: false, []));
        noLoop["loop"] = new JsonObject { ["candidates"] = new JsonArray(),
            ["no_candidate_reason"] = new JsonObject { ["kind"] = "NoTemporalMechanism" } };
        noLoop["suitability"] = new JsonObject { ["verdict"] = "not_suitable", ["rule"] = "fixed_period_exceeds_loop_ceiling" };
        TradeoffOptions.Attach(noLoop);
        check(PlainLanguage.Verdict(noLoop, false) == "不支持",
            "tool register: a missing loop period is named in the details");

        var cheap = Plan(Layer(10, null, "底", live: false, []));
        cheap["settings"]!["video_layout"] = "full_frame";
        TradeoffOptions.Attach(cheap);
        cheap["summary"]!["key"] = "summary.bakeable_static";
        check(PlainLanguage.Verdict(cheap, false) == "可以生成",
            "an older static result is not automatically described as having no savings");
        foreach (var (status, zh) in new[] {
            ("potential_gain", "可以生成，有潜在收益"),
            ("low_value", "可以生成，预计收益较低"),
            ("unknown", "可以生成，收益待确认") })
        {
            cheap[BakeValueAssessment.Field] = new JsonObject { ["status"] = status, ["reason_zh"] = "已有元数据依据。", ["reason_en"] = "Recorded metadata evidence." };
            check(PlainLanguage.Verdict(cheap, false) == zh,
                "benefit assessment is displayed independently of static output: " + status);
        }
        cheap["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.loop_unresolved" });
        check(PlainLanguage.Verdict(cheap, false) == "不支持",
            "a failed plan keeps its actual failure reason rather than becoming a low-value verdict");
    }

    private static void NumberLine(Action<bool, string> check)
    {
        // 可见改动看的是摆动改频那一项（max_change_visible_percent），不是整体调速；档位预算就按这个量给。
        JsonObject Candidate(double drift, double visiblePercent, double residual = 0) => new() {
            ["seconds"] = 60.0, ["total_retime_cost_percent"] = 0.05,
            ["sway_retime"] = new JsonObject { ["phase_drift_cycles"] = drift,
                ["max_change_visible_percent"] = visiblePercent, ["residual_pixels"] = residual } };
        // 三档按 2026-09-18 用户看片结论：平衡档 3% 以内（阿米娅 3486806915 的 2.81%）属于基本看不出来。
        check(PlainLanguage.ChangeLevel(Candidate(0.02, 0.5), false) == "可忽略" &&
            PlainLanguage.ChangeLevel(Candidate(0.42, 2.81), false) == "可忽略" &&
            PlainLanguage.ChangeLevel(Candidate(0.12, 4.2), false) == "轻微" &&
            PlainLanguage.ChangeLevel(Candidate(0.2, 6), false) == "明显",
            "plain language: visible change maps to three plain grades");
        // 相位差越界（改频求解自身恒 < 0.5 圈）与没闭合的位移分量都降一档/直接判成改动明显。
        check(PlainLanguage.ChangeLevel(Candidate(0.6, 2), false) == "轻微" &&
            PlainLanguage.ChangeLevel(Candidate(0.02, 0.5, residual: 1.5), false) == "明显" &&
            PlainLanguage.ChangeLevel(Candidate(0.02, 0.5), true) == "negligible",
            "plain language: phase drift past half a cycle and unclosed displacement are not called invisible");
        // 没有摆动解时退回整体调速百分比。
        check(PlainLanguage.ChangeLevel(new JsonObject { ["total_retime_cost_percent"] = 4.0 }, false) == "轻微" &&
            PlainLanguage.ChangeLevel(new JsonObject(), false) == "可忽略",
            "plain language: without a sway solution the overall retime percentage is used");
    }

    private static void TurnOffCard(Action<bool, string> check)
    {
        var plan = TradeoffPlan();
        var items = PlainLanguage.TurnOffItems(plan, false);
        check(items.Length >= 2,
            "plain language: every card row has a plain name and what you lose");
        check(items.Any(item => item.Kind == "parallax") &&
            items.Any(item => item.Kind == "audio"),
            "tool register: every row names a layer or property and what disabling it removes");
        // 卡片只列工具真能替你关掉的东西：清单没给出方案的那几样不摆上去，免得勾了却什么都没发生。
        check(items.Select(item => item.Kind).All(kind =>
                AppJsonPresentation.TradeoffOptionViews(plan, false).Any(option => option.Kinds.Contains(kind))),
            "plain language: every row belongs to an option the tool can actually apply");

        var options = AppJsonPresentation.TradeoffOptionViews(plan, false);
        check(options.Length > 0 && options[0].Kinds.Length > 0 &&
            items.Where(item => item.Recommended).Select(item => item.Kind).Order(StringComparer.Ordinal)
                .SequenceEqual(options[0].Kinds.Order(StringComparer.Ordinal)),
            "plain language: the most promising option is ticked by default");

        var matched = PlainLanguage.Match(options, [.. options[0].Kinds]);
        check(matched is not null && matched.Kinds.All(options[0].Kinds.Contains),
            "plain language: the ticked set picks an option that turns off no more than what is ticked");
        check(PlainLanguage.Match(options, []) is null,
            "plain language: ticking nothing matches no option");
        var everySelected = PlainLanguage.Match(options, [.. options.SelectMany(option => option.Kinds).Distinct(StringComparer.Ordinal)]);
        check(everySelected is not null && everySelected.Kinds.Length == options.Max(option => option.Kinds.Length),
            "plain language: ticking everything picks the option that turns off the most");
    }
}
