using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.Core;

/// <summary>文案表、一行结论、层名截断与 --lang 解析。这里只查文案与输出结构，不碰任何判定。</summary>
internal static class MessagesChecks
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    private static int[] Indexes(string template) =>
        Placeholder.Matches(template).Select(match => int.Parse(match.Groups[1].Value)).Distinct().Order().ToArray();

    public static void Run(Action<bool, string> Check)
    {
        // ---- 文案表本身 ----
        Check(Messages.Keys.Count > 0, "messages table is not empty");

        bool allFilled = true, placeholdersAgree = true, legacyWithinRange = true;
        var offenders = new List<string>();
        foreach (string key in Messages.Keys)
        {
            var entry = Messages.Find(key)!;
            if (string.IsNullOrWhiteSpace(entry.Zh) || string.IsNullOrWhiteSpace(entry.En)) { allFilled = false; offenders.Add(key); }
            int[] zh = Indexes(entry.Zh), en = Indexes(entry.En);
            if (!zh.SequenceEqual(en)) { placeholdersAgree = false; offenders.Add(key); }
            // legacy 可以少用几个参数，但不能引用 Zh/En 之外更靠后的下标以外的东西：所有下标必须落在参数范围内。
            int highest = Math.Max(zh.Length == 0 ? -1 : zh[^1], Indexes(entry.LegacyTemplate).LastOrDefault(-1));
            if (highest >= 8) { legacyWithinRange = false; offenders.Add(key); }
        }
        Check(allFilled, "every message key has non-empty zh and en text");
        Check(placeholdersAgree, "zh and en of each key use the same format placeholders");
        Check(legacyWithinRange, "message placeholder indexes stay within a small argument range");

        // 中文文案里不应残留 legacy 英文句子；抽查几条改写过的
        Check(Messages.Get("blocker.hdr_unsupported", "zh").Contains("HDR", StringComparison.Ordinal) &&
            Messages.Get("blocker.hdr_unsupported", "zh").Contains("SDR", StringComparison.Ordinal) &&
            !Messages.Get("blocker.hdr_unsupported", "zh").Contains("radiance range", StringComparison.Ordinal),
            "chinese HDR text is a rewrite, not the legacy english sentence");

        // 改写后的英文必须给出真实开关名
        string fullFrameEn = Messages.Get("blocker.fullframe_single_transparent_group", "en");
        Check(fullFrameEn.Contains("--live-overlays foreground", StringComparison.Ordinal) &&
            fullFrameEn.Contains("--video-layout layered", StringComparison.Ordinal) &&
            fullFrameEn.Contains("transparent=true", StringComparison.Ordinal),
            "single transparent group text names the real switches and the transparent group");
        Check(Messages.Get("blocker.fullframe_single_transparent_group", "zh").Contains("--live-overlays foreground", StringComparison.Ordinal),
            "chinese single transparent group text names the real switch");

        // ---- 语言解析 ----
        Check(Messages.NormalizeLanguage("zh") == "zh" && Messages.NormalizeLanguage("zh-CN") == "zh" &&
            Messages.NormalizeLanguage("zh-Hans-CN") == "zh" && Messages.NormalizeLanguage("en") == "en" &&
            Messages.NormalizeLanguage("en-US") == "en" && Messages.NormalizeLanguage(null) == "en" &&
            Messages.NormalizeLanguage("de-DE") == "en" && Messages.NormalizeLanguage("ZH-cn") == "zh",
            "language tags normalize to zh or en");
        Check(Messages.Get("blocker.perspective_needs_screenspace", "zh") != Messages.Get("blocker.perspective_needs_screenspace", "en"),
            "zh and en render different text for the same key");

        // ---- 未知 key 与参数缺失都不抛异常 ----
        Check(Messages.Get("blocker.does_not_exist", "zh") == "blocker.does_not_exist", "unknown key falls back to the key itself");
        Check(Messages.Emit("blocker.does_not_exist") == "blocker.does_not_exist", "emitting an unknown key does not throw");
        Check(Messages.Get("summary.blocked", "zh").Contains("{0}", StringComparison.Ordinal),
            "a parameterised template with no arguments renders as the template");

        // ---- legacy 英文逐字不变 ----
        Check(Messages.Emit("blocker.hdr_unsupported") ==
            "HDR intermediate compositing is not supported by the current RGBA8 group capture; its radiance range must not be silently clipped into an SDR video.",
            "hdr blocker keeps its legacy english wording");
        Check(Messages.Emit("unresolved.particle_sprite_period") ==
            "A particle sprite texture period does not establish the particle system's effective period.",
            "particle sprite unresolved keeps its legacy english wording");
        Check(Messages.Emit("cli.not_scene_project") == "Scene projects only. Video/Web media compression was removed.",
            "cli rejection keeps its legacy english wording");
        Check(Messages.Emit("unresolved.material_temporal_uniforms", "background", "g_Time, g_Runtime") ==
            "Runtime background material uses unmodeled temporal uniforms: g_Time, g_Runtime.",
            "parameterised unresolved keeps its legacy english wording");

        var groups = new JsonArray(new JsonObject { ["transparent"] = true });
        string legacyFullFrame = PlanNarrative.FullFrameConflict(groups, ["Clock", "Spectrum"]);
        Check(legacyFullFrame == "Full-frame mode requires one opaque video group; the selected scene settings currently need 1 group(s) " +
            "or a transparent background. Interleaved realtime layers: Clock, Spectrum. Choose independent live overlays in the foreground " +
            "and analyze again, change the scene settings, or explicitly choose layered video. Foreground placement changes occlusion; " +
            "layered video requires separate playback-cost validation.",
            "full-frame conflict keeps its legacy english wording including the untruncated interleaved layer list");
        Check(PlanNarrative.FullFrameConflict(new JsonArray(new JsonObject { ["transparent"] = false }, new JsonObject()), []) ==
            "Full-frame mode requires one opaque video group; the selected scene settings currently need 2 group(s) or a transparent background. " +
            "Choose independent live overlays in the foreground and analyze again, change the scene settings, or explicitly choose layered video. " +
            "Foreground placement changes occlusion; layered video requires separate playback-cost validation.",
            "full-frame conflict without interleaved layers keeps its legacy english wording");

        // ---- 反查：Localize 找回 key 与双语 ----
        JsonObject localizedFullFrame = Messages.Localize(legacyFullFrame);
        Check(localizedFullFrame["key"]?.GetValue<string>() == "blocker.fullframe_single_transparent_group_live",
            "a single transparent group with interleaved layers maps to its own key");
        Check(localizedFullFrame["zh"]!.GetValue<string>().Contains("唯一的可录分组为透明（transparent=true）", StringComparison.Ordinal),
            "chinese text states that the only group is transparent");
        Check(!localizedFullFrame["zh"]!.GetValue<string>().Contains("transparent background", StringComparison.Ordinal) &&
            !localizedFullFrame["en"]!.GetValue<string>().Contains("or a transparent background", StringComparison.Ordinal),
            "the rewrite no longer offers a transparent background as an acceptable alternative");
        Check(localizedFullFrame["params"] is JsonArray parameters && parameters.Count == 3,
            "params exposes only the arguments the bilingual text uses, not the legacy-only segment");
        Check(PlanNarrative.FullFrameConflict(new JsonArray(), []) is string empty &&
            Messages.Localize(empty)["key"]?.GetValue<string>() == "blocker.fullframe_no_video_group",
            "zero groups maps to its own key");

        JsonObject unknown = Messages.Localize("Something no version of this table has ever produced.");
        Check(unknown["key"] is null && unknown["zh"]?.GetValue<string>() == "Something no version of this table has ever produced." &&
            unknown["en"]?.GetValue<string>() == "Something no version of this table has ever produced.",
            "an unknown blocker falls back to its english text without throwing");
        Check(Messages.Localize(null)["key"] is null, "localizing null does not throw");
        Check(Messages.Localize("Scene projects only. Video/Web media compression was removed.")["key"]?.GetValue<string>() == "cli.not_scene_project",
            "a legacy sentence produced by another process is still recognised");

        // ---- 层名截断与转义 ----
        string[] many = Enumerable.Range(1, 28).Select(index => "Layer " + index).ToArray();
        string list = Messages.NameList(many);
        Check(list.Split('"').Length - 1 == 10 && list.EndsWith('…'), "at most five layer names are listed, then an ellipsis");
        Check(Messages.NameList(["a", "b"]) == "\"a\", \"b\"", "a short list is not truncated");
        Check(Messages.EscapeName("Matrix spawner\r\nREADNAME --> (place \"top left\")") ==
            "Matrix spawner READNAME -> (place 'top l…", "newlines, arrows and quotes in a layer name are escaped and the name is capped");
        Check(!Messages.NameList(["one\ntwo"]).Contains('\n'), "a layer list never contains a newline");

        // ---- summary.verdict 映射 ----
        Check(Verdict(Plan(blockers: ["HDR intermediate compositing is not supported by the current RGBA8 group capture; " +
            "its radiance range must not be silently clipped into an SDR video."], candidates: 0)) == "blocked",
            "blockers present yields verdict blocked");
        Check(Verdict(Plan(blockers: ["one", "two"], candidates: 2)) == "blocked", "blockers win over candidates");
        Check(Verdict(Plan(blockers: [], candidates: 1)) == "bakeable", "a candidate without blockers yields verdict bakeable");
        Check(Verdict(Plan(blockers: [], candidates: 0)) == "unknown", "no candidate and no blocker yields verdict unknown");

        JsonObject blocked = PlanNarrative.Summarize(Plan(blockers: [Messages.Emit("blocker.perspective_needs_screenspace"), "second"], candidates: 0));
        Check(blocked["zh"]!.GetValue<string>().Contains("3D 透视镜头", StringComparison.Ordinal) &&
            blocked["zh"]!.GetValue<string>().Contains("共 2 条", StringComparison.Ordinal),
            "blocked summary uses the localized first blocker and the blocker count");
        Check(blocked["en"]!.GetValue<string>().StartsWith("Cannot generate:", StringComparison.Ordinal), "english blocked summary leads with the verdict");

        JsonObject plan = Plan(blockers: [], candidates: 1);
        JsonObject bakeable = PlanNarrative.Summarize(plan);
        Check(bakeable["zh"]!.GetValue<string>().Contains("48.1 s", StringComparison.Ordinal) &&
            bakeable["zh"]!.GetValue<string>().Contains("5775 帧", StringComparison.Ordinal) &&
            bakeable["zh"]!.GetValue<string>().Contains("@120 fps", StringComparison.Ordinal) &&
            bakeable["zh"]!.GetValue<string>().Contains("0.41", StringComparison.Ordinal) &&
            bakeable["zh"]!.GetValue<string>().Contains("\"Clock\"", StringComparison.Ordinal) &&
            bakeable["zh"]!.GetValue<string>().Contains("接缝", StringComparison.Ordinal),
            "bakeable summary states seconds, frames, fps, retime, live layers and the seam");
        Check(bakeable["en"]!.GetValue<string>().Contains("loop period 48.1 s", StringComparison.Ordinal) &&
            bakeable["en"]!.GetValue<string>().Contains("5775 frames @120 fps", StringComparison.Ordinal),
            "english bakeable summary states the same numbers");

        JsonObject stillPlan = Plan(blockers: [], candidates: 1);
        stillPlan["loop"]!["candidates"]!.AsArray()[0]!["frames"] = 1;
        Check(PlanNarrative.Summarize(stillPlan)["zh"]!.GetValue<string>().Contains("静态画面，仅需 1 帧", StringComparison.Ordinal),
            "a single-frame candidate is described as a still image");

        JsonObject unknownPlan = Plan(blockers: [], candidates: 0);
        unknownPlan["loop"]!["unresolved"]!.AsArray().Add(new JsonObject {
            ["kind"] = "runtime_animation", ["detail"] = Messages.Emit("unresolved.particle_sprite_period") });
        JsonObject unknownSummary = PlanNarrative.Summarize(unknownPlan);
        Check(unknownSummary["verdict"]!.GetValue<string>() == "unknown" &&
            unknownSummary["zh"]!.GetValue<string>().Contains("粒子精灵纹理周期不等于粒子系统的有效周期", StringComparison.Ordinal),
            "an unknown verdict carries the localized unresolved reason");
        Check(PlanNarrative.Summarize(Plan(blockers: [], candidates: 0))["zh"]!.GetValue<string>()
            .Contains("分析未给出内部原因", StringComparison.Ordinal),
            "an unknown verdict with nothing to report says so instead of throwing");

        // ---- Attach 只添加字段，不动现有英文 ----
        JsonObject attached = Plan(blockers: [Messages.Emit("blocker.hdr_unsupported")], candidates: 0);
        string before = attached["blockers"]!.ToJsonString();
        PlanNarrative.Attach(attached);
        Check(attached["blockers"]!.ToJsonString() == before, "attaching bilingual fields leaves the english blockers array untouched");
        Check(attached["blockers_localized"] is JsonArray { Count: 1 } &&
            attached["blockers_localized"]![0]!["zh"]!.GetValue<string>().Contains("场景为 HDR 合成", StringComparison.Ordinal),
            "blockers_localized is a parallel array with chinese text");
        Check(attached["summary"]?["verdict"]?.GetValue<string>() == "blocked" &&
            attached["summary"]?["zh"] is not null && attached["summary"]?["en"] is not null,
            "summary carries a verdict plus both languages");

        // ---- 效果前缀路线的省电提醒 ----
        // 只有效果链的前缀被烘成视频、图层本身仍然实时跑，所以能不能省电取决于被烘走的那段占多少工作量。
        JsonObject prefixPlan = Plan(blockers: [], candidates: 1);
        prefixPlan["route"] = "effect_prefix";
        PlanNarrative.Attach(prefixPlan);
        Check(prefixPlan["summary"]!["zh"]!.GetValue<string>().Contains("效果前缀路线功耗收益有限：仅当被预渲染的效果链占渲染负载主要部分时收益显著", StringComparison.Ordinal) &&
            prefixPlan["summary"]!["en"]!.GetValue<string>().Contains("limited power saving; a measurable reduction requires the pre-rendered segment to dominate the render load", StringComparison.Ordinal),
            "an effect-prefix verdict says the saving depends on how much work is baked away");
        JsonObject wholePlan = Plan(blockers: [], candidates: 1);
        wholePlan["route"] = "whole_layer";
        PlanNarrative.Attach(wholePlan);
        Check(!wholePlan["summary"]!["zh"]!.GetValue<string>().Contains("效果前缀路线功耗收益有限", StringComparison.Ordinal),
            "the whole-layer route does not carry the effect-prefix caveat");
        JsonObject prefixBlocked = Plan(blockers: [Messages.Emit("blocker.hdr_unsupported")], candidates: 0);
        prefixBlocked["route"] = "effect_prefix";
        PlanNarrative.Attach(prefixBlocked);
        Check(!prefixBlocked["summary"]!["zh"]!.GetValue<string>().Contains("效果前缀路线功耗收益有限", StringComparison.Ordinal),
            "a refused effect-prefix plan is not told how much power it would save");
        Check(attached["loop"]?["unresolved_localized"] is JsonArray, "loop carries unresolved_localized");

        // ---- 第 1 批合并新增的文案与裁定口径 ----
        // 视频外壳：blocker 原文由 VideoDominance 给出且没有参数，文案表靠 legacy 原文就能反查出中文。
        JsonObject shell = Messages.Localize(VideoDominance.Blocker);
        Check(shell["key"]?.GetValue<string>() == "blocker.video_shell" &&
            shell["zh"]!.GetValue<string>() == VideoDominance.BlockerZh &&
            shell["en"]!.GetValue<string>() == VideoDominance.Blocker,
            "the video-shell blocker resolves to its bilingual entry by legacy text alone");

        // 全幅不可达：文案带参数，legacy 英文逐字不变（下游脚本按它对账），中英双语改成给出解锁路径
        //（feat/tradeoff-list：实测这一形态 4/4 能靠关掉挡路的可取舍元素进整幅）。
        string unreachable = Messages.EmitBilingual("blocker.fullframe_unreachable",
            ["\"Clock\" (wall_clock_api)", "关掉时钟/日期/系统信息：--exclude-layers 12"],
            ["\"Clock\" (wall_clock_api)", "turn off the clock, date and system readouts: --exclude-layers 12"]);
        JsonObject unreachableLocalized = Messages.Localize(unreachable);
        Check(unreachable.StartsWith("Full-frame mode needs the single video group", StringComparison.Ordinal) &&
            unreachable.EndsWith("This scene has no full-frame layout as authored.", StringComparison.Ordinal) &&
            unreachable.Contains("\"Clock\" (wall_clock_api)", StringComparison.Ordinal) &&
            unreachableLocalized["key"]?.GetValue<string>() == "blocker.fullframe_unreachable" &&
            unreachableLocalized["zh"]!.GetValue<string>().Contains("可选方案：禁用前置的实时元素后生成整幅循环视频", StringComparison.Ordinal) &&
            !unreachableLocalized["zh"]!.GetValue<string>().Contains("没有全幅布局", StringComparison.Ordinal) &&
            unreachableLocalized["zh"]!.GetValue<string>().Contains("--exclude-layers 12", StringComparison.Ordinal) &&
            unreachableLocalized["en"]!.GetValue<string>().Contains("Options: disable the blocking live elements", StringComparison.Ordinal) &&
            unreachableLocalized["zh"]!.GetValue<string>().Contains("\"Clock\" (wall_clock_api)", StringComparison.Ordinal),
            "the unreachable full-frame blocker keeps its legacy english and now points at a way to unlock");

        // 带本场景可执行选项的全幅冲突：legacy 写英文选项，中文那一半渲染中文选项。
        string withOptions = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject { ["transparent"] = false }, new JsonObject()), ["Clock"],
            ("用 --video-layout layered 显式选择分层视频", "explicitly choose layered video with --video-layout layered"));
        JsonObject optionsLocalized = Messages.Localize(withOptions);
        Check(withOptions.StartsWith("Full-frame mode requires one opaque video group", StringComparison.Ordinal) &&
            withOptions.Contains("Options: explicitly choose layered video", StringComparison.Ordinal) &&
            optionsLocalized["key"]?.GetValue<string>() == "blocker.fullframe_needs_opaque_group_options" &&
            optionsLocalized["zh"]!.GetValue<string>().Contains("用 --video-layout layered 显式选择分层视频", StringComparison.Ordinal) &&
            !optionsLocalized["zh"]!.GetValue<string>().Contains("explicitly choose", StringComparison.Ordinal) &&
            optionsLocalized["en"]!.GetValue<string>().Contains("explicitly choose layered video", StringComparison.Ordinal),
            "the full-frame blocker with scene options renders english legacy text and chinese options side by side");

        // feat/shader-verdict-dedup 改写过的运行时材质文案仍能反查到双语条目。
        Check(Messages.Localize("Runtime material omitted active_uniforms; it cannot establish a static or analyzed temporal state.")
                ["key"]?.GetValue<string>() == "unresolved.material_omits_active_uniforms",
            "the rewritten runtime-material detail resolves to a bilingual entry");

        // 第 2 批合并新增（feat/video-control-scope）：粒子证明不出周期时给出的具名理由也进文案表。
        // legacy 英文逐字沿用该分支的原文，中文由同一 key 给出；节点名这一半中英各一套。
        string audio = Messages.EmitBilingual("unresolved.particle_audio_input",
            ["一个未命名的粒子节点", "3"], ["an unnamed particle node", "3"]);
        JsonObject audioLocalized = Messages.Localize(audio);
        Check(audio == "A particle sprite texture period does not establish the particle system's effective period: " +
                "an unnamed particle node responds to the live audio spectrum (audioprocessingmode 3), " +
                "which is an external input with no source-provable period." &&
            audioLocalized["key"]?.GetValue<string>() == "unresolved.particle_audio_input" &&
            audioLocalized["zh"]!.GetValue<string>().Contains("一个未命名的粒子节点", StringComparison.Ordinal) &&
            !audioLocalized["zh"]!.GetValue<string>().Contains("an unnamed particle node", StringComparison.Ordinal),
            "the particle audio-input reason keeps its english wording and names the node in chinese too");

        Check(Messages.Emit("unresolved.particle_random_frame") ==
            "A particle sprite texture period does not establish the particle system's effective period: its sprite animation " +
            "runs in randomframe mode, so every particle starts on an unpredictable frame.",
            "the randomframe particle reason keeps its legacy english wording");

        string[] particleKeys = ["unresolved.particle_definition_missing", "unresolved.particle_definition_unreadable",
            "unresolved.particle_audio_input", "unresolved.particle_turbulent_velocity", "unresolved.particle_random_frame",
            "unresolved.particle_random_initializer", "unresolved.particle_emitter_extent",
            "unresolved.particle_effective_period_not_modelled"];
        Check(particleKeys.All(key => Messages.Find(key) is { } entry &&
                entry.En.StartsWith("A particle sprite texture period does not establish the particle system's effective period:",
                    StringComparison.Ordinal) &&
                entry.Zh.StartsWith("粒子精灵纹理周期不等于粒子系统的有效周期：", StringComparison.Ordinal)),
            "every particle nonperiodic reason states the same conclusion before naming its own cause");

        // 第 2 批合并新增（feat/sdr-closure）：拒绝理由改成"这一组的输出证明不了落在 [0,1] 内"。
        // legacy 英文必须仍以 SdrRadianceClosure.HdrBlocker 开头——HybridSuitability 的能力缺口分诊与
        // SdrRadianceClosureChecks 都按这个前缀识别——并把未通过明细原样带上。
        const string unproven = "group-0 layer 7 \"Glow\": additive blending is not an alpha convex combination (R2).";
        var hdrScene = new JsonObject { ["general"] = new JsonObject { ["hdr"] = true } };
        string plain = PlanNarrative.HdrRadianceOpen(hdrScene, new JsonObject(), unproven);
        JsonObject plainLocalized = Messages.Localize(plain);
        Check(plain == SdrRadianceClosure.HdrBlocker + " Unproven: " + unproven &&
            plainLocalized["key"]?.GetValue<string>() == "blocker.hdr_radiance_open" &&
            plainLocalized["zh"]!.GetValue<string>().Contains("未能证明输出落在 [0,1] 内", StringComparison.Ordinal) &&
            plainLocalized["zh"]!.GetValue<string>().Contains(unproven, StringComparison.Ordinal),
            "the radiance-closure blocker keeps the legacy hdr wording and carries the unproven detail into chinese");

        string named = PlanNarrative.HdrRadianceOpen(
            new JsonObject { ["general"] = new JsonObject { ["hdr"] = new JsonObject { ["user"] = "hdrmode" } } },
            new JsonObject(), unproven);
        JsonObject namedLocalized = Messages.Localize(named);
        Check(named == SdrRadianceClosure.HdrBlocker + " Unproven: " + unproven &&
            namedLocalized["key"]?.GetValue<string>() == "blocker.hdr_radiance_open_property" &&
            namedLocalized["zh"]!.GetValue<string>().Contains("\"hdrmode\"", StringComparison.Ordinal) &&
            namedLocalized["en"]!.GetValue<string>().Contains("an HDR property (\"hdrmode\";", StringComparison.Ordinal) &&
            namedLocalized["en"]!.GetValue<string>().StartsWith(SdrRadianceClosure.HdrBlocker, StringComparison.Ordinal),
            "naming the wallpaper's own hdr switch changes the bilingual text but never the legacy blocker line");

        // 绑定的开关要写清关闭值：无 condition 的 bool 绑定给出可直接写进 --properties 文件的 JSON。
        Check(namedLocalized["zh"]!.GetValue<string>().Contains("{\"hdrmode\": false}", StringComparison.Ordinal) &&
            namedLocalized["en"]!.GetValue<string>().Contains("{\"hdrmode\": false}", StringComparison.Ordinal),
            "a bound hdr switch names its off value as a ready-to-use --properties entry");

        // 带 condition 的绑定：hdr 只在属性值等于 condition 时为 true，关闭值是"任何别的值"。
        JsonObject conditioned = Messages.Localize(PlanNarrative.HdrRadianceOpen(
            new JsonObject { ["general"] = new JsonObject { ["hdr"] = new JsonObject {
                ["user"] = new JsonObject { ["name"] = "mode", ["condition"] = "2" }, ["value"] = true } } },
            new JsonObject { ["general"] = new JsonObject { ["properties"] = new JsonObject {
                ["mode"] = new JsonObject { ["text"] = "Quality" } } } }, unproven));
        Check(conditioned["key"]?.GetValue<string>() == "blocker.hdr_radiance_open_property" &&
            conditioned["zh"]!.GetValue<string>().Contains("\"Quality / mode\"", StringComparison.Ordinal) &&
            conditioned["zh"]!.GetValue<string>().Contains("不等于 \"2\" 就是关", StringComparison.Ordinal) &&
            conditioned["en"]!.GetValue<string>().Contains("is not \"2\"", StringComparison.Ordinal),
            "a condition-bound hdr switch reports the interface label and that any other value turns it off");

        // 场景的 hdr 是字面量 true 时，project.json 里名字带 hdr 的属性（bloom 开关、bloom 强度……）不是 HDR 开关：
        // 关掉它 hdr 仍是 true、判据照样跑（3695791724 的 bloomrequiredforhdr 实测），不得把它当开关点名。
        JsonObject literalHdr = Messages.Localize(PlanNarrative.HdrRadianceOpen(hdrScene, new JsonObject { ["general"] = new JsonObject {
            ["properties"] = new JsonObject {
                ["enablehdr"] = new JsonObject { ["text"] = "HDR 模式" },
                ["bloomrequiredforhdr"] = new JsonObject { ["text"] = "Bloom (required for HDR)" } } } }, unproven));
        Check(literalHdr["key"]?.GetValue<string>() == "blocker.hdr_radiance_open" &&
            !literalHdr["zh"]!.GetValue<string>().Contains("enablehdr", StringComparison.Ordinal) &&
            !literalHdr["zh"]!.GetValue<string>().Contains("bloomrequiredforhdr", StringComparison.Ordinal) &&
            !literalHdr["zh"]!.GetValue<string>().Contains("自带 HDR 开关", StringComparison.Ordinal),
            "a literal hdr flag never names a look-alike project property as the HDR switch");

        // ---- summary.verdict 以 suitability 的裁定为准 ----
        JsonObject ruled = Plan(blockers: [], candidates: 1);
        ruled["suitability"] = new JsonObject { ["verdict"] = "not_suitable", ["rule"] = "no_temporal_mechanism_in_video" };
        JsonObject ruledSummary = PlanNarrative.Summarize(ruled);
        Check(ruledSummary["verdict"]!.GetValue<string>() == "not_suitable" &&
            ruledSummary["key"]!.GetValue<string>().StartsWith("summary.bakeable", StringComparison.Ordinal) &&
            ruledSummary["zh"]!.GetValue<string>().Length > 0,
            "summary verdict follows the suitability ruling while the sentence still describes the plan");
        Check(Verdict(Plan(blockers: [], candidates: 1)) == "bakeable",
            "a plan without suitability keeps the narrative verdict");

    }

    private static string Verdict(JsonObject plan) => PlanNarrative.Summarize(plan)["verdict"]!.GetValue<string>();

    /// <summary>最小 plan 骨架：只放 summary 会读到的字段。</summary>
    private static JsonObject Plan(string[] blockers, int candidates)
    {
        var candidateArray = new JsonArray();
        for (int index = 0; index < candidates; ++index)
            candidateArray.Add(new JsonObject { ["frames"] = 5775UL, ["seconds"] = 48.125, ["total_retime_cost_percent"] = 0.405568644905163 });
        return new JsonObject {
            ["settings"] = new JsonObject { ["fps_numerator"] = 120, ["fps_denominator"] = 1 },
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 26, ["name"] = "Clock" }, new JsonObject { ["id"] = 27, ["name"] = "Spectrum" }),
            ["live_layer_ids"] = new JsonArray(26, 27),
            ["blockers"] = new JsonArray(blockers.Select(text => (JsonNode)JsonValue.Create(text)).ToArray()),
            ["loop"] = new JsonObject { ["candidates"] = candidateArray, ["unresolved"] = new JsonArray() } };
    }
}
