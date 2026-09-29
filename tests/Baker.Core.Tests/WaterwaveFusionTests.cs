using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L1")]
public class WaterwaveFusionTests
{
    [Fact]
    public async Task UnknownEffectsStayUnchangedInEditableCopy()
    {
        await TestTemp.Run(async root =>
        {
            string source = Path.Combine(root, "source"), output = Path.Combine(root, "output");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            string scene = "{\"objects\":[{\"id\":7,\"effects\":[{\"file\":\"effects/unknown.json\",\"visible\":true},{\"file\":\"effects/unknown.json\",\"visible\":true}]}]}";
            await File.WriteAllTextAsync(Path.Combine(source, "scene.json"), scene);
            var result = await WaterwaveFusion.OptimizeAsync(source, output);
            Assert.Equal("unchanged", result["status"]!.GetValue<string>());
            Assert.Equal(0, result["fused_pairs"]!.GetValue<int>());
            Assert.Equal(scene, await File.ReadAllTextAsync(Path.Combine(output, "scene.json")));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(source, "project.json")),
                await File.ReadAllTextAsync(Path.Combine(output, "project.json")));
        });
    }

    [Fact]
    public void EffectScriptsAreScopedToTheirTargetLayer()
    {
        var target = new JsonObject { ["id"] = 7, ["name"] = "Target" };
        var other = new JsonObject { ["id"] = 8, ["name"] = "Other", ["visible"] = new JsonObject {
            ["script"] = "thisLayer.getEffect('unrelated');" } };
        var objects = new[] { target, other };
        var scene = new JsonObject { ["objects"] = new JsonArray(target.DeepClone(), other.DeepClone()) };
        Assert.False(WaterwaveFusion.ScriptsMayAccessEffects(scene, objects, target, Liveness.ScriptLookupEdges(
            objects.ToDictionary(obj => obj["id"]!.GetValue<int>()))));
        other["visible"]!["script"] = "thisScene.getLayer('Target').getEffect('wave');";
        Assert.True(WaterwaveFusion.ScriptsMayAccessEffects(scene, objects, target, Liveness.ScriptLookupEdges(
            objects.ToDictionary(obj => obj["id"]!.GetValue<int>()))));
        other["visible"]!["script"] = "thisScene.getLayer(selected).getEffect('wave');";
        Assert.True(WaterwaveFusion.ScriptsMayAccessEffects(scene, objects, target, Liveness.ScriptLookupEdges(
            objects.ToDictionary(obj => obj["id"]!.GetValue<int>()))));
        other["visible"]!["script"] = "shared.layer.getEffect('wave');";
        Assert.True(WaterwaveFusion.ScriptsMayAccessEffects(scene, objects, target, Liveness.ScriptLookupEdges(
            objects.ToDictionary(obj => obj["id"]!.GetValue<int>()))));
    }
}
