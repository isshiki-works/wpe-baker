using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// waterripple 法线贴图滚动与结构化正弦时钟两条规则。裁定断言在 tests/corpus/shader-corpus.json 的 water-ripple /
/// structured-sine（着色器只保留与时间有关的骨架行，计数与官方源一致）；这里只留候选补丁断言。
/// </summary>
internal static class WaterRippleSineClockChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        ShaderCorpusRun ripple = ShaderCorpusChecks.Run(check, root, "water-ripple");
        using var source = new ProjectSource(ripple.Directory);
        JsonObject rippleReport = HybridLoopService.Analyze(ripple.Scene, source, null, new JsonObject(), [1], 60, 1);
        JsonObject ripplePatch = rippleReport["candidates"]!.AsArray().First()!["patches"]!.AsArray().OfType<JsonObject>()
            .Single(patch => patch["constant_key"]!.GetValue<string>() == "animationspeed");
        check(ripplePatch["speed_exponent"]!.GetValue<double>() == 2 && ripplePatch["owner_layer_id"]!.GetValue<int>() == 1,
            "the water-ripple candidate rewrites animationspeed with the squared-speed exponent");

        // 循环分析把求解器的调速预算原样交给着色器裁定：400 s 的水波在 390 s 上限下要提速 2.56%，
        // 平衡档 3% 预算下成为候选里的补丁，2% 预算下留作未解析项。
        bool PatchesLayer10(double retimePercent) => HybridLoopService.Analyze(ripple.Scene, source, null, new JsonObject(), [10], 60, 1,
                retimePercent, loopLengthMaximumSeconds: 390)["candidates"]!.AsArray().OfType<JsonObject>()
            .Any(candidate => candidate["patches"]!.AsArray().OfType<JsonObject>().Any(patch => patch["owner_layer_id"]!.GetValue<int>() == 10));
        check(PatchesLayer10(3) && !PatchesLayer10(2),
            "loop analysis passes its retime budget to the water-ripple ceiling check instead of a fixed 2%");

        ShaderCorpusChecks.Run(check, root, "structured-sine");
    }
}
