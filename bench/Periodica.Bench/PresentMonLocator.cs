namespace Periodica.Bench;

/// <summary>
/// 找本机已有的 PresentMon：请求或命令行没给路径时，从程序目录与当前目录逐级向上找 <c>.tools/presentmon/</c>。
/// 工具从不下载 PresentMon；版本与哈希记录在 bench/presentmon/source.json。
/// </summary>
internal static class PresentMonLocator
{
    internal const string Missing =
        "PresentMon was not found under .tools/presentmon/; pass present_mon_path (sample) or --present-mon (abba).";

    private static readonly string[] Names = ["PresentMon-2.5.1-x64.exe", "PresentMon.exe"];

    internal static string? Find() =>
        Search(AppContext.BaseDirectory) ?? Search(Environment.CurrentDirectory);

    private static string? Search(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            foreach (string name in Names)
            {
                string candidate = Path.Combine(directory.FullName, ".tools", "presentmon", name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
