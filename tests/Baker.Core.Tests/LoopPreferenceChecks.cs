using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

// 循环取向三档（效率 / 平衡 / 质量）的排序、预算回落与 plan 字段。
internal static class LoopPreferenceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static CommonLoopSearchResult Solve(CommonLoopComponent[] components, CommonLoopPreference preference, long minimumSeconds) =>
            CommonLoopSolver.Suggest(new(60, 1, components, new(minimumSeconds), new(180), MaximumRetimePercent: 2, Preference: preference));

        // 合成分量：最短可行周期 758 帧要 1.17% 的单分量调速，总成本最低的解在 8991 帧。
        CommonLoopComponent[] shimmer =
        [
            new("shimmer", new(6.242857089, CommonLoopPeriodEvidence.Analytic), true),
            new("noise", new(.8514261213, CommonLoopPeriodEvidence.Analytic), true)
        ];
        CommonLoopSearchResult performance = Solve(shimmer, CommonLoopPreference.Performance, 1);
        CommonLoopSearchResult balanced = Solve(shimmer, CommonLoopPreference.Balanced, 1);
        CommonLoopSearchResult quality = Solve(shimmer, CommonLoopPreference.Quality, 1);

        check(performance.Candidates[0].Frames == 758 && performance.RetimeBudgetPercent == 2 && !performance.BudgetRelaxed &&
            performance.Preference == CommonLoopPreference.Performance,
            "performance keeps the shortest feasible period first inside the full two percent budget");
        check(balanced.Candidates[0].Frames == 1124 && balanced.RetimeBudgetPercent == 1 && !balanced.BudgetRelaxed &&
            balanced.Candidates.All(candidate => candidate.Components.All(component => Math.Abs(component.DeltaPercent) <= 1 + 1e-9)),
            "balanced solves inside the tighter one percent component budget without relaxing it");
        check(quality.Candidates[0].Frames == 8991 && quality.RetimeBudgetPercent == 2 &&
            quality.Candidates[0].TotalRetimeCostPercent < performance.Candidates[0].TotalRetimeCostPercent / 100,
            "quality puts the cheapest total retime cost first even though its period is far longer");
        check(quality.Candidates.Zip(quality.Candidates.Skip(1)).All(pair => pair.First.TotalRetimeCostPercent <= pair.Second.TotalRetimeCostPercent) &&
            quality.Candidates.Zip(quality.Candidates.Skip(1)).All(pair => pair.First.TotalRetimeCostPercent < pair.Second.TotalRetimeCostPercent ||
                pair.First.Frames < pair.Second.Frames),
            "quality orders the whole table by total retime cost and breaks ties on the shorter period");
        check(performance.Candidates.Skip(1).Zip(performance.Candidates.Skip(2))
                .All(pair => pair.First.TotalRetimeCostPercent <= pair.Second.TotalRetimeCostPercent) &&
            performance.Candidates.Count == 20 && balanced.Candidates.Count == 20 && quality.Candidates.Count == 20,
            "every preference keeps a full candidate table and only changes its order");

        // 1% 预算下这两个分量在 10..180 秒内无解，2% 下有 19 个可行帧。
        CommonLoopComponent[] coarse =
        [
            new("slow", new(110.0, CommonLoopPeriodEvidence.Analytic), true),
            new("swirl", new(35.33, CommonLoopPeriodEvidence.Analytic), true)
        ];
        CommonLoopSearchResult coarseBalanced = Solve(coarse, CommonLoopPreference.Balanced, 10);
        CommonLoopSearchResult coarsePerformance = Solve(coarse, CommonLoopPreference.Performance, 10);
        CommonLoopSearchResult coarseQuality = Solve(coarse, CommonLoopPreference.Quality, 10);
        check(coarseBalanced.BudgetRelaxed && coarseBalanced.RetimeBudgetPercent == 2 && coarseBalanced.Candidates[0].Frames == 6480,
            "balanced falls back to the full budget and records the relaxation when one percent closes nothing");
        check(!coarsePerformance.BudgetRelaxed && coarsePerformance.Candidates[0].Frames == 6480 && coarseQuality.Candidates[0].Frames == 6489 &&
            coarsePerformance.Candidates.Select(candidate => candidate.Frames).ToHashSet()
                .SetEquals(coarseQuality.Candidates.Select(candidate => candidate.Frames)) &&
            coarsePerformance.Candidates.Count == 19,
            "a relaxed balanced table is the performance table, and quality reorders the same 19 candidates");

        // 预算不是失败原因时（未知周期）不回落，收紧过的预算原样报出。
        CommonLoopSearchResult unknown = Solve([new("known", new(2, CommonLoopPeriodEvidence.Analytic, new(2))), new("blink", null)],
            CommonLoopPreference.Balanced, 10);
        check(unknown.Candidates.Count == 0 && unknown.UnresolvedConstraints.Count == 1 && !unknown.BudgetRelaxed && unknown.RetimeBudgetPercent == 1,
            "a structural constraint never triggers the balanced budget fallback");

        check(new CommonLoopSolveRequest(60, 1, shimmer).Preference == CommonLoopPreference.Balanced &&
            new HybridAnalyzeRequest(2, "source", "assets", "out").LoopPreference == "balanced",
            "balanced is the default loop preference of both the solver and the analysis request");
        check(HybridScenePlanner.LoopPreferenceOf("performance") == CommonLoopPreference.Performance &&
            HybridScenePlanner.LoopPreferenceOf("balanced") == CommonLoopPreference.Balanced &&
            HybridScenePlanner.LoopPreferenceOf("quality") == CommonLoopPreference.Quality,
            "the three preference names map to the three solver preferences");
        bool rejected = false;
        try { HybridScenePlanner.LoopPreferenceOf("ultra"); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "an unknown loop preference name is rejected instead of silently defaulting");

        var annotate = typeof(HybridScenePlanner).GetMethod("AnnotateLoopCandidates", BindingFlags.Static | BindingFlags.NonPublic)!;
        var loop = new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 8671UL }, new JsonObject { ["frames"] = 1920UL })
        };
        annotate.Invoke(null, [loop]);
        check(loop["candidates"]![0]!["rank"]!.GetValue<int>() == 1 && loop["candidates"]![1]!["rank"]!.GetValue<int>() == 2 &&
            loop["selected_candidate_index"]!.GetValue<int>() == 0,
            "plan candidates carry their rank and name the selected candidate index");
        var empty = new JsonObject { ["candidates"] = new JsonArray() };
        annotate.Invoke(null, [empty]);
        check(empty.ContainsKey("selected_candidate_index") && empty["selected_candidate_index"] is null,
            "an empty candidate table reports no selected candidate index");
    }
}
