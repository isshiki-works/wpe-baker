using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>pulse、双水波、shimmer：裁定断言在 tests/corpus/shader-corpus.json 的 pulse / dual-wave / shimmer；这里只留求解器与补丁断言。</summary>
internal static class ShaderAdditionalPeriodChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        ShaderCorpusChecks.Run(check, root, "pulse");
        // 指纹统一在归一化文本上求值：空白写法不同照样认，落在注释里的"代码"不算，注释元数据按 JSON 核对。
        ShaderCorpusChecks.Run(check, root, "whitespace");
        ShaderCorpusChecks.Run(check, root, "shine-edges-default");
        ShaderCorpusChecks.Run(check, root, "shine-edges-default3");
        // 注释元数据的数字按解析后的数值比：1.0 / 2.0 / 0.0 与规则里的 1 / 2 / 0 相同。
        ShaderCorpusChecks.Run(check, root, "shimmer-decimal-defaults");
        // 门控与指纹同一套求值：#if 行按归一化文本找，[COMBO] 缺省值按解析后的 JSON 比。
        ShaderCorpusChecks.Run(check, root, "gate-parsed");
        ShaderCorpusRun dual = ShaderCorpusChecks.Run(check, root, "dual-wave");
        using var dualSource = new ProjectSource(dual.Directory);
        JsonObject dualScene = dual.Scene;
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

        ShaderCorpusRun shimmerRun = ShaderCorpusChecks.Run(check, root, "shimmer");
        using var shimmerSource = new ProjectSource(shimmerRun.Directory);
        JsonObject shimmerScene = shimmerRun.Scene;
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
