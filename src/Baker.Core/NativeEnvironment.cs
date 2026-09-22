using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Win32;

namespace Baker.Core;

/// <summary>Finds local bundled native tools and installed Wallpaper Engine assets without changing desktop state.</summary>
public static class NativeEnvironment
{
    /// <summary>
    /// 找到随包的原生工具。缺件时点名缺的是哪一个、应该在哪个路径、该怎么办——以前只有一句
    /// "Required tool is missing."，界面上看不出要做什么。
    /// <para>
    /// 抛出的异常消息是英文原文，机器可读的字段沿用英文；异常带着 <see cref="Message"/>（键 + 参数），
    /// 显示给用户之前按当前语言重新渲染。
    /// </para>
    /// </summary>
    public static NativeTools FindTools()
    {
        string? configuration = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
            .Select(directory => Path.Combine(directory, "tools.json")).FirstOrDefault(File.Exists);
        NativeTools tools;
        if (configuration is not null)
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
            var value = JsonSerializer.Deserialize<NativeTools>(File.ReadAllText(configuration), options)
                ?? throw new Message("setup.tools_config_missing").Error(text => new InvalidDataException(text));
            string root = Path.GetDirectoryName(configuration)!;
            string Resolve(string path) => Path.GetFullPath(path, root);
            tools = new(Resolve(value.Renderer), Resolve(value.Ffmpeg), Resolve(value.Ffprobe), (value.RuntimeDirectories ?? []).Select(Resolve).ToArray());
        }
        else
        {
            string? root = DevelopmentRoot();
            if (root is null) throw new Message("setup.tools_config_missing").Error(text => new FileNotFoundException(text));
            tools = new(Path.Combine(root, "build/native-release22/bin/wpe-render.exe"),
                Path.Combine(root, ".deps/ffmpeg-encoder-gpl2/portable/ffmpeg.exe"),
                Path.Combine(root, ".deps/ffmpeg-encoder-gpl2/portable/ffprobe.exe"),
                [Path.Combine(root, ".tools/llvm-mingw-22/bin"), Path.Combine(root, ".deps/ffmpeg-lgpl21/prefix/bin")]);
        }
        foreach (var (file, name) in new[] { (tools.Renderer, "setup.tool_renderer"), (tools.Ffmpeg, "setup.tool_ffmpeg"), (tools.Ffprobe, "setup.tool_ffprobe") })
            if (!File.Exists(file))
                throw new Message("setup.tool_file_missing", [MessageCatalog.Get(name, MessageCatalog.English), file], [MessageCatalog.Get(name, MessageCatalog.Chinese), file])
                    .Error(text => new FileNotFoundException(text, file));
        foreach (string directory in tools.RuntimeDirectories)
            if (!Directory.Exists(directory))
                throw new Message("setup.runtime_directory_missing", [directory]).Error(text => new DirectoryNotFoundException(text));
        return tools;
    }

    /// <summary>
    /// 清掉 %TEMP%\WpeBaker 下超过 <paramref name="keepDays"/> 天的分析临时目录。
    /// 分析中途关窗口（以及以前每一次正常结束）都会留下一个 analysis-&lt;guid&gt;，从来没人删过；
    /// 启动时顺手清一次，失败一律忽略——清理不该让程序起不来。
    /// </summary>
    public static void PruneAnalysisScratch(int keepDays = 7) =>
        PruneAnalysisScratch(Path.Combine(Path.GetTempPath(), "WpeBaker"), keepDays);

    /// <summary>指定根目录的版本，供测试在自己的临时目录里验收，不动本机 %TEMP%。</summary>
    public static void PruneAnalysisScratch(string root, int keepDays)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, keepDays));
            foreach (string directory in Directory.EnumerateDirectories(root, "analysis-*"))
                try
                {
                    var info = new DirectoryInfo(directory);
                    if (info.LinkTarget is null && info.LastWriteTimeUtc < cutoff) info.Delete(recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public static string FindAssets()
    {
        if (!OperatingSystem.IsWindows()) return "";
        string running = RunningWallpaperExecutable();
        if (running.Length > 0) return Path.Combine(Path.GetDirectoryName(running)!, "assets");
        return InstalledAssets();
    }

    public static string FindWallpaperExecutable(string? assets = null)
    {
        if (!OperatingSystem.IsWindows()) return "";
        string running = RunningWallpaperExecutable();
        if (running.Length > 0) return running;
        string selected = AssetsValid(assets ?? "") ? assets! : InstalledAssets();
        if (string.IsNullOrWhiteSpace(selected)) return "";
        string folder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected)))!;
        return new[] { "wallpaper64.exe", "wallpaper32.exe" }.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists) ?? "";
    }

    public static string FindWallpaperProjectDirectory(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return "";
        string folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        return AssetsValid(Path.Combine(folder, "assets")) ? Path.Combine(folder, "projects", "myprojects") : "";
    }

    internal static bool WallpaperRunning(string executable) => OperatingSystem.IsWindows() &&
        RunningWallpaperExecutables().Any(path => path.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase));

    private static string RunningWallpaperExecutable() => RunningWallpaperExecutables().FirstOrDefault() ?? "";

    /// <summary>当前会话里正在运行的 Wallpaper Engine 安装目录；没在运行时为空串。</summary>
    internal static string RunningWallpaperDirectory()
    {
        string running = OperatingSystem.IsWindows() ? RunningWallpaperExecutable() : "";
        return running.Length == 0 ? "" : Path.GetDirectoryName(running) ?? "";
    }

    /// <summary>按注册表与 Steam 库推导出的 Wallpaper Engine 安装目录；找不到时为空串。</summary>
    internal static string InstalledWallpaperDirectory()
    {
        string assets = InstalledAssets();
        return assets.Length == 0 ? "" : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(assets))) ?? "";
    }

    private static IEnumerable<string> RunningWallpaperExecutables()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (string name in new[] { "wallpaper64", "wallpaper32" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    string? path = null;
                    try { if (process.SessionId == session) path = process.MainModule?.FileName; }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                    if (path is not null && AssetsValid(Path.Combine(Path.GetDirectoryName(path)!, "assets"))) yield return path;
                }
    }

    private static string InstalledAssets()
    {
        if (!OperatingSystem.IsWindows()) return "";
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (steam?.GetValue("SteamPath") is string path) libraries.Add(path);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var steam = machine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (steam?.GetValue("InstallPath") is string path) libraries.Add(path);
        }
        foreach (string root in libraries.ToArray())
        {
            string file = Path.Combine(root, "steamapps/libraryfolders.vdf");
            if (!File.Exists(file)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"path\"\\s+\"((?:\\\\.|[^\"])*)\""))
                libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libraries.Select(root => Path.Combine(root, "steamapps/common/wallpaper_engine/assets")).FirstOrDefault(AssetsValid) ?? "";
    }

    public static bool AssetsValid(string path) => Directory.Exists(path) && Directory.Exists(Path.Combine(path, "shaders")) &&
        Directory.Exists(Path.Combine(path, "effects"));

    private static string? DevelopmentRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src/Baker.Core/Baker.Core.csproj")) &&
                    Directory.Exists(Path.Combine(directory.FullName, ".tools/llvm-mingw-22"))) return directory.FullName;
        return null;
    }
}
