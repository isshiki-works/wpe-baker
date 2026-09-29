using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>Resource-only optimization for two consecutive, compatible water-wave passes.</summary>
public static class WaterwaveFusion
{
    // These fingerprints identify the reviewed wave algorithm, irrespective of its resource path or effect name.
    // An edited shader is unknown until its sampling and math have been reviewed.
    private const string VertexSha256 = "188D1E33DE160E86708329ED1401CDC546426293E1F0B66B041D5CD556FE388F";
    private const string FragmentSha256 = "745BD77B1333EE53664718AB81F4922D92A8302FEA29A5AD05871780D1E4D1F5";
    private static readonly Regex EffectAccess = new(@"\b(?:getEffects?|findEffect)\s*\(|\.\s*effects\b|\[\s*['""`]effects['""`]\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DynamicLayerAccess = new(@"\b(?:getLayerByIndex|enumerateLayers|getChildren|getParent|eval|Function|Reflect|Proxy)\b", RegexOptions.CultureInvariant);

    internal sealed record Wave(JsonObject Entry, double Direction, double Speed, double Scale, double Strength,
        double TimeOffset, string Mask, string? OffsetTexture, uint MaskWidth, uint MaskHeight);

    public static async Task<JsonObject> OptimizeAsync(string sourcePath, string output, CancellationToken cancellationToken = default)
        => await OptimizeAsync(sourcePath, output, null, null, copyUnchanged: true, cancellationToken);

    /// <summary>Desktop generation keeps the selected wallpaper values and skips an unchanged copy.</summary>
    public static Task<JsonObject> OptimizeSelectedAsync(string sourcePath, string output, JsonObject properties,
        CancellationToken cancellationToken = default) =>
        OptimizeAsync(sourcePath, output, properties, null, copyUnchanged: false, cancellationToken);

    public static Task<JsonObject> OptimizeSelectedAsync(string sourcePath, string output, JsonObject properties,
        string expectedSourceSha256, CancellationToken cancellationToken = default) =>
        OptimizeAsync(sourcePath, output, properties, expectedSourceSha256, copyUnchanged: false, cancellationToken);

    private static async Task<JsonObject> OptimizeAsync(string sourcePath, string output, JsonObject? properties,
        string? expectedSourceSha256, bool copyUnchanged, CancellationToken cancellationToken)
    {
        using var source = new ProjectSource(sourcePath);
        if (source.Kind != "scene") throw new InvalidDataException("Water-wave fusion requires a Scene project.");
        JsonObject scene = source.ReadJson(source.SceneResource);
        var changes = new List<(JsonArray Effects, int Index, JsonObject Replacement, string Stem, string Vertex, string Fragment)>();
        var objects = (scene["objects"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var byId = objects.Where(obj => SceneGraph.Int(obj["id"]) is not null)
            .ToDictionary(obj => SceneGraph.Int(obj["id"])!.Value);
        var lookupEdges = Liveness.ScriptLookupEdges(byId);
        bool skippedEffectAccess = false;
        bool sdr = scene["general"] is JsonObject general && (general["hdr"] is null ||
            general["hdr"] is JsonValue hdr && hdr.TryGetValue<bool>(out bool enabled) && !enabled);
        foreach (var obj in objects)
        {
            // The image effect ping-pong target is RGBA8, clamp-to-edge and linear only in the
            // SDR path with a linearly sampled source (SceneImageObjectParser/Program/Resource).
            if (!sdr || !LinearImageSource(source, obj) || obj["effects"] is not JsonArray effects) continue;
            for (int index = 0; index + 1 < effects.Count; ++index)
            {
                if (effects[index] is not JsonObject first || effects[index + 1] is not JsonObject second ||
                    !TryWave(source, first, requireTimeOffset: false, out var a) ||
                    !TryWave(source, second, requireTimeOffset: true, out var b) ||
                    a!.MaskWidth != b!.MaskWidth || a.MaskHeight != b.MaskHeight) continue;
                if (ScriptsMayAccessEffects(scene, objects, obj, lookupEdges))
                {
                    skippedEffectAccess = true;
                    continue;
                }
                string stem = $"wpe_baker_waterwave/pair_{obj["id"]?.ToJsonString()}_{index}";
                if (obj["id"] is null || new[] { $"effects/{stem}.json", $"materials/{stem}.json",
                    $"shaders/{stem}.vert", $"shaders/{stem}.frag" }.Any(source.Contains)) continue;
                var fusedTextures = a.OffsetTexture is null
                    ? new JsonArray(null, a.Mask, b!.Mask, b.OffsetTexture)
                    : new JsonArray(null, a.Mask, a.OffsetTexture, b!.Mask, b.OffsetTexture);
                var replacement = new JsonObject {
                    ["file"] = $"effects/{stem}.json", ["id"] = first["id"]?.DeepClone(),
                    ["name"] = first["name"]?.DeepClone(), ["visible"] = true,
                    ["passes"] = new JsonArray(new JsonObject {
                        ["id"] = a!.Entry["passes"]![0]!["id"]?.DeepClone(),
                        ["textures"] = fusedTextures }) };
                changes.Add((effects, index, replacement, stem, Vertex(a!, b!), Fragment(a!, b!)));
                ++index;
            }
        }
        string before = await source.SourceHashAsync(cancellationToken);
        if (expectedSourceSha256 is not null && !before.Equals(expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source changed after optimization was queued.");
        if (changes.Count == 0 && !copyUnchanged)
            return new JsonObject { ["status"] = "unchanged", ["source_sha256"] = before,
                ["output"] = null, ["fused_pairs"] = 0, ["skipped_external_effect_access"] = skippedEffectAccess };
        await source.ExtractAsync(output, cancellationToken);
        if (before != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during optimization.");
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string shader = change.Stem;
            await WriteNew($"shaders/{shader}.vert", change.Vertex);
            await WriteNew($"shaders/{shader}.frag", change.Fragment);
            await WriteNew($"materials/{shader}.json", new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["shader"] = shader, ["blending"] = "normal", ["depthtest"] = "disabled",
                ["depthwrite"] = "disabled", ["cullmode"] = "nocull" }) }.ToJsonString());
            await WriteNew($"effects/{shader}.json", new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["material"] = $"materials/{shader}.json" }) }.ToJsonString());
        }
        // Apply from the end: indices in an object were measured against the original array.
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            change.Effects.RemoveAt(change.Index + 1);
            change.Effects[change.Index] = change.Replacement;
        }
        JsonObject metadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        bool hasWorkshopIdentity = metadata.ContainsKey("workshopid") || metadata.ContainsKey("publishedfileid");
        if (properties is not null || hasWorkshopIdentity)
        {
            // Like other generated projects, this independent copy must not claim the workshop item's identity.
            metadata.Remove("workshopid"); metadata.Remove("publishedfileid");
            if (properties is not null)
            {
                ProjectWriter.ApplyPropertySnapshot(metadata, properties);
                ProjectWriter.ApplyVisibilityFallbacks(scene, properties);
                metadata["type"] = "scene";
                metadata["file"] = source.SceneResource;
            }
            await File.WriteAllTextAsync(Path.Combine(output, "project.json"), metadata.ToJsonString(), cancellationToken);
        }
        if (changes.Count > 0)
            await File.WriteAllTextAsync(ProjectSource.ContainedPath(output, source.SceneResource), scene.ToJsonString(), cancellationToken);
        return new JsonObject { ["status"] = changes.Count > 0 ? "optimized" : "unchanged", ["source_sha256"] = before,
            ["output"] = Path.GetFullPath(output), ["fused_pairs"] = changes.Count,
            ["skipped_external_effect_access"] = skippedEffectAccess };

        async Task WriteNew(string resource, string contents)
        {
            string path = ProjectSource.ContainedPath(output, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(contents.AsMemory(), cancellationToken);
        }
    }

    internal static bool ScriptsMayAccessEffects(JsonObject scene, JsonObject[] objects, JsonObject target,
        JsonObject[] lookupEdges)
    {
        int targetId = SceneGraph.Int(target["id"]) ?? -1;
        // The existing layer-name analysis resolves getLayer calls across owners. A dynamic target
        // that it cannot resolve stays conservative; unrelated thisLayer effects may proceed.
        foreach (var owner in objects)
        {
            int ownerId = SceneGraph.Int(owner["id"]) ?? -1;
            foreach (var binding in SceneAnalyzer.Walk(owner).OfType<JsonObject>())
            {
                if (binding["script"] is not JsonValue value || !value.TryGetValue<string>(out var raw)) continue;
                string code = Liveness.CapabilityScanText(raw);
                if (!EffectAccess.IsMatch(code)) continue;
                if (ownerId == targetId || DynamicLayerAccess.IsMatch(code) ||
                    lookupEdges.Any(edge => SceneGraph.Int(edge["owner"]) == ownerId &&
                        SceneGraph.Int(edge["target"]) == targetId)) return true;
                // Only an explicit thisLayer receiver proves a script on another owner stays there.
                // Shared variables and indirect layer references are not statically resolvable here.
                string remainder = Regex.Replace(code, @"\bthisLayer\s*\.\s*(?:getEffects?|findEffect)\s*\(|\bthisLayer\s*\.\s*effects\b",
                    "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (EffectAccess.IsMatch(remainder)) return true;
            }
        }
        return scene.Where(pair => pair.Key != "objects").SelectMany(pair => SceneAnalyzer.Walk(pair.Value))
            .OfType<JsonObject>().Any(binding => binding["script"] is JsonValue value &&
                value.TryGetValue<string>(out var raw) && EffectAccess.IsMatch(Liveness.CapabilityScanText(raw)));
    }

    private static bool LinearImageSource(ProjectSource source, JsonObject owner)
    {
        try
        {
            if (owner["image"]?.GetValue<string>() is not string modelResource || !source.Contains(modelResource)) return false;
            JsonObject model = source.ReadJson(modelResource);
            if (SceneAnalyzer.Walk(model).OfType<JsonObject>().Any(node => node.ContainsKey("script") || node.ContainsKey("user"))) return false;
            if (model["material"]?.GetValue<string>() is not string materialResource || !source.Contains(materialResource)) return false;
            JsonObject material = source.ReadJson(materialResource);
            if (SceneAnalyzer.Walk(material).OfType<JsonObject>().Any(node => node.ContainsKey("script") || node.ContainsKey("user") || node.ContainsKey("usertextures"))) return false;
            if (material["passes"] is not JsonArray { Count: 1 } passes ||
                passes[0]?["textures"] is not JsonArray { Count: > 0 } textures ||
                textures[0]?.GetValue<string>() is not string texture || texture.StartsWith("_rt_", StringComparison.Ordinal)) return false;
            string resource = $"materials/{texture}.tex";
            return source.Contains(resource) && TextureContainer.TryReadHeader(source.ReadPrefix(resource, 64), out var header) &&
                (header.Flags & 1) == 0; // noInterpolation would make the entire ping-pong chain nearest-sampled.
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryWave(ProjectSource source, JsonObject effect, bool requireTimeOffset, out Wave? wave)
    {
        wave = null;
        try
        {
            if (effect["visible"]?.GetValue<bool>() != true ||
                effect.Where(p => p.Key is not ("file" or "id" or "name" or "passes" or "visible")).Any() ||
                SceneAnalyzer.Walk(effect).OfType<JsonObject>().Any(node => node.ContainsKey("script") || node.ContainsKey("user")) ||
                effect["file"]?.GetValue<string>() is not string effectResource || !source.Contains(effectResource) ||
                effect["passes"] is not JsonArray { Count: 1 } instances || instances[0] is not JsonObject instance ||
                instance.Where(p => p.Key is not ("constantshadervalues" or "id" or "textures")).Any() ||
                instance["constantshadervalues"] is not JsonObject values ||
                instance["textures"] is not JsonArray textures || textures.Count is not (2 or 3) ||
                textures[0] is not null || textures[1]?.GetValue<string>() is not string mask ||
                (requireTimeOffset && textures.Count != 3) ||
                (textures.Count == 3 && textures[2]?.GetValue<string>() is not string)) return false;
            JsonObject definition = source.ReadJson(effectResource);
            if (definition.Where(p => p.Key is not ("dependencies" or "editable" or "gizmos" or "group" or "name" or "passes" or "replacementkey" or "version" or "description" or "preview")).Any() ||
                definition["passes"] is not JsonArray { Count: 1 } passes || passes[0] is not JsonObject definitionPass ||
                definitionPass.Count != 1 || definitionPass["material"]?.GetValue<string>() is not string materialResource ||
                !source.Contains(materialResource)) return false;
            JsonObject material = source.ReadJson(materialResource);
            if (material.Count != 1 || material["passes"] is not JsonArray { Count: 1 } materialPasses || materialPasses[0] is not JsonObject materialPass ||
                materialPass["shader"]?.GetValue<string>() is not string shader ||
                materialPass["blending"]?.GetValue<string>() != "normal" ||
                materialPass["depthtest"]?.GetValue<string>() != "disabled" ||
                materialPass["depthwrite"]?.GetValue<string>() != "disabled" ||
                materialPass["cullmode"]?.GetValue<string>() != "nocull" ||
                materialPass.Where(p => p.Key is not ("shader" or "blending" or "depthtest" or "depthwrite" or "cullmode" or "textures")).Any() ||
                materialPass["textures"] is JsonArray { Count: > 0 } ||
                !Hash(source, $"shaders/{shader}.vert").Equals(VertexSha256, StringComparison.Ordinal) ||
                !Hash(source, $"shaders/{shader}.frag").Equals(FragmentSha256, StringComparison.Ordinal)) return false;
            if (!TextureOk(source, mask, out uint width, out uint height) ||
                (textures.Count == 3 && (!TextureOk(source, textures[2]!.GetValue<string>(), out uint w, out uint h) || w != width || h != height))) return false;
            foreach (string key in new[] { "direction", "speed", "scale", "strength", "globleTimeOffset", "offset", "exponent" })
                if (values[key] is not JsonValue v || !v.TryGetValue<double>(out double number) || !double.IsFinite(number)) return false;
            if (values.Count != 7 || values["offset"]!.GetValue<double>() != 0 || values["exponent"]!.GetValue<double>() != 1) return false;
            wave = new Wave(effect, values["direction"]!.GetValue<double>(), values["speed"]!.GetValue<double>(),
                values["scale"]!.GetValue<double>(), values["strength"]!.GetValue<double>(),
                values["globleTimeOffset"]!.GetValue<double>(), mask,
                textures.Count == 3 ? textures[2]!.GetValue<string>() : null, width, height);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string Hash(ProjectSource source, string resource) =>
        Convert.ToHexString(SHA256.HashData(source.Read(resource)));

    private static bool TextureOk(ProjectSource source, string texture, out uint width, out uint height)
    {
        width = height = 0;
        string resource = $"materials/{texture}.tex";
        if (!source.Contains(resource)) return false;
        byte[] prefix = source.ReadPrefix(resource, 71);
        // Engine TexImageParser maps 9 to R8 UNORM; flag 2 is clamp UV. For TEXB4 with no
        // variant table, the mip count is at byte 67. One mip makes explicit LOD 0 equivalent
        // to the authored shader's automatic LOD for this mask.
        return prefix.Length == 71 && TextureContainer.TryReadHeader(prefix, out var header) &&
            header.Format == 9 && header.Flags == 2 && prefix.AsSpan(46, 9).SequenceEqual("TEXB0004\0"u8) &&
            BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(55, 4)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(59, 4)) == -1 &&
            BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(63, 4)) == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(67, 4)) == 1 &&
            TextureContainer.TryReadImageExtent(prefix, out width, out height) &&
            header.Width == width && header.Height == height;
    }

    private static string N(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private static string Vertex(Wave a, Wave b) => $$"""
        #include "common.h"
        uniform mat4 g_ModelViewProjectionMatrix;
        attribute vec3 a_Position;
        attribute vec2 a_TexCoord;
        varying vec2 v_Uv;
        varying vec2 v_DirA;
        varying vec2 v_DirB;
        void main() {
            gl_Position = mul(vec4(a_Position, 1.0), g_ModelViewProjectionMatrix);
            v_Uv = a_TexCoord;
            v_DirA = rotateVec2(vec2(0, 1), {{N(a.Direction)}});
            v_DirB = rotateVec2(vec2(0, 1), {{N(b.Direction)}});
        }
        """;

    internal static string Fragment(Wave a, Wave b) => $$"""
        #include "common.h"
        uniform sampler2D g_Texture0; // {"hidden":true}
        uniform sampler2D g_Texture1;
        uniform sampler2D g_Texture2;
        uniform sampler2D g_Texture3;{{(a.OffsetTexture is null ? "" : "\nuniform sampler2D g_Texture4;")}}
        uniform vec4 g_Texture0Resolution;
        uniform float g_Time;
        varying vec2 v_Uv;
        varying vec2 v_DirA;
        varying vec2 v_DirB;

        vec4 firstPass(vec2 uv, vec2 dirA) {
            float mask = texSample2DLod(g_Texture1, uv, 0.0).r;
            float phase = g_Time * {{N(a.Speed)}} + dot(uv, dirA) * {{N(a.Scale)}};
            {{(a.OffsetTexture is null ? "" : "phase += texSample2DLod(g_Texture2, uv, 0.0).r * M_PI_2;\n            ")}}phase += {{N(a.TimeOffset)}} * M_PI_2;
            vec2 strength = (CAST2(500) / g_Texture0Resolution.xy) * {{N(a.Strength)}} * {{N(a.Strength)}};
            float wave = sin(phase);
            uv += abs(wave) * sign(wave) * vec2(dirA.y, -dirA.x) * strength * mask;
            vec4 color = texSample2DLod(g_Texture0, uv, 0.0);
            return floor(clamp(color, CAST4(0), CAST4(1)) * 255.0 + 0.5) / 255.0;
        }

        vec4 firstTexel(vec2 pixel, vec2 dirA) {
            vec2 size = g_Texture0Resolution.xy;
            return firstPass((clamp(pixel, CAST2(0), size - 1.0) + 0.5) / size, dirA);
        }

        void main() {
            float mask = texSample2D(g_Texture{{(a.OffsetTexture is null ? 2 : 3)}}, v_Uv).r;
            vec4 result;
            if (mask == 0.0) {
                result = firstPass(v_Uv, v_DirA);
            } else {
                float phase = g_Time * {{N(b.Speed)}} + dot(v_Uv, v_DirB) * {{N(b.Scale)}};
                phase += texSample2D(g_Texture{{(a.OffsetTexture is null ? 3 : 4)}}, v_Uv).r * M_PI_2;{{(b.TimeOffset == 0 ? "" : "\n    phase += " + N(b.TimeOffset) + " * M_PI_2;")}}
                vec2 strength = (CAST2(500) / g_Texture0Resolution.xy) * {{N(b.Strength)}} * {{N(b.Strength)}};
                float wave = sin(phase);
                vec2 uv = v_Uv + abs(wave) * sign(wave) * vec2(v_DirB.y, -v_DirB.x) * strength * mask;
                vec2 pixel = uv * g_Texture0Resolution.xy - 0.5;
                vec2 base = floor(pixel), f = frac(pixel);
                result = mix(mix(firstTexel(base, v_DirA), firstTexel(base + vec2(1, 0), v_DirA), f.x),
                    mix(firstTexel(base + vec2(0, 1), v_DirA), firstTexel(base + vec2(1, 1), v_DirA), f.x), f.y);
            }
            gl_FragColor = result;
        }
        """;
}
