using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

if (args is ["--sleep-child"])
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}
string repository = Path.GetFullPath(args.Length == 1 ? args[0] : Environment.CurrentDirectory);

var passed = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    passed.Add(name);
}
MethodInfo Private(string name) => typeof(OfficialPerformanceSampler).GetMethod(name,
    BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(name);
JsonObject Present(string path, double target = 100, string? selected = null, int? pid = null, bool display = true) =>
    (JsonObject)(Private("PresentSummary").Invoke(null, [path, target, selected, pid, display]) ?? throw new InvalidOperationException());
JsonObject Typeperf(byte[] bytes, string[] counters, string[] gpu, string[] memory, string[]? energy = null) =>
    (JsonObject)(Private("BuildTypeperfSummary").Invoke(null, [bytes, counters, gpu, memory, energy ?? []])
        ?? throw new InvalidOperationException());
JsonObject VerdictOf(JsonObject power, double threshold) =>
    (JsonObject)(Private("Verdict").Invoke(null, [power, threshold]) ?? throw new InvalidOperationException());
string Csv(params string[] fields) => string.Join(',', fields.Select(field => '"' + field.Replace("\"", "\"\"") + '"'));

string root = Path.Combine(Path.GetTempPath(), "official-sampler-cpu-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string presentPath = Path.Combine(root, "present.csv");
await File.WriteAllTextAsync(presentPath, string.Join("\r\n",
    Csv("Application", "SwapChainAddress", "msBetweenPresents", "Dropped"),
    Csv("Wallpaper, 64-bit", "0xA", "8", "false"),
    Csv("Wallpaper \"quoted\"", "0xA", "9", "true")) + "\r\n", new UTF8Encoding(true));
JsonObject present = Present(presentPath);
Check(present["status"]!.GetValue<string>() == "sampled" &&
    present["swapchains"]!["0xA"]!["interval_ms"]!["samples"]!.GetValue<int>() == 2 &&
    Math.Abs(present["swapchains"]!["0xA"]!["effective_fps"]!.GetValue<double>() - 1000 / 8.5) < 1e-9 &&
    present["swapchains"]!["0xA"]!["dropped_rows"]!.GetValue<int>() == 1 &&
    present["swapchains"]!["0xA"]!["displayed_fps"] is null &&
    !present["verified_target_fps"]!.GetValue<bool>(), "quoted PresentMon CSV preserves submission FPS without claiming displayed FPS");

string multiplePath = Path.Combine(root, "multiple.csv");
await File.WriteAllTextAsync(multiplePath, string.Join("\n",
    Csv("SwapChainAddress", "msBetweenPresents", "msBetweenDisplayChange", "Dropped"),
    Csv("0xA", "8", "8", "0"), Csv("0xB", "10", "10", "0")));
JsonObject ambiguous = Present(multiplePath, 90);
Check(ambiguous["status"]!.GetValue<string>() == "sampled" &&
    ambiguous["swapchains"]!.AsObject().Count == 2 &&
    ambiguous["frame_pacing_scope"]!.GetValue<string>() == "ambiguous" &&
    !ambiguous["verified_target_fps"]!.GetValue<bool>(), "multiple unselected swap chains never verify target FPS");
JsonObject selected = Present(multiplePath, 90, "0xb");
Check(selected["resolved_swapchain_address"]!.GetValue<string>() == "0xB" &&
    selected["verified_target_fps"]!.GetValue<bool>(), "explicit SwapChainAddress selects its own distribution");
string displayChangePath = Path.Combine(root, "display-change.csv");
await File.WriteAllTextAsync(displayChangePath,
    Csv("SwapChainAddress", "msBetweenDisplayChange", "Dropped") + "\n" + Csv("0xC", "16", "0") + "\n");
Check(Present(displayChangePath, 60)["swapchains"]!["0xC"]!["displayed_fps"]!.GetValue<double>() == 62.5 &&
    Present(displayChangePath, 60)["swapchains"]!["0xC"]!["present_fps"] is null,
    "display-only evidence never becomes a submission interval");

string splitPath = Path.Combine(root, "split-cadence.csv");
string splitHeader = Csv("ProcessID", "SwapChainAddress", "msBetweenPresents", "msBetweenDisplayChange", "Dropped");
await File.WriteAllTextAsync(splitPath, string.Join("\n", splitHeader,
    Csv("123", "0xA", "8.33333333333333", "16.66666666666667", "0"),
    Csv("123", "0xA", "8.33333333333333", "0", "1"),
    Csv("123", "0xA", "8.33333333333333", "16.66666666666667", "0"),
    Csv("456", "0xA", "1", "1", "0")));
JsonObject split = Present(splitPath, 120, pid: 123);
JsonObject splitChain = split["swapchains"]!["0xA"]!.AsObject();
Check(Math.Abs(splitChain["present_fps"]!.GetValue<double>() - 120) < 1e-9 &&
    Math.Abs(splitChain["displayed_fps"]!.GetValue<double>() - 60) < 1e-9 &&
    splitChain["display_interval_rows"]!.GetValue<int>() == 2 &&
    splitChain["display_evidence_complete"]!.GetValue<bool>() &&
    !split["verified_target_fps"]!.GetValue<bool>(),
    "120 API submissions and 60 displayed frames cannot verify 120 FPS; dropped rows are excluded");
Check(split["csv_rows"]!.GetValue<int>() == 3 && split["excluded_process_rows"]!.GetValue<int>() == 1,
    "name-based capture keeps only the requested PID even when another PID shares the address");
JsonObject disabled = Present(splitPath, 30, pid: 123, display: false);
Check(disabled["swapchains"]!["0xA"]!["displayed_fps"] is null &&
    disabled["swapchains"]!["0xA"]!["dropped_rows"] is null &&
    !disabled["verified_target_fps"]!.GetValue<bool>(),
    "disabled display tracking leaves display and drop metrics unmeasured even if columns exist");
Check(Present(multiplePath, pid: 123)["status"]!.GetValue<string>() == "partial_report" &&
    Present(multiplePath, pid: 123)["swapchains"]!.AsObject().Count == 0,
    "PID-scoped sampling rejects CSV without ProcessID rather than mixing processes");
await File.WriteAllTextAsync(splitPath, string.Join("\n", splitHeader,
    Csv("123", "0xA", "8", "8", "0"), Csv("123", "0xA", "8", "NA", "0")));
JsonObject missingDisplay = Present(splitPath, pid: 123);
Check(!missingDisplay["verified_target_fps"]!.GetValue<bool>() &&
    missingDisplay["swapchains"]!["0xA"]!["missing_display_interval_rows"]!.GetValue<int>() == 1,
    "missing display interval cannot be replaced by a fast API interval");
await File.WriteAllTextAsync(splitPath, string.Join("\n", splitHeader,
    Csv("123", "0xA", "8", "8", "0"), Csv("123", "0xA", "8", "8", "unknown")));
Check(!Present(splitPath, pid: 123)["verified_target_fps"]!.GetValue<bool>() &&
    Present(splitPath, pid: 123)["swapchains"]!["0xA"]!["dropped_rows"] is null,
    "unknown dropped status is not silently interpreted as displayed");
await File.WriteAllTextAsync(splitPath, string.Join("\n", splitHeader,
    Csv("123", "0xA", "8", "8", "0"), Csv("broken", "0xA", "8", "8", "0")));
Check(!Present(splitPath, pid: 123)["verified_target_fps"]!.GetValue<bool>() &&
    Present(splitPath, pid: 123)["csv_errors"]!.AsArray().Count == 1,
    "malformed PID preserves the parse error and prevents certification");

string malformedPath = Path.Combine(root, "malformed.csv");
await File.WriteAllTextAsync(malformedPath,
    Csv("SwapChainAddress", "msBetweenPresents") + "\n" + Csv("0xA", "8") + "\n\"0xA\",\"broken");
JsonObject malformed = Present(malformedPath);
Check(malformed["status"]!.GetValue<string>() == "partial_report" &&
    malformed["csv_errors"]!.AsArray().Count > 0 &&
    !malformed["verified_target_fps"]!.GetValue<bool>(), "malformed PresentMon CSV is explicit and never verifies FPS");
string zeroPath = Path.Combine(root, "zero.csv");
await File.WriteAllTextAsync(zeroPath, Csv("SwapChainAddress", "msBetweenPresents") + "\n");
Check(Present(zeroPath)["status"]!.GetValue<string>() == "not_measured", "header-only PresentMon CSV is not measured");
Check(Present(Path.Combine(root, "missing.csv"))["status"]!.GetValue<string>() == "not_measured",
    "missing PresentMon CSV is not measured");
string invalidIntervalPath = Path.Combine(root, "invalid-interval.csv");
await File.WriteAllTextAsync(invalidIntervalPath,
    Csv("SwapChainAddress", "msBetweenPresents") + "\n" + Csv("0xA", "0") + "\n");
Check(Present(invalidIntervalPath)["status"]!.GetValue<string>() == "not_measured",
    "zero valid presentation intervals are not measured");

string gpuCounter = @"\\GPU Engine(pid_123_luid_0x00000000_0x00001234_phys_0_eng_0_engtype_3D)\Utilization Percentage";
string dedicated = @"\\GPU Process Memory(pid_123_luid_0x00000000_0x00001234_phys_0)\Dedicated Usage";
string shared = @"\\GPU Process Memory(pid_123_luid_0x00000000_0x00001234_phys_0)\Shared Usage";
string[] counters = [gpuCounter, dedicated, shared];
string typeperfCsv = string.Join("\r\n", Csv("(PDH-CSV 4.0), local time", gpuCounter, dedicated, shared),
    Csv("09/08/2026 01:00:00", "25", "1000", "500"),
    Csv("09/08/2026 01:00:01", "101", "bad", "600")) + "\r\n";
JsonObject perf = Typeperf(Encoding.UTF8.GetBytes(typeperfCsv), counters, [gpuCounter], [dedicated, shared]);
JsonObject utilization = perf["adapters"]!["0x00000000_0x00001234_phys_0"]!["3d_percent"]!.AsObject();
Check(perf["status"]!.GetValue<string>() == "partial_report" &&
    perf["valid_coverage_ratio"]!.GetValue<double>() == .5 &&
    utilization["timepoints"]!.GetValue<int>() == 2 && utilization["missing_timepoints"]!.GetValue<int>() == 1 &&
    utilization["utilization_percent"]!["mean"]!.GetValue<double>() == 25 &&
    utilization["counters"]![0]!["invalid_raw_values"]![0]!.GetValue<string>() == "101",
    "GPU values above 100 remain raw evidence and are excluded from utilization stats");
Check(perf["memory"]!["dedicated"]!["valid_coverage_ratio"]!.GetValue<double>() == .5 &&
    perf["memory"]!["shared"]!["valid_coverage_ratio"]!.GetValue<double>() == 1 &&
    perf["memory"]!["total"]!["bytes"]!["mean"]!.GetValue<double>() == 1500 &&
    perf["dedicated_bytes"]!["mean"]!.GetValue<double>() == 1000,
    "memory totals and per-kind coverage exclude malformed samples");
JsonObject zeroPerf = Typeperf(Encoding.UTF8.GetBytes(Csv("(PDH-CSV 4.0)", gpuCounter) + "\n"),
    [gpuCounter], [gpuCounter], []);
Check(zeroPerf["samples"]!.GetValue<int>() == 0 && zeroPerf["parse_error"] is not null,
    "typeperf header without samples has an explicit parse error");
JsonObject missingPerf = Typeperf([], [], [], []);
Check(missingPerf["samples"]!.GetValue<int>() == 0 && missingPerf["parse_error"] is not null &&
    missingPerf["dedicated_bytes"] is null, "missing typeperf counters and memory are never zero");
string malformedTypeperfCsv = Csv("(PDH-CSV 4.0)", gpuCounter) + "\n" +
    Csv("09/08/2026 01:00:00", "7", "extra") + "\n";
JsonObject malformedPerf = Typeperf(Encoding.UTF8.GetBytes(malformedTypeperfCsv), [gpuCounter], [gpuCounter], []);
Check(malformedPerf["status"]!.GetValue<string>() == "not_measured" &&
    malformedPerf["parse_error"] is not null && malformedPerf["csv_errors"]!.AsArray().Count > 0,
    "malformed typeperf rows have explicit errors and no synthetic samples");

string pilotDirectory = Path.Combine(repository, "artifacts", "official-core-sampler-pilot-20260908", "sample");
string pilotCsv = Path.Combine(pilotDirectory, "typeperf.stdout.csv");
string pilotCountersPath = Path.Combine(pilotDirectory, "counter-paths.json");
// 2026-09-08 那份 pilot 落在 artifacts\ 里（不随仓库分发），机器上没有就跳过，不再让整个测试项目起不来。
var skipped = new List<string>();
if (!File.Exists(pilotCsv) || !File.Exists(pilotCountersPath))
    skipped.Add("retained 2026-09-08 pilot: artifacts/ is not distributed with the repository");
else
{
    string[] pilotCounters = JsonNode.Parse(await File.ReadAllTextAsync(pilotCountersPath))!.AsArray()
        .Select(node => node!.GetValue<string>()).ToArray();
    string[] pilotGpu = pilotCounters.Where(path => path.EndsWith(@"\Utilization Percentage",
        StringComparison.OrdinalIgnoreCase)).ToArray();
    string[] pilotMemory = pilotCounters.Where(path => path.EndsWith(@"\Dedicated Usage", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(@"\Shared Usage", StringComparison.OrdinalIgnoreCase)).ToArray();
    JsonObject pilot = Typeperf(await File.ReadAllBytesAsync(pilotCsv), pilotCounters, pilotGpu, pilotMemory);
    Check(pilot["status"]!.GetValue<string>() == "sampled" && pilot["samples"]!.GetValue<int>() == 5 &&
        pilot["valid_timepoints"]!.GetValue<int>() == 5 && pilot["valid_coverage_ratio"]!.GetValue<double>() == 1 &&
        pilot["parse_error"] is null && pilot["csv_errors"]!.AsArray().Count == 0,
        "real 120 FPS pilot retains five valid typeperf samples without footer-induced partial status");
    string[] pilotConsole = pilot["console_output"]!.AsArray().Select(row => row!["text"]!.GetValue<string>()).ToArray();
    Check(pilotConsole.Length == 2 && pilotConsole.Any(text => text.Contains("正在退出", StringComparison.Ordinal)) &&
        pilotConsole.Any(text => text.Contains("命令成功结束", StringComparison.Ordinal)),
        "localized OEM console footer is decoded and retained separately from CSV evidence");
}

// 2026-09-18 本机（Ryzen 9800X3D 核显 + RTX 5090）实采的一份 typeperf 输出：GPU 引擎、进程显存与
// Energy Meter 同在一次采集里。它证明这台 AMD 机器只有 PKG 与 CORE 域，没有核显域。
string samplePath = Path.Combine(repository, "tests", "official-sampler-pilot");
string[] sampleCounters = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(samplePath, "counter-paths.json")))!
    .AsArray().Select(node => node!.GetValue<string>()).ToArray();
string[] sampleGpu = sampleCounters.Where(path => path.EndsWith(@"\Utilization Percentage", StringComparison.OrdinalIgnoreCase)).ToArray();
string[] sampleMemory = sampleCounters.Where(path => path.EndsWith(@"\Dedicated Usage", StringComparison.OrdinalIgnoreCase) ||
    path.EndsWith(@"\Shared Usage", StringComparison.OrdinalIgnoreCase)).ToArray();
string[] sampleEnergy = sampleCounters.Where(path => path.Contains("Energy Meter", StringComparison.OrdinalIgnoreCase)).ToArray();
JsonObject sample = Typeperf(await File.ReadAllBytesAsync(Path.Combine(samplePath, "typeperf.stdout.csv")),
    sampleCounters, sampleGpu, sampleMemory, sampleEnergy);
JsonObject samplePower = sample["platform_power"]!.AsObject();
Check(sample["status"]!.GetValue<string>() == "sampled" && sample["samples"]!.GetValue<int>() == 10 &&
    sample["valid_timepoints"]!.GetValue<int>() == 10 && sample["parse_error"] is null &&
    sample["console_output"]!.AsArray().Count == 2 &&
    samplePower["status"]!.GetValue<string>() == "no_graphics_domain" &&
    samplePower["instances"]!.AsArray().Count == 9 &&
    samplePower["package_watts"]!["median"]!.GetValue<double>() > 1 &&
    samplePower["non_core_watts"]!["median"]!.GetValue<double>() > 0 &&
    samplePower["igpu_domain_watts"] is null,
    "a real desktop capture mixes GPU, memory and Energy Meter columns and still refuses an integrated-GPU wattage");

// 机器级功耗（Energy Meter / RAPL）：毫瓦换算、PKG−ΣCORE 的核显域估计、没有计数器时干净报 unsupported_platform。
string pkgCounter = @"\\MACHINE\Energy Meter(RAPL_Package0_PKG)\Power";
string core0Counter = @"\\MACHINE\Energy Meter(RAPL_Package0_Core0_CORE)\Power";
string core1Counter = @"\\MACHINE\Energy Meter(RAPL_Package0_Core1_CORE)\Power";
string[] energyCounters = [pkgCounter, core0Counter, core1Counter];
string energyCsv = string.Join("\r\n",
    Csv("(PDH-CSV 4.0)", pkgCounter, core0Counter, core1Counter),
    Csv("09/18/2026 03:23:13.256", "100000", "20000", "20000"),
    Csv("09/18/2026 03:23:14.256", "120000", "30000", "30000")) + "\r\n";
JsonObject energyPerf = Typeperf(Encoding.UTF8.GetBytes(energyCsv), energyCounters, [], [], energyCounters);
JsonObject energyPower = energyPerf["platform_power"]!.AsObject();
JsonObject amdVerdict = VerdictOf(energyPower, 1);
Check(energyPerf["status"]!.GetValue<string>() == "sampled" && energyPerf["valid_timepoints"]!.GetValue<int>() == 2 &&
    energyPower["status"]!.GetValue<string>() == "no_graphics_domain" &&
    energyPower["igpu_domain_method"]!.GetValue<string>() == "unavailable" &&
    energyPower["igpu_domain_watts"] is null &&
    energyPower["package_watts"]!["median"]!.GetValue<double>() == 110 &&
    energyPower["cores_watts"]!["median"]!.GetValue<double>() == 50 &&
    energyPower["non_core_watts"]!["median"]!.GetValue<double>() == 60 &&
    amdVerdict["status"]!.GetValue<string>() == "unsupported_platform" && amdVerdict["worth_baking"] is null,
    "package and core domains alone never become an integrated-GPU wattage; the uncore stays out of the verdict");
// Intel 平台把核显单列成 PP1 域，这时才是壁纸功耗的绝对读数。
string pp1Counter = @"\\MACHINE\Energy Meter(RAPL_Package0_PP1)\Power";
string[] intelCounters = [pkgCounter, core0Counter, pp1Counter];
string intelCsv = string.Join("\r\n",
    Csv("(PDH-CSV 4.0)", pkgCounter, core0Counter, pp1Counter),
    Csv("09/18/2026 03:23:13.256", "12000", "4000", "700"),
    Csv("09/18/2026 03:23:14.256", "14000", "5000", "900")) + "\r\n";
JsonObject intelPower = Typeperf(Encoding.UTF8.GetBytes(intelCsv), intelCounters, [], [], intelCounters)["platform_power"]!.AsObject();
JsonObject worth = VerdictOf(intelPower, 0.5);
JsonObject notWorth = VerdictOf(intelPower, 1);
Check(intelPower["status"]!.GetValue<string>() == "sampled" &&
    intelPower["igpu_domain_method"]!.GetValue<string>() == "graphics_domain_counter" &&
    Math.Abs(intelPower["igpu_domain_watts"]!["median"]!.GetValue<double>() - 0.8) < 1e-9 &&
    worth["worth_baking"]!.GetValue<bool>() && !notWorth["worth_baking"]!.GetValue<bool>() &&
    notWorth["text"]!.GetValue<string>().Contains("cannot save", StringComparison.Ordinal),
    "a graphics RAPL domain in milliwatts becomes the verdict metric and follows the configured threshold both ways");
JsonObject noEnergyPower = Typeperf(Encoding.UTF8.GetBytes(energyCsv), energyCounters, [], [])["platform_power"]!.AsObject();
JsonObject noEnergyVerdict = VerdictOf(noEnergyPower, 1);
Check(noEnergyPower["status"]!.GetValue<string>() == "unsupported_platform" &&
    noEnergyPower["igpu_domain_watts"] is null && noEnergyPower["package_watts"] is null &&
    noEnergyVerdict["status"]!.GetValue<string>() == "unsupported_platform" &&
    noEnergyVerdict["worth_baking"] is null &&
    noEnergyVerdict["text"]!.GetValue<string>().Contains("cannot measure", StringComparison.Ordinal),
    "a machine without Energy Meter counters reports unsupported_platform instead of a fabricated wattage");
JsonObject partialEnergy = Typeperf(Encoding.UTF8.GetBytes(string.Join("\r\n",
    Csv("(PDH-CSV 4.0)", pkgCounter, core0Counter, pp1Counter),
    Csv("09/18/2026 03:23:13.256", "12000", "4000", " ")) + "\r\n"),
    intelCounters, [], [], intelCounters)["platform_power"]!.AsObject();
Check(partialEnergy["status"]!.GetValue<string>() == "no_valid_samples" &&
    partialEnergy["igpu_domain_watts"] is null && partialEnergy["package_watts"]!["median"]!.GetValue<double>() == 12,
    "an unreadable graphics domain never becomes a zero-watt wallpaper reading");

MethodInfo wait = Private("WaitForCollectorsAsync");
var waitWatch = Stopwatch.StartNew();
await (Task)(wait.Invoke(null, [Array.Empty<Task>(), TimeSpan.FromMilliseconds(150), CancellationToken.None])
    ?? throw new InvalidOperationException());
waitWatch.Stop();
Check(waitWatch.Elapsed >= TimeSpan.FromMilliseconds(120), "no-collector path waits for the actual requested interval");

Type cpuReadType = typeof(OfficialPerformanceSampler).GetNestedType("CpuRead", BindingFlags.NonPublic)
    ?? throw new MissingMemberException("CpuRead");
object before = Activator.CreateInstance(cpuReadType, [(double?)10, null]) ?? throw new InvalidOperationException();
object after = Activator.CreateInstance(cpuReadType, [(double?)11, null]) ?? throw new InvalidOperationException();
JsonObject cpu = (JsonObject)(Private("CpuSummary").Invoke(null, [before, after, 2d]) ?? throw new InvalidOperationException());
Check(cpu["process_cpu_seconds"]!.GetValue<double>() == 1 && cpu["one_core_percent"]!.GetValue<double>() == 50 &&
    cpu["elapsed_seconds"]!.GetValue<double>() == 2, "CPU percentages use the measured stopwatch interval");
object failedAfter = Activator.CreateInstance(cpuReadType, [null, "raw cpu read error"])
    ?? throw new InvalidOperationException();
JsonObject failedCpu = (JsonObject)(Private("CpuSummary").Invoke(null, [before, failedAfter, 2d]) ?? throw new InvalidOperationException());
Check(failedCpu["process_cpu_seconds"] is null && failedCpu["one_core_percent"] is null &&
    failedCpu["machine_percent"] is null && failedCpu["error"]!.GetValue<string>() == "raw cpu read error",
    "CPU read failure remains null with its raw error");
var completeTarget = new JsonObject { ["status"] = "valid" };
var completePresent = new JsonObject { ["status"] = "sampled",
    ["collector"] = new JsonObject { ["completed_successfully"] = true } };
var completeTypeperf = new JsonObject { ["status"] = "sampled", ["samples"] = 1, ["parse_error"] = null,
    ["collector"] = new JsonObject { ["completed_successfully"] = true } };
var emptyPower = new JsonObject { ["gpu_identity"] = null, ["watts"] = null, ["errors"] = new JsonArray() };
JsonArray incomplete = (JsonArray)(Private("CompletionFailures").Invoke(null,
    [completeTarget, completePresent, completeTypeperf, failedCpu, emptyPower, 1,
        new[] { dedicated, shared }, null, new JsonArray()]) ?? throw new InvalidOperationException());
Check(incomplete.Any(item => item!.GetValue<string>().Contains("CPU time", StringComparison.Ordinal)),
    "a valid CSV cannot produce sampled status when CPU reading failed");

Type targetIdentityType = typeof(OfficialPerformanceSampler).GetNestedType("TargetIdentity", BindingFlags.NonPublic)
    ?? throw new MissingMemberException("TargetIdentity");
object initialIdentity = Activator.CreateInstance(targetIdentityType,
    [Environment.ProcessPath, (DateTimeOffset?)DateTimeOffset.UtcNow, null]) ?? throw new InvalidOperationException();
JsonObject missingTarget = (JsonObject)(Private("ValidateTarget").Invoke(null,
    [int.MaxValue, initialIdentity, null]) ?? throw new InvalidOperationException());
Check(missingTarget["status"]!.GetValue<string>() == "not_valid",
    "a missing or restarted target PID makes the interval not valid");

string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path is unavailable.");
Process StartChild()
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("--sleep-child");
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start owned test child.");
}
using Process child = StartChild();
using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
Task drain = (Task)(Private("DrainAsync").Invoke(null, [child, TimeSpan.FromSeconds(10), cancel.Token])
    ?? throw new InvalidOperationException());
try { await drain; throw new InvalidOperationException("Owned collector ignored cancellation."); }
catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(child.HasExited, "collector cancellation terminates only the owned collector child");
using Process timedChild = StartChild();
Task timedDrain = (Task)(Private("DrainAsync").Invoke(null,
    [timedChild, TimeSpan.FromMilliseconds(150), CancellationToken.None]) ?? throw new InvalidOperationException());
await timedDrain;
object timedResult = timedDrain.GetType().GetProperty("Result")?.GetValue(timedDrain)
    ?? throw new InvalidOperationException("Timed drain result is missing.");
bool timedOut = (bool)(timedResult.GetType().GetProperty("TimedOut")?.GetValue(timedResult)
    ?? throw new InvalidOperationException("TimedOut result is missing."));
await timedChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(timedOut && timedChild.HasExited, "collector timeout terminates only the owned collector child");

Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", checks = passed.Count, passed, skipped, root },
    new JsonSerializerOptions { WriteIndented = true }));
