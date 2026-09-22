using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 把"用户选中/拖入的这个路径为什么不能烘"判成一句人话。GUI 的导入与 CLI 的 analyze 共用这一份，
/// 免得同一个壁纸在两处得到两种说法（以前 GUI 只会说 "Only Scene wallpapers can be imported."）。
/// <para>
/// 只读判定：不写任何文件，不启动任何工具。返回 null 表示这是一份可以继续分析的 Scene 来源。
/// </para>
/// </summary>
public static class SourceDiagnosis
{
    /// <summary>预设包：本身不含场景，退出码与其它拒绝理由不同（CLI 用 3）。</summary>
    public const string PresetKind = "preset";

    /// <summary>
    /// 一条拒绝理由。<see cref="Message"/> 是这句话的键与参数（异常带着它走），<see cref="Text"/> 给出指定语言的那一句。
    /// </summary>
    public sealed record Rejection(string Kind, string Key, object?[] EnglishArgs, object?[] ChineseArgs)
    {
        /// <summary>project.json 里的 dependency（只有预设包有），用于指路到它依赖的壁纸。</summary>
        public string? Dependency { get; init; }

        public Message Message => new(Key, EnglishArgs, ChineseArgs);

        /// <summary>按语言取这一句。</summary>
        public string Text(string language) => Message.In(language);
    }

    /// <summary>这个路径看上去像不像一个壁纸来源。只看路径形状，不打开文件，供拖放高亮之类的快速判断用。</summary>
    public static bool LooksLikeSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (File.Exists(path))
                return Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
            return Directory.Exists(path) && EntryNames.Any(name => File.Exists(Path.Combine(path, name)));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return false; }
    }

    private static readonly string[] EntryNames = ["project.json", "scene.pkg", "scene.json"];

    /// <summary>
    /// 判定这个来源能不能分析。返回 null 表示可以；否则返回一条带分类的拒绝理由。
    /// <paramref name="resolved"/> 是规范化之后真正应该交给分析的路径（例如把只存在 .pkg 的 scene.json 换成 scene.pkg）。
    /// </summary>
    public static Rejection? Inspect(string path, out string resolved)
    {
        resolved = path ?? "";
        if (string.IsNullOrWhiteSpace(resolved))
            return new Rejection("missing", "source.path_missing", [""], [""]);
        try { resolved = Path.GetFullPath(resolved); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        { return new Rejection("missing", "source.path_missing", [path], [path]); }

        // 用户手里的常常是 scene.json，而作品里只放了 scene.pkg（渲染器本来就是包优先）；两边都认。
        if (!File.Exists(resolved) && !Directory.Exists(resolved) &&
            Path.GetExtension(resolved).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.ChangeExtension(resolved, ".pkg"))) resolved = Path.ChangeExtension(resolved, ".pkg");

        string candidate = resolved;
        if (Directory.Exists(candidate))
        {
            if (!EntryNames.Any(name => File.Exists(Path.Combine(candidate, name))))
                return new Rejection("empty_folder", "source.folder_not_wallpaper", [candidate], [candidate]);
        }
        else if (!File.Exists(resolved))
            return new Rejection("missing", "source.path_missing", [resolved], [resolved]);
        else if (!Path.GetExtension(resolved).Equals(".pkg", StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetExtension(resolved).Equals(".json", StringComparison.OrdinalIgnoreCase))
            return new Rejection("not_wallpaper_file", "source.file_not_wallpaper",
                [Path.GetFileName(resolved)], [Path.GetFileName(resolved)]);

        try
        {
            using var source = new ProjectSource(resolved);
            if (source.Kind != "scene") return ClassifyProject(source);
            if (source.ReadJson(source.SceneResource)["objects"] is not JsonArray)
                return new Rejection("no_objects", "source.scene_without_objects", [], []);
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Rejection("unreadable", "source.unreadable", [error.Message], [error.Message]);
        }
    }

    /// <summary>不需要规范化路径时的简写。</summary>
    public static Rejection? Inspect(string path) => Inspect(path, out _);

    /// <summary>project.json 说这不是 Scene 时，先分清是视频、网页还是预设包，再给对应的那句话。</summary>
    private static Rejection ClassifyProject(ProjectSource source)
    {
        JsonObject project = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        string? Text(string key) => project[key] is JsonValue value && value.TryGetValue<string>(out string? text) &&
            !string.IsNullOrWhiteSpace(text) ? text : null;
        string? dependency = Text("dependency") ?? (project["dependency"] is JsonValue id && id.TryGetValue<long>(out long number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        string? type = Text("type"), file = Text("file");
        // 预设包：没有 file、没有 type，只有 dependency。它不是壁纸，指路到它依赖的那一张。
        if (type is null && file is null && dependency is not null)
            return new Rejection(PresetKind, "cli.preset_package", [dependency], [dependency]) { Dependency = dependency };
        string kind = type?.ToLowerInvariant() ?? "";
        if (kind is "video" or "web")
        {
            object?[] english = [type, file is null ? "" : MessageCatalog.Get("source.content_clause", MessageCatalog.English, file)];
            object?[] chinese = [type, file is null ? "" : MessageCatalog.Get("source.content_clause", MessageCatalog.Chinese, file)];
            return new Rejection(kind, kind == "video" ? "source.video_wallpaper" : "source.web_wallpaper", english, chinese);
        }
        return new Rejection("other_type", "source.not_scene_project", [type ?? "absent"], [type ?? "缺失"]);
    }
}
