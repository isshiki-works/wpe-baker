using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// Device-independent work that the selected capture would remove; not a power prediction.
/// 规则表（状态、代号、中英理由）与数值比较在 Domain 的 <see cref="WorkloadValue"/>；这里按顺序读 plan、运行时观察与纹理头。
/// </summary>
public static class BakeValueAssessment
{
    public const string Field = "bake_value";
    public static bool IsLowValue(JsonObject plan) => plan[Field]?["status"]?.GetValue<string>() == WorkloadValue.LowValueStatus;

    // Plans mix parsed JSON numbers with CLR-backed uint output extents and int settings.
    internal static double Number(JsonNode? node, double fallback = double.NaN)
    {
        if (node is not JsonValue value) return fallback;
        if (value.TryGetValue<double>(out double number)) return number;
        if (value.TryGetValue<uint>(out uint unsigned)) return unsigned;
        if (value.TryGetValue<int>(out int integer)) return integer;
        if (value.TryGetValue<long>(out long wide)) return wide;
        if (value.TryGetValue<ulong>(out ulong unsignedWide)) return unsignedWide;
        return fallback;
    }

    public static JsonObject Evaluate(JsonObject plan, JsonObject runtime, ProjectSource source, string? assets)
    {
        JsonObject Result(WorkloadValue.Verdict verdict, JsonObject? evidence = null) => new() {
            ["status"] = verdict.Status, ["rule"] = verdict.Rule, ["reason_zh"] = verdict.ReasonZh, ["reason_en"] = verdict.ReasonEn,
            ["evidence"] = evidence ?? new JsonObject(), ["device_independent"] = true,
            ["scope"] = WorkloadValue.Scope };
        if (plan["blockers"] is JsonArray { Count: > 0 })
            return Result(WorkloadValue.UnresolvedPlan);
        if (plan["effect_prefix_caches"] is JsonArray { Count: > 0 } prefixes)
            return Result(WorkloadValue.CachedEffectPrefix, new JsonObject { ["prefix_count"] = prefixes.Count });
        if (plan["loop"]?["candidates"] is not JsonArray { Count: > 0 })
            return Result(WorkloadValue.NoWorkingCandidate);
        JsonObject? decodeWork = plan["video_dominant"]?["decode_work"] as JsonObject;
        if (decodeWork?["status"]?.GetValue<string>() == WorkloadValue.DecodePotentialGain)
            return Result(WorkloadValue.ReducedVideoDecode, decodeWork.DeepClone().AsObject());
        if (decodeWork?["status"]?.GetValue<string>() == WorkloadValue.DecodeNotReduced &&
            plan["video_dominant"]?["status"]?.GetValue<string>() is VideoDominance.ShellStatus or VideoDominance.OverrideStatus)
            return Result(WorkloadValue.UnchangedVideoPlayback);
        int[] owners = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(group => (group["layer_ids"] as JsonArray ?? []).Select(HybridScenePlanner.Int).OfType<int>()).Distinct().ToArray();
        var selected = owners.ToHashSet();
        JsonObject[] observed = (runtime["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => HybridScenePlanner.Int(layer["owner"]) is int id && selected.Contains(id)).ToArray();
        int effects = observed.SelectMany(layer => (layer["materials"] as JsonArray ?? []).OfType<JsonObject>())
            .Count(material => material["role"]?.GetValue<string>() == "effect");
        if (effects > 0)
            return Result(WorkloadValue.CachedEffectPasses, new JsonObject { ["effect_passes"] = effects });
        if (plan["loop"]?["source_static"]?.GetValue<bool>() == true && owners.Length == 1 && observed.Length == 1 &&
            plan["video_groups"] is JsonArray { Count: 1 } && observed[0]["has_effect_layer"]?.GetValue<bool>() == false &&
            observed[0]["materials"] is JsonArray { Count: 1 } materials && materials[0] is JsonObject baseMaterial &&
            baseMaterial["role"]?.GetValue<string>() == "source" && baseMaterial["shader"]?.GetValue<string>() is "genericimage3" or "genericimage4" &&
            baseMaterial["textures"] is JsonArray textures)
        {
            string[] names = textures.Select(n => n?.GetValue<string>()).OfType<string>().Where(n => n.Length > 0).ToArray();
            if (names.Length == 1 && !names[0].StartsWith("_rt_", StringComparison.Ordinal))
            {
                string resource = names[0].EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ? names[0] : "materials/" + names[0] + ".tex";
                double width = Number(plan["output_resolution"]?["width"] ?? plan["settings"]?["width"]);
                double height = Number(plan["output_resolution"]?["height"] ?? plan["settings"]?["height"]);
                var layer = (plan["layers"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(x => HybridScenePlanner.Int(x["id"]) == owners[0]);
                if (double.IsFinite(width * height) && width > 0 && height > 0 && TextureContainer.TryReadHeader(source, assets, resource, out var header, out _) &&
                    (header.Flags & 0x24) == 0 && TextureContainer.TryReadImageExtent(source, assets, resource, out uint iw, out uint ih, out _))
                {
                    var facts = new JsonObject { ["source_width"] = iw, ["source_height"] = ih,
                        ["output_width"] = width, ["output_height"] = height, ["source_format"] = header.Format };
                    if (WorkloadValue.TextureExceedsOutput(iw, ih, width, height))
                        return Result(WorkloadValue.StaticTextureFootprint, facts);
                    if (WorkloadValue.FillsCentredCanvas(HybridScenePlanner.Numeric(layer?["canvas_fraction"], 0),
                        HybridScenePlanner.Numeric(layer?["canvas_center_x"], double.NaN),
                        HybridScenePlanner.Numeric(layer?["canvas_center_y"], double.NaN)))
                        return Result(WorkloadValue.OneStillTextureUnchanged, facts);
                }
            }
        }
        return Result(WorkloadValue.NeedsWorkComparison);
    }
}
