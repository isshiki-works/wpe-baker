// HDR 闭合的 D 类修复夹具：凸组合采样特效不判失败、真超 1 的内容仍判失败、空文字层不计入、前缀回退后按捕获对象重求。
// 着色器全是夹具自带的最小源码，规则表按夹具着色器的指纹现建，不依赖官方 assets。
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Baker.Core;
using Baker.Core.Analysis.EffectRange;
using Xunit;

[Trait("Layer", "L1")]
public class EffectRangeClosureTests
{
    private const string ConvexFrag = "uniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\n" +
        "void main() {\n\tgl_FragColor = texSample2D(g_Texture0, v_TexCoord.xy + vec2(0.01, 0.0));\n}\n";
    // 叠加提亮：c + c·0.64，输入为 1 时输出 1.64。
    private const string AdditiveFrag = "uniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\n" +
        "void main() {\n\tvec4 c = texSample2D(g_Texture0, v_TexCoord.xy);\n\tgl_FragColor = c + c * 0.64;\n}\n";

    // 带条件的夹具：结果依赖头文件里的 ApplyBlending、combo 与常量取值，对应官方 pulse 那类规则。
    private const string PulseFrag = "#include \"common_blending.h\"\nuniform sampler2D g_Texture0;\nuniform float g_PulseAmount;\n" +
        "varying vec2 v_TexCoord;\nvoid main() {\n\tvec4 c = texSample2D(g_Texture0, v_TexCoord.xy);\n" +
        "\tgl_FragColor = vec4(ApplyBlending(BLENDMODE, c.rgb, c.rgb, g_PulseAmount), c.a);\n}\n";
    private const string BlendingHeader = "vec3 ApplyBlending(const int m, vec3 a, vec3 b, float o) { return mix(a, min(a + b, vec3(1.0)), o); }\n";

    private static EffectRangeRules Rules() => EffectRangeRules.Parse(new JsonObject {
        ["rules"] = new JsonArray(new JsonObject {
            ["id"] = "fixture_convex", ["fragment_sha256"] = EffectRangeRules.Fingerprint(ConvexFrag),
            ["forbid_combos"] = new JsonObject { ["BACKGROUND"] = new JsonArray(1) } },
        new JsonObject {
            ["id"] = "fixture_pulse", ["fragment_sha256"] = EffectRangeRules.Fingerprint(PulseFrag),
            ["allow_combos"] = new JsonObject { ["BLENDMODE"] = new JsonObject { ["default"] = 9, ["values"] = new JsonArray(0, 9) } },
            ["includes"] = new JsonObject { ["common_blending.h"] = EffectRangeRules.Fingerprint(BlendingHeader) },
            ["constants"] = new JsonObject {
                ["amount"] = new JsonObject { ["default"] = 1, ["min"] = 0 },
                ["noiseamount"] = new JsonObject { ["default"] = 0, ["min"] = 0 },
                ["power"] = new JsonObject { ["default"] = 1, ["above"] = 0 },
                ["bounds"] = new JsonObject { ["default"] = "0 1", ["increasing"] = true },
                ["tint"] = new JsonObject { ["default"] = "1 1 1", ["min"] = 0, ["max"] = 1 } },
            ["sums"] = new JsonArray(new JsonObject { ["terms"] = new JsonArray("amount", "noiseamount"), ["max"] = 1 }) }) }.ToJsonString());

    private static byte[] Tex8()
    {
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), 0x2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30), 32);
        return bytes;
    }

    private static void Write(string root, string resource, string text) => Write(root, resource, Encoding.UTF8.GetBytes(text));

    private static void Write(string root, string resource, byte[] bytes)
    {
        string path = Path.Combine(root, resource);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void Fixture(string root)
    {
        Write(root, "project.json", "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        Write(root, "scene.json", "{\"general\":{\"hdr\":true},\"objects\":[]}");
        Write(root, "models/layer.json", "{\"material\":\"materials/layer.json\"}");
        Write(root, "materials/layer.json", "{\"passes\":[{\"shader\":\"genericimage2\",\"blending\":\"translucent\",\"textures\":[\"layer\"]}]}");
        Write(root, "materials/layer.tex", Tex8());
        foreach (var (name, frag) in new[] { ("convex", ConvexFrag), ("additive", AdditiveFrag) })
        {
            Write(root, $"effects/{name}/effect.json", $"{{\"passes\":[{{\"material\":\"materials/effects/{name}.json\"}}]}}");
            Write(root, $"materials/effects/{name}.json", $"{{\"passes\":[{{\"shader\":\"effects/{name}\",\"blending\":\"normal\"}}]}}");
            Write(root, $"shaders/effects/{name}.frag", frag);
        }
        // 着色器与凸组合夹具逐字相同，但材质 pass 是叠加混合 / 多一个未证明的 pass：都不能被认领。
        Write(root, "effects/convexadd/effect.json", "{\"passes\":[{\"material\":\"materials/effects/convexadd.json\"}]}");
        Write(root, "materials/effects/convexadd.json", "{\"passes\":[{\"shader\":\"effects/convex\",\"blending\":\"additive\"}]}");
        Write(root, "effects/pulse/effect.json", "{\"passes\":[{\"material\":\"materials/effects/pulse.json\"}]}");
        Write(root, "materials/effects/pulse.json", "{\"passes\":[{\"shader\":\"effects/pulse\",\"blending\":\"normal\"}]}");
        Write(root, "shaders/effects/pulse.frag", PulseFrag);
        Write(root, "shaders/common_blending.h", BlendingHeader);
        Write(root, "effects/convexmulti/effect.json", "{\"passes\":[{\"material\":\"materials/effects/convexmulti.json\"}]}");
        Write(root, "materials/effects/convexmulti.json",
            "{\"passes\":[{\"shader\":\"effects/convex\",\"blending\":\"normal\"},{\"shader\":\"effects/additive\",\"blending\":\"normal\"}]}");
    }

    private static JsonObject Effect(string name, int? background = null) => new() {
        ["file"] = $"effects/{name}/effect.json", ["visible"] = true,
        ["passes"] = new JsonArray(background is int value
            ? new JsonObject { ["combos"] = new JsonObject { ["BACKGROUND"] = value } } : new JsonObject()) };

    private static JsonObject ImageLayer(int id, double brightness, params JsonObject[] effects) => new() {
        ["id"] = id, ["name"] = $"layer {id}", ["image"] = "models/layer.json", ["color"] = "1 1 1",
        ["brightness"] = brightness, ["alpha"] = 1.0, ["colorBlendMode"] = 0, ["visible"] = true,
        ["effects"] = new JsonArray(effects) };

    private static JsonObject Runtime(int id, params string[] effectShaders)
    {
        var materials = new JsonArray(new JsonObject {
            ["shader"] = "genericimage2", ["role"] = "source", ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray("layer") });
        foreach (string shader in effectShaders)
            materials.Add(new JsonObject { ["shader"] = shader, ["role"] = "effect", ["textures"] = new JsonArray($"_rt_imageLayerComposite_{id}_a") });
        if (effectShaders.Length > 0)
            materials.Add(new JsonObject { ["shader"] = "genericimage2", ["role"] = "effect", ["textures"] = new JsonArray($"_rt_imageLayerComposite_{id}_b") });
        return new JsonObject { ["owner"] = id, ["has_effect_layer"] = effectShaders.Length > 0, ["materials"] = materials };
    }

    private static JsonObject Evaluate(string root, JsonObject[] objects, JsonObject[] runtime, params int[] layerIds) =>
        Evaluate(root, new JsonObject(), objects, runtime, layerIds);

    private static JsonObject Evaluate(string root, JsonObject properties, JsonObject[] objects, JsonObject[] runtime, params int[] layerIds)
    {
        using var source = new ProjectSource(root);
        return SdrRadianceClosure.Evaluate(new JsonObject { ["general"] = new JsonObject { ["hdr"] = true }, ["objects"] = new JsonArray(objects) },
            properties, new JsonArray(runtime), new JsonArray(),
            new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray([.. layerIds.Select(id => (JsonNode)id)]) },
            source, null, Rules());
    }

    private static string Status(JsonObject verdict) => verdict["status"]!.GetValue<string>();

    private static string Reasons(JsonObject verdict) =>
        string.Join(" | ", verdict["reasons"]!.AsArray().Select(reason => reason!.GetValue<string>()));

    [Fact]
    public Task ConvexResamplingEffectPassesR1() => TestTemp.Run(root =>
    {
        Fixture(root);
        JsonObject verdict = Evaluate(root, [ImageLayer(10, 1.0, Effect("convex"))], [Runtime(10, "effects/convex")], 10);
        Assert.Equal("closed", Status(verdict));
        return Task.CompletedTask;
    });

    [Fact]
    public Task AdditiveEffectAboveOneStaysOpen() => TestTemp.Run(root =>
    {
        Fixture(root);
        JsonObject verdict = Evaluate(root, [ImageLayer(10, 1.0, Effect("convex"), Effect("additive"))],
            [Runtime(10, "effects/convex", "effects/additive")], 10);
        Assert.Equal("open", Status(verdict));
        Assert.Contains("effects/additive/effect.json", Reasons(verdict), StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ForbiddenComboOnConvexShaderStaysOpen() => TestTemp.Run(root =>
    {
        Fixture(root);
        JsonObject verdict = Evaluate(root, [ImageLayer(10, 1.0, Effect("convex", background: 1))], [Runtime(10, "effects/convex")], 10);
        Assert.Equal("open", Status(verdict));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("convexadd")]
    [InlineData("convexmulti")]
    public Task ProvenShaderInUnprovenMaterialStaysOpen(string effect) => TestTemp.Run(root =>
    {
        Fixture(root);
        JsonObject verdict = Evaluate(root, [ImageLayer(10, 1.0, Effect(effect))], [Runtime(10, "effects/convex")], 10);
        Assert.Equal("open", Status(verdict));
        return Task.CompletedTask;
    });

    // 条件规则：默认取值闭合；combo 出允许集、常量越界、和越界、严格下界/严格递增不成立、绑脚本、材质里的常量越界，都不认领。
    [Theory]
    [InlineData("{}", null, true)]
    [InlineData("{\"combos\":{\"BLENDMODE\":0},\"constantshadervalues\":{\"amount\":0.6,\"noiseamount\":0.4,\"tint\":\"0.5 1 0\"}}", null, true)]
    [InlineData("{\"combos\":{\"BLENDMODE\":31}}", null, false)]
    [InlineData("{\"constantshadervalues\":{\"amount\":0.8,\"noiseamount\":0.4}}", null, false)]
    [InlineData("{\"constantshadervalues\":{\"amount\":-0.1}}", null, false)]
    [InlineData("{\"constantshadervalues\":{\"power\":0}}", null, false)]
    [InlineData("{\"constantshadervalues\":{\"tint\":\"1.2 1 1\"}}", null, false)]
    [InlineData("{\"constantshadervalues\":{\"bounds\":\"1 1\"}}", null, false)]
    [InlineData("{\"constantshadervalues\":{\"amount\":{\"script\":\"export function update(v){return 2;}\",\"value\":0.5}}}", null, false)]
    [InlineData("{}", "{\"passes\":[{\"shader\":\"effects/pulse\",\"blending\":\"normal\",\"constantshadervalues\":{\"amount\":2}}]}", false)]
    public Task ConditionalRuleChecksAuthoredValues(string pass, string? material, bool closed) => TestTemp.Run(root =>
    {
        Fixture(root);
        if (material is not null) Write(root, "materials/effects/pulse.json", material);
        JsonObject effect = new() { ["file"] = "effects/pulse/effect.json", ["visible"] = true, ["passes"] = new JsonArray(JsonNode.Parse(pass)) };
        JsonObject verdict = Evaluate(root, [ImageLayer(10, 1.0, effect)], [Runtime(10, "effects/pulse")], 10);
        Assert.Equal(closed ? "closed" : "open", Status(verdict));
        if (!closed) Assert.Contains("fixture_pulse", Reasons(verdict), StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ConditionalRuleResolvesUserBindingsAndPinsHeader() => TestTemp.Run(root =>
    {
        Fixture(root);
        JsonObject Layer() => ImageLayer(10, 1.0, new JsonObject { ["file"] = "effects/pulse/effect.json", ["visible"] = true,
            ["passes"] = new JsonArray(new JsonObject { ["constantshadervalues"] = new JsonObject {
                ["amount"] = new JsonObject { ["user"] = "pulseamount", ["value"] = 2 } } }) });
        // 用户属性把 amount 定成 0.5：按属性值判，闭合；属性值 2 则越界。
        Assert.Equal("closed", Status(Evaluate(root, new JsonObject { ["pulseamount"] = 0.5 }, [Layer()], [Runtime(10, "effects/pulse")], 10)));
        Assert.Equal("open", Status(Evaluate(root, new JsonObject { ["pulseamount"] = 2 }, [Layer()], [Runtime(10, "effects/pulse")], 10)));
        // 头文件改成不截断的加法：着色器指纹没变，但证明依赖的 ApplyBlending 变了，不认领。
        Write(root, "shaders/common_blending.h", "vec3 ApplyBlending(const int m, vec3 a, vec3 b, float o) { return mix(a, a + b, o); }\n");
        JsonObject changed = Evaluate(root, new JsonObject { ["pulseamount"] = 0.5 }, [Layer()], [Runtime(10, "effects/pulse")], 10);
        Assert.Equal("open", Status(changed));
        Assert.Contains("common_blending.h", Reasons(changed), StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ConvexEffectDoesNotExcuseBrightnessAboveOne() => TestTemp.Run(root =>
    {
        Fixture(root);
        JsonObject verdict = Evaluate(root, [ImageLayer(10, 1.64, Effect("convex"))], [Runtime(10, "effects/convex")], 10);
        Assert.Equal("open", Status(verdict));
        Assert.Contains("(R4)", Reasons(verdict), StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EmptyUnscriptedTextLayerIsNotDrawn() => TestTemp.Run(root =>
    {
        Fixture(root);
        var empty = new JsonObject { ["id"] = 30, ["name"] = "----------------", ["text"] = "", ["visible"] = true };
        var filled = new JsonObject { ["id"] = 31, ["name"] = "caption", ["text"] = "hello", ["visible"] = true };
        JsonObject closed = Evaluate(root, [empty], [], 30);
        Assert.Equal("closed", Status(closed));
        Assert.Equal("not_drawn", closed["per_layer"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("open", Status(Evaluate(root, [filled], [], 31)));
        // 有脚本按名字拿到这层（可能写进文字）才照常判（文字层值域不可判 → open）；渲染器建的网格不作数。
        var writer = new JsonObject { ["id"] = 32, ["name"] = "ctl",
            ["origin"] = new JsonObject { ["script"] = "thisScene.getLayer('----------------').text = 'x';", ["value"] = "0 0 0" } };
        Assert.Equal("open", Status(Evaluate(root, [(JsonObject)empty.DeepClone(), writer], [], 30)));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("\"\"", true)]
    [InlineData("\"hello\"", false)]
    [InlineData("{\"value\":\"\",\"script\":\"export function update(v){return 'x';}\"}", false)]
    [InlineData("{\"value\":\"\"}", true)]
    public void EmptyTextNeedsEmptyLiteralAndNoScript(string text, bool empty) =>
        Assert.Equal(empty, Composer.EmptyText(new JsonObject { ["text"] = JsonNode.Parse(text) }, new JsonObject(), new JsonObject()));

    [Fact]
    public Task PrefixRouteReevaluatesClosureOnCapturedPrefix() => TestTemp.Run(root =>
    {
        // 整层初判：层 10 挂着叠加特效 → open。前缀路线只捕获前 1 个特效（凸组合），叠加特效留实时：重求应当闭合、不写回 HDR 拒因。
        Fixture(root);
        JsonObject scene = new() { ["general"] = new JsonObject { ["hdr"] = true },
            ["objects"] = new JsonArray(ImageLayer(10, 1.0, Effect("convex"), Effect("additive"))) };
        JsonObject Report(int prefixCount) => new() {
            ["route"] = "effect_prefix", ["status"] = "candidate_found", ["blockers"] = new JsonArray(),
            ["hdr_radiance_closure"] = new JsonObject { ["hdr"] = true, ["status"] = "open" },
            ["effect_prefix_caches"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 10, ["prefix_effect_count"] = prefixCount }) };
        JsonObject trace = new() { ["runtime_layers"] = new JsonArray(Runtime(10, "effects/convex", "effects/additive")) };
        using var source = new ProjectSource(root);
        JsonObject prefixOne = Report(1), prefixTwo = Report(2);
        Verdict.ApplyPrefixRadianceClosure(prefixOne, scene, new JsonObject(), trace, source, null, null, Rules());
        Verdict.ApplyPrefixRadianceClosure(prefixTwo, scene, new JsonObject(), trace, source, null, null, Rules());
        Assert.Equal("closed", prefixOne["hdr_radiance_closure"]!["status"]!.GetValue<string>());
        Assert.Equal("effect_prefix", prefixOne["hdr_radiance_closure"]!["capture_scope"]!.GetValue<string>());
        Assert.Equal("open", prefixOne[Verdict.InitialRadianceClosureField]!["status"]!.GetValue<string>());
        Assert.Empty(prefixOne["blockers"]!.AsArray());
        Assert.Equal("candidate_found", prefixOne["status"]!.GetValue<string>());
        // 前 2 个特效含叠加：必须仍判 open 并写回拒因。
        Assert.Equal("open", prefixTwo["hdr_radiance_closure"]!["status"]!.GetValue<string>());
        Assert.Equal("requires_resolution", prefixTwo["status"]!.GetValue<string>());
        Assert.NotEmpty(prefixTwo["blockers"]!.AsArray());
        return Task.CompletedTask;
    });
}
