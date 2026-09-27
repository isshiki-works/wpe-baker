using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// 特效前缀的资格：一层里只要有一个效果读指针/音频，整层就留实时；前缀路线本该接手——把读输入的效果当后缀，
// 之前的纯时间特效照样缓存。原来 HasRuntimeInput 查该层全部材质（读输入的恰好是后缀，永远不合格），
// 底材质又只认 genericimage3/4（抽查的背景全是 genericimage2），这类层一个前缀都提不出来。
[Trait("Layer", "L1")]
public class EffectPrefixEligibilityTests
{
    [Fact]
    public async Task PointerEffectEndsThePrefixOfAGenericImage2Background() => await TestTemp.Run(async dir =>
    {
        void Write(string resource, string text)
        {
            string path = Path.Combine(dir, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("project.json", """{"file":"scene.json","type":"scene"}""");
        Write("models/background.json", """{"material":"materials/background.json","autosize":true}""");
        Write("materials/background.json", """{"passes":[{"shader":"genericimage2","textures":["background"]}]}""");
        foreach (string name in new[] { "waves", "cursor" })
        {
            Write($"effects/{name}/effect.json", $$"""{"passes":[{"material":"materials/effects/{{name}}.json"}]}""");
            Write($"materials/effects/{name}.json", $$"""{"passes":[{"shader":"effects/{{name}}"}]}""");
        }
        var scene = JsonNode.Parse("""
            {"general":{"orthogonalprojection":{"width":64,"height":48}},
             "objects":[{"id":1,"image":"models/background.json","size":"64 48","effects":[
               {"id":10,"file":"effects/waves/effect.json","passes":[{}]},
               {"id":11,"file":"effects/cursor/effect.json","passes":[{}]}]}]}
            """)!.AsObject();
        Write("scene.json", scene.ToJsonString());
        JsonObject Material(string shader, string role, int? effect, string uniforms, string? signature) => JsonNode.Parse($$"""
            {"shader":"{{shader}}","role":"{{role}}"{{(effect is int e ? $",\"effect\":{e},\"pass\":0" : "")}},
             "uses_audio_spectrum":false,"active_uniforms":[{{uniforms}}],"textures":[]{{(signature is null ? "" : ",\"time_signature\":" + signature)}}}
            """)!.AsObject();
        const string Waves = """{"kind":"periodic","reasons":[],"external":[],"transient":false,"terms":[{"seconds":3,"num":3,"den":1,"pi":0,"knobs":[]}]}""";
        const string Still = """{"kind":"static","reasons":[],"external":[],"transient":false,"terms":[]}""";
        var runtime = new JsonObject { ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(), ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 1, ["materials"] = new JsonArray(
                Material("genericimage2", "source", null, "", Still), Material("effects/waves", "effect", 0, "\"g_Time\"", Waves),
                Material("effects/cursor", "effect", 1, "\"g_PointerPosition\"", Still)) }) };
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(1, dir, dir, dir, 64, 48, 30, 1);
        JsonArray proposals = EffectPrefixPlanner.Propose(scene, source, dir, runtime, new JsonObject(), request, new JsonObject());
        JsonObject proposal = Assert.Single(proposals.OfType<JsonObject>());
        Assert.Equal(1, proposal["prefix_effect_count"]!.GetValue<int>());
        Assert.Equal(10, proposal["terminal_effect_id"]!.GetValue<int>());
        await Task.CompletedTask;
    });
}
