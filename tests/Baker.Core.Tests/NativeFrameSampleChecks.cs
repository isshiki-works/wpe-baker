using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class NativeFrameSampleChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        static async Task<JsonObject> CopyAsync(byte[] bytes, Stream destination, RenderRequest request, string output)
        {
            Directory.CreateDirectory(output);
            MethodInfo method = typeof(NativeRenderRunner).GetMethod("CopyFrameStreamAsync",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            using var source = new MemoryStream(bytes, writable: false);
            var task = (Task)method.Invoke(null, [source, destination, request, output, null, CancellationToken.None, false])!;
            await task;
            object summary = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return (JsonObject)summary.GetType().GetProperty("Samples")!.GetValue(summary)!;
        }

        static async Task<JsonObject?> CopyOpaqueAsync(byte[] bytes, RenderRequest request, string output)
        {
            Directory.CreateDirectory(output);
            MethodInfo method = typeof(NativeRenderRunner).GetMethod("CopyFrameStreamAsync",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            using var source = new MemoryStream(bytes, writable: false);
            var task = (Task)method.Invoke(null, [source, Stream.Null, request, output, null, CancellationToken.None, false])!;
            await task;
            object summary = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return (JsonObject?)summary.GetType().GetProperty("OpaquePixels")!.GetValue(summary);
        }

        static byte[] SolidFrame(byte r, byte g, byte b) =>
            Enumerable.Repeat(new byte[] { r, g, b, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        static byte[] SolidFrameSized(int width, int height, byte r, byte g, byte b) =>
            Enumerable.Repeat(new byte[] { r, g, b, 255 }, width * height).SelectMany(pixel => pixel).ToArray();

        byte[] first =
        [
            10, 20, 30, 255, 20, 30, 40, 255,
            30, 40, 50, 255, 40, 50, 60, 255
        ];
        byte[] frames = [.. first, .. SolidFrame(1, 2, 3), .. SolidFrame(100, 110, 120)];
        string exactOutput = Path.Combine(root, "frame-samples-only-exact");
        var request = new RenderRequest(root, root, exactOutput, 2, 2, 60, 1, 3,
            FrameSampleStride: 2, FrameSampleWidth: 1, FrameSamplesOnly: true);
        var runner = new NativeRenderRunner(new(Path.Combine(root, "missing-renderer"),
            Path.Combine(root, "missing-ffmpeg"), Path.Combine(root, "missing-ffprobe"), []));
        foreach ((RenderRequest invalid, string name) in new[]
        {
            (request with { FrameSampleStride = 0 }, "frame-samples-only rejects a zero sample stride"),
            (request with { FrameSampleWidth = 0 }, "frame-samples-only rejects a zero sample width")
        })
        {
            try { await runner.RenderAsync(invalid); }
            catch (ArgumentException) { check(true, name); continue; }
            throw new InvalidOperationException("Accepted invalid samples-only request: " + name);
        }
        using var forwarded = new MemoryStream();
        JsonObject samples = await CopyAsync(frames, forwarded, request, exactOutput);
        check(forwarded.ToArray().SequenceEqual(frames),
            "frame sampling preserves the complete renderer byte stream for either encoder or null sinks");
        check(File.ReadAllBytes(Path.Combine(exactOutput, "frame-samples.rgb"))
                .SequenceEqual(new byte[] { 25, 35, 45, 100, 110, 120 }) &&
            samples["width"]!.GetValue<int>() == 1 && samples["height"]!.GetValue<int>() == 1 &&
            samples["count"]!.GetValue<ulong>() == 2 && samples["stride_frames"]!.GetValue<uint>() == 2,
            "frame-samples-only retains the exact sparse area-averaged RGB bytes and manifest shape");

        async Task RejectAsync(byte[] input, string name, string directory)
        {
            try
            {
                await CopyAsync(input, Stream.Null, request with { OutputDirectory = directory }, directory);
            }
            catch (InvalidDataException) { check(true, name); return; }
            throw new InvalidOperationException("Accepted invalid frame stream: " + name);
        }
        await RejectAsync(frames[..^1], "frame sampling rejects a truncated final RGBA frame",
            Path.Combine(root, "frame-samples-only-truncated"));
        await RejectAsync([.. frames, 1], "frame sampling rejects bytes after the requested frame count",
            Path.Combine(root, "frame-samples-only-extra"));

        string opaqueOutput = Path.Combine(root, "opaque-full-resolution");
        var opaqueRequest = new RenderRequest(root, root, opaqueOutput, 4, 2, 60, 1, 2,
            RequireOpaquePixels: true, EncodeWidth: 2, EncodeHeight: 2);
        byte[] opaqueFrames = [.. SolidFrameSized(4, 2, 1, 2, 3), .. SolidFrameSized(4, 2, 4, 5, 6)];
        JsonObject opaque = await CopyOpaqueAsync(opaqueFrames, opaqueRequest, opaqueOutput)
            ?? throw new InvalidOperationException("Opaque validation did not return success evidence.");
        check(opaque["requested"]?.GetValue<bool>() == true && opaque["verified"]?.GetValue<bool>() == true &&
            opaque["checked_frames"]?.GetValue<ulong>() == 2 && opaque["checked_pixels"]?.GetValue<ulong>() == 16 &&
            opaque["minimum_alpha"]?.GetValue<int>() == 255,
            "opaque validation scans every full-resolution frame before the requested encode downscale");

        byte[] nonOpaqueFrames = opaqueFrames.ToArray();
        int secondFramePixel = 4 * 2 * 4 + (1 * 4 + 2) * 4;
        nonOpaqueFrames[secondFramePixel + 3] = 254;
        try
        {
            await CopyOpaqueAsync(nonOpaqueFrames, opaqueRequest with { OutputDirectory = Path.Combine(root, "opaque-reject") },
                Path.Combine(root, "opaque-reject"));
        }
        catch (IOException error) when (error.Message.Contains("frame 1, (2, 1), alpha=254", StringComparison.Ordinal))
        {
            check(true, "opaque validation reports the second-frame full-resolution pixel coordinate and alpha");
            return;
        }
        throw new InvalidOperationException("Opaque validation accepted alpha 254 or did not report its source coordinate.");
    }
}
