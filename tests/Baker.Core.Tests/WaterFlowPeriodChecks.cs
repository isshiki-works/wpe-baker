using System.Text.Json.Nodes;
using Baker.Core;

internal static class WaterFlowPeriodChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "water-flow-period-source");
        Directory.CreateDirectory(sourceDirectory);
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        const string shader = """
            uniform float g_Time;
            uniform float g_FlowSpeed; // {"material":"speed","default":1}
            float flowPhase = texSample2D(g_Texture2, v_TexCoord.xy * g_FlowPhaseScale).r;
            vec4 cycles = vec4(frac(g_Time * g_FlowSpeed),
                               frac(g_Time * g_FlowSpeed + 0.5),
                               frac(0.25 + g_Time * g_FlowSpeed),
                               frac(0.25 + g_Time * g_FlowSpeed + 0.5));
            float outputPhase = flowPhase + cycles.x;
            """;
        Write("project.json", "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        foreach (string name in new[] { "flow", "flow-drift", "flow-conditional" })
        {
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
            Write($"materials/{name}.json", $"{{\"passes\":[{{\"shader\":\"{name}\"}}]}}");
        }
        Write("shaders/flow.frag", shader);
        Write("shaders/flow-drift.frag", shader + "\nuv += g_Time * 0.125;");
        Write("shaders/flow-conditional.frag", "#if MODE\n" + shader + "\n#endif");
        JsonObject Owner(int id, string effect) => new() {
            ["id"] = id,
            ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{effect}.json",
                ["passes"] = new JsonArray(new JsonObject {
                    ["constantshadervalues"] = new JsonObject { ["speed"] = .19 } }) }) };
        var scene = new JsonObject { ["objects"] = new JsonArray(Owner(1, "flow"), Owner(2, "flow-drift"), Owner(3, "flow-conditional")) };
        Write("scene.json", scene.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, null, [1, 2, 3]);

        ShaderPeriodComponent flow = analysis.Components.Single();
        check(flow.Component.AllowRetime && flow.Patch.ConstantKey == "speed" &&
            Math.Abs(flow.Component.BasePeriod!.Seconds - 1 / .19) < 1e-12,
            "four water-flow frac phases yield one analytic reciprocal-speed period");
        check(analysis.Unresolved.Count == 2 && analysis.Unresolved.All(x =>
                x.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism),
            "extra time drift and an effective preprocessor branch keep water-flow-like shaders unresolved");

        var sprite = new CommonLoopComponent("sprite", new CommonLoopPeriod(4.53,
            CommonLoopPeriodEvidence.Observed, new CommonLoopRational(453, 100)));
        CommonLoopSearchResult joint = CommonLoopSolver.Suggest(new(60, 1, [flow.Component, sprite], MaximumRetimePercent: 2));
        CommonLoopCandidate candidate = joint.Candidates.Single(x => x.Frames == 4077);
        CommonLoopComponentCycle cycle = candidate.Components.Single(x => x.ComponentId == flow.Component.Id);
        double patchedSpeed = .19 * cycle.SpeedMultiplier;
        check(joint.FixedFrameStep == 1359 && cycle.Cycles == 13 && Math.Abs(cycle.DeltaPercent) < 2 &&
            Math.Abs(patchedSpeed - 13 / 67.95) < 1e-12,
            "water flow retimes within two percent to a frame-exact common loop with a 4.53-second sprite");
    }
}
