using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

public sealed record OfficialPerformanceRequest(int SchemaVersion, int ProcessId, string Label, string OutputDirectory,
    int Seconds = 30, double TargetFps = 120, string? PresentMonPath = null, string? NvidiaGpuUuid = null,
    string? ExpectedProcessPath = null, string? SwapChainAddress = null, bool TrackDisplay = true,
    // 以下为"烘前实测原作功耗"用：给了 SourceProject 就由采样器打开原作、等稳定、采样、按 apply 记录还原。
    string? SourceProject = null, string? WallpaperEngineExecutable = null, string? Location = null,
    int SettleSeconds = 20, double IgpuWattsThreshold = 1);

/// <summary>Read-only, per-process Windows sampler. It never starts, stops, or changes the target application
/// unless a SourceProject is requested, in which case playback is applied and restored through WallpaperController.</summary>
public static class OfficialPerformanceSampler
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan CollectorGrace = TimeSpan.FromSeconds(30);

    public static async Task<JsonObject> SampleAsync(OfficialPerformanceRequest request,
        IProgress<RenderProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.SourceProject is not null) return await PlayAndSampleAsync(request, progress, cancellationToken);
        return await SampleCoreAsync(request, progress, false, null, cancellationToken);
    }

    /// <summary>烘前实测：在官方 Wallpaper Engine 里播原作、等稳定、采样，最后按 apply 记录还原原来的壁纸。
    /// 打开与还原都走 WallpaperController 的官方 -control 命令，不改 config.json 的排版，也不重启 WPE。</summary>
    private static async Task<JsonObject> PlayAndSampleAsync(OfficialPerformanceRequest request,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Official performance sampling requires Windows.");
        string executable = request.WallpaperEngineExecutable
            ?? throw new ArgumentException("Playing the source requires the Wallpaper Engine executable.");
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
        ProjectSource.EnsureNoReparsePoints(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Sampler output must be a new directory.");
        var controller = new WallpaperController(executable);
        var targets = await controller.ReadCurrentTargetsAsync(cancellationToken);
        WallpaperTarget target = request.Location is not null
            ? targets.SingleOrDefault(item => item.Location == request.Location)
                ?? throw new InvalidDataException("The requested Wallpaper Engine location is not assigned.")
            : targets.Count == 1 ? targets[0]
                : throw new InvalidDataException("Several Wallpaper Engine locations are assigned; name the one to measure.");
        Directory.CreateDirectory(output);
        string applyDirectory = Path.Combine(output, "apply");
        progress?.Report(new("official_sampling", 0, "Applying the source wallpaper to " + target.Location + "."));
        JsonObject applied = await controller.ApplyAsync(
            new(1, request.SourceProject!, target.Profile, target.Location, applyDirectory), cancellationToken);
        var playback = new JsonObject
        {
            ["source_project"] = request.SourceProject, ["profile"] = target.Profile, ["location"] = target.Location,
            ["settle_seconds"] = request.SettleSeconds, ["applied"] = applied.DeepClone(), ["restored"] = null
        };
        JsonObject? report = null;
        try
        {
            int processId = request.ProcessId > 0 ? request.ProcessId : WallpaperProcessId(executable);
            OfficialPerformanceRequest sampling = request with
            {
                ProcessId = processId, ExpectedProcessPath = request.ExpectedProcessPath ?? executable
            };
            if (sampling.SettleSeconds > 0)
            {
                progress?.Report(new("official_sampling", 0, $"Letting playback settle for {sampling.SettleSeconds} s."));
                await Task.Delay(TimeSpan.FromSeconds(sampling.SettleSeconds), cancellationToken);
            }
            report = await SampleCoreAsync(sampling, progress, true, playback, cancellationToken);
            return report;
        }
        finally
        {
            // 还原永远要做，采样失败也一样；还原本身失败只记录，不掩盖原始异常。
            try
            {
                playback["restored"] = (await controller.RollbackAsync(Path.Combine(applyDirectory, "apply.json"),
                    CancellationToken.None)).DeepClone();
            }
            catch (Exception error) { playback["restore_error"] = error.ToString(); }
            await WriteAsync(Path.Combine(output, "playback.json"), playback, CancellationToken.None);
            if (report is not null)
            {
                report["playback"] = playback.DeepClone();
                await WriteAsync(report["report_path"]!.GetValue<string>(), report, CancellationToken.None);
            }
        }
    }

    private static int WallpaperProcessId(string executable)
    {
        string full = Path.GetFullPath(executable);
        Process[] candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(full));
        try
        {
            int[] ids = candidates.Where(process =>
            {
                try { return string.Equals(process.MainModule?.FileName, full, StringComparison.OrdinalIgnoreCase); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                { return false; }
            }).Select(process => process.Id).ToArray();
            if (ids.Length != 1)
                throw new InvalidDataException("Exactly one running Wallpaper Engine process must match the executable.");
            return ids[0];
        }
        finally { foreach (Process process in candidates) process.Dispose(); }
    }

    private static async Task<JsonObject> SampleCoreAsync(OfficialPerformanceRequest request,
        IProgress<RenderProgress>? progress, bool outputExists, JsonObject? playback, CancellationToken cancellationToken)
    {
        if (request.SchemaVersion != 1 || request.ProcessId <= 0 || request.Seconds <= 0 || request.TargetFps <= 0 ||
            !double.IsFinite(request.TargetFps))
            throw new InvalidDataException("Invalid official sampler request.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Official performance sampling requires Windows.");

        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
        ProjectSource.EnsureNoReparsePoints(output);
        if (!outputExists && (Directory.Exists(output) || File.Exists(output)))
            throw new IOException("Sampler output must be a new directory.");
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, "report.json");

        using var target = Process.GetProcessById(request.ProcessId);
        TargetIdentity initialIdentity = ReadTargetIdentity(target);
        if (request.ExpectedProcessPath is not null)
        {
            string expected = Path.GetFullPath(request.ExpectedProcessPath);
            if (!string.Equals(expected, initialIdentity.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Target process path does not match ExpectedProcessPath.");
        }
        string? processName = Path.GetFileName(initialIdentity.Path);
        if (string.IsNullOrWhiteSpace(processName))
            throw new InvalidDataException("Could not verify the target executable name for PresentMon capture.");

        string presentMon = Path.GetFullPath(request.PresentMonPath ??
            Path.Combine(AppContext.BaseDirectory, "performance", "PresentMon.exe"));
        string typeperf = Path.Combine(Environment.SystemDirectory, "typeperf.exe");
        string smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        string session = "wpe-baker-" + Guid.NewGuid().ToString("N");
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var metadata = new JsonObject
        {
            ["schema_version"] = 1, ["label"] = request.Label, ["pid"] = request.ProcessId,
            ["process_path"] = initialIdentity.Path, ["process_start_utc"] = initialIdentity.StartUtc?.ToString("O"),
            ["target_fps"] = request.TargetFps, ["seconds_requested"] = request.Seconds,
            ["started_utc"] = startedUtc.ToString("O"), ["report_path"] = reportPath,
            ["presentmon"] = presentMon, ["presentmon_session"] = session,
            ["presentmon_process_name"] = processName,
            ["presentmon_filter_note"] = "Capture by verified executable name; CSV statistics retain only the requested PID.",
            ["swap_chain_address"] = request.SwapChainAddress, ["nvidia_gpu_uuid"] = request.NvidiaGpuUuid,
            ["presentation_tracking"] = request.TrackDisplay ? "display" : "present_api_only",
            ["presentation_tracking_note"] = request.TrackDisplay
                ? "PresentMon tracks presentation through display when supported."
                : "Only Present API cadence is measured. Displayed-frame pacing and dropped/displayed status are not measured; GPU engine counters remain separate.",
            // 只读采样与"烘前实测"是两种范围，写死成只读会谎报：后者确实换过桌面壁纸再还原。
            ["scope"] = playback is null
                ? "Read-only measurements of one specified existing process. No desktop or application control occurred."
                : "The requested source was applied to one Wallpaper Engine location through the official control commands, sampled, and the previous wallpaper restored. Every other process was only read.",
            ["power_scope"] = "NVIDIA board power is the total board reading, not an increment attributable to this wallpaper.",
            ["gpu_scope"] = "GPU Engine counters are grouped by adapter LUID from counter paths; process engine attribution is not a per-wallpaper isolation guarantee."
        };
        await WriteAsync(Path.Combine(output, "metadata.json"), metadata, cancellationToken);

        progress?.Report(new("official_sampling", 0, "Discovering Windows per-process counters."));
        var discoveryErrors = new JsonArray();
        string[] gpu = await QueryCountersAsync(typeperf, "GPU Engine", request.ProcessId, discoveryErrors, cancellationToken);
        gpu = gpu.Where(path => path.EndsWith(@"\Utilization Percentage", StringComparison.OrdinalIgnoreCase) &&
            EngineKind(path) is "3d" or "videodecode" or "copy").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] memory = (await QueryCountersAsync(typeperf, "GPU Process Memory", request.ProcessId,
            discoveryErrors, cancellationToken)).Where(path =>
            path.EndsWith(@"\Dedicated Usage", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(@"\Shared Usage", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // 机器级功耗：Windows 的 Energy Meter 计数器集（Intel/AMD 的 RAPL 域，单位毫瓦）。
        // 台式机 AMD 平台没有 PP1/GFX 域，核显功耗含在 PKG 里，只能用 PKG − ΣCORE 估核显+uncore。
        // 平台没有这一计数器集时不算作发现失败，而是干净地报 unsupported_platform。
        var powerDiscoveryErrors = new JsonArray();
        string[] energy = (await QueryCountersAsync(typeperf, "Energy Meter", null, powerDiscoveryErrors, cancellationToken))
            .Where(path => path.EndsWith(@"\Power", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("(_Total)", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] counters = gpu.Concat(memory).Concat(energy).ToArray();
        await WriteAsync(Path.Combine(output, "counter-paths.json"), JsonSerializer.SerializeToNode(counters), cancellationToken);
        await WriteAsync(Path.Combine(output, "counter-discovery-errors.json"), discoveryErrors, cancellationToken);

        string presentCsv = Path.Combine(output, "presentmon-v1.csv");
        // Name filtering produced usable display records in the retained PresentMon pilot.
        // Enforce the exact PID again when parsing the resulting CSV.
        string[] presentArguments = ["--v1_metrics", "--process_name", processName,
            "--timed", request.Seconds.ToString(CultureInfo.InvariantCulture), "--terminate_after_timed", "--no_console_stats",
            "--no_track_input", "--no_track_gpu", "--session_name", session, "--output_file", presentCsv];
        if (!request.TrackDisplay) presentArguments = [.. presentArguments, "--no_track_display"];
        string[] presentCommand = [presentMon, .. presentArguments];
        await WriteAsync(Path.Combine(output, "presentmon-command.json"), JsonSerializer.SerializeToNode(presentCommand), cancellationToken);
        string[] typeperfArguments = [.. counters, "-si", "1", "-sc", request.Seconds.ToString(CultureInfo.InvariantCulture)];
        if (counters.Length > 0)
            await WriteAsync(Path.Combine(output, "typeperf-command.json"),
                JsonSerializer.SerializeToNode(new[] { typeperf }.Concat(typeperfArguments)), cancellationToken);

        var errors = new JsonArray();
        var collectors = new List<(string Name, Process Process, Task<Drained> Drain)>();
        var powerSamples = new JsonArray();
        var powerErrors = new JsonArray();
        var workingSetSamples = new JsonArray();
        var workingSetErrors = new JsonArray();
        var gpuIdentity = new JsonObject();
        using var powerStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task powerTask = PollPowerAsync(smi, request.NvidiaGpuUuid, gpuIdentity, powerSamples, powerErrors, powerStop.Token);
        Task workingSetTask = PollWorkingSetAsync(target, workingSetSamples, workingSetErrors, powerStop.Token);
        var stopwatch = Stopwatch.StartNew();
        CpuRead beforeCpu = ReadCpu(target);
        try
        {
            TryStartCollector("presentmon", presentMon, presentArguments, request.Seconds, collectors, errors, cancellationToken);
            if (counters.Length > 0)
                TryStartCollector("typeperf", typeperf, typeperfArguments, request.Seconds, collectors, errors, cancellationToken);

            await WaitForCollectorsAsync(collectors.Select(item => (Task)item.Drain).ToArray(),
                TimeSpan.FromSeconds(request.Seconds), cancellationToken);

            CpuRead afterCpu = ReadCpu(target);
            stopwatch.Stop();
            double elapsed = stopwatch.Elapsed.TotalSeconds;
            var drained = collectors.ToDictionary(item => item.Name, item => item.Drain.Result,
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in collectors)
            {
                Drained data = drained[item.Name];
                string stdoutName = item.Name == "typeperf" ? "typeperf.stdout.csv" : "presentmon.stdout.log";
                await File.WriteAllBytesAsync(Path.Combine(output, stdoutName), data.Stdout, cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(output, item.Name + ".stderr.log"), data.Stderr, cancellationToken);
                if (data.TimedOut)
                    errors.Add(new JsonObject { ["collector"] = item.Name, ["error"] = "timed out; only the owned collector process was terminated" });
                if (data.Error is not null)
                    errors.Add(new JsonObject { ["collector"] = item.Name, ["error"] = data.Error });
                if (data.ExitCode is not null and not 0)
                    errors.Add(new JsonObject { ["collector"] = item.Name, ["exit_code"] = data.ExitCode,
                        ["stderr"] = Decode(data.Stderr), ["stdout"] = Decode(data.Stdout) });
            }
            if (!drained.ContainsKey("presentmon"))
            {
                await File.WriteAllBytesAsync(Path.Combine(output, "presentmon.stdout.log"), [], cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(output, "presentmon.stderr.log"), [], cancellationToken);
            }
            if (!drained.ContainsKey("typeperf"))
            {
                await File.WriteAllBytesAsync(Path.Combine(output, "typeperf.stdout.csv"), [], cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(output, "typeperf.stderr.log"), [], cancellationToken);
            }

            powerStop.Cancel();
            try { await powerTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { powerErrors.Add("Power collector did not stop within ten seconds."); }
            try { await workingSetTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { workingSetErrors.Add("Working-set collector did not stop within ten seconds."); }

            JsonObject presentSummary = PresentSummary(presentCsv, request.TargetFps, request.SwapChainAddress,
                request.ProcessId, request.TrackDisplay);
            presentSummary["presentation_tracking"] = request.TrackDisplay ? "display" : "present_api_only";
            presentSummary["display_tracking_enabled"] = request.TrackDisplay;
            presentSummary["collector"] = CollectorSummary(drained.GetValueOrDefault("presentmon"),
                collectors.Any(item => item.Name == "presentmon"));
            if (drained.TryGetValue("presentmon", out Drained? presentDrain))
                ApplyTraceLoss(presentSummary, Decode(presentDrain.Stderr) + "\n" + Decode(presentDrain.Stdout));
            byte[] typeperfBytes = drained.TryGetValue("typeperf", out Drained? typeperfDrain) ? typeperfDrain.Stdout : [];
            JsonObject typeperfSummary = BuildTypeperfSummary(typeperfBytes, counters, gpu, memory, energy);
            typeperfSummary["collector"] = CollectorSummary(typeperfDrain,
                collectors.Any(item => item.Name == "typeperf"));
            typeperfSummary["discovery_errors"] = discoveryErrors.DeepClone();
            JsonObject cpu = CpuSummary(beforeCpu, afterCpu, elapsed);
            JsonObject targetValidation = ValidateTarget(request.ProcessId, initialIdentity, request.ExpectedProcessPath);
            JsonObject power = new()
            {
                ["gpu_identity"] = gpuIdentity.Count == 0 ? null : gpuIdentity,
                ["watts"] = Stats(powerSamples.OfType<JsonObject>().Select(item => Metric(item["board_watts"]))
                    .Where(value => value is >= 0 && double.IsFinite(value.Value)).Select(value => value!.Value).ToArray()),
                ["graphics_clock_mhz"] = Stats(powerSamples.OfType<JsonObject>().Select(item => Metric(item["graphics_clock_mhz"]))
                    .Where(value => value is >= 0 && double.IsFinite(value.Value)).Select(value => value!.Value).ToArray()),
                ["samples"] = powerSamples, ["errors"] = powerErrors,
                ["scope"] = "Total NVIDIA board telemetry, not wallpaper increment."
            };
            JsonObject workingSet = new()
            {
                ["status"] = workingSetErrors.Count > 0 ? "partial_report" :
                    workingSetSamples.Count > 0 ? "sampled" : "not_measured",
                ["bytes"] = Stats(workingSetSamples.OfType<JsonObject>().Select(item => Metric(item["working_set_bytes"]))
                    .Where(value => value is >= 0 && double.IsFinite(value.Value)).Select(value => value!.Value).ToArray()),
                ["samples"] = workingSetSamples, ["errors"] = workingSetErrors,
                ["scope"] = "Target process WorkingSet64; this is process RAM, not GPU dedicated/shared memory."
            };

            JsonObject platformPower = typeperfSummary["platform_power"]?.DeepClone()?.AsObject() ??
                new JsonObject { ["status"] = "not_measured" };
            platformPower["discovery_errors"] = powerDiscoveryErrors.DeepClone();
            platformPower["seconds_sampled"] = elapsed;
            JsonObject verdict = Verdict(platformPower, request.IgpuWattsThreshold);
            var incomplete = CompletionFailures(targetValidation, presentSummary, typeperfSummary, cpu, power,
                gpu.Length, memory, request.NvidiaGpuUuid, discoveryErrors);
            if (platformPower["status"]?.GetValue<string>() != "sampled")
                incomplete.Add("Platform power was not measured: " + platformPower["status"]?.GetValue<string>() + ".");
            string status = targetValidation["status"]?.GetValue<string>() == "not_valid" ? "not_valid" :
                incomplete.Count == 0 ? "sampled" : "partial_report";
            var report = new JsonObject
            {
                ["schema_version"] = 1, ["status"] = status, ["report_path"] = reportPath,
                ["metadata"] = metadata, ["elapsed_seconds"] = elapsed, ["target_validation"] = targetValidation,
                ["playback"] = playback?.DeepClone(), ["display"] = DisplayMode(),
                ["platform_power"] = platformPower, ["verdict"] = verdict,
                ["presentmon"] = presentSummary, ["typeperf"] = typeperfSummary, ["cpu"] = cpu,
                ["nvidia_board_power"] = power, ["working_set"] = workingSet,
                ["errors"] = errors, ["incomplete_reasons"] = incomplete,
                ["limitations"] = new JsonArray(metadata["scope"]!.DeepClone(), metadata["power_scope"]!.DeepClone(),
                    metadata["gpu_scope"]!.DeepClone(), "No CSV, counter samples, permission-denied read, or ambiguous swap chain is represented as zero or a pass.")
            };
            await WriteAsync(reportPath, report, cancellationToken);
            return report;
        }
        finally
        {
            stopwatch.Stop();
            powerStop.Cancel();
            try { await powerTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); } catch { }
            foreach (var item in collectors)
            {
                KillOwned(item.Process);
                item.Process.Dispose();
            }
        }
    }

    private sealed record Drained(byte[] Stdout, byte[] Stderr, int? ExitCode, bool TimedOut, string? Error);
    private sealed record CsvData(List<string[]> Rows, JsonArray Errors);
    private sealed record PerfCell(string Raw, double? Value);
    private sealed record TypeperfData(List<PerfCell[]> Rows, string? ParseError, JsonArray Errors,
        JsonArray ConsoleOutput);
    private sealed record CpuRead(double? Seconds, string? Error);
    private sealed record TargetIdentity(string? Path, DateTimeOffset? StartUtc, string? Error);

    private static void TryStartCollector(string name, string executable, IEnumerable<string> arguments, int seconds,
        List<(string Name, Process Process, Task<Drained> Drain)> collectors, JsonArray errors,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            errors.Add(new JsonObject { ["collector"] = name, ["error"] = "not found", ["path"] = executable });
            return;
        }
        try
        {
            Process process = Start(executable, arguments);
            collectors.Add((name, process, DrainAsync(process, TimeSpan.FromSeconds(seconds) + CollectorGrace,
                cancellationToken)));
        }
        catch (Exception error)
        {
            errors.Add(new JsonObject { ["collector"] = name, ["stage"] = "start", ["error"] = error.ToString() });
        }
    }

    private static Process Start(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start collector.");
    }

    private static Task WaitForCollectorsAsync(IReadOnlyCollection<Task> drains, TimeSpan noCollectorInterval,
        CancellationToken cancellationToken) => drains.Count == 0
        ? Task.Delay(noCollectorInterval, cancellationToken)
        : Task.WhenAll(drains).WaitAsync(cancellationToken);

    private static async Task<Drained> DrainAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var stdout = new MemoryStream();
        await using var stderr = new MemoryStream();
        Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None);
        Task stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr, CancellationToken.None);
        Task completion = Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), stdoutTask, stderrTask);
        try
        {
            await completion.WaitAsync(timeout, cancellationToken);
            return new(stdout.ToArray(), stderr.ToArray(), ExitCode(process), false, null);
        }
        catch (TimeoutException)
        {
            KillOwned(process);
            await FinishDrainAsync(completion);
            return new(stdout.ToArray(), stderr.ToArray(), ExitCode(process), true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillOwned(process);
            await FinishDrainAsync(completion);
            throw;
        }
        catch (Exception error)
        {
            KillOwned(process);
            await FinishDrainAsync(completion);
            return new(stdout.ToArray(), stderr.ToArray(), ExitCode(process), false, error.ToString());
        }
    }

    private static async Task FinishDrainAsync(Task completion)
    {
        try { await completion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
    }

    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }

    private static int? ExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; } catch { return null; }
    }

    private static async Task<string[]> QueryCountersAsync(string executable, string category, int? processId,
        JsonArray errors, CancellationToken cancellationToken)
    {
        try
        {
            using Process process = Start(executable, ["-qx", category]);
            Drained drained = await DrainAsync(process, TimeSpan.FromSeconds(20), cancellationToken);
            if (drained.TimedOut)
            {
                errors.Add(new JsonObject { ["stage"] = "typeperf_query", ["category"] = category,
                    ["error"] = "collector timed out" });
                return [];
            }
            if (drained.Error is not null || drained.ExitCode is not 0)
            {
                errors.Add(new JsonObject { ["stage"] = "typeperf_query", ["category"] = category,
                    ["exit_code"] = drained.ExitCode, ["error"] = drained.Error,
                    ["stderr"] = Decode(drained.Stderr), ["stdout"] = Decode(drained.Stdout) });
                return [];
            }
            CsvData csv = ParseCsv(Decode(drained.Stdout));
            foreach (JsonNode? error in csv.Errors) errors.Add(error?.DeepClone());
            return csv.Rows.SelectMany(row => row).Select(field => field.Trim())
                .Where(path => path.Length > 0 && (processId is null ||
                    path.Contains($"(pid_{processId}_", StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            errors.Add(new JsonObject { ["stage"] = "typeperf_query", ["category"] = category,
                ["error"] = error.ToString() });
            return [];
        }
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException)
        {
            int codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            Encoding? oem = CodePagesEncodingProvider.Instance.GetEncoding(codePage);
            return (oem ?? Encoding.Latin1).GetString(bytes, offset, bytes.Length - offset);
        }
    }

    private static CsvData ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var errors = new JsonArray();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, quoteClosed = false, fieldStarted = false;
        int logicalRow = 1;
        for (int i = 0; i < text.Length; ++i)
        {
            char current = text[i];
            if (quoted)
            {
                if (current == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); ++i; }
                    else { quoted = false; quoteClosed = true; }
                }
                else field.Append(current);
                continue;
            }
            if (quoteClosed && current is not ',' and not '\r' and not '\n')
            {
                errors.Add(new JsonObject { ["row"] = logicalRow, ["error"] = "characters followed a closing quote" });
                field.Append(current); fieldStarted = true; quoteClosed = false; continue;
            }
            if (current == '"' && !fieldStarted) { quoted = true; fieldStarted = true; continue; }
            if (current == '"')
            {
                errors.Add(new JsonObject { ["row"] = logicalRow, ["error"] = "unexpected quote in unquoted field" });
                field.Append(current); fieldStarted = true; continue;
            }
            if (current == ',')
            {
                row.Add(field.ToString()); field.Clear(); fieldStarted = false; quoteClosed = false; continue;
            }
            if (current is '\r' or '\n')
            {
                if (current == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ++i;
                row.Add(field.ToString()); field.Clear(); fieldStarted = false; quoteClosed = false;
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                row.Clear(); ++logicalRow; continue;
            }
            field.Append(current); fieldStarted = true;
        }
        if (quoted) errors.Add(new JsonObject { ["row"] = logicalRow, ["error"] = "unterminated quoted field" });
        if (field.Length > 0 || fieldStarted || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
        }
        return new(rows, errors);
    }

    private static TypeperfData ParseTypeperf(byte[] bytes, string[] counters)
    {
        CsvData csv = ParseCsv(Decode(bytes));
        var consoleOutput = new JsonArray();
        int headerIndex = csv.Rows.FindIndex(row => row.Length == counters.Length + 1 &&
            row[0].Contains("PDH-CSV", StringComparison.OrdinalIgnoreCase));
        if (headerIndex < 0)
        {
            for (int i = 0; i < csv.Rows.Count; ++i) consoleOutput.Add(ConsoleRow(i + 1, csv.Rows[i]));
            return new([], "No matching CSV header was produced.", csv.Errors, consoleOutput);
        }
        for (int i = 0; i < headerIndex; ++i) consoleOutput.Add(ConsoleRow(i + 1, csv.Rows[i]));
        int columns = counters.Length + 1;
        var rows = new List<PerfCell[]>();
        for (int i = headerIndex + 1; i < csv.Rows.Count; ++i)
        {
            string[] row = csv.Rows[i];
            if (row.Length != columns)
            {
                if (LooksLikeTimestamp(row.FirstOrDefault()))
                    csv.Errors.Add(new JsonObject { ["row"] = i + 1, ["error"] = "unexpected typeperf column count",
                        ["expected"] = columns, ["actual"] = row.Length,
                        ["raw_fields"] = JsonSerializer.SerializeToNode(row) });
                else consoleOutput.Add(ConsoleRow(i + 1, row));
                continue;
            }
            PerfCell[] values = row.Skip(1).Select(raw => new PerfCell(raw, Number(raw))).ToArray();
            int[] invalid = Enumerable.Range(0, values.Length).Where(index =>
                values[index].Value is null || !double.IsFinite(values[index].Value!.Value)).ToArray();
            if (invalid.Length > 0)
                csv.Errors.Add(new JsonObject { ["row"] = i + 1, ["error"] = "invalid numeric typeperf field",
                    ["columns"] = JsonSerializer.SerializeToNode(invalid.Select(index => index + 1).ToArray()),
                    ["raw_values"] = JsonSerializer.SerializeToNode(invalid.Select(index => values[index].Raw).ToArray()) });
            rows.Add(values);
        }
        string? parseError = rows.Count == 0 ? "Typeperf CSV contains no counter samples." :
            csv.Errors.Count > 0 ? "One or more typeperf CSV rows were malformed." : null;
        return new(rows, parseError, csv.Errors, consoleOutput);
    }

    private static JsonObject ConsoleRow(int row, string[] fields) => new()
    {
        ["row"] = row, ["text"] = string.Join(',', fields), ["raw_fields"] = JsonSerializer.SerializeToNode(fields)
    };

    private static bool LooksLikeTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out _) ||
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);
    }

    private static JsonObject BuildTypeperfSummary(byte[] bytes, string[] counters, string[] gpu, string[] memory,
        string[] energy)
    {
        TypeperfData parsed = counters.Length == 0
            ? new([], "No matching GPU process counters were found.", new JsonArray(), new JsonArray())
            : ParseTypeperf(bytes, counters);
        JsonObject memorySummary = MemorySummary(parsed.Rows, counters, memory);
        var gpuSet = gpu.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var memorySet = memory.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var energySet = energy.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int validTimepoints = parsed.Rows.Count(row => counters.Select((path, index) => gpuSet.Contains(path)
            ? ValidPercent(row[index].Value) : energySet.Contains(path) ? ValidBytes(row[index].Value)
            : memorySet.Contains(path) && ValidBytes(row[index].Value)).All(valid => valid));
        string status = parsed.Rows.Count == 0 ? "not_measured" :
            parsed.ParseError is null && validTimepoints == parsed.Rows.Count ? "sampled" : "partial_report";
        return new JsonObject
        {
            ["status"] = status, ["samples"] = parsed.Rows.Count, ["valid_timepoints"] = validTimepoints,
            ["missing_timepoints"] = parsed.Rows.Count - validTimepoints,
            ["valid_coverage_ratio"] = parsed.Rows.Count == 0 ? null : (double)validTimepoints / parsed.Rows.Count,
            ["parse_error"] = parsed.ParseError,
            ["csv_errors"] = parsed.Errors, ["console_output"] = parsed.ConsoleOutput,
            ["adapters"] = GpuSummary(parsed.Rows, counters, gpu),
            ["platform_power"] = PlatformPower(parsed.Rows, counters, energy),
            ["dedicated_bytes"] = memorySummary["dedicated"]?["bytes"]?.DeepClone(),
            ["shared_bytes"] = memorySummary["shared"]?["bytes"]?.DeepClone(),
            ["total_bytes"] = memorySummary["total"]?["bytes"]?.DeepClone(),
            ["memory"] = memorySummary
        };
    }

    private static JsonObject GpuSummary(List<PerfCell[]> rows, string[] counters, string[] gpu)
    {
        var adapters = new JsonObject();
        var gpuSet = gpu.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = counters.Select((path, index) => (Path: path, Index: index))
            .Where(item => gpuSet.Contains(item.Path))
            .GroupBy(item => (Adapter: Luid(item.Path), Kind: EngineKind(item.Path)));
        foreach (var group in groups)
        {
            string key = group.Key.Kind switch { "3d" => "3d_percent", "videodecode" => "video_decode_percent", _ => "copy_percent" };
            int[] indices = group.Select(item => item.Index).ToArray();
            var aggregates = new List<double>();
            var counterReports = new JsonArray();
            foreach (int index in indices)
            {
                string[] invalid = rows.Where(row => !ValidPercent(row[index].Value)).Select(row => row[index].Raw).ToArray();
                counterReports.Add(new JsonObject { ["counter_path"] = counters[index],
                    ["valid_samples"] = rows.Count - invalid.Length, ["invalid_sample_count"] = invalid.Length,
                    ["invalid_raw_values"] = JsonSerializer.SerializeToNode(invalid) });
            }
            foreach (PerfCell[] row in rows)
            {
                double?[] values = indices.Select(index => row[index].Value).ToArray();
                if (values.All(ValidPercent)) aggregates.Add(values.Sum(value => value!.Value));
            }
            var adapter = adapters[group.Key.Adapter] as JsonObject ?? new JsonObject();
            adapter[key] = new JsonObject
            {
                ["utilization_percent"] = Stats(aggregates), ["timepoints"] = rows.Count,
                ["missing_timepoints"] = rows.Count - aggregates.Count,
                ["valid_coverage_ratio"] = rows.Count == 0 ? null : (double)aggregates.Count / rows.Count,
                ["counters"] = counterReports
            };
            adapters[group.Key.Adapter] = adapter;
        }
        return adapters;
    }

    // 机器级功耗：Windows 的 Energy Meter 计数器（RAPL 域，CookedValue 单位毫瓦），
    // 与 abba-power / abba-igpu 两个已验证脚本读的是同一组计数器，换算方式也一致。
    private static JsonObject PlatformPower(List<PerfCell[]> rows, string[] counters, string[] energy)
    {
        var energySet = energy.ToHashSet(StringComparer.OrdinalIgnoreCase);
        (string Instance, int Index)[] indexed = counters.Select((path, index) => (Instance: Instance(path), Index: index))
            .Where(item => energySet.Contains(counters[item.Index])).ToArray();
        var instances = new JsonArray();
        foreach (var item in indexed) instances.Add(item.Instance);
        if (indexed.Length == 0)
            return new JsonObject
            {
                ["status"] = "unsupported_platform", ["source"] = "windows_energy_meter",
                ["missing"] = @"\Energy Meter(*)\Power",
                ["reason"] = "This machine publishes no Energy Meter (RAPL) power counters, so wallpaper power cannot be measured here.",
                ["instances"] = instances, ["package_watts"] = null, ["cores_watts"] = null,
                ["igpu_domain_watts"] = null, ["igpu_domain_method"] = "unavailable"
            };
        int[] package = indexed.Where(item => item.Instance.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Index).ToArray();
        int[] cores = indexed.Where(item => item.Instance.EndsWith("_CORE", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Index).ToArray();
        // Intel 平台把核显单列成 PP1/GT 域；AMD 台式机没有这个域，只能用 PKG − ΣCORE 估（含 uncore）。
        int[] graphics = indexed.Where(item => item.Instance.EndsWith("_PP1", StringComparison.OrdinalIgnoreCase) ||
            item.Instance.Contains("GFX", StringComparison.OrdinalIgnoreCase) ||
            item.Instance.EndsWith("_GT", StringComparison.OrdinalIgnoreCase)).Select(item => item.Index).ToArray();
        var packageWatts = new List<double>();
        var coresWatts = new List<double>();
        var nonCoreWatts = new List<double>();
        var igpuWatts = new List<double>();
        foreach (PerfCell[] row in rows)
        {
            double? pkg = SumWatts(row, package), core = SumWatts(row, cores), gfx = SumWatts(row, graphics);
            if (pkg is not null) packageWatts.Add(pkg.Value);
            if (core is not null) coresWatts.Add(core.Value);
            if (pkg is not null && core is not null && pkg - core >= 0) nonCoreWatts.Add(pkg.Value - core.Value);
            if (gfx is >= 0) igpuWatts.Add(gfx.Value);
        }
        // 只有真正的核显域（Intel 的 PP1/GT）才是壁纸功耗的绝对读数。
        // AMD 台式机只有 PKG 与 CORE，PKG−ΣCORE 里还含 uncore/IO（本机空转就有几十瓦），
        // 拿它当核显功耗会给出假数，所以这种平台直接报 no_graphics_domain，判断留给用户。
        string method = graphics.Length > 0 ? "graphics_domain_counter" : "unavailable";
        string status = rows.Count == 0 ? "not_measured" : graphics.Length == 0 ? "no_graphics_domain" :
            igpuWatts.Count > 0 ? "sampled" : "no_valid_samples";
        return new JsonObject
        {
            ["status"] = status, ["source"] = "windows_energy_meter", ["instances"] = instances,
            ["timepoints"] = rows.Count, ["valid_timepoints"] = igpuWatts.Count,
            ["package_watts"] = Stats(packageWatts), ["cores_watts"] = Stats(coresWatts),
            ["non_core_watts"] = Stats(nonCoreWatts),
            ["igpu_domain_watts"] = Stats(igpuWatts), ["igpu_domain_method"] = method,
            ["missing"] = graphics.Length > 0 ? null
                : @"\Energy Meter(*_PP1|*GFX*|*_GT)\Power — no RAPL graphics domain is published on this machine.",
            ["non_core_scope"] = "Package minus the sum of core domains. It contains the integrated GPU and the uncore, so it is not the wallpaper's power.",
            ["scope"] = "Graphics (PP1/GT) RAPL domain in watts. It is not wall power and excludes any discrete board.",
            ["note"] = "Whole-machine RAPL telemetry: other load on this machine is included, so sample on an idle machine."
        };
    }

    private static double? SumWatts(PerfCell[] row, int[] indices)
    {
        if (indices.Length == 0) return null;
        double total = 0;
        foreach (int index in indices)
        {
            if (!ValidBytes(row[index].Value)) return null;
            total += row[index].Value!.Value;
        }
        return total / 1000;
    }

    private static string Instance(string path)
    {
        Match match = Regex.Match(path, @"\(([^)]*)\)");
        return match.Success ? match.Groups[1].Value : path;
    }

    /// <summary>记录本机原作读数；不从原作功耗阈值推断生成收益。</summary>
    private static JsonObject Verdict(JsonObject power, double threshold)
    {
        string status = power["status"]?.GetValue<string>() ?? "not_measured";
        double? watts = Metric(power["igpu_domain_watts"]?["median"]);
        if (status != "sampled" || watts is null || !double.IsFinite(watts.Value))
            return new JsonObject
            {
                ["status"] = status is "unsupported_platform" or "no_graphics_domain" ? "unsupported_platform" : "not_measured",
                ["threshold_watts"] = null, ["metric"] = "platform_power.igpu_domain_watts.median",
                ["measured_watts"] = null, ["worth_baking"] = null,
                ["text"] = status switch
                {
                    "unsupported_platform" =>
                        "This machine cannot measure wallpaper power (no Energy Meter/RAPL counters). Decide by hand whether to bake.",
                    "no_graphics_domain" =>
                        "This machine publishes package and core RAPL domains but no graphics domain, so the wallpaper's own power cannot be measured here. Package minus cores also contains the uncore and is not a wallpaper reading.",
                    _ => "Wallpaper power was not measured here, so no baking recommendation is given."
                }
            };
        return new JsonObject
        {
            ["status"] = "measured", ["threshold_watts"] = null,
            ["metric"] = "platform_power.igpu_domain_watts.median", ["measured_watts"] = watts.Value,
            ["measurement_method"] = power["igpu_domain_method"]?.DeepClone(), ["worth_baking"] = null,
            ["measurement_scope"] = "this_device_only",
            ["text"] = string.Create(CultureInfo.InvariantCulture,
                $"The source draws {watts.Value:F2} W on this machine's integrated-GPU domain. This alone does not establish baking savings or load on other devices.")
        };
    }

    /// <summary>刷新率随电源计划或显示设置变化都会改变功耗，逐次记录下来，不同刷新率之间的读数不可比。</summary>
    private static JsonObject DisplayMode()
    {
        if (!OperatingSystem.IsWindows()) return new JsonObject { ["status"] = "unsupported_platform" };
        nint context = GetDC(0);
        if (context == 0) return new JsonObject { ["status"] = "not_measured", ["error"] = "GetDC returned no device context." };
        try
        {
            // DESKTOPHORZRES/DESKTOPVERTRES report physical pixels; HORZRES/VERTRES
            // were DPI-virtualized (the local 4K display appeared as 3072x1728 at 125%).
            int refresh = GetDeviceCaps(context, 116), width = GetDeviceCaps(context, 118), height = GetDeviceCaps(context, 117);
            return new JsonObject
            {
                ["status"] = refresh > 1 ? "read" : "not_measured",
                ["refresh_hz"] = refresh > 1 ? refresh : null, ["width"] = width, ["height"] = height,
                ["scope"] = "Primary display physical mode read at report time, not the test window or swapchain size. Readings taken at different refresh rates are not comparable."
            };
        }
        finally { _ = ReleaseDC(0, context); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint context);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint context, int index);

    private static JsonObject MemorySummary(List<PerfCell[]> rows, string[] counters, string[] memory)
    {
        var memorySet = memory.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int[] dedicated = counters.Select((path, index) => (path, index)).Where(item => memorySet.Contains(item.path) &&
            item.path.EndsWith(@"\Dedicated Usage", StringComparison.OrdinalIgnoreCase)).Select(item => item.index).ToArray();
        int[] shared = counters.Select((path, index) => (path, index)).Where(item => memorySet.Contains(item.path) &&
            item.path.EndsWith(@"\Shared Usage", StringComparison.OrdinalIgnoreCase)).Select(item => item.index).ToArray();
        return new JsonObject
        {
            ["dedicated"] = MemoryCoverage(rows, counters, dedicated),
            ["shared"] = MemoryCoverage(rows, counters, shared),
            ["total"] = MemoryCoverage(rows, counters, dedicated.Concat(shared).ToArray())
        };
    }

    private static JsonObject MemoryCoverage(List<PerfCell[]> rows, string[] counters, int[] indices)
    {
        var values = new List<double>();
        var counterReports = new JsonArray();
        foreach (int index in indices)
        {
            string[] invalid = rows.Where(row => !ValidBytes(row[index].Value)).Select(row => row[index].Raw).ToArray();
            counterReports.Add(new JsonObject { ["counter_path"] = counters[index],
                ["valid_samples"] = rows.Count - invalid.Length, ["invalid_sample_count"] = invalid.Length,
                ["invalid_raw_values"] = JsonSerializer.SerializeToNode(invalid) });
        }
        if (indices.Length > 0)
            foreach (PerfCell[] row in rows)
            {
                double?[] samples = indices.Select(index => row[index].Value).ToArray();
                if (samples.All(ValidBytes)) values.Add(samples.Sum(value => value!.Value));
            }
        return new JsonObject
        {
            ["status"] = indices.Length == 0 ? "not_measured" : values.Count == 0 ? "no_valid_samples" : "sampled",
            ["bytes"] = Stats(values), ["timepoints"] = rows.Count, ["valid_timepoints"] = values.Count,
            ["missing_timepoints"] = rows.Count - values.Count,
            ["valid_coverage_ratio"] = rows.Count == 0 ? null : (double)values.Count / rows.Count,
            ["counters"] = counterReports
        };
    }

    private static void ApplyTraceLoss(JsonObject summary, string diagnostics)
    {
        // PresentMon can exit successfully after losing events. Its surviving CSV
        // cannot establish displayed cadence or a valid dropped-frame ratio.
        if (!Regex.IsMatch(diagnostics, @"\b[1-9]\d* ETW (events|buffers) were lost\b", RegexOptions.IgnoreCase)) return;
        summary["trace_event_loss"] = true;
        summary["trace_loss_diagnostics"] = diagnostics.Trim();
        summary["verified_target_fps"] = false;
        if (summary["status"]?.GetValue<string>() == "sampled") summary["status"] = "incomplete_trace";
    }

    private static JsonObject PresentSummary(string path, double targetFps, string? selectedAddress,
        int? expectedProcessId = null, bool trackDisplay = true)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return new JsonObject { ["status"] = "not_measured",
                ["reason"] = "PresentMon produced no CSV; the target may have had no active presentation.",
                ["verified_target_fps"] = false, ["swapchains"] = new JsonObject(), ["csv_errors"] = new JsonArray() };
        CsvData csv;
        try { csv = ParseCsv(Decode(File.ReadAllBytes(path))); }
        catch (Exception error)
        {
            return new JsonObject { ["status"] = "unreadable", ["error"] = error.ToString(),
                ["verified_target_fps"] = false, ["swapchains"] = new JsonObject(), ["csv_errors"] = new JsonArray() };
        }
        if (csv.Rows.Count < 2)
            return new JsonObject { ["status"] = "not_measured", ["reason"] = "PresentMon CSV has no presentation rows.",
                ["verified_target_fps"] = false, ["swapchains"] = new JsonObject(), ["csv_errors"] = csv.Errors };
        string[] header = csv.Rows[0];
        int chainIndex = Header(header, "SwapChainAddress", "SwapChain", "SwapChainID");
        int presentsIndex = Header(header, "msBetweenPresents");
        int displayIndex = Header(header, "msBetweenDisplayChange");
        int droppedIndex = Header(header, "Dropped", "WasDropped");
        int runtimeIndex = Header(header, "Runtime");
        int processIndex = Header(header, "ProcessID");
        if (chainIndex < 0 || presentsIndex < 0 && displayIndex < 0 || expectedProcessId is not null && processIndex < 0)
            return new JsonObject { ["status"] = "partial_report", ["reason"] = "Required PresentMon v1 columns are unavailable.",
                ["verified_target_fps"] = false, ["swapchains"] = new JsonObject(), ["csv_errors"] = csv.Errors };

        var chains = new Dictionary<string, PresentChain>(StringComparer.OrdinalIgnoreCase);
        int validRows = 0, excludedProcessRows = 0;
        for (int i = 1; i < csv.Rows.Count; ++i)
        {
            string[] row = csv.Rows[i];
            if (row.Length != header.Length)
            {
                csv.Errors.Add(new JsonObject { ["row"] = i + 1, ["error"] = "unexpected PresentMon column count",
                    ["expected"] = header.Length, ["actual"] = row.Length,
                    ["raw_fields"] = JsonSerializer.SerializeToNode(row) });
                continue;
            }
            if (expectedProcessId is not null)
            {
                if (!int.TryParse(row[processIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                {
                    csv.Errors.Add(new JsonObject { ["row"] = i + 1, ["error"] = "invalid ProcessID",
                        ["raw_value"] = row[processIndex] });
                    continue;
                }
                if (pid != expectedProcessId) { ++excludedProcessRows; continue; }
            }
            ++validRows;
            string address = string.IsNullOrWhiteSpace(row[chainIndex]) ? "unidentified_swapchain" : row[chainIndex];
            if (!chains.TryGetValue(address, out PresentChain? chain)) chains.Add(address, chain = new());
            ++chain.Rows;
            if (runtimeIndex >= 0) chain.Runtimes.Add(row[runtimeIndex]);
            double? interval = presentsIndex >= 0 ? Number(row[presentsIndex]) : null;
            if (interval is > 0 && double.IsFinite(interval.Value)) chain.Intervals.Add(interval.Value);
            else if (presentsIndex >= 0)
            {
                ++chain.InvalidIntervals;
                chain.InvalidIntervalRawValues.Add(row[presentsIndex]);
            }
            if (trackDisplay)
            {
                string dropped = droppedIndex < 0 ? "" : row[droppedIndex].Trim().ToLowerInvariant();
                if (dropped is "1" or "true" or "yes") ++chain.Dropped;
                else if (dropped is "0" or "false" or "no") ++chain.Displayed;
                else ++chain.UnknownDisplayStatus;
                // Dropped presents have no display interval and must not dilute displayed FPS.
                if (dropped is "0" or "false" or "no")
                {
                    double? display = displayIndex >= 0 ? Number(row[displayIndex]) : null;
                    if (display is > 0 && double.IsFinite(display.Value)) chain.DisplayIntervals.Add(display.Value);
                    else chain.InvalidDisplayRawValues.Add(displayIndex >= 0 ? row[displayIndex] : "<missing column>");
                }
            }
        }
        var swapchains = new JsonObject();
        foreach (var (address, chain) in chains)
        {
            double[] fps = chain.Intervals.Select(value => 1000 / value).ToArray();
            swapchains[address] = new JsonObject
            {
                ["rows"] = chain.Rows, ["valid_interval_rows"] = chain.Intervals.Count,
                ["runtimes"] = JsonSerializer.SerializeToNode(chain.Runtimes.Order(StringComparer.Ordinal)),
                ["invalid_interval_rows"] = chain.InvalidIntervals,
                ["valid_interval_coverage_ratio"] = chain.Rows == 0 ? null : (double)chain.Intervals.Count / chain.Rows,
                ["invalid_interval_raw_values"] = JsonSerializer.SerializeToNode(chain.InvalidIntervalRawValues),
                ["interval_ms"] = Stats(chain.Intervals),
                ["effective_fps"] = chain.Intervals.Count == 0 ? null : 1000 / chain.Intervals.Average(),
                ["present_fps"] = chain.Intervals.Count == 0 ? null : 1000 / chain.Intervals.Average(),
                ["instantaneous_fps"] = Stats(fps),
                ["dropped_rows"] = trackDisplay && chain.UnknownDisplayStatus == 0 ? chain.Dropped : null,
                ["dropped_row_ratio"] = trackDisplay && chain.UnknownDisplayStatus == 0 && chain.Rows > 0 ? (double)chain.Dropped / chain.Rows : null,
                ["below_target_fps_ratio"] = fps.Length == 0 ? null : (double)fps.Count(value => value < targetFps) / fps.Length,
                ["displayed_rows"] = trackDisplay && chain.UnknownDisplayStatus == 0 ? chain.Displayed : null,
                ["unknown_display_status_rows"] = trackDisplay ? chain.UnknownDisplayStatus : null,
                ["display_interval_rows"] = chain.DisplayIntervals.Count,
                ["missing_display_interval_rows"] = trackDisplay ? chain.InvalidDisplayRawValues.Count : null,
                ["invalid_display_interval_raw_values"] = JsonSerializer.SerializeToNode(chain.InvalidDisplayRawValues),
                ["display_interval_ms"] = Stats(chain.DisplayIntervals),
                ["displayed_fps"] = chain.DisplayIntervals.Count == 0 ? null : 1000 / chain.DisplayIntervals.Average(),
                ["display_status"] = !trackDisplay || chain.DisplayIntervals.Count == 0 ? "not_measured" :
                    chain.UnknownDisplayStatus == 0 && chain.DisplayIntervals.Count == chain.Displayed ? "sampled" : "partial_report",
                ["display_evidence_complete"] = trackDisplay && chain.UnknownDisplayStatus == 0 &&
                    chain.DisplayIntervals.Count > 0 && chain.DisplayIntervals.Count == chain.Displayed
            };
        }
        string? resolved = selectedAddress is not null
            ? chains.Keys.FirstOrDefault(key => key.Equals(selectedAddress, StringComparison.OrdinalIgnoreCase))
            : chains.Count == 1 ? chains.Keys.Single() : null;
        bool requestedMissing = selectedAddress is not null && resolved is null;
        bool unidentified = selectedAddress is null && chains.Count == 1 && chains.ContainsKey("unidentified_swapchain");
        bool ambiguous = selectedAddress is null && (chains.Count != 1 || unidentified);
        JsonObject? resolvedChain = resolved is null ? null : swapchains[resolved]?.AsObject();
        bool incompleteIntervals = chains.Values.Any(chain => chain.InvalidIntervals > 0);
        string status = validRows == 0 || chains.Values.All(chain => chain.Intervals.Count == 0 && chain.DisplayIntervals.Count == 0) ? "not_measured" :
            requestedMissing || incompleteIntervals || unidentified || csv.Errors.Count > 0
                ? "partial_report" : "sampled";
        return new JsonObject
        {
            ["status"] = status, ["reason"] = requestedMissing ? "Requested SwapChainAddress was not present in the CSV." :
                status == "not_measured" ? "PresentMon CSV contained no valid presentation intervals." :
                status == "partial_report" ? "PresentMon CSV contained incomplete or malformed swap-chain evidence." : null,
            ["swapchains"] = swapchains, ["csv_rows"] = validRows, ["csv_rows_total"] = csv.Rows.Count - 1,
            ["expected_process_id"] = expectedProcessId, ["excluded_process_rows"] = excludedProcessRows,
            ["display_tracking_enabled"] = trackDisplay,
            ["csv_errors"] = csv.Errors,
            ["selected_swapchain_address"] = selectedAddress, ["resolved_swapchain_address"] = resolved,
            ["frame_pacing_scope"] = selectedAddress is not null ? "requested_swapchain" : ambiguous ? "ambiguous" : "single_swapchain",
            ["verified_target_fps"] = !ambiguous && !requestedMissing && csv.Errors.Count == 0 &&
                DisplayMeetsTarget(resolvedChain, targetFps),
            ["metric_note"] = "effective_fps, interval_ms and below_target_fps_ratio describe Present API submissions only. displayed_fps uses positive msBetweenDisplayChange values from displayed rows; missing display evidence never verifies target FPS. Chains are never summed."
        };
    }

    private sealed class PresentChain
    {
        public int Rows, InvalidIntervals, Dropped, Displayed, UnknownDisplayStatus;
        public List<double> Intervals { get; } = [];
        public List<double> DisplayIntervals { get; } = [];
        public List<string> InvalidDisplayRawValues { get; } = [];
        public List<string> InvalidIntervalRawValues { get; } = [];
        public HashSet<string> Runtimes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool DisplayMeetsTarget(JsonObject? chain, double targetFps) =>
        chain?["display_evidence_complete"]?.GetValue<bool>() == true &&
        Metric(chain["displayed_fps"]) is double fps && double.IsFinite(fps) && fps >= targetFps;

    private static int Header(string[] header, params string[] names) =>
        Array.FindIndex(header, field => names.Any(name => field.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static JsonObject? Stats(IReadOnlyCollection<double> source)
    {
        double[] values = source.Where(double.IsFinite).Order().ToArray();
        if (values.Length == 0) return null;
        double Percentile(double percentile)
        {
            double index = (values.Length - 1) * percentile;
            int low = (int)index, high = Math.Min(low + 1, values.Length - 1);
            return values[low] + (values[high] - values[low]) * (index - low);
        }
        double median = values.Length % 2 == 1 ? values[values.Length / 2] :
            (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        return new JsonObject { ["samples"] = values.Length, ["mean"] = values.Average(), ["median"] = median,
            ["p50"] = Percentile(.5), ["p95"] = Percentile(.95), ["p99"] = Percentile(.99),
            ["minimum"] = values[0], ["maximum"] = values[^1] };
    }

    private static double? Number(string? raw) => double.TryParse(raw?.Trim(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) ? value : null;
    private static double? Metric(JsonNode? node)
    {
        try { return node?.GetValue<double>(); } catch { return null; }
    }
    private static bool ValidPercent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value);
    private static bool ValidBytes(double? value) => value is >= 0 && double.IsFinite(value.Value);
    private static string EngineKind(string path) => Regex.Match(path, @"engtype_([^)]+)\)",
        RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
    private static string Luid(string path)
    {
        Match match = Regex.Match(path, @"luid_(0x[0-9a-f]+_0x[0-9a-f]+_phys_[^_\\)]+)_eng_", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "unparsed_adapter";
    }

    private static CpuRead ReadCpu(Process process)
    {
        try
        {
            process.Refresh();
            return new((process.PrivilegedProcessorTime + process.UserProcessorTime).TotalSeconds, null);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(null, error.ToString());
        }
    }

    private static JsonObject CpuSummary(CpuRead before, CpuRead after, double elapsed)
    {
        double? delta = before.Seconds is not null && after.Seconds is not null ? after.Seconds - before.Seconds : null;
        string? error = before.Error ?? after.Error;
        if (delta is < 0 || elapsed <= 0 || !double.IsFinite(elapsed))
        {
            error ??= "CPU counter interval was negative or elapsed time was invalid.";
            delta = null;
        }
        return new JsonObject
        {
            ["process_cpu_seconds"] = delta,
            ["one_core_percent"] = delta is null ? null : 100 * delta / elapsed,
            ["machine_percent"] = delta is null ? null : 100 * delta / elapsed / Math.Max(1, Environment.ProcessorCount),
            ["elapsed_seconds"] = elapsed, ["logical_processor_count"] = Environment.ProcessorCount,
            ["error"] = error, ["before_error"] = before.Error, ["after_error"] = after.Error
        };
    }

    private static TargetIdentity ReadTargetIdentity(Process process)
    {
        string? path = null;
        DateTimeOffset? start = null;
        var errors = new List<string>();
        try { path = process.MainModule?.FileName; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { errors.Add(error.ToString()); }
        try { start = process.StartTime.ToUniversalTime(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { errors.Add(error.ToString()); }
        return new(path, start, errors.Count == 0 ? null : string.Join(Environment.NewLine, errors));
    }

    private static JsonObject ValidateTarget(int processId, TargetIdentity initial, string? expectedProcessPath)
    {
        TargetIdentity? final = null;
        bool exited = false;
        string? validationError = null;
        try
        {
            using Process current = Process.GetProcessById(processId);
            exited = current.HasExited;
            if (!exited) final = ReadTargetIdentity(current);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            exited = true; validationError = error.ToString();
        }
        bool? sameStart = initial.StartUtc is not null && final?.StartUtc is not null
            ? initial.StartUtc == final.StartUtc : null;
        bool? samePath = initial.Path is not null && final?.Path is not null
            ? string.Equals(initial.Path, final.Path, StringComparison.OrdinalIgnoreCase) : null;
        bool expectedMatches = expectedProcessPath is null || final?.Path is not null &&
            string.Equals(Path.GetFullPath(expectedProcessPath), final.Path, StringComparison.OrdinalIgnoreCase);
        bool changed = exited || sameStart == false || samePath == false || !expectedMatches;
        bool verified = !changed && sameStart == true && samePath == true;
        return new JsonObject
        {
            ["status"] = changed ? "not_valid" : verified ? "valid" : "not_verified", ["pid"] = processId,
            ["process_exited_or_missing"] = exited, ["same_start_time"] = sameStart, ["same_process_path"] = samePath,
            ["initial_path"] = initial.Path, ["final_path"] = final?.Path,
            ["initial_start_utc"] = initial.StartUtc?.ToString("O"), ["final_start_utc"] = final?.StartUtc?.ToString("O"),
            ["initial_error"] = initial.Error, ["final_error"] = final?.Error, ["validation_error"] = validationError
        };
    }

    private static JsonObject CollectorSummary(Drained? drained, bool started) => new()
    {
        ["started"] = started, ["exit_code"] = drained?.ExitCode, ["timed_out"] = drained?.TimedOut ?? false,
        ["error"] = drained?.Error,
        ["completed_successfully"] = started && drained is { ExitCode: 0, TimedOut: false, Error: null }
    };

    private static JsonArray CompletionFailures(JsonObject target, JsonObject present, JsonObject typeperf, JsonObject cpu,
        JsonObject power, int gpuCounterCount, string[] memoryCounters, string? requestedGpuUuid, JsonArray discoveryErrors)
    {
        var failures = new JsonArray();
        if (target["status"]?.GetValue<string>() != "valid") failures.Add("Target process identity was not valid for the full interval.");
        if (present["collector"]?["completed_successfully"]?.GetValue<bool>() != true)
            failures.Add("PresentMon collector did not complete successfully.");
        if (present["status"]?.GetValue<string>() != "sampled") failures.Add("PresentMon did not produce a complete sampled CSV.");
        if (cpu["process_cpu_seconds"] is null) failures.Add("Process CPU time was not measured for the actual interval.");
        if (gpuCounterCount == 0) failures.Add("No matching GPU engine utilization counters were discovered.");
        if (!memoryCounters.Any(path => path.EndsWith(@"\Dedicated Usage", StringComparison.OrdinalIgnoreCase)))
            failures.Add("No dedicated GPU process-memory counter was discovered.");
        if (!memoryCounters.Any(path => path.EndsWith(@"\Shared Usage", StringComparison.OrdinalIgnoreCase)))
            failures.Add("No shared GPU process-memory counter was discovered.");
        if (typeperf["collector"]?["completed_successfully"]?.GetValue<bool>() != true ||
            typeperf["status"]?.GetValue<string>() != "sampled" ||
            typeperf["samples"]?.GetValue<int>() <= 0 || typeperf["parse_error"] is not null)
            failures.Add("Typeperf did not provide a complete counter sample set.");
        if (discoveryErrors.Count > 0) failures.Add("One or more performance-counter discovery queries failed.");
        if (requestedGpuUuid is not null && (power["gpu_identity"] is null || power["watts"] is null ||
            power["errors"] is JsonArray { Count: > 0 }))
            failures.Add("Pinned NVIDIA board-power telemetry was not measured.");
        return failures;
    }

    private static async Task PollWorkingSetAsync(Process process, JsonArray samples, JsonArray errors,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                process.Refresh();
                if (process.HasExited) return;
                samples.Add(new JsonObject
                {
                    ["time_utc"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    ["working_set_bytes"] = process.WorkingSet64
                });
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            errors.Add(new JsonObject { ["stage"] = "poll", ["error"] = error.ToString() });
        }
    }

    private static async Task PollPowerAsync(string executable, string? requestedUuid, JsonObject identity,
        JsonArray samples, JsonArray errors, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            errors.Add(new JsonObject { ["stage"] = "availability", ["error"] = "nvidia-smi.exe is unavailable" });
            return;
        }
        try
        {
            string selector = requestedUuid ?? "0";
            Drained identityResult = await RunOwnedAsync(executable,
                ["-i", selector, "--query-gpu=name,uuid,pci.bus_id", "--format=csv,noheader,nounits"],
                TimeSpan.FromSeconds(5), cancellationToken);
            if (identityResult.TimedOut || identityResult.ExitCode is not 0 || identityResult.Error is not null)
                throw new IOException("NVIDIA identity query failed: " + CollectorDetail(identityResult));
            CsvData identityCsv = ParseCsv(Decode(identityResult.Stdout));
            if (identityCsv.Errors.Count > 0 || identityCsv.Rows.FirstOrDefault() is not { Length: >= 3 } fields)
                throw new InvalidDataException("NVIDIA identity query returned malformed CSV.");
            string pinnedUuid = fields[1].Trim();
            if (requestedUuid is not null && !NormalizeUuid(pinnedUuid).Equals(NormalizeUuid(requestedUuid),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("NVIDIA identity query did not return the requested UUID.");
            identity["index"] = requestedUuid is null ? 0 : null;
            identity["name"] = fields[0].Trim(); identity["uuid"] = pinnedUuid; identity["pci_bus_id"] = fields[2].Trim();
            while (!cancellationToken.IsCancellationRequested)
            {
                Drained reading = await RunOwnedAsync(executable,
                    ["-i", pinnedUuid, "--query-gpu=power.draw,clocks.current.graphics", "--format=csv,noheader,nounits"],
                    TimeSpan.FromSeconds(5), cancellationToken);
                if (reading.TimedOut || reading.ExitCode is not 0 || reading.Error is not null)
                    throw new IOException("NVIDIA telemetry query failed: " + CollectorDetail(reading));
                CsvData csv = ParseCsv(Decode(reading.Stdout));
                if (csv.Errors.Count > 0 || csv.Rows.FirstOrDefault() is not { Length: >= 2 } values ||
                    Number(values[0]) is not double watts || Number(values[1]) is not double clock ||
                    !double.IsFinite(watts) || !double.IsFinite(clock))
                    throw new InvalidDataException("NVIDIA telemetry query returned malformed CSV.");
                samples.Add(new JsonObject { ["time_utc"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    ["board_watts"] = watts, ["graphics_clock_mhz"] = clock });
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            errors.Add(new JsonObject { ["stage"] = "poll", ["error"] = error.ToString() });
        }
    }

    private static async Task<Drained> RunOwnedAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using Process process = Start(executable, arguments);
        return await DrainAsync(process, timeout, cancellationToken);
    }

    private static string CollectorDetail(Drained drained)
    {
        if (drained.Error is not null) return drained.Error;
        string stderr = Decode(drained.Stderr);
        return !string.IsNullOrWhiteSpace(stderr) ? stderr : $"exit={drained.ExitCode}, timed_out={drained.TimedOut}";
    }
    private static string NormalizeUuid(string value) => value.Replace("GPU-", "", StringComparison.OrdinalIgnoreCase)
        .Replace("-", "", StringComparison.Ordinal).Trim();

    private static Task WriteAsync(string path, JsonNode? node, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, node?.ToJsonString(JsonOptions) ?? "null", cancellationToken);
}
