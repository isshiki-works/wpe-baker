using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// bake.json（schema 2）的唯一骨架与写盘入口。三种报告（拒绝、分组/整帧路线开跑、效果前缀路线开跑）共用同一个身份头与
/// 两项 not_verified 占位，其余键按各自原有顺序追加，写出与改动前逐字节相同。
/// </summary>
internal static class BakeReportWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>身份头：schema、产物种类、状态、源哈希与摘要口径。</summary>
    internal static JsonObject Head(string artifactKind, string status, string sourceSha256) => new()
    {
        ["schema_version"] = 2, ["artifact_kind"] = artifactKind, ["status"] = status,
        ["source_sha256"] = sourceSha256, ["source_digest_scope"] = ProjectSource.DigestScope
    };

    /// <summary>两项从未实测过的占位（official_playback / measured_gain），C3 与 v3 报告一起删。</summary>
    internal static void AddNotVerified(JsonObject report)
    {
        report["official_playback"] = "not_verified";
        report["measured_gain"] = "not_verified";
    }

    /// <summary>分组/整帧路线开跑时的报告；探针与成品只差 artifact_kind。</summary>
    internal static JsonObject Running(HybridBakeRequest request, string sourceSha256, ulong frames, JsonObject plan,
        int groupParallel, string seamPolicy)
    {
        JsonObject report = Head(request.ProbeFrames > 0 ? "hybrid_video_probe" : "hybrid_video_candidate", "running", sourceSha256);
        report["frames"] = frames;
        report["plan"] = plan;
        report["groups"] = new JsonArray();
        AddNotVerified(report);
        report["loop_validation"] = "not_performed";
        report["source_start_frame"] = 0;
        // 这两项记下本次实际生效的并行设置，事后核对每案耗时时不用再翻命令行。
        report["encode_slots"] = request.EncodeSlots;
        report["group_parallel"] = groupParallel;
        report["effect_render_scale"] = request.EffectRenderScale;
        report["match_effect_resolution"] = request.MatchEffectResolution;
        report["seam_policy"] = seamPolicy;
        return report;
    }

    /// <summary>效果前缀路线开跑时的报告：没有帧数与并行设置，接缝策略固定为源周期不修补。</summary>
    internal static JsonObject EffectPrefixRunning(string sourceSha256, JsonObject plan)
    {
        JsonObject report = Head("hybrid_video_candidate", "running", sourceSha256);
        report["plan"] = plan;
        report["groups"] = new JsonArray();
        report["source_start_frame"] = 0;
        report["seam_policy"] = "source_period_no_repair";
        AddNotVerified(report);
        return report;
    }

    /// <summary>覆盖写（运行中、收尾、清理后重写）；给了计时就先盖阶段计时戳。</summary>
    internal static Task SaveAsync(string path, JsonObject report, StageTiming? timing, CancellationToken cancellationToken)
    {
        StampEncodedWork(report);
        timing?.Stamp(report);
        return File.WriteAllTextAsync(path, report.ToJsonString(Options), cancellationToken);
    }

    /// <summary>新建写（拒绝报告、发布目录里那一份）：父目录不存在就建，文件已存在即失败。</summary>
    internal static async Task<JsonObject> WriteNewAsync(string path, JsonObject report, StageTiming? timing,
        CancellationToken cancellationToken)
    {
        StampEncodedWork(report);
        timing?.Stamp(report);
        await VideoSceneBuilder.WriteJsonAsync(path, report, cancellationToken);
        return report;
    }

    private static void StampEncodedWork(JsonObject report)
    {
        if (report["status"]?.GetValue<string>() != "candidate_generated" || report["plan"] is not JsonObject plan) return;
        double fps = BakeValueAssessment.Number(plan["settings"]?["fps_numerator"]) /
            BakeValueAssessment.Number(plan["settings"]?["fps_denominator"]);
        JsonObject[] videos = [.. (report["groups"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(group => group["status"]?.GetValue<string>() == "encoded" && group["video_path"] is JsonValue)];
        double pixels = 0;
        bool complete = double.IsFinite(fps) && fps > 0;
        foreach (JsonObject group in videos)
        {
            JsonNode? extent = group["encoded_extent"] ?? group["hardware_decode_preflight"]?["encoded_extent"];
            double width = BakeValueAssessment.Number(extent?[0]), height = BakeValueAssessment.Number(extent?[1]);
            if (!WorkloadValue.IsComparableOutput(width, height, fps)) { complete = false; break; }
            pixels += width * height * fps;
        }
        report["encoded_video_work"] = new JsonObject {
            ["status"] = complete ? "measured_dimensions" : "incomplete",
            ["video_streams"] = videos.Length,
            ["coded_pixels_per_second"] = complete ? pixels : null,
            ["scope"] = "Sum of encoded video dimensions times frame rate; packed alpha is already included in the coded width or height. This is decoder input, not measured power."
        };
    }
}
