using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 运行时观测阶段的结果：最终采用的 trace（已写成 runtime.json）、其中补过 <c>_rt_link_</c> 合成边的依赖表、
/// 运行时图层表，以及音频效果取舍记录。依赖表与图层表就是 trace 里的同一批节点，改动会随 trace 一起落盘。
/// </summary>
internal sealed record RuntimeObservation(JsonObject Trace, JsonArray Dependencies, JsonArray RuntimeLayers, JsonObject AudioEffectChoice)
{
    /// <summary>
    /// 观测一次（或读开发者给的离线 trace），按请求省略固定音频效果时改场景再观测一次，补合成依赖边，写 runtime.json。
    /// 观测按 <see cref="ObservationKey"/> 缓存；渲染器读不了素材时转成工具局限结论抛出。
    /// </summary>
    internal static async Task<RuntimeObservation> ObserveAsync(HybridAnalyzeRequest request, ProjectSource source, string sourceHash,
        JsonObject scene, JsonObject project, JsonObject properties, SceneGraph graph, string output, NativeRuntimeObserver observer,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken, AnalysisMemo? memo = null)
    {
        async Task<JsonObject> ProbeAsync(string directory, bool audioChoice = false)
        {
            JsonObject? daytimeScene = request.DaytimeSplit && request.DaytimeState is string state
                ? DaytimeSplit.PrepareVideoObservation(scene, properties, state) : null;
            uint probeWidth = Math.Min(512, request.Width);
            uint probeHeight = Math.Max(2, (uint)Math.Round((double)request.Height * probeWidth / request.Width));
            string probeOutput = Path.Combine(output, directory);
            bool gpuTiming = request.Interaction is not null;
            // 键按实际观测的场景区分状态，不能让每个状态重用原作在当前时钟下的同一份视频元数据。
            var key = new ObservationKey(sourceHash, daytimeScene ?? scene, properties, request.Assets, probeWidth, probeHeight,
                request.FpsNumerator, request.FpsDenominator, gpuTiming, gpuTiming ? request.DeviceUuid : null, observer.Identity);
            string cacheKey = "runtime-" + key.Hash();
            // 同一次 analyze 里同一个键只观测一次（退回并行时也不会同时起几个渲染器），各次子分析拿各自的副本。
            return memo is null ? await ObserveOnceAsync() : await memo.JsonAsync(cacheKey, ObserveOnceAsync);
            async Task<JsonObject> ObserveOnceAsync()
            {
                bool derived = audioChoice || daytimeScene is not null;
                if (AnalysisCache.Read(request.AnalysisCacheDirectory, cacheKey) is JsonObject cached)
                {
                    key.VerifyObservation(cached, derived ? null : sourceHash);
                    return cached;
                }
                string snapshotRoot = Path.Combine(Path.GetFullPath(output), ".runtime-source-" + Guid.NewGuid().ToString("N"));
                // OfflineSession uses the parent directory's leaf name as scene_id.
                string snapshotDirectory = Path.Combine(snapshotRoot, new DirectoryInfo(source.DirectoryPath).Name);
                bool snapshotOwned = false;
                try
                {
                    if (derived && sourceHash != await source.SourceHashAsync(cancellationToken))
                        throw new IOException("Source changed before runtime observation preparation.");
                    if (Directory.Exists(snapshotRoot) || File.Exists(snapshotRoot)) throw new IOException("Snapshot root is occupied.");
                    ProjectSource.EnsureNoReparsePoints(snapshotRoot);
                    Directory.CreateDirectory(snapshotRoot);
                    snapshotOwned = true;
                    await observer.CopySnapshotAsync(source, snapshotDirectory, sourceHash, cancellationToken);
                    using var snapshot = new ProjectSource(Path.Combine(snapshotDirectory, Path.GetFileName(source.SourcePath)));
                    string renderSource = snapshot.SourcePath;
                    if (derived)
                    {
                        renderSource = Path.Combine(output, audioChoice ? "audio-choice-source" : directory + "-daytime-source");
                        await snapshot.ExtractAsync(renderSource, cancellationToken);
                        await File.WriteAllTextAsync(ProjectSource.ContainedPath(renderSource, source.SceneResource),
                            (daytimeScene ?? scene).ToJsonString(), cancellationToken);
                        var observedProject = project.DeepClone().AsObject();
                        observedProject["file"] = source.SceneResource;
                        await File.WriteAllTextAsync(Path.Combine(renderSource, "project.json"), observedProject.ToJsonString(), cancellationToken);
                    }
                    string expectedSourceHash = sourceHash;
                    if (derived)
                    {
                        if (sourceHash != await source.SourceHashAsync(cancellationToken))
                            throw new IOException("Source changed during runtime observation preparation.");
                        // Legal state/audio edits change the capture's source digest. Bind Raw to this prepared input,
                        // while the key continues to identify the original source plus the selected scene.
                        using var prepared = new ProjectSource(renderSource);
                        expectedSourceHash = await prepared.SourceHashAsync(cancellationToken);
                    }
                    JsonObject observed = await observer.ObserveAsync(new(key, renderSource, probeOutput, request.DeviceUuid,
                        expectedSourceHash), cancellationToken);
                    key.VerifyObservation(observed, expectedSourceHash);
                    observed["source"] = source.SourcePath;
                    AnalysisCache.Write(request.AnalysisCacheDirectory, cacheKey, observed);
                    return observed;
                }
                // 渲染器读不了某个素材文件时观测根本起不来：这是工具局限，不是壁纸不适用，交给调用方给出结构化结论。
                catch (IOException error) when (error is not AnalysisToolLimitationException &&
                    AnalysisToolLimitation.Classify(error.Message) is { Count: > 0 } findings)
                {
                    throw new AnalysisToolLimitationException(AnalysisToolLimitation.BuildReport(findings, scene, source.SourcePath,
                        sourceHash, probeOutput, error.Message), error);
                }
                finally
                {
                    if (snapshotOwned)
                        try { Directory.Delete(snapshotRoot, recursive: true); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        JsonObject trace;
        if (request.RuntimeTraceFile is not null)
        {
            trace = JsonNode.Parse(await File.ReadAllTextAsync(request.RuntimeTraceFile, cancellationToken))!.AsObject();
            if (!Path.GetFullPath(trace["source"]!.GetValue<string>()).Equals(Path.GetFullPath(source.SourcePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime trace belongs to another source.");
            // A precomputed trace is only an explicit developer input, never an automatic cache hit.
        }
        else trace = await ProbeAsync("runtime-probe");
        if (trace["status"]?.GetValue<string>() != "complete" || trace["runtime_dependencies"] is not JsonArray dependencies ||
            trace["runtime_layers"] is not JsonArray runtimeLayers)
            throw new InvalidDataException("A complete runtime dependency observation is required.");
        var audioEffectChoice = PlanTransforms.DescribeAudioEffectChoice(scene, source, request.Assets, properties, trace, request.AudioEffects,
            interactionOff: request.Interaction == "off");
        if (audioEffectChoice["status"]?.GetValue<string>() == "applied")
        {
            string beforePath = Path.Combine(output, "runtime-before-audio-choice.json");
            await VideoSceneBuilder.WriteJsonAsync(beforePath, trace, cancellationToken);
            audioEffectChoice["original_runtime_evidence"] = beforePath;
            PlanTransforms.ApplyAudioEffectChoice(scene, new JsonObject { ["audio_effects_choice"] = audioEffectChoice.DeepClone() });
            progress?.Report(new("analyzing", null, new Message("progress.observing_without_audio_effects")));
            // 整树拷贝只给渲染器用：观测命中缓存时不拷。
            trace = await ProbeAsync("audio-choice-runtime-probe", audioChoice: true);
            if (trace["status"]?.GetValue<string>() != "complete" || trace["runtime_dependencies"] is not JsonArray chosenDependencies ||
                trace["runtime_layers"] is not JsonArray chosenLayers)
                throw new InvalidDataException("Audio-effect omission requires a complete new runtime observation.");
            dependencies = chosenDependencies; runtimeLayers = chosenLayers;
            audioEffectChoice["runtime_evidence"] = Path.Combine(output, "runtime.json");
        }
        // Public traces belong to the logical input, including cache hits from another identical copy.
        trace["source"] = source.SourcePath;
        LinkComposites(graph, dependencies, runtimeLayers);
        await VideoSceneBuilder.WriteJsonAsync(Path.Combine(output, "runtime.json"), trace, cancellationToken);
        return new(trace, dependencies, runtimeLayers, audioEffectChoice);
    }

    /// <summary>
    /// 渲染器只以材质纹理名 <c>_rt_link_&lt;id&gt;</c> 暴露图层合成的链接：把观测到的、作者写的生产者补成
    /// 读 layerComposite 的依赖边，进同一张依赖图（捕获与导出都读它）。已有同样的非初始化读就不重复加。
    /// </summary>
    internal static void LinkComposites(SceneGraph graph, JsonArray dependencies, JsonArray runtimeLayers)
    {
        var objects = graph.Objects;
        var compositeProducers = runtimeLayers.OfType<JsonObject>().Select(layer => SceneGraph.Int(layer["owner"])).OfType<int>()
            .Where(objects.ContainsKey).ToHashSet();
        foreach (var layer in runtimeLayers.OfType<JsonObject>())
        {
            if (SceneGraph.Int(layer["owner"]) is not int owner || !objects.ContainsKey(owner)) continue;
            if (layer["materials"] is not JsonArray materials) continue;
            foreach (string texture in materials.OfType<JsonObject>()
                .SelectMany(material => material["textures"]?.AsArray().OfType<JsonValue>() ?? [])
                .Select(textureValue => textureValue.TryGetValue<string>(out string? value) ? value : null).OfType<string>())
            {
                var match = Regex.Match(texture, @"^_rt_link_(\d+)$", RegexOptions.CultureInvariant);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int target) ||
                    !compositeProducers.Contains(target) || owner == target || dependencies.OfType<JsonObject>().Any(dependency =>
                        SceneGraph.Int(dependency["owner"]) == owner && SceneGraph.Int(dependency["target"]) == target &&
                        dependency["operation"]?.GetValue<string>() == "read" && dependency["property"]?.GetValue<string>() == "layerComposite" &&
                        dependency["initialization"]?.GetValue<bool>() != true)) continue;
                dependencies.Add(new JsonObject { ["owner"] = owner, ["target"] = target, ["operation"] = "read",
                    ["property"] = "layerComposite", ["initialization"] = false });
            }
        }
    }

    /// <summary>
    /// trace 里的源脚本错误证据。带了就必须完整（计数与条目数一致、条目都是对象）；
    /// 实时观测没带说明渲染器过旧，直接拒绝；只有开发者给的离线 trace 允许没有（之后记成缺证据 blocker）。
    /// </summary>
    internal (bool Available, int? Count, JsonArray Errors) ScriptFaults(bool offlineTrace)
    {
        bool available = Trace["source_script_error_count"] is not null || Trace["source_script_errors"] is not null;
        if (!available)
            return offlineTrace ? (false, null, []) : throw new InvalidDataException("The current renderer omitted source script fault metadata.");
        if (Trace["source_script_error_count"] is not JsonValue countValue || !countValue.TryGetValue<int>(out int count) || count < 0 ||
            Trace["source_script_errors"] is not JsonArray errors || errors.Count != count || errors.Any(error => error is not JsonObject))
            throw new InvalidDataException("Runtime source script fault metadata is incomplete or malformed; collect a fresh trace.");
        return (true, count, errors);
    }
}
