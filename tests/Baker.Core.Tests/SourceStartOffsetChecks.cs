using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

/// <summary>旧成品里的源周期起点偏移记录：记录齐全才放行非零起点，GUI 摘要说明起点为什么不是 0。</summary>
internal static class SourceStartOffsetChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // 与 LoopWarmup 的叠加：S 与残差路线的起点相位同一个位置，精灵整周期预热照旧相加。
        check(LoopWarmup.MasterFrames(0, 29241, 0, SourceStartOffset.OffsetFrames) == 1 &&
              LoopWarmup.MasterFrames(336, 336, 0, SourceStartOffset.OffsetFrames) == 337 &&
              LoopWarmup.BaseFrames(336, 336, 0) == 336,
            "起点偏移：master 跳过 = 精灵整周期预热 + 偏移，精灵预热仍只能是 0 或 P");

        // 保存结果的放行：偏移记录要在、起点要是 1、与 source_start_frame 一致、是源周期路线。
        JsonObject Result(ulong reported, ulong recorded, string policy = "source_period_no_repair", string status = SourceStartOffset.AppliedStatus) => new()
        {
            ["seam_policy"] = policy,
            ["source_start_frame"] = reported,
            ["source_start_offset"] = new JsonObject { ["status"] = status, ["start_frame"] = recorded }
        };
        check(SourceStartOffset.Allows(Result(1, 1)) && SourceStartOffset.Allows(JsonNode.Parse(Result(1, 1).ToJsonString())!.AsObject()),
            "起点偏移：记录齐全的非零起点放行（含读回的 JSON）");
        check(!SourceStartOffset.Allows(Result(2, 2)) && !SourceStartOffset.Allows(Result(1, 2)) &&
              !SourceStartOffset.Allows(Result(1, 1, ResidualMasking.SeamPolicy)) &&
              !SourceStartOffset.Allows(Result(1, 1, status: "not_applied")) &&
              !SourceStartOffset.Allows(new JsonObject { ["seam_policy"] = "source_period_no_repair", ["source_start_frame"] = 1UL }),
            "起点偏移：起点不是 1、与记录不一致、残差路线、未应用或没有记录时不放行");

        var saved = JsonNode.Parse("""
            {"schema_version":2,"artifact_kind":"hybrid_video_candidate","status":"candidate_generated","seam_policy":"source_period_no_repair",
             "source_start_frame":1,"source_start_offset":{"status":"applied","start_frame":1},
             "plan":{"loop":{"status":"analytic_candidate_requires_seam_validation",
             "source_static":false,"candidates":[{"frames":29241}],"unresolved":[]}}}
            """)!.AsObject();
        check(AppJsonPresentation.CandidateCanApply(saved), "起点偏移：GUI 放行带偏移记录的源周期成品");
        check(AppJsonPresentation.PhaseStartSummary(saved, false).Contains("起点异常帧", StringComparison.Ordinal) &&
              AppJsonPresentation.PhaseStartSummary(saved, true).Contains("isolated start anomaly", StringComparison.Ordinal),
            "起点偏移：GUI 摘要说明起点为什么不是 0");
        saved.Remove("source_start_offset");
        check(!AppJsonPresentation.CandidateCanApply(saved), "起点偏移：GUI 不放行没有偏移记录的非零起点");
    }
}
