using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Caches an image's source-side effect prefix and retains its later effects and deformation.</summary>
internal static class EffectPrefixCache
{
    internal sealed record Result(string TextureResource, string MaterialResource, string ModelResource);

    internal static HashSet<string> FixedPropertyKeys(IEnumerable<JsonObject> caches) => caches
        .SelectMany(cache => cache["fixed_user_properties"]?.AsObject()?.Select(pair => pair.Key) ?? [])
        .ToHashSet(StringComparer.Ordinal);

    internal static async Task<Result> ApplyAsync(ProjectSource source, JsonObject originalScene, JsonObject derivedScene,
        string outputProject, int ownerId, int prefixEffectCount, string cacheFile, uint width, uint height, bool rgbaFrame,
        CancellationToken cancellationToken = default, uint? sourceWidth = null, uint? sourceHeight = null, bool packedAlpha = false,
        EncodedContentRegion? paddedContent = null)
    {
        if (paddedContent is { } declared && (rgbaFrame || declared.OffsetX < 0 || declared.OffsetY < 0 || declared.Width <= 0 || declared.Height <= 0 ||
            declared.OffsetX + declared.Width > declared.PaddedWidth || declared.OffsetY + declared.Height > declared.PaddedHeight ||
            (long)declared.PaddedWidth * (packedAlpha && !HardwareDecodeDimensions.StackedVertically(packedAlpha, declared.PaddedWidth) ? 2 : 1) != width ||
            (long)declared.PaddedHeight * (HardwareDecodeDimensions.StackedVertically(packedAlpha, declared.PaddedWidth) ? 2 : 1) != height))
            throw new ArgumentException("Padded cache content must lie inside the stored video canvas.");
        ValidateSource(source, originalScene, derivedScene, ownerId, prefixEffectCount);
        JsonObject original = Owner(originalScene, ownerId), derived = Owner(derivedScene, ownerId);
        if ((sourceWidth is null) != (sourceHeight is null) || sourceWidth is 0 || sourceHeight is 0)
            throw new ArgumentException("Source width and height must be supplied together as positive values.");
        string sourceModel = original["image"]!.GetValue<string>();
        JsonArray effects = original["effects"]!.AsArray();
        JsonObject model = source.ReadJson(sourceModel);
        string sourceMaterial = model["material"]!.GetValue<string>();
        JsonObject material = source.ReadJson(sourceMaterial);

        string stem = $"wpe_baker_effect_prefix/{ownerId}";
        string textureResource = stem + "/cache", materialResource = "materials/" + stem + ".json", modelResource = "models/" + stem + ".json";
        string texturePath = ProjectSource.ContainedPath(outputProject, "materials/" + textureResource + ".tex");
        string materialPath = ProjectSource.ContainedPath(outputProject, materialResource);
        string modelPath = ProjectSource.ContainedPath(outputProject, modelResource);
        foreach (string path in new[] { texturePath, materialPath, modelPath })
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Effect-prefix cache output already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
        if (rgbaFrame)
            await TextureContainer.WriteRgbaAsync(texturePath, width, height, await File.ReadAllBytesAsync(cacheFile, cancellationToken), cancellationToken);
        else await TextureContainer.WriteVideoAsync(texturePath, cacheFile, width, height, cancellationToken);

        JsonObject cachedMaterial = material.DeepClone().AsObject();
        JsonObject cachedPass = cachedMaterial["passes"]!.AsArray()[0]!.AsObject();
        cachedPass["textures"]!.AsArray()[0] = textureResource;
        // 缓存截在前缀末个特效之后，基础 pass（genericimage3/4 按这些组合开关做的光照、反射、雾、精灵帧等逐像素运算）
        // 已经烘在里面；替代材质的基础 pass 只能原样取样，否则会再算一遍。作者写的组合开关全部去掉，
        // 两个着色器里唯一默认开启的 FOG 显式关掉。
        cachedPass["combos"] = new JsonObject { ["FOG"] = 0 };
        JsonObject cachedModel = model.DeepClone().AsObject();
        cachedModel["material"] = materialResource;
        if (sourceWidth is uint logicalWidth) {
            cachedModel["autosize"] = false;
            cachedModel["width"] = logicalWidth; cachedModel["height"] = sourceHeight!.Value;
        }
        await VideoSceneBuilder.WriteJsonAsync(materialPath, cachedMaterial, cancellationToken);
        await VideoSceneBuilder.WriteJsonAsync(modelPath, cachedModel, cancellationToken);
        derived["image"] = modelResource;
        for (int i = 0; i < prefixEffectCount; ++i) derived["effects"]!.AsArray().RemoveAt(0);
        if (packedAlpha || paddedContent is not null)
        {
            int effectId = checked(SceneAnalyzer.Walk(derivedScene).OfType<JsonObject>()
                .Where(node => node["id"] is JsonValue value && value.TryGetValue<int>(out _))
                .Select(node => node["id"]!.GetValue<int>()).DefaultIfEmpty(0).Max() + 1);
            derived["effects"]!.AsArray().Insert(0, await WriteAlphaDecoderAsync(outputProject, stem, effectId, width, cancellationToken,
                height, packedAlpha, paddedContent));
        }
        return new(textureResource, materialResource, modelResource);
    }

    /// <summary>
    /// 解码片段着色器。未补边时与历史输出逐字相同；补边时把 UV 映射回每半幅里的内容矩形，
    /// 并夹在矩形内半个纹素，双线性取样不会混进补边。内容居中放置，纹理上下或左右翻转时矩形 UV 不变。
    /// </summary>
    internal static string DecoderFragment(uint storedWidth, uint storedHeight, bool packedAlpha, EncodedContentRegion? content)
    {
        if (content is null)
        {
            if (!packedAlpha) throw new ArgumentException("An unpadded opaque cache needs no decoder.");
            double edge = .5 / storedWidth;
            return FormattableString.Invariant($"// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main(){{ vec3 rgb=texSample2D(g_Texture0,vec2(clamp(v_TexCoord.x*0.5,{edge:R},{.5-edge:R}),v_TexCoord.y)).rgb; float a=texSample2D(g_Texture0,vec2(clamp(v_TexCoord.x*0.5+0.5,{.5+edge:R},{1-edge:R}),v_TexCoord.y)).r; gl_FragColor=vec4(rgb,a); }}\n");
        }
        bool below = HardwareDecodeDimensions.StackedVertically(packedAlpha, content.PaddedWidth);
        double halfX = packedAlpha && !below ? .5 : 1, halfY = below ? .5 : 1;
        double left = halfX * content.OffsetX / content.PaddedWidth, spanX = halfX * content.Width / content.PaddedWidth;
        double top = halfY * content.OffsetY / content.PaddedHeight, spanY = halfY * content.Height / content.PaddedHeight;
        double edgeX = .5 / storedWidth, edgeY = .5 / storedHeight;
        string x = FormattableString.Invariant($"clamp({left:R}+v_TexCoord.x*{spanX:R},{left + edgeX:R},{left + spanX - edgeX:R})");
        string y = FormattableString.Invariant($"clamp({top:R}+v_TexCoord.y*{spanY:R},{top + edgeY:R},{top + spanY - edgeY:R})");
        string alpha = !packedAlpha ? "1.0" : below ? $"texSample2D(g_Texture0,vec2({x},0.5+{y})).r" : $"texSample2D(g_Texture0,vec2(0.5+{x},{y})).r";
        return $"// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main(){{ vec3 rgb=texSample2D(g_Texture0,vec2({x},{y})).rgb; float a={alpha}; gl_FragColor=vec4(rgb,a); }}\n";
    }

    private static async Task<JsonObject> WriteAlphaDecoderAsync(string project, string stem, int id, uint storedWidth,
        CancellationToken cancellationToken, uint storedHeight = 0, bool packedAlpha = true, EncodedContentRegion? content = null)
    {
        string shader = stem + "/decode", material = "materials/" + shader + ".json", effect = "effects/" + shader + ".json";
        string vertex = "// SPDX-License-Identifier: MIT\nuniform mat4 g_ModelViewProjectionMatrix;\nattribute vec3 a_Position;\nattribute vec2 a_TexCoord;\nvarying vec2 v_TexCoord;\nvoid main(){ gl_Position=mul(vec4(a_Position,1.0),g_ModelViewProjectionMatrix); v_TexCoord=a_TexCoord; }\n";
        string fragment = DecoderFragment(storedWidth, storedHeight, packedAlpha, content);
        string vertexPath = ProjectSource.ContainedPath(project, "shaders/" + shader + ".vert");
        foreach (string resource in new[] { "shaders/" + shader + ".vert", "shaders/" + shader + ".frag", material, effect })
        {
            string path = ProjectSource.ContainedPath(project, resource);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The alpha decoder would overwrite an existing project resource.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(vertexPath)!);
        await File.WriteAllTextAsync(vertexPath, vertex, cancellationToken);
        await File.WriteAllTextAsync(ProjectSource.ContainedPath(project, "shaders/" + shader + ".frag"), fragment, cancellationToken);
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, material), new JsonObject {
            ["passes"] = new JsonArray(new JsonObject { ["shader"] = shader, ["blending"] = "normal",
                ["depthtest"] = "disabled", ["depthwrite"] = "disabled", ["cullmode"] = "nocull" }) }, cancellationToken);
        await VideoSceneBuilder.WriteJsonAsync(ProjectSource.ContainedPath(project, effect), new JsonObject {
            ["name"] = "RGBA cache input", ["passes"] = new JsonArray(new JsonObject { ["material"] = material }) }, cancellationToken);
        // Decode as an ordinary first effect. The original base material must remain genericimage:
        // the engine clones it for the later puppet pass, where decoding a second time would be wrong.
        return new JsonObject { ["id"] = id, ["file"] = effect, ["name"] = "RGBA cache input", ["visible"] = true };
    }

    internal static void ValidateSource(ProjectSource source, JsonObject originalScene, JsonObject derivedScene, int ownerId, int prefixEffectCount)
    {
        JsonObject original = Owner(originalScene, ownerId), derived = Owner(derivedScene, ownerId);
        if (!JsonNode.DeepEquals(original, derived)) throw new InvalidDataException("The derived owner changed before prefix-cache replacement.");
        if (original["image"]?.GetValue<string>() is not string sourceModel || string.IsNullOrWhiteSpace(sourceModel) ||
            prefixEffectCount <= 0 || original["effects"] is not JsonArray effects || prefixEffectCount > effects.Count)
            throw new InvalidDataException("Effect-prefix caching requires an image owner and a non-empty contiguous effect prefix.");
        JsonObject model = source.ReadJson(sourceModel);
        bool retainsPuppet = model["puppet"] is JsonValue puppetValue && puppetValue.TryGetValue<string>(out string? puppet) && source.Contains(puppet);
        JsonObject checkedOwner = original.DeepClone().AsObject();
        if (retainsPuppet) checkedOwner.Remove("animationlayers");
        if (model.ContainsKey("puppet") && !retainsPuppet)
            throw new InvalidDataException("A retained puppet must be a project-owned source resource.");
        if (!Neutral(original["alpha"], 1) || !NeutralColor(original["color"]) || !NeutralVector(original["scale"], 1) ||
            !NeutralVector(original["angles"], 0) || !NeutralAnchor(original["anchor"]) || original["crop"] is not null ||
            original.ContainsKey("puppet") || !retainsPuppet && original.ContainsKey("animationlayers") || HasScriptOrAnimation(checkedOwner) ||
            effects.Any(effect => effect is not JsonObject) || !source.Contains(sourceModel))
            throw new InvalidDataException("Effect-prefix caching requires an unscripted flat project-owned owner with neutral appearance and geometry.");
        if (model["autosize"]?.GetValue<bool>() != true || model["cropoffset"] is not null && model["cropoffset"]?.GetValue<string>() != "0 0" ||
            model["material"]?.GetValue<string>() is not string sourceMaterial || string.IsNullOrWhiteSpace(sourceMaterial) ||
            HasScriptOrAnimation(model) || !source.Contains(sourceMaterial))
            throw new InvalidDataException("Effect-prefix caching requires a flat autosized source model with a project-owned material.");
        JsonObject material = source.ReadJson(sourceMaterial);
        if (HasScriptOrAnimation(material) || SceneAnalyzer.Walk(material).OfType<JsonObject>().Any(node =>
                node["use_puppet"] is JsonNode flag && flag.ToJsonString() != "false") ||
            material["passes"] is not JsonArray { Count: 1 } materialPasses ||
            materialPasses[0] is not JsonObject materialPass || materialPass["shader"]?.GetValue<string>() is not ("genericimage3" or "genericimage4") ||
            materialPass["textures"] is not JsonArray { Count: 1 } textures || textures[0]?.GetValue<string>() is not string sourceTexture || IsFramebuffer(sourceTexture))
            throw new InvalidDataException("Effect-prefix caching requires one genericimage3/4 material pass with one non-framebuffer base texture.");
        for (int index = 0; index < effects.Count; ++index) RejectUnsafeEffect(source, effects[index]!.AsObject(), index < prefixEffectCount);
    }

    private static JsonObject Owner(JsonObject scene, int ownerId) => scene["objects"]?.AsArray().OfType<JsonObject>()
        .SingleOrDefault(obj => SceneGraph.Id(obj) == ownerId)
        ?? throw new InvalidDataException("Effect-prefix cache owner is absent from the scene.");

    private static bool HasScriptOrAnimation(JsonNode node) => SceneAnalyzer.Walk(node).OfType<JsonObject>()
        .Any(value => value.ContainsKey("script") || value.ContainsKey("animation") || value.ContainsKey("animations") || value.ContainsKey("animationlayers"));

    private static bool Neutral(JsonNode? value, double neutral) => value is null || value is JsonValue scalar &&
        scalar.TryGetValue<double>(out double number) && number == neutral;

    private static bool NeutralColor(JsonNode? value)
    {
        if (value is null) return true;
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out string? text)) return false;
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is 3 or 4 && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number == 1);
    }

    private static bool NeutralVector(JsonNode? value, double neutral)
    {
        if (value is null) return true;
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out string? text)) return false;
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number == neutral);
    }

    private static bool NeutralAnchor(JsonNode? value) => value is null || value is JsonValue scalar &&
        scalar.TryGetValue<string>(out string? anchor) && anchor == "none";

    private static bool IsFramebuffer(string texture) => texture is "_rt_default" or "_rt_FullFrameBuffer";

    private static void RejectUnsafeEffect(ProjectSource source, JsonObject effect, bool captured)
    {
        if (HasScriptOrAnimation(effect) || effect["file"]?.GetValue<string>() is not string effectResource ||
            !source.Contains(effectResource)) throw new InvalidDataException("Effect-prefix caching requires project-owned static effects.");
        JsonObject definition = source.ReadJson(effectResource);
        if (HasScriptOrAnimation(definition) || definition["passes"] is not JsonArray passes)
            throw new InvalidDataException("Effect-prefix caching requires static effect definitions.");
        foreach (JsonObject pass in passes.OfType<JsonObject>())
        {
            if (pass["material"]?.GetValue<string>() is not string materialResource || !source.Contains(materialResource))
                throw new InvalidDataException("Effect-prefix caching requires project-owned effect materials.");
            JsonObject material = source.ReadJson(materialResource);
            if (captured && SceneAnalyzer.Walk(material).OfType<JsonObject>().Any(node =>
                node["use_puppet"] is JsonNode value && value.ToJsonString() != "false"))
                throw new InvalidDataException("A captured prefix must run before puppet deformation.");
            if (HasScriptOrAnimation(material) || material["passes"] is not JsonArray materialPasses ||
                materialPasses.OfType<JsonObject>().Any(materialPass => materialPass["textures"] is JsonArray textures &&
                    textures.OfType<JsonValue>().Any(texture => texture.TryGetValue<string>(out string? name) && IsFramebuffer(name))))
                throw new InvalidDataException("Effect-prefix caching rejects scripted or framebuffer-dependent effects.");
        }
    }
}
