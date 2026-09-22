using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class OfficialTraceLossChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(false, "C0.3 CI 红灯演练：故意让 L0 测试失败");
        var apply = typeof(OfficialPerformanceSampler).GetMethod("ApplyTraceLoss", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (string diagnostics in new[] { "warning: 359413 ETW events were lost.", "warning: 2 ETW buffers were lost." })
        {
            var summary = new JsonObject { ["status"] = "sampled", ["verified_target_fps"] = true };
            apply.Invoke(null, [summary, diagnostics]);
            check(summary["status"]!.GetValue<string>() == "incomplete_trace" &&
                !summary["verified_target_fps"]!.GetValue<bool>(), "ETW loss invalidates even an otherwise valid displayed-FPS sample");
        }
        var intact = new JsonObject { ["status"] = "sampled", ["verified_target_fps"] = true };
        apply.Invoke(null, [intact, "Started recording.\nwarning: 0 ETW events were lost.\nStopped recording."]);
        check(intact["verified_target_fps"]!.GetValue<bool>(), "Successful logging or zero lost events does not invalidate cadence");
    }
}
