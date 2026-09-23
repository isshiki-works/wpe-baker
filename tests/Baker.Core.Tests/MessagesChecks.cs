using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.Core;

/// <summary>文案表、一行结论、层名截断与 --lang 解析。</summary>
internal static class MessagesChecks
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    private static int[] Indexes(string template) =>
        Placeholder.Matches(template).Select(match => int.Parse(match.Groups[1].Value)).Distinct().Order().ToArray();

    /// <summary>
    /// C0.2（决策 7）起只查文案表的结构与按 key 的路由：键存在、中英非空、占位符一致、源码里引用的键都在表里，
    /// 以及 verdict/key/参数的走向。句子原文不再逐字断言；要比对文案时引用表里的条目，不写字面句子。
    /// </summary>
    public static void Run(Action<bool, string> Check)
    {
        // ---- 文案表本身 ----
        Check(MessageCatalog.Keys.Count > 0, "messages table is not empty");

        bool placeholdersAgree = true, legacyWithinRange = true;
        var offenders = new List<string>();
        foreach (string key in MessageCatalog.Keys)
        {
            var entry = MessageCatalog.Find(key)!;
            int[] zh = Indexes(entry.Zh), en = Indexes(entry.En);
            if (!zh.SequenceEqual(en)) { placeholdersAgree = false; offenders.Add(key); }
            int highest = Math.Max(zh.Length == 0 ? -1 : zh[^1], Indexes(entry.LegacyTemplate).LastOrDefault(-1));
            if (highest >= 8) { legacyWithinRange = false; offenders.Add(key); }
        }
        Check(placeholdersAgree, "zh and en of each key use the same format placeholders");
        Check(legacyWithinRange, "message placeholder indexes stay within a small argument range");

        // 源码里按字面量引用的键都必须在表里（无未知键）。
        string source = Path.Combine(LocalTools.RepositoryRoot, "src");
        string objSegment = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        var referenced = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(objSegment, StringComparison.Ordinal))
            .SelectMany(file => KeyReference.Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .Distinct().ToArray();
        string[] unknownKeys = referenced.Where(key => MessageCatalog.Find(key) is null).ToArray();
        Check(referenced.Length > 50 && unknownKeys.Length == 0,
            "every message key referenced by a literal in src exists in the table: " + string.Join(", ", unknownKeys));

        // ---- 语言解析 ----
        Check(MessageCatalog.NormalizeLanguage("zh") == "zh" && MessageCatalog.NormalizeLanguage("zh-CN") == "zh" &&
            MessageCatalog.NormalizeLanguage("zh-Hans-CN") == "zh" && MessageCatalog.NormalizeLanguage("en") == "en" &&
            MessageCatalog.NormalizeLanguage("en-US") == "en" && MessageCatalog.NormalizeLanguage(null) == "en" &&
            MessageCatalog.NormalizeLanguage("de-DE") == "en" && MessageCatalog.NormalizeLanguage("ZH-cn") == "zh",
            "language tags normalize to zh or en");

        // ---- 未知 key 与参数缺失都不抛异常 ----
        Check(MessageCatalog.Get("blocker.does_not_exist", "zh") == "blocker.does_not_exist", "unknown key falls back to the key itself");
        Check(MessageCatalog.RenderLegacy("blocker.does_not_exist") == "blocker.does_not_exist", "rendering an unknown key does not throw");
        Check(MessageCatalog.Get("summary.blocked", "zh").Contains("{0}", StringComparison.Ordinal),
            "a parameterised template with no arguments renders as the template");

        // ---- 反查：Localize 找回 key ----
        var groups = new JsonArray(new JsonObject { ["transparent"] = true });
        Blocker legacyFullFrameBlocker = PlanNarrative.FullFrameConflict(groups, ["Clock", "Spectrum"]);
        string legacyFullFrame = legacyFullFrameBlocker.Text;
        JsonObject localizedFullFrame = legacyFullFrameBlocker.ToNode();
        Check(localizedFullFrame["key"]?.GetValue<string>() == "blocker.fullframe_single_transparent_group_live",
            "a single transparent group with interleaved layers maps to its own key");
        Check(localizedFullFrame["params"] is JsonArray parameters && parameters.Count == 3,
            "params exposes only the arguments the bilingual text uses, not the legacy-only segment");
        Check(PlanNarrative.FullFrameConflict(new JsonArray(), []).Code == BlockerCode.FullframeNoVideoGroup,
            "zero groups maps to its own key");


        // ---- 层名截断与转义 ----
        string[] many = Enumerable.Range(1, 28).Select(index => "Layer " + index).ToArray();
        string list = MessageCatalog.NameList(many);
        Check(list.Split('"').Length - 1 == 10 && list.EndsWith('…'), "at most five layer names are listed, then an ellipsis");
        Check(!MessageCatalog.NameList(["one\ntwo"]).Contains('\n'), "a layer list never contains a newline");

        // ---- summary.verdict 映射 ----
        Check(Verdict(Plan(blockers: [Hdr], candidates: 0)) == "blocked",
            "blockers present yields verdict blocked");
        Check(Verdict(Plan(blockers: [Perspective, new Blocker(BlockerCode.CameraPathNeedsEnvelope)], candidates: 2)) == "blocked", "blockers win over candidates");
        Check(Verdict(Plan(blockers: [], candidates: 1)) == "bakeable", "a candidate without blockers yields verdict bakeable");
        Check(Verdict(Plan(blockers: [], candidates: 0)) == "unknown", "no candidate and no blocker yields verdict unknown");

        JsonObject blocked = PlanNarrative.Summarize(Plan(blockers: [Perspective, new Blocker(BlockerCode.CameraPathNeedsEnvelope)], candidates: 0));
        Check(blocked["key"]?.GetValue<string>() == "summary.blocked",
            "blocked summary uses the localized first blocker and the blocker count");

        JsonObject bakeable = PlanNarrative.Summarize(Plan(blockers: [], candidates: 1));
        Check(bakeable["key"]?.GetValue<string>() == "summary.bakeable",
            "bakeable summary carries seconds, frames, fps, retime and live layers in both languages");

        JsonObject stillPlan = Plan(blockers: [], candidates: 1);
        stillPlan["loop"]!["candidates"]!.AsArray()[0]!["frames"] = 1;
        Check(PlanNarrative.Summarize(stillPlan)["key"]?.GetValue<string>() == "summary.bakeable_static",
            "a single-frame candidate is described as a still image");

        JsonObject unknownPlan = Plan(blockers: [], candidates: 0);
        var omitted = new Message("unresolved.material_omits_active_uniforms");
        var unknownNotes = new UnresolvedNotes();
        Baker.Core.Verdict.AddLoopUnresolved(unknownPlan, "runtime_animation", omitted.Text, unknownNotes, omitted.Localized());
        Check(unknownPlan["loop"]!["unresolved"]![0]!.AsObject().Select(x => x.Key).SequenceEqual(["kind", "detail"]),
            "the appended unresolved item carries only its v3 fields");
        PlanNarrative.Attach(unknownPlan, unknownNotes);
        JsonObject unknownSummary = unknownPlan["summary"]!.AsObject();
        Check(unknownPlan["loop"]!["unresolved_localized"]![0]!["key"]?.GetValue<string>() == "unresolved.material_omits_active_uniforms",
            "Attach renders the typed message of each unresolved item into unresolved_localized");
        Check(unknownSummary["verdict"]!.GetValue<string>() == "unknown" &&
            unknownSummary["key"]?.GetValue<string>() != "summary.unknown_no_reason",
            "an unknown verdict carries the localized unresolved reason");
        Check(PlanNarrative.Summarize(Plan(blockers: [], candidates: 0))["key"]?.GetValue<string>() == "summary.unknown_no_reason",
            "an unknown verdict with nothing to report says so instead of throwing");

        // ---- Attach 只添加字段，不动现有英文 ----
        JsonObject attached = Plan(blockers: [Hdr], candidates: 0);
        PlanNarrative.Attach(attached);
        Check(attached["blockers"] is JsonArray { Count: 1 } blockerTexts && blockerTexts[0]!.GetValue<string>() == Hdr.Text,
            "the plan carries the v3 english blockers array rendered from the blocker codes");
        Check(attached["blockers_localized"] is JsonArray { Count: 1 } &&
            attached["blockers_localized"]![0]!["key"]!.GetValue<string>() == "blocker.hdr_radiance_open",
            "blockers_localized is a parallel array keyed by the blocker code");
        Check(attached["summary"]?["verdict"]?.GetValue<string>() == "blocked" &&
            attached["summary"]?["zh"] is not null && attached["summary"]?["en"] is not null,
            "summary carries a verdict plus both languages");
        Check(attached["loop"]?["unresolved_localized"] is JsonArray, "loop carries unresolved_localized");

        // ---- 各路 blocker 反查到自己的 key，参数带进双语 ----
        JsonObject unreachableLocalized = new Blocker(BlockerCode.FullframeUnreachable,
            ["\"Clock\" (wall_clock_api)", "turn off the clock, date and system readouts: --exclude-layers 12"],
            ["\"Clock\" (wall_clock_api)", "关掉时钟/日期/系统信息：--exclude-layers 12"]).ToNode();
        Check(unreachableLocalized["key"]?.GetValue<string>() == "blocker.fullframe_unreachable",
            "the unreachable full-frame blocker resolves to its key and carries its arguments");

        Blocker withOptionsBlocker = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject { ["transparent"] = false }, new JsonObject()), ["Clock"],
            ("用 --video-layout layered 显式选择分层视频", "explicitly choose layered video with --video-layout layered"));
        string withOptions = withOptionsBlocker.Text;
        JsonObject optionsLocalized = withOptionsBlocker.ToNode();
        Check(optionsLocalized["key"]?.GetValue<string>() == "blocker.fullframe_needs_opaque_group_options",
            "the full-frame blocker with scene options renders english and chinese options side by side");

        JsonObject audioLocalized = new Message("unresolved.particle_audio_input",
            ["an unnamed particle node", "3"], ["一个未命名的粒子节点", "3"]).Localized();
        Check(audioLocalized["key"]?.GetValue<string>() == "unresolved.particle_audio_input",
            "the particle audio-input reason names the node in chinese too");

        string[] particleKeys = ["unresolved.particle_definition_missing", "unresolved.particle_definition_unreadable",
            "unresolved.particle_audio_input", "unresolved.particle_turbulent_velocity", "unresolved.particle_random_frame",
            "unresolved.particle_random_initializer", "unresolved.particle_emitter_extent",
            "unresolved.particle_effective_period_not_modelled"];
        Check(particleKeys.All(key => MessageCatalog.Find(key) is not null), "every particle nonperiodic reason has a table entry");

        // HDR 拒绝：legacy 行以 SdrRadianceClosure.HdrBlocker 开头（能力缺口分诊按前缀识别），未通过明细原样带进中文。
        const string unproven = "group-0 layer 7 \"Glow\": additive blending is not an alpha convex combination (R2).";
        var hdrScene = new JsonObject { ["general"] = new JsonObject { ["hdr"] = true } };
        Blocker plainBlocker = PlanNarrative.HdrRadianceOpen(hdrScene, new JsonObject(), unproven);
        string plain = plainBlocker.Text;
        JsonObject plainLocalized = plainBlocker.ToNode();
        Check(plain.StartsWith(SdrRadianceClosure.HdrBlocker, StringComparison.Ordinal) &&
            plainLocalized["key"]?.GetValue<string>() == "blocker.hdr_radiance_open",
            "the radiance-closure blocker keeps the hdr prefix and carries the unproven detail into chinese");

        Blocker namedBlocker = PlanNarrative.HdrRadianceOpen(
            new JsonObject { ["general"] = new JsonObject { ["hdr"] = new JsonObject { ["user"] = "hdrmode" } } },
            new JsonObject(), unproven);
        string named = namedBlocker.Text;
        JsonObject namedLocalized = namedBlocker.ToNode();
        Check(named == plain &&
            namedLocalized["key"]?.GetValue<string>() == "blocker.hdr_radiance_open_property",
            "naming the wallpaper's own hdr switch changes the bilingual text but never the legacy blocker line");

        JsonObject conditioned = PlanNarrative.HdrRadianceOpen(
            new JsonObject { ["general"] = new JsonObject { ["hdr"] = new JsonObject {
                ["user"] = new JsonObject { ["name"] = "mode", ["condition"] = "2" }, ["value"] = true } } },
            new JsonObject { ["general"] = new JsonObject { ["properties"] = new JsonObject {
                ["mode"] = new JsonObject { ["text"] = "Quality" } } } }, unproven).ToNode();
        Check(conditioned["key"]?.GetValue<string>() == "blocker.hdr_radiance_open_property",
            "a condition-bound hdr switch reports the interface label and its condition value");

        // 场景的 hdr 是字面量 true 时，名字带 hdr 的属性不是 HDR 开关，不得点名。
        JsonObject literalHdr = PlanNarrative.HdrRadianceOpen(hdrScene, new JsonObject { ["general"] = new JsonObject {
            ["properties"] = new JsonObject {
                ["enablehdr"] = new JsonObject { ["text"] = "HDR 模式" },
                ["bloomrequiredforhdr"] = new JsonObject { ["text"] = "Bloom (required for HDR)" } } } }, unproven).ToNode();
        Check(literalHdr["key"]?.GetValue<string>() == "blocker.hdr_radiance_open",
            "a literal hdr flag never names a look-alike project property as the HDR switch");

        // ---- summary.verdict 以 suitability 的裁定为准 ----
        JsonObject ruled = Plan(blockers: [], candidates: 1);
        ruled["suitability"] = new JsonObject { ["verdict"] = "not_suitable", ["rule"] = "no_temporal_mechanism_in_video" };
        JsonObject ruledSummary = PlanNarrative.Summarize(ruled);
        Check(ruledSummary["verdict"]!.GetValue<string>() == "not_suitable" &&
            ruledSummary["key"]!.GetValue<string>().StartsWith("summary.bakeable", StringComparison.Ordinal),
            "summary verdict follows the suitability ruling while the sentence still describes the plan");
        Check(Verdict(Plan(blockers: [], candidates: 1)) == "bakeable",
            "a plan without suitability keeps the narrative verdict");
    }

    /// <summary>源码里对文案表的字面量引用：new Message("key"…) 与 MessageCatalog.RenderLegacy/Get/Find("key"…)。</summary>
    private static readonly Regex KeyReference = new(@"(?:new Message|MessageCatalog\.(?:RenderLegacy|Get|Find))\(\s*""([a-z_]+(?:\.[a-z0-9_]+)+)""",
        RegexOptions.CultureInvariant);

    private static string Verdict(JsonObject plan) => PlanNarrative.Summarize(plan)["verdict"]!.GetValue<string>();

    /// <summary>最小 plan 骨架：只放 summary 会读到的字段。</summary>
    private static readonly Blocker Hdr = new(BlockerCode.HdrRadianceOpen, ["group-1 layer 1 (R1)."], ["group-1 layer 1 (R1)."]);
    private static readonly Blocker Perspective = new(BlockerCode.PerspectiveNeedsScreenspace);

    private static JsonObject Plan(Blocker[] blockers, int candidates)
    {
        var candidateArray = new JsonArray();
        for (int index = 0; index < candidates; ++index)
            candidateArray.Add(new JsonObject { ["frames"] = 5775UL, ["seconds"] = 48.125, ["total_retime_cost_percent"] = 0.405568644905163 });
        var plan = new JsonObject {
            ["settings"] = new JsonObject { ["fps_numerator"] = 120, ["fps_denominator"] = 1 },
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 26, ["name"] = "Clock" }, new JsonObject { ["id"] = 27, ["name"] = "Spectrum" }),
            ["live_layer_ids"] = new JsonArray(26, 27),
            ["blockers"] = new JsonArray(),
            ["loop"] = new JsonObject { ["candidates"] = candidateArray, ["unresolved"] = new JsonArray() } };
        PlanBlockers.Set(plan, blockers);
        return plan;
    }
}
