using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

/// <summary>
/// 源周期路线的起点偏移：第 0 帧孤立异常（闭合只因它失败）才顺延 1 帧；闭合通过、周期真错、连续多帧异常都不偏移。
/// 帧用合成数据：整幅灰度按 100 + 30·sin(2πk/P) 周期变化，异常帧把左上 64px 瓦片涂黑（或 alpha 清零）。
/// </summary>
internal static class SourceStartOffsetChecks
{
    private const int Width = 128, Height = 64;
    private const ulong Period = 60;

    private static byte[] Frame(ulong k, bool opaque = true)
    {
        byte value = (byte)Math.Round(100 + 30 * Math.Sin(2 * Math.PI * (k % Period) / Period));
        byte[] rgba = new byte[Width * Height * 4];
        for (int pixel = 0; pixel < Width * Height; ++pixel)
        {
            rgba[pixel * 4] = rgba[pixel * 4 + 1] = rgba[pixel * 4 + 2] = value;
            rgba[pixel * 4 + 3] = opaque ? (byte)255 : (byte)200;
        }
        return rgba;
    }

    private static byte[] Anomalous(ulong k, bool alpha = false)
    {
        byte[] rgba = Frame(k, opaque: !alpha);
        for (int y = 0; y < 64; ++y)
            for (int x = 0; x < 64; ++x)
            {
                int p = (y * Width + x) * 4;
                if (alpha) rgba[p + 3] = 0;
                else rgba[p] = rgba[p + 1] = rgba[p + 2] = 0;
            }
        return rgba;
    }

    private static string Status(byte[] f0, byte[] f1, byte[] f2, byte[] beforeWrap, byte[] wrap, bool withAlpha = false, ulong period = Period) =>
        SourceStartOffset.Evaluate(f0, f1, f2, beforeWrap, wrap, Width, Height, withAlpha, period)["status"]!.GetValue<string>();

    internal static void Run(Action<bool, string> check)
    {
        // 3653991401 形态：第 0 帧有黑块，第 P 帧（解析周期的"第 0 帧"）正常，接得上第 1 帧。
        JsonObject isolated = SourceStartOffset.Evaluate(Anomalous(0), Frame(1), Frame(2), Frame(Period - 1), Frame(Period),
            Width, Height, false, Period);
        check(SourceStartOffset.RequiresOffset(isolated) && isolated["closure_f0_fp"]!.GetValue<double>() > 90 &&
              isolated["bridge_fp_f1"]!.GetValue<double>() <= isolated["ordinary_step"]!.GetValue<double>() + LoopClosureCheck.MaximumTileMae255,
            "起点偏移：第 0 帧孤立异常、第 P 帧接得上第 1 帧时要求偏移");

        check(Status(Frame(0), Frame(1), Frame(2), Frame(Period - 1), Frame(Period)) == SourceStartOffset.ClosedStatus,
            "起点偏移：闭合通过时不偏移");

        // 两端都异常（闭合碰巧通过）：第 0 帧的内容在第 P 帧同样出现，任何整数帧偏移只会把它挪进片中，不偏移。
        check(Status(Anomalous(0), Frame(1), Frame(2), Frame(Period - 1), Anomalous(Period)) == SourceStartOffset.ClosedStatus,
            "起点偏移：第 0 帧与第 P 帧同样异常时闭合通过，不偏移");

        // 周期真错（渲染器第 P−1、P 帧其实落在相位 9、10 帧处，两帧仍是连续的普通一步）：第 0 帧本身正常，不是孤立异常。
        check(Status(Frame(0), Frame(1), Frame(2), Frame(Period + 9), Frame(Period + 10)) == SourceStartOffset.NotIsolatedStatus,
            "起点偏移：周期错导致的闭合失败不偏移");

        // 第 0 帧异常但周期也错：第 P 帧接不上第 1 帧，偏移救不了，不偏移。
        check(Status(Anomalous(0), Frame(1), Frame(2), Frame(Period + 9), Frame(Period + 10)) == SourceStartOffset.NotIsolatedStatus,
            "起点偏移：第 0 帧异常且第 P 帧接不上第 1 帧时不偏移");

        // 连续两帧异常：顺延一帧仍落在异常里，不偏移。
        check(Status(Anomalous(0), Anomalous(1), Frame(2), Frame(Period - 1), Frame(Period)) == SourceStartOffset.NotIsolatedStatus,
            "起点偏移：第 0、1 帧都异常时不偏移");

        // 变异对照：去掉"第 P 帧接得上第 1 帧"这一条，周期错 + 第 0 帧异常会被误判为孤立异常。
        double ordinary = Math.Max(LoopClosureCheck.WorstTileMae(Frame(Period + 9), Frame(Period + 10), Width, Height, false),
            LoopClosureCheck.WorstTileMae(Frame(1), Frame(2), Width, Height, false));
        check(LoopClosureCheck.WorstTileMae(Anomalous(0), Frame(1), Width, Height, false) > ordinary + LoopClosureCheck.MaximumTileMae255,
            "起点偏移（变异对照）：只看第 0 帧偏离轨迹不足以区分周期错");

        // 透明组：第 0 帧某瓦片 alpha 清零，按闭合检验同一口径（alpha 平面单独判）同样识别。
        check(Status(Anomalous(0, alpha: true), Frame(1, false), Frame(2, false), Frame(Period - 1, false), Frame(Period, false), withAlpha: true) ==
              SourceStartOffset.IsolatedStatus,
            "起点偏移：透明组第 0 帧 alpha 孤立异常时要求偏移");

        check(Status(Anomalous(0), Frame(1), Frame(2), Frame(1), Frame(2), period: 2) == SourceStartOffset.NotApplicableStatus,
            "起点偏移：P < 3 时五帧不能互不相同，不判");

        // 与闭合检验同一把尺子：WorstTileMae 与 Evaluate 的最差瓦片一致（Evaluate 保留 4 位）。
        JsonObject closure = LoopClosureCheck.Evaluate(Anomalous(0), Frame(Period), Width, Height, false, Period, true);
        check(Math.Abs(closure["rgb"]!["tile_64"]!["worst"]!.GetValue<double>() -
                Math.Round(LoopClosureCheck.WorstTileMae(Anomalous(0), Frame(Period), Width, Height, false), 4)) < 1e-9,
            "起点偏移：瓦片读数与闭合检验的最差瓦片一致");

        check(SourceStartOffset.RetainedFrameIndices(1).SequenceEqual([0UL, 1UL]) &&
              SourceStartOffset.RetainedFrameIndices(2).SequenceEqual([0UL, 1UL, 2UL]) &&
              SourceStartOffset.RetainedFrameIndices(3).SequenceEqual([0UL, 1UL, 2UL, 3UL]) &&
              SourceStartOffset.RetainedFrameIndices(29241).SequenceEqual([0UL, 1UL, 2UL, 29240UL, 29241UL]),
            "起点偏移：源周期路线留第 0、1、2、P−1、P 帧，去重且不超过 P");

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
