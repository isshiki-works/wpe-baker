using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class HybridVideoWorkloadChecks
{
    internal static JsonObject Workload(params string[] streams)
    {
        var native = JsonNode.Parse("{\"runtime_video_decoders\":[" + string.Join(',', streams) +
            "],\"runtime_video_decoder_observation\":{\"status\":\"observed\",\"opened_instances\":" + streams.Length + "}}")!.AsObject();
        return Summary(native);
    }
    internal static string Stream(int id, string key, int width = 3840, string codec = "h264", int numerator = 60000, int denominator = 1001) =>
        $$"""{"instance_id":{{id}},"resource_key":"{{key}}","codec":"{{codec}}","coded_width":{{width}},"coded_height":2160,"pixel_format":"yuv420p","fps_num":{{numerator}},"fps_den":{{denominator}},"metadata_unknown":false} """;
    private static Type WorkloadType => typeof(HybridScenePlanner).Assembly.GetType("Baker.Core.HybridVideoWorkload")!;
    private static JsonObject Summary(JsonObject native) => (JsonObject)WorkloadType.GetMethod("Summarize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [native])!;

    internal static void Run(Action<bool, string> check)
    {
        bool Within(JsonObject candidate, JsonObject reference) => (bool)WorkloadType.GetMethod("NoGreaterThan", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [candidate, reference])!;
        var original = Workload(Stream(1, "original.tex"));
        var retainedAndNew = Workload(Stream(1, "original.tex"), Stream(2, "baked.tex", 1920));
        check(!Within(retainedAndNew, original) && retainedAndNew["instance_count"]!.GetValue<int>() == 2,
            "complete decoder workload counts retained 4K media plus the new baked stream");
        check(Math.Abs(original["coded_pixels_per_second"]!.GetValue<double>() - 3840d * 2160 * 60000 / 1001) < .001,
            "decoder workload uses encoded extent and the stream's exact rational rate");
        check(Within(Workload(Stream(2, "baked.tex", 1920), Stream(1, "original.tex")), retainedAndNew),
            "decoder budget comparison is independent of stream enumeration order");
        check(Within(original, retainedAndNew) && Within(Workload(Stream(1, "smaller.tex", 1920)), original),
            "removing a stream or shrinking the same format can improve the decoder budget");
        check(!Within(Workload(Stream(1, "hevc.tex", 1920, "hevc")), original),
            "different codecs do not become equivalent through pixel counts alone");
        check(Workload(Stream(1, "bad-fps.tex", denominator: 0))["status"]!.GetValue<string>() == "incomplete" &&
            Summary(new JsonObject())["status"]!.GetValue<string>() == "incomplete",
            "unknown rate or older renderer evidence cannot masquerade as zero decode cost");
        var malformedMetadata = JsonNode.Parse("{\"runtime_video_decoders\":[{\"instance_id\":1,\"resource_key\":\"x\",\"codec\":\"h264\",\"coded_width\":1,\"coded_height\":1,\"pixel_format\":\"yuv420p\",\"fps_num\":1,\"fps_den\":1,\"metadata_unknown\":\"false\"}],\"runtime_video_decoder_observation\":{\"status\":\"observed\",\"opened_instances\":1}}")!.AsObject();
        check(Summary(malformedMetadata)["status"]!.GetValue<string>() == "incomplete",
            "non-boolean unknown-metadata evidence fails closed without crashing the cost report");
        check(Workload(Stream(1, "one.tex"), Stream(1, "duplicate.tex"))["status"]!.GetValue<string>() == "incomplete",
            "duplicate decoder instance identities reject the workload evidence");
        var comparison = (JsonObject)WorkloadType.GetMethod("Compare", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [original, retainedAndNew])!;
        check(comparison["status"]!.GetValue<string>() == "requires_official_decode_tradeoff" &&
            comparison["candidate_instances_using_source_resource_keys"]!.GetValue<int>() == 1,
            "retained original video and added decoding stay an explicit unverified tradeoff");
    }
}
