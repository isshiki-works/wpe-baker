using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 用户可见文案的几处确定缺陷：中文通道混进英文拼接、全幅冲突"切成 N 块或者只留下透明背景"的并列措辞、
/// 结论不明的细分、bake --help 的 --encoder 说明。只查文案，不碰任何判定。
/// </summary>
internal static class NarrativePolishChecks
{
    private static readonly Type DemotionType = typeof(HybridPlanFormat).Assembly.GetType("Baker.Core.FullFrameDemotion")!;

    public static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        // ---- ① 全幅冲突选项：中文通道用中文拼接层名 ----
        var retentionPlan = new JsonObject {
            ["settings"] = new JsonObject { ["video_layout"] = "full_frame" },
            ["full_frame_retention"] = new JsonObject { ["status"] = "available", ["root_ids"] = new JsonArray(1, 2, 3, 4, 5, 6, 7) },
            ["layers"] = new JsonArray(Enumerable.Range(1, 7).Select(id => (JsonNode)new JsonObject {
                ["id"] = id, ["root"] = id, ["name"] = "雨景 " + id, ["visible"] = true, ["drawable"] = true }).ToArray()) };
        var options = ((string Zh, string En))DemotionType.GetMethod("ConflictOptions", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [retentionPlan])!;

        Blocker conflictBlocker = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject { ["transparent"] = false }, new JsonObject { ["transparent"] = true }), ["纯色", "栏杆"], options);
        JsonObject conflictLocalized = conflictBlocker.ToNode();
        check(conflictLocalized["key"]?.GetValue<string>() == "blocker.fullframe_needs_opaque_group_options",
            "the full-frame blocker carries the options per language while the chinese channel carries no english fragment");

        // ---- ② 按实际状态分支：N≥2 夹着实时层 / N≥2 无实时层 / N=1 透明（点名前面的层） / N=1 无前置层 ----
        (string, string) plainOptions = ("用 --video-layout layered 显式选择分层视频", "explicitly choose layered video with --video-layout layered");
        var single = new JsonArray(new JsonObject { ["transparent"] = true, ["include_scene_clear"] = false });
        Blocker singleLegacyBlocker = PlanNarrative.FullFrameConflict(single, [], plainOptions, ["Picture", "Vapor (single)"]);
        JsonObject singleLocalized = singleLegacyBlocker.ToNode();
        check(singleLocalized["key"]?.GetValue<string>() == "blocker.fullframe_single_transparent_group_options",
            "a single transparent group says the only block is transparent and names the realtime layers drawn before it");

        JsonObject bare = PlanNarrative.FullFrameConflict(single, [], plainOptions).ToNode();
        check(bare["key"]?.GetValue<string>() == "blocker.fullframe_single_transparent_group_options",
            "a single transparent group without leading realtime layers still states the transparent block without a dangling clause");

        JsonObject split = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject(), new JsonObject(), new JsonObject()), [], plainOptions).ToNode();
        check(split["key"]?.GetValue<string>() == "blocker.fullframe_split_groups_options",
            "several groups without interleaved realtime layers do not blame realtime layers or a transparent background");

        JsonObject interleaved = PlanNarrative.FullFrameConflict(
            new JsonArray(new JsonObject(), new JsonObject()), ["Clock"], plainOptions).ToNode();
        check(interleaved["key"]?.GetValue<string>() == "blocker.fullframe_needs_opaque_group_options",
            "several groups with interleaved realtime layers name those layers and never offer a transparent background");

        // ---- ③ 结论不明的细分：有未解析机制时说清几处、首条、出路；verdict 不变 ----
        JsonObject retain = UnresolvedPlan("candidate_found");
        JsonObject retainSummary = PlanNarrative.Summarize(retain);
        check(retainSummary["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            retainSummary["key"]!.GetValue<string>() == "summary.loop_unresolved_retain_live",
            "an unknown plan whose smaller allocation resolves points to --retain-live and keeps the english detail out of chinese");

        JsonObject still = PlanNarrative.Summarize(UnresolvedPlan("still_unavailable"));
        check(still["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            still["key"]!.GetValue<string>() == "summary.loop_unresolved",
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
        check(staticSummary["key"]!.GetValue<string>() == "summary.loop_unresolved",
            "a still-image proof failure is counted without the allocation record and described in chinese");

        // ---- ③b 平稳随机粒子不算"证明不了周期"；重查只被全幅布局挡住时给出完整 --retain-live ----
        StationaryParticleNarrative(check);

        check(Baker.Cli.OptionTable.HelpLanguage(["bake", "--help", "--lang", "en"], "zh") == "en" &&
            Baker.Cli.OptionTable.HelpLanguage(["bake", "--lang", "zh", "--help"], "en") == "zh" &&
            Baker.Cli.OptionTable.HelpLanguage(["bake", "--help"], "zh") == "zh" &&
            Baker.Cli.OptionTable.HelpLanguage(["bake", "--help", "--lang", "fr"], "en") == "en",
            "help output language follows --lang wherever it appears and otherwise the interface language");
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
        check(only["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            only["key"]!.GetValue<string>() == "summary.loop_unresolved_stationary_only",
            "a plan whose only unresolved items are stationary particles does not cite them as mechanisms without a provable period");

        // 精灵轨道粒子项带着非周期原因、同时通过判据（3565190341）；同一层两条项按一层计；求解器另有原因时换一种说法。
        JsonObject spritePlan = UnresolvedPlan("not_applicable");
        spritePlan["loop"]!["no_candidate_reason"] = new JsonObject { ["kind"] = "FixedPeriodExceedsCeiling" };
        spritePlan["loop"]!["unresolved"] = new JsonArray(
            Stationary(28, "An english particle sentence that no process registered in this run.", "particle_nonperiodic_turbulent_velocity"),
            Stationary(28, "Another english particle sentence.", "particle_nonperiodic_turbulent_velocity"));
        JsonObject sprite = PlanNarrative.Summarize(spritePlan);
        check(sprite["key"]!.GetValue<string>() == "summary.loop_unresolved_stationary_only",
            "a stationary sprite-track particle item with a nonperiodic reason is still not cited, and one layer counts once");

        // 平稳粒子与真正证明不了周期的机制并存（3639101641，回退找到候选）：只数后者、首条是后者，另起半句说明粒子。
        JsonObject mixedPlan = UnresolvedPlan("candidate_found");
        mixedPlan["loop"]!["unresolved"]![1] = Stationary(28, stationaryDetail, "particle_nonperiodic_random_initializer");
        JsonObject mixed = PlanNarrative.Summarize(mixedPlan);
        check(mixed["key"]!.GetValue<string>() == "summary.loop_unresolved_retain_live",
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
        check(blocked["verdict"]!.GetValue<string>() == PlanNarrative.Unknown &&
            blocked["key"]!.GetValue<string>() == "summary.loop_unresolved_retain_live_full_frame",
            "a smaller allocation stopped only by a full-frame conflict restores a runnable cumulative --retain-live suggestion");

        // 重查被别的阻断挡住、没有可执行的保留做法：不说"找不到循环"，指向阻断原文，也不编造 --retain-live。
        blockedFallback.Remove("replanned_retain_live_suggestion");
        JsonObject other = PlanNarrative.Summarize(blockedPlan);
        check(other["key"]!.GetValue<string>() == "summary.loop_unresolved",
            "a smaller allocation stopped by another blocker points at the replanned blockers instead of claiming no loop exists");
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
