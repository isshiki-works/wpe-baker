using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// bake 侧按 plan 里的候选改写捕获场景（<see cref="ApplyPatches"/>）。循环分析本体在 <see cref="LoopAnalysis"/>。
/// </summary>
public static class HybridLoopService
{
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
                JsonObject effect = owner["effects"]!.AsArray()[patch["effect_index"]!.GetValue<int>()]!.AsObject();
                if (effect["passes"] is not JsonArray passes) effect["passes"] = passes = [];
                int passIndex = patch["pass_index"]!.GetValue<int>();
                while (passes.Count <= passIndex) passes.Add(new JsonObject());
                JsonObject pass = passes[passIndex]!.AsObject();
                if (pass["constantshadervalues"] is not JsonObject values) pass["constantshadervalues"] = values = [];
                string key = patch["constant_key"]!.GetValue<string>(); int index = patch["value_index"]!.GetValue<int>();
                JsonNode? value = values[key];
                // 场景没写这个键时渲染器取 shader 默认值（分析记作 old_value），插入新值即可。
                if (value is null && !values.ContainsKey(key)) values[key] = newValue;
                else if (value is JsonArray array) { Verify(array[index], oldValue); array[index] = newValue; }
                else { Verify(value, oldValue); values[key] = newValue; }
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
