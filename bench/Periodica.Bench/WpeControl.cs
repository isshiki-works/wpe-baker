using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace Periodica.Bench;

/// <summary>
/// Wallpaper Engine 官方 <c>-control</c> 命令的最小封装（同 X1 编排脚本的做法）：按显示器编号取当前壁纸、打开壁纸、
/// 暂停/继续播放。只关闭指定显示器的旧壁纸，不重启 WPE。
/// </summary>
internal sealed class WpeControl(string executable,
    Func<IEnumerable<string>, CancellationToken, Task<string>>? send = null)
{
    internal string Executable { get; } = Path.GetFullPath(executable);
    internal string ConfigPath => Path.Combine(Path.GetDirectoryName(Executable)!, "config.json");
    internal static JsonObject ParseConfig(byte[] bytes) =>
        JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))!.AsObject();

    internal static void RequireSingleMonitor(int monitor, int displayCount)
    {
        if (monitor != 0 || displayCount != 1)
            throw new InvalidDataException("Playback comparison supports one connected display and monitor 0 only; isolated loading stops Wallpaper Engine globally.");
    }

    internal void RequireSingleMonitor(int monitor) => RequireSingleMonitor(monitor, GetSystemMetrics(80));

    internal static JsonObject Assignment(JsonObject config, string profile)
    {
        if (config[profile]?["general"]?["wallpaperconfig"]?["selectedwallpapers"] is not JsonObject selected ||
            selected["Monitor0"] is not JsonObject assignment || assignment["file"] is null)
            throw new InvalidDataException("Playback comparison requires one unambiguous Monitor0 assignment for the current Windows account.");
        if (assignment["playlist"] is JsonNode playlist &&
            !(playlist is JsonValue value && value.TryGetValue<bool>(out bool active) && !active))
            throw new InvalidDataException("Playback comparison cannot safely restore an active playlist. Select one wallpaper manually in Wallpaper Engine before comparing.");
        return assignment;
    }

    internal async Task<(string File, string Evidence)> ObserveAsync(int monitor, CancellationToken token)
    {
        string selected = await GetWallpaperAsync(monitor, token);
        if (selected.Length > 0) return (selected, "official_getWallpaper");
        // A saved assignment is enough for guarded control, but never verifies rendered playback.
        var config = ParseConfig(await File.ReadAllBytesAsync(ConfigPath, token));
        return (Assignment(config, Environment.UserName)["file"]!.GetValue<string>(), "saved_configuration_getWallpaper_returned_empty");
    }

    // Only the selected file and WPE's known removal of local=true belong to this comparison.
    internal static bool ConfigMatchesSelection(JsonObject before, JsonObject current, string profile)
    {
        JsonObject expected = before.DeepClone().AsObject();
        JsonObject oldAssignment = Assignment(expected, profile), currentAssignment = Assignment(current, profile);
        oldAssignment["file"] = currentAssignment["file"]!.DeepClone();
        if (oldAssignment["local"] is JsonValue local && local.TryGetValue<bool>(out bool enabled) && enabled && currentAssignment["local"] is null)
            oldAssignment.Remove("local");
        return JsonNode.DeepEquals(expected, current);
    }

    internal static bool OnlyLocalFlagRemoved(JsonObject before, JsonObject current, string profile)
    {
        JsonObject expected = before.DeepClone().AsObject();
        JsonObject assignment = Assignment(expected, profile);
        if (assignment["local"] is not JsonValue local || !local.TryGetValue<bool>(out bool enabled) || !enabled) return false;
        assignment.Remove("local");
        return JsonNode.DeepEquals(expected, current);
    }

    internal static bool OnlyMonitorRemoved(JsonObject before, JsonObject current, string profile)
    {
        JsonObject expected = before.DeepClone().AsObject();
        expected[profile]!["general"]!["wallpaperconfig"]!["selectedwallpapers"]!.AsObject().Remove("Monitor0");
        return JsonNode.DeepEquals(expected, current);
    }

    internal (int Pid, long StartTicks) Identity()
    {
        int pid = ProcessId();
        using Process process = Process.GetProcessById(pid);
        return (pid, process.StartTime.ToUniversalTime().Ticks);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    internal async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        if (send is not null) return await send(arguments, token);
        var start = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Executable)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Wallpaper Engine did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var cancelled = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
        if (process.ExitCode != 0) throw new IOException($"Wallpaper Engine control exited {process.ExitCode}: {stderr.Result}");
        return stdout.Result.Trim().Trim('"');
    }

    internal Task<string> GetWallpaperAsync(int monitor, CancellationToken token) =>
        RunAsync(["-control", "getWallpaper", "-monitor", monitor.ToString(CultureInfo.InvariantCulture)], token);

    internal Task OpenAsync(string project, int monitor, CancellationToken token) =>
        RunAsync(["-control", "openWallpaper", "-file", ProjectFile(project), "-monitor",
            monitor.ToString(CultureInfo.InvariantCulture)], token);

    internal Task StopAsync(CancellationToken token) => RunAsync(["-control", "stop"], token);

    internal Task CloseAsync(int monitor, CancellationToken token) =>
        RunAsync(["-control", "closeWallpaper", "-monitor", monitor.ToString(CultureInfo.InvariantCulture)], token);

    internal async Task IsolatedOpenAsync(string project, int monitor, CancellationToken token,
        Action? afterClose = null, Action? afterOpen = null)
    {
        await StopAsync(token);
        await CloseAsync(monitor, token);
        afterClose?.Invoke();
        await OpenAsync(project, monitor, token);
        afterOpen?.Invoke();
        await PlayAsync(token);
    }

    internal Task PauseAsync(CancellationToken token) => RunAsync(["-control", "pause"], token);

    internal Task PlayAsync(CancellationToken token) => RunAsync(["-control", "play"], token);

    /// <summary>目录形式的工程取其 project.json；已经是文件就原样用。</summary>
    internal static string ProjectFile(string project) =>
        Directory.Exists(project) ? Path.Combine(Path.GetFullPath(project), "project.json") : Path.GetFullPath(project);

    /// <summary>与可执行文件路径一致的那一个正在运行的 WPE 进程；不是恰好一个就报错。</summary>
    internal int ProcessId()
    {
        Process[] candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable));
        try
        {
            int[] ids = candidates.Where(process =>
            {
                try { return string.Equals(process.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                { return false; }
            }).Select(process => process.Id).ToArray();
            if (ids.Length != 1)
                throw new InvalidDataException("Exactly one running Wallpaper Engine process must match the executable.");
            return ids[0];
        }
        finally { foreach (Process process in candidates) process.Dispose(); }
    }
}
