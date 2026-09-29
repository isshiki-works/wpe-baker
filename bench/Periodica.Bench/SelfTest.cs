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

        bool Refuses(Action action)
        {
            try { action(); return false; } catch (InvalidDataException) { return true; }
        }
        WpeControl.RequireSingleMonitor(0, 1);
        Check(Refuses(() => WpeControl.RequireSingleMonitor(1, 1)) && Refuses(() => WpeControl.RequireSingleMonitor(0, 2)),
            "isolated loading rejects non-primary or multiple connected displays before control");
        var config = JsonNode.Parse("""
            {"test":{"general":{"wallpaperconfig":{"selectedwallpapers":{
              "Monitor0":{"file":"original","local":true,"playlist":false},
              "Monitor1":{"file":"disconnected-cache"}}}},"setting":42}}
            """)!.AsObject();
        Check(WpeControl.Assignment(config, "test")["file"]?.GetValue<string>() == "original",
            "disconnected assignments are preserved without pretending they are connected displays");
        var playlist = config.DeepClone().AsObject();
        WpeControl.Assignment(playlist, "test")["playlist"] = "favorites";
        Check(Refuses(() => WpeControl.Assignment(playlist, "test")), "active playlist is refused before switching");
        var during = config.DeepClone().AsObject();
        WpeControl.Assignment(during, "test")["file"] = "candidate";
        WpeControl.Assignment(during, "test").Remove("local");
        Check(WpeControl.ConfigMatchesSelection(config, during, "test"), "only our selected file and local removal are allowed during comparison");
        during["test"]!["setting"] = 43;
        Check(!WpeControl.ConfigMatchesSelection(config, during, "test"), "unrelated user configuration changes prevent automatic switching and restoration");
        var after = config.DeepClone().AsObject();
        WpeControl.Assignment(after, "test").Remove("local");
        Check(WpeControl.OnlyLocalFlagRemoved(config, after, "test"), "exact removal of local=true is the only repairable config difference");
        WpeControl.Assignment(after, "test")["file"] = "user-later-selection";
        Check(!WpeControl.OnlyLocalFlagRemoved(config, after, "test"), "a later user selection cannot be overwritten by the config backup");
        var closed = config.DeepClone().AsObject();
        closed["test"]!["general"]!["wallpaperconfig"]!["selectedwallpapers"]!.AsObject().Remove("Monitor0");
        Check(WpeControl.OnlyMonitorRemoved(config, closed, "test"), "a successful own close can be recovered after a failed open");
        closed["test"]!["setting"] = 43;
        Check(!WpeControl.OnlyMonitorRemoved(config, closed, "test"), "own close does not authorize overwriting another config change");

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

        JsonObject Comparison(double originalWatts, double bakedWatts)
        {
            var playback = new JsonArray(Playback("A", originalWatts), Playback("B", bakedWatts),
                Playback("B", bakedWatts), Playback("A", originalWatts));
            return new JsonObject { ["target_fps"] = 60, ["restored"] = true,
                ["segments"] = playback, ["summary"] = Abba.Summarize(playback) };
        }
        var worse = Comparison(10.3299, 11.4482);
        Check(Abba.Compare(worse)["choice"]?.GetValue<string>() == "original", "315-like measured regression chooses original");
        var same = Comparison(0.6893, 0.6930);
        Check(Abba.Compare(same)["choice"]?.GetValue<string>() == "indistinguishable", "Rem preview-sized difference gives no benefit claim");
        var saved = Comparison(10.168, 0.711);
        Check(Abba.Compare(saved)["choice"]?.GetValue<string>() == "baked", "Rem-like measured saving chooses baked");
        var mixed = Comparison(3.36866, 1.43179);
        mixed["summary"]!["original_package_watts"] = 10.0032;
        mixed["summary"]!["baked_package_watts"] = 10.5101;
        Check(Abba.Compare(mixed)["choice"]?.GetValue<string>() == "tradeoff" &&
            Abba.Compare(mixed)["gpu_choice"]?.GetValue<string>() == "baked", "194-like iGPU saving with higher package power is a tradeoff");
        saved["restored"] = false;
        Check(Abba.Compare(saved)["choice"]?.GetValue<string>() == "unavailable", "failed restoration withholds a choice");
        saved["restored"] = true;
        saved["segments"]![0]!["displayed_fps"] = 58;
        Check(Abba.Compare(saved)["choice"]?.GetValue<string>() == "unavailable", "material displayed-frame loss withholds a choice");
        saved["segments"]![0]!["displayed_fps"] = 59.98;
        Check(Abba.Compare(saved)["choice"]?.GetValue<string>() == "baked", "59.98 of 60 in a finite 900-frame sample passes");
        saved["segments"]![0]!["selected_verified"] = false;
        Check(Abba.Compare(saved)["choice"]?.GetValue<string>() == "unavailable", "unverified selected project withholds a choice");
        Check(Abba.Compare(Comparison(0.001, 0.002))["choice"]?.GetValue<string>() == "unavailable",
            "idle-scale iGPU telemetry cannot impersonate discrete-GPU playback cost");
        var fallback = Comparison(10, 6);
        fallback["segments"]![0]!["selected_evidence"] = "saved_configuration_getWallpaper_returned_empty";
        Check(Abba.Compare(fallback)["choice"]?.GetValue<string>() == "unavailable",
            "saved assignment fallback cannot impersonate official playback evidence");
        var idleRun = Comparison(10, 6);
        idleRun["idle_seconds"] = 10;
        var idleSegments = idleRun["segments"]!.AsArray();
        JsonObject Idle() => new() { ["kind"] = "I", ["power_status"] = "sampled", ["typeperf_status"] = "sampled",
            ["power_collector_complete"] = true, ["target_status"] = "valid", ["igpu_watts"] = 4.0, ["package_watts"] = 5.0,
            ["presentmon_status"] = "not_measured" };
        idleSegments.Insert(0, Idle());
        idleSegments.Add(Idle());
        idleRun["summary"] = Abba.Summarize(idleSegments);
        Check(Abba.Compare(idleRun)["choice"]?.GetValue<string>() == "baked", "idle power does not require displayed-frame evidence");
        idleSegments[0]!["power_collector_complete"] = false;
        Check(Abba.Compare(idleRun)["choice"]?.GetValue<string>() == "unavailable", "incomplete idle collector withholds comparison even with median readings");
        idleSegments[0]!["power_collector_complete"] = true;
        idleSegments[0]!["igpu_watts"] = null;
        idleRun["summary"] = Abba.Summarize(idleSegments);
        Check(Abba.Compare(idleRun)["choice"]?.GetValue<string>() == "unavailable" &&
            idleRun["summary"]!["gain"]!["status"]?.GetValue<string>() == "not_measured",
            "missing requested idle power cannot fall back to an uncorrected saving");

        var captured = new JsonObject
        {
            ["status"] = "sampled", ["playback"] = new JsonObject { ["selected_verified"] = true },
            ["platform_power"] = new JsonObject { ["status"] = "sampled", ["igpu_domain_watts"] = new JsonObject { ["median"] = 10.3 } },
            ["target_validation"] = new JsonObject { ["status"] = "valid", ["pid"] = 10020 },
            ["metadata"] = new JsonObject { ["target_fps"] = 60 }, ["display"] = new JsonObject { ["refresh_hz"] = 165 },
            ["presentmon"] = new JsonObject { ["status"] = "sampled", ["frame_pacing_scope"] = "single_swapchain",
                ["resolved_swapchain_address"] = "chain", ["swapchains"] = new JsonObject { ["chain"] = new JsonObject
                    { ["displayed_rows"] = 900, ["displayed_fps"] = 59.98, ["dropped_rows"] = 0,
                      ["display_evidence_complete"] = true } } }
        };
        JsonObject extracted = Abba.Segment("01-A", 'A', captured);
        Check(extracted["pid"]?.GetValue<int>() == 10020 && extracted["displayed_rows"]?.GetValue<int>() == 900 &&
            extracted["displayed_fps"]?.GetValue<double>() == 59.98, "official report fields reach the comparison segment");

        long tick = Stopwatch.Frequency / 60;
        JsonObject pace = Pacer.Summarize([0, tick, 2 * tick, 3 * tick, 6 * tick], 60);
        Check(Math.Abs(pace["achieved_fps"]!.GetValue<double>() - 40) < 1e-3 && pace["over_budget_intervals"]!.GetValue<int>() == 1 &&
            Math.Abs(Pacer.Quantile([1, 2, 3, 4], 0.5) - 2.5) < 1e-12, "pace statistics count the late frame");

        Console.WriteLine(failed == 0 ? "selftest: all passed" : $"selftest: {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static JsonObject Seg(string kind, double? watts) => new() { ["kind"] = kind, ["igpu_watts"] = watts };

    private static JsonObject Playback(string kind, double watts) => new()
    {
        ["kind"] = kind, ["label"] = kind, ["status"] = "sampled", ["power_status"] = "sampled",
        ["presentmon_status"] = "sampled", ["target_status"] = "valid", ["selected_verified"] = true,
        ["frame_pacing_scope"] = "single_swapchain", ["display_evidence_complete"] = true,
        ["target_fps"] = 60, ["refresh_hz"] = 165, ["pid"] = 10020, ["displayed_rows"] = 900,
        ["displayed_fps"] = 59.98, ["dropped_rows"] = 0, ["igpu_watts"] = watts,
        ["package_watts"] = watts + 5, ["gpu_adapters"] = new JsonObject { ["one"] = new JsonObject() },
        ["incomplete_reasons"] = new JsonArray()
    };
}
