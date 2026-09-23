using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Periodica.Bench;

/// <summary>
/// Wallpaper Engine 官方 <c>-control</c> 命令的最小封装（同 X1 编排脚本的做法）：按显示器编号取当前壁纸、打开壁纸、
/// 暂停/继续播放。不读写 config.json，不重启 WPE；还原就是把原来的文件再 openWallpaper 一次。
/// </summary>
internal sealed class WpeControl(string executable)
{
    internal string Executable { get; } = Path.GetFullPath(executable);

    internal async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Wallpaper Engine did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (await stdout).Trim().Trim('"');
    }

    internal Task<string> GetWallpaperAsync(int monitor, CancellationToken token) =>
        RunAsync(["-control", "getWallpaper", "-monitor", monitor.ToString(CultureInfo.InvariantCulture)], token);

    internal Task OpenAsync(string project, int monitor, CancellationToken token) =>
        RunAsync(["-control", "openWallpaper", "-file", ProjectFile(project), "-monitor",
            monitor.ToString(CultureInfo.InvariantCulture)], token);

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
