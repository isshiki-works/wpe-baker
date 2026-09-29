using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L1")]
public class EffectPrefixScaleTests
{
    [Fact]
    public async Task LocalPrefixKeepsTheAuthoredScaleAndRejectsDynamicScale() => await TestTemp.Run(async root =>
    {
        string author = Path.Combine(root, "author"), output = Path.Combine(root, "output");
        void Write(string resource, string json)
        {
            string path = Path.Combine(author, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        Write("project.json", """{"file":"scene.json","type":"scene"}""");
        Write("models/image.json", """{"material":"materials/image.json","autosize":true}""");
        Write("materials/image.json", """{"passes":[{"shader":"genericimage3","textures":["image"]}]}""");
        Write("effects/prefix.json", """{"passes":[{"material":"materials/prefix.json"}]}""");
        Write("materials/prefix.json", """{"passes":[{"shader":"prefix"}]}""");
        JsonObject scene = JsonNode.Parse("""
            {"objects":[{"id":1,"image":"models/image.json","scale":"0.99000 0.99000 1.00000",
            "effects":[{"id":2,"file":"effects/prefix.json"}]}]}
            """)!.AsObject();
        Write("scene.json", scene.ToJsonString());
        using var source = new ProjectSource(author);
        JsonObject derived = scene.DeepClone().AsObject();
        string frame = Path.Combine(root, "frame.rgba");
        await File.WriteAllBytesAsync(frame, [40, 80, 120, 255], TestContext.Current.CancellationToken);
        await EffectPrefixCache.ApplyAsync(source, scene, derived, output, 1, 1, frame, 1, 1,
            rgbaFrame: true, TestContext.Current.CancellationToken);
        Assert.Equal("0.99000 0.99000 1.00000", derived["objects"]![0]!["scale"]!.GetValue<string>());
        Assert.Empty(derived["objects"]![0]!["effects"]!.AsArray());
        foreach (JsonNode invalid in new JsonNode[] {
            JsonValue.Create("NaN 1 1"), JsonValue.Create("1 1"),
            new JsonObject { ["value"] = "1 1 1", ["script"] = "export function update(v) { return v; }" } })
        {
            scene["objects"]![0]!["scale"] = invalid;
            Assert.Throws<InvalidDataException>(() => EffectPrefixCache.ValidateSource(source, scene, scene, 1, 1));
        }
    });
}
