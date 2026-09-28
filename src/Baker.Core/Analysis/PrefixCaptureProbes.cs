using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 特效前缀的终端捕获点探测：渲染 1 帧，只看渲染器实际从哪个目标取帧（与 bake 的元数据探测同一个捕获选择）。
/// 同一分析里同一个终端捕获点只问一次渲染器（整层、布局冲突后两处回退都可能要前缀缓存）；成功的裁定按键落盘缓存，探测失败不缓存。
/// 探测过的全部留档，由 HybridScenePlanner 写进 plan 的 effect_prefix_capture_probes。
/// </summary>
internal sealed class PrefixCaptureProbes(NativeTools tools, HybridAnalyzeRequest request, ProjectSource source, JsonObject scene,
    JsonObject properties, string output, AnalysisMemo? memo = null)
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
        // 同一次 analyze 里探到的捕获点记下来（退回并行时同一个键也只起一个渲染器）；探测失败的不记，与磁盘缓存同一口径，下次照常重探。
        return probes[key] = memo is null ? await ProbeAsync() : await memo.JsonAsync(persistentKey, ProbeAsync,
            verdict => verdict["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.ProbeFailedStatus);
        async Task<JsonObject> ProbeAsync()
        {
            if (AnalysisCache.Read(request.AnalysisCacheDirectory, persistentKey) is JsonObject cachedProbe) return cachedProbe;
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
            if (verdict["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.ProbeFailedStatus)
                AnalysisCache.Write(request.AnalysisCacheDirectory, persistentKey, verdict);
            return verdict;
        }
    }

    /// <summary>完整区间复核通过的状态；其余（晚到依赖、探测失败）都不采用这条前缀。</summary>
    internal const string CompleteCapturePassedStatus = "complete_capture_passed";

    /// <summary>
    /// 前缀的完整区间复核：按烘焙同一份捕获副本（<see cref="EffectPrefixBakeService.PrepareCaptureSourceAsync"/>）与同一个终端捕获点，
    /// 从第 0 帧渲满 P+1 帧（逐帧模拟、只留稀疏样本，尺寸不影响依赖记录），再用烘焙复核的同一判据
    /// （<see cref="EffectPrefixBakeService.SurvivesCompleteCapture"/>）判这条前缀。短观测看不到的晚到依赖（计时、回调里才取层、写层）在这里现形，
    /// 分析就不判"能"、交给短一级前缀或整层实时，不再拖到烘焙才被拒。<paramref name="analyzedRuntime"/> 是写进 runtime.json 的那份观测，
    /// 只用来列出完整区间里新出现的依赖。离线 trace 没有渲染器可问，返回 null。
    /// </summary>
    internal async Task<JsonObject?> CompleteCaptureAsync(JsonObject cache, JsonObject analyzedRuntime, JsonObject projection,
        CancellationToken cancellationToken)
    {
        if (request.RuntimeTraceFile is not null) return null;
        int owner = cache["owner_layer_id"]!.GetValue<int>(), terminal = cache["terminal_effect_id"]!.GetValue<int>();
        int prefix = cache["prefix_effect_count"]!.GetValue<int>();
        JsonObject loop = cache["loop"]!.AsObject();
        ulong frames = loop["candidates"]!.AsArray()[0]!["frames"]!.GetValue<ulong>();
        bool forceVisibleOwner = cache["preserve_external_visibility"]?.GetValue<bool>() == true;
        string key = $"{owner}:{terminal}:{prefix}:{frames}:" + AnalysisCache.Key(loop) + (forceVisibleOwner ? ":visible-control" : "");
        if (probes.TryGetValue("complete:" + key, out JsonObject? known)) return known;
        string persistentKey = "complete-capture-" + AnalysisCache.Key(source.SourcePath, properties, request.Assets, tools,
            File.Exists(tools.Renderer) ? File.GetLastWriteTimeUtc(tools.Renderer).Ticks : 0,
            request.FpsNumerator, request.FpsDenominator, request.DeviceUuid, key);
        return probes["complete:" + key] = memo is null ? await ProbeAsync() : await memo.JsonAsync(persistentKey, ProbeAsync,
            verdict => verdict["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.ProbeFailedStatus);
        async Task<JsonObject> ProbeAsync()
        {
            if (AnalysisCache.Read(request.AnalysisCacheDirectory, persistentKey) is JsonObject cachedProbe) return cachedProbe;
            string probeOutput = Path.Combine(output, $"effect-prefix-complete-probe-{owner}-{terminal}-{prefix}-{frames}");
            string captureProject = Path.Combine(probeOutput, "capture-source");
            var verdict = new JsonObject { ["kind"] = "complete_capture", ["owner_layer_id"] = owner, ["terminal_effect_id"] = terminal,
                ["prefix_effect_count"] = prefix, ["frames"] = frames, ["probe_output"] = probeOutput };
            try
            {
                // 烘焙读的是源里的原场景（不带音频效果取舍），复核也按它，结论才与烘焙一致。
                JsonObject pristine = source.ReadJson(source.SceneResource);
                await EffectPrefixBakeService.PrepareCaptureSourceAsync(captureProject, source, request.Assets, pristine, properties, loop, cancellationToken);
                var raw = await new NativeRenderRunner(tools).RenderAsync(new(captureProject, request.Assets, Path.Combine(probeOutput, "render"), 64, 64,
                    request.FpsNumerator, request.FpsDenominator, checked(frames + 1), Seed: 17,
                    CaptureTarget: new RenderCaptureSelection(owner, terminal, EffectTerminal: true, ExactExtent: false,
                        ForceVisibleOwner: forceVisibleOwner ? true : null),
                    UserProperties: properties, DeviceUuid: request.DeviceUuid, TraceScene: true,
                    FrameSamplesOnly: true, FrameSampleStride: checked((uint)frames), FrameSampleWidth: 1,
                    OfflineVideoRateOverrides: HybridBakeService.SelectVideoRateOverrides(loop, new HashSet<int> { owner })), null, cancellationToken);
                JsonObject fullRuntime = raw["native_result"]?.AsObject()
                    ?? throw new InvalidDataException("The complete prefix probe omitted its runtime evidence.");
                if (EffectPrefixBakeService.SurvivesCompleteCapture(pristine, source, request, fullRuntime, properties, projection, owner, prefix))
                    verdict["status"] = CompleteCapturePassedStatus;
                else
                {
                    verdict["status"] = "rejected_late_dependency";
                    verdict["late_dependencies"] = EffectPrefixBakeService.LateDependencyRejection(owner, frames, loop, fullRuntime, analyzedRuntime)["late_dependencies"]!.DeepClone();
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                verdict["status"] = EffectPrefixCaptureTarget.ProbeFailedStatus;
                verdict["error"] = error.Message;
            }
            finally
            {
                // 捕获副本是整份源的解包，只为这一次渲染存在。
                if (Directory.Exists(captureProject)) Directory.Delete(captureProject, recursive: true);
            }
            if (verdict["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.ProbeFailedStatus)
                AnalysisCache.Write(request.AnalysisCacheDirectory, persistentKey, verdict);
            return verdict;
        }
    }
}
