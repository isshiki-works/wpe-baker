using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 特效前缀按不透明视频规划、全分辨率捕获却读到 alpha&lt;255 时的离线检查：
/// 逐帧扫描给出的整帧证据、异常链里取回证据，以及拒绝记录与中英理由。不需要渲染器也不需要 GPU。
/// </summary>
internal static class OpaqueCaptureRejectionChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        static byte[] OpaqueFrame(int width, int height) =>
            Enumerable.Repeat(new byte[] { 9, 8, 7, 255 }, width * height).SelectMany(pixel => pixel).ToArray();

        static async Task<Exception> ScanFailureAsync(byte[] frames, RenderRequest request)
        {
            Directory.CreateDirectory(request.OutputDirectory);
            MethodInfo method = typeof(NativeRenderRunner).GetMethod("CopyFrameStreamAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            using var source = new MemoryStream(frames, writable: false);
            try
            {
                await (Task)method.Invoke(null, [source, Stream.Null, request, request.OutputDirectory, null, CancellationToken.None])!;
            }
            catch (Exception error) { return error; }
            throw new InvalidOperationException("A non-opaque frame stream was accepted.");
        }

        MethodInfo evidenceOf = typeof(NativeRenderRunner).GetMethod("OpaquePixelEvidence", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonObject? Evidence(Exception error) => (JsonObject?)evidenceOf.Invoke(null, [error]);

        // 5×4、两帧：第 0 帧全不透明；第 1 帧顶行 alpha 246，另有 (2, 2) alpha 240（离最近边缘 1 像素）。
        const int width = 5, height = 4;
        byte[] frames = [.. OpaqueFrame(width, height), .. OpaqueFrame(width, height)];
        int second = width * height * 4;
        for (int x = 0; x < width; ++x) frames[second + x * 4 + 3] = 246;
        frames[second + (2 * width + 2) * 4 + 3] = 240;
        var request = new RenderRequest(root, root, Path.Combine(root, "opaque-capture-rejection"), width, height, 60, 1, 2,
            RequireOpaquePixels: true);
        Exception failure = await ScanFailureAsync(frames, request);
        check(failure is IOException && failure.Message.Contains("frame 1, (0, 0), alpha=246", StringComparison.Ordinal),
            "non-opaque capture keeps the original first-pixel exception message");
        JsonObject evidence = Evidence(failure) ?? throw new InvalidOperationException("No opaque evidence was recovered.");
        check(evidence["verified"]?.GetValue<bool>() == false && evidence["checked_frames"]?.GetValue<ulong>() == 2 &&
            evidence["first_nonopaque_frame"]?.GetValue<ulong>() == 1 && evidence["x"]?.GetValue<int>() == 0 &&
            evidence["y"]?.GetValue<int>() == 0 && evidence["alpha"]?.GetValue<byte>() == 246,
            "non-opaque evidence names the first frame, coordinate and alpha");
        check(evidence["nonopaque_pixels_in_frame"]?.GetValue<ulong>() == 6 && evidence["minimum_alpha_in_frame"]?.GetValue<int>() == 240 &&
            evidence["maximum_edge_distance_in_frame"]?.GetValue<int>() == 1 &&
            evidence["frame_width"]?.GetValue<int>() == width && evidence["frame_height"]?.GetValue<int>() == height,
            "non-opaque evidence finishes scanning that frame for count, lowest alpha and farthest edge distance");

        byte[] edgeOnly = OpaqueFrame(width, height);
        for (int x = 0; x < width; ++x) edgeOnly[((height - 1) * width + x) * 4 + 3] = 250;
        JsonObject edge = Evidence(await ScanFailureAsync(edgeOnly, request with {
            OutputDirectory = Path.Combine(root, "opaque-capture-edge"), Frames = 1 })) ?? new JsonObject();
        check(edge["x"]?.GetValue<int>() == 0 && edge["y"]?.GetValue<int>() == height - 1 &&
            edge["nonopaque_pixels_in_frame"]?.GetValue<ulong>() == 5 && edge["minimum_alpha_in_frame"]?.GetValue<int>() == 250 &&
            edge["maximum_edge_distance_in_frame"]?.GetValue<int>() == 0,
            "semi-transparent pixels confined to one edge row report edge distance zero");

        check(Evidence(new IOException("renderer stopped")) is null &&
            Evidence(new IOException("wrapped", failure)) is JsonObject wrapped && JsonNode.DeepEquals(wrapped, evidence) &&
            !ReferenceEquals(Evidence(failure), Evidence(failure)),
            "opaque evidence is found through wrapping exceptions, absent otherwise, and returned as a copy");

        // 拒绝记录：纯函数，点名层、坐标与 alpha；中英理由走 Messages。
        Type service = typeof(NativeRenderRunner).Assembly.GetType("Baker.Core.EffectPrefixBakeService", throwOnError: true)!;
        MethodInfo rejection = service.GetMethod("OpaqueCaptureRejection", BindingFlags.Static | BindingFlags.NonPublic)!;
        (JsonObject Group, string Reason) Reject(JsonObject scene, int owner) =>
            ((JsonObject, string))rejection.Invoke(null, [scene, owner, 356, 188UL, 5160U, 2160U,
                new JsonObject { ["candidates"] = new JsonArray() }, evidence])!;
        var sceneJson = new JsonObject { ["objects"] = new JsonArray(
            new JsonObject { ["id"] = 7, ["name"] = "前景" },
            new JsonObject { ["id"] = 23, ["name"] = "背景\n底图" }) };
        (JsonObject group, string reason) = Reject(sceneJson, 23);
        check(group["status"]?.GetValue<string>() == "rejected_opaque_capture" && group["id"]?.GetValue<string>() == "effect-prefix-23" &&
            group["owner_layer_id"]?.GetValue<int>() == 23 && group["terminal_effect_id"]?.GetValue<int>() == 356 &&
            group["packed_alpha"]?.GetValue<bool>() == false && group["probe_opacity"]?["minimum_alpha"]?.GetValue<int>() == 255 &&
            JsonNode.DeepEquals(group["opaque_pixels"], evidence) && !ReferenceEquals(group["opaque_pixels"], evidence) &&
            group["video_path"] is null,
            "opaque capture rejection records the probe verdict and the full-resolution evidence without a video path");
        check(reason.Contains("Layer 23 (\"背景 底图\")", StringComparison.Ordinal) &&
            reason.Contains("alpha=246 at (0, 0) in frame 1", StringComparison.Ordinal) &&
            reason.Contains("6 pixel(s)", StringComparison.Ordinal) && reason.Contains("lowest alpha 240", StringComparison.Ordinal),
            "opaque capture rejection reason names the layer, coordinate, alpha and affected pixel count");
        JsonObject localized = Messages.Localize(reason);
        string zh = localized["zh"]?.GetValue<string>() ?? "";
        check(localized["key"]?.GetValue<string>() == "bake.effect_prefix_nonopaque_capture" &&
            localized["en"]!.GetValue<string>().Contains("alpha=246 at (0, 0) in frame 1", StringComparison.Ordinal) &&
            localized["en"]!.GetValue<string>().Contains("lowest alpha 240", StringComparison.Ordinal) &&
            zh.Contains("图层 23「背景 底图」", StringComparison.Ordinal) && zh.Contains("alpha=246", StringComparison.Ordinal) &&
            zh.Contains("该帧有 6 个像素", StringComparison.Ordinal) && zh.Contains("最低 alpha 240", StringComparison.Ordinal) &&
            !zh.Contains("opaque video", StringComparison.Ordinal),
            "opaque capture rejection reason localizes to Chinese with the same layer and alpha");
        check(Reject(sceneJson, 99).Reason.Contains("Layer 99 (\"\")", StringComparison.Ordinal),
            "opaque capture rejection tolerates an owner missing from the scene");
    }
}
