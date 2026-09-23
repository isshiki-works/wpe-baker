using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 分析侧向渲染器要运行时观测（真实脚本输入、对象访问、场景层级）的唯一出口。
/// 真实渲染由 <see cref="NativeRuntimeObserver"/> 实现；C3 的录制/回放各加一个实现，按 <see cref="ObservationRequest.Key"/> 落盘或查找，调用方不变。
/// </summary>
internal interface IRuntimeObserver
{
    /// <summary>观测器身份，进缓存键：真实渲染器是其二进制内容摘要，换了渲染器就必须重观测。</summary>
    string Identity { get; }

    /// <summary>渲染 <paramref name="request"/> 并返回渲染器的 native_result；临时帧与音频文件由实现自己清掉。</summary>
    Task<JsonObject> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken);
}

/// <summary>一次观测：<see cref="Key"/> 是决定结果的输入，其余是在哪渲染、用哪块设备这类不进键的执行参数。</summary>
/// <param name="RenderSource">实际交给渲染器的工程目录（原作，或本次分析写出的状态/音频取舍副本）。</param>
internal sealed record ObservationRequest(ObservationKey Key, string RenderSource, string OutputDirectory, string? DeviceUuid);

/// <summary>
/// 观测缓存键：只含决定观测结果的输入。源按内容摘要、场景按实际观测的 JSON 进键；
/// 不含 NativeTools 路径、渲染器 mtime、输出目录与缓存目录。设备只在要 GPU 计时时进键（计时读数随设备变，依赖 trace 不随设备变）。
/// </summary>
internal sealed record ObservationKey(string SourceSha256, JsonObject Scene, JsonObject Properties, string Assets,
    uint Width, uint Height, uint FpsNumerator, uint FpsDenominator, bool GpuTiming, string? TimingDevice, string Renderer)
{
    internal string Hash() => AnalysisCache.Key(this);
}
