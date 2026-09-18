using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Observed decoder instances, not material references or a hardware power estimate.</summary>
internal static class HybridVideoWorkload
{
    internal static JsonObject Summarize(JsonObject native)
    {
        var issues = new JsonArray();
        var streams = native["runtime_video_decoders"] as JsonArray;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        double pixels = 0;
        var observation = native["runtime_video_decoder_observation"] as JsonObject;
        if (Text(observation?["status"]) != "observed" ||
            !Integer(observation?["opened_instances"], out int opened) || opened != streams?.Count)
            issues.Add("Renderer did not confirm a cumulative decoder observation with matching instance count.");
        if (streams is null) issues.Add("Renderer did not report its observed video decoder instances.");
        else foreach (var item in streams)
        {
            if (item is not JsonObject stream || stream["instance_id"] is null ||
                !ids.Add(stream["instance_id"]!.ToJsonString()) || !False(stream["metadata_unknown"]) ||
                Unknown(Text(stream["resource_key"])) || Unknown(Text(stream["codec"])) ||
                Unknown(Text(stream["pixel_format"])) ||
                !Positive(stream["coded_width"], out double width) ||
                !Positive(stream["coded_height"], out double height) ||
                !Positive(stream["fps_num"], out double numerator) ||
                !Positive(stream["fps_den"], out double denominator))
            { issues.Add("A decoder instance has missing, duplicate or unknown stream metadata."); continue; }
            pixels += width * height * numerator / denominator;
        }
        if (!double.IsFinite(pixels)) issues.Add("Decoder workload exceeded a finite numeric range.");
        return new JsonObject {
            ["status"] = issues.Count == 0 ? "observed" : "incomplete",
            ["scope"] = "Union of decoder instances opened during this short native observation, including warmup and retained source videos. The union is a conservative workload proxy, not simultaneous decoding, hardware usage, full-loop coverage or power.",
            ["streams"] = streams?.DeepClone(), ["observation"] = observation?.DeepClone(), ["instance_count"] = streams?.Count,
            ["coded_pixels_per_second"] = issues.Count == 0 ? pixels : null,
            ["issues"] = issues };
    }

    internal static bool Complete(JsonObject? workload) => Text(workload?["status"]) == "observed";

    internal static bool NoGreaterThan(JsonObject? candidate, JsonObject? reference)
    {
        if (!Complete(candidate) || !Complete(reference)) return false;
        var budget = Buckets(reference!);
        foreach (var (format, amount) in Buckets(candidate!))
            if (!budget.TryGetValue(format, out var limit) || amount.Count > limit.Count || amount.Pixels > limit.Pixels)
                return false;
        return true;
    }

    internal static JsonObject Compare(JsonObject source, JsonObject candidate)
    {
        bool complete = Complete(source) && Complete(candidate);
        var sourceKeys = (source["streams"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(stream => Text(stream["resource_key"])).Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
        int retained = (candidate["streams"] as JsonArray ?? []).OfType<JsonObject>()
            .Count(stream => sourceKeys.Contains(Text(stream["resource_key"])));
        return new JsonObject {
            ["status"] = !complete ? "incomplete" : NoGreaterThan(candidate, source) ? "within_source_decoder_budget" : "requires_official_decode_tradeoff",
            ["candidate_instances_using_source_resource_keys"] = retained,
            ["source_coded_pixels_per_second"] = source["coded_pixels_per_second"]?.DeepClone(),
            ["candidate_coded_pixels_per_second"] = candidate["coded_pixels_per_second"]?.DeepClone(),
            ["scope"] = "Resource-key matches indicate retained media in these projects; they do not prove equal content or WPE decoder sharing. Codec/pixel-format buckets compare observed instance counts and coded pixel rates only." };
    }

    private static Dictionary<(string Codec, string Format), (int Count, double Pixels)> Buckets(JsonObject workload)
    {
        var result = new Dictionary<(string, string), (int Count, double Pixels)>();
        foreach (var stream in workload["streams"]!.AsArray().OfType<JsonObject>())
        {
            var key = (stream["codec"]!.GetValue<string>(), stream["pixel_format"]!.GetValue<string>());
            Positive(stream["coded_width"], out double width); Positive(stream["coded_height"], out double height);
            Positive(stream["fps_num"], out double numerator); Positive(stream["fps_den"], out double denominator);
            var previous = result.GetValueOrDefault(key);
            result[key] = (previous.Count + 1, previous.Pixels + width * height * numerator / denominator);
        }
        return result;
    }

    private static bool Positive(JsonNode? node, out double value)
    {
        value = double.NaN;
        return node is JsonValue number && number.TryGetValue<double>(out value) && double.IsFinite(value) && value > 0;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
    private static bool False(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out bool known) && !known;
    private static bool Integer(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue number && number.TryGetValue(out value);
    }
    private static bool Unknown(string? text) => string.IsNullOrWhiteSpace(text) || text == "unknown";
}
