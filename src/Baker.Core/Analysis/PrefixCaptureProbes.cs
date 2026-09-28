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

    private static (int? Id, int? Ordinal, string Key) Terminal(JsonObject cache)
    {
        int? id = SceneGraph.Int(cache["terminal_effect_id"]), ordinal = SceneGraph.Int(cache["terminal_effect_ordinal"]);
        if (id is null && ordinal is null) throw new InvalidDataException("Effect-prefix terminal needs an authored ID or ordinal.");
        return (id, ordinal, id?.ToString(CultureInfo.InvariantCulture) ?? "ordinal-" + ordinal!.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>本次分析探测过的捕获点（按首次探测的先后）。</summary>
    internal IReadOnlyCollection<JsonObject> Recorded => probes.Values;

    /// <summary>这条前缀缓存的捕获点裁定；离线 trace（开发者输入，没有渲染器可问）返回 null，由 bake 的元数据探测做同一裁定。</summary>
    internal async Task<JsonObject?> TargetAsync(JsonObject cache, CancellationToken cancellationToken)
    {
        if (request.RuntimeTraceFile is not null) return null;
        int owner = cache["owner_layer_id"]!.GetValue<int>();
        var terminal = Terminal(cache);
        bool forceVisibleOwner = cache["preserve_external_visibility"]?.GetValue<bool>() == true;
        string key = owner.ToString(CultureInfo.InvariantCulture) + ":" + terminal.Key;
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
            string probeOutput = Path.Combine(output, $"effect-prefix-capture-probe-{owner}-{terminal.Key}" + (forceVisibleOwner ? "-visible" : ""));
            JsonObject observed = new(), verdict;
            try
            {
                // 与 bake 的元数据探测同一个捕获选择：原始源、快照属性、1 帧，只看渲染器实际从哪个目标取帧。
                var raw = await new NativeRenderRunner(tools).RenderRawAsync(new(source.SourcePath, request.Assets, probeOutput, 64, 64,
                    request.FpsNumerator, request.FpsDenominator, 1, Seed: 17,
                    CaptureTarget: new RenderCaptureSelection(owner, terminal.Id, EffectOrdinal: terminal.Ordinal,
                        EffectTerminal: true, ExactExtent: false,
                        ForceVisibleOwner: forceVisibleOwner ? true : null),
                    UserProperties: properties, DeviceUuid: request.DeviceUuid, TraceScene: true), cancellationToken);
                observed = raw["native_result"]!.AsObject();
                verdict = EffectPrefixCaptureTarget.Evaluate(observed, owner, terminal.Id, name, terminal.Ordinal);
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                verdict = EffectPrefixCaptureTarget.ProbeFailed(owner, terminal.Id, name, error.Message, terminal.Ordinal);
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

    /// <summary>完整区间复核通过的状态；晚到依赖、改频证据变化或探测失败都不采用这条前缀。</summary>
    internal const string CompleteCapturePassedStatus = "complete_capture_passed";

    /// <summary>
    /// 前缀的完整区间复核：按烘焙同一份捕获副本（<see cref="EffectPrefixBakeService.PrepareCaptureSourceAsync"/>）与同一个终端捕获点，
    /// 从第 0 帧渲满 P+1 帧（逐帧模拟、只留稀疏样本，尺寸不影响依赖记录），再用烘焙复核的同一判据
    /// （<see cref="EffectPrefixBakeService.SurvivesCompleteCapture"/>）判这条前缀。短观测看不到的晚到依赖或全区间改频缺口在这里现形，
    /// 分析就不判"能"、交给短一级前缀或整层实时，不再拖到烘焙才被拒。<paramref name="analyzedRuntime"/> 是写进 runtime.json 的那份观测，
    /// 只用来列出完整区间里新出现的依赖。离线 trace 没有渲染器可问，返回 null。
    /// </summary>
    internal async Task<JsonObject?> CompleteCaptureAsync(JsonObject cache, JsonObject analyzedRuntime, JsonObject projection,
        CancellationToken cancellationToken)
    {
        if (request.RuntimeTraceFile is not null) return null;
        int owner = cache["owner_layer_id"]!.GetValue<int>();
        var terminal = Terminal(cache);
        int prefix = cache["prefix_effect_count"]!.GetValue<int>();
        JsonObject loop = cache["loop"]!.AsObject();
        ulong frames = loop["candidates"]!.AsArray()[0]!["frames"]!.GetValue<ulong>();
        bool forceVisibleOwner = cache["preserve_external_visibility"]?.GetValue<bool>() == true;
        string key = $"{owner}:{terminal.Key}:{prefix}:{frames}:" + AnalysisCache.Key(loop) + (forceVisibleOwner ? ":visible-control" : "");
        if (probes.TryGetValue("complete:" + key, out JsonObject? known)) return known;
        string persistentKey = "complete-capture-" + AnalysisCache.Key(source.SourcePath, properties, request.Assets, tools,
            File.Exists(tools.Renderer) ? File.GetLastWriteTimeUtc(tools.Renderer).Ticks : 0,
            request.FpsNumerator, request.FpsDenominator, request.DeviceUuid, key);
        return probes["complete:" + key] = memo is null ? await ProbeAsync() : await memo.JsonAsync(persistentKey, ProbeAsync,
            verdict => verdict["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.ProbeFailedStatus);
        async Task<JsonObject> ProbeAsync()
        {
            if (AnalysisCache.Read(request.AnalysisCacheDirectory, persistentKey) is JsonObject cachedProbe)
            {
                // Older reports called changed loop eligibility a "late dependency" even with no new edge.
                // The complete runtime is retained, so reclassify without replaying P+1 frames.
                if (cachedProbe["status"]?.GetValue<string>() == "rejected_late_dependency" &&
                    cachedProbe["late_dependencies"] is JsonArray { Count: 0 })
                {
                    string? folder = cachedProbe["probe_output"]?.GetValue<string>();
                    string manifest = Path.Combine(folder ?? "", "render", "manifest.json");
                    if (File.Exists(manifest))
                    {
                        // Recreate the exact patched source before trusting the retained full-runtime trace.
                        // The old report omitted the installed shader-key provenance needed to interpret it.
                        string recheck = Path.Combine(output, "effect-prefix-recheck-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            JsonObject pristine = source.ReadJson(source.SceneResource);
                            JsonArray installed = await EffectPrefixBakeService.PrepareCaptureSourceAsync(recheck,
                                source, request.Assets, pristine, properties, loop, cancellationToken);
                            using var prepared = new ProjectSource(recheck);
                            JsonObject retained = JsonNode.Parse(await File.ReadAllTextAsync(manifest, cancellationToken))!.AsObject();
                            if (await prepared.SourceHashAsync(cancellationToken) == retained["source_sha256"]?.GetValue<string>() &&
                                retained["native_result"] is JsonObject full)
                            {
                                if (EffectPrefixBakeService.SurvivesCompleteCapture(pristine, source, request, full,
                                    properties, projection, owner, prefix, installed))
                                {
                                    cachedProbe["status"] = CompleteCapturePassedStatus;
                                    cachedProbe["late_dependencies"] = new JsonArray();
                                }
                                else ExplainRetreat(cachedProbe, pristine, source, request, full, analyzedRuntime,
                                    properties, projection, owner, prefix, frames, loop, installed);
                                AnalysisCache.Write(request.AnalysisCacheDirectory, persistentKey, cachedProbe);
                                return cachedProbe;
                            }
                        }
                        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
                        {
                            // The retained trace is unusable; take a fresh capture below.
                            _ = error;
                        }
                        finally
                        {
                            if (Directory.Exists(recheck)) Directory.Delete(recheck, recursive: true);
                        }
                    }
                    // Missing or mismatched evidence cannot be upgraded by a changed diagnostic label.
                }
                else return cachedProbe;
            }
            string probeOutput = Path.Combine(output, $"effect-prefix-complete-probe-{owner}-{terminal.Key}-{prefix}-{frames}");
            if (Directory.Exists(probeOutput)) probeOutput += "-fresh-" + Guid.NewGuid().ToString("N");
            string captureProject = Path.Combine(probeOutput, "capture-source");
            var verdict = new JsonObject { ["kind"] = "complete_capture", ["owner_layer_id"] = owner,
                ["terminal_effect_id"] = cache["terminal_effect_id"]?.DeepClone(),
                ["prefix_effect_count"] = prefix, ["frames"] = frames, ["probe_output"] = probeOutput };
            if (terminal.Ordinal is int ordinal) verdict["terminal_effect_ordinal"] = ordinal;
            try
            {
                // 烘焙读的是源里的原场景（不带音频效果取舍），复核也按它，结论才与烘焙一致。
                JsonObject pristine = source.ReadJson(source.SceneResource);
                JsonArray installedPatches = await EffectPrefixBakeService.PrepareCaptureSourceAsync(captureProject,
                    source, request.Assets, pristine, properties, loop, cancellationToken);
                var raw = await new NativeRenderRunner(tools).RenderAsync(new(captureProject, request.Assets, Path.Combine(probeOutput, "render"), 64, 64,
                    request.FpsNumerator, request.FpsDenominator, checked(frames + 1), Seed: 17,
                    CaptureTarget: new RenderCaptureSelection(owner, terminal.Id, EffectOrdinal: terminal.Ordinal,
                        EffectTerminal: true, ExactExtent: false,
                        ForceVisibleOwner: forceVisibleOwner ? true : null),
                    UserProperties: properties, DeviceUuid: request.DeviceUuid, TraceScene: true,
                    FrameSamplesOnly: true, FrameSampleStride: checked((uint)frames), FrameSampleWidth: 1,
                    OfflineVideoRateOverrides: HybridBakeService.SelectVideoRateOverrides(loop, new HashSet<int> { owner })), null, cancellationToken);
                JsonObject fullRuntime = raw["native_result"]?.AsObject()
                    ?? throw new InvalidDataException("The complete prefix probe omitted its runtime evidence.");
                if (EffectPrefixBakeService.SurvivesCompleteCapture(pristine, source, request, fullRuntime,
                    properties, projection, owner, prefix, installedPatches))
                    verdict["status"] = CompleteCapturePassedStatus;
                else
                    ExplainRetreat(verdict, pristine, source, request, fullRuntime, analyzedRuntime,
                        properties, projection, owner, prefix, frames, loop, installedPatches);
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

    internal static void ExplainRetreat(JsonObject verdict, JsonObject scene, ProjectSource source,
        HybridAnalyzeRequest request, JsonObject fullRuntime, JsonObject analyzedRuntime, JsonObject properties,
        JsonObject projection, int owner, int prefix, ulong frames, JsonObject previousLoop,
        JsonArray? installedPatches = null)
    {
        JsonArray late = EffectPrefixBakeService.LateDependencyRejection(owner, frames, previousLoop,
            fullRuntime, analyzedRuntime)["late_dependencies"]!.AsArray();
        verdict["late_dependencies"] = late.DeepClone();
        if (late.Count > 0) { verdict["status"] = "rejected_late_dependency"; return; }
        JsonObject checkedRuntime = installedPatches is null ? fullRuntime :
            ShaderPeriodAnalysis.ReconcileInstalledKnobs(fullRuntime, source, request.Assets, installedPatches);
        JsonObject revised = EffectPrefixPlanner.AnalyzeIndexedPrefix(scene, source, request.Assets,
            checkedRuntime, properties, owner, prefix, request, projection, null);
        verdict["revised_loop_status"] = revised["status"]?.DeepClone();
        verdict["revised_loop_unresolved"] = revised["unresolved"]?.DeepClone() ?? new JsonArray();
        verdict["status"] = !EffectPrefixPlanner.Cacheable(revised) ? "rejected_revised_loop" :
            "rejected_changed_prefix_eligibility";
    }
}
