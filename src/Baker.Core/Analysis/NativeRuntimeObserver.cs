using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 用本机渲染器做运行时观测：48 帧、固定种子、一条固定的指针输入时间线，打开场景 trace；
/// 渲染器的临时帧与音频文件用完即删（删不掉记进结果的 temporary_cleanup_errors）。经 <see cref="NativeRenderRunner"/> 调用，不改其接口。
/// </summary>
internal sealed class NativeRuntimeObserver(NativeTools tools,
    Func<RenderRequest, CancellationToken, Task<JsonObject>>? renderRaw = null, Action<string>? beforeSnapshotCopy = null)
{
    internal Task CopySnapshotAsync(ProjectSource source, string directory, string expectedHash, CancellationToken token) =>
        source.CopySnapshotAsync(directory, expectedHash, token, beforeSnapshotCopy);
    // 进程内按 (路径, 长度, 修改时间) 记住渲染器摘要：mtime 只用来判断要不要重算摘要，本身不进缓存键。
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), string> Digests = new();

    public string Identity
    {
        get
        {
            var file = new FileInfo(tools.Renderer);
            if (!file.Exists) return "missing";
            return Digests.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), key =>
            {
                using FileStream stream = File.OpenRead(key.Path);
                return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
            });
        }
    }

    public async Task<JsonObject> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ObservationKey key = request.Key;
        var events = new JsonArray(
            new JsonObject { ["frame"] = 12, ["cursor_x"] = .25, ["cursor_y"] = .25, ["cursor_in_window"] = true },
            new JsonObject { ["frame"] = 24, ["cursor_x"] = .75, ["cursor_y"] = .75, ["mouse_buttons_down"] = 1 },
            new JsonObject { ["frame"] = 36, ["mouse_buttons_down"] = 0 });
        JsonObject observed = new();
        try
        {
            using var source = new ProjectSource(request.RenderSource);
            if (!JsonNode.DeepEquals(source.ReadJson(source.SceneResource), key.Scene))
                throw new InvalidDataException("Runtime observation scene does not match its cache key.");
            var capture = new RenderRequest(request.RenderSource, key.Assets,
                request.OutputDirectory, key.Width, key.Height, key.FpsNumerator, key.FpsDenominator,
                48, Seed: 17, InputTimeline: events, UserProperties: key.Properties, DeviceUuid: request.DeviceUuid, TraceScene: true,
                GpuTiming: key.GpuTiming);
            var raw = renderRaw is null
                ? await new NativeRenderRunner(tools).RenderRawAsync(capture, cancellationToken)
                : await renderRaw(capture, cancellationToken);
            observed = raw["native_result"]!.DeepClone().AsObject();
            // Keep the Raw runner's before/after-checked identity when projecting its native trace.
            observed["observation_identity"] = new JsonObject {
                ["key_sha256"] = key.Hash(), ["source_sha256"] = raw["source_sha256"]?.DeepClone(),
                ["source_digest_scope"] = raw["source_digest_scope"]?.DeepClone() };
            key.VerifyObservation(observed, request.ExpectedSourceSha256);
            return observed;
        }
        finally
        {
            TemporaryCaptureFiles.Delete(observed, request.OutputDirectory, "native/frames.rgba", "native/frames.rgba.partial",
                "native/audio.f32le", "native/audio.f32le.partial");
        }
    }
}
