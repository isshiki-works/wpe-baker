using System.IO;
using System.Text.Json.Nodes;
using Baker.Core;

namespace Baker.App;

internal static class AppEnvironment
{
    /// <summary>界面当前语言，由 MainWindow.SetLanguage 同步；导入与自检的报错按它出中文或英文。</summary>
    public static string Language { get; set; } = MessageCatalog.DefaultLanguage();

    /// <summary>
    /// 自检随包工具。Core 抛的异常消息是英文原文（机器可读字段沿用英文），异常带着文案的键与参数，
    /// 这里在显示给用户之前按界面语言重新渲染。
    /// </summary>
    public static NativeTools FindTools()
    {
        try { return NativeEnvironment.FindTools(); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            throw new InvalidDataException(Message.Of(error)?.In(Language) ?? error.Message, error);
        }
    }

    public static string FindAssets() => NativeEnvironment.FindAssets();

    public static bool AssetsValid(string path) => NativeEnvironment.AssetsValid(path);

    public static string FindWallpaperExecutable(string? assets = null) => NativeEnvironment.FindWallpaperExecutable(assets);

    public static bool SourceExists(string path) => (File.Exists(path) &&
        (Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))) ||
        (Directory.Exists(path) && new[] { "project.json", "scene.pkg", "scene.json" }.Any(name => File.Exists(Path.Combine(path, name))));

    /// <summary>
    /// 拖放接受的范围：只要是本机上存在的一个路径就收下。以前只收"看着像壁纸"的，拖进别的东西时
    /// 鼠标显示禁止、松手什么也不发生，用户完全不知道为什么——现在一律收下，由导入给出一句理由。
    /// </summary>
    public static bool DropTarget(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return false; }
    }

    /// <summary>
    /// 判定并规范化来源路径。拒绝理由与 CLI 共用 <see cref="SourceDiagnosis"/>：视频壁纸、网页壁纸、
    /// 预设包、空文件夹各有各的那一句，而不是一律"Only Scene wallpapers can be imported."。
    /// </summary>
    public static string ValidateSource(string path)
    {
        if (SourceDiagnosis.Inspect(path, out string resolved) is { } rejection)
            throw new InvalidDataException(rejection.Text(Language));
        return resolved;
    }

    public static bool OutputValid(string output, string source)
    {
        try
        {
            if (!Path.IsPathFullyQualified(output) || File.Exists(output)) return false;
            string root = Path.GetFullPath(Directory.Exists(source) ? source : Path.GetDirectoryName(source) ?? "").TrimEnd(Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar);
            return !target.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    public static bool TryFrameRate(string text, out uint numerator, out uint denominator)
    {
        string[] parts = text.Trim().Split('/');
        numerator = 0; denominator = 1;
        return parts.Length is 1 or 2 && uint.TryParse(parts[0].Trim(), out numerator) && numerator > 0 &&
            (parts.Length == 1 || (uint.TryParse(parts[1].Trim(), out denominator) && denominator > 0)) &&
            (double)numerator / denominator is >= 1 and <= 1000;
    }

    public static string NewOutput(string root, string source)
    {
        string name = Path.GetFileName(Directory.Exists(source) ? source.TrimEnd(Path.DirectorySeparatorChar) : Path.GetDirectoryName(source)) ?? "Wallpaper";
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Path.GetFullPath(root), $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
    }

    /// <summary>工作目录的固定名字：放在保存位置下面，与成品文件夹并排，一眼看得出是中间产物。</summary>
    public const string WorkFolderName = "wpe-baker-work";

    /// <summary>
    /// 一次生成的工作目录。中间产物（捕获副本、各组无损 master）峰值可达几十 GB，所以默认放在
    /// <paramref name="outputRoot"/>（用户选的保存位置）所在的盘上，而不是系统盘；
    /// 那个盘建不了目录时才回退到 %LOCALAPPDATA%。
    /// </summary>
    public static string NewWorkDirectory(string source, string? outputRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(outputRoot))
            try
            {
                string root = Path.Combine(Path.GetFullPath(outputRoot), WorkFolderName);
                Directory.CreateDirectory(root);
                return NewOutput(root, source);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return NewOutput(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpeBaker", "work"), source);
    }
}
