using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// fix-prefix-lighting：前缀缓存截在前缀末个特效之后，基础 pass 按组合开关做的逐像素运算（光照等）已经烘在缓存里，
/// 替代材质不能再算一遍。合成夹具：宿主图层的基础材质叫 genericimage4（夹具自带的着色器，与官方同名、同样声明
/// LIGHTING 默认关、FOG 默认开，各自把颜色改一个明显的量），作者写了 LIGHTING=1；前缀 1 个特效，后面留 1 个特效。
/// L1 查写出的替代材质；L3 用真渲染器截缓存、装候选工程，与源工程整帧比。
/// </summary>
internal static class EffectPrefixLightingChecks
{
    private const uint Size = 64;
    private const int Owner = 1, PrefixEffect = 11;
    private const string Vertex = "// Original WpeBaker test shader, MIT.\nuniform mat4 g_ModelViewProjectionMatrix;\nattribute vec3 a_Position;\nattribute vec2 a_TexCoord;\nvarying vec2 v_TexCoord;\nvoid main(){ gl_Position=mul(vec4(a_Position,1.0),g_ModelViewProjectionMatrix); v_TexCoord=a_TexCoord; }\n";

    private static async Task<string> WriteSourceAsync(string root, bool sharp = false, bool hdr = false, bool signed = false,
        bool spanOne = false)
    {
        string source = Path.Combine(root, spanOne ? "span-one-prefix-source" : signed ? "signed-prefix-source" : hdr ? "hdr-prefix-source" :
            sharp ? "sharp-prefix-source" : "lit-prefix-source");
        async Task Write(string relative, string text)
        {
            string path = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
        }
        await Write("project.json", """{"type":"scene","file":"scene.json","title":"lit effect-prefix fixture"}""");
        await Write("scene.json", new JsonObject
        {
            ["camera"] = new JsonObject { ["center"] = "0 0 0", ["eye"] = "0 0 1", ["up"] = "0 1 0" },
            ["general"] = new JsonObject { ["clearcolor"] = "0 0 0", ["clearenabled"] = true,
                ["ambientcolor"] = hdr ? "1 1 1" : "0.5 0.5 0.5", ["hdr"] = hdr,
                ["orthogonalprojection"] = new JsonObject { ["width"] = Size, ["height"] = Size }, ["bloom"] = hdr },
            ["objects"] = new JsonArray(new JsonObject
            {
                ["id"] = Owner, ["name"] = "lit", ["image"] = "models/lit.json", ["origin"] = $"{Size / 2} {Size / 2} 0",
                ["size"] = $"{Size} {Size}", ["visible"] = true, ["brightness"] = hdr && !signed ? 1.64 : 1.0,
                ["effects"] = new JsonArray(
                    new JsonObject { ["id"] = PrefixEffect, ["file"] = "effects/prefix.json", ["visible"] = true },
                    new JsonObject { ["id"] = 12, ["file"] = "effects/suffix.json", ["visible"] = true })
            })
        }.ToJsonString());
        await Write("models/lit.json", """{"material":"materials/lit.json","autosize":true}""");
        // VERSION 是真实作品里常见、对着色器无作用的作者开关：替代材质应当连它一起去掉。
        await Write("materials/lit.json", """{"passes":[{"shader":"genericimage4","textures":["lit-color"],"blending":"normal","cullmode":"nocull","depthtest":"disabled","depthwrite":"disabled","combos":{"LIGHTING":1,"VERSION":2}}]}""");
        byte[] texture = new byte[Size * Size * 4];
        for (int y = 0; y < Size; ++y)
            for (int x = 0; x < Size; ++x)
            {
                int i = (y * (int)Size + x) * 4;
                if (sharp)
                {
                    texture[i] = (byte)(x % 8 == 0 || y % 8 == 0 ? 255 : 0);
                    texture[i + 1] = (byte)(x % 8 == 0 ? 255 : 0);
                    texture[i + 2] = (byte)(y % 8 == 0 ? 255 : 0);
                    texture[i + 3] = (byte)(x % 8 == 0 ? 96 : 255);
                }
                else
                {
                    texture[i] = (byte)(x * 4); texture[i + 1] = (byte)(y * 4); texture[i + 2] = 200; texture[i + 3] = 255;
                }
            }
        await TextureContainer.WriteRgbaAsync(Path.Combine(source, "materials", "lit-color.tex"), Size, Size, texture);
        await Write("shaders/genericimage4.vert", Vertex);
        await Write("shaders/genericimage4.frag", """
            // Original WpeBaker test shader, MIT.
            // [COMBO] {"material":"ui_editor_properties_lighting","combo":"LIGHTING","default":0}
            // [COMBO] {"material":"ui_editor_properties_fog","combo":"FOG","default":1}
            uniform sampler2D g_Texture0;
            uniform vec3 g_LightAmbientColor;
            varying vec2 v_TexCoord;
            void main() {
                vec4 color = texSample2D(g_Texture0, v_TexCoord);
            #if LIGHTING
                color.rgb *= g_LightAmbientColor;
            #endif
            #if FOG
                color.rgb = color.rgb * 0.75 + vec3(0.0, 0.0, 0.25);
            #endif
                gl_FragColor = color;
            }
            """);
        // 夹具目录同时当 assets：带特效的图层最后一步要 materials/util/effectpassthrough.json（官方版也是 genericimage3 原样取样）。
        await Write("materials/util/effectpassthrough.json", """{"passes":[{"shader":"genericimage3","blending":"normal","depthtest":"disabled","depthwrite":"disabled","cullmode":"nocull"}]}""");
        await Write("shaders/genericimage3.vert", Vertex);
        await Write("shaders/genericimage3.frag", "// Original WpeBaker test shader, MIT.\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main(){ gl_FragColor = texSample2D(g_Texture0, v_TexCoord); }\n");
        foreach ((string name, string body) in new[] {
            ("prefix", spanOne ? "gl_FragColor = vec4(c.rgb * 0.75 - 0.2, c.a);" :
                signed ? "gl_FragColor = vec4(c.rgb * 2.5 - 0.5, c.a);" :
                "gl_FragColor = vec4(c.g, c.r, c.b, c.a);"),
            ("suffix", "gl_FragColor = vec4(c.rgb * vec3(1.0, 0.9, 0.8), c.a);") })
        {
            await Write($"effects/{name}.json", $$"""{"name":"{{name}}","passes":[{"material":"materials/{{name}}.json"}]}""");
            await Write($"materials/{name}.json", $$"""{"passes":[{"shader":"{{name}}","blending":"normal","cullmode":"nocull","depthtest":"disabled","depthwrite":"disabled"}]}""");
            await Write($"shaders/{name}.vert", Vertex);
            await Write($"shaders/{name}.frag", "// Original WpeBaker test shader, MIT.\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main(){ vec4 c = texSample2D(g_Texture0, v_TexCoord); " + body + " }\n");
        }
        return source;
    }

    /// <summary>从源工程复制出候选工程，把前缀换成缓存；返回候选工程目录。</summary>
    private static async Task<string> AssembleCandidateAsync(string root, string sourceDirectory, string cacheFile,
        bool packedAlpha = false, double hdrScale = 1, double hdrLowerBound = 0, bool hdrSignedSqrt = false)
    {
        using var source = new ProjectSource(sourceDirectory);
        JsonObject scene = source.ReadJson(source.SceneResource), derived = scene.DeepClone().AsObject();
        string candidate = Path.Combine(root, Path.GetFileName(sourceDirectory).Replace("-source", "-candidate", StringComparison.Ordinal));
        await source.ExtractAsync(candidate);
        await EffectPrefixCache.ApplyAsync(source, scene, derived, candidate, Owner, 1, cacheFile,
            packedAlpha ? Size * 2 : Size, Size, rgbaFrame: true, sourceWidth: Size, sourceHeight: Size,
            packedAlpha: packedAlpha, hdrScale: hdrScale, hdrLowerBound: hdrLowerBound, hdrSignedSqrt: hdrSignedSqrt);
        await File.WriteAllTextAsync(Path.Combine(candidate, source.SceneResource), derived.ToJsonString());
        return candidate;
    }

    internal static async Task RunMaterialAsync(Action<bool, string> check, string root)
    {
        string source = await WriteSourceAsync(root);
        string cache = Path.Combine(root, "dummy-cache.rgba");
        await File.WriteAllBytesAsync(cache, new byte[Size * Size * 4]);
        string candidate = await AssembleCandidateAsync(root, source, cache);
        JsonObject pass = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidate, "materials", "wpe_baker_effect_prefix", Owner + ".json")))!
            ["passes"]![0]!.AsObject();
        check(JsonNode.DeepEquals(pass["combos"], new JsonObject { ["FOG"] = 0 }) &&
            pass["textures"]![0]!.GetValue<string>() == $"wpe_baker_effect_prefix/{Owner}/cache" &&
            pass["shader"]!.GetValue<string>() == "genericimage4" && pass["blending"]!.GetValue<string>() == "normal",
            "the prefix-cache material drops every authored base-pass combo and switches off the default-on FOG, keeping shader and blend state");

        foreach (bool puppet in new[] { false, true })
        {
            string fixture = Path.Combine(root, puppet ? "puppet" : "flat");
            string packedSource = await WriteSourceAsync(fixture);
            JsonObject original = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(packedSource, "scene.json")))!.AsObject();
            if (puppet)
            {
                JsonObject model = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(packedSource, "models/lit.json")))!.AsObject();
                model["puppet"] = "models/retained.mdl";
                await File.WriteAllTextAsync(Path.Combine(packedSource, "models/lit.json"), model.ToJsonString());
                await File.WriteAllBytesAsync(Path.Combine(packedSource, "models/retained.mdl"), [1]);
                original["objects"]![0]!["animationlayers"] = new JsonArray(new JsonObject { ["id"] = 13 });
                await File.WriteAllTextAsync(Path.Combine(packedSource, "scene.json"), original.ToJsonString());
            }
            string packedCache = Path.Combine(fixture, "packed.rgba");
            await File.WriteAllBytesAsync(packedCache, new byte[Size * 2 * Size * 4]);
            string packedCandidate = await AssembleCandidateAsync(fixture, packedSource, packedCache, packedAlpha: true);
            JsonObject basePass = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(packedCandidate,
                "materials/wpe_baker_effect_prefix/1.json")))!["passes"]![0]!.AsObject();
            JsonObject owner = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(packedCandidate, "scene.json")))!
                ["objects"]![0]!.AsObject();
            JsonArray effects = owner["effects"]!.AsArray();
            string fragment = await File.ReadAllTextAsync(Path.Combine(packedCandidate, "shaders/wpe_baker_effect_prefix/1/decode.frag"));
            check(effects.Count == (puppet ? 2 : 1) && effects[^1]!["id"]!.GetValue<int>() == 12 &&
                basePass["shader"]!.GetValue<string>() == (puppet ? "genericimage4" : "wpe_baker_effect_prefix/1/decode") &&
                fragment.Contains(puppet ? "g_Texture1" : "g_Texture0", StringComparison.Ordinal) &&
                !fragment.Contains(puppet ? "g_Texture0" : "g_Texture1", StringComparison.Ordinal) &&
                JsonNode.DeepEquals(owner["animationlayers"], original["objects"]![0]!["animationlayers"]),
                "flat packed caches decode in the base; project-owned puppets retain first-effect decoding, animation layers, and the authored suffix");
            if (puppet)
            {
                JsonObject decoder = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(packedCandidate,
                    "materials/wpe_baker_effect_prefix/1/decode.json")))!.AsObject();
                check(decoder["passes"]![0]!["textures"]!.ToJsonString() == "[\"\",\"wpe_baker_effect_prefix/1/cache\"]" &&
                    effects[0]!["file"]!.GetValue<string>() == "effects/wpe_baker_effect_prefix/1/decode.json",
                    "the retained puppet decoder still samples the original cache in slot 1");
            }
        }
    }

    internal static async Task RunRenderAsync(Action<bool, string> check, string root)
    {
        string source = await WriteSourceAsync(root);
        var runner = new NativeRenderRunner(LocalTools.Tools!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        async Task<byte[]> Render(string project, string output, RenderCaptureSelection? capture = null)
        {
            JsonObject result = await runner.RenderRawAsync(new(project, source, Path.Combine(root, output), Size, Size, 60, 1, 1,
                Seed: 17, CaptureTarget: capture), timeout.Token);
            return await File.ReadAllBytesAsync(result["rgba_path"]!.GetValue<string>(), timeout.Token);
        }
        byte[] captured = await Render(source, "capture", new(Owner, PrefixEffect, EffectTerminal: true, ExactExtent: true));
        string cacheFile = Path.Combine(root, "cache.rgba");
        await File.WriteAllBytesAsync(cacheFile, captured, timeout.Token);
        byte[] reference = await Render(source, "reference");
        byte[] candidate = await Render(await AssembleCandidateAsync(root, source, cacheFile), "candidate");
        int worst = 0; long total = 0;
        for (int i = 0; i < reference.Length; ++i)
            if (i % 4 != 3) { int d = Math.Abs(reference[i] - candidate[i]); worst = Math.Max(worst, d); total += d; }
        double mean = total / (reference.Length * .75);
        check(reference.Length == Size * Size * 4 && candidate.Length == reference.Length && worst <= 2 && mean <= .5,
            $"a lit owner's prefix cache already holds the base-pass lighting and fog: candidate matches the source frame (worst {worst}/255, mean {mean:F3}/255)");
    }

    internal static async Task RunDirectSamplerAsync(Action<bool, string> check, string root)
    {
        string source = await WriteSourceAsync(root, sharp: true);
        string scenePath = Path.Combine(source, "scene.json");
        JsonObject scene = JsonNode.Parse(await File.ReadAllTextAsync(scenePath))!.AsObject();
        scene["general"]!["cameraparallax"] = true;
        scene["general"]!["cameraparallaxamount"] = 0.2;
        scene["general"]!["cameraparallaxmouseinfluence"] = 1;
        scene["general"]!["cameraparallaxdelay"] = 0;
        scene["objects"]![0]!["parallaxDepth"] = "1 1";
        await File.WriteAllTextAsync(scenePath, scene.ToJsonString());
        var runner = new NativeRenderRunner(LocalTools.Tools!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        async Task<byte[]> Render(string project, string name, RenderCaptureSelection? capture = null, JsonObject? input = null)
        {
            JsonObject result = await runner.RenderRawAsync(new(project, source, Path.Combine(root, name), Size, Size, 60, 1, 1,
                Seed: 17, CaptureTarget: capture, Input: input), timeout.Token);
            return await File.ReadAllBytesAsync(result["rgba_path"]!.GetValue<string>(), timeout.Token);
        }
        byte[] captured = await Render(source, "sharp-capture", new(Owner, PrefixEffect, EffectTerminal: true, ExactExtent: true));
        byte[] packed = new byte[Size * 2 * Size * 4];
        for (int y = 0; y < Size; ++y)
            for (int x = 0; x < Size; ++x)
            {
                int at = (y * (int)Size + x) * 4, left = (y * (int)(Size * 2) + x) * 4, right = left + (int)Size * 4;
                captured.AsSpan(at, 3).CopyTo(packed.AsSpan(left, 3));
                packed[left + 3] = 255;
                packed[right] = captured[at + 3];
                packed[right + 3] = 255;
            }
        string cache = Path.Combine(root, "sharp-packed.rgba");
        await File.WriteAllBytesAsync(cache, packed, timeout.Token);
        string candidate = await AssembleCandidateAsync(root, source, cache, packedAlpha: true);
        string decoderMaterial = Path.Combine(candidate, "materials", "wpe_baker_effect_prefix", Owner + ".json");
        string decoderShader = Path.Combine(candidate, "shaders", "wpe_baker_effect_prefix", Owner.ToString(), "decode.frag");
        JsonObject material = JsonNode.Parse(await File.ReadAllTextAsync(decoderMaterial, timeout.Token))!.AsObject();
        string shader = await File.ReadAllTextAsync(decoderShader, timeout.Token);
        check(material["passes"]![0]!["textures"]?.ToJsonString() == "[\"wpe_baker_effect_prefix/1/cache\"]" &&
            material["passes"]![0]!["shader"]!.GetValue<string>() == "wpe_baker_effect_prefix/1/decode" &&
            shader.Contains("texSample2D(g_Texture0", StringComparison.Ordinal) && !shader.Contains("g_Texture1", StringComparison.Ordinal),
            "the flat owner's base pass decodes the original packed cache directly in slot 0");
        JsonObject derived = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidate, "scene.json"), timeout.Token))!.AsObject();
        check(derived["objects"]![0]!["effects"]!.AsArray().Count == 1 &&
            derived["objects"]![0]!["effects"]![0]!["id"]!.GetValue<int>() == 12,
            "base decoding leaves only the authored suffix effect");
        byte[] reference = await Render(source, "sharp-reference");
        byte[] direct = await Render(candidate, "sharp-direct");
        byte[] directTerminal = await Render(candidate, "sharp-direct-terminal", new(Owner, 12, EffectTerminal: true, ExactExtent: true));
        static double Mae(byte[] a, byte[] b, int channelCount)
        {
            long total = 0;
            for (int pixel = 0; pixel < a.Length; pixel += 4)
                for (int channel = 0; channel < channelCount; ++channel)
                    total += Math.Abs(a[pixel + channel] - b[pixel + channel]);
            return (double)total / (a.Length / 4 * channelCount);
        }
        double directRgb = Mae(reference, direct, 3);
        double AlphaMae(byte[] a, byte[] b)
        {
            long total = 0;
            for (int pixel = 3; pixel < a.Length; pixel += 4) total += Math.Abs(a[pixel] - b[pixel]);
            return (double)total / (a.Length / 4);
        }
        double directAlpha = AlphaMae(captured, directTerminal);
        check(Enumerable.Range(0, captured.Length / 4).Any(pixel => captured[pixel * 4 + 3] < 255) &&
            directRgb < 2 && directAlpha <= 1,
            $"sharp one-pixel color and alpha grid survives base decode and the retained suffix (RGB MAE {directRgb:F3}; alpha MAE {directAlpha:F3})");
        JsonObject mouse = new() { ["cursor_x"] = 0.2, ["cursor_y"] = 0.8, ["cursor_in_window"] = true };
        byte[] movedReference = await Render(source, "sharp-reference-mouse", input: mouse);
        byte[] movedCandidate = await Render(candidate, "sharp-candidate-mouse", input: mouse.DeepClone().AsObject());
        double mouseResponse = Mae(reference, movedReference, 3), movedRgb = Mae(movedReference, movedCandidate, 3);
        check(mouseResponse > 1 && movedRgb < 2,
            $"enabled camera parallax responds to a non-centred cursor and base decoding follows the same transform " +
            $"(reference mouse response {mouseResponse:F3}; candidate RGB MAE {movedRgb:F3})");
    }

    internal static async Task RunHdrAsync(Action<bool, string> check, string root, string assets, bool signed = false,
        bool spanOne = false)
    {
        string source = await WriteSourceAsync(root, hdr: true, signed: signed, spanOne: spanOne);
        double scale = spanOne ? 1 : signed ? 2.5 : 2, lower = spanOne ? -0.2 : signed ? -0.5 : 0;
        bool signedEncoding = signed || spanOne;
        var runner = new NativeRenderRunner(LocalTools.Tools!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        async Task<byte[]> Render(string project, string name, RenderCaptureSelection? target = null) =>
            await File.ReadAllBytesAsync((await runner.RenderRawAsync(new(project, assets, Path.Combine(root, name),
                Size, Size, 60, 1, 1, Seed: 17, CaptureTarget: target, HdrScale: target is null ? 1 : scale,
                HdrLowerBound: target is null ? 0 : lower, HdrSignedSqrt: target is not null && signedEncoding), timeout.Token))
                ["rgba_path"]!.GetValue<string>(), timeout.Token);
        var target = new RenderCaptureSelection(Owner, PrefixEffect, EffectTerminal: true, ExactExtent: true);
        byte[] scaled = await Render(source, "hdr-scaled-prefix", target);
        string cache = Path.Combine(root, "hdr-scaled.rgba");
        await File.WriteAllBytesAsync(cache, scaled, timeout.Token);
        string candidate = await AssembleCandidateAsync(root, source, cache, hdrScale: scale, hdrLowerBound: lower,
            hdrSignedSqrt: signedEncoding);
        byte[] expected = await Render(source, "hdr-reference");
        byte[] actual = await Render(candidate, "hdr-candidate");
        long rgb = 0, alpha = 0;
        for (int i = 0; i < actual.Length; i += 4)
        {
            for (int c = 0; c < 3; ++c) rgb += Math.Abs(actual[i + c] - expected[i + c]);
            alpha += Math.Abs(actual[i + 3] - expected[i + 3]);
        }
        double rgbMae = (double)rgb / (Size * Size * 3), alphaMae = (double)alpha / (Size * Size);
        check(rgbMae <= 2 && alphaMae <= 1 && (signed || scaled.Where((_, index) => index % 4 != 3).Max() < 255),
            $"the bounded HDR terminal is encoded and restored in the first effect " +
            $"(RGB MAE {rgbMae:F3}, alpha MAE {alphaMae:F3})");
    }


}
