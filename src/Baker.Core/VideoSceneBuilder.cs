using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 缩进 JSON 写盘（新建文件，已存在即失败）。视频图层的写出已搬到 <see cref="ProjectWriter"/>；
/// 这个助手被分析与烘焙多处共用，名字是历史遗留，C3 挪到 JSON 工具处。
/// </summary>
public static class VideoSceneBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task WriteJsonAsync(string path, JsonNode value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, value, JsonOptions, cancellationToken);
    }
}
