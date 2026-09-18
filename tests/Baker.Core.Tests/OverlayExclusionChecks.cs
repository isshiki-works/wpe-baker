using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.App;
using Baker.Core;

internal static class OverlayExclusionChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "overlay-exclusion-source");
        Directory.CreateDirectory(sourceDirectory);
        var objects = new JsonArray(
            new JsonObject { ["id"] = 10, ["name"] = "背景", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" },
            new JsonObject { ["id"] = 20, ["name"] = "水印", ["image"] = "models/mark.json",
                ["size"] = "8 4", ["origin"] = "56 28 0" },
            new JsonObject { ["id"] = 30, ["name"] = "promo banner", ["image"] = "models/promo.json",
                ["size"] = "16 8", ["origin"] = "32 16 0",
                ["visible"] = new JsonObject { ["value"] = true,
                    ["script"] = "setInterval(function () { thisLayer.visible = true; }, 5000);" } },
            new JsonObject { ["id"] = 40, ["name"] = "捐赠" },
            new JsonObject { ["id"] = 41, ["parent"] = 40, ["name"] = "二维码", ["image"] = "models/qr.json",
                ["size"] = "6 6", ["origin"] = "6 4 0" });
        string scenePath = Path.Combine(sourceDirectory, "scene.json");
        await File.WriteAllTextAsync(scenePath, new JsonObject {
            ["general"] = new JsonObject {
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = true },
            ["objects"] = objects }.ToJsonString());
        string tracePath = Path.Combine(root, "overlay-exclusion-trace.json");
        await File.WriteAllTextAsync(tracePath, new JsonObject {
            ["source"] = scenePath, ["status"] = "complete",
            ["runtime_dependencies"] = new JsonArray(),
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(objects.OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
                ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(), ["visible"] = true,
                ["has_mesh"] = obj["id"]!.GetValue<int>() != 40,
                ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() })
            }).ToArray()) }.ToJsonString());

        async Task<JsonObject> PlanAsync(string name, int[]? excluded) =>
            await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeAsync(
                new(2, sourceDirectory, root, Path.Combine(root, name), 64, 32,
                    RuntimeTraceFile: tracePath, VideoLayout: "layered",
                    ExcludedLayerIds: excluded));
        static JsonObject Layer(JsonObject plan, int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id);
        static string[] Reasons(JsonObject layer) => layer["suspected_overlay_reasons"]!.AsArray()
            .Select(reason => reason!.GetValue<string>()).ToArray();

        JsonObject listed = await PlanAsync("overlay-exclusion-listing", null);
        check(listed["layers"]!.AsArray().Count == 5 &&
            Layer(listed, 10)["kind"]!.GetValue<string>() == "image" && Layer(listed, 10)["has_image"]!.GetValue<bool>() &&
            Layer(listed, 10)["quadrant"]!.GetValue<string>() == "center" &&
            Math.Abs(Layer(listed, 10)["canvas_fraction"]!.GetValue<double>() - 1) < 1e-6 &&
            Layer(listed, 20)["quadrant"]!.GetValue<string>() == "top_right" &&
            Layer(listed, 20)["visible_binding"]!.GetValue<string>() == "constant" &&
            Layer(listed, 30)["visible_binding"]!.GetValue<string>() == "script" &&
            Layer(listed, 41)["parent"]!.GetValue<int>() == 40 &&
            Layer(listed, 41)["quadrant"]!.GetValue<string>() == "bottom_left" &&
            Layer(listed, 10)["allocation"]!.GetValue<string>() == "video" &&
            Layer(listed, 40)["allocation"]!.GetValue<string>() == "inactive" &&
            listed["excluded_layer_ids"]!.AsArray().Count == 0,
            "the plan lists every layer with its kind, canvas share, quadrant, visibility binding and allocation");
        check(Layer(listed, 20)["suspected_overlay"]!.GetValue<bool>() &&
            Reasons(Layer(listed, 20)).Contains("name_matches_overlay_vocabulary") &&
            Reasons(Layer(listed, 20)).Contains("small_image_in_canvas_corner") &&
            Layer(listed, 30)["suspected_overlay"]!.GetValue<bool>() &&
            Reasons(Layer(listed, 30)).SequenceEqual(new[] { "timer_driven_visibility" }) &&
            Layer(listed, 41)["suspected_overlay"]!.GetValue<bool>() &&
            !Layer(listed, 10)["suspected_overlay"]!.GetValue<bool>() &&
            Layer(listed, 20)["allocation"]!.GetValue<string>() == "video" &&
            Layer(listed, 30)["allocation"]!.GetValue<string>() == "video",
            "overlay vocabulary, corner placement and timer-driven visibility are flagged without changing any allocation");

        // 界面提示只描述位置与显隐方式：名字匹配的用途猜测不上界面，中英文都不出现广告/水印/二维码类字样。
        string[] hintTexts = new[] { 10, 20, 30, 41 }.SelectMany(id => new[] {
            AppJsonPresentation.LayerHints(Layer(listed, id), false), AppJsonPresentation.LayerHints(Layer(listed, id), true) }).ToArray();
        check(AppJsonPresentation.LayerHints(Layer(listed, 20), false) == "角落小图层" &&
            AppJsonPresentation.LayerHints(Layer(listed, 20), true) == "small layer in a corner" &&
            AppJsonPresentation.LayerHints(Layer(listed, 30), false) == "定时显示图层" &&
            AppJsonPresentation.LayerHints(Layer(listed, 30), true) == "timer-driven visibility" &&
            AppJsonPresentation.LayerHints(Layer(listed, 10), false) == "" &&
            hintTexts.All(text => !Regex.IsMatch(text, @"广告|水印|二维码|捐赠|\bads?\b|advert|watermark|\bQR\b|donat", RegexOptions.IgnoreCase)),
            "layer-list hints describe placement and visibility only and never name ads, watermarks or QR codes");

        JsonObject excludedPlan = await PlanAsync("overlay-exclusion-applied", [30, 40, 20]);
        var omitted = excludedPlan["omitted_snapshot_layer_ids"]!.AsArray().Select(id => id!.GetValue<int>()).ToArray();
        var groupLayers = excludedPlan["video_groups"]!.AsArray().OfType<JsonObject>()
            .SelectMany(group => group["layer_ids"]!.AsArray().Select(id => id!.GetValue<int>())).ToArray();
        check(excludedPlan["excluded_layer_ids"]!.AsArray().Select(id => id!.GetValue<int>()).SequenceEqual(new[] { 20, 30, 40 }) &&
            new[] { 20, 30, 40, 41 }.All(id => Layer(excludedPlan, id)["allocation"]!.GetValue<string>() == "excluded") &&
            new[] { 20, 30, 40, 41 }.All(omitted.Contains) &&
            Layer(excludedPlan, 10)["allocation"]!.GetValue<string>() == "video" &&
            groupLayers.SequenceEqual(new[] { 10 }) &&
            excludedPlan["live_layer_ids"]!.AsArray().Count == 0 &&
            new[] { 20, 30, 41 }.All(id => !Layer(excludedPlan, id)["visible"]!.GetValue<bool>() &&
                !Layer(excludedPlan, id)["drawable"]!.GetValue<bool>()),
            "explicitly excluded layers and their subtrees leave the video, the live scene and the static snapshot");

        bool unknownRejected = false;
        try { await PlanAsync("overlay-exclusion-unknown", [10, 999]); }
        catch (InvalidDataException error) when (error.Message.Contains("excluded layer id", StringComparison.Ordinal))
        { unknownRejected = true; }
        check(unknownRejected, "an excluded layer id that is not in the scene fails closed instead of being ignored");
    }
}
