using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Periodica.Bench;

/// <summary>
/// 不碰 WPE、GPU、计数器的自测（CI 跑）：只核对纯函数——ETW 丢事件判废、PresentMon 缺 CSV 的原因、
/// 段序展开、空闲基线扣除与省电判定、节拍统计。每条都对应一处删了会出错的代码。
/// </summary>
internal static class SelfTest
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "pass  " : "FAIL  ") + name);
            if (!ok) ++failed;
        }

        // 原 tests/Baker.Core.Tests/OfficialTraceLossChecks：PresentMon 丢过 ETW 事件，CSV 就不能证明显示节奏。
        foreach (string diagnostics in new[] { "warning: 359413 ETW events were lost.", "warning: 2 ETW buffers were lost." })
        {
            var trace = new JsonObject { ["status"] = "sampled", ["verified_target_fps"] = true };
            OfficialPerformanceSampler.ApplyTraceLoss(trace, diagnostics);
            Check(trace["status"]!.GetValue<string>() == "incomplete_trace" && !trace["verified_target_fps"]!.GetValue<bool>(),
                "ETW loss invalidates an otherwise valid displayed-FPS sample: " + diagnostics);
        }
        var intact = new JsonObject { ["status"] = "sampled", ["verified_target_fps"] = true };
        OfficialPerformanceSampler.ApplyTraceLoss(intact, "Started recording.\nwarning: 0 ETW events were lost.\nStopped recording.");
        Check(intact["verified_target_fps"]!.GetValue<bool>(), "zero lost events does not invalidate cadence");

        JsonObject noCsv = OfficialPerformanceSampler.PresentSummary(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv"), 60, null);
        Check(noCsv["status"]?.GetValue<string>() == "not_measured" && noCsv["reason_key"]?.GetValue<string>() == "reason.presentmon_no_csv" &&
            noCsv["reason"]?.GetValue<string>() == Reason.Texts["reason.presentmon_no_csv"], "missing PresentMon CSV is not_measured with its reason");

        Check(Abba.Phases("ABBA", 0) == "ABBA" && Abba.Phases("ABBA", 30) == "IABBAI" && Abba.Phases("A", 10) == "IAI",
            "idle baseline wraps the requested order");
        bool rejected = false;
        try { Abba.Phases("ABX", 0); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "order letters other than A/B are rejected");

        Check(Abba.Gain(10, 6.9, null)["verdict"]?.GetValue<string>() == "saved", "31% lower is saved");
        Check(Abba.Gain(10, 7.1, null)["verdict"]?.GetValue<string>() == "same", "29% lower is level");
        Check(Abba.Gain(10, 10.6, null)["verdict"]?.GetValue<string>() == "worse", "6% higher is worse");
        Check(Abba.Gain(null, 5, null)["status"]?.GetValue<string>() == "not_measured", "missing original gives no verdict");
        // 空闲 4 W：原作增量 6 W、成品增量 3 W → 降 50%；不扣基线只降 30%，判定口径不同。
        JsonObject withIdle = Abba.Gain(10, 7, 4);
        Check(withIdle["verdict"]?.GetValue<string>() == "saved" && Math.Abs(withIdle["change_percent"]!.GetValue<double>() + 50) < 1e-9 &&
            withIdle["baseline_subtracted"]!.GetValue<bool>(), "idle baseline is subtracted before comparing");

        var segments = new JsonArray(
            Seg("I", 4), Seg("A", 10), Seg("B", 6), Seg("B", 8), Seg("A", 12), Seg("I", 4));
        JsonObject summary = Abba.Summarize(segments);
        Check(summary["original_igpu_watts"]!.GetValue<double>() == 11 && summary["baked_igpu_watts"]!.GetValue<double>() == 7 &&
            summary["idle_igpu_watts"]!.GetValue<double>() == 4, "segments of the same kind are averaged");
        segments.Add(Seg("B", null));
        Check(Abba.Summarize(segments)["gain"]!["status"]!.GetValue<string>() == "not_measured", "one unmeasured segment withholds the verdict");

        long tick = Stopwatch.Frequency / 60;
        JsonObject pace = Pacer.Summarize([0, tick, 2 * tick, 3 * tick, 6 * tick], 60);
        Check(Math.Abs(pace["achieved_fps"]!.GetValue<double>() - 40) < 1e-3 && pace["over_budget_intervals"]!.GetValue<int>() == 1 &&
            Math.Abs(Pacer.Quantile([1, 2, 3, 4], 0.5) - 2.5) < 1e-12, "pace statistics count the late frame");

        Console.WriteLine(failed == 0 ? "selftest: all passed" : $"selftest: {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static JsonObject Seg(string kind, double? watts) => new() { ["kind"] = kind, ["igpu_watts"] = watts };
}
