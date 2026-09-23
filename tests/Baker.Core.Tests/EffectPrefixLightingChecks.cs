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

    private static async Task<string> WriteSourceAsync(string root)
    {
        string source = Path.Combine(root, "lit-prefix-source");
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
            ["general"] = new JsonObject { ["clearcolor"] = "0 0 0", ["clearenabled"] = true, ["ambientcolor"] = "0.5 0.5 0.5",
                ["orthogonalprojection"] = new JsonObject { ["width"] = Size, ["height"] = Size }, ["bloom"] = false },
            ["objects"] = new JsonArray(new JsonObject
            {
                ["id"] = Owner, ["name"] = "lit", ["image"] = "models/lit.json", ["origin"] = $"{Size / 2} {Size / 2} 0",
                ["size"] = $"{Size} {Size}", ["visible"] = true,
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
                texture[i] = (byte)(x * 4); texture[i + 1] = (byte)(y * 4); texture[i + 2] = 200; texture[i + 3] = 255;
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
            ("prefix", "gl_FragColor = vec4(c.g, c.r, c.b, c.a);"), ("suffix", "gl_FragColor = vec4(c.rgb * vec3(1.0, 0.9, 0.8), c.a);") })
        {
            await Write($"effects/{name}.json", $$"""{"name":"{{name}}","passes":[{"material":"materials/{{name}}.json"}]}""");
            await Write($"materials/{name}.json", $$"""{"passes":[{"shader":"{{name}}","blending":"normal","cullmode":"nocull","depthtest":"disabled","depthwrite":"disabled"}]}""");
            await Write($"shaders/{name}.vert", Vertex);
            await Write($"shaders/{name}.frag", "// Original WpeBaker test shader, MIT.\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main(){ vec4 c = texSample2D(g_Texture0, v_TexCoord); " + body + " }\n");
        }
        return source;
    }

    /// <summary>从源工程复制出候选工程，把前缀换成缓存；返回候选工程目录。</summary>
    private static async Task<string> AssembleCandidateAsync(string root, string sourceDirectory, string cacheFile)
    {
        using var source = new ProjectSource(sourceDirectory);
        JsonObject scene = source.ReadJson(source.SceneResource), derived = scene.DeepClone().AsObject();
        string candidate = Path.Combine(root, "lit-prefix-candidate");
        await source.ExtractAsync(candidate);
        await EffectPrefixCache.ApplyAsync(source, scene, derived, candidate, Owner, 1, cacheFile, Size, Size, rgbaFrame: true,
            sourceWidth: Size, sourceHeight: Size);
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
}
