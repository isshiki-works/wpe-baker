using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Device-independent work that the selected capture would remove; not a power prediction.</summary>
public static class BakeValueAssessment
{
    public const string Field = "bake_value";
    public static bool IsLowValue(JsonObject plan) => plan[Field]?["status"]?.GetValue<string>() == "low_value";

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
        JsonObject Result(string status, string rule, string zh, string en, JsonObject? evidence = null) => new() {
            ["status"] = status, ["rule"] = rule, ["reason_zh"] = zh, ["reason_en"] = en,
            ["evidence"] = evidence ?? new JsonObject(), ["device_independent"] = true,
            ["scope"] = "Work removed by this plan, independent of current GPU speed. Potential gain is not measured power saving; unknown is not low value." };
        if (plan["blockers"] is JsonArray { Count: > 0 })
            return Result("unknown", "unresolved_plan", "当前方案仍有未解决的问题，不能据此判断原作没有优化价值。",
                "The current plan has unresolved issues; that does not establish a lack of optimization value.");
        if (plan["effect_prefix_caches"] is JsonArray { Count: > 0 } prefixes)
            return Result("potential_gain", "cached_effect_prefix", "方案可以缓存重复执行的特效；实际收益仍需原作与成品对照。",
                "The plan caches repeated effect work; actual savings still need a source/candidate comparison.",
                new JsonObject { ["prefix_count"] = prefixes.Count });
        if (plan["loop"]?["candidates"] is not JsonArray { Count: > 0 })
            return Result("unknown", "no_working_candidate", "当前未找到可用方案，不能据此断言原作负载低或没有优化价值。",
                "No usable candidate was found; that does not establish low load or lack of optimization value.");
        JsonObject? decodeWork = plan["video_dominant"]?["decode_work"] as JsonObject;
        if (decodeWork?["status"]?.GetValue<string>() == "potential_gain")
            return Result("potential_gain", "reduced_video_decode", "源视频的编码像素数或帧率可降低，可能减少解码开销；实际收益仍待对照。",
                "The source video's encoded pixel count or frame rate can fall, potentially reducing decoding work; actual savings still need comparison.",
                decodeWork.DeepClone().AsObject());
        if (decodeWork?["status"]?.GetValue<string>() == "not_reduced" &&
            plan["video_dominant"]?["status"]?.GetValue<string>() is VideoDominance.ShellStatus or VideoDominance.OverrideStatus)
            return Result("low_value", "unchanged_video_playback", "当前方案没有已识别的特效、绘制或视频解码量缩减，通常无需重复烘焙。",
                "The plan removes no identified effect, draw or video decoding work; rebaking is usually unnecessary.");
        int[] owners = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(group => (group["layer_ids"] as JsonArray ?? []).Select(HybridScenePlanner.Int).OfType<int>()).Distinct().ToArray();
        var selected = owners.ToHashSet();
        JsonObject[] observed = (runtime["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => HybridScenePlanner.Int(layer["owner"]) is int id && selected.Contains(id)).ToArray();
        int effects = observed.SelectMany(layer => (layer["materials"] as JsonArray ?? []).OfType<JsonObject>())
            .Count(material => material["role"]?.GetValue<string>() == "effect");
        if (effects > 0)
            return Result("potential_gain", "cached_effect_passes", "方案可以省去逐帧特效计算；画面静止也可能有缓存价值。",
                "The plan can remove per-frame effects; a still result can still have caching value.",
                new JsonObject { ["effect_passes"] = effects });
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
                    if (iw > width || ih > height)
                        return Result("potential_gain", "static_texture_footprint", "原静态纹理大于输出尺寸，可能降低纹理驻留或采样开销；不能仅因画面静止而排除。",
                            "The static texture exceeds the output size; residency or sampling costs may fall, so still imagery is not automatically excluded.", facts);
                    if (HybridScenePlanner.Numeric(layer?["canvas_fraction"], 0) >= 1 &&
                        Math.Abs(HybridScenePlanner.Numeric(layer?["canvas_center_x"], double.NaN) - .5) < 1e-9 &&
                        Math.Abs(HybridScenePlanner.Numeric(layer?["canvas_center_y"], double.NaN) - .5) < 1e-9)
                        return Result("low_value", "one_still_texture_unchanged", "原作已经只绘制一张无特效静态背景，纹理也不大于输出；生成后仍需同一次贴图，其他实时层不会因此省去。",
                            "The source already draws one plain still background no larger than the output; generation retains that draw and does not remove the other live layers.", facts);
                }
            }
        }
        return Result("unknown", "needs_work_comparison", "尚未证明能省去多少计算，不按本机运行轻松或画面静止直接排除。",
            "Removed work has not been established; low load on this GPU or still imagery alone is not an exclusion.");
    }
}
