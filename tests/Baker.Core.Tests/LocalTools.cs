using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// L3 测试用的本机工具与仓库内夹具位置，不再按当前目录硬编码 build/、dist/。
/// tools.json 取环境变量 WPE_BAKER_TOOLS_JSON；没设就从测试程序目录向上找仓库根的 tools.json。
/// 格式与产品的 tools.json 相同，相对路径按该文件所在目录解析；另可选 reference_renderer（旧版 wpe-render.exe，稀疏读回对照用）。
/// </summary>
internal static class LocalTools
{
    /// <summary>仓库根：从测试程序目录向上第一个含 tests/fixtures/native 的目录。</summary>
    internal static readonly string RepositoryRoot = Ancestors().FirstOrDefault(directory =>
        Directory.Exists(Path.Combine(directory, "tests", "fixtures", "native"))) ?? Environment.CurrentDirectory;

    internal static readonly string Missing;
    internal static readonly NativeTools? Tools;
    internal static readonly string? ReferenceRenderer;

    static LocalTools()
    {
        string configuration = Environment.GetEnvironmentVariable("WPE_BAKER_TOOLS_JSON") is { Length: > 0 } configured
            ? configured : Path.Combine(RepositoryRoot, "tools.json");
        Missing = "缺 tools.json：" + configuration + "（用 WPE_BAKER_TOOLS_JSON 指定）";
        if (!File.Exists(configuration)) return;
        JsonObject json = JsonNode.Parse(File.ReadAllText(configuration))!.AsObject();
        string directory = Path.GetDirectoryName(Path.GetFullPath(configuration))!;
        string? Resolve(string key) => json[key]?.GetValue<string>() is { } path ? Path.GetFullPath(path, directory) : null;
        var tools = new NativeTools(Resolve("renderer") ?? "", Resolve("ffmpeg") ?? "", Resolve("ffprobe") ?? "",
            (json["runtime_directories"]?.AsArray() ?? []).Select(item => Path.GetFullPath(item!.GetValue<string>(), directory)).ToArray());
        string? absent = new[] { tools.Renderer, tools.Ffmpeg, tools.Ffprobe }.FirstOrDefault(file => !File.Exists(file));
        if (absent is not null) { Missing = configuration + " 指向的工具不存在：" + absent; return; }
        Tools = tools;
        ReferenceRenderer = Resolve("reference_renderer") is { } reference && File.Exists(reference) ? reference : null;
    }

    private static IEnumerable<string> Ancestors()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            yield return directory.FullName;
    }
}
