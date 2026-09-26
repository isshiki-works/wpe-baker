using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 成品工程的写出：视频/静态图层（TEX 纹理、材质、模型、需要时的着色器）、装配好的 scene.json，
/// 以及改过标题与属性控件的 project.json。对象表本身由 <see cref="SceneAssembler"/> 装配。
/// </summary>
internal static class ProjectWriter
{
    private const string Vertex = "// SPDX-License-Identifier: MIT\nuniform mat4 g_ModelViewProjectionMatrix;\nattribute vec3 a_Position;\nattribute vec2 a_TexCoord;\nvarying vec2 v_TexCoord;\nvarying vec3 v_ScreenPos;\nvoid main() { gl_Position = mul(vec4(a_Position, 1.0), g_ModelViewProjectionMatrix); v_TexCoord = a_TexCoord; v_ScreenPos = gl_Position.xyw;\n#ifdef HLSL\nv_ScreenPos.y = -v_ScreenPos.y;\n#endif\n}\n";
    private const string OpaqueFragment = "// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main() { gl_FragColor = vec4(texSample2D(g_Texture0, v_TexCoord).rgb, 1.0); }\n";

    /// <summary>写出一个视频（或静态 RGBA）图层的全部资源，返回要放进场景对象表的图层对象。</summary>
    public static async Task<JsonObject> WriteLayerAsync(string project, string stem, string videoFile,
        uint videoWidth, uint videoHeight, int id, double x, double y, double drawWidth, double drawHeight,
        CancellationToken cancellationToken = default, bool packedAlpha = false,
        double parallaxDepthX = 0, double parallaxDepthY = 0, double geometryOffsetX = 0, double geometryOffsetY = 0,
        bool rgbaFrame = false, bool capturedColor = false, double hdrScale = 1, bool alphaBelow = false, bool additiveLight = false)
    {
        string textureStem = "wpe_baker_video/" + stem;
        string texturePath = ProjectSource.ContainedPath(project, $"materials/{textureStem}.tex");
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
            string vertex = Vertex;
            string fragment = "// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\n" +
                (readsFramebuffer ? "uniform sampler2D g_Texture1; // {\"hidden\":true,\"default\":\"_rt_FullFrameBuffer\"}\nvarying vec2 v_TexCoord;\nvarying vec3 v_ScreenPos;\n" : "varying vec2 v_TexCoord;\n") +
                "void main() { vec3 rgb = texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5, v_TexCoord.y)).rgb; float a = texSample2D(g_Texture0, vec2(v_TexCoord.x * 0.5 + 0.5, v_TexCoord.y)).r; " +
                (readsFramebuffer ? "vec2 uv = (v_ScreenPos.xy / v_ScreenPos.z) * 0.5 + 0.5; vec3 background = texSample2D(g_Texture1, uv).rgb; gl_FragColor = vec4(rgb + background * (1.0 - a), 1.0); }\n"
                    : "gl_FragColor = vec4(rgb / max(a, 0.004), a); }\n");
            // Keep the node at the original camera center so parallax contributes only the
            // pointer displacement. Cropping is a vertex offset, not a new parallax origin.
            string offset = FormattableString.Invariant($"vec4(a_Position + vec3({geometryOffsetX * videoWidth / drawWidth:R}, {geometryOffsetY * videoHeight / drawHeight:R}, 0.0), 1.0)");
            vertex = vertex.Replace("vec4(a_Position, 1.0)", offset, StringComparison.Ordinal);
            if (!packedAlpha) fragment = OpaqueFragment;
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
        await WriteMaterialAsync(project, stem, shader,
            readsFramebuffer ? new JsonArray(textureStem, "_rt_FullFrameBuffer") : new JsonArray(textureStem), autosize: true, cancellationToken);
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        return new JsonObject {
            ["id"] = id, ["name"] = (rgbaFrame ? "Static " : "Video ") + stem, ["image"] = modelResource,
            ["origin"] = $"{Number(x)} {Number(y)} 0", ["angles"] = "0 0 0",
            ["scale"] = $"{Number(drawWidth / videoWidth)} {Number(drawHeight / videoHeight)} 1",
            ["size"] = $"{videoWidth} {videoHeight}", ["visible"] = true, ["alpha"] = 1,
            ["parallaxDepth"] = $"{Number(parallaxDepthX)} {Number(parallaxDepthY)}" };
    }

    /// <summary>材质与模型（models/wpe_baker_video/&lt;stem&gt;.json）。</summary>
    private static async Task WriteMaterialAsync(string project, string stem, string shader, JsonArray textures, bool autosize,
        CancellationToken cancellationToken)
    {
        string materialResource = $"materials/wpe_baker_video/{stem}.json";
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, materialResource), new JsonObject {
            ["passes"] = new JsonArray(new JsonObject {
                ["shader"] = shader, ["blending"] = "translucent", ["cullmode"] = "nocull",
                ["depthtest"] = "disabled", ["depthwrite"] = "disabled",
                ["combos"] = new JsonObject { ["LIGHTING"] = 0, ["REFLECTION"] = 0, ["FOG"] = 0 },
                ["textures"] = textures }) }, cancellationToken);
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, $"models/wpe_baker_video/{stem}.json"), new JsonObject {
            ["material"] = materialResource, ["autosize"] = autosize, ["cropoffset"] = "0 0" }, cancellationToken);
    }

    /// <summary>
    /// 视频枢纽（runs/VLAYERCOST）：核显每帧的固定开销按"采样视频纹理的绘制次数"计（低负载下每次约 0.46 W），与路数、分辨率、面积无关。
    /// 加一个隐藏的枢纽层，一次绘制把各视频上下堆进自己的合成目标；各视频层留在原位、着色器照旧，只改为采样目标里自己那一块。
    /// 官方 WPE 上枢纽层与视频层都要关 autosize（否则枢纽按第一张纹理定尺寸、视频层按渲染目标定四边形）；
    /// 块间空 4 行（只靠半纹素夹取，块边仍有 1 像素亮线）。并进去的不足 2 层时不写，返回 null；否则返回枢纽对象，放在对象表最前面。
    /// </summary>
    internal static async Task<JsonObject?> WriteVideoHubAsync(string project, IEnumerable<JsonObject> videos, int id,
        CancellationToken cancellationToken)
    {
        const int gap = 4, limit = 8192;
        var blocks = new List<(JsonObject Video, string Stem, int Top, int Width, int Height)>();
        int y = 0;
        foreach (var video in videos)
        {
            int[] size = video["size"]!.GetValue<string>().Split(' ').Select(int.Parse).ToArray();
            if (size[0] > limit || y + size[1] > limit) continue;   // 放不下的仍是独立视频层
            blocks.Add((video, Path.GetFileNameWithoutExtension(video["image"]!.GetValue<string>()), y, size[0], size[1]));
            y += size[1] + gap;
        }
        if (blocks.Count < 2) return null;
        int width = blocks.Max(b => b.Width), height = y - gap;
        string Shader(string stem, string extension) => ProjectSource.ContainedPath(project, $"shaders/wpe_baker_video/{stem}.{extension}");
        var hub = new StringBuilder("// SPDX-License-Identifier: MIT\n");
        for (int i = 0; i < blocks.Count; ++i) hub.Append(CultureInfo.InvariantCulture, $"uniform sampler2D g_Texture{i};\n");
        hub.Append(CultureInfo.InvariantCulture, $"varying vec2 v_TexCoord;\nvoid main() {{\nvec2 p = v_TexCoord * vec2({width}.0, {height}.0);\nvec3 c = vec3(0.0, 0.0, 0.0);\n");
        for (int i = 0; i < blocks.Count; ++i)
        {
            var (_, _, top, w, h) = blocks[i];
            hub.Append(CultureInfo.InvariantCulture, $"vec3 c{i} = texSample2D(g_Texture{i}, vec2(p.x / {w}.0, (p.y - {top}.0) / {h}.0)).rgb;\n" +
                $"c = (p.y >= {top}.0 && p.y < {top + h}.0 && p.x < {w}.0) ? c{i} : c;\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Shader("hub", "vert"))!);
        await File.WriteAllTextAsync(Shader("hub", "vert"), Vertex, cancellationToken);
        await File.WriteAllTextAsync(Shader("hub", "frag"), hub.Append("gl_FragColor = vec4(c, 1.0); }\n").ToString(), cancellationToken);
        await WriteMaterialAsync(project, "hub", "wpe_baker_video/hub",
            new JsonArray(blocks.Select(b => (JsonNode)JsonValue.Create("wpe_baker_video/" + b.Stem)).ToArray()), autosize: false, cancellationToken);
        // 空效果给枢纽层一个自己的合成目标：原样拷贝的一道 pass，文件都写进工程。借用自带的 effects/opacity 时，
        // 它的材质只在资源目录的效果文件夹里，本仓库引擎按工程/资源根目录找不到，成品在合成门渲染时失败。
        await File.WriteAllTextAsync(Shader("hubcopy", "vert"), Vertex, cancellationToken);
        await File.WriteAllTextAsync(Shader("hubcopy", "frag"), OpaqueFragment, cancellationToken);
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, "materials/wpe_baker_video/hubcopy.json"), new JsonObject {
            ["passes"] = new JsonArray(new JsonObject { ["shader"] = "wpe_baker_video/hubcopy", ["blending"] = "normal",
                ["depthtest"] = "disabled", ["depthwrite"] = "disabled", ["cullmode"] = "nocull" }) }, cancellationToken);
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, "effects/wpe_baker_video/hubcopy.json"), new JsonObject {
            ["name"] = "Video hub copy", ["passes"] = new JsonArray(new JsonObject { ["material"] = "materials/wpe_baker_video/hubcopy.json" }) },
            cancellationToken);
        foreach (var (video, stem, top, w, h) in blocks)
        {
            var textures = JsonNode.Parse(await File.ReadAllTextAsync(ProjectSource.ContainedPath(project, $"materials/wpe_baker_video/{stem}.json"),
                cancellationToken))!["passes"]![0]!["textures"]!.DeepClone().AsArray();
            textures[0] = $"_rt_imageLayerComposite_{id}_a";
            // 用 genericimage4 的层（不透明、无偏移）换成等价的最小着色器，才能改采样坐标。
            string fragment = File.Exists(Shader(stem, "frag")) ? await File.ReadAllTextAsync(Shader(stem, "frag"), cancellationToken) : OpaqueFragment;
            if (!File.Exists(Shader(stem, "vert"))) await File.WriteAllTextAsync(Shader(stem, "vert"), Vertex, cancellationToken);
            await File.WriteAllTextAsync(Shader(stem, "frag"), BlockSample(fragment, top, w, h, width, height), cancellationToken);
            File.Delete(ProjectSource.ContainedPath(project, $"materials/wpe_baker_video/{stem}.json"));
            File.Delete(ProjectSource.ContainedPath(project, $"models/wpe_baker_video/{stem}.json"));
            await WriteMaterialAsync(project, stem, "wpe_baker_video/" + stem, textures, autosize: false, cancellationToken);
            video["dependencies"] = new JsonArray(id);
        }
        return new JsonObject {
            ["id"] = id, ["name"] = "Video hub", ["image"] = "models/wpe_baker_video/hub.json",
            ["origin"] = FormattableString.Invariant($"{width / 2d:R} {height / 2d:R} 0"), ["angles"] = "0 0 0", ["scale"] = "1 1 1",
            ["size"] = $"{width} {height}", ["visible"] = false, ["alpha"] = 1, ["parallaxDepth"] = "0 0",
            ["effects"] = new JsonArray(new JsonObject { ["file"] = "effects/wpe_baker_video/hubcopy.json",
                ["passes"] = new JsonArray(new JsonObject { ["combos"] = null, ["constantshadervalues"] = null }) }) };
    }

    /// <summary>texSample2D(g_Texture0, X) 换成采样图集里这一块：X 先夹在块内半个纹素，再仿射到块的位置。</summary>
    private static string BlockSample(string fragment, int top, int w, int h, int width, int height)
    {
        const string key = "texSample2D(g_Texture0, ";
        var result = new StringBuilder();
        int i = 0;
        for (int j; (j = fragment.IndexOf(key, i, StringComparison.Ordinal)) >= 0;)
        {
            int k = j + key.Length;
            for (int depth = 1; depth > 0; ++k) depth += fragment[k] switch { '(' => 1, ')' => -1, _ => 0 };
            result.Append(fragment, i, j - i).Append(CultureInfo.InvariantCulture,
                $"{key}vec2(0.0, {(double)top / height:R}) + clamp({fragment[(j + key.Length)..(k - 1)]}, vec2({.5 / w:R}, {.5 / h:R}), " +
                $"vec2({1 - .5 / w:R}, {1 - .5 / h:R})) * vec2({(double)w / width:R}, {(double)h / height:R}))");
            i = k;
        }
        return result.Append(fragment, i, fragment.Length - i).ToString();
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
