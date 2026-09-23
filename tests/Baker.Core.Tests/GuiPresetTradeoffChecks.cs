using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

/// 界面用的档位行与取舍清单投影（feat/gui-preset-tradeoff）：结论区那一行、清单分块与"按此方案重新分析"要填回的值。
internal static class GuiPresetTradeoffChecks
{
    internal static void Run(Action<bool, string> check)
    {
        ProfileLine(check);
        Listing(check);
        TwoAxes(check);
    }

    private static void TwoAxes(Action<bool, string> check)
    {
        var original = new HybridAnalyzeRequest(2, "s", "a", "o", SwayRetime: true);
        var fixedQuality = AppJsonPresentation.ConfigureAnalysis(original, "quality", "fixed", false, false);
        var offEfficiency = AppJsonPresentation.ConfigureAnalysis(original, "efficiency", "off", false, false);
        check(fixedQuality is { Preset: "quality", Interaction: "fixed", ViewMode: "fixed_view", CustomSettings: false } &&
            offEfficiency is { Preset: "efficiency", Interaction: "off", CustomSettings: false } &&
            fixedQuality.UserProperties is null && offEfficiency.ExcludedLayerIds is null,
            "GUI axes map independently to Core without implicitly excluding content or marking custom");
        check(AppJsonPresentation.ConfigureAnalysis(original, "quality", "fixed", true, true) is { CustomSettings: true, LayoutExplicit: true },
            "GUI advanced overrides are recorded separately from the preset");
        var plan = new JsonObject { ["preset_applied"] = "balanced", ["applied_tradeoffs"] = new JsonObject {
            ["turn_off_kinds"] = new JsonArray("parallax", "parallax"), ["daytime_state"] = "morning" },
            ["live_overlays_hoisted"] = new JsonArray(new JsonObject(), new JsonObject()) };
        check(PlainLanguage.PresetAppliedLine(plan, "balanced", false) == "" &&
            PlainLanguage.PresetAppliedLine(plan, "quality", false).Contains(AppJsonPresentation.PresetLabel(RetimeProfile.Balanced, false)) &&
            PlainLanguage.PresetAppliedLine(plan, "quality", true).Contains(AppJsonPresentation.PresetLabel(RetimeProfile.Balanced, true)),
            "GUI downgrade line is bilingual and hidden when requested and applied presets match");
        check(PlainLanguage.AppliedChangeLines(plan, false).Length == 2 &&
            !PlainLanguage.AppliedChangeLines(plan, false).Any(line => line.Contains("小组件")) &&
            PlainLanguage.AppliedChangeLines(plan, true).Any(line => line.Contains("morning")) &&
            PlainLanguage.AppliedChangeLines(new JsonObject(), false).Length == 0,
            "GUI omitted card contains only omitted content and fixed state, not retained widgets");
        check(AppJsonPresentation.NumberRows(plan, false).Any(row => row.Label == "置顶小组件数" && row.Value == "2"),
            "retained foreground widgets are counted in technical details");
        plan["video_groups"] = new JsonArray(new JsonObject { ["id"] = "video", ["static_verified"] = false },
            new JsonObject { ["id"] = "static", ["static_verified"] = true,
                ["static_verification"] = new JsonObject { ["basis"] = "source_and_runtime_static_proof" } });
        plan["route"] = "whole_layer";
        check(AppJsonPresentation.RouteSummary(plan, true).Contains("1 video groups · 1 static caches", StringComparison.Ordinal) &&
            AppJsonPresentation.NumberRows(plan, false).Any(row => row.Label == "静态缓存" && row.Value == "1"),
            "the details distinguish decoder videos from verified static caches");
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

    /// 结论区一行：档位、观感预算（带手动标记）、相位差（圈）、循环长度。
    private static void ProfileLine(Action<bool, string> check)
    {
        JsonObject Plan(RetimeProfile profile, double? drift) => new() {
            ["retime_profile"] = profile.ToJson(),
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject {
                ["seconds"] = 120.5, ["frames"] = 7230,
                ["sway_retime"] = drift is null ? null : new JsonObject { ["phase_drift_cycles"] = drift } }) } };

        var balanced = Plan(RetimeProfile.Resolve(RetimeProfile.Balanced, null, null, 2), 0.42);
        string zh = AppJsonPresentation.ProfileSummary(balanced, false);
        string en = AppJsonPresentation.ProfileSummary(balanced, true);
        check(zh.Contains(AppJsonPresentation.PresetLabel(RetimeProfile.Balanced, false)) && zh.Contains("3%") && zh.Contains("0.42") &&
            zh.Contains("120.5") && zh.Contains("600"),
            "gui profile line: Chinese names the preset, budget, phase drift and loop length");
        check(en.Contains(AppJsonPresentation.PresetLabel(RetimeProfile.Balanced, true)) && en.Contains("3%") && en.Contains("0.42") &&
            en.Contains("120.5"), "gui profile line: English says the same four things");
        check(!zh.Contains("手动") && !en.Contains("manual"), "gui profile line: a preset value is not marked manual");

        var overridden = Plan(RetimeProfile.Resolve(RetimeProfile.Balanced, 1.5, null, 2), 0.1);
        check(AppJsonPresentation.ProfileSummary(overridden, false).Contains("1.5%") && AppJsonPresentation.ProfileSummary(overridden, false).Contains("手动") &&
            AppJsonPresentation.ProfileSummary(overridden, true).Contains("1.5%") && AppJsonPresentation.ProfileSummary(overridden, true).Contains("manual"),
            "gui profile line: an advanced override is marked manual");

        var quality = Plan(RetimeProfile.Resolve(RetimeProfile.Quality, null, null, 2), null);
        string qualityZh = AppJsonPresentation.ProfileSummary(quality, false);
        check(qualityZh.Contains(AppJsonPresentation.PresetLabel(RetimeProfile.Quality, false)) && !qualityZh.Contains("3%") && !qualityZh.Contains("相位差"),
            "gui profile line: the quality preset has no budget threshold, and no phase drift without sway retime");
        check(AppJsonPresentation.ProfileSummary(new JsonObject(), false).Length == 0,
            "gui profile line: a plan analyzed without a preset shows nothing");
        check(AppJsonPresentation.PresetLabel(RetimeProfile.Efficiency, false).Length > 0 &&
            AppJsonPresentation.PresetLabel(RetimeProfile.Efficiency, false) != AppJsonPresentation.PresetLabel(RetimeProfile.Efficiency, true) &&
            AppJsonPresentation.PresetLabel(RetimeProfile.Efficiency, false) != AppJsonPresentation.PresetLabel(RetimeProfile.Balanced, false) &&
            AppJsonPresentation.PresetLabel(null, false).Length == 0,
            "gui profile line: preset labels are bilingual and empty when there is no preset");
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
        check(AppJsonPresentation.TradeoffHeader(plan, false).Contains(listed.ToString(System.Globalization.CultureInfo.InvariantCulture)) &&
            AppJsonPresentation.TradeoffHeader(plan, true).Contains(listed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "gui tradeoff list: the header counts the options in both languages");
        check(views.All(view => view.Title.Length > 0 && view.Lines.Length >= 4),
            "gui tradeoff list: every block has a title and the explanation lines");
        check(views.Select(view => view.Lines[^2]).Distinct().Count() == 1,
            "gui tradeoff list: every block carries the measured note that keeping layers live saves nothing");
        // 清单只过了分析这一关，卡片最后一句必须是烘制阶段仍可能失败。
        check(views.Select(view => view.Lines[^1]).Distinct().Count() == 1 && views[0].Lines[^1] != views[0].Lines[^2],
            "gui tradeoff list: every block ends with the bake-stage caveat");

        var audio = views.Single(view => view.ExcludeLayers.SequenceEqual([40]));
        check(audio.Properties.Length == 1 && audio.Properties[0].Key == "audiocross" &&
            audio.Properties[0].OffValue?.GetValue<bool>() == false,
            "gui tradeoff list: reanalyzing an option fills in the property off value");
        check(audio.Command.Contains("--exclude-layers 40") && !audio.FixedView,
            "gui tradeoff list: the command line is offered for copying");
        // 界面顺序：怎么关（属性优先于命令行）→ 连带关掉什么 → 关掉后的路线与残留 → 不省电提醒 → 烘制阶段免责。
        check(audio.Lines.Length == 7 && audio.Lines[0].Contains("audiocross") && audio.Lines[1].Contains("--exclude-layers 40") &&
            audio.Lines[3].Contains('2') && audio.Lines[^2] == views[0].Lines[^2],
            "gui tradeoff list: property before command line, collateral before the resulting route and residual");

        var parallax = views.Where(view => view.FixedView).ToArray();
        check(parallax.Length > 0 && parallax.All(view => view.Command.Contains("--interaction fixed")),
            "gui tradeoff list: an option that turns off parallax carries fixed_view");

        // 主体类：只显示那句拒绝说明，不出清单块。
        var subject = Plan(Layer(10, null, "指针着色器", live: true, ["active_shader_pointer_input"]));
        // v3 形态：blockers 英文原文与 blockers_localized 同下标成对；取舍清单按编号读，只有 blockers_localized 读不到。
        subject["blockers"] = new JsonArray(MessageCatalog.RenderLegacy("blocker.no_input_independent_group"));
        subject["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.no_input_independent_group" });
        TradeoffOptions.Attach(subject);
        check(AppJsonPresentation.TradeoffOptionViews(subject, false).Length == 0 &&
            AppJsonPresentation.TradeoffHeader(subject, false).Length > 0,
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
