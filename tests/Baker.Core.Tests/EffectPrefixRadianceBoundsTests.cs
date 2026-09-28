using System.Text.Json.Nodes;
using Baker.Core;
using Baker.Core.Analysis.EffectRange;
using Xunit;

[Trait("Layer", "L3")]
public class EffectPrefixRadianceBoundsTests(ITestOutputHelper output)
{
    private static string Source => Environment.GetEnvironmentVariable("PERIODICA_RANGE_SOURCE") ?? "D:/WPE-regress-src/3643143272/scene.pkg";
    private static string Assets => Environment.GetEnvironmentVariable("PERIODICA_RANGE_ASSETS") ?? "D:/Apps/Steam/steamapps/common/wallpaper_engine/assets";
    public static bool Available => File.Exists(Source) && Directory.Exists(Assets);

    [Fact(SkipUnless = nameof(Available), Skip = "Requires the local HDR prefix corpus and official assets.")]
    public void RealPrefixComposesSignedIntervalsAndRejectsUnprovenInputs()
    {
        using var source = new ProjectSource(Source);
        JsonObject layer = source.ReadJson(source.SceneResource)["objects"]!.AsArray().OfType<JsonObject>()
            .Single(o => o["id"]!.GetValue<int>() == 17).DeepClone().AsObject();
        layer["effects"] = new JsonArray(layer["effects"]!.AsArray().Take(6).Select(e => e!.DeepClone()).ToArray());
        Assert.True(EffectPrefixRadianceBounds.TryProve(layer, new(), source, Assets, out double lo, out double hi, out string detail), detail);
        output.WriteLine(detail);
        Assert.True(lo < 0 && hi > 2 && double.IsFinite(lo) && double.IsFinite(hi));

        JsonObject Report() => new()
        {
            ["route"] = "effect_prefix", ["status"] = "candidate_found", ["blockers"] = new JsonArray(),
            ["hdr_radiance_closure"] = new JsonObject { ["hdr"] = true, ["status"] = "open" },
            ["effect_prefix_caches"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 17, ["prefix_effect_count"] = 6 })
        };
        JsonObject report = Report(), unsupported = Report(), scene = source.ReadJson(source.SceneResource);
        Verdict.ApplyPrefixRadianceClosure(report, scene, new(), new(), source, Assets, null,
            terminalHdrScaleSupported: true, terminalAffineSupported: true);
        JsonNode closure = report["hdr_radiance_closure"]!, encoded = closure["groups"]![0]!;
        Assert.Equal("float_terminal_rgb_affine", closure["capture_encoding"]!.GetValue<string>());
        Assert.True(encoded["capture_lower_bound"]!.GetValue<double>() <= lo);
        Assert.True(encoded["capture_lower_bound"]!.GetValue<double>() + encoded["capture_scale"]!.GetValue<double>() >= hi);
        Assert.Null(closure["blocker"]);
        Assert.Empty(report["blockers"]!.AsArray());
        Verdict.ApplyPrefixRadianceClosure(unsupported, scene, new(), new(), source, Assets, null,
            terminalHdrScaleSupported: true, terminalAffineSupported: false);
        Assert.NotEmpty(unsupported["blockers"]!.AsArray());

        var changed = layer.DeepClone().AsObject();
        changed["alpha"] = .5;
        Assert.False(EffectPrefixRadianceBounds.TryProve(changed, new(), source, Assets, out _, out _, out _));
        changed = layer.DeepClone().AsObject();
        changed["effects"]![0]!["passes"]![0]!["constantshadervalues"]!["Strength"] = 6;
        Assert.True(EffectPrefixRadianceBounds.TryProve(changed, new(), source, Assets, out double expandedLo, out double expandedHi, out detail), detail);
        Assert.True(expandedLo < lo && expandedHi > hi, detail);
        changed["effects"]![0]!["passes"]![0]!["constantshadervalues"]!["Strength"] = 16777216;
        Assert.False(EffectPrefixRadianceBounds.TryProve(changed, new(), source, Assets, out _, out _, out detail));
        Assert.Contains("alpha-one", detail);
        changed = layer.DeepClone().AsObject();
        changed["effects"]![0]!["passes"]![0]!["combos"] = new JsonObject { ["UNREVIEWED"] = 1 };
        Assert.False(EffectPrefixRadianceBounds.TryProve(changed, new(), source, Assets, out _, out _, out _));

        // A shorter/reordered authored chain uses the same operators, rather
        // than matching this fixture's six effects as a wallpaper whitelist.
        changed = layer.DeepClone().AsObject();
        changed["effects"] = new JsonArray(layer["effects"]![0]!.DeepClone(), layer["effects"]![3]!.DeepClone());
        Assert.True(EffectPrefixRadianceBounds.TryProve(changed, new(), source, Assets, out lo, out hi, out detail), detail);
        Assert.True(lo < 0 && hi > 1 && hi < 2, detail);
    }
}
