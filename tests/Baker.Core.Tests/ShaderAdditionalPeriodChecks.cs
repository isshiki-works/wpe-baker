using System.Text.Json.Nodes;
using Baker.Core;

internal static class ShaderAdditionalPeriodChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "shader-additional-period-source");
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        const string metadata = """
            // [COMBO] {"combo":"AUDIOPROCESSING","type":"audioprocessingoptions","default":0}
            uniform float g_PulseSpeed; // {"material":"speed","default":3}
            uniform float g_NoiseSpeed; // {"material":"noisespeed","default":0.5}
            uniform float g_NoiseAmount; // {"material":"noiseamount","default":0}
            """;
        const string pulse = """
            pulse = sin(g_Time * g_PulseSpeed + phase);
            float noise = texSample2D(g_Texture1, vec2(g_Time * 0.08333333, g_Time * 0.02777777) * g_NoiseSpeed).r * g_NoiseAmount;
            pulse += noise;
            """;
        string single = "uniform float g_Time;\n" + metadata + pulse;
        string dualFragment = "uniform float g_Time;\n" + metadata + pulse;
        string dualVertex = "uniform float g_Time;\nuniform float g_PulseSpeed; // {\"material\":\"speed\",\"default\":3}\n" +
            "value = sin(g_Time * g_PulseSpeed + otherPhase);";
        foreach (string name in new[] { "single", "dual", "noise", "audio", "drift" })
        {
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
            Write($"materials/{name}.json", $"{{\"passes\":[{{\"shader\":\"{name}\"}}]}}");
        }
        Write("shaders/single.frag", single);
        Write("shaders/dual.frag", dualFragment); Write("shaders/dual.vert", dualVertex);
        Write("shaders/noise.frag", single);
        Write("shaders/audio.frag", single);
        Write("shaders/drift.frag", single + "\nuv += g_Time * 0.125;");
        JsonObject Owner(int id, string name, double speed, double noiseAmount, int? audio = null)
        {
            var pass = new JsonObject { ["constantshadervalues"] = new JsonObject {
                ["speed"] = speed, ["noiseamount"] = noiseAmount } };
            if (audio is int value) pass["combos"] = new JsonObject { ["AUDIOPROCESSING"] = value };
            return new JsonObject { ["id"] = id, ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{name}.json", ["passes"] = new JsonArray(pass) }) };
        }
        var scene = new JsonObject { ["objects"] = new JsonArray(
            Owner(1, "single", 2, 0), Owner(2, "dual", 1.5, 0), Owner(3, "noise", 2, .1),
            Owner(4, "audio", 2, 0, 1), Owner(5, "drift", 2, 0)) };
        Write("scene.json", scene.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, null, [1, 2, 3, 4, 5]);

        ShaderPeriodComponent[] pulses = analysis.Components.OrderBy(item => item.Patch.OwnerLayerId).ToArray();
        check(pulses.Length == 2 && pulses[0].Patch.OwnerLayerId == 1 && pulses[1].Patch.OwnerLayerId == 2 &&
            pulses.All(item => item.Component.AllowRetime) &&
            Math.Abs(pulses[0].Component.BasePeriod!.Seconds - Math.PI) < 1e-12 &&
            Math.Abs(pulses[1].Component.BasePeriod!.Seconds - 4 * Math.PI / 3) < 1e-12,
            "one- and two-stage pulse shaders with disabled noise yield the same reciprocal-speed sine mechanism");
        check(analysis.Unresolved.Single(item => item.OwnerLayerId == 3).Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism &&
            analysis.Unresolved.Single(item => item.OwnerLayerId == 4).Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            "pulse noise drift and external audio remain unresolved");
        check(analysis.Unresolved.Single(item => item.OwnerLayerId == 5).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
            "an additional pulse time input is not hidden by the noise-disabled mechanism");

        const string dualShader = """
            uniform float g_Time;
            uniform float g_Speed; // {"material":"speed","default":5}
            uniform float g_Speed2; // {"material":"speed2","default":3}
            uniform float g_Offset2; // {"material":"offset2","default":0}
            float distance = g_Time * g_Speed + dot(texCoordMotion, v_Direction) * g_Scale;
            float distance2 = (g_Time + g_Offset2) * g_Speed2 + dot(texCoordMotion, v_Direction2) * g_Scale2;
            float val1 = sin(distance);
            float val2 = sin(distance2);
            texCoord += val1 * s1 * val2 * s2 * offset * strength * mask;
            """;
        foreach (string name in new[] { "dual-wave", "dual-wave-drift" })
        {
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
            Write($"materials/{name}.json", $"{{\"passes\":[{{\"shader\":\"{name}\"}}]}}");
            Write($"shaders/{name}.frag", dualShader + (name.EndsWith("drift", StringComparison.Ordinal) ? "\nuv += g_Time * 0.125;" : ""));
            Write($"shaders/{name}.vert", "uniform float g_Time;");
        }
        JsonObject DualOwner(int id, string name, double speed1, double speed2, JsonNode offset2)
        {
            var pass = new JsonObject {
                ["combos"] = new JsonObject { ["DUALWAVES"] = 1 },
                ["constantshadervalues"] = new JsonObject {
                    ["speed"] = speed1, ["speed2"] = speed2, ["offset2"] = offset2.DeepClone() } };
            return new JsonObject { ["id"] = id, ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{name}.json", ["passes"] = new JsonArray(pass) }) };
        }
        var dualScene = new JsonObject { ["objects"] = new JsonArray(
            DualOwner(6, "dual-wave", 7, 2, JsonValue.Create(0.0)!),
            DualOwner(7, "dual-wave", 4, 3, JsonValue.Create(-5.0)!),
            DualOwner(8, "dual-wave", Math.Sqrt(2), 1, JsonValue.Create(0.0)!),
            DualOwner(9, "dual-wave-drift", 5, 4, JsonValue.Create(0.0)!),
            DualOwner(10, "dual-wave", 1, 1, new JsonObject { ["script"] = "return 0", ["value"] = 0.0 })) };
        using var dualSource = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult dualAnalysis = ShaderPeriodAnalysis.Analyze(dualScene, dualSource, null, [6, 7, 8, 9, 10]);
        ShaderPeriodComponent[] dual6 = dualAnalysis.Components.Where(item => item.Patch.OwnerLayerId == 6).ToArray();
        ShaderPeriodComponent[] dual7 = dualAnalysis.Components.Where(item => item.Patch.OwnerLayerId == 7).ToArray();
        check(dual6.Length == 2 && dual7.Length == 2 && dual6.Select(item => item.Component.BasePeriod!.Seconds).Distinct().Single() == 2 * Math.PI &&
            dual7.Select(item => item.Component.BasePeriod!.Seconds).Distinct().Single() == 2 * Math.PI &&
            dual6.All(item => item.Patch.CompanionConstantKey is null) &&
            dual7.Single(item => item.Patch.ConstantKey == "speed2").Patch.CompanionConstantKey == "offset2",
            "simple-rational dual speeds share one gcd period and only nonzero phase gets a companion patch");
        check(dualAnalysis.Unresolved.Single(item => item.OwnerLayerId == 8).Kind == ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed &&
            dualAnalysis.Unresolved.Single(item => item.OwnerLayerId == 9).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            dualAnalysis.Unresolved.Single(item => item.OwnerLayerId == 10).Kind == ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
            "non-simple speed, extra clock, and unknown second phase remain unresolved");

        JsonObject dualReport = HybridLoopService.Analyze(dualScene, dualSource, null, new JsonObject(), [7], 60, 1);
        JsonObject dualCandidate = dualReport["candidates"]!.AsArray().First()!.AsObject();
        JsonObject[] dualCycles = dualCandidate["components"]!.AsArray().OfType<JsonObject>().ToArray();
        JsonObject[] dualPatches = dualCandidate["patches"]!.AsArray().OfType<JsonObject>().ToArray();
        double multiplier1 = dualCycles.Single(item => item["id"]!.GetValue<string>().EndsWith("/speed", StringComparison.Ordinal))["speed_multiplier"]!.GetValue<double>();
        double multiplier2 = dualCycles.Single(item => item["id"]!.GetValue<string>().EndsWith("/speed2", StringComparison.Ordinal))["speed_multiplier"]!.GetValue<double>();
        double newSpeed1 = dualPatches.Single(item => item["constant_key"]!.GetValue<string>() == "speed")["new_value"]!.GetValue<double>();
        double newSpeed2 = dualPatches.Single(item => item["constant_key"]!.GetValue<string>() == "speed2")["new_value"]!.GetValue<double>();
        JsonObject phasePatch = dualPatches.Single(item => item["kind"]!.GetValue<string>() == "shader_phase");
        double newOffset2 = phasePatch["new_value"]!.GetValue<double>();
        check(multiplier1 == multiplier2 && Math.Abs(newSpeed2 / newSpeed1 - .75) < 1e-12 &&
            Math.Abs(newOffset2 * newSpeed2 - -15) < 1e-12 && phasePatch["speed_exponent"]!.GetValue<double>() == -1,
            "dual speed patches share one multiplier while inverse phase compensation preserves speed ratio and initial phase");
        JsonObject patchedDual = dualScene.DeepClone().AsObject();
        HybridLoopService.ApplyPatches(patchedDual, dualReport);
        JsonObject patchedValues = patchedDual["objects"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<int>() == 7)
            ["effects"]![0]!["passes"]![0]!["constantshadervalues"]!.AsObject();
        check(Math.Abs(patchedValues["speed2"]!.GetValue<double>() / patchedValues["speed"]!.GetValue<double>() - .75) < 1e-12 &&
            Math.Abs(patchedValues["offset2"]!.GetValue<double>() * patchedValues["speed2"]!.GetValue<double>() - -15) < 1e-12,
            "applying dual-wave patches keeps the authored speed ratio and t=0 phase in the capture scene");

        const string shimmer = """
            // [COMBO] {"material":"ui_editor_properties_style","combo":"MODE","default":0,"options":{"ui_editor_properties_linear":0,"ui_editor_properties_mirror":1}}
            uniform float g_Time;
            uniform float u_scale; // {"material":"ui_editor_properties_granularity","default":1,"range":[1,5]}
            uniform float u_speed; // {"material":"ui_editor_properties_speed","default":1,"range":[0,5]}
            uniform float u_delay; // {"material":"ui_editor_properties_delay","default":2,"range":[1,5]}
            vec2 shimmerCoord = rotateVec2(v_TexCoord, -u_direction + 1.57079632679) * u_scale;
            #if MODE == 1
            shimmerCoord.x += u_offset + u_width * sin(u_speed * g_Time + offset);
            #else
            shimmerCoord.x += u_offset + u_speed * (g_Time + offset);
            #endif
            shimmerCoord.x = saturate(frac(shimmerCoord.x / (u_scale * u_delay)) * u_scale * u_delay);
            """;
        foreach (string name in new[] { "shimmer", "shimmer-mode", "shimmer-formula" })
        {
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
            Write($"materials/{name}.json", $"{{\"passes\":[{{\"shader\":\"{name}\"}}]}}");
        }
        Write("shaders/shimmer.frag", shimmer);
        Write("shaders/shimmer.vert", "attribute vec3 a_Position;");
        Write("shaders/shimmer-mode.frag", shimmer);
        Write("shaders/shimmer-formula.frag", shimmer.Replace("g_Time + offset", "g_Time - offset", StringComparison.Ordinal));
        JsonObject ShimmerOwner(int id, string name, int? mode = null)
        {
            var pass = new JsonObject { ["constantshadervalues"] = new JsonObject {
                ["ui_editor_properties_granularity"] = 1.0,
                ["ui_editor_properties_delay"] = 1.77,
                ["ui_editor_properties_speed"] = .44 } };
            if (mode is int value) pass["combos"] = new JsonObject { ["MODE"] = value };
            return new JsonObject { ["id"] = id, ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{name}.json", ["passes"] = new JsonArray(pass) }) };
        }
        var shimmerScene = new JsonObject { ["objects"] = new JsonArray(
            ShimmerOwner(11, "shimmer"), ShimmerOwner(12, "shimmer-mode", 1), ShimmerOwner(13, "shimmer-formula")) };
        using var shimmerSource = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult shimmerAnalysis = ShaderPeriodAnalysis.Analyze(shimmerScene, shimmerSource, null, [11, 12, 13]);
        ShaderPeriodComponent shimmerComponent = shimmerAnalysis.Components.Single();
        check(shimmerComponent.Patch.OwnerLayerId == 11 && shimmerComponent.Patch.ConstantKey == "ui_editor_properties_speed" &&
            shimmerComponent.Component.AllowRetime && Math.Abs(shimmerComponent.Component.BasePeriod!.Seconds - 1.77 / .44) < 1e-12,
            "the real linear shimmer source default MODE=0 branch yields granularity*delay/speed");
        check(shimmerAnalysis.Unresolved.Count == 2 && shimmerAnalysis.Unresolved.All(item =>
                item.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism),
            "mirror MODE and a changed shimmer time formula remain outside the verified linear family");
        JsonObject singleCycleScene = shimmerScene.DeepClone().AsObject();
        var singleValues = singleCycleScene["objects"]![0]!["effects"]![0]!["passes"]![0]!["constantshadervalues"]!.AsObject();
        singleValues["ui_editor_properties_delay"] = 4.3699999;
        singleValues["ui_editor_properties_speed"] = .69999999;
        JsonObject singleCycle = HybridLoopService.Analyze(singleCycleScene, shimmerSource, null, new JsonObject(), [11], 60, 1);
        check(singleCycle["candidates"]![0]!["frames"]!.GetValue<ulong>() == 375 &&
            singleCycle["candidates"]![0]!["components"]![0]!["cycles"]!.GetValue<ulong>() == 1,
            "one retimable 6.24-second source cycle uses 375 frames instead of seven traversals to avoid a tiny retime");
    }
}
