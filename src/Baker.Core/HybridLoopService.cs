using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 循环分析的门面。分析本体在 <see cref="LoopAnalysis"/>（C2.2c 拆出），这里只把类型化报告渲染成 plan.loop；
/// <see cref="ApplyPatches"/> 是 bake 侧按 plan 里的候选改写捕获场景。
/// </summary>
public static class HybridLoopService
{
    /// <summary>转发器（登记于 runs/C2-plan/forwarders.txt，C3 删）：调用方改为直接用 <see cref="LoopAnalysis.Analyze"/>。</summary>
    public static JsonObject Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory, JsonObject runtime,
        IReadOnlyCollection<int> bakedLayerIds, uint fpsNumerator, uint fpsDenominator, double maximumRetimePercent = 2,
        CommonLoopPreference preference = CommonLoopPreference.Balanced, SwayRetimeOptions? swayRetime = null,
        double? loopLengthMaximumSeconds = null, EmbeddedVideoLoopLimit? loopLengthLimit = null) =>
        LoopAnalysis.Analyze(scene, source, assetsDirectory, runtime, bakedLayerIds, fpsNumerator, fpsDenominator, maximumRetimePercent,
            preference, swayRetime, loopLengthMaximumSeconds, loopLengthLimit).ToJson();

    /// <summary>Applies only the selected report candidate's rate patches to a temporary capture scene.</summary>
    public static void ApplyPatches(JsonObject captureScene, JsonObject loopReport, int candidateIndex = 0)
    {
        JsonObject candidate = loopReport["candidates"]?.AsArray().ElementAtOrDefault(candidateIndex)?.AsObject()
            ?? throw new InvalidDataException("Loop report has no selected candidate.");
        var owners = captureScene["objects"]?.AsArray().OfType<JsonObject>().ToDictionary(x => x["id"]!.GetValue<int>())
            ?? throw new InvalidDataException("Capture scene has no objects.");
        foreach (JsonObject patch in candidate["patches"]!.AsArray().OfType<JsonObject>())
        {
            if (patch["kind"]?.GetValue<string>() == "video_rate") continue;
            int ownerId = patch["owner_layer_id"]!.GetValue<int>();
            if (!owners.TryGetValue(ownerId, out JsonObject? owner)) throw new InvalidDataException($"Patch owner {ownerId} is absent.");
            double oldValue = patch["old_value"]!.GetValue<double>(), newValue = patch["new_value"]!.GetValue<double>();
            if (patch["kind"]!.GetValue<string>() is "shader_speed" or "shader_phase")
            {
                JsonObject pass = owner["effects"]!.AsArray()[patch["effect_index"]!.GetValue<int>()]!.AsObject()["passes"]!.AsArray()[patch["pass_index"]!.GetValue<int>()]!.AsObject();
                string key = patch["constant_key"]!.GetValue<string>(); int index = patch["value_index"]!.GetValue<int>();
                JsonNode? value = pass["constantshadervalues"]?[key];
                if (value is JsonArray array) { Verify(array[index], oldValue); array[index] = newValue; }
                else { Verify(value, oldValue); pass["constantshadervalues"]![key] = newValue; }
            }
            else if (patch["kind"]!.GetValue<string>() == "animation_rate")
            {
                int layerId = patch["animation_layer_id"]!.GetValue<int>();
                JsonObject layer = owner["animationlayers"]!.AsArray().OfType<JsonObject>().Single(x => x["id"]?.GetValue<int>() == layerId);
                Verify(layer["rate"], oldValue); layer["rate"] = newValue;
            }
            else if (patch["kind"]!.GetValue<string>() == "animation_fps")
            {
                // 属性动画轨道：按分析时记下的 JSON 指针找回那条字段动画，改它的 options.fps。
                JsonObject options = Resolve(owner, patch["animation_path"]!.GetValue<string>())?["options"] as JsonObject
                    ?? throw new InvalidDataException("Capture scene changed since its loop patch was analyzed.");
                Verify(options["fps"], oldValue); options["fps"] = newValue;
            }
            else throw new InvalidDataException("Unknown loop patch kind.");
        }
    }

    /// <summary>按 RFC 6901 JSON 指针（相对 <paramref name="root"/>）取节点；路径不存在返回 null。</summary>
    private static JsonNode? Resolve(JsonNode root, string pointer)
    {
        JsonNode? node = root;
        foreach (string token in pointer.Split('/').Skip(1).Select(x => x.Replace("~1", "/").Replace("~0", "~")))
            node = node switch {
                JsonObject item => item[token],
                JsonArray array when int.TryParse(token, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index) && index < array.Count => array[index],
                _ => null };
        return node;
    }

    private static void Verify(JsonNode? node, double expected)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out double actual) || Math.Abs(actual - expected) > Math.Max(1e-10, Math.Abs(expected) * 1e-10))
            throw new InvalidDataException("Capture scene changed since its loop patch was analyzed.");
    }
}
