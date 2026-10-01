using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>一次观测：<see cref="Key"/> 是决定结果的输入，其余是在哪渲染、用哪块设备这类不进键的执行参数。</summary>
/// <param name="RenderSource">实际交给渲染器的工程目录（原作，或本次分析写出的状态/音频取舍副本）。</param>
/// <param name="ExpectedSourceSha256">本次捕获应当读到的工程摘要；派生副本准备完后确定，与原作摘要可不同。</param>
internal sealed record ObservationRequest(ObservationKey Key, string RenderSource, string OutputDirectory, string? DeviceUuid,
    string ExpectedSourceSha256);

/// <summary>
/// 观测缓存键：只含决定观测结果的输入。源按内容摘要、场景按实际观测的 JSON 进键；
/// 不含 NativeTools 路径、渲染器 mtime、输出目录与缓存目录。设备只在要 GPU 计时时进键（计时读数随设备变，依赖 trace 不随设备变）。
/// </summary>
internal sealed record ObservationKey(string SourceSha256, JsonObject Scene, JsonObject Properties, string Assets,
    uint Width, uint Height, uint FpsNumerator, uint FpsDenominator, bool GpuTiming, string? TimingDevice, string Renderer)
{
    // Earlier runtime caches discarded the raw capture's source identity; they cannot be reused.
    internal string Hash() => AnalysisCache.Key("runtime-observation-source-snapshot-v2", this);

    internal void VerifyObservation(JsonObject trace, string? expectedSourceSha256 = null)
    {
        if (trace["observation_identity"] is not JsonObject identity ||
            !JsonNode.DeepEquals(identity["key_sha256"], JsonValue.Create(Hash())) ||
            !JsonNode.DeepEquals(identity["source_digest_scope"], JsonValue.Create(ProjectSource.DigestScope)) ||
            identity["source_sha256"] is not JsonValue hashValue ||
            !hashValue.TryGetValue<string>(out string? hash) || string.IsNullOrWhiteSpace(hash) ||
            expectedSourceSha256 is not null && hash != expectedSourceSha256)
            throw new InvalidDataException("Runtime observation does not match its expected source identity.");
    }
}
