using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 用本机渲染器做运行时观测：48 帧、固定种子、一条固定的指针输入时间线，打开场景 trace；
/// 渲染器的临时帧与音频文件用完即删（删不掉记进结果的 temporary_cleanup_errors）。经 <see cref="NativeRenderRunner"/> 调用，不改其接口。
/// </summary>
internal sealed class NativeRuntimeObserver(NativeTools tools) : IRuntimeObserver
{
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
            var raw = await new NativeRenderRunner(tools).RenderRawAsync(new(request.RenderSource, key.Assets,
                request.OutputDirectory, key.Width, key.Height, key.FpsNumerator, key.FpsDenominator,
                48, Seed: 17, InputTimeline: events, UserProperties: key.Properties, DeviceUuid: request.DeviceUuid, TraceScene: true,
                GpuTiming: key.GpuTiming), cancellationToken);
            observed = raw["native_result"]!.DeepClone().AsObject();
            return observed;
        }
        finally
        {
            TemporaryCaptureFiles.Delete(observed, request.OutputDirectory, "native/frames.rgba", "native/frames.rgba.partial",
                "native/audio.f32le", "native/audio.f32le.partial");
        }
    }
}
