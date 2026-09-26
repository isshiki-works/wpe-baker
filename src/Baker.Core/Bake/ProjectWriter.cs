using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 成品工程的写出：视频/静态图层（TEX 纹理、材质、模型、需要时的着色器）、装配好的 scene.json，
/// 以及改过标题与属性控件的 project.json。对象表本身由 <see cref="SceneAssembler"/> 装配。
/// </summary>
internal static class ProjectWriter
{
    /// <summary>写出一个视频（或静态 RGBA）图层的全部资源，返回要放进场景对象表的图层对象。</summary>
    public static async Task<JsonObject> WriteLayerAsync(string project, string stem, string videoFile,
        uint videoWidth, uint videoHeight, int id, double x, double y, double drawWidth, double drawHeight,
        CancellationToken cancellationToken = default, bool packedAlpha = false,
        double parallaxDepthX = 0, double parallaxDepthY = 0, double geometryOffsetX = 0, double geometryOffsetY = 0,
        bool rgbaFrame = false, bool capturedColor = false, double hdrScale = 1, bool alphaBelow = false, bool additiveLight = false)
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
        // 左半是预乘色 C、右半是覆盖度 a，要画出 C + (1 - a)·背景。translucent 混合给的是 src·a + 背景·(1 - a)，
        // 输出 C/a 即得同一结果，不必读帧缓冲。加性光（C 可以大于 a，a = 0 处也有 C）translucent 表达不了，这种组仍读帧缓冲。
        bool readsFramebuffer = packedAlpha && additiveLight;
        if (packedAlpha || geometryOffsetX != 0 || geometryOffsetY != 0 || capturedColor || hdrScale != 1)
        {
            shader = "wpe_baker_video/" + stem;
            string vertex = "// SPDX-License-Identifier: MIT\nuniform mat4 g_ModelViewProjectionMatrix;\nattribute vec3 a_Position;\nattribute vec2 a_TexCoord;\nvarying vec2 v_TexCoord;\nvarying vec3 v_ScreenPos;\nvoid main() { gl_Position = mul(vec4(a_Position, 1.0), g_ModelViewProjectionMatrix); v_TexCoord = a_TexCoord; v_ScreenPos = gl_Position.xyw;\n#ifdef HLSL\nv_ScreenPos.y = -v_ScreenPos.y;\n#endif\n}\n";
            string fragment = "// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\n" +
                (readsFramebuffer ? "uniform sampler2D g_Texture1; // {\"hidden\":true,\"default\":\"_rt_FullFrameBuffer\"}\nvarying vec2 v_TexCoord;\nvarying vec3 v_ScreenPos;\n" : "varying vec2 v_TexCoord;\n") +
                "void main() { vec3 rgb = texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5, v_TexCoord.y)).rgb; float a = texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5 + 0.5, v_TexCoord.y)).r; " +
                (readsFramebuffer ? "vec2 uv = (v_ScreenPos.xy / v_ScreenPos.z) * 0.5 + 0.5; vec3 background = texSample2D(g_Texture1, uv).rgb; gl_FragColor = vec4(rgb + background * (1.0 - a), 1.0); }\n"
                    : "gl_FragColor = vec4(rgb / max(a, 0.004), a); }\n");
            // Keep the node at the original camera center so parallax contributes only the
            // pointer displacement. Cropping is a vertex offset, not a new parallax origin.
            string offset = FormattableString.Invariant($"vec4(a_Position + vec3({geometryOffsetX * videoWidth / drawWidth:R}, {geometryOffsetY * videoHeight / drawHeight:R}, 0.0), 1.0)");
            vertex = vertex.Replace("vec4(a_Position, 1.0)", offset, StringComparison.Ordinal);
            if (!packedAlpha) fragment = "// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main() { gl_FragColor = vec4(texSample2D(g_Texture0, v_TexCoord).rgb, 1.0); }\n";
            else if (rgbaFrame) fragment = fragment.Replace("vec2(v_TexCoord.x * 0.5, v_TexCoord.y)", "v_TexCoord", StringComparison.Ordinal)
                .Replace("texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5 + 0.5, v_TexCoord.y)).r", "texSample2D(g_Texture0, v_TexCoord).a", StringComparison.Ordinal);
            else if (alphaBelow)
            {
                // 上下并排（HardwareDecodeDimensions.StackedVertically）：上半 RGB、下半 alpha，各自夹在自己那半幅内。
                double halfTexel = .5 / videoHeight;
                fragment = fragment.Replace("vec2(v_TexCoord.x * 0.5, v_TexCoord.y)",
                    FormattableString.Invariant($"vec2(v_TexCoord.x, clamp(v_TexCoord.y * 0.5, {halfTexel:R}, {.5 - halfTexel:R}))"), StringComparison.Ordinal)
                    .Replace("vec2(v_TexCoord.x * 0.5 + 0.5, v_TexCoord.y)",
                    FormattableString.Invariant($"vec2(v_TexCoord.x, clamp(v_TexCoord.y * 0.5 + 0.5, {.5 + halfTexel:R}, {1 - halfTexel:R}))"), StringComparison.Ordinal);
            }
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
            // 浮点捕获的组存的是 rgb/k（预乘），在着色器里乘回 k：官方 HDR 管线的浮点目标保留 >1。
            // genericimage4 没有 g_Brightness，所以不靠图层 brightness。
            if (hdrScale != 1) fragment = fragment.Replace("gl_FragColor = vec4(rgb ", $"gl_FragColor = vec4(rgb * {Number(hdrScale)} ", StringComparison.Ordinal)
                .Replace(".rgb, 1.0);", $".rgb * {Number(hdrScale)}, 1.0);", StringComparison.Ordinal);
            string vertexPath = ProjectSource.ContainedPath(project, $"shaders/{shader}.vert");
            Directory.CreateDirectory(Path.GetDirectoryName(vertexPath)!);
            await File.WriteAllTextAsync(vertexPath, vertex, cancellationToken);
            await File.WriteAllTextAsync(ProjectSource.ContainedPath(project, $"shaders/{shader}.frag"), fragment, cancellationToken);
        }
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, materialResource), new JsonObject {
            ["passes"] = new JsonArray(new JsonObject {
                ["shader"] = shader, ["blending"] = "translucent", ["cullmode"] = "nocull",
                ["depthtest"] = "disabled", ["depthwrite"] = "disabled",
                ["combos"] = new JsonObject { ["LIGHTING"] = 0, ["REFLECTION"] = 0, ["FOG"] = 0 },
                ["textures"] = readsFramebuffer ? new JsonArray(textureStem, "_rt_FullFrameBuffer") : new JsonArray(textureStem) }) }, cancellationToken);
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, modelResource), new JsonObject {
            ["material"] = materialResource, ["autosize"] = true, ["cropoffset"] = "0 0" }, cancellationToken);
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        return new JsonObject {
            ["id"] = id, ["name"] = (rgbaFrame ? "Static " : "Video ") + stem, ["image"] = modelResource,
            ["origin"] = $"{Number(x)} {Number(y)} 0", ["angles"] = "0 0 0",
            ["scale"] = $"{Number(drawWidth / videoWidth)} {Number(drawHeight / videoHeight)} 1",
            ["size"] = $"{videoWidth} {videoHeight}", ["visible"] = true, ["alpha"] = 1,
            ["parallaxDepth"] = $"{Number(parallaxDepthX)} {Number(parallaxDepthY)}" };
    }

    /// <summary>
    /// 写 scene.json 与 project.json：标题加后缀、去掉创意工坊编号；属性控件除昼夜动态导出仍可调的几项外全部隐藏
    /// （值照旧留给实时脚本读），另加一条说明。
    /// </summary>
    internal static async Task WriteAsync(string project, string sceneResource, JsonObject scene, JsonObject metadata,
        DaytimeSplit.DynamicExport? daytime, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(ProjectSource.ContainedPath(project, sceneResource), scene.ToJsonString(), cancellationToken);
        metadata["title"] = (metadata["title"]?.GetValue<string>() ?? "Wallpaper") + " · Video + live";
        metadata.Remove("workshopid"); metadata.Remove("publishedfileid");
        metadata["type"] = "scene"; metadata["file"] = sceneResource;
        if (metadata["general"]?["properties"] is JsonObject exportedProperties)
        {
            // Display conditions hide controls without removing the values read by live scripts.
            HideFixedPropertyControls(exportedProperties, daytime?.PropertyKeys);
            string noticeKey = "wpebakersnapshotnotice";
            while (exportedProperties.ContainsKey(noticeKey)) noticeKey += "0";
            exportedProperties[noticeKey] = new JsonObject { ["type"] = "text", ["value"] = "", ["order"] = -1, ["index"] = -1,
                ["text"] = daytime is null
                    ? "画面设置已固定；在 WPE Baker 中改设置后重新生成。 / Settings are fixed; change them in WPE Baker and generate again."
                    : "昼夜和时段选择仍可调整；其他画面设置已固定。 / Daytime and manual selection remain adjustable; other visual settings are fixed." };
        }
        metadata["description"] = (metadata["description"]?.GetValue<string>() ?? "") +
            (daytime is null
                ? "\nGenerated for the selected settings. Change omitted styles or baked visual settings in WPE Baker and generate again."
                : "\nAutomatic daytime changes and manual selection are preserved. Unreplaced states retain their original videos; other visual settings use the selected snapshot.");
        await File.WriteAllTextAsync(Path.Combine(project, "project.json"), metadata.ToJsonString(), cancellationToken);
    }

    internal static void ApplyPropertySnapshot(JsonObject project, JsonObject snapshot)
    {
        if (project["general"]?["properties"] is not JsonObject properties) return;
        foreach (var (key, value) in snapshot)
            if (properties[key] is JsonObject property) property["value"] = value?.DeepClone();
    }

    internal static void HideFixedPropertyControls(JsonObject properties, IReadOnlyCollection<string>? adjustableKeys)
    {
        foreach (var (key, value) in properties)
            if (value is JsonObject property && !(adjustableKeys?.Contains(key, StringComparer.Ordinal) ?? false))
                property["condition"] = "false";
    }

    internal static void ApplyVisibilityFallbacks(JsonObject scene, JsonObject snapshot)
    {
        // The player can instantiate visibility before applying properties. Only synchronize
        // boolean fallbacks: a scalar slider must not replace a serialized vector such as scale.
        foreach (var binding in SceneAnalyzer.Walk(scene).OfType<JsonObject>().Where(n => n.ContainsKey("user") &&
            n["value"] is JsonValue value && value.TryGetValue<bool>(out _)).ToArray())
            binding["value"] = SceneGraph.Resolve(binding, snapshot);
    }
}
