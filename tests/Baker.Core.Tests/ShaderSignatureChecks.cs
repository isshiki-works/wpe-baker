using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>引擎时间签名原因码到结论的映射：分析没推下去的只记未收敛，不当"不能"的证明。</summary>
internal static class ShaderSignatureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Directory.CreateTempSubdirectory("shader-signature-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "scene.json"), """{"objects":[{"id":10}]}""");
            using var source = new ProjectSource(root);
            foreach (string reason in (string[])["analysis_not_converged:op199 @fragment", "analysis_not_converged @fragment", "unsupported_side_effect",
                "names_stripped", "spirv_unreadable", "time_rate_not_constant @fragment", "scroll_rate_not_constant:s @fragment",
                "drift_rate_not_constant @fragment", "too_many_periods", "sampler_wrap_unknown:s @fragment"])
            {
                var result = Analyze(source, reason);
                check(result.Unresolved.Count == 1 && result.Unresolved[0].Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                    $"engine reason '{reason}' stays not converged");
            }
            var drift = Analyze(source, "drift @fragment");
            check(drift.Unresolved.Count == 1 && drift.Unresolved[0].Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                "a drift reason is a proof of cannot");
        }
        finally { Directory.Delete(root, true); }
    }

    private static ShaderPeriodAnalysisResult Analyze(ProjectSource source, string reason) =>
        ShaderPeriodAnalysis.Analyze(JsonNode.Parse("""{"objects":[{"id":10}]}""")!.AsObject(), source, null,
            new JsonObject { ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 10, ["materials"] = new JsonArray(new JsonObject {
                ["shader"] = "effects/x", ["active_uniforms"] = new JsonArray(),
                ["time_signature"] = new JsonObject { ["kind"] = "aperiodic", ["periods"] = new JsonArray(), ["reasons"] = new JsonArray(reason),
                    ["external"] = new JsonArray(), ["transient"] = false } }) }) }, [10]);
}
