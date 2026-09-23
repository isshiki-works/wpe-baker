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

        ShaderCorpusChecks.Run(check, root, "structured-sine");
    }
}
