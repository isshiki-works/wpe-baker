using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Builds the same ordinary video image used by full-frame and grouped scene conversion.</summary>
public static class VideoSceneBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<JsonObject> WriteLayerAsync(string project, string stem, string videoFile,
        uint videoWidth, uint videoHeight, int id, double x, double y, double drawWidth, double drawHeight,
        CancellationToken cancellationToken = default, bool packedAlpha = false,
        double parallaxDepthX = 0, double parallaxDepthY = 0, double geometryOffsetX = 0, double geometryOffsetY = 0,
        bool rgbaFrame = false, bool capturedColor = false)
    {
        string textureStem = "wpe_baker_video/" + stem;
        string texturePath = ProjectSource.ContainedPath(project, $"materials/{textureStem}.tex");
        string materialResource = $"materials/{textureStem}.json";
        string modelResource = $"models/wpe_baker_video/{stem}.json";
        Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
        if (rgbaFrame) await TextureContainer.WriteRgbaAsync(texturePath, videoWidth, videoHeight,
            await File.ReadAllBytesAsync(videoFile, cancellationToken), cancellationToken);
        else await TextureContainer.WriteVideoAsync(texturePath, videoFile, videoWidth, videoHeight, cancellationToken);
        string shader = "genericimage4";
        if (packedAlpha || geometryOffsetX != 0 || geometryOffsetY != 0 || capturedColor)
        {
            shader = "wpe_baker_video/" + stem;
            string vertex = "// SPDX-License-Identifier: MIT\nuniform mat4 g_ModelViewProjectionMatrix;\nattribute vec3 a_Position;\nattribute vec2 a_TexCoord;\nvarying vec2 v_TexCoord;\nvarying vec3 v_ScreenPos;\nvoid main() { gl_Position = mul(vec4(a_Position, 1.0), g_ModelViewProjectionMatrix); v_TexCoord = a_TexCoord; v_ScreenPos = gl_Position.xyw;\n#ifdef HLSL\nv_ScreenPos.y = -v_ScreenPos.y;\n#endif\n}\n";
            string fragment = "// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\nuniform sampler2D g_Texture1; // {\"hidden\":true,\"default\":\"_rt_FullFrameBuffer\"}\nvarying vec2 v_TexCoord;\nvarying vec3 v_ScreenPos;\nvoid main() { vec3 rgb = texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5, v_TexCoord.y)).rgb; float a = texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5 + 0.5, v_TexCoord.y)).r; vec2 uv = (v_ScreenPos.xy / v_ScreenPos.z) * 0.5 + 0.5; vec3 background = texSample2D(g_Texture1, uv).rgb; gl_FragColor = vec4(rgb + background * (1.0 - a), 1.0); }\n";
            // Keep the node at the original camera center so parallax contributes only the
            // pointer displacement. Cropping is a vertex offset, not a new parallax origin.
            string offset = FormattableString.Invariant($"vec4(a_Position + vec3({geometryOffsetX * videoWidth / drawWidth:R}, {geometryOffsetY * videoHeight / drawHeight:R}, 0.0), 1.0)");
            vertex = vertex.Replace("vec4(a_Position, 1.0)", offset, StringComparison.Ordinal);
            if (!packedAlpha) fragment = "// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main() { gl_FragColor = vec4(texSample2D(g_Texture0, v_TexCoord).rgb, 1.0); }\n";
            else if (rgbaFrame) fragment = fragment.Replace("vec2(v_TexCoord.x * 0.5, v_TexCoord.y)", "v_TexCoord", StringComparison.Ordinal)
                .Replace("texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5 + 0.5, v_TexCoord.y)).r", "texSample2D(g_Texture0, v_TexCoord).a", StringComparison.Ordinal);
            else
            {
                // Each packed half needs its own texel boundary when the image is enlarged.
                // Whole-texture clamp mode alone permits RGB samples to bleed into coverage.
                double halfTexel = .5 / videoWidth;
                fragment = fragment.Replace("vec2(v_TexCoord.x * 0.5, v_TexCoord.y)",
                    FormattableString.Invariant($"vec2(clamp(v_TexCoord.x * 0.5, {halfTexel:R}, {.5 - halfTexel:R}), v_TexCoord.y)"), StringComparison.Ordinal)
                    .Replace("vec2(v_TexCoord.x * 0.5 + 0.5, v_TexCoord.y)",
                    FormattableString.Invariant($"vec2(clamp(v_TexCoord.x * 0.5 + 0.5, {.5 + halfTexel:R}, {1 - halfTexel:R}), v_TexCoord.y)"), StringComparison.Ordinal);
            }
            string vertexPath = ProjectSource.ContainedPath(project, $"shaders/{shader}.vert");
            Directory.CreateDirectory(Path.GetDirectoryName(vertexPath)!);
            await File.WriteAllTextAsync(vertexPath, vertex, cancellationToken);
            await File.WriteAllTextAsync(ProjectSource.ContainedPath(project, $"shaders/{shader}.frag"), fragment, cancellationToken);
        }
        await WriteJsonAsync(ProjectSource.ContainedPath(project, materialResource), new JsonObject {
            ["passes"] = new JsonArray(new JsonObject {
                ["shader"] = shader, ["blending"] = "translucent", ["cullmode"] = "nocull",
                ["depthtest"] = "disabled", ["depthwrite"] = "disabled",
                ["combos"] = new JsonObject { ["LIGHTING"] = 0, ["REFLECTION"] = 0, ["FOG"] = 0 },
                ["textures"] = packedAlpha ? new JsonArray(textureStem, "_rt_FullFrameBuffer") : new JsonArray(textureStem) }) }, cancellationToken);
        await WriteJsonAsync(ProjectSource.ContainedPath(project, modelResource), new JsonObject {
            ["material"] = materialResource, ["autosize"] = true, ["cropoffset"] = "0 0" }, cancellationToken);
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        return new JsonObject {
            ["id"] = id, ["name"] = (rgbaFrame ? "Static " : "Video ") + stem, ["image"] = modelResource,
            ["origin"] = $"{Number(x)} {Number(y)} 0", ["angles"] = "0 0 0",
            ["scale"] = $"{Number(drawWidth / videoWidth)} {Number(drawHeight / videoHeight)} 1",
            ["size"] = $"{videoWidth} {videoHeight}", ["visible"] = true, ["alpha"] = 1,
            ["parallaxDepth"] = $"{Number(parallaxDepthX)} {Number(parallaxDepthY)}" };
    }

    internal static async Task WriteJsonAsync(string path, JsonNode value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, value, JsonOptions, cancellationToken);
    }
}
