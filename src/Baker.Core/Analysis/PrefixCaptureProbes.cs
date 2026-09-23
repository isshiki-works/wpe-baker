using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 特效前缀的终端捕获点探测：渲染 1 帧，只看渲染器实际从哪个目标取帧（与 bake 的元数据探测同一个捕获选择）。
/// 同一分析里同一个终端捕获点只问一次渲染器（整层、布局冲突后两处回退都可能要前缀缓存）；成功的裁定按键落盘缓存，探测失败不缓存。
/// 探测过的全部留档，由 HybridScenePlanner 写进 plan 的 effect_prefix_capture_probes。
/// </summary>
internal sealed class PrefixCaptureProbes(NativeTools tools, HybridAnalyzeRequest request, ProjectSource source, JsonObject scene,
    JsonObject properties, string output)
{
    private readonly Dictionary<string, JsonObject> probes = new(StringComparer.Ordinal);

    /// <summary>本次分析探测过的捕获点（按首次探测的先后）。</summary>
    internal IReadOnlyCollection<JsonObject> Recorded => probes.Values;

    /// <summary>这条前缀缓存的捕获点裁定；离线 trace（开发者输入，没有渲染器可问）返回 null，由 bake 的元数据探测做同一裁定。</summary>
    internal async Task<JsonObject?> TargetAsync(JsonObject cache, CancellationToken cancellationToken)
    {
        if (request.RuntimeTraceFile is not null) return null;
        int owner = cache["owner_layer_id"]!.GetValue<int>(), terminal = cache["terminal_effect_id"]!.GetValue<int>();
        bool forceVisibleOwner = cache["preserve_external_visibility"]?.GetValue<bool>() == true;
        string key = owner.ToString(CultureInfo.InvariantCulture) + ":" + terminal.ToString(CultureInfo.InvariantCulture);
        if (forceVisibleOwner) key += ":visible-control";
        if (probes.TryGetValue(key, out JsonObject? known)) return known;
        string persistentKey = "capture-" + AnalysisCache.Key(source.SourcePath, properties, request.Assets, tools,
            File.Exists(tools.Renderer) ? File.GetLastWriteTimeUtc(tools.Renderer).Ticks : 0,
            request.FpsNumerator, request.FpsDenominator, request.DeviceUuid, key);
        if (AnalysisCache.Read(request.AnalysisCacheDirectory, persistentKey) is JsonObject cachedProbe)
            return probes[key] = cachedProbe;
        string? name = EffectPrefixCaptureTarget.LayerName(scene, owner);
        string probeOutput = Path.Combine(output, $"effect-prefix-capture-probe-{owner}-{terminal}" + (forceVisibleOwner ? "-visible" : ""));
        JsonObject observed = new(), verdict;
        try
        {
            // 与 bake 的元数据探测同一个捕获选择：原始源、快照属性、1 帧，只看渲染器实际从哪个目标取帧。
            var raw = await new NativeRenderRunner(tools).RenderRawAsync(new(source.SourcePath, request.Assets, probeOutput, 64, 64,
                request.FpsNumerator, request.FpsDenominator, 1, Seed: 17,
                CaptureTarget: new RenderCaptureSelection(owner, terminal, EffectTerminal: true, ExactExtent: false,
                    ForceVisibleOwner: forceVisibleOwner ? true : null),
                UserProperties: properties, DeviceUuid: request.DeviceUuid, TraceScene: true), cancellationToken);
            observed = raw["native_result"]!.AsObject();
            verdict = EffectPrefixCaptureTarget.Evaluate(observed, owner, terminal, name);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            verdict = EffectPrefixCaptureTarget.ProbeFailed(owner, terminal, name, error.Message);
        }
        finally
        {
            TemporaryCaptureFiles.Delete(observed, probeOutput, "native/frames.rgba", "native/frames.rgba.partial",
                "native/audio.f32le", "native/audio.f32le.partial");
        }
        verdict["probe_output"] = probeOutput;
        probes[key] = verdict;
        if (verdict["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.ProbeFailedStatus)
            AnalysisCache.Write(request.AnalysisCacheDirectory, persistentKey, verdict);
        return verdict;
    }
}
