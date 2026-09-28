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
        JsonObject report = Report(), unsupported = Report(), noProbe = Report(), scene = source.ReadJson(source.SceneResource);
        var measured = new Dictionary<int, EffectPrefixRadianceBounds.StaticHeadBound> {
            [17] = new(21, -0.0147171020508, 1.103515625, "synthetic static-head bound for range propagation") };
        Verdict.ApplyPrefixRadianceClosure(report, scene, new(), new(), source, Assets, null,
            terminalSignedSqrtSupported: true, terminalAffineSupported: true, staticHeads: measured);
        Verdict.ApplyPrefixRadianceClosure(noProbe, scene, new(), new(), source, Assets, null,
            terminalSignedSqrtSupported: true, terminalAffineSupported: true);
        JsonNode closure = report["hdr_radiance_closure"]!, encoded = closure["groups"]![0]!;
        Assert.Equal("float_terminal_rgb_signed_sqrt", closure["capture_encoding"]!.GetValue<string>());
        Assert.True(encoded["capture_lower_bound"]!.GetValue<double>() < 0);
        Assert.InRange(encoded["capture_scale"]!.GetValue<double>(), 5.35, 5.37);
        Assert.Null(closure["blocker"]);
        Assert.Empty(report["blockers"]!.AsArray());
        Assert.NotEmpty(noProbe["blockers"]!.AsArray());
        Verdict.ApplyPrefixRadianceClosure(unsupported, scene, new(), new(), source, Assets, null,
            terminalSignedSqrtSupported: true, terminalAffineSupported: false);
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

    [Fact(SkipUnless = nameof(Available), Skip = "Requires the local HDR prefix corpus and official assets.")]
    public Task RealStaticHeadProbeBoundsTheRetainedChain() => TestTemp.Run(async root =>
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        using var source = new ProjectSource(Source);
        JsonObject scene = source.ReadJson(source.SceneResource);
        var properties = new JsonObject { ["schemecolor"] = "0.33333 0.51765 0.55294" };
        var renderer = new NativeRenderRunner(LocalTools.Tools!);
        var observed = await renderer.RenderRawAsync(new(Source, Assets, Path.Combine(root, "trace"),
            64, 64, 60, 1, 1, Seed: 17, TraceScene: true,
            CaptureTarget: new(17, 21, EffectTerminal: true, ExactExtent: false),
            UserProperties: properties, HdrScale: 2.5, HdrLowerBound: -0.75));
        var measured = await EffectPrefixStaticRangeProbe.MeasureAsync(scene, properties,
            observed["native_result"]!.AsObject(), source, Assets, LocalTools.Tools!, root, 17, 6,
            CancellationToken.None);
        Assert.True(measured.Bound is not null, measured.Detail);
        var bound = measured.Bound!.Value;
        Assert.InRange(bound.Lower, -0.02, 0);
        Assert.InRange(bound.Upper, 1.1, 1.11);
        JsonObject owner = scene["objects"]!.AsArray().OfType<JsonObject>().Single(o => o["id"]!.GetValue<int>() == 17)
            .DeepClone().AsObject();
        owner["effects"] = new JsonArray(owner["effects"]!.AsArray().Take(6).Select(e => e!.DeepClone()).ToArray());
        Assert.True(EffectPrefixRadianceBounds.TryProve(owner, properties, source, Assets,
            out double lo, out double hi, out string detail, bound), detail);
        Assert.InRange(hi - lo, 5.35, 5.4);
        output.WriteLine($"static head {bound.Lower:R}..{bound.Upper:R}; terminal {lo:R}..{hi:R}");
    });

    [Fact(SkipUnless = nameof(Available), Skip = "Requires the local HDR prefix corpus and official assets.")]
    public void ReviewedUvOnlyEffectsExtendTheSignedRange()
    {
        using var source = new ProjectSource(Source);
        JsonObject original = source.ReadJson(source.SceneResource)["objects"]!.AsArray().OfType<JsonObject>()
            .Single(o => o["id"]!.GetValue<int>() == 17).DeepClone().AsObject();
        var bound = new EffectPrefixRadianceBounds.StaticHeadBound(21, -0.0147171020508, 1.103515625,
            "full static head probe");
        JsonObject Prefix(int count)
        {
            JsonObject layer = original.DeepClone().AsObject();
            layer["effects"] = new JsonArray(layer["effects"]!.AsArray().Take(count).Select(e => e!.DeepClone()).ToArray());
            return layer;
        }
        var properties = new JsonObject { ["schemecolor"] = "0.33333 0.51765 0.55294" };
        Assert.True(EffectPrefixRadianceBounds.TryProve(Prefix(6), properties, source, Assets,
            out double lo6, out double hi6, out string detail, bound), detail);
        Assert.True(EffectPrefixRadianceBounds.TryProve(Prefix(7), properties, source, Assets,
            out double lo7, out double hi7, out detail, bound), detail);
        Assert.Contains("iris-resample", detail);
        Assert.True(EffectPrefixRadianceBounds.TryProve(Prefix(8), properties, source, Assets,
            out double lo8, out double hi8, out detail, bound), detail);
        Assert.Contains("shake-resample", detail);
        Assert.True(lo8 <= lo7 && lo7 <= lo6 && hi8 >= hi7 && hi7 >= hi6, detail);
        output.WriteLine($"prefix6={lo6:R}..{hi6:R}; prefix7={lo7:R}..{hi7:R}; prefix8={lo8:R}..{hi8:R}");

        JsonObject unsupported = Prefix(8);
        unsupported["effects"]![6]!["passes"]![0]!["combos"] = new JsonObject { ["BACKGROUND"] = 1 };
        Assert.False(EffectPrefixRadianceBounds.TryProve(unsupported, properties, source, Assets,
            out _, out _, out _));
        unsupported = Prefix(8);
        unsupported["effects"]![7]!["passes"]![0]!["combos"] = new JsonObject { ["AUDIOPROCESSING"] = 1 };
        Assert.False(EffectPrefixRadianceBounds.TryProve(unsupported, properties, source, Assets,
            out _, out _, out _));
    }

}
