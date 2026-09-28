using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Baker.Core.Analysis.EffectRange;

namespace Baker.Core;

/// <summary>One exact raw frame can bound a reviewed effect RT only when its complete input is time invariant.</summary>
internal static class EffectPrefixStaticRangeProbe
{
    internal sealed record Result(EffectPrefixRadianceBounds.StaticHeadBound? Bound, string Detail);

    internal static async Task<Result> MeasureAsync(JsonObject scene, JsonObject properties, JsonObject trace,
        ProjectSource source, string assets, NativeTools tools, string output, int ownerId, int prefixCount,
        CancellationToken cancellationToken)
    {
        try
        {
            if (prefixCount < 2) return new(null, "No later dynamic effect needs a static-head range.");
            JsonObject owner = scene["objects"]!.AsArray().OfType<JsonObject>()
                .Single(layer => SceneGraph.Int(layer["id"]) == ownerId);
            if (owner["parent"] is not null || owner["effects"] is not JsonArray effects || effects.Count < prefixCount ||
                effects[0] is not JsonObject first || SceneGraph.Int(first["id"]) is not int effectId ||
                SceneGraph.Resolve(first["visible"], properties)?.ToJsonString() == "false")
                return new(null, "The first authored effect is not an independent visible static head.");
            JsonNode? cameraMotion = SceneGraph.Resolve(scene["general"]?["camerashake"], properties);
            JsonNode? parallax = SceneGraph.Resolve(scene["general"]?["cameraparallax"], properties);
            if (cameraMotion?.ToJsonString() is not null and not "false" ||
                parallax?.ToJsonString() is not null and not "false" ||
                scene["camera"] is JsonNode camera && SceneAnalyzer.Walk(camera).OfType<JsonObject>()
                    .Any(node => node.ContainsKey("script") || node.ContainsKey("animation")))
                return new(null, "Camera motion could change the sampled source texels.");
            JsonObject runtime = trace["runtime_layers"]!.AsArray().OfType<JsonObject>()
                .Single(layer => SceneGraph.Int(layer["owner"]) == ownerId);
            JsonObject[] head = runtime["materials"]!.AsArray().OfType<JsonObject>()
                .Where(material => material["role"]?.GetValue<string>() == "source" ||
                    material["role"]?.GetValue<string>() == "effect" && SceneGraph.Int(material["effect"]) == 0)
                .ToArray();
            if (head.Length != 2 || head.Any(material => material["time_signature"]?["kind"]?.GetValue<string>() != "static" ||
                material["time_signature"]?["external"] is JsonArray { Count: > 0 } ||
                material["time_signature"]?["transient"]?.GetValue<bool>() == true ||
                material["uses_audio_spectrum"]?.GetValue<bool>() == true ||
                material["uses_system_media_thumbnail"]?.GetValue<bool>() == true))
                return new(null, "The source or first effect has a time/external input.");
            JsonObject reviewed = owner.DeepClone().AsObject();
            reviewed["effects"] = new JsonArray(effects.Take(prefixCount).Select(effect => effect!.DeepClone()).ToArray());
            if (!EffectPrefixRadianceBounds.TryProve(reviewed, properties, source, assets, out _, out _, out string fullDetail))
                return new(null, "The retained chain has no reviewed range: " + fullDetail);
            JsonObject firstOnly = owner.DeepClone().AsObject();
            firstOnly["effects"] = new JsonArray(first.DeepClone());
            if (!EffectPrefixRadianceBounds.TryProve(firstOnly, properties, source, assets,
                    out double analyticLower, out double analyticUpper, out string firstDetail))
                return new(null, "The static head has no reviewed analytic range: " + firstDetail);
            var encoding = Verdict.CaptureEncoding(new(analyticLower, analyticUpper));
            if (encoding is null) return new(null, "The static head cannot be encoded without clipping.");
            var runner = new NativeRenderRunner(tools);
            var target = new RenderCaptureSelection(ownerId, effectId, EffectTerminal: true, ExactExtent: false);
            JsonObject metadata = await runner.RenderRawAsync(new(source.SourcePath, assets,
                Path.Combine(output, $"hdr-static-{ownerId}-metadata"), 64, 64, 60, 1, 1, Seed: 17,
                CaptureTarget: target, UserProperties: properties, HdrScale: encoding.Value.Scale,
                HdrLowerBound: encoding.Value.Lower), cancellationToken);
            JsonObject sourceInfo = metadata["native_result"]?["capture_source"]?.AsObject()
                ?? throw new InvalidDataException("Static range metadata has no capture source.");
            uint width = sourceInfo["width"]!.GetValue<uint>(), height = sourceInfo["height"]!.GetValue<uint>();
            if (width == 0 || height == 0 || (ulong)width * height * 4 > 256ul * 1024 * 1024 ||
                sourceInfo["pass"]?.GetValue<string>() != "__wpe_baker_hdr_affine")
                return new(null, "The reviewed first effect has no bounded exact local target.");
            JsonObject exact = await runner.RenderRawAsync(new(source.SourcePath, assets,
                Path.Combine(output, $"hdr-static-{ownerId}-exact"), width, height, 60, 1, 1, Seed: 17,
                CaptureTarget: target with { ExactExtent = true }, UserProperties: properties,
                HdrScale: encoding.Value.Scale, HdrLowerBound: encoding.Value.Lower, HdrRangeProbe: true),
                cancellationToken);
            JsonObject rendered = exact["native_result"]?.AsObject()
                ?? throw new InvalidDataException("Static range probe has no native result.");
            JsonObject captured = rendered["capture_source"]?.AsObject()
                ?? throw new InvalidDataException("Static range probe has no bound capture source.");
            if (rendered["readback_width"]?.GetValue<uint>() != width ||
                rendered["readback_height"]?.GetValue<uint>() != height ||
                captured["render_target"]?.GetValue<string>() != sourceInfo["render_target"]?.GetValue<string>() ||
                captured["pass"]?.GetValue<string>() != "__wpe_baker_hdr_affine" ||
                exact["source_sha256"]?.GetValue<string>() != metadata["source_sha256"]?.GetValue<string>())
                return new(null, "Static range probe changed source, extent, or terminal binding.");
            byte[] pixels = await File.ReadAllBytesAsync(exact["rgba_path"]!.GetValue<string>(), cancellationToken);
            if (pixels.Length != (long)width * height * 4)
                return new(null, "Static range probe did not read every target texel.");
            byte minimum = byte.MaxValue, maximum = byte.MinValue;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 3] != byte.MaxValue) return new(null, "Static range probe alpha is not opaque.");
                for (int channel = 0; channel < 3; ++channel)
                {
                    minimum = Math.Min(minimum, pixels[index + channel]);
                    maximum = Math.Max(maximum, pixels[index + channel]);
                }
            }
            if (minimum == 0 || maximum == 255) return new(null, "Static range probe touched a clipping endpoint.");
            double lower = encoding.Value.Lower, scale = encoding.Value.Scale;
            double magnitude = Math.Max(Math.Abs(analyticLower), Math.Abs(analyticUpper));
            double distance = Math.Max(Math.Abs(analyticLower - lower), Math.Abs(analyticUpper - lower));
            double epsilon = 8 * Math.ScaleB(1.0, -24) * (magnitude + Math.Abs(lower) + distance) / scale;
            double measuredLower = lower + scale * (minimum / 255.0 - 1.0 / 255 - epsilon);
            double measuredUpper = lower + scale * (maximum / 255.0 + 1.0 / 255 + epsilon);
            double boundedLower = (double)Half.BitDecrement((Half)measuredLower);
            double boundedUpper = (double)Half.BitIncrement((Half)measuredUpper);
            if (!double.IsFinite(boundedLower) || !double.IsFinite(boundedUpper) ||
                boundedLower < analyticLower || boundedUpper > analyticUpper)
                return new(null, "Static range probe does not refine the analytic bound.");
            string evidence = $"effect={effectId}; extent={width}x{height}; codes={minimum}..{maximum}; " +
                $"source={exact["source_sha256"]}; rgba_sha256={Convert.ToHexStringLower(SHA256.HashData(pixels))}";
            return new(new(effectId, boundedLower, boundedUpper, evidence), evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or
            OverflowException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new(null, error.Message);
        }
    }
}
