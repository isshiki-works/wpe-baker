using System.Text.Json.Nodes;

namespace Baker.Core;

internal static class InteractionPolicy
{
    // A conservative sampling threshold, not a prediction of playback power savings.
    internal const double ExpensiveShare = .25;
    internal static bool Protected(JsonObject layer) => layer["kind"]?.GetValue<string>() == "text" ||
        Kinds(layer).Any(k => k is "clock" or "media" or "fps" or "bgm");
    private static IEnumerable<string> Kinds(JsonObject layer) => (layer["tradeoff_kinds"] as JsonArray ?? []).Select(k => k!.GetValue<string>());
    internal static bool ExpensiveAudio(JsonObject layer, double? before, double? after) => !Protected(layer) &&
        Kinds(layer).Contains("audio") && layer["canvas_fraction"] is JsonValue area && area.TryGetValue<double>(out double fraction) &&
        fraction >= .5 && before is > 0 && after is >= 0 && (before.Value - after.Value) / before.Value >= ExpensiveShare;

    internal static async Task<(int[] Excluded, JsonArray Costs)> ExclusionsAsync(JsonObject plan, HybridAnalyzeRequest request,
        NativeTools? tools, string output, CancellationToken token)
    {
        // plan 的图层清单来自已拒绝父链成环的 SceneGraph，子树判断直接用它的按对象表重载。
        var layers = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(l => l["id"]!.GetValue<int>());
        var excluded = new List<int>();
        var costs = new JsonArray();
        JsonObject? runtime = plan["runtime_evidence"]?.GetValue<string>() is string path && File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, token))?.AsObject() : null;
        double? before = runtime?["gpu_timing"]?["supported"]?.GetValue<bool>() == true
            ? runtime["gpu_timing"]?["draw_ms"]?.GetValue<double>() : null;
        foreach (var (id, layer) in layers)
        {
            if (Protected(layer) || layers.Any(pair => SceneGraph.Within(layers, pair.Key, id) && Protected(pair.Value))) continue;
            if (Kinds(layer).Contains("pointer"))
            {
                excluded.Add(id);
                costs.Add(new JsonObject { ["layer_id"] = id, ["kind"] = "pointer", ["decision"] = "omit", ["basis"] = "interaction_off" });
                continue;
            }
            if (!Kinds(layer).Contains("audio") || layer["canvas_fraction"] is not JsonValue area ||
                !area.TryGetValue<double>(out double fraction) || fraction < .5) continue;
            string key = "interaction-" + AnalysisCache.Key(plan["source_sha256"], plan["snapshot_properties"], tools,
                tools is not null && File.Exists(tools.Renderer) ? File.GetLastWriteTimeUtc(tools.Renderer).Ticks : 0,
                request.DeviceUuid, request.Width, request.Height, request.FpsNumerator, request.FpsDenominator, id);
            JsonObject? reading = AnalysisCache.Read(request.AnalysisCacheDirectory, key);
            if (reading is null)
            {
                reading = new JsonObject { ["layer_id"] = id, ["kind"] = "audio", ["status"] = "unavailable", ["before_draw_ms"] = before,
                    ["threshold_share"] = ExpensiveShare, ["basis"] = "512px_gpu_timestamp_difference_not_power" };
                if (before is > 0 && tools is not null)
                {
                    string directory = Path.Combine(output, id.ToString());
                    JsonObject raw = new();
                    try
                    {
                        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
                        uint width = Math.Min(512, settings.Width);
                        uint height = Math.Max(2, (uint)Math.Round((double)settings.Height * width / settings.Width));
                        raw = await new NativeRenderRunner(tools).RenderRawAsync(new(request.Source, request.Assets, directory,
                            width, height, request.FpsNumerator, request.FpsDenominator, 48, Seed: 17,
                            UserProperties: plan["snapshot_properties"]?.AsObject(), DeviceUuid: request.DeviceUuid,
                            InputTimeline: new JsonArray(new JsonObject { ["frame"] = 12, ["cursor_x"] = .25, ["cursor_y"] = .25, ["cursor_in_window"] = true },
                                new JsonObject { ["frame"] = 24, ["cursor_x"] = .75, ["cursor_y"] = .75, ["mouse_buttons_down"] = 1 },
                                new JsonObject { ["frame"] = 36, ["mouse_buttons_down"] = 0 }),
                            LayerSelection: new(layers.Keys.Where(other => !SceneGraph.Within(layers, other, id)).ToArray()), GpuTiming: true), token);
                        JsonNode? timing = raw["native_result"]?["gpu_timing"];
                        if (timing?["supported"]?.GetValue<bool>() == true && timing["draw_ms"] is JsonValue value && value.TryGetValue<double>(out double after))
                        {
                            reading["after_draw_ms"] = after;
                            reading["sampled_share"] = Math.Clamp((before.Value - after) / before.Value, 0, 1);
                            reading["status"] = "sampled";
                        }
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException) { reading["error"] = error.Message; }
                    finally { TemporaryCaptureFiles.Delete(raw["native_result"] as JsonObject ?? new(), directory,
                        "native/frames.rgba", "native/frames.rgba.partial", "native/audio.f32le", "native/audio.f32le.partial"); }
                }
                if (reading["status"]?.GetValue<string>() == "sampled") AnalysisCache.Write(request.AnalysisCacheDirectory, key, reading);
            }
            bool omit = ExpensiveAudio(layer, reading["before_draw_ms"]?.GetValue<double>(), reading["after_draw_ms"]?.GetValue<double>());
            reading["decision"] = omit ? "omit" : "keep";
            costs.Add(reading);
            if (omit) excluded.Add(id);
        }
        return (excluded.ToArray(), costs);
    }
}
