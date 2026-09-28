using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class DirectTextRuntimeTests
{
    [Fact]
    public async Task DirectTextAppliesAbsoluteAlphaColorAndBrightness()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        await TestTemp.Run(async root =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var runner = new NativeRenderRunner(LocalTools.Tools!);
            async Task<byte[]> Frame(string name, JsonNode color, double alpha = 1, double brightness = 1, ulong warmup = 0)
            {
                string project = Path.Combine(root, name + "-source"), output = Path.Combine(root, name + "-render");
                Directory.CreateDirectory(project);
                await File.WriteAllTextAsync(Path.Combine(project, "project.json"),
                    """{"type":"scene","file":"scene.json"}""", timeout.Token);
                var scene = new JsonObject
                {
                    ["camera"] = new JsonObject { ["center"] = "0 0 0", ["eye"] = "0 0 1", ["up"] = "0 1 0" },
                    ["general"] = new JsonObject { ["clearcolor"] = "0 0 0", ["clearenabled"] = true,
                        ["orthogonalprojection"] = new JsonObject { ["width"] = 256, ["height"] = 128 },
                        ["cameraparallax"] = false, ["camerashake"] = false, ["bloom"] = false },
                    ["objects"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "text tint probe",
                        ["font"] = "systemfont_arial", ["text"] = "ALPHA", ["origin"] = "128 64 0",
                        ["scale"] = "1 1 1", ["size"] = "200 80", ["pointsize"] = 40,
                        ["color"] = color.DeepClone(), ["alpha"] = alpha, ["brightness"] = brightness, ["visible"] = true })
                };
                await File.WriteAllTextAsync(Path.Combine(project, "scene.json"), scene.ToJsonString(), timeout.Token);
                await runner.RenderAsync(new RenderRequest(project, project, output, 256, 128, 60, 1, 1,
                    WarmupFrames: warmup, Seed: 17, RetainFrames: [0]), cancellationToken: timeout.Token);
                return await File.ReadAllBytesAsync(Path.Combine(output, "retained-frames.rgba"), timeout.Token);
            }

            static long Channel(byte[] rgba, int channel) => Enumerable.Range(0, rgba.Length / 4)
                .Sum(i => (long)rgba[i * 4 + channel]);
            static long Luma(byte[] rgba) => Channel(rgba, 0) + Channel(rgba, 1) + Channel(rgba, 2);
            byte[] white = await Frame("white", JsonValue.Create("1 1 1")!);
            byte[] halfAlpha = await Frame("half-alpha", JsonValue.Create("1 1 1")!, alpha: .5);
            byte[] halfBrightness = await Frame("half-brightness", JsonValue.Create("1 1 1")!, brightness: .5);
            Assert.True(Luma(white) > 0);
            Assert.InRange((double)Luma(halfAlpha) / Luma(white), .45, .55);
            Assert.InRange((double)Luma(halfBrightness) / Luma(white), .45, .55);

            JsonObject ScriptColor() => new() { ["value"] = "1 0 0",
                ["script"] = "export function update(value) { return engine.runtime < 1 ? [1, 0, 0] : [0, 1, 0]; }" };
            byte[] early = await Frame("red", ScriptColor());
            byte[] late = await Frame("green", ScriptColor(), warmup: 120);
            Assert.True(Channel(early, 0) > Channel(early, 1) * 4);
            Assert.True(Channel(late, 1) > Channel(late, 0) * 4);
        });
    }
}
