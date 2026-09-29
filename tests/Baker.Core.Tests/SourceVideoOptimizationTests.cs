using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

public class SourceVideoOptimizationTests
{
    [Fact]
    public async Task UserZoomUpToTwoCannotUseSnapshotSizedVideo() => await TestTemp.Run(async root =>
    {
        await File.WriteAllTextAsync(Path.Combine(root, "scene.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(root, "project.json"),
            """{"type":"scene","file":"scene.json","general":{"properties":{"lens":{"type":"slider","min":0.1,"max":2,"value":1}}}}""");
        using var source = new ProjectSource(Path.Combine(root, "scene.json"));
        var scene = new JsonObject { ["general"] = new JsonObject {
            ["orthogonalprojection"] = new JsonObject { ["width"] = 3840, ["height"] = 2160 }, ["zoom"] = 1.0 },
            ["objects"] = new JsonArray(new JsonObject { ["id"] = 9, ["camera"] = "default",
                ["zoom"] = new JsonObject { ["user"] = "lens", ["value"] = 1.0 } }) };
        Assert.False(SourceVideoOptimization.CameraSamplingFixed(scene, source));
        scene["objects"]![0]!["zoom"] = 2.0;
        Assert.False(SourceVideoOptimization.CameraSamplingFixed(scene, source));
        scene["objects"]![0]!["zoom"] = 1.0;
        Assert.True(SourceVideoOptimization.CameraSamplingFixed(scene, source));
    });

    [Fact]
    public void ValidatesEveryAuthoredManualIndexAndExplicitAutomaticMode()
    {
        var plan = new JsonObject { ["snapshot_properties"] = new JsonObject { ["clock"] = false, ["choice"] = "2" },
            ["daytime_split"] = new JsonObject { ["video_selection"] = new JsonObject {
                ["mode_property"] = "clock", ["manual_property"] = "choice",
                ["layer_ids"] = new JsonArray(11, 12, 13) } } };
        var states = SourceVideoOptimization.ValidationStates(plan);
        Assert.Equal(["0", "1", "2", "automatic"], states.Select(state => state.Label));
        Assert.All(states[..3], state => Assert.False(state.Properties["clock"]!.GetValue<bool>()));
        Assert.Equal("0", states[0].Properties["choice"]!.GetValue<string>());
        Assert.Equal("2", states[2].Properties["choice"]!.GetValue<string>());
        Assert.True(states[3].Properties["clock"]!.GetValue<bool>());
    }

    [Fact]
    public void MissingCanvasCenterCannotProveFullScreenMapping()
    {
        var layer = new JsonObject { ["id"] = 1, ["image"] = "models/video.json",
            ["size"] = "1920 1080", ["scale"] = "1 1" };
        var objects = new Dictionary<int, JsonObject> { [1] = layer };
        var described = new JsonObject { ["canvas_fraction"] = 1.0,
            ["canvas_center_x"] = .5, ["canvas_center_y"] = .5 };
        Assert.True(SourceVideoOptimization.SimpleLeaf(layer, objects, described, 1920, 1080, 1920, 1080));
        described.Remove("canvas_center_x");
        Assert.False(SourceVideoOptimization.SimpleLeaf(layer, objects, described, 1920, 1080, 1920, 1080));
    }

    [Fact]
    public async Task ProbeDirectoriesAreExclusiveAndKeepExistingFiles() => await TestTemp.Run(async root =>
    {
        string old = Path.Combine(root, "source-video-probe");
        Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, "sentinel.txt"), "keep");
        string first = SourceVideoOptimization.CreateProbeDirectory();
        string second = SourceVideoOptimization.CreateProbeDirectory();
        try
        {
            Assert.NotEqual(first, second);
            Assert.NotEqual(old, first);
            Assert.True(File.Exists(Path.Combine(old, "sentinel.txt")));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    });

    [Fact]
    public async Task RepackedVideoKeepsAuthoredTextureFlags() => await TestTemp.Run(async root =>
    {
        string mp4 = Path.Combine(root, "minimal.mp4");
        await File.WriteAllBytesAsync(mp4, [0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0]);
        string tex = Path.Combine(root, "video.tex");
        await TextureContainer.WriteVideoAsync(tex, mp4, 1920, 1080, sourceFlags: 0x20);
        Assert.True(TextureContainer.TryReadHeader(await File.ReadAllBytesAsync(tex), out var header));
        Assert.Equal(0x20u, header.Flags);
    });

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
