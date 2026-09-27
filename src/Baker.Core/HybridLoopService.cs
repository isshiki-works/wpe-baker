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
            // 粒子场补丁改写粒子定义资源，由 WriteParticleFieldsAsync 在写捕获场景前处理
            if (patch["kind"]?.GetValue<string>() is "video_rate" or LoopAnalysis.ParticleFieldPatchKind) continue;
            int ownerId = patch["owner_layer_id"]!.GetValue<int>();
            if (!owners.TryGetValue(ownerId, out JsonObject? owner)) throw new InvalidDataException($"Patch owner {ownerId} is absent.");
            double oldValue = patch["old_value"]!.GetValue<double>(), newValue = patch["new_value"]!.GetValue<double>();
            if (patch["kind"]!.GetValue<string>() == "shader_speed")
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
            else if (patch["kind"]!.GetValue<string>() == "script_speed")
            {
                // 脚本调速：这段脚本读到的 engine.runtime 乘倍率（见 ScriptTime.Retime），constant_key 是绑定相对图层的 JSON 指针
                JsonObject binding = Resolve(owner, patch["constant_key"]!.GetValue<string>()) as JsonObject
                    ?? throw new InvalidDataException("Capture scene changed since its loop patch was analyzed.");
                binding["script"] = ScriptTime.Retime(binding["script"]!.GetValue<string>(), newValue);
            }
            else throw new InvalidDataException("Unknown loop patch kind.");
        }
    }

    /// <summary>
    /// 冻结 turbulence 共享场（<see cref="LoopAnalysis.ParticleFieldPatchKind"/>）：把这层的粒子定义另存一份（原路径加 .periodica-层号），
    /// 改写 operator[value_index].timescale，捕获场景里这层改指向新文件；共用同一定义的别的层不受影响。只写进捕获副本。
    /// </summary>
    internal static async Task WriteParticleFieldsAsync(string captureProject, ProjectSource source, string? assetsDirectory,
        JsonObject captureScene, JsonObject loopReport, CancellationToken cancellationToken)
    {
        JsonObject[] patches = [.. ((loopReport["candidates"] as JsonArray)?.FirstOrDefault()?["patches"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(patch => patch["kind"]?.GetValue<string>() == LoopAnalysis.ParticleFieldPatchKind)];
        foreach (var owner in patches.GroupBy(patch => patch["owner_layer_id"]!.GetValue<int>()))
        {
            JsonObject layer = captureScene["objects"]!.AsArray().OfType<JsonObject>().SingleOrDefault(x => x["id"]?.GetValue<int>() == owner.Key)
                ?? throw new InvalidDataException($"Patch owner {owner.Key} is absent.");
            string resource = layer["particle"]!.GetValue<string>();
            JsonObject definition = SceneAnalyzer.ReadResourceJson(source, assetsDirectory, resource);
            // 下标与分析（ParticleStationarity.Entries）同一口径：只数对象项
            JsonObject[] operators = [.. (definition["operator"] as JsonArray ?? []).OfType<JsonObject>()];
            foreach (JsonObject patch in owner)
            {
                JsonObject item = operators[patch["value_index"]!.GetValue<int>()];
                // 没写这个键时分析按渲染器缺省值（20）记旧值，直接插入；写成数字的核对旧值
                if (item["timescale"] is JsonValue value && value.TryGetValue(out double _)) Verify(value, patch["old_value"]!.GetValue<double>());
                item["timescale"] = patch["new_value"]!.GetValue<double>();
            }
            string patched = resource + ".periodica-" + owner.Key + ".json";
            string path = ProjectSource.ContainedPath(captureProject, patched);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, definition.ToJsonString(), cancellationToken);
            layer["particle"] = patched;
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
