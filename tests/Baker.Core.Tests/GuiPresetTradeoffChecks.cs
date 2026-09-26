using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

/// 界面用的档位行与取舍清单投影（feat/gui-preset-tradeoff）：结论区那一行、清单分块与"按此方案重新分析"要填回的值。
internal static class GuiPresetTradeoffChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Listing(check);
        TwoAxes(check);
    }

    private static void TwoAxes(Action<bool, string> check)
    {
        // 界面控件经 AnalyzeOptions.ForDesktop → AnalyzeRequestFactory 生成请求（C2.5a，与 CLI 选项表同一个工厂）。
        static HybridAnalyzeRequest Gui(string preset, string interaction, bool custom, bool layered) =>
            AnalyzeRequestFactory.Build(AnalyzeOptions.ForDesktop(preset, interaction, true, layered, false, [], null, custom, 0, 0, null),
                "s", "a", "o", null, null, 60, 1, new JsonObject());
        var fixedQuality = Gui("quality", "fixed", false, false);
        var offEfficiency = Gui("efficiency", "off", false, false);
        check(fixedQuality is { Preset: "quality", Interaction: "fixed", LoopPreference: "quality", CustomSettings: false } &&
            offEfficiency is { Preset: "efficiency", Interaction: "off", LoopPreference: "performance", CustomSettings: false } &&
            fixedQuality.UserProperties is null && offEfficiency.ExcludedLayerIds is null,
            "GUI axes map independently to Core without implicitly excluding content or marking custom");
        check(Gui("quality", "fixed", true, true) is { CustomSettings: true, LayoutExplicit: true, VideoLayout: "layered" },
            "GUI advanced overrides are recorded separately from the preset");
        var plan = new JsonObject { ["preset_applied"] = "balanced", ["applied_tradeoffs"] = new JsonObject {
            ["turn_off_kinds"] = new JsonArray("parallax", "parallax"), ["daytime_state"] = "morning" },
            ["live_overlays_hoisted"] = new JsonArray(new JsonObject(), new JsonObject()) };
        check(PlainLanguage.AppliedChangeLines(plan, false).Length == 2 &&
            PlainLanguage.AppliedChangeLines(new JsonObject(), false).Length == 0,
            "GUI omitted card contains only omitted content and fixed state, not retained widgets");
        plan["suggested_change"] = new JsonObject { ["verified"] = true,
            ["settings"] = new JsonObject { ["interaction"] = "off", ["preset"] = "balanced" } };
        JsonObject? settings = AppJsonPresentation.SuggestedSettings(plan);
        check(settings?["interaction"]?.GetValue<string>() == "off" && settings["preset"]?.GetValue<string>() == "balanced",
            "GUI one-click suggestion exposes both verified axis settings");
        plan["suggested_change"]!["verified"] = false;
        check(AppJsonPresentation.SuggestedSettings(plan) is null, "GUI never applies an unverified suggestion");
        plan["suggested_change"]!["verified"] = true;
        plan["suggested_change"]!["settings"]!["interaction"] = "invalid";
        check(AppJsonPresentation.SuggestedSettings(plan) is null, "GUI refuses unsupported suggested axis values");
    }

    /// 清单：每个方案一块、界面顺序、要填回的属性关闭值与排除图层；主体类不出块。
    private static void Listing(Action<bool, string> check)
    {
        JsonObject Layer(int id, int? parent, string name, bool live, string[] reasons, JsonObject? property = null)
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
            Layer(30, 20, "时钟", live: true, ["observed_wall_clock"]),
            Layer(40, null, "音频十字架", live: true, ["active_shader_audio_spectrum"], new JsonObject {
                ["key"] = "audiocross", ["label"] = null, ["type"] = "bool", ["declared"] = true, ["binding"] = "self",
                ["bound_layer_id"] = 40, ["condition"] = null, ["current_value"] = true, ["off_value"] = false,
                ["status"] = "resolved", ["off_hint_zh"] = "关闭值 false", ["off_hint_en"] = "the off value is false" }));
        TradeoffOptions.Attach(plan);
        var views = AppJsonPresentation.TradeoffOptionViews(plan, false);
        int listed = plan[TradeoffOptions.Field]!["options"]!.AsArray().Count;
        check(views.Length == listed && listed > 0, "gui tradeoff list: one block per option in the plan");

        var audio = views.Single(view => view.ExcludeLayers.SequenceEqual([40]));
        check(audio.Properties.Length == 1 && audio.Properties[0].Key == "audiocross" &&
            audio.Properties[0].OffValue?.GetValue<bool>() == false,
            "gui tradeoff list: reanalyzing an option fills in the property off value");
        check(audio.Command.Contains("--exclude-layers 40") && !audio.FixedView,
            "gui tradeoff list: the command line is offered for copying");

        var parallax = views.Where(view => view.FixedView).ToArray();
        check(parallax.Length > 0 && parallax.All(view => view.Command.Contains("--interaction fixed")),
            "gui tradeoff list: an option that turns off parallax carries fixed_view");

        // 主体类：只显示那句拒绝说明，不出清单块。
        var subject = Plan(Layer(10, null, "指针着色器", live: true, ["active_shader_pointer_input"]));
        // v3 形态：blockers 英文原文与 blockers_localized 同下标成对；取舍清单按编号读，只有 blockers_localized 读不到。
        subject["blockers"] = new JsonArray(MessageCatalog.RenderLegacy("blocker.no_input_independent_group"));
        subject["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.no_input_independent_group" });
        TradeoffOptions.Attach(subject);
        check(AppJsonPresentation.TradeoffOptionViews(subject, false).Length == 0,
            "gui tradeoff list: dependency blockage shows the bounded conclusion and no speculative options");

        // 分段拼回去就是 CLI 的那一段话，两处永远同一份文案。
        var option = plan[TradeoffOptions.Field]!["options"]!.AsArray().OfType<JsonObject>().First();
        check(string.Join("", TradeoffOptions.OptionLines(option, MessageCatalog.Chinese).Select(part => part.Text)) ==
            option[MessageCatalog.Chinese]!.GetValue<string>(),
            "gui tradeoff list: the segments join back into the CLI sentence");
        check(TradeoffOptions.OptionLines(option, MessageCatalog.Chinese).Select(part => part.Field).Contains("retain_live"),
            "gui tradeoff list: the segments are labelled by field");
    }
}
