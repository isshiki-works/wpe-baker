using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.Core;

/// <summary>
/// 用户可见文案的几处确定缺陷：中文通道混进英文拼接、全幅冲突"切成 N 块或者只留下透明背景"的并列措辞、
/// 结论不明的细分、bake --help 的 --encoder 说明。只查文案，不碰任何判定。
/// </summary>
internal static class NarrativePolishChecks
{
    private static readonly Type DemotionType = typeof(HybridPlanFormat).Assembly.GetType("Baker.Core.FullFrameDemotion")!;

    /// <summary>
    /// 中文文本里的英文泄漏：and N more、more unproven、英文 root，以及连续四个以上的小写英文单词（英文句子片段）。
    /// 引号里的层名、资源名先抹掉（作者起的英文名不算泄漏），命令行开关不会形成这种序列。
    /// </summary>
    private static readonly Regex EnglishLeak = new(
        @"\band \d+ more\b|\bmore unproven\b|\broots?\b|\b[a-z]+(?: [a-z]+){3,}\b", RegexOptions.CultureInvariant);

    internal static bool Leaks(string chinese) => EnglishLeak.IsMatch(Regex.Replace(chinese, "\"[^\"]*\"", "\"\"", RegexOptions.CultureInvariant));

    public static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        // ---- 泄漏检测器本身 ----
        check(Leaks("让这些 root (雨景远景, 雪景远景 and 2 more) 保持实时") &&
            Leaks("未通过的部分：group-1 layer 26: material combo \"version\" is not a known range-preserving combo (R1).") &&
            !Leaks("用 --video-layout layered 显式选择分层视频；图层 \"Rain downpour\", \"things in the air\" 保持实时"),
            "the english-leak detector flags joined english fragments but not layer names or switches");
        check(MessageCatalog.Keys.Where(key => !key.StartsWith("cli.", StringComparison.Ordinal))
                .All(key => !Leaks(MessageCatalog.Find(key)!.Zh)),
            "no chinese template in the messages table contains an english sentence fragment");

        // ---- ① 全幅冲突选项：中文通道用中文拼接层名 ----
        var retentionPlan = new JsonObject {
            ["settings"] = new JsonObject { ["video_layout"] = "full_frame" },
            ["full_frame_retention"] = new JsonObject { ["status"] = "available", ["root_ids"] = new JsonArray(1, 2, 3, 4, 5, 6, 7) },
            ["layers"] = new JsonArray(Enumerable.Range(1, 7).Select(id => (JsonNode)new JsonObject {
                ["id"] = id, ["root"] = id, ["name"] = "雨景 " + id, ["visible"] = true, ["drawable"] = true }).ToArray()) };
        var options = ((string Zh, string En))DemotionType.GetMethod("ConflictOptions", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [retentionPlan])!;
        check(Enumerable.Range(1, 5).All(i => options.Zh.Contains("雨景 " + i, StringComparison.Ordinal) && options.En.Contains("雨景 " + i, StringComparison.Ordinal)) &&
            !options.Zh.Contains("雨景 6", StringComparison.Ordinal) && !options.En.Contains("雨景 6", StringComparison.Ordinal) && !Leaks(options.Zh),
            "the retain-live option names at most five layers in both languages and the chinese channel carries no english fragment");

        Blocker conflictBlocker = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject { ["transparent"] = false }, new JsonObject { ["transparent"] = true }), ["纯色", "栏杆"], options);
        string conflict = conflictBlocker.Text;
        JsonObject conflictLocalized = conflictBlocker.ToNode();
        check(conflictLocalized["key"]?.GetValue<string>() == "blocker.fullframe_needs_opaque_group_options" &&
            conflict.Contains(options.En, StringComparison.Ordinal) && conflictLocalized["zh"]!.GetValue<string>().Contains(options.Zh, StringComparison.Ordinal) &&
            !Leaks(conflictLocalized["zh"]!.GetValue<string>()),
            "the full-frame blocker carries the options per language while the chinese channel carries no english fragment");

        // ---- ② 按实际状态分支：N≥2 夹着实时层 / N≥2 无实时层 / N=1 透明（点名前面的层） / N=1 无前置层 ----
        (string, string) plainOptions = ("用 --video-layout layered 显式选择分层视频", "explicitly choose layered video with --video-layout layered");
        var single = new JsonArray(new JsonObject { ["transparent"] = true, ["include_scene_clear"] = false });
        Blocker singleLegacyBlocker = PlanNarrative.FullFrameConflict(single, [], plainOptions, ["Picture", "Vapor (single)"]);
        JsonObject singleLocalized = singleLegacyBlocker.ToNode();
        string singleZh = singleLocalized["zh"]!.GetValue<string>(), singleEn = singleLocalized["en"]!.GetValue<string>();
        check(singleLocalized["key"]?.GetValue<string>() == "blocker.fullframe_single_transparent_group_options" &&
            singleZh.Contains("\"Picture\", \"Vapor (single)\"", StringComparison.Ordinal) &&
            singleEn.Contains("\"Picture\", \"Vapor (single)\"", StringComparison.Ordinal),
            "a single transparent group says the only block is transparent and names the realtime layers drawn before it");

        JsonObject bare = PlanNarrative.FullFrameConflict(single, [], plainOptions).ToNode();
        check(bare["key"]?.GetValue<string>() == "blocker.fullframe_single_transparent_group_options" &&
            !bare["zh"]!.GetValue<string>().Contains("Picture", StringComparison.Ordinal),
            "a single transparent group without leading realtime layers still states the transparent block without a dangling clause");

        JsonObject split = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject(), new JsonObject(), new JsonObject()), [], plainOptions).ToNode();
        check(split["key"]?.GetValue<string>() == "blocker.fullframe_split_groups_options",
            "several groups without interleaved realtime layers do not blame realtime layers or a transparent background");

        JsonObject interleaved = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject(), new JsonObject()), ["Clock"], plainOptions).ToNode();
        check(interleaved["key"]?.GetValue<string>() == "blocker.fullframe_needs_opaque_group_options" &&
            interleaved["zh"]!.GetValue<string>().Contains("\"Clock\"", StringComparison.Ordinal),
            "several groups with interleaved realtime layers name those layers and never offer a transparent background");

        // ---- ①b HDR 辐射闭合的未通过明细：中文按结构化判据另拼一份 ----
        var verdict = new JsonObject {
            ["group_id"] = "group-1",
            ["group_checks"] = new JsonArray(new JsonObject { ["rule"] = "R4", ["status"] = "fail",
                ["detail"] = "scene clear color is bound to a script; its value range cannot be decided" }),
            ["per_layer"] = new JsonArray(
                new JsonObject { ["layer_id"] = 26, ["layer_name"] = "纯色背景", ["status"] = "open", ["checks"] = new JsonArray(
                    new JsonObject { ["rule"] = "R1", ["status"] = "fail", ["detail"] = "material combo \"version\" is not a known range-preserving combo" }) },
                new JsonObject { ["layer_id"] = 7, ["layer_name"] = "Glow", ["status"] = "open", ["checks"] = new JsonArray(
                    new JsonObject { ["rule"] = "R1", ["status"] = "pass", ["detail"] = "built-in SDR shaders only" },
                    new JsonObject { ["rule"] = "R3", ["status"] = "fail",
                        ["detail"] = "texture \"color_map\" uses TEX format 9, which is not a known 8-bit unsigned container" }) },
                new JsonObject { ["layer_id"] = 8, ["status"] = "open", ["checks"] = new JsonArray(
                    new JsonObject { ["rule"] = "R2", ["status"] = "fail", ["detail"] = "a detail no version of the closure has produced" }) },
                new JsonObject { ["layer_id"] = 9, ["layer_name"] = "Hidden", ["status"] = "not_drawn", ["checks"] = new JsonArray(
                    new JsonObject { ["rule"] = "R0", ["status"] = "pass", ["detail"] = "Layer visibility resolves to false." }) }) };
        string[] reasonsZh = ((IEnumerable<string>)typeof(SdrRadianceClosure).GetMethod("ChineseReasons", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [verdict])!).ToArray();
        check(reasonsZh.Length == 4 &&
            reasonsZh[0].Contains("R4", StringComparison.Ordinal) &&
            reasonsZh[1].Contains("R1", StringComparison.Ordinal) && reasonsZh[1].Contains("\"纯色背景\"", StringComparison.Ordinal) && reasonsZh[1].Contains("\"version\"", StringComparison.Ordinal) &&
            reasonsZh[2].Contains("R3", StringComparison.Ordinal) && reasonsZh[2].Contains("\"Glow\"", StringComparison.Ordinal) && reasonsZh[2].Contains("\"color_map\"", StringComparison.Ordinal) &&
            reasonsZh[3].Contains("R2", StringComparison.Ordinal) && !reasonsZh[3].Contains("no version", StringComparison.Ordinal) &&
            reasonsZh.All(reason => !Leaks(reason)),
            "radiance-closure failures render in chinese in the same order, keep quoted resource names and fall back by rule code");

        string unprovenZh = (string)typeof(SdrRadianceClosure).GetMethod("ChineseUnproven", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [reasonsZh, 6])!;
        const string unprovenEn = "group-1 layer 26 \"纯色背景\": material combo \"version\" is not a known range-preserving combo (R1). (+2 more unproven layers.)";
        Blocker hdrBlocker = PlanNarrative.HdrRadianceOpen(new JsonObject { ["general"] = new JsonObject { ["hdr"] = true } }, null, unprovenEn, unprovenZh);
        string hdr = hdrBlocker.Text;
        JsonObject hdrLocalized = hdrBlocker.ToNode();
        check(hdr == SdrRadianceClosure.HdrBlocker + " Unproven: " + unprovenEn &&
            hdrLocalized["zh"]!.GetValue<string>().Contains(unprovenZh, StringComparison.Ordinal) &&
            !Leaks(hdrLocalized["zh"]!.GetValue<string>()) &&
            hdrLocalized["en"]!.GetValue<string>().Contains(unprovenEn, StringComparison.Ordinal),
            "the radiance blocker keeps english legacy and english detail while its chinese channel lists the failures in chinese");

        // ---- ③ 结论不明的细分：有未解析机制时说清几处、首条、出路；verdict 不变 ----
        JsonObject retain = UnresolvedPlan("candidate_found");
        JsonObject retainSummary = PlanNarrative.Summarize(retain);
        string retainZh = retainSummary["zh"]!.GetValue<string>();
        check(retainSummary["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            retainSummary["key"]!.GetValue<string>() == "summary.loop_unresolved_retain_live" &&
            retainZh.Contains("\"Light shafts\"", StringComparison.Ordinal) && retainZh.Contains("lightshafts", StringComparison.Ordinal) &&
            retainZh.Contains("--retain-live 20,28", StringComparison.Ordinal) &&
            retainZh.Contains("\"Light shafts\", \"Fog\"", StringComparison.Ordinal) &&
            !Leaks(retainZh) && !retainZh.Contains("Light-shaft noise", StringComparison.Ordinal) &&
            retainSummary["en"]!.GetValue<string>().Contains("Light-shaft noise UVs translate", StringComparison.Ordinal),
            "an unknown plan whose smaller allocation resolves points to --retain-live and keeps the english detail out of chinese");

        JsonObject still = PlanNarrative.Summarize(UnresolvedPlan("still_unavailable"));
        check(still["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            still["key"]!.GetValue<string>() == "summary.loop_unresolved" &&
            !still["zh"]!.GetValue<string>().Contains("--retain-live", StringComparison.Ordinal) && !Leaks(still["zh"]!.GetValue<string>()),
            "an unknown plan whose smaller allocation still has no loop says so without suggesting --retain-live");

        JsonObject staticPlan = UnresolvedPlan("not_applicable");
        // 点名图层不在条目里，经类型化记录（UnresolvedNotes）带到结论。
        var staticItem = new SourceStaticUnresolved("Baked layer 542 \"背景\": material texture \"背景\" is not a proven still image.", null,
            new StaticLayerNaming("背景", false));
        staticPlan["loop"]!["unresolved"] = new JsonArray(staticItem.ToJson());
        var (_, staticNotes) = UnresolvedNotes.Unpack(new JsonObject {
            ["loop"] = staticPlan["loop"]!.DeepClone(), ["notes"] = UnresolvedNotes.PackNotes([staticItem]) });
        Verdict.AddLoopUnresolved(staticPlan, "loop_allocation_fallback", "A smaller bake allocation was not attempted: no reason recorded.", staticNotes);
        JsonObject staticSummary = PlanNarrative.Summarize(staticPlan, staticNotes);
        check(!PlanNarrative.Summarize(staticPlan)["zh"]!.GetValue<string>().Contains("\"背景\"", StringComparison.Ordinal),
            "without the typed notes (a plan attached again) the still-image failure falls back to the generic wording");
        check(staticSummary["key"]!.GetValue<string>() == "summary.loop_unresolved" &&
            staticSummary["zh"]!.GetValue<string>().Contains("\"背景\"", StringComparison.Ordinal) &&
            !Leaks(staticSummary["zh"]!.GetValue<string>()),
            "a still-image proof failure is counted without the allocation record and described in chinese");

        JsonObject SwayPlan(int modeled)
        {
            JsonObject plan = UnresolvedPlan("still_unavailable");
            plan["loop"]!["unresolved"] = new JsonArray(
                new JsonObject { ["kind"] = "NonPeriodicOrDriftingMechanism", ["owner_layer_id"] = 17, ["mechanism"] = "uv_sway",
                    ["resource"] = "shaders/effects/foliagesway.frag + shaders/effects/foliagesway.vert", ["detail"] = "Foliage sway detail." },
                new JsonObject { ["kind"] = "NonPeriodicOrDriftingMechanism", ["owner_layer_id"] = 17, ["mechanism"] = "irrational_sine_hash",
                    ["resource"] = "shaders/effects/iris.frag + shaders/effects/iris.vert", ["detail"] = "Iris detail." });
            plan["loop"]!["sway_retime"] = new JsonObject { ["enabled"] = true, ["sway_items"] = 1, ["modeled_items"] = modeled,
                ["status"] = "no_base_candidate" };
            return plan;
        }
        string swayModeled = PlanNarrative.Summarize(SwayPlan(1))["zh"]!.GetValue<string>();
        string swayUnmodeled = PlanNarrative.Summarize(SwayPlan(0))["zh"]!.GetValue<string>();
        check(swayModeled.Contains("iris", StringComparison.Ordinal) && !swayModeled.Contains("foliagesway", StringComparison.Ordinal) &&
            swayUnmodeled.Contains("foliagesway", StringComparison.Ordinal),
            "with sway retime on and every sway item modeled, the summary names the mechanism retiming cannot absorb first");

        JsonObject particlePlan = UnresolvedPlan(null);
        particlePlan["loop"]!["unresolved"] = new JsonArray(new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = 28,
            ["particle_nonperiodic_reason"] = "particle_nonperiodic_turbulent_velocity",
            ["detail"] = "An english particle sentence that no process registered in this run." });
        JsonObject particleSummary = PlanNarrative.Summarize(particlePlan);
        check(particleSummary["zh"]!.GetValue<string>().Contains("\"Fog\"", StringComparison.Ordinal) &&
            !particleSummary["zh"]!.GetValue<string>().Contains("english particle sentence", StringComparison.Ordinal) &&
            !Leaks(particleSummary["zh"]!.GetValue<string>()),
            "an unregistered particle reason read from an older plan is described from its structured reason code");

        // ---- ③b 平稳随机粒子不算"证明不了周期"；重查只被全幅布局挡住时给出完整 --retain-live ----
        StationaryParticleNarrative(check);

        // ---- ④ bake --help 的 --encoder 说明按 --lang 出 ----
        string bakeEn = Baker.Cli.OptionTable.Usage("bake", "en"), bakeZh = Baker.Cli.OptionTable.Usage("bake", "zh");
        static bool Indented(string section, string language) => MessageCatalog.Get("cli.bake_encoder_help", language).Split('\n')
            .All(line => section.Contains("  " + line.TrimEnd('\r'), StringComparison.Ordinal));
        check(Indented(bakeEn, "en") && !Regex.IsMatch(bakeEn, @"\p{IsCJKUnifiedIdeographs}"),
            "bake --help in english has an english encoder note and no chinese");
        check(Indented(bakeZh, "zh") &&
            bakeZh.Split(Environment.NewLine).Where(line => line.StartsWith("wpe-baker", StringComparison.Ordinal))
                .SequenceEqual(bakeEn.Split(Environment.NewLine).Where(line => line.StartsWith("wpe-baker", StringComparison.Ordinal))) &&
            bakeZh.Split(Environment.NewLine).All(line => line.StartsWith("wpe-baker", StringComparison.Ordinal) || line.StartsWith("  ", StringComparison.Ordinal)),
            "bake --help in chinese keeps the english usage skeleton and indents the chinese encoder note");
        check(Baker.Cli.OptionTable.HelpLanguage(["bake", "--help", "--lang", "en"], "zh") == "en" &&
            Baker.Cli.OptionTable.HelpLanguage(["bake", "--lang", "zh", "--help"], "en") == "zh" &&
            Baker.Cli.OptionTable.HelpLanguage(["bake", "--help"], "zh") == "zh" &&
            Baker.Cli.OptionTable.HelpLanguage(["bake", "--help", "--lang", "fr"], "en") == "en",
            "help output language follows --lang wherever it appears and otherwise the interface language");
        check(Baker.Cli.OptionTable.Usage("analyze", "zh") == Baker.Cli.OptionTable.Usage("analyze", "en"),
            "analyze --help stays the same english skeleton in both languages");
    }

    /// <summary>
    /// rc10 回归里的三种文案缺陷：通过平稳随机判据的粒子被当成"证明不了周期"的首条（3594269099、3565190341 形态）、
    /// 被计进机制条数（3639101641 形态）、重查只被全幅布局挡住时丢了 --retain-live 建议（3639101641、3647396093 形态）。
    /// </summary>
    private static void StationaryParticleNarrative(Action<bool, string> check)
    {
        static JsonObject Stationary(int owner, string detail, string? nonperiodicReason = null)
        {
            var item = new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = owner, ["detail"] = detail,
                ["particle_stationarity"] = new JsonObject { ["stationary"] = true, ["failed_conditions"] = new JsonArray(), ["warmup_seconds"] = 7 } };
            if (nonperiodicReason is not null) item["particle_nonperiodic_reason"] = nonperiodicReason;
            return item;
        }
        string stationaryDetail = MessageCatalog.RenderLegacy("unresolved.particle_stationary_random");

        // 只剩平稳粒子（3594269099）：不说"1 处证明不了周期"，也不拿它当首条。
        JsonObject onlyPlan = UnresolvedPlan("not_applicable");
        onlyPlan["loop"]!["no_candidate_reason"] = new JsonObject { ["kind"] = "NoTemporalMechanism" };
        onlyPlan["loop"]!["unresolved"] = new JsonArray(Stationary(28, stationaryDetail),
            new JsonObject { ["kind"] = "loop_allocation_fallback", ["detail"] = "A smaller bake allocation was not attempted: no reason recorded." });
        JsonObject only = PlanNarrative.Summarize(onlyPlan);
        string onlyZh = only["zh"]!.GetValue<string>(), onlyEn = only["en"]!.GetValue<string>();
        check(only["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            only["key"]!.GetValue<string>() == "summary.loop_unresolved_stationary_only" &&
            onlyZh.StartsWith(MessageCatalog.Get("summary.loop_unresolved_stationary_only", "zh", 1, "\"Fog\"",
                MessageCatalog.Get("summary.stationary_only_no_period_source", "zh"), ""), StringComparison.Ordinal) &&
            onlyEn.StartsWith(MessageCatalog.Get("summary.loop_unresolved_stationary_only", "en", 1, "\"Fog\"",
                MessageCatalog.Get("summary.stationary_only_no_period_source", "en"), ""), StringComparison.Ordinal) &&
            !Leaks(onlyZh) && !Regex.IsMatch(onlyEn, @"\p{IsCJKUnifiedIdeographs}"),
            "a plan whose only unresolved items are stationary particles does not cite them as mechanisms without a provable period");

        // 精灵轨道粒子项带着非周期原因、同时通过判据（3565190341）；同一层两条项按一层计；求解器另有原因时换一种说法。
        JsonObject spritePlan = UnresolvedPlan("not_applicable");
        spritePlan["loop"]!["no_candidate_reason"] = new JsonObject { ["kind"] = "FixedPeriodExceedsCeiling" };
        spritePlan["loop"]!["unresolved"] = new JsonArray(
            Stationary(28, "An english particle sentence that no process registered in this run.", "particle_nonperiodic_turbulent_velocity"),
            Stationary(28, "Another english particle sentence.", "particle_nonperiodic_turbulent_velocity"));
        JsonObject sprite = PlanNarrative.Summarize(spritePlan);
        string spriteZh = sprite["zh"]!.GetValue<string>();
        check(sprite["key"]!.GetValue<string>() == "summary.loop_unresolved_stationary_only" &&
            spriteZh.StartsWith(MessageCatalog.Get("summary.loop_unresolved_stationary_only", "zh", 1, "\"Fog\"",
                MessageCatalog.Get("summary.stationary_only_no_candidate", "zh"), ""), StringComparison.Ordinal) && !Leaks(spriteZh),
            "a stationary sprite-track particle item with a nonperiodic reason is still not cited, and one layer counts once");

        // 平稳粒子与真正证明不了周期的机制并存（3639101641，回退找到候选）：只数后者、首条是后者，另起半句说明粒子。
        JsonObject mixedPlan = UnresolvedPlan("candidate_found");
        mixedPlan["loop"]!["unresolved"]![1] = Stationary(28, stationaryDetail, "particle_nonperiodic_random_initializer");
        JsonObject mixed = PlanNarrative.Summarize(mixedPlan);
        string mixedZh = mixed["zh"]!.GetValue<string>(), mixedEn = mixed["en"]!.GetValue<string>();
        check(mixed["key"]!.GetValue<string>() == "summary.loop_unresolved_retain_live" &&
            mixedZh.Contains("\"Light shafts\"", StringComparison.Ordinal) && mixedZh.Contains("lightshafts", StringComparison.Ordinal) &&
            mixedZh.Contains(MessageCatalog.Get("summary.stationary_particles_note", "zh", 1, "\"Fog\""), StringComparison.Ordinal) &&
            mixedEn.Contains(MessageCatalog.Get("summary.stationary_particles_note", "en", 1, "\"Fog\""), StringComparison.Ordinal) &&
            mixedZh.Contains("--retain-live 20,28", StringComparison.Ordinal) && !Leaks(mixedZh) &&
            mixedEn.Contains("Light-shaft noise UVs translate", StringComparison.Ordinal),
            "stationary particles next to a real mechanism are neither counted nor cited first, and are explained in one extra clause");

        // 重查只被全幅布局挡住（3639101641、3647396093）：给出已保留根在前的完整 --retain-live，并说明这个分配还没重查。
        JsonObject blockedPlan = UnresolvedPlan("still_unavailable");
        blockedPlan["loop"]!["unresolved"]![1] = Stationary(28, stationaryDetail, "particle_nonperiodic_random_initializer");
        var blockedFallback = blockedPlan["loop_allocation_fallback"]!.AsObject();
        blockedFallback["retain_live_root_ids"] = new JsonArray(20);
        blockedFallback["replanned_blockers"] = new JsonArray("Full-frame mode requires one opaque video group; options follow.");
        blockedFallback["replanned_retain_live_suggestion"] = new JsonObject { ["basis"] = "full_frame_retention",
            ["retain_live_root_ids"] = new JsonArray(20, 28), ["added_live_root_ids"] = new JsonArray(28) };
        JsonObject blocked = PlanNarrative.Summarize(blockedPlan);
        string blockedZh = blocked["zh"]!.GetValue<string>(), blockedEn = blocked["en"]!.GetValue<string>();
        check(blocked["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            blocked["key"]!.GetValue<string>() == "summary.loop_unresolved_retain_live_full_frame" &&
            blockedZh.Contains("\"Light shafts\"", StringComparison.Ordinal) && blockedZh.Contains("\"Fog\"", StringComparison.Ordinal) &&
            blockedZh.Contains("--retain-live 20,28", StringComparison.Ordinal) && !Leaks(blockedZh) &&
            blockedEn.Contains("--retain-live 20,28", StringComparison.Ordinal) && !Regex.IsMatch(blockedEn, @"\p{IsCJKUnifiedIdeographs}"),
            "a smaller allocation stopped only by a full-frame conflict restores a runnable cumulative --retain-live suggestion");

        // 重查被别的阻断挡住、没有可执行的保留做法：不说"找不到循环"，指向阻断原文，也不编造 --retain-live。
        blockedFallback.Remove("replanned_retain_live_suggestion");
        JsonObject other = PlanNarrative.Summarize(blockedPlan);
        string otherZh = other["zh"]!.GetValue<string>();
        check(other["key"]!.GetValue<string>() == "summary.loop_unresolved" &&
            otherZh.Contains("loop_allocation_fallback.replanned_blockers", StringComparison.Ordinal) &&
            !otherZh.Contains("--retain-live", StringComparison.Ordinal) && !Leaks(otherZh) &&
            other["en"]!.GetValue<string>().Contains("loop_allocation_fallback.replanned_blockers", StringComparison.Ordinal),
            "a smaller allocation stopped by another blocker points at the replanned blockers instead of claiming no loop exists");

        // 全幅冲突自己的选项：plan 已经带 --retain-live 时，建议的 id 要把已保留的根一并写上（--retain-live 整体替换）。
        var cumulativePlan = new JsonObject {
            ["settings"] = new JsonObject { ["video_layout"] = "full_frame", ["retain_live_root_ids"] = new JsonArray(20) },
            ["full_frame_retention"] = new JsonObject { ["status"] = "available", ["root_ids"] = new JsonArray(28) },
            ["layers"] = new JsonArray(
                new JsonObject { ["id"] = 20, ["root"] = 20, ["name"] = "光束", ["visible"] = true, ["drawable"] = true },
                new JsonObject { ["id"] = 28, ["root"] = 28, ["name"] = "雾 2", ["visible"] = true, ["drawable"] = true }) };
        var cumulative = ((string Zh, string En))DemotionType.GetMethod("ConflictOptions", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [cumulativePlan])!;
        check(cumulative.Zh.Contains("--retain-live 20,28", StringComparison.Ordinal) && cumulative.Zh.Contains("光束", StringComparison.Ordinal) &&
            cumulative.Zh.Contains("雾 2", StringComparison.Ordinal) && cumulative.En.Contains("--retain-live 20,28", StringComparison.Ordinal),
            "the full-frame retain-live option keeps roots the plan already retains, so following it does not bake them again");

        // 保留做法里的分配根是作者根拆开后的子单元（全量批处理 8 案 exit=1 的形态）：--retain-live 只收源作者根。
        // 作者根 382 下的 155/379 在尾组视频里、382 本身是不画的容器：换成 382 不会多退回任何视频单元，照换。
        // 作者根 129 下的 53 在尾组、16 却在不透明底组：整棵保留会连底组一起退回（实测重跑后一组视频都不剩），不给这条建议。
        static JsonObject SplitLayer(int id, int root, string name, bool drawable = true) => new() {
            ["id"] = id, ["root"] = root, ["allocation_root"] = id, ["name"] = name, ["visible"] = true, ["drawable"] = drawable };
        JsonObject SplitPlan(int[] retention, params JsonObject[] layers) => new() {
            ["settings"] = new JsonObject { ["video_layout"] = "full_frame", ["retain_live_root_ids"] = new JsonArray(20) },
            ["full_frame_retention"] = new JsonObject { ["status"] = "available",
                ["root_ids"] = new JsonArray([.. retention.Select(id => (JsonNode)JsonValue.Create(id))]) },
            ["video_groups"] = new JsonArray(
                new JsonObject { ["id"] = "group-1", ["root_ids"] = new JsonArray(17, 16), ["include_scene_clear"] = true, ["transparent"] = false },
                new JsonObject { ["id"] = "group-2", ["root_ids"] = new JsonArray([.. retention.Select(id => (JsonNode)JsonValue.Create(id))]), ["transparent"] = true }),
            ["layers"] = new JsonArray([.. layers]) };
        var commandMethod = DemotionType.GetMethod("RetainLiveCommandRoots", BindingFlags.Static | BindingFlags.NonPublic)!;
        var conflictOptions = DemotionType.GetMethod("ConflictOptions", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonObject exactPlan = SplitPlan([155, 379], SplitLayer(17, 17, "Base"), SplitLayer(16, 16, "Sky"), SplitLayer(20, 20, "Shafts"),
            SplitLayer(382, 382, "Rain group", drawable: false), SplitLayer(155, 382, "Rain A"), SplitLayer(379, 382, "Rain B"));
        var exact = ((string Zh, string En))conflictOptions.Invoke(null, [exactPlan])!;
        check(commandMethod.Invoke(null, [exactPlan, new[] { 155, 379 }]) is int[] exactIds && exactIds.SequenceEqual([20, 382]) &&
            exact.Zh.Contains("--retain-live 20,382", StringComparison.Ordinal) && exact.En.Contains("--retain-live 20,382", StringComparison.Ordinal) &&
            !exact.En.Contains("155", StringComparison.Ordinal),
            "split allocation units in a retention become their source author root, together with roots the plan already retains");
        JsonObject inexactPlan = SplitPlan([53], SplitLayer(17, 17, "Base"), SplitLayer(20, 20, "Shafts"),
            SplitLayer(129, 129, "Scene group", drawable: false), SplitLayer(16, 129, "Sky"), SplitLayer(53, 129, "Birds"));
        var inexact = ((string Zh, string En))conflictOptions.Invoke(null, [inexactPlan])!;
        check(commandMethod.Invoke(null, [inexactPlan, new[] { 53 }]) is null &&
            !inexact.Zh.Contains("--retain-live", StringComparison.Ordinal) && !inexact.En.Contains("--retain-live", StringComparison.Ordinal) &&
            inexact.Zh.Contains("--video-layout layered", StringComparison.Ordinal),
            "no retain-live option when retaining the author root would also send another video unit such as the opaque base group live");
    }

    /// <summary>无阻断、无候选的整层 plan：一个漂移着色器 + 一个粒子 + 补充分析记录。</summary>
    private static JsonObject UnresolvedPlan(string? fallbackStatus)
    {
        var plan = new JsonObject {
            ["settings"] = new JsonObject { ["fps_numerator"] = 60, ["fps_denominator"] = 1 },
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 17, ["name"] = "Base" },
                new JsonObject { ["id"] = 20, ["name"] = "Light shafts" }, new JsonObject { ["id"] = 28, ["name"] = "Fog" }),
            ["live_layer_ids"] = new JsonArray(),
            ["blockers"] = new JsonArray(),
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(), ["unresolved"] = new JsonArray(
                new JsonObject { ["kind"] = "NonPeriodicOrDriftingMechanism", ["owner_layer_id"] = 20,
                    ["resource"] = "shaders/effects/lightshafts.frag + shaders/effects/lightshafts.vert",
                    ["detail"] = "Light-shaft noise UVs translate at rayspeed 0.39 * (0.003, 0.000375111) per second. The fastest axis alone repeats only every 544.3 s." },
                new JsonObject { ["kind"] = "runtime_animation", ["owner_layer_id"] = 28,
                    ["particle_nonperiodic_reason"] = "particle_nonperiodic_random_initializer",
                    ["detail"] = "A particle sentence that is not registered in this process." },
                new JsonObject { ["kind"] = "loop_allocation_fallback", ["detail"] = "No loop covers every baked layer." }) } };
        if (fallbackStatus is not null)
            plan["loop_allocation_fallback"] = new JsonObject { ["status"] = fallbackStatus, ["retain_live_root_ids"] = new JsonArray(20, 28) };
        return plan;
    }
}
