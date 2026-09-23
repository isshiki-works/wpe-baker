using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 循环时长上限跟随 --loop-max-seconds：上限从请求一路传到求解器与 plan，拒绝理由复述实际上限；
/// 原本 180 秒内就有候选的，效率档与未回落的平衡档选中项不随上限变大而改变。
/// </summary>
internal static class LoopCeilingChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        // ---- 求解器上限 ----
        bool Throws(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
        check(CommonLoopSolver.DefaultMaximumSeconds == CommonLoopSolver.DefaultLoopLengthMaximumSeconds &&
            CommonLoopSolver.Ceiling(3600) == new CommonLoopRational(3600) && CommonLoopSolver.Ceiling(45.5) == new CommonLoopRational(91, 2) &&
            Throws(() => CommonLoopSolver.Ceiling(0)) && Throws(() => CommonLoopSolver.Ceiling(3600.5)) && Throws(() => CommonLoopSolver.Ceiling(double.NaN)),
            "the solver ceiling defaults to the --loop-max-seconds default and accepts exactly the (0, 3600] range");

        CommonLoopComponent[] fixed300 = [new("track", new(300, CommonLoopPeriodEvidence.Analytic, new(300)))];
        CommonLoopSearchResult defaultCeiling = CommonLoopSolver.Suggest(new(60, 1, fixed300, new(1, 60)));
        CommonLoopSearchResult legacyCeiling = CommonLoopSolver.Suggest(new(60, 1, fixed300, new(1, 60), new(180)));
        CommonLoopSearchResult longestCeiling = CommonLoopSolver.Suggest(new(60, 1, fixed300, new(1, 60), CommonLoopSolver.Ceiling(3600)));
        check(defaultCeiling.Candidates.Select(candidate => candidate.Frames).SequenceEqual(new ulong[] { 18000, 36000 }) &&
            legacyCeiling.Candidates.Count == 0 && legacyCeiling.NoCandidate is { Kind: CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling } legacy &&
            legacy.CeilingSeconds == 180 && legacy.FixedPeriodSeconds == 300 &&
            longestCeiling.Candidates.Count == 12 && longestCeiling.Candidates[^1].Frames == 216000 &&
            Throws(() => CommonLoopSolver.Suggest(new(60, 1, fixed300, new(1, 60), new(3601)))),
            "a 300-second fixed track closes under the default 600-second ceiling, is refused with the passed 180-second ceiling, and a 3600-second ceiling is searched in full");

        // ---- 原本 180 秒内有候选：效率档与未回落的平衡档选中项不变 ----
        CommonLoopSearchResult Solve(CommonLoopComponent[] components, CommonLoopPreference preference, long minimumSeconds, long maximumSeconds) =>
            CommonLoopSolver.Suggest(new(60, 1, components, new(minimumSeconds), new(maximumSeconds), MaximumRetimePercent: 2, Preference: preference));
        CommonLoopComponent[] shimmer =
        [
            new("shimmer", new(6.242857089, CommonLoopPeriodEvidence.Analytic), true),
            new("noise", new(.8514261213, CommonLoopPeriodEvidence.Analytic), true)
        ];
        check(new long[] { 180, 600, 3600 }.All(maximum =>
                Solve(shimmer, CommonLoopPreference.Performance, 1, maximum).Candidates[0].Frames == 758 &&
                Solve(shimmer, CommonLoopPreference.Balanced, 1, maximum) is { BudgetRelaxed: false } balanced && balanced.Candidates[0].Frames == 1124),
            "a loop that closed within 180 seconds keeps the same performance (758) and balanced (1124) selection under 600 and 3600-second ceilings");
        CommonLoopComponent[] coarse =
        [
            new("slow", new(110.0, CommonLoopPeriodEvidence.Analytic), true),
            new("swirl", new(35.33, CommonLoopPeriodEvidence.Analytic), true)
        ];
        CommonLoopSearchResult coarse600 = Solve(coarse, CommonLoopPreference.Balanced, 10, 600);
        CommonLoopSearchResult coarse3600 = Solve(coarse, CommonLoopPreference.Balanced, 10, 3600);
        check(coarse600.BudgetRelaxed && coarse600.Candidates[0].Frames == 6480 &&
            !coarse3600.BudgetRelaxed && coarse3600.RetimeBudgetPercent == 1 && coarse3600.Candidates[0].Frames == 39938 &&
            coarse3600.Candidates[0].Components.All(component => Math.Abs(component.DeltaPercent) <= 1 + 1e-9),
            "balanced keeps its rule (one percent anywhere under the ceiling before relaxing): the relaxed 108-second pick stays under 600 seconds and becomes a one-percent 665.6-second pick only under 3600");
        CommonLoopSearchResult quality180 = Solve(shimmer, CommonLoopPreference.Quality, 1, 180);
        CommonLoopSearchResult quality600 = Solve(shimmer, CommonLoopPreference.Quality, 1, 600);
        check(quality180.Candidates[0].Frames == 8991 && quality600.Candidates[0].Frames == 19106 &&
            quality600.Candidates[0].TotalRetimeCostPercent <= quality180.Candidates[0].TotalRetimeCostPercent,
            "quality still orders by total retime cost, so a longer ceiling can pick a longer and cheaper loop");

        // ---- 上限从请求传到循环分析与 plan ----
        string sourceDirectory = Path.Combine(root, "loop-ceiling-source");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(sourceDirectory, "scene.json"), "{\"objects\":[]}");
        using var source = new ProjectSource(sourceDirectory);
        JsonObject Analyze(double seconds, double? ceiling, SwayRetimeOptions? sway = null, EmbeddedVideoLoopLimit? limit = null) => LoopAnalysis.Analyze(
            new JsonObject { ["objects"] = new JsonArray { new JsonObject { ["id"] = 1 } } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray { new JsonObject {
                ["source_owner_layer_id"] = 1, ["mechanism"] = "authored_track", ["track_name"] = null, ["duration_seconds"] = seconds,
                ["playback_rate"] = 1, ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" } } },
            [1], 60, 1, 2, CommonLoopPreference.Balanced, sway, ceiling, limit).ToJson();
        static ulong[] Frames(JsonObject loop) => loop["candidates"]!.AsArray().Select(candidate => candidate!["frames"]!.GetValue<ulong>()).ToArray();
        JsonObject byDefault = Analyze(300, null), legacy180 = Analyze(300, 180), wide = Analyze(300, 1200);
        check(Frames(byDefault).SequenceEqual(new ulong[] { 18000, 36000 }) && byDefault["maximum_seconds"]!.GetValue<double>() == 600 &&
            Frames(legacy180).Length == 0 && legacy180["maximum_seconds"]!.GetValue<double>() == 180 &&
            legacy180["no_candidate_reason"]!["kind"]!.GetValue<string>() == "FixedPeriodExceedsCeiling" &&
            legacy180["no_candidate_reason"]!["ceiling_seconds"]!.GetValue<double>() == 180 &&
            Frames(wide).SequenceEqual(new ulong[] { 18000, 36000, 54000, 72000 }) && wide["maximum_seconds"]!.GetValue<double>() == 1200,
            "the loop analysis searches and records the ceiling it was given, and the no-candidate reason quotes that ceiling");
        JsonObject withSway = Analyze(300, null, new SwayRetimeOptions(1200));
        check(Frames(withSway).SequenceEqual(Frames(wide)) && withSway["maximum_seconds"]!.GetValue<double>() == 1200 &&
            Throws(() => Analyze(300, 600, new SwayRetimeOptions(1200))),
            "with sway retime on the solver takes the same loop length maximum, and a mismatched pair is rejected");

        var loopLengthMaximumOf = typeof(HybridScenePlanner).GetMethod("LoopLengthMaximumOf", BindingFlags.Static | BindingFlags.NonPublic)!;
        var request = new HybridAnalyzeRequest(2, "source", "assets", "out");
        check((double)loopLengthMaximumOf.Invoke(null, [request, null, null])! == 600 &&
            (double)loopLengthMaximumOf.Invoke(null, [request with { LoopLengthMaximumSeconds = 1800 }, null, null])! == 1800 &&
            (double)loopLengthMaximumOf.Invoke(null, [request with { LoopLengthMaximumSeconds = 1800, SwayRetime = false }, null, null])! == 1800,
            "the planner takes the loop ceiling from --loop-max-seconds whether or not sway retime is on, defaulting to 600 seconds");

        // ---- 合并 fix/embedded-video-size 后：内嵌视频 2 GiB 收紧与 --loop-max-seconds 是同一个实际上限 ----
        var swayOptionsOf = typeof(HybridScenePlanner).GetMethod("SwayRetimeOptionsOf", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fourKRequest = new HybridAnalyzeRequest(2, "source", "assets", "out", Width: 3840, Height: 2160, FpsNumerator: 60,
            LoopLengthMaximumSeconds: 3600);
        var opaqueGroups = new JsonArray(new JsonObject { ["transparent"] = false });
        var packedGroups = new JsonArray(new JsonObject { ["transparent"] = false }, new JsonObject { ["transparent"] = true });
        double opaqueCeiling = (double)loopLengthMaximumOf.Invoke(null, [fourKRequest, opaqueGroups, null])!;
        double packedCeiling = (double)loopLengthMaximumOf.Invoke(null, [fourKRequest, packedGroups, null])!;
        var swayOn = (SwayRetimeOptions)swayOptionsOf.Invoke(null,
            [fourKRequest with { SwayRetime = true }, new JsonObject { ["visible_width"] = 3840, ["visible_height"] = 2160 }, packedGroups, null])!;
        check(opaqueCeiling == 558 && packedCeiling < 558 &&
            (double)loopLengthMaximumOf.Invoke(null, [fourKRequest, null, null])! == 558 &&
            (double)loopLengthMaximumOf.Invoke(null, [fourKRequest with { LoopLengthMaximumSeconds = 300 }, opaqueGroups, null])! == 300 &&
            swayOn.LoopLengthMaximumSeconds == packedCeiling && swayOn.VideoLimit is { Applied: true, PackedAlpha: true },
            "the solver ceiling and the sway Lmax are one value: --loop-max-seconds lowered by the embedded video limit, with or without sway retime");
        EmbeddedVideoLoopLimit fourK = EmbeddedVideoBudget.LoopLengthLimit(3600, 3840, 2160, false, 60, 1)!;
        JsonObject limitedOff = Analyze(300, fourK.EffectiveSeconds, limit: fourK);
        JsonObject limitedSummary = PlanNarrative.Summarize(new JsonObject {
            ["settings"] = new JsonObject { ["fps_numerator"] = 60, ["fps_denominator"] = 1 },
            ["blockers"] = new JsonArray(), ["layers"] = new JsonArray(), ["live_layer_ids"] = new JsonArray(), ["loop"] = limitedOff.DeepClone() });
        JsonObject roomy = Analyze(300, 600, limit: EmbeddedVideoBudget.LoopLengthLimit(600, 1920, 1080, false, 60, 1));
        check(Frames(limitedOff).SequenceEqual(new ulong[] { 18000 }) && limitedOff["maximum_seconds"]!.GetValue<double>() == 558 &&
            limitedOff["embedded_video_limit"]!["applied"]!.GetValue<bool>() && limitedOff["sway_retime"] is null &&
            limitedSummary["zh"]!.GetValue<string>().Contains("循环长度上限由 3600 s 降至 558 s", StringComparison.Ordinal) &&
            roomy["embedded_video_limit"] is null && Frames(roomy).SequenceEqual(Frames(byDefault)) &&
            Throws(() => Analyze(300, 600, limit: fourK)),
            "with sway retime off a lowered ceiling is still recorded on the loop and stated in the conclusion; a mismatched limit is rejected");
    }
}
