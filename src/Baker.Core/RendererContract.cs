using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 发给 wpe-render 的 job（schema 1）。属性声明顺序就是 JSON 字段顺序；按 <see cref="NativeRenderRunner"/> 的
/// snake_case、null 不写选项序列化，写出与原来逐字段拼 JsonObject 的 renderer-job.json 逐字节相同。
/// 可空字段为 null 即不写；只在为真时才写的开关用 <c>bool?</c>（假时给 null）。
/// </summary>
internal sealed record RenderJob
{
    public int SchemaVersion { get; init; } = 1;
    public required string Source { get; init; }
    public required string Assets { get; init; }
    public required string OutputDir { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint FpsNum { get; init; }
    public uint FpsDen { get; init; }
    public ulong Frames { get; init; }
    public ulong WarmupFrames { get; init; }
    public ulong Seed { get; init; }
    public bool RawStdout { get; init; }
    public bool? WriteAudio { get; init; }
    public RenderCaptureSelection? CaptureTarget { get; init; }
    public OrthographicCaptureViewport? OrthographicCaptureViewport { get; init; }
    public RenderLayerSelection? LayerSelection { get; init; }
    public JsonObject? Input { get; init; }
    public JsonArray? InputTimeline { get; init; }
    public JsonObject? UserProperties { get; init; }
    public JsonArray? OfflineVideoRateOverrides { get; init; }
    public string? DeviceUuid { get; init; }
    public bool? GpuTiming { get; init; }
    public bool? TraceScene { get; init; }
    public double? EffectRenderScale { get; init; }
    public bool? MatchEffectResolution { get; init; }
    public uint? OutputFrameStride { get; init; }
    public ulong? OutputFramePhase { get; init; }
    public uint? OutputSampleWidth { get; init; }
    public uint? OutputSampleHeight { get; init; }
    public bool? CollectSamplingCoverage { get; init; }
    public RenderGpuEncodeJob? GpuEncode { get; init; }

    /// <summary>整帧渲染与原始帧渲染共用的字段。JsonObject 类输入直接引用请求里的节点：记录只被序列化、不挂父节点，不必深拷贝。</summary>
    public static RenderJob From(RenderRequest request, string source, string outputDir, bool rawStdout) => new()
    {
        Source = source, Assets = Path.GetFullPath(request.Assets), OutputDir = outputDir,
        Width = request.Width, Height = request.Height, FpsNum = request.FpsNumerator, FpsDen = request.FpsDenominator,
        Frames = request.Frames, WarmupFrames = request.WarmupFrames, Seed = request.Seed, RawStdout = rawStdout,
        CaptureTarget = request.CaptureTarget, OrthographicCaptureViewport = request.OrthographicCaptureViewport,
        LayerSelection = request.LayerSelection, Input = request.Input, InputTimeline = request.InputTimeline,
        UserProperties = request.UserProperties, OfflineVideoRateOverrides = request.OfflineVideoRateOverrides, DeviceUuid = request.DeviceUuid,
        GpuTiming = request.GpuTiming ? true : null, TraceScene = request.TraceScene ? true : null
    };
}

/// <summary>job.gpu_encode：同卡编码参数。字段全部写出（crop 缺省为整幅），resize_* 只在请求了编码尺寸时写。</summary>
internal sealed record RenderGpuEncodeJob(string Codec, int Qp, bool PackedAlpha, ulong EncodedFrames, bool CollectBounds,
    bool BoundsIncludeRgb, ulong[] RetainFrames, uint CrossfadeFrames, bool RetainLoopWindow, int CropX, int CropY,
    int CropWidth, int CropHeight, uint? ResizeWidth, uint? ResizeHeight);

/// <summary>
/// wpe-render 的 result.json（schema 1）里本仓库实际读取的字段。没列出的字段反序列化时忽略、不报错；
/// 原文整份留在 <see cref="Json"/>，照旧写进渲染 manifest 的 native_result。要原样转写进 manifest 的子对象保留 JsonObject。
/// </summary>
internal sealed record RenderResult
{
    public string? Status { get; init; }
    public ulong? WrittenFrames { get; init; }
    public ulong? RendererErrorCount { get; init; }
    public string? DeviceUuid { get; init; }
    public RenderResultCaptureSource? CaptureSource { get; init; }
    public JsonNode? OrthographicCaptureViewport { get; init; }
    public JsonNode? LayerSelection { get; init; }
    public JsonArray? RuntimeVideoRateOverrides { get; init; }
    public JsonArray? RuntimeLayers { get; init; }
    public JsonArray? RuntimeDependencies { get; init; }
    public double? EffectRenderScale { get; init; }
    public bool? MatchEffectResolution { get; init; }
    public JsonObject? SamplingCoverage { get; init; }
    public uint? ReadbackWidth { get; init; }
    public uint? ReadbackHeight { get; init; }
    public bool? GpuSampled { get; init; }
    public uint? OutputFrameStride { get; init; }
    public ulong? OutputFramePhase { get; init; }
    public bool? GpuEncoded { get; init; }
    public ulong? ReadbackFrames { get; init; }
    public string? GpuEncoder { get; init; }
    public bool? GpuPackedAlpha { get; init; }
    public RenderResultGpuCapture? GpuCapture { get; init; }
    /// <summary>result.json 原文。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public JsonObject Json { get; private init; } = [];

    /// <summary>帧序列回执：状态 complete、写出帧数等于预期、渲染器错误数为 0。</summary>
    public bool Confirms(ulong frames) => Status == "complete" && WrittenFrames == frames && RendererErrorCount == 0;

    public static RenderResult Parse(string text)
    {
        JsonObject json = JsonNode.Parse(text)!.AsObject();
        return json.Deserialize<RenderResult>(Options)! with { Json = json };
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

internal sealed record RenderResultCaptureSource(string? RenderTarget);

/// <summary>result.gpu_capture：各子对象会带上路径等补充字段转写进 manifest，所以保留 JsonObject。</summary>
internal sealed record RenderResultGpuCapture(JsonObject? Resize, JsonNode? Crop, JsonObject? LoopCrossfade, JsonObject? LoopWindow,
    JsonObject? AlphaBounds, JsonObject? RetainedFrames, ulong? EncodedPackets);
