using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L1")]
public class WaterwaveFusionTests
{
    [Fact]
    public void UnscaledWavesKeepTheirStrengthAndOptionalClockBindings()
    {
        var a = new WaterwaveFusion.Wave(new JsonObject(), 0, 3, 15, .2, 0, "first-mask", null, 642, 866,
            ResolutionScaled: false);
        var b = a with { Mask = "second-mask", Strength = .3 };
        string fragment = WaterwaveFusion.Fragment(a, b);
        Assert.Contains("float strength = 0.2 * 0.2;", fragment);
        Assert.Contains("float strength = 0.3 * 0.3;", fragment);
        Assert.DoesNotContain("CAST2(500)", fragment);
        Assert.DoesNotContain("M_PI_2", fragment);
        Assert.DoesNotContain("g_Texture3", fragment);
        Assert.Contains("texSample2D(g_Texture2, v_Uv).r", fragment);
        Assert.Contains("floor(clamp(color, CAST4(0), CAST4(1)) * 255.0 + 0.5) / 255.0", fragment);
        Assert.Contains("firstTexel(base + vec2(1, 1), v_DirA)", fragment);

        string mixed = WaterwaveFusion.Fragment(a with { ResolutionScaled = true, TimeOffset = .74,
            OffsetTexture = "first-offset" }, b);
        Assert.Contains("vec2 strength = (CAST2(500) / g_Texture0Resolution.xy) * 0.2 * 0.2;", mixed);
        Assert.Contains("phase += 0.74 * M_PI_2;", mixed);
        Assert.Contains("float strength = 0.3 * 0.3;", mixed);
        Assert.Contains("texSample2DLod(g_Texture2, uv, 0.0).r * M_PI_2", mixed);
        Assert.Contains("texSample2D(g_Texture3, v_Uv).r", mixed);
        Assert.DoesNotContain("texSample2D(g_Texture4", mixed);

        string secondClock = WaterwaveFusion.Fragment(a, b with { ResolutionScaled = true,
            TimeOffset = .24, OffsetTexture = "second-offset" });
        Assert.Contains("float strength = 0.2 * 0.2;", secondClock);
        Assert.Contains("vec2 strength = (CAST2(500) / g_Texture0Resolution.xy) * 0.3 * 0.3;", secondClock);
        Assert.Contains("texSample2D(g_Texture3, v_Uv).r * M_PI_2;\n    phase += 0.24 * M_PI_2;", secondClock);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task OnlySingleUnpaddedR8MipWithKnownBodyVersionIsAccepted(int bodyVersion)
    {
        await TestTemp.Run(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "scene.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "materials"));
            byte[] texture = new byte[91];
            Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0").CopyTo(texture, 0);
            Encoding.ASCII.GetBytes($"TEXB000{bodyVersion}\0").CopyTo(texture, 46);
            int mipOffset = bodyVersion == 3 ? 63 : 67;
            void Set(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
            foreach (var (offset, value) in new[] { (18, 9), (22, 2), (26, 642), (30, 866), (34, 642), (38, 866),
                (55, 1), (59, -1), (mipOffset, 1), (mipOffset + 4, 642), (mipOffset + 8, 866) }) Set(texture, offset, value);
            string path = Path.Combine(root, "materials", "mask.tex");
            await File.WriteAllBytesAsync(path, texture);
            using var source = new ProjectSource(root);
            Assert.True(WaterwaveFusion.TextureOk(source, "mask", out uint width, out uint height));
            Assert.Equal(642u, width);
            Assert.Equal(866u, height);
            foreach (var (offset, value) in new[] { (18, 0), (22, 0), (26, 644), (55, 2), (59, 0),
                (mipOffset, 2), (mipOffset + 4, 644), (mipOffset + 8, 868) })
            {
                byte[] rejected = (byte[])texture.Clone();
                Set(rejected, offset, value);
                await File.WriteAllBytesAsync(path, rejected);
                Assert.False(WaterwaveFusion.TextureOk(source, "mask", out _, out _));
            }
            byte[] unknown = (byte[])texture.Clone();
            unknown[53] = (byte)'2';
            await File.WriteAllBytesAsync(path, unknown);
            Assert.False(WaterwaveFusion.TextureOk(source, "mask", out _, out _));
            if (bodyVersion == 4)
            {
                Set(texture, 63, 1);
                await File.WriteAllBytesAsync(path, texture);
                Assert.False(WaterwaveFusion.TextureOk(source, "mask", out _, out _));
            }
        });
    }

    [Fact]
    public void FirstTimeOffsetKeepsBothMaskAndClockBindings()
    {
        var a = new WaterwaveFusion.Wave(new JsonObject(), -2.8, 3, 15, .29, .74, "first-mask", "first-offset", 1144, 795);
        var b = new WaterwaveFusion.Wave(new JsonObject(), -2.4, 3, 14, .29, .24, "second-mask", "second-offset", 1144, 795);
        string fragment = WaterwaveFusion.Fragment(a, b);
        Assert.Contains("texSample2DLod(g_Texture2, uv, 0.0).r * M_PI_2", fragment);
        Assert.Contains("texSample2D(g_Texture3, v_Uv).r", fragment);
        Assert.Contains("texSample2D(g_Texture4, v_Uv).r", fragment);
        Assert.Contains("texSample2D(g_Texture4, v_Uv).r * M_PI_2;\n    phase += 0.24 * M_PI_2;", fragment);
        string legacy = WaterwaveFusion.Fragment(a with { OffsetTexture = null }, b);
        Assert.DoesNotContain("g_Texture4", legacy);
        Assert.Contains("texSample2D(g_Texture2, v_Uv).r", legacy);
        Assert.Contains("texSample2D(g_Texture3, v_Uv).r", legacy);
    }

    [Fact]
    public async Task UnknownEffectsStayUnchangedInEditableCopy()
    {
        await TestTemp.Run(async root =>
        {
            string source = Path.Combine(root, "source"), output = Path.Combine(root, "output");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "project.json"),
                "{\"type\":\"scene\",\"file\":\"scene.json\",\"title\":\"Author title\",\"workshopid\":\"123\",\"publishedfileid\":\"456\",\"workshopurl\":\"author-link\"}");
            string scene = "{\"objects\":[{\"id\":7,\"effects\":[{\"file\":\"effects/unknown.json\",\"visible\":true},{\"file\":\"effects/unknown.json\",\"visible\":true}]}]}";
            await File.WriteAllTextAsync(Path.Combine(source, "scene.json"), scene);
            var result = await WaterwaveFusion.OptimizeAsync(source, output);
            Assert.Equal("unchanged", result["status"]!.GetValue<string>());
            Assert.Equal(0, result["fused_pairs"]!.GetValue<int>());
            Assert.Equal(scene, await File.ReadAllTextAsync(Path.Combine(output, "scene.json")));
            JsonObject metadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "project.json")))!.AsObject();
            Assert.False(metadata.ContainsKey("workshopid"));
            Assert.False(metadata.ContainsKey("publishedfileid"));
            Assert.Equal("Author title", metadata["title"]!.GetValue<string>());
            Assert.Equal("author-link", metadata["workshopurl"]!.GetValue<string>());
            string selectedOutput = Path.Combine(root, "selected-output");
            JsonObject selected = await WaterwaveFusion.OptimizeSelectedAsync(source, selectedOutput, new JsonObject());
            Assert.Equal("unchanged", selected["status"]!.GetValue<string>());
            Assert.False(Directory.Exists(selectedOutput));
            string staleOutput = Path.Combine(root, "stale-output");
            await Assert.ThrowsAsync<InvalidDataException>(() => WaterwaveFusion.OptimizeSelectedAsync(
                source, staleOutput, new JsonObject(), "old-source-hash", TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(staleOutput));
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
