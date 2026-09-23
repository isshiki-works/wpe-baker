using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 合成校验：用同一份计划跑一个短探针，与原作参照配对比较完整画面（<see cref="ProbeBake"/>），比较结果过
/// <see cref="Evaluate"/> 的固定阈值。不通过就拒绝，报告带上探针、参照与比较结果的路径；
/// 探针里出现候选特有的脚本报错时按 <see cref="CandidateScriptErrorGate"/> 的状态单独拒绝，并附中英文理由。
/// </summary>
internal sealed class CompositionGate(CompositionGate.Validator validate) : IBakeGate
{
    // ---- 阈值判定（原 HybridCompositionValidator）：对 48 帧成对比较套固定阈值。候选脚本报错多于原作时先拒绝、不判像素；
    // 保留脚本的查找绑定不一致时拒绝；然后判全局 RGB、全局 alpha 与每个 64px 瓦片的 RGB 平均误差。
    // 返回的对象就是 bake.json 的 composition_validation（v2 形状）。----
    public const ulong RequiredFrames = 48;
    public const uint RequiredTileSize = 64;
    public const double MaximumGlobalRgbMae255 = 8;
    public const double MaximumTileRgbMae255 = 25;
    public const double MaximumGlobalAlphaMae255 = 1;

    public static JsonObject Evaluate(PairedComparison comparison)
    {
        JsonObject report = comparison.Report;
        var failures = new JsonArray();
        var result = new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = "composition_rejected",
            ["scope"] = "A short offline composition gate over 48 paired frames; it does not certify a full loop or official playback.",
            ["automatic_visual_certification"] = false,
            ["official_playback_verified"] = false,
            // v2 字段：合成比较总从第 0 帧起，恒为 0。
            ["expected_source_start_frame"] = 0UL,
            ["limits"] = new JsonObject
            {
                ["global_rgb_mae_255"] = MaximumGlobalRgbMae255,
                ["every_64px_tile_rgb_mae_255"] = MaximumTileRgbMae255,
                ["global_alpha_mae_255"] = MaximumGlobalAlphaMae255
            },
            ["failures"] = failures,
            ["comparison_report_path"] = report["report_path"]!.GetValue<string>()
        };
        // 候选脚本报错多于原作：直接拒绝，不再判定后面的像素指标；指标原样附上只作记录。
        JsonObject scriptErrors = CandidateScriptErrorGate.Evaluate(report);
        result["script_error_validation"] = scriptErrors;
        if (scriptErrors["status"]?.GetValue<string>() == CandidateScriptErrorGate.RejectedValidationStatus)
        {
            failures.Add(scriptErrors["reason"]!.GetValue<string>());
            result["status"] = CandidateScriptErrorGate.RejectedCompositionStatus;
            result["reason"] = scriptErrors["reason"]!.DeepClone();
            result["reason_zh"] = scriptErrors["reason_zh"]!.DeepClone();
            result["reason_en"] = scriptErrors["reason_en"]!.DeepClone();
            result["frames_compared"] = report["frames_compared"]?.DeepClone();
            result["metrics"] = report["metrics"]?.DeepClone();
            result["metrics_judged"] = false;
            return result;
        }
        if (report["lookup_binding_validation"] is JsonObject lookups)
        {
            result["lookup_binding_validation"] = lookups.DeepClone();
            if (lookups["status"]?.GetValue<string>() != "observed_lookups_match")
                failures.Add("Retained script lookup bindings differ or their paired trace is unavailable.");
        }
        double globalRgb = comparison.Total.RgbMae, globalAlpha = comparison.Total.AlphaMae;
        if (globalRgb > MaximumGlobalRgbMae255)
            failures.Add($"Global RGB MAE {globalRgb:G6} exceeds {MaximumGlobalRgbMae255:G6}.");
        if (globalAlpha > MaximumGlobalAlphaMae255)
            failures.Add($"Global alpha MAE {globalAlpha:G6} exceeds {MaximumGlobalAlphaMae255:G6}.");
        int worst = 0;
        double worstRgb = double.NegativeInfinity;
        for (int i = 0; i < comparison.Tiles.Length; ++i)
        {
            PairedTile tile = comparison.Tiles[i];
            double rgb = tile.Errors.RgbMae;
            if (rgb > worstRgb) { worstRgb = rgb; worst = i; }
            if (rgb > MaximumTileRgbMae255)
                failures.Add($"Tile ({tile.X},{tile.Y}) RGB MAE {rgb:G6} exceeds {MaximumTileRgbMae255:G6}.");
        }
        result["frames_compared"] = report["frames_compared"]?.DeepClone();
        result["metrics"] = report["metrics"]?.DeepClone();
        result["tiles_evaluated"] = comparison.Tiles.Length;
        result["worst_rgb_tile"] = report["tiles"]![worst]!.DeepClone();
        bool passed = failures.Count == 0;
        result["status"] = passed ? "composition_pass" : "composition_rejected";
        new Message(passed ? "reason.composition_pass" : "reason.composition_rejected").Write(result, "reason");
        return result;
    }

    /// <summary>跑短探针并与原作参照比较，返回合成校验记录。</summary>
    internal delegate Task<JsonObject> Validator(BakeGateContext context, CancellationToken cancellationToken);

    public async Task<BakeRejection?> CheckAsync(BakeGateContext context, CancellationToken cancellationToken)
    {
        JsonObject compositionValidation = await validate(context, cancellationToken);
        context.CompositionValidation = compositionValidation;
        if (compositionValidation["status"]?.GetValue<string>() == "composition_pass") return null;
        bool scriptErrorsRejected = compositionValidation["status"]?.GetValue<string>() == CandidateScriptErrorGate.RejectedCompositionStatus;
        var evidence = new JsonObject
        {
            ["reason"] = compositionValidation["reason"]?.DeepClone(),
            ["metrics"] = compositionValidation["metrics"]?.DeepClone(),
            ["composition_validation"] = compositionValidation,
            ["probe_paths"] = new JsonObject
            {
                ["output"] = compositionValidation["probe_output_path"]?.DeepClone(),
                ["capture_source"] = compositionValidation["probe_capture_source_path"]?.DeepClone(),
                ["reference"] = compositionValidation["comparison_reference_path"]?.DeepClone(),
                ["project"] = compositionValidation["probe_project_path"]?.DeepClone(),
                ["comparison"] = compositionValidation["comparison_report_path"]?.DeepClone()
            }
        };
        var trailer = new JsonObject();
        if (scriptErrorsRejected)
        {
            trailer["reason_zh"] = compositionValidation["reason_zh"]?.DeepClone();
            trailer["reason_en"] = compositionValidation["reason_en"]?.DeepClone();
        }
        return context.Reject(scriptErrorsRejected ? CandidateScriptErrorGate.RejectedBakeStatus : "candidate_rejected_composition",
            context.Plan, "not_performed", evidence, trailer);
    }
}
