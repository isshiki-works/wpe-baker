using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class SharedLoopStartChecks
{
    private static readonly MethodInfo Combine = typeof(NativeRenderRunner)
        .GetMethod("CombineLoopStartSearches", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static void Run(Action<bool, string> check)
    {
        JsonObject Search(string id) => new()
        {
            ["group_id"] = id, ["warmup_frames"] = 1348UL, ["period_frames"] = 64UL,
            ["stride_frames"] = 16, ["crossfade_frames"] = 24, ["candidate_count"] = 4,
            ["sampling_coverage"] = new JsonObject { ["owner"] = id }
        };
        JsonObject Select(params (JsonObject Search, IReadOnlyList<ResidualStartCandidate> Candidates)[] groups) =>
            (JsonObject)Combine.Invoke(null, [groups])!;

        ResidualStartCandidate[] first = [new(0, .5, 1, 2), new(16, 1.9, 8, 9), new(32, 1.2, 20, 21), new(48, 2.00004, .1, .1)];
        ResidualStartCandidate[] second = [new(0, 5.616, 30, 31), new(16, 1.5, 6, 7), new(32, .1, 1, 2), new(48, .2, .1, .1)];
        JsonObject firstSearch = Search("group-1"), secondSearch = Search("group-2");
        JsonObject joint = Select((firstSearch, first), (secondSearch, second));
        check(ResidualMasking.OrderStartCandidates(first)[0].Start == 0 &&
            ResidualMasking.OrderStartCandidates(second)[0].Start == 48 &&
            joint["selected_start_frame"]!.GetValue<ulong>() == 16 &&
            joint["selected"]!["sampled_global_rgb_mae_255"]!.GetValue<double>() == 1.9 &&
            joint["selected"]!["sampled_sort_key"]!.GetValue<double>() == 9 &&
            joint["candidates_within_global_limit"]!.GetValue<int>() == 2 &&
            joint["groups"]![0]!["at_shared_start"]!["start_frame"]!.GetValue<ulong>() == 16 &&
            joint["groups"]![1]!["at_shared_start"]!["start_frame"]!.GetValue<ulong>() == 16,
            "shared loop start: conflicting individual optima choose the common admitted phase, using every group's original precision before reporting rounded values");
        check(Select((secondSearch, second), (firstSearch, first))["selected_start_frame"]!.GetValue<ulong>() == 16 &&
            ReferenceEquals(Select((firstSearch, first)), firstSearch) &&
            joint["maximum_start_attempts"]!.GetValue<int>() == 1 && joint["start_attempt_order"]!.AsArray().Count == 1 &&
            joint["sampling_coverage"] is null &&
            joint["groups"]![1]!["sampling_coverage"]!["owner"]!.GetValue<string>() == "group-2" &&
            firstSearch["at_shared_start"] is null,
            "shared loop start: group order does not matter, one group is unchanged, one complete render remains the limit, and coverage stays attached to its own group");

        ResidualStartCandidate[] blocked = second.Select(candidate => candidate with { Global = 3 }).ToArray();
        JsonObject none = Select((firstSearch, first), (secondSearch, blocked));
        check(none["candidates_within_global_limit"]!.GetValue<int>() == 0 &&
            none["status"]!.GetValue<string>() == "selected_without_joint_global_limit_candidate" &&
            none["selected"]!["sampled_global_rgb_mae_255"]!.GetValue<double>() == 3,
            "shared loop start: no jointly admitted phase remains explicitly unadmitted, with no threshold relaxation");
        // 各组按自身周期 P_g 评分：短周期组的候选（0..P_short）是长周期组的前缀，共享起点只在公共前缀里挑。
        JsonObject shortSearch = Search("group-1");
        shortSearch["period_frames"] = 32UL;
        JsonObject prefix = Select((shortSearch, first[..2]), (secondSearch, second));
        check(prefix["selected_start_frame"]!.GetValue<ulong>() == 16 && prefix["candidates_within_global_limit"]!.GetValue<int>() == 1 &&
            prefix["groups"]![1]!["at_shared_start"]!["start_frame"]!.GetValue<ulong>() == 16,
            "shared loop start: groups with different periods share a start within the shorter group's phases");
        bool mismatchRejected = false;
        try { _ = Select((firstSearch, first), (secondSearch, second.Select(candidate => candidate with { Start = candidate.Start + 8 }).ToArray())); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { mismatchRejected = true; }
        check(mismatchRejected, "shared loop start: groups on different phase grids cannot be combined");
    }
}
