using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.App;
using Baker.Core;

/// 界面说人话（feat/gui-plain-language）：结论三行、取舍卡片，以及"用户看得到的字符串里不许出现术语"的扫描。
internal static class PlainLanguageChecks
{
    internal static void Run(Action<bool, string> check)
    {
        BannedWords(check);
        Verdicts(check);
        NumberLine(check);
        TurnOffCard(check);
    }

    // ---------------------------------------------------------------------------------------
    // 禁用词扫描
    // ---------------------------------------------------------------------------------------

    /// 用户看得见的字符串里一个都不许出现的实现细节词。标准术语（循环周期、实时图层、着色器、粒子系统、核显功耗…）不在此列。
    private static readonly (string Name, Regex Pattern)[] Banned = [
        ("残差", new Regex("残差")),
        ("候选", new Regex("候选")),
        ("闭合", new Regex("闭合")),
        ("相位差", new Regex("相位差")),
        ("圈", new Regex("圈")),
        ("兜底", new Regex("兜底")),
        ("上限", new Regex("上限")),
        ("实时对象", new Regex("实时对象")),
        ("视频组/特效前缀缓存组", new Regex("(视频组|缓存 ?\\{?\\d*\\}? ?组|[0-9}] ?组)")),
        ("visible_property", new Regex("visible_property")),
        ("命令行参数", new Regex("--[A-Za-z]")),
        ("图层数字 ID", new Regex("(#\\d+|图层 ?\\d+|对象 ?\\d+|(?i:layer|owner) \\d+)")),
        ("plan/root/blocker/candidate/seam/residual/bakeable", new Regex(
            "(?i)(?<![A-Za-z])(plans?|roots?|blockers?|candidates?|seams?|residuals?|bakeable|phase drift|closure|cycles)(?![A-Za-z])")),
    ];

    /// feat/tool-register：聊天口吻词表。工具的状态输出是陈述句 + 依据 + 动作，不劝、不聊、不打比方、不称呼用户。
    /// 这一份比 <see cref="Banned"/> 管得宽：界面字符串之外，MessageCatalog.cs 里全部 Zh/En 文案也要过这一遍。
    private static readonly (string Name, Regex Pattern)[] ChatRegister = [
        ("烘/烘焙", new Regex("烘")),
        ("关掉/关了", new Regex("关(掉|了)")),
        ("称呼用户", new Regex("[你您]")),
        ("这张/这条路/那个", new Regex("(这张|这条路|那个|这一张)")),
        ("就能/什么都不剩", new Regex("(就能|什么都不剩|省不回来|帮不上忙)")),
        ("费电说法", new Regex("(费电|用得不多|值不值得|划算|白花|劝退)")),
        ("闲聊连接词", new Regex("(先说清楚|另外：|还要知道|真的|本来就|直说|有希望|一档|几样东西|见下|最干净的关法)")),
        ("做出来/做片子", new Regex("(做出来|做片子|做好的|做不了|能不能做)")),
        ("感叹与反问", new Regex("[！？]")),
        ("英文口语", new Regex("(?i)(?<![A-Za-z])(this one|really|to begin with|barely|a few things|the things below|worth baking|it can be baked|you|your|yours)(?![A-Za-z])")),
    ];

    /// 扫描要跳过的两处：折叠的技术细节面板（XAML 里用注释标出），以及贴给开发者的那段原文。
    private const string TechnicalBegin = "plain-language:tech-begin";
    private const string TechnicalEnd = "plain-language:tech-end";

    /// 界面文案的写法：XAML 里的文本属性，代码里的 L("中文", "English") 与 L(english, "中文", "English")。
    private static readonly Regex XamlText = new("\\b(Tag|Header|Content|Text|ToolTip)=\"([^\"]*)\"");
    private static readonly Regex Localized = new(
        "(?<![A-Za-z0-9_])L\\(\\s*(?:english\\s*,\\s*)?\\$?\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,\\s*\\$?\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\)");
    private static readonly Regex Placeholder = new("\\{[^}]*\\}");

    private static string AppSources([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "src", "Baker.App"));

    /// MessageCatalog.cs 里 new(Zh: "…", En: "…") 的两个自然语言串；Legacy 的历史英文原文不在其中。
    private static readonly Regex MessageText = new("\\b(Zh|En):\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

    private static string MessagesSource([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "src", "Baker.Core", "MessageCatalog.cs"));

    private static (string Source, string Text)[] MessageStrings() =>
        [.. MessageText.Matches(File.ReadAllText(MessagesSource()))
            .Select(match => ("MessageCatalog.cs", match.Groups[2].Value))];

    /// 扫描到的每条用户可见字符串，带上它是从哪来的。
    private static (string Source, string Text)[] VisibleStrings()
    {
        string directory = AppSources();
        var found = new List<(string Source, string Text)>();
        bool technical = false;
        foreach (string line in File.ReadAllLines(Path.Combine(directory, "MainWindow.xaml")))
        {
            if (line.Contains(TechnicalBegin, StringComparison.Ordinal)) { technical = true; continue; }
            if (line.Contains(TechnicalEnd, StringComparison.Ordinal)) { technical = false; continue; }
            if (technical) continue;
            foreach (Match match in XamlText.Matches(line))
            {
                string value = match.Groups[2].Value;
                if (value.StartsWith('{')) continue;
                foreach (string part in value.Split('|', '‖'))
                    if (part.Trim().Length > 0) found.Add(("MainWindow.xaml", part));
            }
        }
        foreach (string file in Directory.GetFiles(directory, "*.cs").Order(StringComparer.Ordinal))
            foreach (Match match in Localized.Matches(File.ReadAllText(file)))
                for (int group = 1; group <= 2; ++group)
                    found.Add((Path.GetFileName(file), match.Groups[group].Value));
        foreach (string key in PlainLanguage.GuiMessageKeys.Concat(new[] {
            "preset.generated", "preset.omitted", "preset.experimental", "preset.daytime", "preset.too_many_video_groups",
            "interaction.suggest_fixed", "interaction.suggest_off" }))
            foreach (string language in new[] { MessageCatalog.Chinese, MessageCatalog.English })
                found.Add(("MessageCatalog:" + key, MessageCatalog.Get(key, language)));
        return [.. found];
    }

    private static void BannedWords(Action<bool, string> check)
    {
        var strings = VisibleStrings();
        check(strings.Any(item => item.Source == "MainWindow.xaml") &&
            strings.Any(item => item.Source == "MainWindow.xaml.cs") &&
            strings.Any(item => item.Source == "PlainLanguage.cs") &&
            strings.Any(item => item.Source.StartsWith("MessageCatalog:", StringComparison.Ordinal)) &&
            strings.Length > 200,
            "plain language: the scan covers the window markup, its code-behind, the plain-language texts and the GUI message entries");
        var hits = new List<string>();
        foreach (var (source, text) in strings)
        {
            string cleaned = Placeholder.Replace(text, " ");
            foreach (var (name, pattern) in Banned)
                if (pattern.IsMatch(cleaned)) hits.Add($"{source}: [{name}] {text}");
        }
        check(hits.Count == 0, "tool register: no implementation detail in user-visible strings" +
            (hits.Count == 0 ? "" : " -> " + string.Join(" | ", hits.Take(12))));
        // 扫描本身要有牙：故意拿一条术语文案喂进去必须被抓住。
        check(Banned.Any(banned => banned.Pattern.IsMatch("源周期候选 120.5 秒，相位差 0.42 圈")),
            "plain language: the scan actually catches jargon");

        // feat/tool-register：聊天口吻扫描。界面字符串 + MessageCatalog.cs 全部 Zh/En 文案。
        var everything = strings.Concat(MessageStrings()).ToArray();
        check(everything.Length > strings.Length + 300 &&
            everything.Any(item => item.Source == "MessageCatalog.cs") &&
            everything.Any(item => item.Source == "MainWindow.xaml") &&
            everything.Any(item => item.Source == "PlainLanguage.cs"),
            "tool register: the scan covers the markup, the plain-language texts and every MessageCatalog entry -> " + everything.Length);
        var chatty = new List<string>();
        foreach (var (source, text) in everything)
        {
            string cleaned = Placeholder.Replace(text, " ");
            foreach (var (name, pattern) in ChatRegister)
                if (pattern.IsMatch(cleaned)) chatty.Add($"{source}: [{name}] {text}");
        }
        check(chatty.Count == 0, "tool register: no chat register in user-visible strings" +
            (chatty.Count == 0 ? "" : " (" + chatty.Count + ") -> " + string.Join(" | ", chatty.Take(12))));
        check(ChatRegister.Any(banned => banned.Pattern.IsMatch("这张能烘，关掉几样东西就能整张录成视频。")) &&
            ChatRegister.Any(banned => banned.Pattern.IsMatch("It can be baked, but a few things have to be turned off first.")),
            "tool register: the scan actually catches chat register");
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
        check(PlainLanguage.Verdict(null, false).Length == 0 && PlainLanguage.NextAction(null, false).Length == 0 &&
            PlainLanguage.Basis(null, false).Length == 0, "plain language: nothing analyzed yet says nothing");

        // 能烘：没有阻塞、有循环、整幅路线，取舍清单判成"不需要"。
        var bakeable = Plan(Layer(10, null, "底", live: false, []));
        bakeable["settings"]!["video_layout"] = "full_frame";
        TradeoffOptions.Attach(bakeable);
        check(PlainLanguage.Verdict(bakeable, false) == "可以生成" && PlainLanguage.Verdict(bakeable, true) == "Ready to generate" &&
            PlainLanguage.NextAction(bakeable, false) == "点击\"生成\"开始" &&
            PlainLanguage.Basis(bakeable, false).Length > 0,
            "tool register: a clean plan reports one fixed state, an action line and the basis in the details");

        var tradeoff = TradeoffPlan();
        check(PlainLanguage.Verdict(tradeoff, false).StartsWith("可以生成（需先禁用 ", StringComparison.Ordinal) &&
            PlainLanguage.Verdict(tradeoff, true).StartsWith("Ready to generate (", StringComparison.Ordinal) &&
            PlainLanguage.NextAction(tradeoff, false) == "先在下方禁用列出的项目，然后重新分析",
            "tool register: a plan with tradeoff options reports the item count and one action");
        var applied = bakeable.DeepClone().AsObject();
        applied["preset_applied"] = "quality";
        applied[TradeoffOptions.Field] = tradeoff[TradeoffOptions.Field]!.DeepClone();
        check(PlainLanguage.Verdict(applied, false) == "可以生成" && PlainLanguage.NextAction(applied, true) == "Use Generate to start",
            "integrated GUI does not mistake optional tradeoffs for unapplied requirements");
        applied["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.loop_unresolved" });
        applied["preset_applied"] = "none";
        check(PlainLanguage.Verdict(applied, false) == "无法生成", "integrated GUI does not promote a rejected plan because tradeoff options exist");

        // 主体类：整张画面就是那个实时效果画出来的。
        var subject = Plan(Layer(10, null, "指针着色器", live: true, ["active_shader_pointer_input"]));
        // v3 形态：blockers 英文原文与 blockers_localized 同下标成对；取舍清单按编号读，只有 blockers_localized 读不到。
        subject["blockers"] = new JsonArray(MessageCatalog.RenderLegacy("blocker.no_input_independent_group"));
        subject["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.no_input_independent_group" });
        TradeoffOptions.Attach(subject);
        check(PlainLanguage.Verdict(subject, false) == "无法生成" && PlainLanguage.NextAction(subject, false) == "原因见\"详情\"" &&
            PlainLanguage.Basis(subject, false).Contains("结果尚未确认") &&
            !PlainLanguage.HasTurnOffCard(subject) && PlainLanguage.TurnOffItems(subject, false).Length == 0,
            "plain language: dependency blockage does not claim that disabling effects leaves no content");

        // 3D 镜头。
        var camera = Plan(Layer(10, null, "底", live: false, []));
        camera["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.perspective_needs_screenspace" });
        TradeoffOptions.Attach(camera);
        check(PlainLanguage.Verdict(camera, false) == "无法生成" && PlainLanguage.Basis(camera, false).Contains("3D 摄像机") &&
            PlainLanguage.Basis(camera, true).Contains("3D camera"),
            "tool register: a moving 3D camera is stated as the cause, in the details");

        // 找不到循环。
        var noLoop = Plan(Layer(10, null, "底", live: false, []));
        noLoop["loop"] = new JsonObject { ["candidates"] = new JsonArray(),
            ["no_candidate_reason"] = new JsonObject { ["kind"] = "NoTemporalMechanism" } };
        noLoop["suitability"] = new JsonObject { ["verdict"] = "not_suitable", ["rule"] = "fixed_period_exceeds_loop_ceiling" };
        TradeoffOptions.Attach(noLoop);
        check(PlainLanguage.Basis(noLoop, false).Contains("未找到循环周期") &&
            PlainLanguage.Basis(noLoop, true).Contains("no loop period") && PlainLanguage.Verdict(noLoop, false) == "无法生成",
            "tool register: a missing loop period is named in the details");

        // Old source-only power verdicts remain readable without inheriting their unsupported benefit inference.
        var cheap = Plan(Layer(10, null, "底", live: false, []));
        cheap["settings"]!["video_layout"] = "full_frame";
        TradeoffOptions.Attach(cheap);
        cheap["source_power"] = new JsonObject { ["verdict"] = new JsonObject {
            ["status"] = "measured", ["worth_baking"] = false } };
        check(PlainLanguage.Verdict(cheap, false) == "可以生成" &&
            PlainLanguage.Verdict(cheap, true) == "Ready to generate" &&
            PlainLanguage.Basis(cheap, false).Contains("仅代表本机"),
            "source-only power in older reports does not predict a lack of savings");
        cheap["source_power"] = new JsonObject { ["verdict"] = new JsonObject {
            ["status"] = "not_measured", ["worth_baking"] = null } };
        check(PlainLanguage.Verdict(cheap, false) == "可以生成",
            "plain language: without a measurement nothing is claimed about power");
        cheap.Remove("source_power");
        cheap["summary"]!["key"] = "summary.bakeable_static";
        check(PlainLanguage.Verdict(cheap, false) == "可以生成" && PlainLanguage.Basis(cheap, false).Contains("尚待确认"),
            "an older static result is not automatically described as having no savings");
        foreach (var (status, zh, en) in new[] {
            ("potential_gain", "可以生成，有潜在收益", "Ready to generate, potential benefit"),
            ("low_value", "可以生成，预计收益较低", "Ready to generate, low expected benefit"),
            ("unknown", "可以生成，收益待确认", "Ready to generate, benefit unconfirmed") })
        {
            cheap[BakeValueAssessment.Field] = new JsonObject { ["status"] = status, ["reason_zh"] = "已有元数据依据。", ["reason_en"] = "Recorded metadata evidence." };
            check(PlainLanguage.Verdict(cheap, false) == zh && PlainLanguage.Verdict(cheap, true) == en &&
                PlainLanguage.Basis(cheap, true) == "Recorded metadata evidence.",
                "benefit assessment is displayed independently of static output: " + status);
        }
        cheap["blockers_localized"] = new JsonArray(new JsonObject { ["key"] = "blocker.loop_unresolved" });
        check(PlainLanguage.Verdict(cheap, false) == "无法生成" && PlainLanguage.Basis(cheap, false).Contains("未找到循环周期"),
            "a failed plan keeps its actual failure reason rather than becoming a low-value verdict");
    }

    private static void NumberLine(Action<bool, string> check)
    {
        var plan = Plan(Layer(10, null, "底", live: false, []));
        string zh = PlainLanguage.Numbers(plan, false), en = PlainLanguage.Numbers(plan, true);
        check(zh == "循环周期 2 分 31 秒 · 画面差异：可忽略 · 60 fps · 1920×1080",
            "plain language: the number line reads as plain words -> " + zh);
        check(en == "loop period 2 min 31 s · frame difference: negligible · 60 fps · 1920×1080",
            "plain language: the English number line matches -> " + en);
        check(PlainLanguage.Duration(31, false) == "31 秒" && PlainLanguage.Duration(151, true) == "2 min 31 s",
            "plain language: short loops are written in seconds only");

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

        plan["settings"]!["fps_numerator"] = 60000;
        plan["settings"]!["fps_denominator"] = 1001;
        check(PlainLanguage.Numbers(plan, false).Contains("59.94 fps"),
            "plain language: an odd frame rate is shown as a number people know");
    }

    private static void TurnOffCard(Action<bool, string> check)
    {
        var plan = TradeoffPlan();
        var items = PlainLanguage.TurnOffItems(plan, false);
        check(items.Length >= 2 && items.All(item => item.Label.Length > 0 && item.Consequence.Length > 0),
            "plain language: every card row has a plain name and what you lose");
        check(items.Any(item => item.Kind == "parallax" && item.Label == "鼠标视差") &&
            items.Any(item => item.Kind == "audio" && item.Consequence.Contains("音频")) &&
            PlainLanguage.KindLabel("clock", false) == "时钟与日期" &&
            PlainLanguage.KindConsequence("clock", false).Contains("移除时钟显示"),
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

        string note = PlainLanguage.ResidualNote(matched, false);
        check(note.Contains("实时图层") && note.Contains("需重新分析"),
            "plain language: the card foot says what is still running afterwards -> " + note);
        check(PlainLanguage.ResidualNote(null, false).Length > 0 && PlainLanguage.ResidualNote(null, true).Length > 0,
            "plain language: with nothing ticked the card foot still says something");
    }
}
