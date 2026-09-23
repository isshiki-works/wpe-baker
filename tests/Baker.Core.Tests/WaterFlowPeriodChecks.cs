using Baker.Core;

/// <summary>水流四相规则：裁定断言在 tests/corpus/shader-corpus.json 的 water-flow；这里只留跨模块的求解器断言。</summary>
internal static class WaterFlowPeriodChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        ShaderPeriodAnalysisResult analysis = ShaderCorpusChecks.Run(check, root, "water-flow").Analysis;
        ShaderPeriodComponent flow = analysis.Components.Single();
        var sprite = new CommonLoopComponent("sprite", new CommonLoopPeriod(4.53,
            CommonLoopPeriodEvidence.Observed, new CommonLoopRational(453, 100)));
        CommonLoopSearchResult joint = CommonLoopSolver.Suggest(new(60, 1, [flow.Component, sprite], MaximumRetimePercent: 2));
        CommonLoopCandidate candidate = joint.Candidates.Single(x => x.Frames == 4077);
        CommonLoopComponentCycle cycle = candidate.Components.Single(x => x.ComponentId == flow.Component.Id);
        double patchedSpeed = .19 * cycle.SpeedMultiplier;
        check(joint.FixedFrameStep == 1359 && cycle.Cycles == 13 && Math.Abs(cycle.DeltaPercent) < 2 &&
            Math.Abs(patchedSpeed - 13 / 67.95) < 1e-12,
            "water flow retimes within two percent to a frame-exact common loop with a 4.53-second sprite");
    }
}
