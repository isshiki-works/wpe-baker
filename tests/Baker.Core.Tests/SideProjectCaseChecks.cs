using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

// C0.2：删侧工程前抄进来的独有案例，代码原样搬入（只把 Check 换成参数、临时目录换成 root）。
// 侧工程早已不跑：参照构造（现为 ProbeBake.CreateReferenceAsync）后来多了 plan 参数，这里补传空 plan（其中的 Apply* 对空 plan 不做事）。
// tests/hybrid-composition-validator 里读 artifacts/ 下 4 份留存 comparison.json 的语料案例没有搬：那些文件不在仓库里。

/// <summary>原 tests/common-loop-solver：waterwaves 100 s 精确网格、NTSC 有理帧率、不可能区间、未知周期、早周期组合。</summary>
internal static class CommonLoopSolverCaseChecks
{
    internal static void Run(Action<bool, string> Check)
    {
var fable = new CommonLoopSolveRequest(60, 1,
[
    new("waterwave-a", new(2 * Math.PI / 5, CommonLoopPeriodEvidence.Analytic), true),
    new("waterwave-b", new(Math.PI, CommonLoopPeriodEvidence.Analytic), true),
    new("fixed-2.5", new(2.5, CommonLoopPeriodEvidence.Analytic, new(5, 2))),
    new("fixed-4/3", new(4d / 3, CommonLoopPeriodEvidence.Analytic, new(4, 3))),
    new("fixed-25/7", new(25d / 7, CommonLoopPeriodEvidence.Analytic, new(25, 7))),
    new("fixed-50/9", new(50d / 9, CommonLoopPeriodEvidence.Analytic, new(50, 9))),
    new("fixed-4", new(4, CommonLoopPeriodEvidence.Analytic, new(4))),
    new("fixed-20/9", new(20d / 9, CommonLoopPeriodEvidence.Analytic, new(20, 9)))
]);
var at100 = CommonLoopSolver.EvaluateAtFrames(fable, 6000);
Check(at100.Candidate is not null && at100.Frames == 6000, "100 seconds is an exact 60 FPS grid duration");
var waterA = at100.Candidate!.Components.Single(x => x.ComponentId == "waterwave-a");
var waterB = at100.Candidate.Components.Single(x => x.ComponentId == "waterwave-b");
Check(Math.Abs(waterA.SpeedMultiplier - 1.0053096491487339) < 1e-9 && Math.Abs(waterB.SpeedMultiplier - 1.0053096491487339) < 1e-9,
    "analytic waterwaves choose the expected local retime at 100 seconds");
Check(at100.Candidate.Components.Where(x => x.ComponentId.StartsWith("fixed-", StringComparison.Ordinal)).All(x => x.SpeedMultiplier == 1 && x.DeltaPercent == 0),
    "all fixed rational periods close exactly at 100 seconds");
Check(at100.Candidate.Components.Where(x => x.ComponentId.StartsWith("fixed-", StringComparison.Ordinal)).Select(x => x.Cycles)
    .SequenceEqual(new ulong[] { 40, 75, 28, 18, 25, 45 }), "fixed component cycle counts are exact");
var fableSuggestions = CommonLoopSolver.Suggest(fable);
Check(fableSuggestions.Candidates.Count > 0 && fableSuggestions.FixedFrameStep == 6000 &&
      fableSuggestions.Candidates.Any(x => x.Frames == 6000), "fixed periods reduce the bounded search to their common frame step");

var ntsc = new CommonLoopSolveRequest(120000, 1001,
    [new("grid-period", new(1001d / 120, CommonLoopPeriodEvidence.Analytic, new(1001, 120)))], new(10), new(20));
var ntscResult = CommonLoopSolver.Suggest(ntsc);
Check(ntscResult.Candidates.Count > 0 && ntscResult.Candidates.All(x => x.Frames % 1000 == 0) &&
      ntscResult.Candidates.All(x => Math.Abs(x.Seconds * 120000 / 1001 - x.Frames) < 1e-9),
    "rational FPS constrains candidates to exact frame counts");
Check(ntscResult.FixedFrameStep == 1000, "120000/1001 FPS period constraint has the expected exact frame step");

var impossible = new CommonLoopSolveRequest(60, 1,
    [new("seven", new(7, CommonLoopPeriodEvidence.Analytic, new(7)))], new(10), new(12));
Check(CommonLoopSolver.Suggest(impossible).Candidates.Count == 0, "no fixed common cycle is invented inside an impossible duration range");

var unknown = new CommonLoopSolveRequest(60, 1,
    [new("known", new(2, CommonLoopPeriodEvidence.Analytic, new(2))), new("blink", null)]);
var unknownResult = CommonLoopSolver.Suggest(unknown);
Check(unknownResult.Candidates.Count == 0 && unknownResult.UnresolvedConstraints.Single().Kind == CommonLoopConstraintKind.UnknownPeriod,
    "unknown event marker is surfaced as a constraint and never accepted as a hard cut");

var balancedEarlyCycles = new CommonLoopSolveRequest(60, 1,
[
    new("shimmer", new(6.242857089, CommonLoopPeriodEvidence.Analytic), true),
    new("noise", new(.8514261213, CommonLoopPeriodEvidence.Analytic), true)
], new(1), new(180), MaximumRetimePercent: 2, Preference: CommonLoopPreference.Performance);
var balancedResult = CommonLoopSolver.Suggest(balancedEarlyCycles);
CommonLoopCandidate balanced = balancedResult.Candidates[0];
Check(balanced.Frames == 758 && balanced.Components.Select(component => component.Cycles).SequenceEqual([2UL, 15UL]) &&
      Math.Abs(balanced.Components.Max(component => Math.Abs(component.DeltaPercent)) - 1.1684893562005194) < 1e-6,
    "the earliest feasible shimmer/noise cycle combination chooses its balanced 758-frame seam before longer lower-cost alternatives");

    }
}

/// <summary>原 tests/hybrid-composition-validator 里比较参照的构造（阈值判定见 PairedComparisonTests）。</summary>
internal static class CompositionReferenceCaseChecks
{
    internal static async Task RunAsync(Action<bool, string> Check, string root)
    {
string referenceRoot = Path.Combine(root, "hybrid-composition-reference");
string referenceSource = Path.Combine(referenceRoot, "source");
Directory.CreateDirectory(referenceSource);
await File.WriteAllTextAsync(Path.Combine(referenceSource, "project.json"),
    "{\"type\":\"scene\",\"file\":\"scene.json\",\"general\":{\"properties\":{\"tone\":{\"value\":false}}}}");
await File.WriteAllTextAsync(Path.Combine(referenceSource, "scene.json"),
    "{\"general\":{\"camerashake\":true,\"cameraparallax\":true,\"preserved\":17},\"objects\":[]}");
Task CreateReferenceAsync(ProjectSource source, string destination, string viewMode) =>
    ProbeBake.CreateReferenceAsync(source, destination, new JsonObject { ["tone"] = true }, viewMode, new JsonObject(), CancellationToken.None);
using (var source = new ProjectSource(referenceSource))
{
    string fixedReference = Path.Combine(referenceRoot, "fixed");
    await CreateReferenceAsync(source, fixedReference, "fixed_view");
    JsonObject fixedScene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixedReference, "scene.json")))!.AsObject();
    JsonObject fixedProject = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixedReference, "project.json")))!.AsObject();
    Check(fixedScene["general"]!["camerashake"]!.GetValue<bool>() &&
        !fixedScene["general"]!["cameraparallax"]!.GetValue<bool>() &&
        fixedScene["general"]!["preserved"]!.GetValue<int>() == 17,
        "fixed-view reference preserves camera shake and live scene state while disabling parallax");
    Check(fixedProject["general"]!["properties"]!["tone"]!["value"]!.GetValue<bool>(),
        "comparison reference applies the selected property snapshot");
    string preserveReference = Path.Combine(referenceRoot, "preserve");
    await CreateReferenceAsync(source, preserveReference, "preserve");
    JsonObject preserveScene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(preserveReference, "scene.json")))!.AsObject();
    Check(preserveScene["general"]!["camerashake"]!.GetValue<bool>() &&
        preserveScene["general"]!["cameraparallax"]!.GetValue<bool>(),
        "preserve reference retains original camera behavior");
    JsonObject unchanged = source.ReadJson(source.SceneResource);
    Check(unchanged["general"]!["camerashake"]!.GetValue<bool>() &&
        unchanged["general"]!["cameraparallax"]!.GetValue<bool>(), "reference construction does not mutate its source");
}

    }
}
