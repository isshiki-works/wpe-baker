using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

public class SourceVideoOptimizationTests
{
    [Fact]
    public async Task HiddenMaterialOrLayerUseKeepsSharedVideoAtSourceSize() => await TestTemp.Run(async root =>
    {
        Directory.CreateDirectory(Path.Combine(root, "materials"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        await File.WriteAllTextAsync(Path.Combine(root, "scene.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(root, "materials", "main.json"),
            """{"passes":[{"textures":["video"]}]}""");
        await File.WriteAllTextAsync(Path.Combine(root, "models", "main.json"),
            """{"material":"materials/main.json"}""");
        using var source = new ProjectSource(Path.Combine(root, "scene.json"));
        var objects = new Dictionary<int, JsonObject> { [1] = new() { ["image"] = "models/main.json" } };
        Assert.True(SourceVideoOptimization.UniqueStaticUse(source, objects, 1, "materials/main.json", "video"));
        await File.WriteAllTextAsync(Path.Combine(root, "materials", "hidden.json"),
            """{"passes":[{"textures":["video"]}]}""");
        Assert.False(SourceVideoOptimization.UniqueStaticUse(source, objects, 1, "materials/main.json", "video"));
        File.Delete(Path.Combine(root, "materials", "hidden.json"));
        objects[2] = new JsonObject { ["image"] = "models/main.json" };
        Assert.False(SourceVideoOptimization.UniqueStaticUse(source, objects, 1, "materials/main.json", "video"));
    });
}
