using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Applies the fixed short-sample gate to a CandidateValidation report.</summary>
public static class HybridCompositionValidator
{
    public const ulong RequiredFrames = 48;
    public const uint RequiredTileSize = 64;
    public const double MaximumGlobalRgbMae255 = 8;
    public const double MaximumTileRgbMae255 = 25;
    public const double MaximumGlobalAlphaMae255 = 1;

    public static JsonObject Evaluate(JsonObject comparison, ulong expectedWarmupFrames = 0)
    {
        var failures = new JsonArray();
        var result = new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = "composition_rejected",
            ["scope"] = "A short offline composition gate over 48 paired frames; it does not certify a full loop or official playback.",
            ["automatic_visual_certification"] = false,
            ["official_playback_verified"] = false,
            ["expected_source_start_frame"] = expectedWarmupFrames,
            ["limits"] = new JsonObject
            {
                ["global_rgb_mae_255"] = MaximumGlobalRgbMae255,
                ["every_64px_tile_rgb_mae_255"] = MaximumTileRgbMae255,
                ["global_alpha_mae_255"] = MaximumGlobalAlphaMae255
            },
            ["failures"] = failures
        };

        if (comparison["report_path"] is JsonValue reportPath && TryString(reportPath, out string? path))
            result["comparison_report_path"] = path;
        // 候选脚本报错多于原作：直接拒绝，不再判定后面的像素指标；指标原样附上只作记录。
        JsonObject scriptErrors = CandidateScriptErrorGate.Evaluate(comparison);
        result["script_error_validation"] = scriptErrors;
        if (scriptErrors["status"]?.GetValue<string>() == CandidateScriptErrorGate.RejectedValidationStatus)
        {
            failures.Add(scriptErrors["reason"]!.GetValue<string>());
            result["status"] = CandidateScriptErrorGate.RejectedCompositionStatus;
            result["reason"] = scriptErrors["reason"]!.DeepClone();
            result["reason_zh"] = scriptErrors["reason_zh"]!.DeepClone();
            result["reason_en"] = scriptErrors["reason_en"]!.DeepClone();
            result["frames_compared"] = comparison["frames_compared"]?.DeepClone();
            result["metrics"] = comparison["metrics"]?.DeepClone();
            result["metrics_judged"] = false;
            return result;
        }
        if (!TryInt(comparison["schema_version"], out int schemaVersion) || schemaVersion != 1)
            failures.Add("Comparison schema_version must be 1.");
        if (!TryString(comparison["status"], out string? status) || status != "compared")
            failures.Add("Comparison status must be compared.");
        if (comparison["lookup_binding_validation"] is JsonObject lookups)
        {
            result["lookup_binding_validation"] = lookups.DeepClone();
            if (lookups["status"]?.GetValue<string>() != "observed_lookups_match")
                failures.Add("Retained script lookup bindings differ or their paired trace is unavailable.");
        }

        JsonObject? request = comparison["request"] as JsonObject;
        uint width = 0, height = 0;
        ulong requestedFrames = 0;
        bool requestComplete = request is not null &&
            TryUInt(request["width"], out width) && width is > 0 and <= ushort.MaxValue &&
            TryUInt(request["height"], out height) && height is > 0 and <= ushort.MaxValue &&
            TryUInt(request["fps_numerator"], out uint fpsNumerator) && fpsNumerator > 0 &&
            TryUInt(request["fps_denominator"], out uint fpsDenominator) && fpsDenominator > 0 &&
            TryULong(request["frames"], out requestedFrames) && requestedFrames == RequiredFrames &&
            TryULong(request["warmup_frames"], out ulong warmupFrames) && warmupFrames == expectedWarmupFrames &&
            TryULong(request["seed"], out ulong seed) && seed == 17 &&
            TryUInt(request["tile_size"], out uint tileSize) && tileSize == RequiredTileSize;
        if (!requestComplete)
        {
            failures.Add($"Comparison request must contain valid dimensions and FPS, 48 frames, warmup {expectedWarmupFrames}, seed 17, and 64px tiles.");
            return Finish(result, comparison, failures, null, 0, null);
        }

        if (!TryULong(comparison["frames_compared"], out ulong framesCompared) || framesCompared != requestedFrames)
            failures.Add("frames_compared is missing or does not match the 48 requested frames.");

        JsonObject? metrics = comparison["metrics"] as JsonObject;
        ulong globalPixels = 0;
        double globalRgb = double.NaN, globalAlpha = double.NaN;
        bool metricsComplete = metrics is not null &&
            TryULong(metrics["pixels"], out globalPixels) &&
            TryMetric(metrics["rgb_mae_255"], out globalRgb) &&
            TryMetric(metrics["alpha_mae_255"], out globalAlpha);
        ulong expectedGlobalPixels;
        try { expectedGlobalPixels = checked((ulong)width * height * requestedFrames); }
        catch (OverflowException)
        {
            failures.Add("Comparison dimensions overflow the supported pixel count.");
            return Finish(result, comparison, failures, metrics, 0, null);
        }
        if (!metricsComplete || globalPixels != expectedGlobalPixels)
        {
            failures.Add("Global RGB/alpha metrics or their complete pixel count are missing or malformed.");
            return Finish(result, comparison, failures, metrics, 0, null);
        }
        if (globalRgb > MaximumGlobalRgbMae255)
            failures.Add($"Global RGB MAE {globalRgb:G6} exceeds {MaximumGlobalRgbMae255:G6}.");
        if (globalAlpha > MaximumGlobalAlphaMae255)
            failures.Add($"Global alpha MAE {globalAlpha:G6} exceeds {MaximumGlobalAlphaMae255:G6}.");

        JsonArray? tiles = comparison["tiles"] as JsonArray;
        int columns = checked((int)(((ulong)width + RequiredTileSize - 1) / RequiredTileSize));
        int rows = checked((int)(((ulong)height + RequiredTileSize - 1) / RequiredTileSize));
        int expectedTiles = checked(columns * rows);
        int validTiles = 0;
        JsonObject? worstTile = null;
        double worstTileRgb = double.NegativeInfinity;
        var seen = new HashSet<(uint X, uint Y)>();
        if (tiles is null || tiles.Count != expectedTiles)
        {
            failures.Add($"Tile metrics are incomplete: expected {expectedTiles}, found {tiles?.Count ?? 0}.");
        }
        else
        {
            foreach (JsonNode? node in tiles)
            {
                if (node is not JsonObject tile || !TryUInt(tile["x"], out uint x) || !TryUInt(tile["y"], out uint y) ||
                    !TryUInt(tile["width"], out uint tileWidth) || !TryUInt(tile["height"], out uint tileHeight) ||
                    tile["metrics"] is not JsonObject tileMetrics || !TryULong(tileMetrics["pixels"], out ulong pixels) ||
                    !TryMetric(tileMetrics["rgb_mae_255"], out double rgb) || !TryMetric(tileMetrics["alpha_mae_255"], out _))
                {
                    failures.Add("At least one tile is missing its geometry, pixel count, or RGB/alpha metrics.");
                    continue;
                }
                uint expectedWidth = x < width ? Math.Min(RequiredTileSize, width - x) : 0;
                uint expectedHeight = y < height ? Math.Min(RequiredTileSize, height - y) : 0;
                bool geometryValid = x % RequiredTileSize == 0 && y % RequiredTileSize == 0 &&
                    expectedWidth > 0 && expectedHeight > 0 && tileWidth == expectedWidth && tileHeight == expectedHeight &&
                    pixels == checked((ulong)tileWidth * tileHeight * requestedFrames) && seen.Add((x, y));
                if (!geometryValid)
                {
                    failures.Add("At least one tile has missing, duplicate, or inconsistent 64px coverage.");
                    continue;
                }
                ++validTiles;
                if (rgb > worstTileRgb)
                {
                    worstTileRgb = rgb;
                    worstTile = tile.DeepClone().AsObject();
                }
                if (rgb > MaximumTileRgbMae255)
                    failures.Add($"Tile ({x},{y}) RGB MAE {rgb:G6} exceeds {MaximumTileRgbMae255:G6}.");
            }
            if (validTiles != expectedTiles)
                failures.Add($"Only {validTiles} of {expectedTiles} tile metrics had complete 64px coverage.");
        }

        return Finish(result, comparison, failures, metrics, validTiles, worstTile);
    }

    private static JsonObject Finish(JsonObject result, JsonObject comparison, JsonArray failures,
        JsonObject? metrics, int tilesEvaluated, JsonObject? worstTile)
    {
        result["frames_compared"] = comparison["frames_compared"]?.DeepClone();
        result["metrics"] = metrics?.DeepClone();
        result["tiles_evaluated"] = tilesEvaluated;
        result["worst_rgb_tile"] = worstTile;
        bool passed = failures.Count == 0;
        result["status"] = passed ? "composition_pass" : "composition_rejected";
        new Message(passed ? "reason.composition_pass" : "reason.composition_rejected").Write(result, "reason");
        return result;
    }

    private static bool TryMetric(JsonNode? node, out double value)
    {
        if (node is JsonValue json)
        {
            if (json.TryGetValue<double>(out value)) return double.IsFinite(value) && value >= 0;
            if (json.TryGetValue<float>(out float single)) { value = single; return float.IsFinite(single) && single >= 0; }
            if (json.TryGetValue<decimal>(out decimal decimalValue)) { value = (double)decimalValue; return decimalValue >= 0; }
            if (json.TryGetValue<ulong>(out ulong unsigned)) { value = unsigned; return true; }
            if (json.TryGetValue<long>(out long signed)) { value = signed; return signed >= 0; }
        }
        value = double.NaN;
        return false;
    }

    private static bool TryULong(JsonNode? node, out ulong value)
    {
        if (node is JsonValue json)
        {
            if (json.TryGetValue<ulong>(out value)) return true;
            if (json.TryGetValue<uint>(out uint unsigned)) { value = unsigned; return true; }
            if (json.TryGetValue<long>(out long signed) && signed >= 0) { value = (ulong)signed; return true; }
            if (json.TryGetValue<int>(out int integer) && integer >= 0) { value = (ulong)integer; return true; }
        }
        value = 0;
        return false;
    }

    private static bool TryUInt(JsonNode? node, out uint value)
    {
        if (TryULong(node, out ulong wide) && wide <= uint.MaxValue) { value = (uint)wide; return true; }
        value = 0; return false;
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        if (node is JsonValue json)
        {
            if (json.TryGetValue<int>(out value)) return true;
            if (json.TryGetValue<long>(out long signed) && signed is >= int.MinValue and <= int.MaxValue) { value = (int)signed; return true; }
            if (json.TryGetValue<uint>(out uint unsigned) && unsigned <= int.MaxValue) { value = (int)unsigned; return true; }
        }
        value = 0;
        return false;
    }

    private static bool TryString(JsonNode? node, out string? value)
    {
        try { value = node?.GetValue<string>(); return value is not null; }
        catch (InvalidOperationException) { value = null; return false; }
        catch (FormatException) { value = null; return false; }
    }
}
