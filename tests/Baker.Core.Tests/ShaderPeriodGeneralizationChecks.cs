using System.Text.Json.Nodes;
using Baker.Core;

internal static class ShaderPeriodGeneralizationChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "shader-period-generalization-source");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "effects"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "materials"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "shaders"));
        File.WriteAllText(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(sourceDirectory, "effects", "shake.json"), "{\"passes\":[{\"material\":\"materials/shake.json\"}]}");
        File.WriteAllText(Path.Combine(sourceDirectory, "effects", "shake-drift.json"), "{\"passes\":[{\"material\":\"materials/shake-drift.json\"}]}");
        File.WriteAllText(Path.Combine(sourceDirectory, "materials", "shake.json"), "{\"passes\":[{\"shader\":\"shake\"}]}");
        File.WriteAllText(Path.Combine(sourceDirectory, "materials", "shake-drift.json"), "{\"passes\":[{\"shader\":\"shake-drift\"}]}");
        const string shakeShader = """
            // [COMBO] {"combo":"NOISE","type":"options","default":0}
            // [COMBO] {"combo":"AUDIOPROCESSING","type":"audioprocessingoptions","default":0}
            #if AUDIOPROCESSING == 0
            float flowPhase = 0.0;
            flowPhase = texSample2D(g_Texture2, v_TexCoord.zw).r * M_PI_2;
            #if NOISE
            vec4 sines = flowPhase + frac(g_Speed * g_Time / M_PI_2 * vec4(1, -0.16161616, 0.0083333, -0.00019841)) * M_PI_2;
            #else
            float time = g_Speed * g_Time + flowPhase;
            offset = sin(frac(time / M_PI_2) * M_PI_2);
            float base = step(0.0, cos(time));
            #endif
            #endif
            """;
        File.WriteAllText(Path.Combine(sourceDirectory, "shaders", "shake.frag"), "uniform float g_Time;\n" + shakeShader);
        File.WriteAllText(Path.Combine(sourceDirectory, "shaders", "shake-drift.frag"),
            "uniform float g_Time;\n" + shakeShader + "\nuv += g_Time * 0.125;");
        var objects = new JsonArray {
            Owner(1, 1.25),
            Owner(2, .5, new JsonObject { ["NOISE"] = 1 }),
            Owner(3, .75, new JsonObject { ["AUDIOPROCESSING"] = 1 }),
            Owner(4, 1, effect: "effects/shake-drift.json") };
        File.WriteAllText(Path.Combine(sourceDirectory, "scene.json"), new JsonObject { ["objects"] = objects }.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(source.ReadJson(source.SceneResource), source, null, [1, 2, 3, 4]);

        ShaderPeriodComponent shake = analysis.Components.Single();
        check(shake.Component.AllowRetime && shake.Patch.OwnerLayerId == 1 && shake.Patch.ConstantKey == "speed" &&
            Math.Abs(shake.Component.BasePeriod!.Seconds - 2 * Math.PI / 1.25) < 1e-12,
            "wrapped shake sin/cos timing yields one analytic retimable speed component");
        ShaderTemporalUnresolved[] variants = analysis.Unresolved.Where(x => x.OwnerLayerId is 2 or 3).ToArray();
        check(variants.Length == 2 && variants.All(x =>
                x.Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism) &&
            variants.Any(x => x.Detail.Contains("NOISE", StringComparison.Ordinal)) &&
            variants.Any(x => x.Detail.Contains("audio", StringComparison.OrdinalIgnoreCase)),
            "shake noise and audio variants remain unresolved");
        check(analysis.Unresolved.Single(x => x.OwnerLayerId == 4).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
            "a shake-like shader with an additional time drift is not reduced to the wrapped period");
    }

    private static JsonObject Owner(int id, double speed, JsonObject? combos = null, string effect = "effects/shake.json") => new() {
        ["id"] = id,
        ["effects"] = new JsonArray { new JsonObject {
            ["file"] = effect,
            ["passes"] = new JsonArray { new JsonObject {
                ["combos"] = combos,
                ["constantshadervalues"] = new JsonObject { ["speed"] = speed } } } } } };
}
