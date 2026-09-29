using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace Periodica.Bench;

/// <param name="Order">段序，由 A（原作）与 B（成品）组成，如 ABBA；只测原作时为 A。</param>
/// <param name="IdleSeconds">大于 0 时在首尾各加一段空闲基线 I（WPE 暂停），功耗按两段空闲均值扣除。</param>
public sealed record AbbaOptions(string WallpaperEngine, string Original, string? Baked, string Output,
    string Order = "ABBA", int Monitor = 0, int Seconds = 45, int SettleSeconds = 20, double Fps = 60,
    int IdleSeconds = 0, string? PresentMon = null, string? Restore = null);

/// <summary>
/// 官方 WPE 播放功耗的 A/B/B/A 编排：记下该显示器当前壁纸，逐段打开原作/成品（或暂停作空闲基线）、等稳定、
/// 调 <see cref="OfficialPerformanceSampler"/> 采样，最后无论成败都把原壁纸打开回去并继续播放。
/// 判定口径同 2026-09-18 的 A/B/B/A 实测：核显域中位功耗降幅 ≥30% 算省电，升幅 >5% 算更费，其余持平。
/// </summary>
public static class Abba
{
    internal const double SavedPercent = 30, WorsePercent = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>实际执行的段序：有空闲基线时首尾各包一段 I。</summary>
    internal static string Phases(string order, int idleSeconds)
    {
        if (order.Length == 0 || order.Any(kind => kind is not ('A' or 'B')))
            throw new ArgumentException("--order may contain only A and B.");
        return idleSeconds > 0 ? "I" + order + "I" : order;
    }

    static void RequireEntryFile(string project)
    {
        string json = Directory.Exists(project) ? Path.Combine(project, "project.json") : project;
        if (!File.Exists(json)) throw new FileNotFoundException("project.json not found.", json);
        string? entry = JsonNode.Parse(File.ReadAllText(json))?["file"]?.GetValue<string>();
        string dir = Path.GetDirectoryName(json)!;   // 创意工坊原作的入口在 scene.pkg 里
        if (entry is null || !File.Exists(Path.Combine(dir, entry)) && !File.Exists(Path.Combine(dir, "scene.pkg")))
            throw new InvalidDataException($"{json}: entry file '{entry}' is missing; the wallpaper would render nothing.");
    }

    public static async Task<JsonObject> RunAsync(AbbaOptions options, IProgress<string> progress, CancellationToken token)
    {
        string phases = Phases(options.Order, options.IdleSeconds);
        if (phases.Contains('B') && options.Baked is null) throw new ArgumentException("--order uses B but --baked is missing.");
        // 项目缺入口文件时 WPE 照样 60 fps 出空帧，功耗接近空闲，会被误读成"大幅节省"（9/24 Q4 三张静态成品即如此）
        foreach (string? project in new[] { options.Original, options.Baked })
            if (project is not null) RequireEntryFile(project);
        if (options.Seconds <= 0 || options.SettleSeconds < 0 || options.IdleSeconds < 0 || options.Fps <= 0 || !double.IsFinite(options.Fps) || options.Monitor < 0)
            throw new ArgumentException("Measurement duration, settle time, target FPS, or monitor number is invalid.");
        // Fail before touching the desktop when the collector is absent.
        if (options.PresentMon is not null && !File.Exists(options.PresentMon) ||
            options.PresentMon is null && PresentMonLocator.Find() is null)
            throw new FileNotFoundException(PresentMonLocator.Missing);
        string output = Path.GetFullPath(options.Output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("--out must be a new directory.");
        var wpe = new WpeControl(options.WallpaperEngine);
        wpe.RequireSingleMonitor(options.Monitor);
        var identity = wpe.Identity();
        byte[] configBefore = await File.ReadAllBytesAsync(wpe.ConfigPath, token);
        JsonObject before = WpeControl.ParseConfig(configBefore);
        string profile = Environment.UserName;
        JsonObject assignment = WpeControl.Assignment(before, profile);
        var previousObservation = await wpe.ObserveAsync(options.Monitor, token);
        string previous = previousObservation.File;
        if (previous.Length == 0)
            throw new InvalidDataException($"Wallpaper Engine reports no wallpaper on monitor {options.Monitor}; nothing to restore to.");
        if (!File.Exists(previous) || !SamePath(previous, assignment["file"]!.GetValue<string>()) ||
            options.Restore is not null && !SamePath(previous, options.Restore))
            throw new InvalidDataException("The observed current wallpaper must match the saved assignment and any --restore path before comparison.");
        Directory.CreateDirectory(output);
        string backup = Path.Combine(output, "config.before.json");
        await File.WriteAllBytesAsync(backup, configBefore, token);
        var segments = new JsonArray();
        var run = new JsonObject
        {
            ["schema_version"] = 1, ["order"] = phases, ["monitor"] = options.Monitor, ["previous_wallpaper"] = previous,
            ["seconds"] = options.Seconds, ["settle_seconds"] = options.SettleSeconds, ["target_fps"] = options.Fps,
            ["idle_seconds"] = options.IdleSeconds, ["original"] = options.Original, ["baked"] = options.Baked,
            ["config_backup"] = backup, ["before_pid"] = identity.Pid, ["before_start_ticks"] = identity.StartTicks,
            ["previous_evidence"] = previousObservation.Evidence,
            ["control_scope"] = "One connected display, monitor 0. Each load uses stop/close/open/play. Saved assignments and this WPE process's playback readings are checked; config fallback does not establish visual correctness or official playback identity.",
            ["segments"] = segments
        };
        string expectedProject = previous;
        bool expectedIsPrevious = true, changed = false, ownedClose = false;

        async Task<(string File, string Evidence)> RequireOwnedSelection(CancellationToken checkToken, bool allowClosed = false)
        {
            wpe.RequireSingleMonitor(options.Monitor);
            if (wpe.Identity() != identity) throw new InvalidDataException("Wallpaper Engine PID or start time changed; comparison stopped without replacing its current selection.");
            JsonObject current = WpeControl.ParseConfig(await File.ReadAllBytesAsync(wpe.ConfigPath, checkToken));
            if (allowClosed && ownedClose && WpeControl.OnlyMonitorRemoved(before, current, profile) &&
                (await wpe.GetWallpaperAsync(options.Monitor, checkToken)).Length == 0 && wpe.Identity() == identity)
                return ("", "own_close_verified_in_configuration");
            var observation = await wpe.ObserveAsync(options.Monitor, checkToken);
            string selected = observation.File;
            if (!(expectedIsPrevious ? SamePath(previous, selected) : SelectedMatches(expectedProject, selected)) ||
                !SamePath(selected, WpeControl.Assignment(current, profile)["file"]!.GetValue<string>()) ||
                !WpeControl.ConfigMatchesSelection(before, current, profile) || wpe.Identity() != identity)
                throw new InvalidDataException("The wallpaper selection or configuration changed beyond this comparison; the current user state was preserved. Use config.before.json only for manual recovery.");
            ownedClose = false;
            return observation;
        }
        try
        {
            for (int index = 0; index < phases.Length; ++index)
            {
                char kind = phases[index];
                string label = $"{index:00}-{kind}";
                var playback = new JsonObject { ["segment"] = label, ["kind"] = kind.ToString() };
                await RequireOwnedSelection(token);
                if (kind == 'I')
                {
                    progress.Report($"{label}: pausing Wallpaper Engine for the idle baseline.");
                    changed = true;
                    await wpe.PauseAsync(token);
                }
                else
                {
                    string project = kind == 'A' ? options.Original : options.Baked!;
                    playback["project"] = project;
                    playback["load_sequence"] = "stop/closeWallpaper/openWallpaper/play";
                    progress.Report($"{label}: opening {project}.");
                    changed = true;
                    expectedProject = project;
                    expectedIsPrevious = false;
                    await wpe.IsolatedOpenAsync(project, options.Monitor, token, () => ownedClose = true);
                }
                await Task.Delay(TimeSpan.FromSeconds(options.SettleSeconds), token);
                var selectedBefore = await RequireOwnedSelection(token);
                if (kind != 'I')
                {
                    playback["selected_wallpaper"] = selectedBefore.File;
                    playback["selected_evidence"] = selectedBefore.Evidence;
                    playback["selected_verified"] = selectedBefore.Evidence == "official_getWallpaper";
                }
                JsonObject report = await OfficialPerformanceSampler.SampleAsync(new(1, identity.Pid, label,
                    Path.Combine(output, label), kind == 'I' ? options.IdleSeconds : options.Seconds, options.Fps,
                    options.PresentMon, ExpectedProcessPath: wpe.Executable), progress, playback, token);
                var selectedAfter = await RequireOwnedSelection(token);
                if (kind != 'I')
                {
                    var observed = report["playback"]!.AsObject();
                    observed["selected_after_sample"] = selectedAfter.File;
                    observed["selected_after_evidence"] = selectedAfter.Evidence;
                    observed["selected_verified"] = observed["selected_verified"]?.GetValue<bool>() == true && selectedAfter.Evidence == "official_getWallpaper";
                    await File.WriteAllTextAsync(report["report_path"]!.GetValue<string>(), report.ToJsonString(JsonOptions), token);
                }
                segments.Add(Segment(label, kind, report));
            }
        }
        finally
        {
            // Restore only while the same process still owns our expected selection and no unrelated config changed.
            try
            {
                var restoreFrom = await RequireOwnedSelection(CancellationToken.None, allowClosed: true);
                run["restore_from_evidence"] = restoreFrom.Evidence;
                if (changed)
                {
                    expectedProject = previous;
                    expectedIsPrevious = true;
                    await wpe.IsolatedOpenAsync(previous, options.Monitor, CancellationToken.None, () => ownedClose = true);
                }
                var restored = await RequireOwnedSelection(CancellationToken.None);
                run["restored_wallpaper"] = restored.File;
                run["restore_evidence"] = restored.Evidence;
                byte[] currentBytes = await File.ReadAllBytesAsync(wpe.ConfigPath);
                bool sameConfig = currentBytes.AsSpan().SequenceEqual(configBefore);
                if (!sameConfig && WpeControl.OnlyLocalFlagRemoved(before, WpeControl.ParseConfig(currentBytes), profile))
                {
                    string temporary = wpe.ConfigPath + ".periodica-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, configBefore);
                        await RequireOwnedSelection(CancellationToken.None);
                        if (!(await File.ReadAllBytesAsync(wpe.ConfigPath)).AsSpan().SequenceEqual(currentBytes))
                            throw new InvalidDataException("Configuration changed immediately before restoration; the newer file was preserved.");
                        File.Move(temporary, wpe.ConfigPath, overwrite: true);
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        await RequireOwnedSelection(CancellationToken.None);
                        sameConfig = (await File.ReadAllBytesAsync(wpe.ConfigPath)).AsSpan().SequenceEqual(configBefore);
                        run["config_local_repaired"] = sameConfig;
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                run["config_restored"] = sameConfig;
                run["same_wpe_pid_start"] = wpe.Identity() == identity;
                run["restored"] = sameConfig && run["same_wpe_pid_start"]!.GetValue<bool>();
                if (!sameConfig) run["restore_error"] = "Wallpaper restored, but config bytes differ from the backup; no unrelated configuration was overwritten.";
            }
            catch (Exception error) { run["restored"] = false; run["restore_error"] = error.ToString(); }
            run["summary"] = Summarize(segments);
            run["comparison"] = Compare(run);
            await File.WriteAllTextAsync(Path.Combine(output, "abba.json"), run.ToJsonString(JsonOptions), CancellationToken.None);
        }
        return run;
    }

    private static bool SamePath(string a, string b) => a.Length > 0 && b.Length > 0 &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool SelectedMatches(string project, string selected)
    {
        if (selected.Length == 0) return false;
        string json = Directory.Exists(project) ? Path.Combine(project, "project.json") : project;
        string? entry = JsonNode.Parse(File.ReadAllText(json))?["file"]?.GetValue<string>();
        string directory = Path.GetDirectoryName(Path.GetFullPath(json))!;
        return SamePath(Path.Combine(directory, entry ?? ""), selected) ||
            SamePath(Path.Combine(directory, "scene.pkg"), selected) || SamePath(json, selected);
    }

    internal static JsonObject Segment(string label, char kind, JsonObject report)
    {
        JsonNode? power = report["platform_power"];
        string? chainKey = report["presentmon"]?["resolved_swapchain_address"]?.GetValue<string>();
        JsonNode? chain = chainKey is null ? null : report["presentmon"]?["swapchains"]?[chainKey];
        return new JsonObject
        {
            ["label"] = label, ["kind"] = kind.ToString(), ["status"] = report["status"]?.DeepClone(),
            ["power_status"] = power?["status"]?.DeepClone(),
            ["igpu_watts"] = Median(power?["igpu_domain_watts"]), ["package_watts"] = Median(power?["package_watts"]),
            ["presentmon_status"] = report["presentmon"]?["status"]?.DeepClone(),
            ["report_path"] = report["report_path"]?.DeepClone(),
            ["selected_wallpaper"] = report["playback"]?["selected_wallpaper"]?.DeepClone(),
            ["selected_evidence"] = report["playback"]?["selected_evidence"]?.DeepClone(),
            ["selected_after_evidence"] = report["playback"]?["selected_after_evidence"]?.DeepClone(),
            ["load_sequence"] = report["playback"]?["load_sequence"]?.DeepClone(),
            ["selected_verified"] = report["playback"]?["selected_verified"]?.DeepClone(),
            ["power_collector_complete"] = report["typeperf"]?["collector"]?["completed_successfully"]?.DeepClone(),
            ["typeperf_status"] = report["typeperf"]?["status"]?.DeepClone(),
            ["target_status"] = report["target_validation"]?["status"]?.DeepClone(),
            ["pid"] = report["target_validation"]?["pid"]?.DeepClone(),
            ["target_fps"] = report["metadata"]?["target_fps"]?.DeepClone(),
            ["refresh_hz"] = report["display"]?["refresh_hz"]?.DeepClone(),
            ["frame_pacing_scope"] = report["presentmon"]?["frame_pacing_scope"]?.DeepClone(),
            ["displayed_fps"] = chain?["displayed_fps"]?.DeepClone(),
            ["displayed_rows"] = chain?["displayed_rows"]?.DeepClone(),
            ["dropped_rows"] = chain?["dropped_rows"]?.DeepClone(),
            ["display_evidence_complete"] = chain?["display_evidence_complete"]?.DeepClone(),
            ["cpu_one_core_percent"] = report["cpu"]?["one_core_percent"]?.DeepClone(),
            ["gpu_adapters"] = report["typeperf"]?["adapters"]?.DeepClone(),
            ["gpu_3d_percent"] = AdapterMedian(report, "3d_percent"),
            ["video_decode_percent"] = AdapterMedian(report, "video_decode_percent"),
            ["nvidia_board_watts"] = Median(report["nvidia_board_power"]?["watts"]),
            ["incomplete_reasons"] = report["incomplete_reasons"]?.DeepClone()
        };
    }

    private static double? AdapterMedian(JsonObject report, string engine)
    {
        JsonObject? adapters = report["typeperf"]?["adapters"] as JsonObject;
        return adapters?.Count == 1 ? Median(adapters.First().Value?[engine]?["utilization_percent"]) : null;
    }

    /// <summary>Only the measured playback of these two projects can be compared; incomplete identity, display, or restore evidence withholds a choice.</summary>
    public static JsonObject Compare(JsonObject run)
    {
        var reasons = new JsonArray();
        if (run["restored"]?.GetValue<bool>() != true) reasons.Add("The previous wallpaper was not verified restored.");
        double target = Number(run["target_fps"]) ?? 0;
        int? refresh = null, pid = null;
        JsonArray segments = run["segments"] as JsonArray ?? new JsonArray();
        if (!segments.Any(s => s?["kind"]?.GetValue<string>() == "A") ||
            !segments.Any(s => s?["kind"]?.GetValue<string>() == "B")) reasons.Add("Both original and baked playback segments are required.");
        if (Number(run["idle_seconds"]) is > 0 && segments.Count(s => s?["kind"]?.GetValue<string>() == "I") != 2)
            reasons.Add("Both requested idle baseline segments are required.");
        foreach (JsonNode? item in segments)
        {
            string label = item?["label"]?.GetValue<string>() ?? "segment";
            if (item?["kind"]?.GetValue<string>() == "I")
            {
                if (item?["power_status"]?.GetValue<string>() != "sampled" ||
                    item?["typeperf_status"]?.GetValue<string>() != "sampled" ||
                    item?["power_collector_complete"]?.GetValue<bool>() != true ||
                    item?["target_status"]?.GetValue<string>() != "valid" ||
                    Number(item?["igpu_watts"]) is null || Number(item?["package_watts"]) is null)
                    reasons.Add($"{label}: requested idle baseline power capture was incomplete.");
                continue;
            }
            if (item?["selected_evidence"]?.GetValue<string>() == "saved_configuration_getWallpaper_returned_empty" ||
                item?["selected_after_evidence"]?.GetValue<string>() == "saved_configuration_getWallpaper_returned_empty")
                reasons.Add($"{label}: saved configuration confirms assignment only; official playback selection was not verified.");
            if (item?["status"]?.GetValue<string>() != "sampled" ||
                item?["power_status"]?.GetValue<string>() != "sampled" ||
                item?["presentmon_status"]?.GetValue<string>() != "sampled" ||
                item?["target_status"]?.GetValue<string>() != "valid" ||
                item?["selected_verified"]?.GetValue<bool>() != true ||
                item?["frame_pacing_scope"]?.GetValue<string>() != "single_swapchain" ||
                item?["display_evidence_complete"]?.GetValue<bool>() != true ||
                item?["incomplete_reasons"] is JsonArray { Count: > 0 })
                reasons.Add($"{label}: playback selection, PID, power, or display capture was incomplete.");
            if (Number(item?["target_fps"]) != target || target <= 0)
                reasons.Add($"{label}: target FPS differs from the comparison request.");
            int? thisPid = item?["pid"]?.GetValue<int>();
            if (thisPid is null || pid is not null && thisPid != pid) reasons.Add($"{label}: Wallpaper Engine PID was not consistent.");
            pid ??= thisPid;
            int? thisRefresh = item?["refresh_hz"]?.GetValue<int>();
            if (thisRefresh is null || refresh is not null && thisRefresh != refresh)
                reasons.Add($"{label}: display refresh rate was not consistent.");
            refresh ??= thisRefresh;
            int rows = item?["displayed_rows"]?.GetValue<int>() ?? 0;
            double fps = Number(item?["displayed_fps"]) ?? 0;
            int? dropped = item?["dropped_rows"]?.GetValue<int>();
            // Finite samples can differ by one frame even at steady 60 FPS (e.g. 59.98).
            if (rows <= 1 || dropped is null || dropped > 0 || fps < target - target / rows)
                reasons.Add($"{label}: displayed frame count, drops, or cadence did not establish target FPS.");
            if (item?["gpu_adapters"] is not JsonObject { Count: 1 })
                reasons.Add($"{label}: GPU adapter attribution was ambiguous.");
        }
        JsonObject? gain = run["summary"]?["gain"] as JsonObject;
        if (gain?["status"]?.GetValue<string>() != "measured") reasons.Add("Comparable iGPU power was not measured.");
        if (Number(gain?["original_watts"]) is double watts && watts < 0.1)
            reasons.Add("iGPU graphics-domain power was near idle; it cannot establish playback cost, especially on a discrete GPU.");
        string gpuChoice = reasons.Count > 0 ? "unavailable" : gain?["verdict"]?.GetValue<string>() switch
        {
            "saved" => "baked", "worse" => "original", _ => "indistinguishable"
        };
        double? packageA = Number(run["summary"]?["original_package_watts"]);
        double? packageB = Number(run["summary"]?["baked_package_watts"]);
        if (packageA is null || packageB is null) reasons.Add("Comparable CPU package power was not measured.");
        if (reasons.Count > 0) gpuChoice = "unavailable";
        string choice = gpuChoice is "baked" or "original" && packageA is not null && packageB is not null &&
            (gpuChoice == "baked" ? packageB > packageA : packageA > packageB) ? "tradeoff" : gpuChoice;
        return new JsonObject { ["status"] = reasons.Count == 0 ? "measured" : "unavailable",
            ["choice"] = choice, ["gpu_choice"] = gpuChoice,
            ["scope"] = "Saved project assignments and iGPU graphics-domain/CPU-package readings from this WPE process on one display. Configuration fallback alone does not verify official playback identity. Not total-system savings or visual correctness.",
            ["reasons"] = reasons };
    }

    /// <summary>按段种求核显域与封装的均值，扣空闲基线，给出 A 对 B 的结论。任一段缺读数就不给结论。</summary>
    internal static JsonObject Summarize(JsonArray segments)
    {
        double? Mean(char kind, string field)
        {
            var values = segments.Where(item => item?["kind"]?.GetValue<string>() == kind.ToString())
                .Select(item => item?[field] is JsonValue value && value.TryGetValue(out double watts) ? watts : (double?)null).ToArray();
            return values.Length == 0 || values.Any(value => value is null) ? null : values.Average(value => value!.Value);
        }
        double? idle = Mean('I', "igpu_watts"), original = Mean('A', "igpu_watts"), baked = Mean('B', "igpu_watts");
        var summary = new JsonObject
        {
            ["metric"] = "platform_power.igpu_domain_watts.median, mean over segments of the same kind",
            ["idle_igpu_watts"] = idle, ["original_igpu_watts"] = original, ["baked_igpu_watts"] = baked,
            ["idle_package_watts"] = Mean('I', "package_watts"), ["original_package_watts"] = Mean('A', "package_watts"),
            ["baked_package_watts"] = Mean('B', "package_watts"),
            ["original_cpu_one_core_percent"] = Mean('A', "cpu_one_core_percent"),
            ["baked_cpu_one_core_percent"] = Mean('B', "cpu_one_core_percent"),
            ["original_gpu_3d_percent"] = Mean('A', "gpu_3d_percent"),
            ["baked_gpu_3d_percent"] = Mean('B', "gpu_3d_percent"),
            ["original_video_decode_percent"] = Mean('A', "video_decode_percent"),
            ["baked_video_decode_percent"] = Mean('B', "video_decode_percent"),
            ["original_nvidia_board_watts"] = Mean('A', "nvidia_board_watts"),
            ["baked_nvidia_board_watts"] = Mean('B', "nvidia_board_watts")
        };
        summary["gain"] = segments.Any(item => item?["kind"]?.GetValue<string>() == "I") && idle is null
            ? Gain(null, null, null) : Gain(original, baked, idle);
        return summary;
    }

    /// <summary>原作对成品的核显域变化；给了空闲基线就先扣掉再比（比的是壁纸自身的增量）。</summary>
    internal static JsonObject Gain(double? original, double? baked, double? idle)
    {
        double? a = original - (idle ?? 0), b = baked - (idle ?? 0);
        if (a is not > 0 || b is null)
            return new JsonObject { ["status"] = "not_measured", ["verdict"] = "unavailable" };
        double change = (b.Value - a.Value) / a.Value * 100;
        return new JsonObject
        {
            ["status"] = "measured", ["baseline_subtracted"] = idle is not null,
            ["original_watts"] = a, ["baked_watts"] = b, ["change_percent"] = change,
            ["verdict"] = change <= -SavedPercent ? "saved" : change > WorsePercent ? "worse" : "same"
        };
    }

    private static double? Median(JsonNode? stats) =>
        stats?["median"] is JsonValue value && value.TryGetValue(out double median) && double.IsFinite(median) ? median : null;

    private static double? Number(JsonNode? value) => double.TryParse(value?.ToString(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
}
