using System.Collections.Concurrent;

namespace Baker.Core;

/// <summary>
/// wpe-render 的客户端门面：能力握手（每个渲染器文件只跑一次 <c>--version</c>）、<c>render --job</c> 进程、读回 result.json。
/// job 的构造与回执核对仍在 <see cref="NativeRenderRunner"/>（对外签名不变）；进程统一走 <see cref="FfmpegTool"/>。
/// </summary>
internal sealed class RendererClient(FfmpegTool tool)
{
    // 键 = 渲染器全路径（不分大小写）+ 修改时间 + 长度：换了渲染器文件就重新握手；进程内其它时候只握一次。
    private static readonly ConcurrentDictionary<(string Path, long WriteTicks, long Length), RendererCapabilities> Handshakes = new();

    /// <summary>
    /// 渲染器能力。命中缓存时不起进程、不写日志；首次握手把 stderr 落到 <paramref name="logPath"/>。
    /// 握手失败不缓存（下次照样重试）；渲染器文件不存在时不查缓存，原样让进程启动报错。
    /// </summary>
    public async Task<RendererCapabilities> CapabilitiesAsync(string logPath, CancellationToken token)
    {
        var file = new FileInfo(Path.GetFullPath(tool.Tools.Renderer));
        (string, long, long)? key = file.Exists ? (file.FullName.ToUpperInvariant(), file.LastWriteTimeUtc.Ticks, file.Length) : null;
        if (key is { } known && Handshakes.TryGetValue(known, out RendererCapabilities? cached)) return cached;
        RendererCapabilities capabilities = RendererCapabilities.Parse(
            await tool.RunTextAsync(tool.Tools.Renderer, ["--version"], logPath, token));
        if (key is { } fresh) Handshakes[fresh] = capabilities;
        return capabilities;
    }

    /// <summary>跑完一次 <c>render --job</c>，stderr 落日志（可逐行回调进度）；退出码非 0 抛 IOException。</summary>
    public Task RenderAsync(string jobPath, string logPath, CancellationToken token, Action<string>? stderrLine = null) =>
        tool.RunTextAsync(tool.Tools.Renderer, ["render", "--job", jobPath], logPath, token, stderrLine);

    /// <summary>起一个 <c>render --job</c> 进程，由调用方读它 stdout 上的原始帧流。</summary>
    public NativeProcess StartRender(string jobPath, CancellationToken token) =>
        tool.Start(tool.Tools.Renderer, ["render", "--job", jobPath], token);

    /// <summary>读 native 目录里的 result.json（schema 1）。</summary>
    public static async Task<RenderResult> ReadResultAsync(string nativeDirectory, CancellationToken token) =>
        RenderResult.Parse(await File.ReadAllTextAsync(Path.Combine(nativeDirectory, "result.json"), token));
}
